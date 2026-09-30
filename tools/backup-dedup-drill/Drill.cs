using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// THE DEDUP, DRILLED ON REAL FILES (PR #105, round 6). The real BackupDedup (editor/BackupDedup.cs) and BackupRules
// (editor/EditorRules.cs) compiled with Unity's Roslyn and run on Unity's Mono, over a scratch tree: "unchanged" must
// mean the bytes - a file rewritten with new bytes under the SAME size and the SAME last-write time (to the tick) is
// copied, not linked; identical bytes are linked; two snapshots of the same bytes sign the same; a previous snapshot
// without a content index links nothing. Stubs.cs supplies the one thing the code takes from the window (Human).
static class Drill
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static bool SameInode(string a, string b)
    {
        // a hard link is the same file: same volume serial + file index (GetFileInformationByHandle), cheaper to see
        // through the link count - a linked pair has 2 names, a copied file 1
        return LinkCount(a) >= 2 && LinkCount(b) >= 2;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION { public uint attrs; public System.Runtime.InteropServices.ComTypes.FILETIME c, a, w; public uint volSerial, sizeHigh, sizeLow, links, indexHigh, indexLow; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(IntPtr h, out BY_HANDLE_FILE_INFORMATION info);
    static uint LinkCount(string p)
    {
        using (var fs = File.OpenRead(p))
        {
            GetFileInformationByHandle(fs.SafeFileHandle.DangerousGetHandle(), out var info);
            return info.links;
        }
    }

    static BackupDedup.Stats Snapshot(string live, string dir, string prev)
    {
        var st = new BackupDedup.Stats { NewRoot = dir, Prev = BackupDedup.ReadHashes(prev), PrevSnapshotExisted = prev != null };
        BackupDedup.PrehashLive(new[] { live }, st);
        Directory.CreateDirectory(dir);
        string dst = Path.Combine(dir, "source", "tree");
        BackupDedup.CopyTreeLinked(live, dst, prev == null ? null : Path.Combine(prev, "source", "tree"), st);
        BackupDedup.WriteHashes(dir, st);
        BackupDedup.WriteSignature(dir);
        return st;
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "haf_dedup_drill_" + Guid.NewGuid().ToString("N"));
        try
        {
            string live = Path.Combine(root, "live"); Directory.CreateDirectory(Path.Combine(live, "sub"));
            var rnd = new Random(5);
            var a = new byte[3 * 1024 * 1024]; rnd.NextBytes(a);   // a "model": big
            var b = new byte[70 * 1024]; rnd.NextBytes(b);         // a "registry": small
            string A = Path.Combine(live, "sub", "Tank.glb"), B = Path.Combine(live, "pack.json"), C = Path.Combine(live, "empty.txt");
            File.WriteAllBytes(A, a); File.WriteAllBytes(B, b); File.WriteAllBytes(C, new byte[0]);

            // 1. first snapshot: nothing to link against - all copied, an index written
            var s1 = Snapshot(live, Path.Combine(root, "s1"), null);
            Check(s1.Copied == 3 && s1.Linked == 0, "first snapshot copies everything (" + s1.Report + ")");
            var idx1 = BackupDedup.ReadHashes(Path.Combine(root, "s1"));
            Check(idx1 != null && idx1.Count == 3 && idx1["source/tree/pack.json"] == BackupRules.ContentKey(B), "the index records every file's content key");

            // 2. nothing changed: everything links (2 names per file), nothing copied
            var s2 = Snapshot(live, Path.Combine(root, "s2"), Path.Combine(root, "s1"));
            Check(s2.Linked == 3 && s2.Copied == 0, "unchanged bytes are linked (" + s2.Report + ")");
            Check(SameInode(Path.Combine(root, "s1", "source", "tree", "sub", "Tank.glb"), Path.Combine(root, "s2", "source", "tree", "sub", "Tank.glb")), "the linked model is one file under two names");
            Check(BackupDedup.Signature(Path.Combine(root, "s1")) == BackupDedup.Signature(Path.Combine(root, "s2")) && BackupDedup.Signature(Path.Combine(root, "s1")).Length == 40, "two snapshots of the same bytes sign the same");

            // 3. THE CASE: new bytes under a preserved size AND a preserved last-write time, to the tick
            var t = File.GetLastWriteTimeUtc(B);
            var b2 = (byte[])b.Clone(); b2[100] ^= 0x55; File.WriteAllBytes(B, b2); File.SetLastWriteTimeUtc(B, t);
            Check(new FileInfo(B).Length == b.Length && File.GetLastWriteTimeUtc(B) == t, "the edit kept the size and the time (the shape no size+time rule can see)");
            var s3 = Snapshot(live, Path.Combine(root, "s3"), Path.Combine(root, "s2"));
            Check(s3.Copied == 1 && s3.Linked == 2, "the edited file is copied, the others linked (" + s3.Report + ")");
            Check(File.ReadAllBytes(Path.Combine(root, "s3", "source", "tree", "pack.json")).SequenceEqual(b2), "the new snapshot holds the NEW bytes");
            Check(File.ReadAllBytes(Path.Combine(root, "s2", "source", "tree", "pack.json")).SequenceEqual(b), "the old snapshot still holds the OLD bytes (nothing wrote through a link)");
            Check(BackupDedup.ReadHashes(Path.Combine(root, "s3"))["source/tree/pack.json"] == BackupRules.ContentKey(B), "the index records the new key");
            Check(BackupDedup.Signature(Path.Combine(root, "s3")) != BackupDedup.Signature(Path.Combine(root, "s2")), "changed bytes sign differently");

            // 4. the record is of the bytes IN the snapshot: a live file that changes right after its copy is caught next time
            var s4 = Snapshot(live, Path.Combine(root, "s4"), Path.Combine(root, "s3"));
            Check(s4.Linked == 3, "steady state links everything again (" + s4.Report + ")");
            var idx4 = BackupDedup.ReadHashes(Path.Combine(root, "s4"));
            Check(idx4["source/tree/pack.json"] == BackupRules.ContentKey(Path.Combine(root, "s4", "source", "tree", "pack.json")), "every recorded key is the key of the bytes at that path in that snapshot");

            // 5. a previous snapshot WITHOUT an index (made before the change) links nothing, and says so
            File.Delete(Path.Combine(root, "s4", BackupDedup.HashName));
            var s5 = Snapshot(live, Path.Combine(root, "s5"), Path.Combine(root, "s4"));
            Check(s5.Copied == 3 && s5.Linked == 0 && s5.Report.Contains("no content index"), "no index in the previous snapshot: nothing linked, said (" + s5.Report + ")");
            var s6 = Snapshot(live, Path.Combine(root, "s6"), Path.Combine(root, "s5"));
            Check(s6.Linked == 3, "and the one after links by content again");

            // 6. a file that vanished from the live tree is simply absent; a new file is copied; nothing is ever linked by name alone
            File.Delete(C); File.WriteAllBytes(Path.Combine(live, "new.txt"), new byte[] { 1, 2, 3 });
            var s7 = Snapshot(live, Path.Combine(root, "s7"), Path.Combine(root, "s6"));
            Check(s7.Linked == 2 && s7.Copied == 1 && !File.Exists(Path.Combine(root, "s7", "source", "tree", "empty.txt")), "removed file absent, new file copied (" + s7.Report + ")");
        }
        catch (Exception e) { Console.WriteLine("FAIL drill threw: " + e); fails++; }
        finally { try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}
