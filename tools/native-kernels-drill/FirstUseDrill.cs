// Run by native_kernels_drill.sh in 64 fresh processes. A version probe initializes the common kernels, then 32 workers
// enter the SHA-256 and dissolve exports together before any serial call can hide a first-use initialization race.
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class FirstUseDrill
{
    [DllImport("hafkernels", CallingConvention = CallingConvention.Cdecl)] static extern int haf_kernels_version();
    [DllImport("hafkernels", CallingConvention = CallingConvention.Cdecl)] static extern int haf_sha256(byte[] data, long n, byte[] hash);
    [DllImport("hafkernels", CallingConvention = CallingConvention.Cdecl)] static extern void haf_bmesh_status(IntPtr handle, int[] status);
    [DllImport("hafkernels", CallingConvention = CallingConvention.Cdecl)] static extern void haf_bmesh_free(IntPtr handle);
    [DllImport("hafkernels", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr haf_dissolve_limit(
        int[] VE, byte[] VAlive, float[] VCo, float[] VNo, byte[] VFlag,
        int[] EV1, int[] EV2, int[] EL, int[] EDiskNext, int[] EDiskPrev, byte[] EAlive, byte[] EFlag,
        int[] LV, int[] LE, int[] LF, int[] LNext, int[] LPrev, int[] LRadialNext, int[] LRadialPrev, byte[] LAlive,
        int[] FLFirst, int[] FLen, int[] FMat, float[] FNo, byte[] FAlive, byte[] FFlag, int[] counts,
        int[] freeV, int nFreeV, int[] freeE, int nFreeE, int[] freeL, int nFreeL, int[] freeF, int nFreeF,
        int nUv, IntPtr[] uv, int[] uvLengths, int nCornerColor, IntPtr[] cornerColor, int[] cornerColorLengths, short[] customNormal, int customNormalLength,
        float angleLimit, int nJobs, int[] vsOffsets, int[] vs, int[] esOffsets, int[] es, int skipEmpty);

    static int Main()
    {
        if (haf_kernels_version() < 8) throw new Exception("BMesh/PNG kernel DLL did not load");
        const int workers = 32;
        using (var ready = new CountdownEvent(workers))
        using (var start = new ManualResetEventSlim(false))
        {
            var threads = new Thread[workers]; int failed = 0;
            var shaStatus = new int[workers]; var hashes = new byte[workers][];
            for (int i = 0; i < workers; i++)
            {
                int worker = i;
                threads[i] = new Thread(() =>
                {
                    hashes[worker] = new byte[32];
                    var z = new int[8]; var flags = new byte[1]; var floats = new float[1]; var pointers = new IntPtr[1];
                    ready.Signal(); start.Wait(); IntPtr handle = IntPtr.Zero;
                    try
                    {
                        shaStatus[worker] = haf_sha256(new byte[] { 97, 98, 99 }, 3, hashes[worker]);
                        handle = haf_dissolve_limit(z, flags, floats, floats, flags, z, z, z, z, z, flags, flags,
                            z, z, z, z, z, z, z, flags, z, z, z, floats, flags, flags, z,
                            z, 0, z, 0, z, 0, z, 0, 0, pointers, z, 0, pointers, z, null, 0, 0.1f, 0, z, z, z, z, 1);
                        var status = new int[4]; haf_bmesh_status(handle, status);
                        if (status[0] != 0) { Interlocked.Increment(ref failed); Console.WriteLine("FAIL first-use dissolve status " + status[0]); }
                    }
                    catch (Exception ex) { Interlocked.Increment(ref failed); Console.WriteLine("FAIL " + ex); }
                    finally { if (handle != IntPtr.Zero) haf_bmesh_free(handle); }
                });
                threads[i].Start();
            }
            ready.Wait(); start.Set(); foreach (var thread in threads) thread.Join();
            var serialHash = new byte[32];
            if (haf_sha256(new byte[] { 97, 98, 99 }, 3, serialHash) == 0)
            {
                const string expected = "BA-78-16-BF-8F-01-CF-EA-41-41-40-DE-5D-AE-22-23-B0-03-61-A3-96-17-7A-9C-B4-10-FF-61-F2-00-15-AD";
                for (int i = 0; i < workers; i++)
                    if (shaStatus[i] != 0 || BitConverter.ToString(hashes[i]) != expected)
                    { failed++; Console.WriteLine("FAIL first-use SHA-256 worker " + i + " status " + shaStatus[i]); }
            }
            else Console.WriteLine("SHA-256 system provider unavailable; managed fallback required");
            Console.WriteLine("FIRST USE workers " + workers + " failed " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
