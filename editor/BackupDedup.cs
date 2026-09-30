using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// SMARTER BACKUPS (2026-08-23) — measured, not assumed.
//
// Two full snapshots taken 3.5 hours apart were compared file by file: **18 of 4,076 files differed**. Every backup
// was re-copying ~1.4 GB of which ~99.6% was byte-identical to the one before, and `source` alone — the licensed
// models, which change rarely — is 971 MB of that. Seven snapshots on disk, seven near-identical copies; and the
// offsite zip re-uploaded the same ~1 GB daily into a 15 GB quota.
//
// Two independent savings, deliberately kept separate because they fail differently:
//
//   1) LOCAL: hard-link unchanged files instead of copying them. A hard link is a second NAME for the same bytes on
//      the same volume, so an unchanged file costs ZERO additional space while each snapshot stays a complete,
//      independently browsable, independently restorable folder. Nothing about restore changes — a hard link IS the
//      file. This is what Time Machine and `rsync --link-dest` do. "Unchanged" is decided BY CONTENT (PR #105, round
//      6): every snapshot writes an index of <length>|<sha1> per file (haf_hashes.txt), and the next links a file only
//      when the live file's key equals the record — no size, no last-write time, however precise. Measured on Unity's
//      Mono: keying the 3 GB live tree takes 2.1 s on 8 cores (12.8 s on one); reading the previous snapshot on the
//      backup drive would take 51 s, which is why the index exists.
//
//      THE ONE RULE THAT MAKES IT SAFE: never write INTO a snapshot. Editing a hard-linked file edits every snapshot
//      sharing it. Restore only ever copies OUT of a snapshot into the live tree, and the delete-guard copies IN
//      from the live tree to a fresh folder, so neither writes through a link. Deleting a snapshot is always safe —
//      it removes one name; the bytes survive while any other name remains.
//
//   2) OFFSITE: skip the zip entirely when the snapshot's content signature matches the last one uploaded. Most days
//      genuinely produce nothing new, so most days should upload nothing.
internal static class BackupDedup
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>Hard-link `link` to the bytes of `existing`. False = not possible (other volume, FS without links,
    /// permissions) — every caller falls back to a plain copy, so a failure costs space, never correctness.</summary>
    internal static bool TryHardLink(string existing, string link)
    {
        try
        {
            if (!File.Exists(existing)) return false;
            var dir = Path.GetDirectoryName(link);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(link)) File.Delete(link);
            return CreateHardLink(link, existing, IntPtr.Zero);
        }
        catch { return false; }
    }

    /// <summary>The per-snapshot content index: one <c>H&lt;tab&gt;rel&lt;tab&gt;&lt;length&gt;|&lt;sha1&gt;</c> line per
    /// file, written by the snapshot that holds the bytes. The next snapshot links a file only when the LIVE file's key
    /// equals the record (review of PR #105, round 6: size + last-write time — even to the tick — is not the bytes).</summary>
    internal const string HashName = "haf_hashes.txt";

    /// <summary>The previous snapshot's index, or null when it has none (made before 2026-09-30, or not a full snapshot): nothing links then.</summary>
    internal static Dictionary<string, string> ReadHashes(string snapshotDir)
    {
        try
        {
            if (snapshotDir == null) return null;
            string p = Path.Combine(snapshotDir, HashName);
            if (!File.Exists(p)) return null;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(p))
                if (BackupRules.TryParseHashLine(line, out string rel, out string key)) d[rel] = key;
            return d;
        }
        catch { return null; }
    }

    /// <summary>Written AFTER every file landed, sorted, so the file is the same for the same content.</summary>
    internal static void WriteHashes(string snapshotDir, Stats st)
    {
        var lines = new List<string> { "# HAF content index: <length>|<sha1> of every file in this snapshot, as written" };
        foreach (var kv in st.New.OrderBy(k => k.Key, StringComparer.Ordinal)) lines.Add(BackupRules.HashLine(kv.Key, kv.Value));
        File.WriteAllLines(Path.Combine(snapshotDir, HashName), lines);
    }

    /// <summary>Key every live file under <paramref name="roots"/> in parallel, ahead of the copy loop (measured on Unity's
    /// Mono: one core 12.8 s for the 3 GB tree, eight cores 2.1 s). The loop then decides link-or-copy without reading
    /// the previous snapshot at all — its index has the keys.</summary>
    internal static void PrehashLive(IEnumerable<string> roots, Stats st)
    {
        var files = new List<string>();
        foreach (var r in roots)
        {
            if (File.Exists(r)) files.Add(r);
            else if (Directory.Exists(r)) files.AddRange(Directory.GetFiles(r, "*", SearchOption.AllDirectories));
        }
        int par = Math.Min(8, Math.Max(1, Environment.ProcessorCount / 2));
        System.Threading.Tasks.Parallel.ForEach(files, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = par },
            f => { var k = BackupRules.ContentKey(f); if (k != null) st.LiveKeys[Path.GetFullPath(f)] = k; });
    }

    /// <summary>Running tally for one snapshot, so the report can state what was actually saved rather than claim it.</summary>
    internal sealed class Stats
    {
        public int Linked, Copied;
        public long LinkedBytes, CopiedBytes;
        public int Files => Linked + Copied;
        public string NewRoot;                                    // the snapshot being written; index paths are relative to it
        public Dictionary<string, string> Prev;                   // the previous snapshot's index (rel -> key); null = none, nothing links
        public bool PrevSnapshotExisted;                          // a previous snapshot was there (with or without an index)
        public readonly Dictionary<string, string> New = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LiveKeys = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Rel(string dst) => dst.Substring(NewRoot.Length).TrimStart('/', '\\').Replace('\\', '/');
        public string KeyOf(string live) => LiveKeys.TryGetValue(Path.GetFullPath(live), out var k) ? k : BackupRules.ContentKey(live);
        public string Report =>
            Linked == 0
                ? $"{Copied} file(s) copied ({BackupWindow.Human(CopiedBytes)}) — " + (!PrevSnapshotExisted ? "no previous snapshot to link against"
                    : Prev == null ? "the previous snapshot has no content index (made before 2026-09-30), so nothing was linked this once; from the next backup on, unchanged files are linked by content"
                    : "nothing unchanged by content")
                : $"{Files} file(s): {Linked} unchanged by content (hard-linked, {BackupWindow.Human(LinkedBytes)} saved), {Copied} copied ({BackupWindow.Human(CopiedBytes)})";
    }

    /// <summary>Copy `src` into `dst`, hard-linking any file that is byte-for-byte unchanged from the matching file
    /// under `linkBase`. `linkBase` null/missing = a plain copy of everything (the first snapshot, or a new group).</summary>
    internal static int CopyTreeLinked(string src, string dst, string linkBase, Stats st)
    {
        int n = 0;
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
        {
            string name = Path.GetFileName(f);
            string target = Path.Combine(dst, name);
            string prev = linkBase == null ? null : Path.Combine(linkBase, name);
            n += CopyOrLink(f, target, prev, st);
        }
        foreach (var d in Directory.GetDirectories(src))
        {
            string name = Path.GetFileName(d);
            n += CopyTreeLinked(d, Path.Combine(dst, name), linkBase == null ? null : Path.Combine(linkBase, name), st);
        }
        return n;
    }

    internal static int CopyOrLink(string src, string dst, string prev, Stats st)
    {
        long len = 0;
        try { len = new FileInfo(src).Length; } catch { }
        string rel = st.Rel(dst);
        // LINK ONLY BY CONTENT: the live file's key must equal what the previous snapshot RECORDED for the bytes at this
        // path (its index) - never a size or a time. The previous snapshot is not read; its index is.
        if (prev != null && st.Prev != null && st.Prev.TryGetValue(rel, out var recorded) && File.Exists(prev))
        {
            string live = st.KeyOf(src);
            if (live != null && live == recorded && TryHardLink(prev, dst))
            { st.New[rel] = recorded; st.Linked++; st.LinkedBytes += len; return 1; }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst));
        File.Copy(src, dst, true);
        // the record is of the bytes IN the snapshot, read back after the copy (the live file may move on meanwhile)
        st.New[rel] = BackupRules.ContentKey(dst) ?? "";
        st.Copied++; st.CopiedBytes += len;
        return 1;
    }

    // ---- content signature: what makes "nothing changed since the last offsite zip" answerable ----

    /// <summary>A stable fingerprint of a snapshot's CONTENT: every file's relative path and content key, from the
    /// snapshot's own index (already computed by the copy step - no second read), sorted so nothing about enumeration
    /// order or dates can change the answer. Two snapshots of the same bytes sign the same, whenever they were taken.
    /// (Until round 6 of PR #105 the signature took each file's mtime, and the manifest's - which differs per
    /// snapshot - with it: no two snapshots ever signed the same, and the offsite skip never fired.) A snapshot without
    /// an index (made before 2026-09-30) signs by path, size and mtime as before, which is always "changed".</summary>
    internal static string Signature(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return "";
            var sb = new StringBuilder();
            var index = ReadHashes(dir);
            if (index != null)
                foreach (var kv in index.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('|').Append(kv.Value).Append('\n');
            else
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                                           .Where(p => !p.EndsWith(SigName, StringComparison.OrdinalIgnoreCase))
                                           .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(f);
                    sb.Append(f.Substring(dir.Length).Replace('\\', '/')).Append('|')
                      .Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                }
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").ToLowerInvariant();
        }
        catch { return ""; }
    }

    internal const string SigName = "haf_signature.txt";

    internal static void WriteSignature(string snapshotDir)
    {
        try { File.WriteAllText(Path.Combine(snapshotDir, SigName), Signature(snapshotDir)); } catch { }
    }

    internal static string ReadSignature(string snapshotDir)
    {
        try { var p = Path.Combine(snapshotDir, SigName); return File.Exists(p) ? File.ReadAllText(p).Trim() : ""; }
        catch { return ""; }
    }

    /// <summary>The signature of the newest zip already offsite, via its sidecar. "" = none/unknown, which always
    /// means "go ahead and zip" — an unreadable sidecar must never be mistaken for "unchanged, skip".</summary>
    internal static string LastOffsiteSignature(string offsiteDir)
    {
        try
        {
            if (!Directory.Exists(offsiteDir)) return "";
            var newest = Directory.GetFiles(offsiteDir, "*.zip.sig").OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            return newest == null ? "" : File.ReadAllText(newest).Trim();
        }
        catch { return ""; }
    }
}
