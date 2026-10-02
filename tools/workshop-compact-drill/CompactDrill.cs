// CompactDrill.cs — the Workshop's compaction on real files (tools/workshop_compact_drill.sh).
//   mono drill.exe <outDir> <keepList|-> <file.glb>...
// <keepList>: a text file naming the sources whose compacted copy is to stay in <outDir> (for Blender); "-" = keep all.
// Per file: compact it (GlbDisconnectedParts.Compact — the same step every Workshop output goes through), then
//   1. the GLB READER, which shares no code with the Workshop, reads the file as it was and the compacted one: the
//      first, less the meshes no node uses, must equal the second field by field (HafModelDiff);
//   2. compacting the result again must find nothing more to leave out;
//   3. the compacted file is written to <outDir> for Blender to import beside the original.
// A file given as  remove:<path>  is first put through a real Workshop OPERATION: its first mesh node is removed
// (GlbDisconnectedParts.RemoveMeshes - the Del key), which orphans that mesh and compacts on the way out. The reader's
// model of the source, with that node's mesh taken off by hand, must then equal the operation's output. That runs the
// compaction on other exporters' layouts (the Khronos samples), which no Workshop file has.
// One line per file:
//   COMPACT <TAB> <source> <TAB> <status> <TAB> <output or -> <TAB> meshes=a->b <TAB> accessors=a->b <TAB> bytes=a->b <TAB> ms=n <TAB> mode=asis|remove
// status: compacted | nothing | refused: <why> | FAIL: <why>.   Exit 1 when any file FAILs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

static class CompactDrill
{
    static HafModel WithoutUnusedMeshes(HafModel m)
    {
        var used = new SortedSet<int>(m.Nodes.Where(n => n.Mesh >= 0).Select(n => n.Mesh));
        var map = new Dictionary<int, int>(); var kept = new List<HafMesh>();
        foreach (int i in used) { map[i] = kept.Count; kept.Add(m.Meshes[i]); }
        m.Meshes.Clear(); m.Meshes.AddRange(kept);
        foreach (var n in m.Nodes) if (n.Mesh >= 0) n.Mesh = map[n.Mesh];
        return m;
    }

    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    static int Main(string[] args)
    {
        var inv = CultureInfo.InvariantCulture;
        string outDir = args[0]; Directory.CreateDirectory(outDir);
        HashSet<string> keep = args[1] == "-" ? null : new HashSet<string>(File.ReadAllLines(args[1]).Select(l => l.Trim().Replace('\\', '/')).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase);
        int failed = 0, index = 0; long before = 0, after = 0; var total = Stopwatch.StartNew();
        foreach (string argument in args.Skip(2))
        {
            string file = argument;
            index++;
            Collect();
            string status, output = "-", counts = "meshes=-\taccessors=-\tbytes=-"; long ms = 0;
            string mode = argument.StartsWith("remove:", StringComparison.Ordinal) ? "remove" : "asis";
            try
            {
                bool remove = file.StartsWith("remove:", StringComparison.Ordinal);
                if (remove) file = file.Substring("remove:".Length);
                byte[] source = File.ReadAllBytes(file);
                int removedNode = -1;
                if (remove)
                {
                    HafModel probe = GlbReader.Read(source);
                    removedNode = probe.Nodes.FindIndex(n => n.Mesh >= 0);
                    if (removedNode < 0) throw new InvalidDataException("no node carries a mesh");
                }
                var clock = Stopwatch.StartNew();
                GlbDisconnectedParts.Result r = remove ? GlbDisconnectedParts.RemoveMeshes(source, new HashSet<int> { removedNode }) : GlbDisconnectedParts.Compact(source);
                ms = clock.ElapsedMilliseconds;
                var c = r.Compaction;
                counts = string.Format(inv, "meshes={0}->{1}\taccessors={2}->{3}\tbytes={4}->{5}", c.MeshesBefore, c.MeshesAfter, c.AccessorsBefore, c.AccessorsAfter, source.Length, r.Bytes != null ? r.Bytes.Length : source.Length);
                if (r.Bytes == null || !c.Changed) status = c.Skipped != null ? "refused: " + c.Skipped : "nothing";
                else
                {
                    // LEAN ON PURPOSE: the drill runs on Unity's standalone Mono, which is a 32-bit process - a 277 MB source,
                    // its compacted copy and two decoded models do not fit an address space of 2 GB at once. So every
                    // buffer is dropped the moment it is no longer needed, and the compacted file is read back from disk.
                    long sourceLength = source.Length, outputLength = r.Bytes.Length;
                    before += sourceLength; after += outputLength;
                    output = Path.Combine(outDir, index.ToString("000", inv) + "_" + Path.GetFileName(file)).Replace('\\', '/');
                    File.WriteAllBytes(output, r.Bytes);
                    var again = GlbDisconnectedParts.Compact(r.Bytes);
                    if (again.Bytes != null || again.Compaction.Skipped != null) throw new InvalidDataException("a second compaction was not a no-op: " + again.Details[0]);
                    again = null; r = null; Collect();
                    HafModel was = GlbReader.Read(source);
                    if (removedNode >= 0) { was.Nodes[removedNode].Mesh = -1; was.Nodes[removedNode].Skin = -1; }   // what RemoveMeshes takes off the node
                    WithoutUnusedMeshes(was);
                    source = null; Collect();
                    HafModel now = GlbReader.Read(File.ReadAllBytes(output));
                    if (keep != null && !keep.Contains(file.Replace('\\', '/'))) { File.Delete(output); output = "-"; }
                    Collect();
                    string difference = HafModelDiff.FirstDifference(was, now);
                    if (difference != null) throw new InvalidDataException("the reader sees a difference: " + difference);
                    status = remove ? "compacted after removing node " + removedNode.ToString(inv) : "compacted";
                }
            }
            catch (Exception ex) { status = "FAIL: " + ex.GetType().Name + ": " + ex.Message.Replace('\t', ' ').Replace('\n', ' '); failed++; }
            Console.WriteLine("COMPACT\t" + file.Replace('\\', '/') + "\t" + status + "\t" + output + "\t" + counts + "\tms=" + ms.ToString(inv) + "\tmode=" + mode);
        }
        Console.WriteLine(string.Format(inv, "TOTAL\tfiles={0}\tfailed={1}\tbytes={2}->{3}\tms={4}", index, failed, before, after, total.ElapsedMilliseconds));
        return failed > 0 ? 1 : 0;
    }
}
