// BlenderVehicleRig.Stage2.cs - vehicle_rig.py's stage 2, the SOURCE-SIDE reduction tiers (lines 1338-1514), in C# (step (c) of
// the vehicle_rig port, 2026-10-15; docs/Review-Backlog.md item (5)). Per tier - DEFAULT, RIGGING, SHROUD, STRUCTURE, BODY, OAR,
// SAIL, RUDDER, WHEEL, TURRET, GUN, WINDOW, FLIP, PRESERVE, DETAIL, skipped without names or at a dial of 0.5 % or less - and per named
// part (the first object of that name; a name the scene has not is a WARN): the mesh into a bmesh; the WELD tiers (all but
// RIGGING and SHROUD) weld coincident vertices within each material (the vertices whose corners all share one material,
// grouped in the order the materials are first met, remove_doubles per group at 1e-5 of the part's largest extent) and
// mark the manifold edges over 40 degrees sharp; the SOUP tiers (RIGGING, SHROUD) run the limited dissolve at 5 degrees
// instead (BlenderDissolve: one dissolve_limit over every vertex and edge, or past 4,000 faces one per BSP cell of the face
// centres on the cell's interior edges and vertices, as _dissolve_limited chunks it); triangulate (BlenderPolyfill: nothing to
// do on triangles, the quads and n-gons the dissolve left cut by BEAUTY); the bmesh back into the mesh, every face smooth on a
// weld tier; then the dial's target (max(8, int(v0 * (1 - pct / 100))) from the welded count, or the raw count on a soup tier) and, when the mesh is still above
// it, a DECIMATE modifier at ratio max(0.02, target / vmid) - BlenderDecimate's collapse on the mesh as the modifier
// converts it (face normals recomputed, the vertex normals the mesh reports) -, the evaluated mesh copied into a new
// datablock named after the old one (meshes.new_from_object) and the old one removed; the lines per part and per tier.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public static partial class BlenderVehicleRig
{
    static readonly (string label, string role)[] Tiers =
    {
        ("DEFAULT", "default"), ("RIGGING", "rigging"), ("SHROUD", "shroud"), ("STRUCTURE", "structure"), ("BODY", "bodies"), ("OAR", "oars"), ("SAIL", "sails"),
        ("RUDDER", "rudders"), ("WHEEL", "wheels"), ("TURRET", "turrets"), ("GUN", "guns"), ("WINDOW", "window"), ("FLIP", "flip"), ("PRESERVE", "preserve"), ("DETAIL", "detail"),
    };

    /// <summary>Stage 2 on the scene stage 1 left: the reduction tiers. Lines go to s.Logs2; a branch not modelled sets s.Fallback.</summary>
    public static void RunStage2(BlenderRigAnim.Stage1 s, Args a) => RunStage2(s, a, null);

    // checkpoint: the Lab's cancel and deadline, polled between the parts of a tier - the reduction is the long stage, and a stage
    // boundary alone let a long one run past both (review of #188)
    public static void RunStage2(BlenderRigAnim.Stage1 s, Args a, System.Action checkpoint)
    {
        if (s.Fallback != null || s.Exits || s.Dies != null) return;
        var names = s.Names;
        // _by_name over objs (the mesh objects after the ignore): the first of a name wins
        var byName = new Dictionary<string, BlenderRigAnim.SceneObject>(StringComparer.Ordinal);
        foreach (var o in s.Objects) if (o.Type == "MESH" && !byName.ContainsKey(o.Name)) byName[o.Name] = o;
        foreach (var (label, role) in Tiers)
        {
            var rnames = a.Names.TryGetValue(role, out var nl) ? nl : new List<string>();
            double pct = a.Reduce.TryGetValue(role, out double p) ? p : 0.0;
            if (rnames.Count == 0 || pct <= 0.5) continue;
            // bpy.ops.object.mode_set: the operator call updates the view layer first - every object's bounds_eval is current again
            foreach (var so in s.Objects) { so.EvalMin = null; so.EvalMax = null; }
            bool weld = label != "RIGGING" && label != "SHROUD";
            var rows = new List<(BlenderRigAnim.SceneObject o, int v0, int vmid, int target, int v1, int vraw, bool weld, string name, float ratio)>();
            foreach (string rn in rnames)
            {
                checkpoint?.Invoke();
                if (!byName.TryGetValue(rn, out var o)) { s.Logs2.Add("VEHICLE WARN: " + label.ToLowerInvariant() + " part '" + rn + "' not found — skipped"); continue; }
                int vraw = o.Mesh.Vertices;
                var sw = System.Diagnostics.Stopwatch.StartNew(); bool timing = Environment.GetEnvironmentVariable("VEHICLERIG_DEBUG_TIME") == "1";
                var layers = new BlenderWeld.Layers();
                var bm = FromMeshData(o.Mesh, layers, faceNormals: true, vertexNormals: false);
                if (timing) Console.Error.WriteLine($"TIME   from_mesh {sw.ElapsedMilliseconds} ms");
                // weld within each material: the vertices whose corners all share one material index, grouped as first met
                // o.dimensions reads the bounds as last evaluated (bounds_eval): a part listed twice in one tier sees its first pass's
                // input
                float[] mn, mx;
                if (o.EvalMin != null) { mn = o.EvalMin; mx = o.EvalMax; } else LocalBox(o.Mesh, out mn, out mx);
                float dim = 0f;
                for (int k = 0; k < 3; k++) { float ext = (float)(mx[k] - mn[k]); if (ext > dim) dim = ext; }   // o.dimensions at an identity transform, max()
                double dimD = dim == 0f ? 1.0 : dim;
                float dist = (float)(dimD * 1e-5);
                var groups = new List<(int mat, List<int> verts)>();
                var loops = new List<int>();
                for (int v = 0; v < bm.VertCount; v++)
                {
                    if (!bm.VAlive[v]) continue;
                    loops.Clear(); bm.LoopsOfVert(v, loops);
                    if (loops.Count == 0) continue;
                    int m0 = bm.FMat[bm.LF[loops[0]]]; bool same = true;
                    for (int i = 1; i < loops.Count && same; i++) same = bm.FMat[bm.LF[loops[i]]] == m0;
                    if (!same) continue;
                    int g = groups.FindIndex(x => x.mat == m0);
                    if (g < 0) { groups.Add((m0, new List<int>())); g = groups.Count - 1; }
                    groups[g].verts.Add(v);
                }
                if (timing) Console.Error.WriteLine($"TIME   grouping {sw.ElapsedMilliseconds} ms ({groups.Count} groups)");
                int welded = 0, sharp = 0;
                if (weld)
                {
                    foreach (var g in groups) welded += BlenderWeld.RemoveDoubles(bm, layers, g.verts, dist);
                    if (timing) Console.Error.WriteLine($"TIME {rn}: {vraw} verts, weld {sw.ElapsedMilliseconds} ms ({welded} merged)");
                    sharp = BlenderWeld.MarkSharpOverAngle(bm, 40.0);
                    if (timing) Console.Error.WriteLine($"TIME   sharp marked {sw.ElapsedMilliseconds} ms");
                }
                else
                {
                    // the SOUP tiers: _dissolve_limited(_rb, 5.0) - one dissolve_limit over every vertex and edge, or, past 4,000 faces, the
                    // faces split by their median centre into cells and each cell's interior edges and vertices dissolved apart
                    string death = DissolveLimited(bm, layers, 5.0);
                    if (death != null) { s.Dies = death; return; }
                    if (timing) Console.Error.WriteLine($"TIME {rn}: {vraw} verts, dissolve {sw.ElapsedMilliseconds} ms ({bm.TotVert} left, {bm.TotFace} faces)");
                    Note(s, "a soup tier: the limited dissolve");
                }
                // bmesh.ops.triangulate on every face: a no-op on triangles, the quads and n-gons the dissolve left cut by BEAUTY; the
                // operator's NORMALS_CALC finish recomputes the face normals
                int cut = BlenderPolyfill.TriangulateAll(bm, layers);
                if (cut > 0) { BlenderDissolve.FaceNormalsUpdateAll(bm); Note(s, "n-gons triangulated"); }
                if (timing) Console.Error.WriteLine($"TIME   triangulated {sw.ElapsedMilliseconds} ms ({cut} faces cut)");
                var d2 = ToMeshData(bm, layers, o.Mesh);
                if (timing) Console.Error.WriteLine($"TIME   to_mesh {sw.ElapsedMilliseconds} ms");
                if (weld) for (int f = 0; f < d2.FaceSharp.Length; f++) d2.FaceSharp[f] = false;   // p.use_smooth = True (the weld tiers)
                // bm.to_mesh only TAGS the mesh: Object.bound_box keeps the last evaluation's bounds until the next update
                if (o.EvalMin == null) { LocalBox(o.Mesh, out var emn, out var emx); o.EvalMin = emn; o.EvalMax = emx; }
                o.Mesh = d2;
                if (Environment.GetEnvironmentVariable("VEHICLERIG_DEBUG_WELD") == rn)
                {
                    DebugPart = rn;
                    Console.WriteLine($"WELD {rn}: verts {d2.Vertices} faces {d2.Faces.Length / 3} edges {d2.Edges.Length / 2} sharp {(d2.EdgeSharp == null ? 0 : d2.EdgeSharp.Count(x => x))} dist {dist.ToString("R", CultureInfo.InvariantCulture)} pos {Sha(d2.Positions)} loops {Sha(d2.Faces)} uv {(d2.Uv == null ? "-" : Sha(d2.Uv))} edges_sha {Sha(d2.Edges)}");
                }
                if (welded > 0) Note(s, "a weld tier: coincident vertices merged");
                if (sharp > 0) Note(s, "a weld tier: edges marked sharp");
                int vmid = d2.Vertices, v0 = weld ? vmid : vraw;   // the dial's base: the welded count, or the raw count on the soup pass
                int target = Math.Max(8, (int)(v0 * (1.0 - pct / 100.0)));
                if (vmid > target)
                {
                    float ratio = (float)Math.Max(0.02, (double)target / (double)vmid);   // _dm2.ratio: a float property
                    rows.Add((o, v0, vmid, target, -1, vraw, weld, rn, ratio));
                }
                else rows.Add((null, v0, vmid, target, vmid, vraw, weld, rn, 1f));
            }
            // the ONE depsgraph evaluation: every pending decimate, the evaluated mesh into a new datablock named after the old
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.o == null) continue;
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var d3 = Decimated(r.o.Mesh, r.ratio, s);
                if (Environment.GetEnvironmentVariable("VEHICLERIG_DEBUG_TIME") == "1") Console.Error.WriteLine($"TIME {r.name}: decimate {r.vmid} -> {d3.Vertices} in {sw2.ElapsedMilliseconds} ms");
                string old = r.o.DataName;
                r.o.DataName = names.MeshPool.Unique(old);   // bpy.data.meshes.new_from_object: the evaluated mesh's name, made unique
                names.MeshPool.Remove(old);                   // bpy.data.meshes.remove(_old)
                r.o.Mesh = d3;
                rows[i] = (r.o, r.v0, r.vmid, r.target, d3.Vertices, r.vraw, r.weld, r.name, r.ratio);
                Note(s, "a reduce tier: the decimate modifier applied");
            }
            // evaluated_depsgraph_get(), when a decimate is pending: every tagged object evaluated, its bounds current
            if (rows.Any(r => r.o != null)) foreach (var so in s.Objects) { so.EvalMin = null; so.EvalMax = null; }
            if (s.Objects.Any(so => so.EvalMin != null)) Note(s, "a part's bound_box left stale by the tier (no evaluation after its to_mesh)");
            long rrV0 = 0, rrV1 = 0; int rrN = 0;
            foreach (var r in rows)
            {
                rrV0 += r.v0; rrV1 += r.v1; rrN++;
                if (r.weld) s.Logs2.Add("VEHICLE " + label + " '" + r.name + "': " + r.v0 + " -> " + r.v1 + " verts (welded from " + r.vraw + " raw; dial " + Py(pct, 0) + "% = target " + r.target + ")");
                else s.Logs2.Add("VEHICLE " + label + " '" + r.name + "': " + r.v0 + " -> " + r.v1 + " verts (dissolve pass: " + r.vmid + "; dial " + Py(pct, 0) + "% = target " + r.target + ")");
            }
            if (rrN > 0) s.Logs2.Add("VEHICLE " + label + ": " + rrN + " part(s) reduced, " + rrV0 + " -> " + rrV1 + " verts total (" + Py(100.0 * (1.0 - (double)rrV1 / Math.Max(1L, rrV0)), 0) + "% cut)");
            Note(s, "the " + label + " tier");
        }
    }

    const int DissolveChunkFaces = 4000;

    /// <summary>vehicle_rig.py's _dissolve_limited: at or under the cap one dissolve_limit over every vertex and edge (slot order); past
    /// it the faces' median centres (BM_face_calc_center_median) BSP-split at the median along the widest axis - a Python stack,
    /// the second half popped first - until every cell holds at most the cap, then per leaf (in leaf order) the edges whose two
    /// faces are in the cell and the vertices whose faces all are, each list in index order, dissolved when either is non-empty.
    /// Every operator call recomputes the face normals when it finishes (NORMALS_CALC). Returns null on success, or the Python
    /// TypeError raised when a later cell names an element removed by an earlier collapse.</summary>
    static string DissolveLimited(BMesh bm, BlenderWeld.Layers layers, double angleDeg)
    {
        float angle = (float)(angleDeg * (Math.PI / 180.0));   // math.radians(5.0) into the float slot
        if (bm.TotFace <= DissolveChunkFaces)
        {
            var verts = new List<int>(); var edges = new List<int>();
            for (int v = 0; v < bm.VertCount; v++) if (bm.VAlive[v]) verts.Add(v);
            for (int e = 0; e < bm.EdgeCount; e++) if (bm.EAlive[e]) edges.Add(e);
            BlenderDissolve.DissolveLimit(bm, layers, angle, verts, edges);
            BlenderDissolve.FaceNormalsUpdateAll(bm);
            return null;
        }
        // (face, centre) for every face, in slot order
        var all = new List<(int f, float cx, float cy, float cz)>();
        for (int f = 0; f < bm.FaceCount; f++)
        {
            if (!bm.FAlive[f]) continue;
            float sx = 0f, sy = 0f, sz = 0f; int l = bm.FLFirst[f], l0 = l;
            do { int v = bm.LV[l]; sx = (float)(sx + bm.VCo[3 * v]); sy = (float)(sy + bm.VCo[3 * v + 1]); sz = (float)(sz + bm.VCo[3 * v + 2]); l = bm.LNext[l]; } while (l != l0);
            float inv = (float)(1.0f / (float)bm.FLen[f]);
            all.Add((f, (float)(sx * inv), (float)(sy * inv), (float)(sz * inv)));
        }
        var cells = new Stack<List<(int f, float cx, float cy, float cz)>>(); cells.Push(all);
        var leaves = new List<List<(int f, float cx, float cy, float cz)>>();
        while (cells.Count > 0)
        {
            var cell = cells.Pop();
            if (cell.Count <= DissolveChunkFaces) { leaves.Add(cell); continue; }
            // the spans per axis as Python computes them (max - min over the Python floats of float32 values), the first widest axis
            double[] spans = new double[3];
            for (int ax = 0; ax < 3; ax++)
            {
                double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
                foreach (var c in cell) { double x = ax == 0 ? c.cx : ax == 1 ? c.cy : c.cz; if (x < mn) mn = x; if (x > mx) mx = x; }
                spans[ax] = mx - mn;
            }
            int axis = 0; for (int ax = 1; ax < 3; ax++) if (spans[ax] > spans[axis]) axis = ax;
            // list.sort by the centre's coordinate: a stable sort
            var sorted = new List<(int f, float cx, float cy, float cz)>(cell);
            BlenderExportTree.StableSort(sorted, (a, b) => (axis == 0 ? a.cx : axis == 1 ? a.cy : a.cz).CompareTo(axis == 0 ? b.cx : axis == 1 ? b.cy : b.cz));
            int mid = sorted.Count / 2;
            cells.Push(sorted.GetRange(0, mid)); cells.Push(sorted.GetRange(mid, sorted.Count - mid));
        }
        var fid = new Dictionary<int, int>();
        for (int li = 0; li < leaves.Count; li++) foreach (var c in leaves[li]) fid[c.f] = li;
        // index_update: the vertex and edge indices in slot order
        var vIndex = new int[bm.VertCount]; int vc = 0; for (int v = 0; v < bm.VertCount; v++) vIndex[v] = bm.VAlive[v] ? vc++ : -1;
        var eIndex = new int[bm.EdgeCount]; int ec = 0; for (int e = 0; e < bm.EdgeCount; e++) eIndex[e] = bm.EAlive[e] ? ec++ : -1;
        var jobs = new List<(List<int> es, List<int> vs)>();
        var loops = new List<int>();
        for (int li = 0; li < leaves.Count; li++)
        {
            var es = new HashSet<int>(); var vs = new HashSet<int>();
            foreach (var c in leaves[li])
            {
                int l = bm.FLFirst[c.f], l0 = l;
                do
                {
                    int e = bm.LE[l];
                    // _e.link_faces: two faces, both in this leaf
                    int le = bm.EL[e]; int nf = 0; bool both = true;
                    if (le != BMesh.None) { int lr = le; do { nf++; if (!fid.TryGetValue(bm.LF[lr], out int fl) || fl != li) both = false; lr = bm.LRadialNext[lr]; } while (lr != le); }
                    if (nf == 2 && both) es.Add(e);
                    int v = bm.LV[l];
                    loops.Clear(); bm.LoopsOfVert(v, loops);
                    bool inLeaf = true; foreach (int lv in loops) if (!fid.TryGetValue(bm.LF[lv], out int fl) || fl != li) { inLeaf = false; break; }
                    if (inLeaf) vs.Add(v);
                    l = bm.LNext[l];
                } while (l != l0);
            }
            var esl = new List<int>(es); esl.Sort((a, b) => eIndex[a].CompareTo(eIndex[b]));
            var vsl = new List<int>(vs); vsl.Sort((a, b) => vIndex[a].CompareTo(vIndex[b]));
            jobs.Add((esl, vsl));
        }
        foreach (var (es, vs) in jobs)
        {
            if (es.Count == 0 && vs.Count == 0) continue;
            // A collapse can splice away an existing edge used by a later cell, even though its input vertices
            // were interior to the first cell. Python validates the stored BMVert/BMEdge wrappers before each op;
            // integer slots alone would silently reuse a removed element. Match that TypeError before mutating.
            for (int i = 0; i < vs.Count; i++) if (!bm.VAlive[vs[i]]) return "TypeError: verts: " + i + " BMVert has been removed";
            for (int i = 0; i < es.Count; i++) if (!bm.EAlive[es[i]]) return "TypeError: edges: " + i + " BMEdge has been removed";
            BlenderDissolve.DissolveLimit(bm, layers, angle, vs, es);
            BlenderDissolve.FaceNormalsUpdateAll(bm);
        }
        return null;
    }

    /// <summary>The DECIMATE modifier on a mesh: BKE_mesh_to_bmesh_ex with the face normals recomputed and the vertex normals the
    /// mesh reports (its custom normals mixed per vertex when it has them, as the prep port measured), the collapse, then
    /// BM_mesh_bm_to_me_for_eval - or the mesh as it is when the modifier would not run (ratio 1, three faces or fewer).</summary>
    static BlenderRigAnim.MeshData Decimated(BlenderRigAnim.MeshData d, float ratio, BlenderRigAnim.Stage1 s)
    {
        int nf = d.Faces.Length / 3;
        if (!BlenderDecimate.WouldRun(nf, ratio)) return CloneMesh(d);
        var layers = new BlenderWeld.Layers();
        var bm = FromMeshData(d, layers, faceNormals: true, vertexNormals: true);
        var dd = new BlenderDecimate.MeshData { CustomNormal = layers.CustomNormal };
        dd.Uv.AddRange(layers.Uv); dd.CornerColor.AddRange(layers.CornerColor); dd.PointColor.AddRange(layers.PointColor);
        BlenderDecimate.Collapse(bm, ratio, dd);
        layers.CustomNormal = dd.CustomNormal;
        return ToMeshData(bm, layers, d);
    }

    /// <summary>BM_mesh_bm_from_me on a stage mesh: the vertices in order, the edges in the mesh's order (SMOOTH unless sharp),
    /// the faces with their corners' edges (SMOOTH unless sharp_face, the material); the loop layers per loop slot (corner
    /// 3f + k), the point colours per vertex. Face normals as normal_tri_v3 when asked; vertex normals as the mesh reports
    /// them when asked.</summary>
    static BMesh FromMeshData(BlenderRigAnim.MeshData d, BlenderWeld.Layers layers, bool faceNormals, bool vertexNormals)
    {
        int nv = d.Vertices, nf = d.Faces.Length / 3;
        if (d.Edges == null) throw new InvalidOperationException("a stage mesh without its edge list");
        var bm = new BMesh();
        var swf = System.Diagnostics.Stopwatch.StartNew(); bool timing = Environment.GetEnvironmentVariable("VEHICLERIG_DEBUG_TIME") == "1";
        for (int v = 0; v < nv; v++) bm.VertCreate(d.Positions[3 * v], d.Positions[3 * v + 1], d.Positions[3 * v + 2]);
        if (timing) Console.Error.WriteLine($"TIME     verts {swf.ElapsedMilliseconds} ms");
        int ne = d.Edges.Length / 2;
        var edgeOf = new Dictionary<long, int>(ne, BlenderMesh.EdgeKeyComparer.Instance);   // a packed pair hashes badly as a plain long (its halves XOR): the mesh's own comparer
        for (int e = 0; e < ne; e++)
        {
            int a = d.Edges[2 * e], b = d.Edges[2 * e + 1];
            int i = bm.EdgeCreate(a, b);
            bm.EFlag[i] = d.EdgeSharp != null && d.EdgeSharp[e] ? (byte)0 : BMesh.FlagSmooth;
            edgeOf[EdgeKey(a, b)] = i;
        }
        if (timing) Console.Error.WriteLine($"TIME     edges {swf.ElapsedMilliseconds} ms ({ne})");
        var verts = new int[3]; var edges = new int[3];
        for (int f = 0; f < nf; f++)
        {
            for (int k = 0; k < 3; k++) verts[k] = d.Faces[3 * f + k];
            for (int k = 0; k < 3; k++)
                if (!edgeOf.TryGetValue(EdgeKey(verts[k], verts[(k + 1) % 3]), out edges[k])) throw new InvalidOperationException($"face {f}: no edge joins corners {verts[k]} and {verts[(k + 1) % 3]}");
            int i = bm.FaceCreate(verts, edges, 3);
            bm.FFlag[i] = d.FaceSharp != null && d.FaceSharp[f] ? (byte)0 : BMesh.FlagSmooth;
            bm.FMat[i] = d.FaceMaterial[f];
        }
        if (timing) Console.Error.WriteLine($"TIME     faces {swf.ElapsedMilliseconds} ms ({nf})");
        foreach (var u in d.UvAll) layers.Uv.Add((float[])u.Clone());
        if (d.UvAll.Count == 0 && d.Uv != null) layers.Uv.Add((float[])d.Uv.Clone());
        foreach (var c in d.Colors) { if (c.point) layers.PointColor.Add((byte[])c.bytes.Clone()); else layers.CornerColor.Add((byte[])c.bytes.Clone()); }
        layers.CustomNormal = d.CustomNormal == null ? null : (short[])d.CustomNormal.Clone();
        if (faceNormals) BlenderDecimate.FaceNormalsUpdate(bm);
        if (timing) Console.Error.WriteLine($"TIME     normals {swf.ElapsedMilliseconds} ms");
        if (vertexNormals)
        {
            float[] vno;
            if (d.CustomNormal != null)
            {
                var d0 = new short[3 * nf]; var d1 = new short[3 * nf];
                for (int c = 0; c < 3 * nf; c++) { d0[c] = d.CustomNormal[2 * c]; d1[c] = d.CustomNormal[2 * c + 1]; }
                vno = VehicleProbe.DecodeCustomShorts(d.Positions, d.Faces, d.FaceSharp ?? new bool[nf], d0, d1, VehicleProbe.SharpEdgeSet(d.Edges, d.EdgeSharp));
            }
            else vno = VehicleProbe.BlenderVertexNormals(d.Positions, d.Faces, null);
            Array.Copy(vno, bm.VNo, vno.Length);
            if (DebugPart != null) Console.WriteLine($"VNO {DebugPart}: {Sha(vno)} first {string.Join(" ", vno.Take(9).Select(x => x.ToString("R", CultureInfo.InvariantCulture)))}");
        }
        return bm;
    }

    static string DebugPart;
    static string Sha(float[] a) { var b = new byte[4 * a.Length]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return Sha(b); }
    static string Sha(int[] a) { var b = new byte[4 * a.Length]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return Sha(b); }
    static string Sha(byte[] b) { using (var h = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }

    static long EdgeKey(int a, int b) { int lo = Math.Min(a, b), hi = Math.Max(a, b); return ((long)lo << 32) | (uint)hi; }

    /// <summary>BM_mesh_bm_to_me: the live elements in slot order - vertices, edges (with sharp_edge), faces (with sharp_face
    /// and the material) and their corners; the layers gathered per corner and per vertex; the names of the old mesh kept.</summary>
    static BlenderRigAnim.MeshData ToMeshData(BMesh bm, BlenderWeld.Layers layers, BlenderRigAnim.MeshData old)
    {
        var newIndex = new int[bm.VertCount]; int count = 0;
        for (int v = 0; v < bm.VertCount; v++) newIndex[v] = bm.VAlive[v] ? count++ : -1;
        var r = new BlenderRigAnim.MeshData { Vertices = count, Positions = new float[3 * count], Materials = new List<string>(old.Materials), Groups = new List<string>(), ActiveColor = old.ActiveColor };
        for (int v = 0; v < bm.VertCount; v++)
        {
            if (!bm.VAlive[v]) continue;
            int i = newIndex[v];
            r.Positions[3 * i] = bm.VCo[3 * v]; r.Positions[3 * i + 1] = bm.VCo[3 * v + 1]; r.Positions[3 * i + 2] = bm.VCo[3 * v + 2];
        }
        var edges = new List<int>(); var edgeSharp = new List<bool>(); bool anySharp = false;
        for (int e = 0; e < bm.EdgeCount; e++)
        {
            if (!bm.EAlive[e]) continue;
            edges.Add(newIndex[bm.EV1[e]]); edges.Add(newIndex[bm.EV2[e]]);
            bool sharp = (bm.EFlag[e] & BMesh.FlagSmooth) == 0; edgeSharp.Add(sharp); anySharp |= sharp;
        }
        r.Edges = edges.ToArray(); r.EdgeSharp = anySharp ? edgeSharp.ToArray() : null;   // BM_mesh_bm_to_me: the attribute only when an edge is marked
        int nf = bm.TotFace;
        r.Faces = new int[3 * nf]; r.FaceMaterial = new int[nf]; r.FaceSharp = new bool[nf];
        var uvOut = new List<float[]>(); foreach (var _ in layers.Uv) uvOut.Add(new float[2 * 3 * nf]);
        var ccOut = new List<byte[]>(); foreach (var _ in layers.CornerColor) ccOut.Add(new byte[4 * 3 * nf]);
        var cnOut = layers.CustomNormal != null ? new short[2 * 3 * nf] : null;
        int fo = 0;
        for (int f = 0; f < bm.FaceCount; f++)
        {
            if (!bm.FAlive[f]) continue;
            if (bm.FLen[f] != 3) throw new NotSupportedException("a face of " + bm.FLen[f] + " corners: the stage rows hold triangles only");
            int l = bm.FLFirst[f];
            for (int k = 0; k < 3; k++)
            {
                int c = 3 * fo + k;
                r.Faces[c] = newIndex[bm.LV[l]];
                for (int i = 0; i < layers.Uv.Count; i++) { uvOut[i][2 * c] = layers.Uv[i][2 * l]; uvOut[i][2 * c + 1] = layers.Uv[i][2 * l + 1]; }
                for (int i = 0; i < layers.CornerColor.Count; i++) Array.Copy(layers.CornerColor[i], 4 * l, ccOut[i], 4 * c, 4);
                if (cnOut != null) { cnOut[2 * c] = layers.CustomNormal[2 * l]; cnOut[2 * c + 1] = layers.CustomNormal[2 * l + 1]; }
                l = bm.LNext[l];
            }
            r.FaceMaterial[fo] = bm.FMat[f];
            r.FaceSharp[fo] = (bm.FFlag[f] & BMesh.FlagSmooth) == 0;
            fo++;
        }
        r.UvAll = uvOut; r.Uv = uvOut.Count > 0 ? uvOut[0] : null; r.CustomNormal = cnOut;
        int pi = 0, ci = 0;
        foreach (var c in old.Colors)
        {
            if (c.point)
            {
                var src = layers.PointColor[pi++]; var outb = new byte[4 * count];
                for (int v = 0; v < bm.VertCount; v++) if (bm.VAlive[v]) Array.Copy(src, 4 * v, outb, 4 * newIndex[v], 4);
                r.Colors.Add((c.name, true, outb));
            }
            else r.Colors.Add((c.name, false, ccOut[ci++]));
        }
        return r;
    }
}
