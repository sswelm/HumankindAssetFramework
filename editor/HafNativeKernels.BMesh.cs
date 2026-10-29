// HafNativeKernels.BMesh.cs - the Vehicle Lab reduce stage's kernels in native/hafbmesh.cpp (2026-10-27): the BMesh build of a
// stage mesh (BlenderVehicleRig.Stage2.FromMeshData), the weld grouping's per-vertex material, bmesh_find_doubles
// (BlenderWeld.FindDoubles) and bmesh.ops.dissolve_limit over the chunk jobs (BlenderVehicleRig.Stage2.DissolveLimited over
// BlenderDissolve.DissolveLimit). Measured in Unity 2021.3's Mono on the Espana (2026-10-27): the limited dissolve of its
// 170k-vertex shroud 2.8 s, the BMesh builds 0.5 s, the double search 0.35 s of a 6.7 s reduce stage.
// The dissolve edits a COPY of the mesh in the DLL (a face join creates an n-gon and its corners: the pools may grow) and the
// managed BMesh adopts the result - the arrays, the counts and the free lists, so a later creation takes the same slot as the
// managed twin would. The build returns exact-size arrays the same way. Everything the C++ indexes is checked first, as the
// collapse does; a malformed input throws the twin's exception.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static partial class HafNativeKernels
{
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr haf_bmesh_build(float[] positions, int nv, int[] edges, int ne, byte[] edgeSharp, int[] faces, int nf, byte[] faceSharp, int[] faceMaterial, int faceNormals);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_bmesh_vert_material(int[] VE, byte[] VAlive, int[] EV1, int[] EV2, int[] EL, int[] EDiskNext, int[] LV, int[] LE, int[] LF, int[] LNext, int[] LRadialNext, int[] FMat, int vertCount, int[] material, byte[] uniform);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_find_doubles(float[] VCo, int[] verts, int n, float dist, int[] duplicates);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr haf_dissolve_limit(
        int[] VE, byte[] VAlive, float[] VCo, float[] VNo, byte[] VFlag,
        int[] EV1, int[] EV2, int[] EL, int[] EDiskNext, int[] EDiskPrev, byte[] EAlive, byte[] EFlag,
        int[] LV, int[] LE, int[] LF, int[] LNext, int[] LPrev, int[] LRadialNext, int[] LRadialPrev, byte[] LAlive,
        int[] FLFirst, int[] FLen, int[] FMat, float[] FNo, byte[] FAlive, byte[] FFlag, int[] counts,
        int[] freeV, int nFreeV, int[] freeE, int nFreeE, int[] freeL, int nFreeL, int[] freeF, int nFreeF,
        int nUv, IntPtr[] uv, int[] uvLengths, int nCornerColor, IntPtr[] cornerColor, int[] cornerColorLengths, short[] customNormal, int customNormalLength,
        float angleLimit, int nJobs, int[] vsOffsets, int[] vs, int[] esOffsets, int[] es, int skipEmpty);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)] static extern void haf_bmesh_sizes(IntPtr handle, int[] counts, int[] freeSizes, int[] layerLengths);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)] static extern void haf_bmesh_status(IntPtr handle, int[] status);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_bmesh_copy(IntPtr handle,
        int[] VE, byte[] VAlive, float[] VCo, float[] VNo, byte[] VFlag,
        int[] EV1, int[] EV2, int[] EL, int[] EDiskNext, int[] EDiskPrev, byte[] EAlive, byte[] EFlag,
        int[] LV, int[] LE, int[] LF, int[] LNext, int[] LPrev, int[] LRadialNext, int[] LRadialPrev, byte[] LAlive,
        int[] FLFirst, int[] FLen, int[] FMat, float[] FNo, byte[] FAlive, byte[] FFlag,
        int[] freeV, int[] freeE, int[] freeL, int[] freeF,
        IntPtr[] uv, IntPtr[] cornerColor, short[] customNormal);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)] static extern void haf_bmesh_free(IntPtr handle);

    static byte[] FlagsOrNull(bool[] flags, int n) => flags == null ? null : Bytes(flags, n);

    /// <summary>The result handle's BMesh into a managed one (the build: a new BMesh; the dissolve: the caller's, which adopts the arrays, the
    /// counts and the free lists). The loop layers come back through the same copy when the handle carries them (the dissolve):
    /// each list's arrays are replaced in place, the custom-normal shorts through the ref.</summary>
    static void TakeResult(IntPtr handle, BMesh into, List<float[]> uv, List<byte[]> cornerColor, ref short[] customNormal)
    {
        var counts = new int[8]; var freeSizes = new int[4];
        int nUv = uv?.Count ?? 0, nCc = cornerColor?.Count ?? 0;
        var layerLengths = new int[nUv + nCc + 1];
        haf_bmesh_sizes(handle, counts, freeSizes, layerLengths);
        int nv = counts[0], ne = counts[2], nl = counts[4], nf = counts[6];
        var ve = new int[nv]; var vAlive = new byte[nv]; var vco = new float[3 * nv]; var vno = new float[3 * nv]; var vflag = new byte[nv];
        var ev1 = new int[ne]; var ev2 = new int[ne]; var el = new int[ne]; var ediskNext = new int[2 * ne]; var ediskPrev = new int[2 * ne]; var eAlive = new byte[ne]; var eflag = new byte[ne];
        var lv = new int[nl]; var le = new int[nl]; var lf = new int[nl]; var lnext = new int[nl]; var lprev = new int[nl]; var lrn = new int[nl]; var lrp = new int[nl]; var lAlive = new byte[nl];
        var flfirst = new int[nf]; var flen = new int[nf]; var fmat = new int[nf]; var fno = new float[3 * nf]; var fAlive = new byte[nf]; var fflag = new byte[nf];
        var freeV = new int[freeSizes[0]]; var freeE = new int[freeSizes[1]]; var freeL = new int[freeSizes[2]]; var freeF = new int[freeSizes[3]];
        var uvOut = new List<float[]>(); var ccOut = new List<byte[]>(); short[] cnOut = null;
        for (int i = 0; i < nUv; i++) uvOut.Add(new float[layerLengths[i]]);
        for (int i = 0; i < nCc; i++) ccOut.Add(new byte[layerLengths[nUv + i]]);
        if (customNormal != null) cnOut = new short[layerLengths[nUv + nCc]];
        var pins = new List<GCHandle>();
        IntPtr[] Pin<T>(List<T[]> arrays) { var ptrs = new IntPtr[Math.Max(1, arrays.Count)]; for (int i = 0; i < arrays.Count; i++) { var h = GCHandle.Alloc(arrays[i], GCHandleType.Pinned); pins.Add(h); ptrs[i] = h.AddrOfPinnedObject(); } return ptrs; }
        try
        {
            var uvp = Pin(uvOut); var ccp = Pin(ccOut);
            haf_bmesh_copy(handle, ve, vAlive, vco, vno, vflag, ev1, ev2, el, ediskNext, ediskPrev, eAlive, eflag,
                lv, le, lf, lnext, lprev, lrn, lrp, lAlive, flfirst, flen, fmat, fno, fAlive, fflag, freeV, freeE, freeL, freeF, uvp, ccp, cnOut);
        }
        finally { foreach (var h in pins) h.Free(); }
        var vb = new bool[nv]; Flags(vAlive, vb, nv); var eb = new bool[ne]; Flags(eAlive, eb, ne); var lb = new bool[nl]; Flags(lAlive, lb, nl); var fb = new bool[nf]; Flags(fAlive, fb, nf);
        into.Adopt(ve, vb, vco, vno, vflag, ev1, ev2, el, ediskNext, ediskPrev, eb, eflag, lv, le, lf, lnext, lprev, lrn, lrp, lb, flfirst, flen, fmat, fno, fb, fflag, counts, freeV, freeE, freeL, freeF);
        for (int i = 0; i < nUv; i++) uv[i] = uvOut[i];
        for (int i = 0; i < nCc; i++) cornerColor[i] = ccOut[i];
        if (cnOut != null) customNormal = cnOut;
    }

    /// <summary>BM_mesh_bm_from_me on a stage mesh (BlenderVehicleRig.Stage2.FromMeshData's vertex, edge and face part): the vertices in
    /// order, the edges in the mesh's order (SMOOTH unless sharp), the triangles with their corners' edges (SMOOTH unless
    /// sharp_face, the material); the face normals (normal_tri_v3) when asked. The twin's InvalidOperationException when a face
    /// names a corner pair no edge joins.</summary>
    internal static BMesh BuildFromMesh(float[] positions, int nv, int[] edges, bool[] edgeSharp, int[] faces, bool[] faceSharp, int[] faceMaterial, bool faceNormals)
    {
        int ne = edges.Length / 2, nf = faces.Length / 3;
        Need(positions.Length >= 3 * nv, "positions: " + positions.Length + " floats for " + nv + " vertices");
        Need(edges.Length % 2 == 0, "edges: " + edges.Length + " entries is not a multiple of 2");
        for (int i = 0; i < edges.Length; i++) if ((uint)edges[i] >= (uint)nv) throw new IndexOutOfRangeException("native kernels: edge end " + i + " names vertex " + edges[i] + " of " + nv);
        CheckFaces(faces, nv);
        Need(edgeSharp == null || edgeSharp.Length >= ne, "sharp edges: flags for " + ne + " edges");
        Need(faceSharp == null || faceSharp.Length >= nf, "sharp faces: flags for " + nf + " faces");
        Need(faceMaterial.Length >= nf, "face materials: " + faceMaterial.Length + " for " + nf + " faces");
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = haf_bmesh_build(positions, nv, edges, ne, FlagsOrNull(edgeSharp, ne), faces, nf, FlagsOrNull(faceSharp, nf), faceMaterial, faceNormals ? 1 : 0);
            var status = new int[4]; haf_bmesh_status(handle, status);
            if (status[0] == 1)
            {
                int f = status[1], k = status[2];
                throw new InvalidOperationException($"face {f}: no edge joins corners {faces[3 * f + k]} and {faces[3 * f + (k + 1) % 3]}");
            }
            var bm = new BMesh(); short[] noShorts = null;
            TakeResult(handle, bm, null, null, ref noShorts);
            return bm;
        }
        finally { if (handle != IntPtr.Zero) haf_bmesh_free(handle); }
    }

    /// <summary>The weld grouping's test per vertex: uniform[v] when every corner of the vertex shares one material index (material[v]);
    /// not for a dead vertex, one without corners, or one whose corners' faces differ (the twin skips all three).</summary>
    internal static void VertMaterial(BMesh bm, out int[] material, out bool[] uniform)
    {
        int nv = bm.VertCount, ne = bm.EdgeCount, nl = bm.LoopCount, nf = bm.FaceCount;
        Need(bm.VAlive.Length >= nv && bm.VE.Length >= nv, "vertex arrays for " + nv);
        Need(bm.EV1.Length >= ne && bm.EV2.Length >= ne && bm.EL.Length >= ne && bm.EDiskNext.Length >= 2 * ne, "edge arrays for " + ne);
        Need(bm.LV.Length >= nl && bm.LE.Length >= nl && bm.LF.Length >= nl && bm.LNext.Length >= nl && bm.LRadialNext.Length >= nl, "loop arrays for " + nl);
        Need(bm.FMat.Length >= nf, "face arrays for " + nf);
        Links(bm.VE, nv, ne, "VE", bm.VAlive, targets: bm.EAlive);
        Links(bm.EV1, ne, nv, "EV1", bm.EAlive, optional: false, targets: bm.VAlive);
        Links(bm.EV2, ne, nv, "EV2", bm.EAlive, optional: false, targets: bm.VAlive);
        Links(bm.EL, ne, nl, "EL", bm.EAlive, targets: bm.LAlive);
        Links(bm.EDiskNext, 2 * ne, ne, "EDiskNext", bm.EAlive, 2, false, bm.EAlive);
        Links(bm.LV, nl, nv, "LV", bm.LAlive, optional: false, targets: bm.VAlive);
        Links(bm.LE, nl, ne, "LE", bm.LAlive, optional: false, targets: bm.EAlive);
        Links(bm.LF, nl, nf, "LF", bm.LAlive, optional: false, targets: bm.FAlive);
        Links(bm.LNext, nl, nl, "LNext", bm.LAlive, optional: false, targets: bm.LAlive);
        Links(bm.LRadialNext, nl, nl, "LRadialNext", bm.LAlive, optional: false, targets: bm.LAlive);
        material = new int[nv]; var flags = new byte[nv];
        haf_bmesh_vert_material(bm.VE, Bytes(bm.VAlive, nv), bm.EV1, bm.EV2, bm.EL, bm.EDiskNext, bm.LV, bm.LE, bm.LF, bm.LNext, bm.LRadialNext, bm.FMat, nv, material, flags);
        uniform = new bool[nv]; Flags(flags, uniform, nv);
    }

    /// <summary>bmesh_find_doubles_by_distance_impl + kdtree_calc_duplicates_cb (BlenderWeld.FindDoubles's raw array): for each listed
    /// vertex the index (into verts) of the vertex it merges into, itself for a cluster's target, -1 when in no cluster.</summary>
    internal static int[] FindDoubles(float[] VCo, int vertCount, IReadOnlyList<int> verts, float dist)
    {
        int n = verts.Count;
        Need(VCo.Length >= 3 * vertCount, "positions: " + VCo.Length + " floats for " + vertCount + " vertices");
        var list = new int[Math.Max(1, n)];
        for (int i = 0; i < n; i++) { list[i] = verts[i]; if ((uint)verts[i] >= (uint)vertCount) throw new IndexOutOfRangeException("native kernels: weld vertex " + i + " names " + verts[i] + " of " + vertCount); }
        var duplicates = new int[Math.Max(1, n)];
        haf_find_doubles(VCo, list, n, dist, duplicates);
        if (n == 0) return new int[0];
        if (duplicates.Length != n) Array.Resize(ref duplicates, n);
        return duplicates;
    }

    /// <summary>BlenderVehicleRig.Stage2.DissolveLimited's job loop: per job (its vertices and edges, in their order) the Python
    /// wrapper's "has been removed" check, bmesh.ops.dissolve_limit, the NORMALS_CALC finish; with skipEmpty a job without
    /// elements is passed over. The loop layers (BlenderWeld.Layers: the UV layers, the corner colours, the custom-normal shorts or
    /// null) grow with the n-gons' corners and are replaced in place. Returns the twin's death string, or null when the mesh and
    /// the layers now hold the result.</summary>
    internal static string DissolveLimit(BMesh bm, List<float[]> uv, List<byte[]> cornerColor, ref short[] customNormal, float angleLimit, IReadOnlyList<(List<int> es, List<int> vs)> jobs, bool skipEmpty)
    {
        int nv = bm.VertCount, ne = bm.EdgeCount, nl = bm.LoopCount, nf = bm.FaceCount;
        Need(bm.VAlive.Length >= nv && bm.VCo.Length >= 3 * nv && bm.VNo.Length >= 3 * nv && bm.VFlag.Length >= nv && bm.VE.Length >= nv, "vertex arrays for " + nv);
        Need(bm.EAlive.Length >= ne && bm.EFlag.Length >= ne && bm.EDiskNext.Length >= 2 * ne && bm.EDiskPrev.Length >= 2 * ne && bm.EV1.Length >= ne && bm.EV2.Length >= ne && bm.EL.Length >= ne, "edge arrays for " + ne);
        Need(bm.LAlive.Length >= nl && bm.LV.Length >= nl && bm.LE.Length >= nl && bm.LF.Length >= nl && bm.LNext.Length >= nl && bm.LPrev.Length >= nl && bm.LRadialNext.Length >= nl && bm.LRadialPrev.Length >= nl, "loop arrays for " + nl);
        Need(bm.FAlive.Length >= nf && bm.FFlag.Length >= nf && bm.FLen.Length >= nf && bm.FMat.Length >= nf && bm.FNo.Length >= 3 * nf && bm.FLFirst.Length >= nf, "face arrays for " + nf);
        Links(bm.VE, nv, ne, "VE", bm.VAlive, targets: bm.EAlive);
        Links(bm.EV1, ne, nv, "EV1", bm.EAlive, optional: false, targets: bm.VAlive);
        Links(bm.EV2, ne, nv, "EV2", bm.EAlive, optional: false, targets: bm.VAlive);
        Links(bm.EL, ne, nl, "EL", bm.EAlive, targets: bm.LAlive);
        Links(bm.EDiskNext, 2 * ne, ne, "EDiskNext", bm.EAlive, 2, false, bm.EAlive);
        Links(bm.EDiskPrev, 2 * ne, ne, "EDiskPrev", bm.EAlive, 2, false, bm.EAlive);
        Links(bm.LV, nl, nv, "LV", bm.LAlive, optional: false, targets: bm.VAlive);
        Links(bm.LE, nl, ne, "LE", bm.LAlive, optional: false, targets: bm.EAlive);
        Links(bm.LF, nl, nf, "LF", bm.LAlive, optional: false, targets: bm.FAlive);
        Links(bm.LNext, nl, nl, "LNext", bm.LAlive, optional: false, targets: bm.LAlive);
        Links(bm.LPrev, nl, nl, "LPrev", bm.LAlive, optional: false, targets: bm.LAlive);
        Links(bm.LRadialNext, nl, nl, "LRadialNext", bm.LAlive, optional: false, targets: bm.LAlive);
        Links(bm.LRadialPrev, nl, nl, "LRadialPrev", bm.LAlive, optional: false, targets: bm.LAlive);
        Links(bm.FLFirst, nf, nl, "FLFirst", bm.FAlive, optional: false, targets: bm.LAlive);
        foreach (var u in uv) Need(u.Length >= 2 * nl, "uv layer: " + u.Length + " floats for " + nl + " corners");
        foreach (var c in cornerColor) Need(c.Length >= 4 * nl, "corner colour layer: " + c.Length + " bytes for " + nl + " corners");
        Need(customNormal == null || customNormal.Length >= 2 * nl, "custom normals: shorts for " + nl + " corners");
        int nJobs = jobs.Count;
        var vsOffsets = new int[nJobs + 1]; var esOffsets = new int[nJobs + 1];
        for (int j = 0; j < nJobs; j++) { vsOffsets[j + 1] = vsOffsets[j] + jobs[j].vs.Count; esOffsets[j + 1] = esOffsets[j] + jobs[j].es.Count; }
        var vs = new int[Math.Max(1, vsOffsets[nJobs])]; var es = new int[Math.Max(1, esOffsets[nJobs])];
        for (int j = 0; j < nJobs; j++)
        {
            var job = jobs[j];
            for (int i = 0; i < job.vs.Count; i++) { int v = job.vs[i]; if ((uint)v >= (uint)nv) throw new IndexOutOfRangeException("native kernels: dissolve vertex " + v + " of " + nv); vs[vsOffsets[j] + i] = v; }
            for (int i = 0; i < job.es.Count; i++) { int e = job.es[i]; if ((uint)e >= (uint)ne) throw new IndexOutOfRangeException("native kernels: dissolve edge " + e + " of " + ne); es[esOffsets[j] + i] = e; }
        }
        var counts = new[] { bm.VertCount, bm.TotVert, bm.EdgeCount, bm.TotEdge, bm.LoopCount, bm.TotLoop, bm.FaceCount, bm.TotFace };
        var freeV = bm.FreeList(0); var freeE = bm.FreeList(1); var freeL = bm.FreeList(2); var freeF = bm.FreeList(3);
        var uvLengths = new int[Math.Max(1, uv.Count)]; for (int i = 0; i < uv.Count; i++) uvLengths[i] = uv[i].Length;
        var ccLengths = new int[Math.Max(1, cornerColor.Count)]; for (int i = 0; i < cornerColor.Count; i++) ccLengths[i] = cornerColor[i].Length;
        var pins = new List<GCHandle>();
        IntPtr[] Pin<T>(List<T[]> arrays) { var ptrs = new IntPtr[Math.Max(1, arrays.Count)]; for (int i = 0; i < arrays.Count; i++) { var h = GCHandle.Alloc(arrays[i], GCHandleType.Pinned); pins.Add(h); ptrs[i] = h.AddrOfPinnedObject(); } return ptrs; }
        IntPtr handle = IntPtr.Zero;
        try
        {
            var uvp = Pin(uv); var ccp = Pin(cornerColor);
            handle = haf_dissolve_limit(bm.VE, Bytes(bm.VAlive, nv), bm.VCo, bm.VNo, bm.VFlag,
                bm.EV1, bm.EV2, bm.EL, bm.EDiskNext, bm.EDiskPrev, Bytes(bm.EAlive, ne), bm.EFlag,
                bm.LV, bm.LE, bm.LF, bm.LNext, bm.LPrev, bm.LRadialNext, bm.LRadialPrev, Bytes(bm.LAlive, nl),
                bm.FLFirst, bm.FLen, bm.FMat, bm.FNo, Bytes(bm.FAlive, nf), bm.FFlag, counts,
                freeV, freeV.Length, freeE, freeE.Length, freeL, freeL.Length, freeF, freeF.Length,
                uv.Count, uvp, uvLengths, cornerColor.Count, ccp, ccLengths, customNormal, customNormal?.Length ?? 0,
                angleLimit, nJobs, vsOffsets, vs, esOffsets, es, skipEmpty ? 1 : 0);
            foreach (var h in pins) h.Free(); pins.Clear();
            var status = new int[4]; haf_bmesh_status(handle, status);
            if (status[0] == 3) throw new InvalidOperationException("native kernels: ucrtbase asinf/cosf did not resolve for the dissolve");
            if (status[0] == 1) return "TypeError: verts: " + status[2] + " BMVert has been removed";
            if (status[0] == 2) return "TypeError: edges: " + status[2] + " BMEdge has been removed";
            TakeResult(handle, bm, uv, cornerColor, ref customNormal);
            return null;
        }
        finally
        {
            foreach (var h in pins) h.Free();
            if (handle != IntPtr.Zero) haf_bmesh_free(handle);
        }
    }
}
