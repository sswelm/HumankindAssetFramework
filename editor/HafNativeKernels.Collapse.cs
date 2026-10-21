// HafNativeKernels.Collapse.cs - the Decimate modifier's collapse (BlenderDecimate.Collapse over a BMesh) in native/hafdecimate.cpp.
// The BMesh's arrays are pinned and edited in place: the collapse creates no element, so nothing grows and the free lists
// stay empty; the alive flags (bool[]) cross as bytes both ways (Unity's Mono marshals a bool[] four bytes wide, PR #199);
// the custom-data layers cross as pinned pointers (IntPtr[]: a jagged array is not blittable); the vertex groups go in as
// CSR and come back through a handle, because their lists change length as collapses merge them. Everything the C++
// indexes is checked first (a corrupt link throws the managed twin's IndexOutOfRangeException rather than reading past a
// buffer). The checkpoint the managed loop polls (the Bake Tests' cancel, PrepInProcess's deadline) cannot fire inside
// the native block; the caller invokes it before and after.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static partial class HafNativeKernels
{
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr haf_collapse(
        int[] VE, byte[] VAlive, float[] VCo, float[] VNo, byte[] VFlag,
        int[] EV1, int[] EV2, int[] EL, int[] EDiskNext, int[] EDiskPrev, byte[] EAlive, byte[] EFlag,
        int[] LV, int[] LE, int[] LF, int[] LNext, int[] LPrev, int[] LRadialNext, int[] LRadialPrev, byte[] LAlive,
        int[] FLFirst, int[] FLen, int[] FMat, float[] FNo, byte[] FAlive, byte[] FFlag,
        int[] counts, float factor,
        int nUv, IntPtr[] uv, int nCornerColor, IntPtr[] cornerColor, int nPointColor, IntPtr[] pointColor,
        int hasDef, int[] defOffsets, int[] defNr, float[] defWeight, byte[] defPresent,
        CheckpointFn checkpoint, int every);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CheckpointFn();
    /// <summary>The native loop polls the checkpoint every this many collapses: every one, as the managed twin does (measured on
    /// the Saleg's: 2.6 million reverse P/Invokes cost nothing visible, 11.70 s against 11.74 s at every 16th), so a cancel or a
    /// deadline stops at the first reduction as BlenderPrepTests demands. At the start and every 1,024 edges of the cost build
    /// it polls as the twin does.</summary>
    internal const int CollapsePollEvery = 1;
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)] static extern int haf_collapse_def_size(IntPtr handle);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)] static extern void haf_collapse_def_copy(IntPtr handle, int[] offsets, int[] nr, float[] weight, byte[] present);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)] static extern void haf_collapse_free(IntPtr handle);

    static byte[] Bytes(bool[] flags, int n) { var b = new byte[n]; for (int i = 0; i < n; i++) b[i] = flags[i] ? (byte)1 : (byte)0; return b; }
    static void Flags(byte[] bytes, bool[] flags, int n) { for (int i = 0; i < n; i++) flags[i] = bytes[i] != 0; }
    static void Links(int[] links, int n, int limit, string what)
    {
        Need(links.Length >= n, what + ": " + links.Length + " entries for " + n);
        for (int i = 0; i < n; i++) if (links[i] != BMesh.None && (uint)links[i] >= (uint)limit) throw new IndexOutOfRangeException("native kernels: " + what + "[" + i + "] = " + links[i] + " of " + limit);
    }

    /// <summary>BlenderDecimate.Collapse: the mesh reduced to totface * factor faces, the layers of d blended along; the BMesh's Tot*
    /// counts and alive flags follow. The vertex and face normals must be in place, as for the managed twin. The checkpoint is
    /// polled from inside the native loop at every collapse (bm.TotFace is current when it runs); what it throws stops the
    /// collapse where it is and is rethrown here, the mesh left as the twin would leave it at that collapse.</summary>
    internal static void Collapse(BMesh bm, float factor, BlenderDecimate.MeshData d, Action checkpoint = null)
    {
        int nv = bm.VertCount, ne = bm.EdgeCount, nl = bm.LoopCount, nf = bm.FaceCount;
        Need(bm.VAlive.Length >= nv && bm.VCo.Length >= 3 * nv && bm.VNo.Length >= 3 * nv && bm.VFlag.Length >= nv, "vertex arrays for " + nv);
        Need(bm.EAlive.Length >= ne && bm.EFlag.Length >= ne && bm.EDiskNext.Length >= 2 * ne && bm.EDiskPrev.Length >= 2 * ne, "edge arrays for " + ne);
        Need(bm.LAlive.Length >= nl, "loop arrays for " + nl);
        Need(bm.FAlive.Length >= nf && bm.FFlag.Length >= nf && bm.FLen.Length >= nf && bm.FMat.Length >= nf && bm.FNo.Length >= 3 * nf, "face arrays for " + nf);
        Links(bm.VE, nv, ne, "VE");
        Links(bm.EV1, ne, nv, "EV1"); Links(bm.EV2, ne, nv, "EV2"); Links(bm.EL, ne, nl, "EL"); Links(bm.EDiskNext, 2 * ne, ne, "EDiskNext"); Links(bm.EDiskPrev, 2 * ne, ne, "EDiskPrev");
        Links(bm.LV, nl, nv, "LV"); Links(bm.LE, nl, ne, "LE"); Links(bm.LF, nl, nf, "LF"); Links(bm.LNext, nl, nl, "LNext"); Links(bm.LPrev, nl, nl, "LPrev"); Links(bm.LRadialNext, nl, nl, "LRadialNext"); Links(bm.LRadialPrev, nl, nl, "LRadialPrev");
        Links(bm.FLFirst, nf, nl, "FLFirst");
        foreach (var uv in d.Uv) Need(uv.Length >= 2 * nl, "uv layer: " + uv.Length + " floats for " + nl + " corners");
        foreach (var c in d.CornerColor) Need(c.Length >= 4 * nl, "corner colour layer: " + c.Length + " bytes for " + nl + " corners");
        foreach (var c in d.PointColor) Need(c.Length >= 4 * nv, "point colour layer: " + c.Length + " bytes for " + nv + " vertices");
        bool hasDef = d.DefNr != null;
        if (hasDef) Need(d.DefNr.Length >= nv && d.DefWeight != null && d.DefWeight.Length >= nv, "vertex groups for " + nv);

        var vAlive = Bytes(bm.VAlive, nv); var eAlive = Bytes(bm.EAlive, ne); var lAlive = Bytes(bm.LAlive, nl); var fAlive = Bytes(bm.FAlive, nf);
        var counts = new[] { bm.VertCount, bm.TotVert, bm.EdgeCount, bm.TotEdge, bm.LoopCount, bm.TotLoop, bm.FaceCount, bm.TotFace };
        // the vertex groups as CSR; a vertex without a list (null) is absent, an empty list present
        int[] defOffsets = null, defNr = null; float[] defWeight = null; byte[] defPresent = null;
        if (hasDef)
        {
            defOffsets = new int[nv + 1]; defPresent = new byte[nv]; int total = 0;
            for (int v = 0; v < nv; v++) { defOffsets[v] = total; if (d.DefNr[v] != null) { defPresent[v] = 1; total += d.DefNr[v].Count; } }
            defOffsets[nv] = total;
            defNr = new int[Math.Max(1, total)]; defWeight = new float[Math.Max(1, total)];
            for (int v = 0, at = 0; v < nv; v++) if (d.DefNr[v] != null) for (int j = 0; j < d.DefNr[v].Count; j++) { defNr[at] = d.DefNr[v][j]; defWeight[at] = d.DefWeight[v][j]; at++; }
        }
        var pins = new List<GCHandle>();
        IntPtr[] Pin<T>(List<T[]> layers) { var ptrs = new IntPtr[Math.Max(1, layers.Count)]; for (int i = 0; i < layers.Count; i++) { var h = GCHandle.Alloc(layers[i], GCHandleType.Pinned); pins.Add(h); ptrs[i] = h.AddrOfPinnedObject(); } return ptrs; }
        IntPtr handle = IntPtr.Zero;
        // the poll: the counts the C++ published become the BMesh's (a checkpoint reads bm.TotFace), then the caller's checkpoint;
        // an exception cannot cross the native frames, so it is kept and the loop told to stop
        Exception stopped = null;
        CheckpointFn poll = checkpoint == null ? null : (CheckpointFn)(() =>
        {
            bm.TotVert = counts[1]; bm.TotEdge = counts[3]; bm.TotLoop = counts[5]; bm.TotFace = counts[7];
            try { checkpoint(); return 0; } catch (Exception e) { stopped = e; return 1; }
        });
        try
        {
            var uv = Pin(d.Uv); var cc = Pin(d.CornerColor); var pc = Pin(d.PointColor);
            handle = haf_collapse(bm.VE, vAlive, bm.VCo, bm.VNo, bm.VFlag,
                bm.EV1, bm.EV2, bm.EL, bm.EDiskNext, bm.EDiskPrev, eAlive, bm.EFlag,
                bm.LV, bm.LE, bm.LF, bm.LNext, bm.LPrev, bm.LRadialNext, bm.LRadialPrev, lAlive,
                bm.FLFirst, bm.FLen, bm.FMat, bm.FNo, fAlive, bm.FFlag,
                counts, factor,
                d.Uv.Count, uv, d.CornerColor.Count, cc, d.PointColor.Count, pc,
                hasDef ? 1 : 0, defOffsets, defNr, defWeight, defPresent,
                poll, CollapsePollEvery);
            GC.KeepAlive(poll);
            if (hasDef)
            {
                int total = haf_collapse_def_size(handle);
                var offsets = new int[nv + 1]; var nr = new int[Math.Max(1, total)]; var weight = new float[Math.Max(1, total)]; var present = new byte[nv];
                haf_collapse_def_copy(handle, offsets, nr, weight, present);
                for (int v = 0; v < nv; v++)
                {
                    if (present[v] == 0) { d.DefNr[v] = null; d.DefWeight[v] = null; continue; }
                    var nrs = new List<int>(offsets[v + 1] - offsets[v]); var ws = new List<float>(offsets[v + 1] - offsets[v]);
                    for (int at = offsets[v]; at < offsets[v + 1]; at++) { nrs.Add(nr[at]); ws.Add(weight[at]); }
                    d.DefNr[v] = nrs; d.DefWeight[v] = ws;
                }
            }
        }
        finally
        {
            if (handle != IntPtr.Zero) haf_collapse_free(handle);
            foreach (var h in pins) h.Free();
        }
        Flags(vAlive, bm.VAlive, nv); Flags(eAlive, bm.EAlive, ne); Flags(lAlive, bm.LAlive, nl); Flags(fAlive, bm.FAlive, nf);
        bm.TotVert = counts[1]; bm.TotEdge = counts[3]; bm.TotLoop = counts[5]; bm.TotFace = counts[7];
        if (stopped != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopped).Throw();
    }
}
