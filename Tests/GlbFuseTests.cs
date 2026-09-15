using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

// FUSE (2026-09-15): several parts -> one welded shell with consistent winding, at the SOURCE (the Model Workshop).
// Fixtures are hand-built GLBs: a handful of quads whose positions, winding, UVs and materials are chosen so each
// rule has exactly one thing to prove. Outputs are read back from the written bytes, never from the inputs.
public class GlbFuseTests
{
    // ---- fixtures ----

    sealed class Part
    {
        public string Name; public float[] Positions; public int[] Indices; public float[] Uvs; public float[] Normals;
        public int Material = -1; public double[] Translation; public bool Skinned;
    }

    // a unit quad in the XY plane at z, spanning [x0,x1] x [y0,y1]; `inward` winds it so the geometric normal points -Z
    static Part Quad(string name, float x0, float x1, float y0, float y1, float z, bool inward = false, int material = -1)
    {
        var p = new Part { Name = name, Material = material,
            Positions = new[] { x0, y0, z,  x1, y0, z,  x1, y1, z,  x0, y1, z } };
        p.Indices = inward ? new[] { 0, 2, 1,  0, 3, 2 } : new[] { 0, 1, 2,  0, 2, 3 };
        return p;
    }

    // a closed axis-aligned box [0,s]^3 offset by o; `inward` winds every face toward the centre
    static Part Box(string name, float s, float ox, float oy, float oz, bool inward)
    {
        float[] v = {
            ox, oy, oz,  ox + s, oy, oz,  ox + s, oy + s, oz,  ox, oy + s, oz,
            ox, oy, oz + s,  ox + s, oy, oz + s,  ox + s, oy + s, oz + s,  ox, oy + s, oz + s };
        int[][] faces = { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 4, 7, 3 }, new[] { 1, 2, 6, 5 } };   // outward (CCW from outside)
        var idx = new List<int>();
        foreach (int[] q in faces)
        {
            if (inward) { idx.AddRange(new[] { q[0], q[2], q[1], q[0], q[3], q[2] }); }
            else { idx.AddRange(new[] { q[0], q[1], q[2], q[0], q[2], q[3] }); }
        }
        return new Part { Name = name, Positions = v, Indices = idx.ToArray() };
    }

    static byte[] BuildGlb(params Part[] parts)
    {
        var bin = new List<byte>();
        var accessors = new JArray(); var views = new JArray(); var meshes = new JArray(); var nodes = new JArray(); var sceneNodes = new JArray();
        int View(int offset, int length, int target) { views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length, ["target"] = target }); return views.Count - 1; }
        foreach (Part part in parts)
        {
            int vcount = part.Positions.Length / 3;
            int off = bin.Count; foreach (float f in part.Positions) bin.AddRange(BitConverter.GetBytes(f));
            double[] mins = { double.MaxValue, double.MaxValue, double.MaxValue }, maxs = { double.MinValue, double.MinValue, double.MinValue };
            for (int i = 0; i < vcount; i++) for (int a = 0; a < 3; a++) { mins[a] = Math.Min(mins[a], part.Positions[i * 3 + a]); maxs[a] = Math.Max(maxs[a], part.Positions[i * 3 + a]); }
            accessors.Add(new JObject { ["bufferView"] = View(off, vcount * 12, 34962), ["componentType"] = 5126, ["count"] = vcount, ["type"] = "VEC3", ["min"] = new JArray(mins), ["max"] = new JArray(maxs) });
            var attrs = new JObject { ["POSITION"] = accessors.Count - 1 };
            if (part.Normals != null)
            {
                off = bin.Count; foreach (float f in part.Normals) bin.AddRange(BitConverter.GetBytes(f));
                accessors.Add(new JObject { ["bufferView"] = View(off, vcount * 12, 34962), ["componentType"] = 5126, ["count"] = vcount, ["type"] = "VEC3" });
                attrs["NORMAL"] = accessors.Count - 1;
            }
            if (part.Uvs != null)
            {
                off = bin.Count; foreach (float f in part.Uvs) bin.AddRange(BitConverter.GetBytes(f));
                accessors.Add(new JObject { ["bufferView"] = View(off, vcount * 8, 34962), ["componentType"] = 5126, ["count"] = vcount, ["type"] = "VEC2" });
                attrs["TEXCOORD_0"] = accessors.Count - 1;
            }
            int[] indices = part.Indices ?? Enumerable.Range(0, vcount).ToArray();
            off = bin.Count; foreach (int i in indices) bin.AddRange(BitConverter.GetBytes((ushort)i));
            while ((bin.Count & 3) != 0) bin.Add(0);
            accessors.Add(new JObject { ["bufferView"] = View(off, indices.Length * 2, 34963), ["componentType"] = 5123, ["count"] = indices.Length, ["type"] = "SCALAR" });
            var prim = new JObject { ["attributes"] = attrs, ["indices"] = accessors.Count - 1 };
            if (part.Material >= 0) prim["material"] = part.Material;
            meshes.Add(new JObject { ["name"] = part.Name + "Mesh", ["primitives"] = new JArray { prim } });
            var node = new JObject { ["name"] = part.Name, ["mesh"] = meshes.Count - 1 };
            if (part.Translation != null) node["translation"] = new JArray(part.Translation);
            if (part.Skinned) node["skin"] = 0;
            nodes.Add(node); sceneNodes.Add(nodes.Count - 1);
        }
        var root = new JObject
        {
            ["asset"] = new JObject { ["version"] = "2.0" }, ["scene"] = 0,
            ["scenes"] = new JArray { new JObject { ["nodes"] = sceneNodes } },
            ["nodes"] = nodes, ["meshes"] = meshes, ["accessors"] = accessors, ["bufferViews"] = views,
            ["buffers"] = new JArray { new JObject { ["byteLength"] = bin.Count } },
            ["materials"] = new JArray { new JObject { ["name"] = "m0" }, new JObject { ["name"] = "m1" } },
        };
        return WriteGlb(root, bin.ToArray());
    }

    static byte[] WriteGlb(JObject root, byte[] bin)
    {
        byte[] json = Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
        int jsonPad = (4 - json.Length % 4) % 4, binPad = (4 - bin.Length % 4) % 4;
        var bytes = new List<byte>();
        void U32(uint v) { bytes.AddRange(BitConverter.GetBytes(v)); }
        U32(0x46546C67); U32(2); U32((uint)(12 + 8 + json.Length + jsonPad + 8 + bin.Length + binPad));
        U32((uint)(json.Length + jsonPad)); U32(0x4E4F534A); bytes.AddRange(json); for (int i = 0; i < jsonPad; i++) bytes.Add(0x20);
        U32((uint)(bin.Length + binPad)); U32(0x004E4942); bytes.AddRange(bin); for (int i = 0; i < binPad; i++) bytes.Add(0);
        return bytes.ToArray();
    }

    // ---- readers over the OUTPUT bytes ----

    sealed class Glb
    {
        public JObject Root; public byte[] Bin;
        public JObject Node(string name) => Root["nodes"].OfType<JObject>().First(n => (string)n["name"] == name);
        public JArray Primitives(JObject node) => (JArray)((JObject)Root["meshes"][node.Value<int>("mesh")])["primitives"];
        public float[] Floats(int accessor, int comps)
        {
            var a = (JObject)Root["accessors"][accessor]; var v = (JObject)Root["bufferViews"][a.Value<int>("bufferView")];
            int off = (v.Value<int?>("byteOffset") ?? 0) + (a.Value<int?>("byteOffset") ?? 0); int count = a.Value<int>("count");
            var r = new float[count * comps];
            for (int i = 0; i < r.Length; i++) r[i] = BitConverter.ToSingle(Bin, off + i * 4);
            return r;
        }
        public uint[] Indices(int accessor)
        {
            var a = (JObject)Root["accessors"][accessor]; var v = (JObject)Root["bufferViews"][a.Value<int>("bufferView")];
            int off = (v.Value<int?>("byteOffset") ?? 0) + (a.Value<int?>("byteOffset") ?? 0); int count = a.Value<int>("count");
            Assert.Equal(5125, a.Value<int>("componentType"));
            var r = new uint[count];
            for (int i = 0; i < count; i++) r[i] = BitConverter.ToUInt32(Bin, off + i * 4);
            return r;
        }
    }

    static Glb Read(byte[] glb)
    {
        uint jsonLen = BitConverter.ToUInt32(glb, 12);
        string json = Encoding.UTF8.GetString(glb, 20, (int)jsonLen);
        int binHeader = 20 + (int)jsonLen; uint binLen = BitConverter.ToUInt32(glb, binHeader);
        var bin = new byte[binLen]; Array.Copy(glb, binHeader + 8, bin, 0, binLen);
        return new Glb { Root = JObject.Parse(json), Bin = bin };
    }

    // per-triangle geometric normals of a primitive, from the written positions and indices
    static List<double[]> FaceNormals(Glb g, JObject prim)
    {
        float[] p = g.Floats(((JObject)prim["attributes"]).Value<int>("POSITION"), 3);
        uint[] ix = g.Indices(prim.Value<int>("indices"));
        var r = new List<double[]>();
        for (int t = 0; t < ix.Length; t += 3)
        {
            double ax = p[ix[t] * 3], ay = p[ix[t] * 3 + 1], az = p[ix[t] * 3 + 2];
            double bx = p[ix[t + 1] * 3] - ax, by = p[ix[t + 1] * 3 + 1] - ay, bz = p[ix[t + 1] * 3 + 2] - az;
            double cx = p[ix[t + 2] * 3] - ax, cy = p[ix[t + 2] * 3 + 1] - ay, cz = p[ix[t + 2] * 3 + 2] - az;
            r.Add(new[] { by * cz - bz * cy, bz * cx - bx * cz, bx * cy - by * cx });
        }
        return r;
    }

    static int TriangleCount(Glb g, JObject node) => g.Primitives(node).Sum(p => (int)g.Indices(((JObject)p).Value<int>("indices")).Length / 3);
    static int VertexCount(Glb g, JObject node) => g.Primitives(node).Sum(p => (int)g.Root["accessors"][((JObject)((JObject)p)["attributes"]).Value<int>("POSITION")].Value<int>("count"));

    // ---- the rules ----

    [Fact]
    public void Two_abutting_plates_one_inverted_become_one_consistent_welded_island()
    {
        // A: x 0..1, B: x 1..2, sharing the seam x=1 by POSITION only (separate nodes); B is wound inward.
        byte[] src = BuildGlb(Quad("A", 0, 1, 0, 1, 0), Quad("B", 1, 2, 0, 1, 0, inward: true));
        var r = GlbDisconnectedParts.FuseNodes(src, new[] { 0, 1 }, 0.001);

        Assert.True(r.Changed);
        Assert.Equal(4, r.SourceTriangles); Assert.Equal(4, r.OutputTriangles);
        Assert.Equal(1, r.IslandsBefore);          // the seam vertices coincide EXACTLY, so by position the plates already connect
        Assert.Equal(1, r.IslandsAfter);
        Assert.Equal(8, r.VerticesBefore); Assert.Equal(6, r.VerticesAfter);   // the two seam vertices welded
        Assert.Equal(2, r.FacesRewound);           // one plate's two triangles reversed to agree with the other (a tie: parity 1 goes)
        var g = Read(r.Bytes);
        var fused = g.Node("A_Fused");
        Assert.Null(g.Node("A")["mesh"]); Assert.Null(g.Node("B")["mesh"]);
        var normals = FaceNormals(g, (JObject)g.Primitives(fused)[0]);
        Assert.Equal(4, normals.Count);
        Assert.All(normals, n => Assert.True(Math.Sign(n[2]) == Math.Sign(normals[0][2]), "every face winds the same way"));
        Assert.Contains("1 made consistent", r.Details[0]);
    }

    [Fact]
    public void An_open_sheet_facing_the_hull_interior_is_reversed_a_correct_one_is_kept()
    {
        // Three separate open sheets (nothing touches): a side wall (normal +X, away from the hull centre — kept),
        // a bottom plate at y=0 (pulls the belly percentile down; scores ~0 — kept), and a deck at y=5 whose
        // normal points DOWN toward the axis — the one that must be reversed. Y is up.
        var wall = new Part { Name = "Wall", Positions = new float[] { 5, 0, -4,  5, 0, 4,  5, 6, 4,  5, 6, -4 }, Indices = new[] { 0, 3, 2, 0, 2, 1 } };   // (b-a)x(c-a): (0,6,0)x(0,6,8) = (48,0,0): +X, away from the centre
        var floor = new Part { Name = "Floor", Positions = new float[] { -4, 0, -3,  -4, 0, 3,  3, 0, 3,  3, 0, -3 }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };
        var deck = new Part { Name = "Deck", Positions = new float[] { -4, 5, -3,  3, 5, -3,  3, 5, 3,  -4, 5, 3 }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };   // (b-a)x(c-a): (7,0,0)x(7,0,6) = (0,-42,0): DOWN
        byte[] src = BuildGlb(wall, floor, deck);
        var r = GlbDisconnectedParts.FuseNodes(src, new[] { 0, 1, 2 }, 0.0);   // no welding: three open islands

        Assert.Equal(3, r.IslandsAfter);
        Assert.True(r.FacesRewound == 2, "the deck's two triangles only — " + r.Details[0]);
        var g = Read(r.Bytes);
        var normals = FaceNormals(g, (JObject)g.Primitives(g.Node("Wall_Fused"))[0]);
        Assert.Equal(2, normals.Count(n => n[0] > 0 && Math.Abs(n[1]) < 1e-6));   // wall still +X
        Assert.Equal(4, normals.Count(n => n[1] > 0));                            // deck now UP (plus the floor, authored up)
        Assert.Contains("3 open sheet(s) judged, 1 reversed", r.Details[0]);
    }

    [Fact]
    public void A_closed_shell_wound_inward_is_reversed_whole_a_correct_one_is_untouched()
    {
        byte[] inverted = BuildGlb(Box("Box", 2, 0, 0, 0, inward: true));
        var r = GlbDisconnectedParts.FuseNodes(inverted, new[] { 0 }, 0.001);
        Assert.Equal(12, r.FacesRewound);
        Assert.Contains("1 closed shell(s) reversed whole", r.Details[0]);
        var g = Read(r.Bytes);
        var normals = FaceNormals(g, (JObject)g.Primitives(g.Node("Box_Fused"))[0]);
        float[] p = g.Floats(((JObject)((JObject)g.Primitives(g.Node("Box_Fused"))[0])["attributes"]).Value<int>("POSITION"), 3);
        uint[] ix = g.Indices(((JObject)g.Primitives(g.Node("Box_Fused"))[0]).Value<int>("indices"));
        for (int t = 0; t < normals.Count; t++)
        {   // outward: the face normal points away from the box centre (1,1,1)
            double cx = (p[ix[t * 3] * 3] + p[ix[t * 3 + 1] * 3] + p[ix[t * 3 + 2] * 3]) / 3 - 1, cy = (p[ix[t * 3] * 3 + 1] + p[ix[t * 3 + 1] * 3 + 1] + p[ix[t * 3 + 2] * 3 + 1]) / 3 - 1, cz = (p[ix[t * 3] * 3 + 2] + p[ix[t * 3 + 1] * 3 + 2] + p[ix[t * 3 + 2] * 3 + 2]) / 3 - 1;
            Assert.True(normals[t][0] * cx + normals[t][1] * cy + normals[t][2] * cz > 0, "face " + t + " must face outward");
        }

        var ok = GlbDisconnectedParts.FuseNodes(BuildGlb(Box("Box", 2, 0, 0, 0, inward: false)), new[] { 0 }, 0.001);
        Assert.Equal(0, ok.FacesRewound);
        Assert.Contains("0 closed shell(s) reversed whole", ok.Details[0]);
    }

    [Fact]
    public void A_UV_seam_keeps_its_vertices_while_connectivity_still_merges_the_plates()
    {
        // same abutting plates, both correctly wound, but the seam vertices carry DIFFERENT UVs on each side
        var a = Quad("A", 0, 1, 0, 1, 0); a.Uvs = new float[] { 0, 0, 0.5f, 0, 0.5f, 1, 0, 1 };
        var b = Quad("B", 1, 2, 0, 1, 0); b.Uvs = new float[] { 0.7f, 0, 1, 0, 1, 1, 0.7f, 1 };   // x=1 edge: 0.7, not 0.5
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(a, b), new[] { 0, 1 }, 0.001);
        Assert.Equal(1, r.IslandsAfter);            // connected by position
        Assert.Equal(8, r.VerticesAfter);           // but no vertex merged across the UV seam
        Assert.Equal(0, r.FacesRewound);
        var g = Read(r.Bytes);
        var prim = (JObject)g.Primitives(g.Node("A_Fused"))[0];
        Assert.NotNull(((JObject)prim["attributes"])["TEXCOORD_0"]);
        float[] uv = g.Floats(((JObject)prim["attributes"]).Value<int>("TEXCOORD_0"), 2);
        Assert.Contains(0.5f, uv); Assert.Contains(0.7f, uv);
    }

    [Fact]
    public void Materials_become_separate_primitives_of_the_one_fused_mesh()
    {
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(Quad("A", 0, 1, 0, 1, 0, material: 0), Quad("B", 1, 2, 0, 1, 0, material: 1), Quad("C", 2, 3, 0, 1, 0, material: 0)), new[] { 0, 1, 2 }, 0.001);
        var g = Read(r.Bytes);
        var prims = g.Primitives(g.Node("A_Fused"));
        Assert.Equal(2, prims.Count);
        Assert.Equal(0, ((JObject)prims[0]).Value<int>("material")); Assert.Equal(1, ((JObject)prims[1]).Value<int>("material"));
        Assert.Equal(4, g.Indices(((JObject)prims[0]).Value<int>("indices")).Length / 3);   // A + C
        Assert.Equal(2, g.Indices(((JObject)prims[1]).Value<int>("indices")).Length / 3);   // B
        Assert.Equal(6, r.OutputTriangles);
    }

    [Fact]
    public void The_weld_distance_decides_whether_a_gap_joins()
    {
        // B starts 0.3 past A's edge: a gap of 0.3 on a model 2.3 long
        var far = BuildGlb(Quad("A", 0, 1, 0, 1, 0), Quad("B", 1.3f, 2.3f, 0, 1, 0, inward: true));
        var tight = GlbDisconnectedParts.FuseNodes(far, new[] { 0, 1 }, 0.05);    // 0.05 * 2.3 = 0.115 < 0.3: stays apart
        Assert.Equal(2, tight.IslandsAfter);
        Assert.Equal(8, tight.VerticesAfter);
        var wide = GlbDisconnectedParts.FuseNodes(far, new[] { 0, 1 }, 0.2);      // 0.2 * 2.3 = 0.46 > 0.3: welded, and B's winding follows A's
        Assert.Equal(1, wide.IslandsAfter);
        Assert.Equal(6, wide.VerticesAfter);
        Assert.Equal(2, wide.FacesRewound);
    }

    [Fact]
    public void A_lap_strip_lying_on_its_plate_and_facing_with_it_is_not_treated_as_inverted()
    {
        // The Teutonic's Object_8: a riveted strip folded back OVER the plate along a shared edge, both authored +Z.
        // In manifold terms a fold-back must face the other way, so the plain majority rule flipped every strip to
        // face inward and they rendered as dark lines. Same traversal + agreeing normals = a lap: leave it alone.
        var plate = new Part { Name = "Plate", Positions = new float[] { 0, 0, 0,  2, 0, 0,  2, 1, 0,  0, 1, 0 }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };           // +Z
        var strip = new Part { Name = "Strip", Positions = new float[] { 0, 1, 0,  0, 0.6f, 0.01f,  2, 0.6f, 0.01f,  2, 1, 0 }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };   // +Z, shares the y=1 edge, lies over the plate
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(plate, strip), new[] { 0, 1 }, 0.0);
        Assert.Equal(1, r.IslandsAfter);
        Assert.Equal(0, r.FacesRewound);
        var g = Read(r.Bytes);
        var normals = FaceNormals(g, (JObject)g.Primitives(g.Node("Plate_Fused"))[0]);
        Assert.Equal(4, normals.Count(n => n[2] > 0));   // plate AND strip still face +Z

        // the control: the same strip folded over but authored facing DOWN is a thin solid's LIP — the manifold rule
        // reads a fold-back facing the other way as consistent, and it is left exactly as authored (both cases: the
        // artist's winding wins; only a fold-back that CONTRADICTS the manifold rule and yet agrees in direction is a lap)
        var down = new Part { Name = "Strip", Positions = strip.Positions, Indices = new[] { 0, 2, 1, 0, 3, 2 } };   // -Z
        var r2 = GlbDisconnectedParts.FuseNodes(BuildGlb(plate, down), new[] { 0, 1 }, 0.0);
        Assert.Equal(0, r2.FacesRewound);
        var normals2 = FaceNormals(Read(r2.Bytes), (JObject)Read(r2.Bytes).Primitives(Read(r2.Bytes).Node("Plate_Fused"))[0]);
        Assert.Equal(2, normals2.Count(n => n[2] > 0));
        Assert.Equal(2, normals2.Count(n => n[2] < 0));
    }

    [Fact]
    public void A_triangle_smaller_than_the_weld_is_kept_collapsed_and_joins_no_island()
    {
        // a plate plus a rivet-sized triangle far from it; the weld distance (0.2 of the 2-long model = 0.4) swallows the rivet
        var rivet = new Part { Name = "Rivet", Positions = new float[] { 1.9f, 0.5f, 0,  2.0f, 0.5f, 0,  1.95f, 0.6f, 0 }, Indices = new[] { 0, 1, 2 } };
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(Quad("A", 0, 1, 0, 1, 0), rivet), new[] { 0, 1 }, 0.2);
        Assert.Equal(3, r.SourceTriangles); Assert.Equal(3, r.OutputTriangles);   // preserved, as always
        Assert.Equal(1, r.IslandsAfter);                                          // the collapsed rivet is no island
        Assert.Contains("1 face(s) smaller than the weld kept collapsed", r.Details[0]);
        Assert.StartsWith("largest islands: ", r.Details[1]);
        var g = Read(r.Bytes);
        Assert.Equal(3, TriangleCount(g, g.Node("A_Fused")));
        Assert.Equal(5, VertexCount(g, g.Node("A_Fused")));                       // 4 plate corners + the rivet's one welded vertex

        var exact = GlbDisconnectedParts.FuseNodes(BuildGlb(Quad("A", 0, 1, 0, 1, 0), rivet), new[] { 0, 1 }, 0.0);
        Assert.Equal(2, exact.IslandsAfter);                                      // at 0 nothing collapses: the rivet is its own open sheet
        Assert.Contains("0 face(s) smaller than the weld kept collapsed", exact.Details[0]);
    }

    [Fact]
    public void Parent_transforms_are_baked_into_the_fused_world_space_mesh()
    {
        var a = Quad("A", 0, 1, 0, 1, 0); a.Translation = new double[] { 10, 0, 0 };
        var b = Quad("B", 0, 1, 0, 1, 0); b.Translation = new double[] { 11, 0, 0 };   // abuts A at world x=11
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(a, b), new[] { 0, 1 }, 0.001);
        Assert.Equal(1, r.IslandsAfter);
        var g = Read(r.Bytes);
        var fused = g.Node("A_Fused");
        Assert.Null(fused["translation"]);
        float[] p = g.Floats(((JObject)((JObject)g.Primitives(fused)[0])["attributes"]).Value<int>("POSITION"), 3);
        Assert.Equal(10f, Enumerable.Range(0, p.Length / 3).Min(i => p[i * 3]));
        Assert.Equal(12f, Enumerable.Range(0, p.Length / 3).Max(i => p[i * 3]));
    }

    [Fact]
    public void Refuses_skinned_parts_empty_selections_and_bad_weld_values()
    {
        var skinned = new Part { Name = "S", Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Indices = new[] { 0, 1, 2 }, Skinned = true };
        Assert.Throws<System.IO.InvalidDataException>(() => GlbDisconnectedParts.FuseNodes(BuildGlb(skinned), new[] { 0 }, 0.001));
        Assert.Throws<ArgumentException>(() => GlbDisconnectedParts.FuseNodes(BuildGlb(Quad("A", 0, 1, 0, 1, 0)), new int[0], 0.001));
        Assert.Throws<ArgumentOutOfRangeException>(() => GlbDisconnectedParts.FuseNodes(BuildGlb(Quad("A", 0, 1, 0, 1, 0)), new[] { 0 }, -1));
    }

    [Fact]
    public void Vertex_normals_follow_the_final_winding()
    {
        // B authored inward WITH matching inward normals; after the majority flip its normals must point +Z like A's
        var a = Quad("A", 0, 1, 0, 1, 0); a.Normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 };
        var b = Quad("B", 1, 2, 0, 1, 0, inward: true); b.Normals = new float[] { 0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1 };
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(a, b), new[] { 0, 1 }, 0.001);
        var g = Read(r.Bytes);
        var prim = (JObject)g.Primitives(g.Node("A_Fused"))[0];
        float[] n = g.Floats(((JObject)prim["attributes"]).Value<int>("NORMAL"), 3);
        for (int i = 0; i < n.Length / 3; i++) Assert.True(n[i * 3 + 2] > 0.99f, "vertex " + i + " normal must point +Z after the flip");
    }
}
