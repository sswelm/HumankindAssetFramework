using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

// Compile with the same sources/references as ProbeDrill.cs. One warm-up and five measured reads + probes per file.
// Run as an x64 executable, or as a DLL in an isolated Unity project with -executeMethod ProbePerformance.RunUnity -- <files>.
// Hashing includes every preview buffer and happens AFTER timing. Compare hashes across builds as well as repeated runs.
// HAF_PROBE_PERF_RUNS=<n> measured runs after the warm-up (default 5; the native kernels drill uses 1). The KERNELS line
// says whether the native kernels (HafNativeKernels) or the managed twins ran.
public static class ProbePerformance
{
    public static void RunUnity() => Run(Environment.GetCommandLineArgs().SkipWhile(a => a != "--").Skip(1).ToArray());
    public static int Main(string[] args) { Run(args); return 0; }

    public static void Run(string[] files)
    {
        if (files.Length == 0) throw new ArgumentException("Pass one or more GLB/glTF files.");
        Console.WriteLine("RUNTIME " + Environment.Version + " x64=" + (IntPtr.Size == 8));
        Console.WriteLine("KERNELS " + HafNativeKernels.Status);
        int runs = int.TryParse(Environment.GetEnvironmentVariable("HAF_PROBE_PERF_RUNS"), out int r) && r >= 1 ? r : 5;
        foreach (var file in files)
        {
            string expected = null;
            var totals = new double[runs]; var probes = new double[runs];
            for (int run = -1; run < runs; run++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                var clock = Stopwatch.StartNew();
                var model = GlbReader.Read(file);
                double read = clock.Elapsed.TotalMilliseconds;
                var result = VehicleProbe.Run(model);
                clock.Stop();
                double total = clock.Elapsed.TotalMilliseconds, probe = total - read;
                int collections0 = GC.CollectionCount(0) - gc0, collections1 = GC.CollectionCount(1) - gc1, collections2 = GC.CollectionCount(2) - gc2;
                string hash = Hash(result);
                if (expected != null && hash != expected) throw new InvalidDataException("Probe output changed between runs: " + file);
                expected = hash;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "MEASURE {0} run={1} read_ms={2:F3} probe_ms={3:F3} total_ms={4:F3} gc={5}/{6}/{7} sha256={8}",
                    Path.GetFileName(file), run, read, probe, total, collections0, collections1, collections2, hash));
                if (run >= 0) { totals[run] = total; probes[run] = probe; }
            }
            Array.Sort(totals); Array.Sort(probes);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "MEDIAN {0} probe_ms={1:F3} total_ms={2:F3} sha256={3}", Path.GetFileName(file), probes[runs / 2], totals[runs / 2], expected));
        }
        Console.WriteLine("BENCHMARK PASS files=" + files.Length);
    }

    static string Hash(VehicleProbe.Result result)
    {
        using (var sha = SHA256.Create())
        using (var sink = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
        using (var buffer = new BufferedStream(sink, 65536))
        using (var writer = new BinaryWriter(buffer))
        {
            writer.Write(result.Armature ?? ""); writer.Write(result.Split);
            foreach (var note in result.Notes) writer.Write(note);
            foreach (var bone in result.RigBones) writer.Write(bone.Row);
            foreach (var part in result.Parts)
            {
                writer.Write(part.Row); writer.Write(part.UnderBone); writer.Write(part.Source); writer.Write(part.Placed);
                Floats(writer, part.BlenderMatrix); Floats(writer, part.FirstVertex);
                foreach (double v in part.Center) writer.Write(v);
                foreach (double v in part.Size) writer.Write(v);
                var mesh = part.Mesh; writer.Write(mesh != null);
                if (mesh == null) continue;
                writer.Write(mesh.Count); writer.Write(mesh.Source);
                Floats(writer, mesh.World); Floats(writer, mesh.Normal); Floats(writer, mesh.PreviewNormal);
                Floats(writer, mesh.Local); Floats(writer, mesh.Uv);
                Ints(writer, mesh.Tris); Ints(writer, mesh.TriMaterial);
            }
            writer.Flush(); sink.FlushFinalBlock();
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
    }

    static void Floats(BinaryWriter writer, float[] values)
    {
        writer.Write(values == null ? -1 : values.Length);
        if (values == null) return;
        if (!BitConverter.IsLittleEndian) { foreach (var value in values) writer.Write(value); }
        else Bytes(writer, values);
    }
    static void Ints(BinaryWriter writer, int[] values)
    {
        writer.Write(values == null ? -1 : values.Length);
        if (values == null) return;
        if (!BitConverter.IsLittleEndian) { foreach (var value in values) writer.Write(value); }
        else Bytes(writer, values);
    }
    static void Bytes(BinaryWriter writer, Array values)
    {
        // Bulk copy preserves float bits and avoids Mono's BinaryWriter allocating a tiny byte array per number.
        var buffer = new byte[65536]; int bytes = Buffer.ByteLength(values);
        for (int at = 0; at < bytes; at += buffer.Length)
        {
            int count = Math.Min(buffer.Length, bytes - at);
            Buffer.BlockCopy(values, at, buffer, 0, count); writer.Write(buffer, 0, count);
        }
    }
}
