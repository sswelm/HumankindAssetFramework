using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

// THE GLB WRITER, DRILLED ON THE REAL REGISTRY (2026-10-01, step 2 of replacing Blender): every model the registry
// names, read by GlbReader and written by GlbWriter (the real file overload: temporary name, moved into place) into
// <outDir>/<basename>; then the written FILE is read back and compared with the original FIELD BY FIELD - every
// node, every vertex of every attribute array, every index, every material field, every texture, sampler, image
// byte, skin matrix, animation key (review of PR #110: the shell drill's value summaries - box, area, centroid -
// cannot see a dropped sampler setting or a lost extras object). The first differing field is named: DIFF\t<name>\t<field>.
// tools/glb_writer_drill.sh then reads the written files with the reader drill and has Blender import them.
// One line per file: WROTE\t<basename>\t<bytes>\t<ms>. Two sources with one basename are refused, not overwritten.
static class WriteDrill
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string outDir = args[0]; Directory.CreateDirectory(outDir);
        int fails = 0; long totalBytes = 0; double totalMs = 0;
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            string name = Path.GetFileNameWithoutExtension(args[i]) + ".glb";   // a .gltf source is written as a .glb (the writer's only format)
            if (seen.TryGetValue(name, out var other)) { Console.WriteLine($"FAIL\t{name}\tbasename collision: {args[i]} and {other} would be written to the same file"); fails++; continue; }
            seen[name] = args[i];
            try
            {
                var m = GlbReader.Read(args[i]);
                string target = Path.Combine(outDir, name);
                var sw = Stopwatch.StartNew();
                GlbWriter.Write(m, target);
                sw.Stop();
                long bytes = new FileInfo(target).Length;
                totalBytes += bytes; totalMs += sw.Elapsed.TotalMilliseconds;
                var back = GlbReader.Read(target);
                string diff = HafModelDiff.FirstDifference(m, back);
                m = null;   // two whole models is the peak; the second write below needs only one
                if (diff != null) { Console.WriteLine($"DIFF\t{name}\t{diff}"); fails++; }
                else
                {
                    // deterministic on real files, not only the fixture: the read-back model written again is byte-identical to the file - compared as it streams, no second copy
                    bool same; using (var cmp = new CompareStream(target)) { GlbWriter.Write(back, cmp); same = cmp.Same; }
                    if (!same) { Console.WriteLine($"DIFF\t{name}\ta second write of the read-back model is not byte-identical to the first"); fails++; }
                }
                Console.WriteLine($"WROTE\t{name}\t{bytes}\t{sw.Elapsed.TotalMilliseconds:0}");
            }
            catch (Exception e) { Console.WriteLine($"FAIL\t{name}\t{e.Message}"); fails++; }
            // two whole models and a file's bytes per iteration, every array in Mono's non-moving large-object space:
            // collect between files, or thirty files fragment it into "Insufficient memory" (seen 2026-10-01)
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        if (Directory.GetFiles(outDir, "*.tmp").Length > 0) { Console.WriteLine("FAIL\t-\ta temporary file was left behind by the writer"); fails++; }
        Console.WriteLine($"TOTAL\tfiles={args.Length - 1}\tMB={totalBytes / 1e6:0.0}\tms={totalMs:0}");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>A write-only stream that compares what is written with a file on disk, byte for byte, as it comes.</summary>
    sealed class CompareStream : Stream
    {
        readonly FileStream file; readonly byte[] buf = new byte[1 << 16]; bool mismatch; long written;
        public CompareStream(string path) { file = File.OpenRead(path); }
        public bool Same => !mismatch && written == file.Length;
        public override void Write(byte[] b, int off, int n)
        {
            written += n;
            while (n > 0 && !mismatch)
            {
                int want = Math.Min(n, buf.Length), got = 0;
                while (got < want) { int r = file.Read(buf, got, want - got); if (r <= 0) { mismatch = true; return; } got += r; }
                for (int i = 0; i < want; i++) if (buf[i] != b[off + i]) { mismatch = true; return; }
                off += want; n -= want;
            }
        }
        public override void WriteByte(byte v) { Write(new[] { v }, 0, 1); }
        protected override void Dispose(bool disposing) { file.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => written; public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

}
