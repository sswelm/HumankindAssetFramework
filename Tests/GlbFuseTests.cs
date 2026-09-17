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
        public string Name; public float[] Positions; public int[] Indices; public float[] Uvs; public float[] Normals; public float[] Colors; public float[] Uvs1;
        public int Material = -1; public double[] Translation; public double[] Scale; public bool Skinned;
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
            if (part.Uvs1 != null)
            {
                off = bin.Count; foreach (float f in part.Uvs1) bin.AddRange(BitConverter.GetBytes(f));
                accessors.Add(new JObject { ["bufferView"] = View(off, vcount * 8, 34962), ["componentType"] = 5126, ["count"] = vcount, ["type"] = "VEC2" });
                attrs["TEXCOORD_1"] = accessors.Count - 1;
            }
            if (part.Colors != null)
            {   // stored the compact way real exporters use: normalized unsigned bytes, RGBA
                off = bin.Count; foreach (float f in part.Colors) bin.Add((byte)Math.Round(f * 255));
                while ((bin.Count & 3) != 0) bin.Add(0);
                accessors.Add(new JObject { ["bufferView"] = View(off, vcount * 4, 34962), ["componentType"] = 5121, ["normalized"] = true, ["count"] = vcount, ["type"] = "VEC4" });
                attrs["COLOR_0"] = accessors.Count - 1;
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
            if (part.Scale != null) node["scale"] = new JArray(part.Scale);
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
        // a bottom plate at y=0 facing DOWN (a hull bottom: away from the belly a quarter up the model — kept), and a
        // deck at y=5 whose normal points DOWN toward the belly — the one that must be reversed. Y is up.
        var wall = new Part { Name = "Wall", Positions = new float[] { 5, 0, -4,  5, 0, 4,  5, 6, 4,  5, 6, -4 }, Indices = new[] { 0, 3, 2, 0, 2, 1 } };   // (b-a)x(c-a): (0,6,0)x(0,6,8) = (48,0,0): +X, away from the centre
        var floor = new Part { Name = "Floor", Positions = new float[] { -4, 0, -3,  -4, 0, 3,  3, 0, 3,  3, 0, -3 }, Indices = new[] { 0, 2, 1, 0, 3, 2 } };   // wound to face DOWN
        var deck = new Part { Name = "Deck", Positions = new float[] { -4, 5, -3,  3, 5, -3,  3, 5, 3,  -4, 5, 3 }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };   // (b-a)x(c-a): (7,0,0)x(7,0,6) = (0,-42,0): DOWN
        byte[] src = BuildGlb(wall, floor, deck);
        var r = GlbDisconnectedParts.FuseNodes(src, new[] { 0, 1, 2 }, 0.0);   // no welding: three open islands

        Assert.Equal(3, r.IslandsAfter);
        Assert.True(r.FacesRewound == 2, "the deck's two triangles only — " + r.Details[0]);
        var g = Read(r.Bytes);
        var normals = FaceNormals(g, (JObject)g.Primitives(g.Node("Wall_Fused"))[0]);
        Assert.Equal(2, normals.Count(n => n[0] > 0 && Math.Abs(n[1]) < 1e-6));   // wall still +X
        Assert.Equal(2, normals.Count(n => n[1] > 0));                            // deck now UP
        Assert.Equal(2, normals.Count(n => n[1] < 0));                            // the hull bottom still DOWN
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

    // ---- review of 4e748c1 (2026-09-15) ----

    // a flat n x n grid of quads in the XZ plane at height y, every triangle wound to face +Y
    static Part Grid(string name, int n, float y)
    {
        var p = new List<float>(); var idx = new List<int>();
        for (int r = 0; r <= n; r++) for (int c = 0; c <= n; c++) { p.Add(c); p.Add(y); p.Add(r); }
        for (int r = 0; r < n; r++) for (int c = 0; c < n; c++)
        {
            int a = r * (n + 1) + c, b = a + 1, d = a + (n + 1), e = d + 1;   // (b-a)x(d-a) = (1,0,0)x(0,0,1) = (0,-1,0): so wind a,d,b for +Y
            idx.AddRange(new[] { a, d, b, b, d, e });
        }
        return new Part { Name = name, Positions = p.ToArray(), Indices = idx.ToArray() };
    }

    [Fact]
    public void A_densely_triangulated_open_deck_below_the_origin_is_not_a_closed_shell()
    {
        // 5x5 quads: 20 boundary edges of 85 (23 %) — under a "mostly closed" threshold this was judged by signed
        // volume, and a correct upward deck at y=-1 came back with all 50 triangles reversed. Closed means no boundary edge.
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(Grid("Deck", 5, -1)), new[] { 0 }, 0.0);
        Assert.Equal(50, r.SourceTriangles);
        Assert.Equal(0, r.FacesRewound);
        Assert.Contains("1 open sheet(s) judged, 0 reversed; 0 closed shell(s) reversed whole", r.Details[0]);
        var g = Read(r.Bytes);
        Assert.All(FaceNormals(g, (JObject)g.Primitives(g.Node("Deck_Fused"))[0]), n => Assert.True(n[1] > 0, "still faces up"));

        // and a genuinely closed shell is still judged by volume wherever it sits: a box entirely below and behind the origin
        var far = GlbDisconnectedParts.FuseNodes(BuildGlb(Box("Box", 2, -10, -10, -10, inward: true)), new[] { 0 }, 0.0);
        Assert.Equal(12, far.FacesRewound);
        Assert.Contains("1 closed shell(s) reversed whole", far.Details[0]);
        var farOk = GlbDisconnectedParts.FuseNodes(BuildGlb(Box("Box", 2, -10, -10, -10, inward: false)), new[] { 0 }, 0.0);
        Assert.Equal(0, farOk.FacesRewound);
    }

    [Fact]
    public void A_gap_inside_the_weld_closes_even_where_a_UV_seam_keeps_separate_vertices()
    {
        // B starts 0.01 past A's edge with DIFFERENT UVs at the seam: connectivity welded them (one island) but the
        // separately emitted seam vertices kept x=1.0 and x=1.01 — a visible crack. One position per welded class.
        var a = Quad("A", 0, 1, 0, 1, 0); a.Uvs = new float[] { 0, 0, 0.5f, 0, 0.5f, 1, 0, 1 };
        var b = Quad("B", 1.01f, 2.01f, 0, 1, 0); b.Uvs = new float[] { 0.7f, 0, 1, 0, 1, 1, 0.7f, 1 };
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(a, b), new[] { 0, 1 }, 0.05);   // 0.05 * 2.01 = 0.1 > 0.01
        Assert.Equal(1, r.IslandsAfter);
        Assert.Equal(8, r.VerticesAfter);            // the UV seam still keeps its own vertices…
        var g = Read(r.Bytes);
        var prim = (JObject)g.Primitives(g.Node("A_Fused"))[0];
        float[] p = g.Floats(((JObject)prim["attributes"]).Value<int>("POSITION"), 3);
        var xs = Enumerable.Range(0, p.Length / 3).Select(i => p[i * 3]).OrderBy(x => x).ToList();
        Assert.Equal(4, xs.Count(x => Math.Abs(x - 1.005f) < 1e-5f));   // …but all four seam vertices sit on ONE line, the class centroid
        Assert.DoesNotContain(xs, x => Math.Abs(x - 1.0f) < 1e-6f || Math.Abs(x - 1.01f) < 1e-6f);
        Assert.Equal(2, xs.Count(x => x == 0f)); Assert.Equal(2, xs.Count(x => Math.Abs(x - 2.01f) < 1e-6f));   // the far edges untouched

        // at weld 0 nothing inside rounding is touched: the exact-seam plates keep their authored coordinates bit for bit
        var exact = GlbDisconnectedParts.FuseNodes(BuildGlb(Quad("A", 0, 1, 0, 1, 0), Quad("B", 1, 2, 0, 1, 0)), new[] { 0, 1 }, 0.0);
        float[] pe = Read(exact.Bytes).Floats(((JObject)((JObject)Read(exact.Bytes).Primitives(Read(exact.Bytes).Node("A_Fused"))[0])["attributes"]).Value<int>("POSITION"), 3);
        Assert.All(pe, v => Assert.True(v == 0f || v == 1f || v == 2f, "coordinate " + v));
    }

    [Fact]
    public void A_mirrored_instance_keeps_the_facing_it_renders_with()
    {
        // glTF: a node with a negative-determinant transform renders its triangles with the front face reversed. The
        // Teutonic's port half is the starboard meshes under a (0.0254, -0.0254, 0.0254) node; baked to world space with
        // the raw winding it arrived inside-out and the fuse "corrected" it by reversing whole (by luck, per island).
        var a = Quad("A", 0, 1, 0, 1, 0); a.Scale = new double[] { -1, 1, 1 };   // mirrored across X: world x -1..0, raw winding now reads -Z, rendered front is +Z
        var b = Quad("B", 0, 1, 0, 1, 0);                                       // abuts it at x=0, +Z
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(a, b), new[] { 0, 1 }, 0.0);
        Assert.Equal(1, r.IslandsAfter);
        Assert.Equal(0, r.FacesRewound);   // nothing to correct: both render +Z, and the fused mesh is written that way
        var g = Read(r.Bytes);
        var normals = FaceNormals(g, (JObject)g.Primitives(g.Node("A_Fused"))[0]);
        Assert.Equal(4, normals.Count); Assert.All(normals, n => Assert.True(n[2] > 0, "faces +Z"));
        float[] p = g.Floats(((JObject)((JObject)g.Primitives(g.Node("A_Fused"))[0])["attributes"]).Value<int>("POSITION"), 3);
        Assert.Equal(-1f, Enumerable.Range(0, p.Length / 3).Min(i => p[i * 3]));
    }

    [Fact]
    public void Vertex_colours_and_a_second_UV_set_survive_the_fuse_and_keep_their_own_vertices()
    {
        // review of 3052ed0: the fused mesh kept only POSITION / NORMAL / TEXCOORD_0 — a coloured mesh lost COLOR_0.
        // A is red, B is blue, both carry a second UV set; the seam vertices differ in colour so they must NOT merge.
        var a = Quad("A", 0, 1, 0, 1, 0); a.Colors = Enumerable.Repeat(new[] { 1f, 0f, 0f, 1f }, 4).SelectMany(c => c).ToArray(); a.Uvs1 = new float[] { 0, 0, 1, 0, 1, 1, 0, 1 };
        var b = Quad("B", 1, 2, 0, 1, 0); b.Colors = Enumerable.Repeat(new[] { 0f, 0f, 1f, 1f }, 4).SelectMany(c => c).ToArray(); b.Uvs1 = new float[] { 0, 0, 1, 0, 1, 1, 0, 1 };
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(a, b), new[] { 0, 1 }, 0.0);
        Assert.Equal(1, r.IslandsAfter);
        Assert.Equal(8, r.VerticesAfter);   // the colour seam keeps its vertices
        var g = Read(r.Bytes);
        var attrs = (JObject)((JObject)g.Primitives(g.Node("A_Fused"))[0])["attributes"];
        Assert.NotNull(attrs["COLOR_0"]); Assert.NotNull(attrs["TEXCOORD_1"]);
        float[] col = g.Floats(attrs.Value<int>("COLOR_0"), 4);
        Assert.Equal(4, Enumerable.Range(0, 8).Count(i => col[i * 4] > 0.99f && col[i * 4 + 2] < 0.01f));   // four red…
        Assert.Equal(4, Enumerable.Range(0, 8).Count(i => col[i * 4 + 2] > 0.99f && col[i * 4] < 0.01f));   // …four blue, nothing blended
        Assert.Equal(16, g.Floats(attrs.Value<int>("TEXCOORD_1"), 2).Length);

        // the same colours (and matching second UVs) on both sides DO merge, as first UVs do
        b.Colors = a.Colors; b.Uvs1 = new float[] { 1, 0, 2, 0, 2, 1, 1, 1 };
        var same = GlbDisconnectedParts.FuseNodes(BuildGlb(a, b), new[] { 0, 1 }, 0.0);
        Assert.Equal(6, same.VerticesAfter);

        // a part without colours fused with one that has them: the attribute stays, the colourless part is white
        var plain = Quad("C", 2, 3, 0, 1, 0);
        var mixed = GlbDisconnectedParts.FuseNodes(BuildGlb(a, plain), new[] { 0, 1 }, 0.0);
        var ga = (JObject)((JObject)Read(mixed.Bytes).Primitives(Read(mixed.Bytes).Node("A_Fused"))[0])["attributes"];
        float[] mc = Read(mixed.Bytes).Floats(ga.Value<int>("COLOR_0"), 4);
        Assert.Equal(4, Enumerable.Range(0, mc.Length / 4).Count(i => mc[i * 4] > 0.99f && mc[i * 4 + 1] > 0.99f && mc[i * 4 + 2] > 0.99f));
    }

    [Fact]
    public void Non_uniform_scale_carries_normals_by_the_inverse_transpose()
    {
        // review of 3052ed0: normals went through the plain world matrix. A triangle with normal (1,1,1)/√3 under scale
        // (2,1,1) has the surface normal (1,2,2)/3; the plain matrix gives (2,1,1)/√6 — 35° off.
        float k = (float)(1 / Math.Sqrt(3));
        var t = new Part { Name = "T", Positions = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, Indices = new[] { 0, 1, 2 }, Normals = new[] { k, k, k, k, k, k, k, k, k }, Scale = new double[] { 2, 1, 1 } };
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(t), new[] { 0 }, 0.0);
        // a lone sloped triangle has no meaningful facing, so the direction rule may or may not rewind it; the claim
        // under test is the DIRECTION of the carried normal up to that sign, and its agreement with the written geometry
        var g = Read(r.Bytes);
        float[] n = g.Floats(((JObject)((JObject)g.Primitives(g.Node("T_Fused"))[0])["attributes"]).Value<int>("NORMAL"), 3);
        for (int i = 0; i < n.Length / 3; i++)
            Assert.True(Math.Abs((n[i * 3] * 1 + n[i * 3 + 1] * 2 + n[i * 3 + 2] * 2) / 3) > 0.999, "vertex " + i + " normal must be ±(1,2,2)/3, got " + n[i * 3] + "," + n[i * 3 + 1] + "," + n[i * 3 + 2]);
        // and the written normal agrees with the written geometry
        var fn = FaceNormals(g, (JObject)g.Primitives(g.Node("T_Fused"))[0])[0];
        double len = Math.Sqrt(fn[0] * fn[0] + fn[1] * fn[1] + fn[2] * fn[2]);
        Assert.True((fn[0] * n[0] + fn[1] * n[1] + fn[2] * n[2]) / len > 0.999);
    }

    [Fact]
    public void A_part_without_UVs_does_not_strip_the_UVs_of_its_material_mates()
    {
        // review of 82088d4: one unmapped contributor made the whole material's primitive drop TEXCOORD_0
        var a = Quad("A", 0, 1, 0, 1, 0); a.Uvs = new float[] { 0, 0, 0.5f, 0, 0.5f, 1, 0, 1 };
        var b = Quad("B", 1, 2, 0, 1, 0);   // no UVs, same (absent) material
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(a, b), new[] { 0, 1 }, 0.0);
        var g = Read(r.Bytes);
        var prim = (JObject)g.Primitives(g.Node("A_Fused"))[0];
        Assert.NotNull(((JObject)prim["attributes"])["TEXCOORD_0"]);
        float[] uv = g.Floats(((JObject)prim["attributes"]).Value<int>("TEXCOORD_0"), 2);
        Assert.Equal(r.VerticesAfter * 2, uv.Length);
        Assert.Equal(8, r.VerticesAfter);            // A's seam (UV 0.5) and B's seam (no UV) stay separate vertices
        Assert.Equal(2, uv.Count(x => x == 0.5f));   // A's coordinates intact…
        Assert.Equal(4, Enumerable.Range(0, uv.Length / 2).Count(i => uv[i * 2] == 0f && uv[i * 2 + 1] == 0f) - 1);   // …B's four padded (0,0), plus A's own (0,0) corner

        var none = GlbDisconnectedParts.FuseNodes(BuildGlb(Quad("A", 0, 1, 0, 1, 0), b), new[] { 0, 1 }, 0.0);   // nobody has UVs: none written
        Assert.Null(((JObject)((JObject)Read(none.Bytes).Primitives(Read(none.Bytes).Node("A_Fused"))[0])["attributes"])["TEXCOORD_0"]);
    }

    [Fact]
    public void The_lap_warning_names_strips_lying_on_a_plate_and_not_covers_or_rails_that_share_its_edge()
    {
        // 2026-09-16, the lifeboats: vertex sharing alone flagged every gunwale rail and cover (100 % shared by
        // construction). A lap is parallel to the plate AND its face centres sit on it.
        // the plate has a vertex row at y=0.6 so the lap — a riveted strake — coincides with plate vertices along BOTH its
        // long edges, as Object_8's strips do (86 % of their vertices exactly on plate vertices), and lies flat on it
        var plate = new Part { Name = "Plate", Positions = new float[] { 0, 0, 0,  2, 0, 0,  2, 0.6f, 0,  0, 0.6f, 0,  2, 1, 0,  0, 1, 0 }, Indices = new[] { 0, 1, 2, 0, 2, 3, 3, 2, 4, 3, 4, 5 } };   // +Z, 2 x 1
        var lap = new Part { Name = "Lap", Positions = new float[] { 0, 1, 0,  0, 0.6f, 0,  2, 0.6f, 0,  2, 1, 0 }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };                        // over the plate's top strip, +Z
        // a boat-like case: two walls and a cover spanning their top edges — every cover vertex is a wall vertex (100 %
        // shared, like a lifeboat cover on the gunwale) but the cover's faces stand off the walls
        var wallL = new Part { Name = "WallL", Positions = new float[] { 5, 0, 0,  5, 1, 0,  5, 1, 0.5f,  5, 0, 0.5f }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };
        var wallR = new Part { Name = "WallR", Positions = new float[] { 7, 0, 0,  7, 1, 0,  7, 1, 0.5f,  7, 0, 0.5f }, Indices = new[] { 0, 2, 1, 0, 3, 2 } };
        var cover = new Part { Name = "Cover", Positions = new float[] { 5, 0, 0.5f,  7, 0, 0.5f,  7, 1, 0.5f,  5, 1, 0.5f }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(plate, lap, wallL, wallR, cover), new[] { 0, 1, 2, 3, 4 }, 0.0);
        string warning = r.Warnings.FirstOrDefault(w => w.Contains("lap/trim strip"));
        Assert.NotNull(warning);
        Assert.Contains("'Lap'", warning); Assert.DoesNotContain("'Cover'", warning);
        Assert.Contains("1 lap/trim strip(s)", warning);   // one line for the whole fuse, the parts named in it
        string stitched = r.Details.FirstOrDefault(d => d.StartsWith("stitched parts: "));
        Assert.NotNull(stitched); Assert.Contains("Cover", stitched); Assert.Contains("Lap", stitched);   // both share 100 % of their vertices…
        Assert.Contains("Cover 100% verts shared, 0% of its touching faces lying on them", stitched);       // …only the lap lies on the plate
        Assert.StartsWith("Fused ", r.Details[0]);                   // the summary stays first: it is what the Workshop status shows (review of ce91915)
        Assert.StartsWith("largest islands: ", r.Details[1]);
        Assert.Same(stitched, r.Details[2]);

        // a lap wound the OTHER way is still a lap: same overlap, same crease under reduction (review of ce91915)
        var lapReversed = new Part { Name = "Lap", Positions = lap.Positions, Indices = new[] { 0, 2, 1, 0, 3, 2 } };
        var r2 = GlbDisconnectedParts.FuseNodes(BuildGlb(plate, lapReversed, wallL, wallR, cover), new[] { 0, 1, 2, 3, 4 }, 0.0);   // same fixture: a lap must also be a SMALL part (under a quarter of the vertices)
        Assert.Contains(r2.Warnings, w => w.Contains("lap/trim strip") && w.Contains("'Lap'"));
    }

    [Fact]
    public void Analyze_reports_each_part_s_world_bbox_and_vertex_count_for_the_list_filters()
    {
        var a = Quad("A", 0, 1, 0, 1, 0); a.Translation = new double[] { 10, 0, 0 };
        var b = Quad("B", 0, 2, 0, 1, 0); b.Scale = new double[] { 1, 3, 1 };
        var parts = GlbDisconnectedParts.Analyze(BuildGlb(a, b));
        Assert.Equal(2, parts.Count);
        Assert.Equal(4, parts[0].Vertices);
        Assert.Equal(new double[] { 10, 0, 0 }, parts[0].Min); Assert.Equal(new double[] { 11, 1, 0 }, parts[0].Max);   // through the node transform: world space
        Assert.Equal(new double[] { 0, 0, 0 }, parts[1].Min); Assert.Equal(new double[] { 2, 3, 0 }, parts[1].Max);
    }

    [Fact]
    public void A_deck_group_is_judged_against_the_whole_model_s_belly_not_its_own()
    {
        // 2026-09-16, the Teutonic's deck strips: a group made only of two deck strips (y = 8) has its own belly line
        // at y = 8, so a strip facing DOWN scored ~0 and stayed down. The hull (node 0, NOT in the group) puts the
        // model's belly at its bottom, and the strips must then be reversed to face up.
        var hull = Box("Hull", 6, 0, 0, 0, inward: false);                                                   // y 0..6
        var deck = new Part { Name = "Deck", Positions = new float[] { 1, 8, 1,  5, 8, 1,  5, 8, 5,  1, 8, 5 }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };       // (b-a)x(c-a) = (4,0,0)x(4,0,4) = (0,-16,0): DOWN
        var deck2 = new Part { Name = "Deck2", Positions = new float[] { 1, 8, 6,  5, 8, 6,  5, 8, 9,  1, 8, 9 }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };   // DOWN too, separate island
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(hull, deck, deck2), new[] { 1, 2 }, 0.0);   // the hull is not fused, only measured
        Assert.Equal(2, r.IslandsAfter);
        Assert.Equal(4, r.FacesRewound);
        Assert.Contains("2 open sheet(s) judged, 2 reversed", r.Details[0]);
        var g = Read(r.Bytes);
        Assert.All(FaceNormals(g, (JObject)g.Primitives(g.Node("Deck_Fused"))[0]), n => Assert.True(n[1] > 0, "faces up"));
        Assert.NotNull(g.Node("Hull")["mesh"]);   // untouched
    }

    [Fact]
    public void An_island_whose_parity_cannot_be_satisfied_is_left_as_authored()
    {
        // 2026-09-17, the Teutonic's propeller blades: a surface with an odd cycle — here a Möbius band of three quads,
        // the third joining back to the first edge with a half twist — has no consistent winding. The old rule flipped
        // the "minority" of a parity assignment that could never be satisfied (198 faces per blade, holes at every tip).
        float r = 3f, w = 1f; var pos = new List<float>(); var idx = new List<int>();
        for (int k = 0; k < 3; k++)
        {
            double a = k * 2 * Math.PI / 3, half = a / 2;   // the half twist
            float cx = (float)(r * Math.Cos(a)), cz = (float)(r * Math.Sin(a));
            float ux = (float)(Math.Cos(half) * Math.Cos(a) * w), uy = (float)(Math.Sin(half) * w), uz = (float)(Math.Cos(half) * Math.Sin(a) * w);
            pos.AddRange(new[] { cx - ux, 5 - uy, cz - uz,  cx + ux, 5 + uy, cz + uz });   // A_k, B_k
        }
        for (int k = 0; k < 3; k++)
        {
            int a0 = 2 * k, b0 = 2 * k + 1, a1, b1;
            if (k < 2) { a1 = 2 * (k + 1); b1 = 2 * (k + 1) + 1; } else { a1 = 1; b1 = 0; }   // the twist: A2->B0, B2->A0
            idx.AddRange(new[] { a0, b0, b1, a0, b1, a1 });
        }
        var band = new Part { Name = "Band", Positions = pos.ToArray(), Indices = idx.ToArray() };
        var hull = Box("Hull", 6, -3, -3, -3, inward: false);   // the model's belly, outside the group
        var res = GlbDisconnectedParts.FuseNodes(BuildGlb(hull, band), new[] { 1 }, 0.0);
        Assert.Equal(1, res.IslandsAfter);
        Assert.Contains("0 made consistent, 1 not orientable by traversal (kept as authored)", res.Details[0]);
        Assert.Contains("not orientable, kept as authored", res.Details[1]);
        Assert.Contains("not judged", res.Details[1]);
        Assert.Equal(0, res.FacesRewound);
        // KEPT means kept: the same band wound the other way is not reversed whole by the direction pass either (review of 0097bd5)
        var bandReversed = new Part { Name = "Band", Positions = pos.ToArray(), Indices = idx.Select((v, i) => i % 3 == 1 ? idx[i + 1] : i % 3 == 2 ? idx[i - 1] : v).ToArray() };
        var res2 = GlbDisconnectedParts.FuseNodes(BuildGlb(hull, bandReversed), new[] { 1 }, 0.0);
        Assert.Equal(0, res2.FacesRewound);
        Assert.Contains("1 not orientable by traversal (kept as authored)", res2.Details[0]);
        // and a plain inverted-plate seam (2-colourable) is still fixed: the existing first test covers it
    }

    [Fact]
    public void Every_island_is_listed_for_the_report_while_the_status_keeps_six()
    {
        var parts = Enumerable.Range(0, 7).Select(i => Quad("Q" + i, i * 3, i * 3 + 1, 0, 1, 0)).ToArray();   // seven separate plates
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(parts), Enumerable.Range(0, 7).ToArray(), 0.0);
        Assert.Equal(7, r.IslandsAfter);
        Assert.Equal(7, r.IslandLines.Count);
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(r.Details[1], @"\d+ faces \(").Count);   // the status line: the largest six
        Assert.All(r.IslandLines, l => Assert.StartsWith("2 faces (", l));
    }

    [Fact]
    public void Analyze_reports_each_part_s_parent_node()
    {
        var a = Quad("A", 0, 1, 0, 1, 0);
        var src = GlbDisconnectedParts.Split(BuildGlb(a, Quad("B", 5, 6, 0, 1, 0)), new HashSet<int> { 1 }, 0.0);   // B has one island: nothing to split, stays as is
        var parts = GlbDisconnectedParts.Analyze(BuildGlb(a));
        Assert.Equal(-1, parts[0].ParentIndex);   // a root node
        var table = GlbDisconnectedParts.NodeParents(BuildGlb(a));
        Assert.Equal(new KeyValuePair<int, int>(0, -1), table[0]);
    }

    [Fact]
    public void The_path_guard_refuses_output_equal_to_source()
    {
        Assert.Throws<InvalidOperationException>(() => GlbDisconnectedParts.GuardPaths(@"C:\models\ship.glb", @"C:/models/SHIP.GLB"));
        GlbDisconnectedParts.GuardPaths(@"C:\models\ship.glb", @"C:\models\ship_fused.glb");
    }
}
