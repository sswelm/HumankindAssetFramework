// HafNativeKernels.Weld.cs - the Workshop fuse's weld classes (GlbDisconnectedParts.cs, WeldClasses) in native/hafweld.cpp.
// The managed twin walks 27 cells per vertex through GlbDisconnectedParts.Weld.cs's CellGrid; measured 2026-10-27 on the
// Saleg's Revenge the two passes took 97 s summed over the 22 groups in Unity's Mono, the walk itself. The C++ walks the
// same cells in the same order and unions the same pairs in the same order, so every class has the representative the
// managed walk would give it and the fused bytes do not move. The positions cross once per group as one flat array.
using System;
using System.Runtime.InteropServices;

public static partial class HafNativeKernels
{
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_weld_classes(int n, double[] pos, double cellSize, double thresh2, int[] cls);

    internal static long WeldTicks, WeldCalls;   // the harness and the tests read these

    /// <summary>WeldClasses(distance): pos holds 3 doubles per vertex (n vertices); cellSize &gt; 0; thresh2 the squared weld
    /// distance (0 = coincident within the cell). Returns the class per vertex, a vertex index.</summary>
    internal static int[] WeldClasses(double[] pos, int n, double cellSize, double thresh2)
    {
        Need(n >= 0 && n <= pos.Length / 3, "weld positions: " + pos.Length + " doubles for " + n + " vertices");
        Need(cellSize > 0 && !double.IsInfinity(cellSize) && !double.IsNaN(cellSize), "weld cell " + cellSize);
        Need(thresh2 >= 0, "weld threshold " + thresh2);
        var cls = new int[n];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (n > 0) haf_weld_classes(n, pos, cellSize, thresh2, cls);
        System.Threading.Interlocked.Add(ref WeldTicks, clock.ElapsedTicks); System.Threading.Interlocked.Increment(ref WeldCalls);
        return cls;
    }
}
