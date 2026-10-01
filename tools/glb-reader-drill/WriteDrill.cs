using System;
using System.Diagnostics;
using System.IO;

// THE GLB WRITER, DRILLED ON THE REAL REGISTRY (2026-10-01, step 2 of replacing Blender): every model the registry
// names, read by GlbReader and written by GlbWriter into <outDir>/<basename>. tools/glb_writer_drill.sh then reads
// the written files back with the reader drill (every value equal to the original's) and has Blender import them
// (every value equal to what it saw in the original). One line per file: WROTE\t<basename>\t<bytes>\t<ms>
static class WriteDrill
{
    static int Main(string[] args)
    {
        string outDir = args[0]; Directory.CreateDirectory(outDir);
        int fails = 0; long totalBytes = 0; double totalMs = 0;
        for (int i = 1; i < args.Length; i++)
        {
            string name = Path.GetFileName(args[i]);
            try
            {
                var m = GlbReader.Read(args[i]);
                var sw = Stopwatch.StartNew();
                var bytes = GlbWriter.Write(m);
                sw.Stop();
                File.WriteAllBytes(Path.Combine(outDir, name), bytes);
                totalBytes += bytes.Length; totalMs += sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"WROTE\t{name}\t{bytes.Length}\t{sw.Elapsed.TotalMilliseconds:0}");
            }
            catch (Exception e) { Console.WriteLine($"FAIL\t{name}\t{e.Message}"); fails++; }
        }
        Console.WriteLine($"TOTAL\tfiles={args.Length - 1}\tMB={totalBytes / 1e6:0.0}\tms={totalMs:0}");
        return fails == 0 ? 0 : 1;
    }
}
