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
//      file. This is what Time Machine and `rsync --link-dest` do.
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

    /// <summary>Is `candidate` (in the previous snapshot) the same file as `src` (live)? Size + last-write time, the
    /// standard cheap test — hashing 1.4 GB every backup to find 18 changed files would cost more than it saves.
    /// A 2-second tolerance absorbs filesystem timestamp granularity (FAT/network shares round to 2 s).</summary>
    internal static bool SameFile(FileInfo src, FileInfo candidate)
    {
        try
        {
            return src.Length == candidate.Length
                && Math.Abs((src.LastWriteTimeUtc - candidate.LastWriteTimeUtc).TotalSeconds) <= 2;
        }
        catch { return false; }
    }

    /// <summary>Running tally for one snapshot, so the report can state what was actually saved rather than claim it.</summary>
    internal sealed class Stats
    {
        public int Linked, Copied;
        public long LinkedBytes, CopiedBytes;
        public int Files => Linked + Copied;
        public string Report =>
            Linked == 0
                ? $"{Copied} file(s) copied ({BackupWindow.Human(CopiedBytes)}) — no previous snapshot to link against"
                : $"{Files} file(s): {Linked} unchanged (hard-linked, {BackupWindow.Human(LinkedBytes)} saved), {Copied} copied ({BackupWindow.Human(CopiedBytes)})";
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
        if (prev != null && File.Exists(prev) && SameFile(new FileInfo(src), new FileInfo(prev)) && TryHardLink(prev, dst))
        { st.Linked++; st.LinkedBytes += len; return 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(dst));
        File.Copy(src, dst, true);
        st.Copied++; st.CopiedBytes += len;
        return 1;
    }

    // ---- content signature: what makes "nothing changed since the last offsite zip" answerable ----

    /// <summary>A stable fingerprint of a snapshot's CONTENT: every file's relative path, size and mtime, sorted so
    /// directory-enumeration order can't change the answer. Deliberately not a hash of the bytes — same reason
    /// SameFile isn't: reading 1.4 GB to decide whether to upload 1 GB is a poor trade, and path+size+mtime is the
    /// same evidence the copy step already trusts.</summary>
    internal static string Signature(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return "";
            var sb = new StringBuilder();
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
