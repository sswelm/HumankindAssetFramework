// HafNativeKernels.Twin.cs - the Workshop fuse's twin census (GlbDisconnectedParts.cs, the twin grid and TwinOfFace) in
// native/haftwin.cpp. The fuse hands its face corners (9 doubles a face), the passes' winding so far (a byte a face) and the
// three numbers it chose (the cell, the reach, the float pad); the C++ builds the same grid - the same cells, the same
// 40-cell cap, the candidates of a cell in face order - and answers every face's nearest facing-back twin with the same
// float prefilter and double tests, walking the 27 cells in the same order and keeping the first of equal distances. The
// three result arrays come back as the managed loop fills them, so everything after the census reads what it read before.
using System;
using System.Runtime.InteropServices;

public static partial class HafNativeKernels
{
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr haf_twin_build(double[] P, int nFaces, byte[] flipped, double cell, double reach, float pad);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)] static extern void haf_twin_free(IntPtr handle);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_twin_all(IntPtr handle, int workers, double[] best, double[] proj, int[] bestG);

    internal static long TwinTicks;   // the harness reads this

    /// <summary>Every face's twin: best (distance, +inf for none), proj (along the normal), bestG (the twin face, or -1).
    /// faces: 9 doubles per face; flipped: one flag per face. The corner array is pinned for the build and the census only.</summary>
    internal static void TwinCensus(double[] faces, bool[] flipped, double cell, double reach, float pad, int workers, double[] best, double[] proj, int[] bestG)
    {
        Need(faces.Length % 9 == 0, "twin corners: " + faces.Length + " doubles");
        int n = faces.Length / 9;
        Need(flipped.Length >= n && best.Length >= n && proj.Length >= n && bestG.Length >= n, "twin buffers for " + n + " faces");
        Need(cell > 0 && !double.IsInfinity(cell) && !double.IsNaN(cell), "twin cell " + cell);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var flags = new byte[Math.Max(1, n)]; for (int f = 0; f < n; f++) flags[f] = flipped[f] ? (byte)1 : (byte)0;
        var pin = GCHandle.Alloc(faces, GCHandleType.Pinned);   // the grid reads the corners at every query
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = haf_twin_build(faces, n, flags, cell, reach, pad);
            haf_twin_all(handle, Math.Max(1, workers), best, proj, bestG);
        }
        finally
        {
            if (handle != IntPtr.Zero) haf_twin_free(handle);
            pin.Free();
        }
        System.Threading.Interlocked.Add(ref TwinTicks, clock.ElapsedTicks);
    }
}
