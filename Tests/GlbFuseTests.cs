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

    // a box without its top face (5 faces): an OPEN thin solid's two skins cancel in volume, so the twin rule decides
    static Part OpenBox(string name, float s, float ox, float oy, float oz, bool inward)
    {
        var full = Box(name, s, ox, oy, oz, inward);
        return new Part { Name = name, Positions = full.Positions, Indices = full.Indices.Take(6).Concat(full.Indices.Skip(12)).ToArray() };   // face 1 (top, +Y) dropped
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
        // (2026-09-17) that control is a thin solid whose two skins face INTO their own gap — the double-skin rule now reads
        // it as inside-out and turns it whole (plate down, strip up: material behind both), which is the right reading
        Assert.Equal(4, r2.FacesRewound);
        Assert.Contains("double skin", r2.Details[1]);
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

    // ---- the mirrored-part check (2026-09-19, the Confederate frigate; option "Check mirrored parts") ----
    // A 1 x 3 strip of quads in the XY plane at z, spanning x0..x1 and y 0..3: its x = x0 column is three edges long, so
    // a strip abutting it shares three seam edges (the check wants at least three before it judges a part). `inward`
    // winds it so the geometric normal points -Z.
    static Part Strip(string name, float x0, float x1, float z, bool inward)
    {
        var pos = new List<float>(); var idx = new List<int>();
        for (int j = 0; j <= 3; j++) { pos.AddRange(new[] { x0, (float)j, z }); pos.AddRange(new[] { x1, (float)j, z }); }
        for (int k = 0; k < 3; k++)
        {
            int v00 = 2 * k, v10 = 2 * k + 1, v11 = 2 * k + 3, v01 = 2 * k + 2;
            idx.AddRange(inward ? new[] { v00, v11, v10, v00, v01, v11 } : new[] { v00, v10, v11, v00, v11, v01 });
        }
        return new Part { Name = name, Positions = pos.ToArray(), Indices = idx.ToArray() };
    }

    // `pairs` plain strips (x 0..1, facing +Z) each abutted at x = 0 by a mirrored strip (scale -1 on X, so world x -1..0).
    // preFlipped[i] true = the file stores that mirrored strip ALREADY facing +Z after the mirror (the frigate's case: the
    // glTF reversal then turns it to -Z); false = stored the standard way (the reversal is what brings it to +Z).
    static Part[] MirrorPairs(params bool[] preFlipped)
    {
        var parts = new List<Part>();
        for (int i = 0; i < preFlipped.Length; i++)
        {
            parts.Add(Strip("P" + i, 0, 1, 10 * i, inward: false));
            var m = Strip("M" + i, 0, 1, 10 * i, inward: preFlipped[i]); m.Scale = new double[] { -1, 1, 1 };
            parts.Add(m);
        }
        return parts.ToArray();
    }

    static int[] AllNodes(int n) => Enumerable.Range(0, n).ToArray();

    static List<double[]> LoneFaces(byte[] fused)
    {
        var g = Read(fused); var prim = (JObject)g.Primitives(g.Node("P0_Fused"))[0];
        float[] p = g.Floats(((JObject)prim["attributes"]).Value<int>("POSITION"), 3);
        uint[] ix = g.Indices(prim.Value<int>("indices"));
        var all = FaceNormals(g, prim); var r = new List<double[]>();
        for (int t = 0; t < ix.Length; t += 3) if (p[ix[t] * 3 + 2] > 39f) r.Add(all[t / 3]);
        Assert.Equal(6, r.Count);   // the lone strip's six triangles
        return r;
    }

    [Fact]
    public void A_file_that_stores_mirrored_parts_pre_flipped_is_fixed_only_with_the_option()
    {
        var parts = MirrorPairs(true, true, true);
        byte[] glb = BuildGlb(parts);
        var off = GlbDisconnectedParts.FuseNodes(glb, AllNodes(parts.Length), 0.0, null, false);
        Assert.Contains(off.Warnings, w => w.Contains("Check mirrored parts") && w.Contains("3 of 3"));   // off: it says so, changes nothing
        Assert.DoesNotContain(off.Details, d => d.StartsWith("mirrored parts", StringComparison.Ordinal));
        // (These simple pairs are cleanly orientable, so the consistency stage repairs them even with the option off.
        // The frigate broke because its hull island also held a structure that cannot be oriented, and the fuse then
        // keeps the whole island as it stands. The case the fuse cannot repair alone is reproduced in the lone-strip
        // test below; the real hull is covered by the drill: 94.8 % back-facing off, 0.0 % on.)

        var on = GlbDisconnectedParts.FuseNodes(glb, AllNodes(parts.Length), 0.0, null, true);
        Assert.Contains(on.Details, d => d.StartsWith("mirrored parts", StringComparison.Ordinal) && d.Contains("3 of 3 judged") && d.Contains("undone for 3"));
        var g = Read(on.Bytes);
        var normals = FaceNormals(g, (JObject)g.Primitives(g.Node("P0_Fused"))[0]);
        Assert.Equal(36, normals.Count);                                 // 6 strips x 6 triangles
        Assert.All(normals, n => Assert.True(n[2] > 0, "every face +Z, the mirrored halves included"));
    }

    [Fact]
    public void A_file_stored_the_standard_way_is_left_exactly_as_before()
    {
        // The Teutonic's case: the glTF reversal is RIGHT. The check must confirm it and change nothing, byte for byte.
        var parts = MirrorPairs(false, false, false);
        byte[] glb = BuildGlb(parts);
        var off = GlbDisconnectedParts.FuseNodes(glb, AllNodes(parts.Length), 0.0, null, false);
        var on = GlbDisconnectedParts.FuseNodes(glb, AllNodes(parts.Length), 0.0, null, true);
        Assert.Equal(off.Bytes, on.Bytes);
        Assert.Contains(on.Details, d => d.StartsWith("mirrored parts", StringComparison.Ordinal) && d.Contains("confirm the glTF reversal"));
        Assert.DoesNotContain(off.Warnings, w => w.Contains("Check mirrored parts"));
    }

    [Fact]
    public void Mixed_evidence_changes_nothing()
    {
        // The Romanic's group H judged 1 pre-flipped against 2 standard, and acting on the one made the group worse
        // (2,667 -> 2,955 back-facing cells). One pre-flipped of three is no convention: output identical, no warning.
        var parts = MirrorPairs(true, false, false);
        byte[] glb = BuildGlb(parts);
        var off = GlbDisconnectedParts.FuseNodes(glb, AllNodes(parts.Length), 0.0, null, false);
        var on = GlbDisconnectedParts.FuseNodes(glb, AllNodes(parts.Length), 0.0, null, true);
        Assert.Equal(off.Bytes, on.Bytes);
        Assert.Contains(on.Details, d => d.Contains("not enough agreement to act on"));
        Assert.DoesNotContain(off.Warnings, w => w.Contains("Check mirrored parts"));
    }

    [Fact]
    public void Mirrored_lap_strips_lying_on_their_plates_are_not_mistaken_for_inside_out()
    {
        // Review of PR #67 (76de8e3): a lap strip lies ON its plate, stitched along one edge and facing the same way. Both
        // faces then walk the shared edge the SAME way, legitimately — the consistency pass's lap rule knows this (the
        // Teutonic's Object_8, 671 strips). The first mirrored-part check counted it as a conflict, gave the group a
        // unanimous "already facing outward" verdict, and flipped all 18 lap faces inward.
        var parts = new List<Part>();
        for (int i = 0; i < 3; i++)
        {
            parts.Add(Strip("P" + i, 0, 1, 10 * i, inward: false));                      // the plate, x 0..1, +Z
            var lap = Strip("L" + i, -0.5f, 0, 10 * i, inward: false);                   // local x -0.5..0 -> world 0..0.5: ON the plate
            lap.Scale = new double[] { -1, 1, 1 };                                        // mirrored, stored the standard way: +Z after the glTF reversal
            parts.Add(lap);
        }
        byte[] glb = BuildGlb(parts.ToArray());
        var off = GlbDisconnectedParts.FuseNodes(glb, AllNodes(parts.Count), 0.0, null, false);
        var goff = Read(off.Bytes);
        Assert.All(FaceNormals(goff, (JObject)goff.Primitives(goff.Node("P0_Fused"))[0]), n => Assert.True(n[2] > 0, "off: every face +Z (premise)"));
        var on = GlbDisconnectedParts.FuseNodes(glb, AllNodes(parts.Count), 0.0, null, true);
        Assert.Equal(off.Bytes, on.Bytes);                                               // nothing to change, nothing changed
        Assert.DoesNotContain(on.Details, d => d.Contains("already stores them facing outward"));
        Assert.DoesNotContain(off.Warnings, w => w.Contains("Check mirrored parts"));
    }

    [Fact]
    public void A_mirrored_part_with_no_plain_neighbour_follows_the_files_convention()
    {
        // The frigate's Object_961: no plain neighbour to judge it by, and it kept the reversal, carrying 2,443 of the
        // 2,462 back-facing cells left on the hull. Three judged parts agreeing make the convention; it follows.
        var parts = MirrorPairs(true, true, true).ToList();
        var lone = Strip("Lone", 0, 1, 40, inward: true); lone.Scale = new double[] { -1, 1, 1 };   // pre-flipped, touches nothing
        parts.Add(lone);
        byte[] glb = BuildGlb(parts.ToArray());
        // off: a flat strip on its own gives the direction stage nothing to judge, so it keeps the glTF reversal: -Z,
        // see-through from above. This is the bug, reproduced where the fuse cannot repair it by itself.
        var off = GlbDisconnectedParts.FuseNodes(glb, AllNodes(parts.Count), 0.0, null, false);
        Assert.All(LoneFaces(off.Bytes), n => Assert.True(n[2] < 0, "off: the lone strip is inside-out"));
        var on = GlbDisconnectedParts.FuseNodes(glb, AllNodes(parts.Count), 0.0, null, true);
        Assert.Contains(on.Details, d => d.Contains("undone for 4") && d.Contains("1 of them had no plain neighbour"));
        Assert.All(LoneFaces(on.Bytes), n => Assert.True(n[2] > 0, "on: the lone strip faces +Z"));
        var g = Read(on.Bytes);
        Assert.All(FaceNormals(g, (JObject)g.Primitives(g.Node("P0_Fused"))[0]), n => Assert.True(n[2] > 0, "and every other face too"));
    }

    // ---- level sheets: a deck faces up (2026-09-20, the Confederate frigate's gun deck) ----
    // A horizontal quad at height y spanning x0..x1 by z0..z1; `down` winds it so the normal points -Y.
    static Part Level(string name, float x0, float x1, float z0, float z1, float y, bool down)
    {
        var p = new Part { Name = name, Positions = new[] { x0, y, z0,  x1, y, z0,  x1, y, z1,  x0, y, z1 } };
        p.Indices = down ? new[] { 0, 1, 2,  0, 2, 3 } : new[] { 0, 2, 1,  0, 3, 2 };
        return p;
    }

    // A rig whose SAILS own the model's height and area, which is what puts the belly line above the decks: the fuse
    // takes the belly a quarter of the way up the model, here about +6, while the deck sits at +0.5 — the frigate's
    // case exactly (belly 2.59, deck islands at 0.61, 2.06 and 2.53).
    static Part[] ShipWithSails(Part deck) => new[]
    {
        deck,
        Level("Bottom", 0, 20, 0, 6, -5f, down: true),        // bottom plating, honestly facing down, at the hull's floor
        Quad("Sail", 0, 20, 5, 40, 0),                        // a tall sail: most of the model's height and area
    };

    static List<double[]> FusedNormals(GlbDisconnectedParts.Result r, string node)
    {
        var g = Read(r.Bytes);
        return FaceNormals(g, (JObject)g.Primitives(g.Node(node))[0]);
    }

    [Fact]
    public void A_deck_below_the_belly_line_is_left_facing_up()
    {
        // The gun deck: level, facing up, and BELOW the belly line, so the radial score reads it as pointing inward
        // (-0.67 on the frigate) and the fuse reversed it whole — 88 % of the deck see-through from above, 94 % for
        // the group. A level sheet is not asked the radial question: above the hull's floor it is a deck, facing up.
        var deck = Level("Deck", 2, 18, 1, 5, 0.5f, down: false);
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(ShipWithSails(deck)), new[] { 0 }, 0.0);
        Assert.Contains(r.Details, d => d.StartsWith("frame:", StringComparison.Ordinal));
        Assert.All(FusedNormals(r, "Deck_Fused"), n => Assert.True(n[1] > 0, "the deck still faces up"));
        Assert.Equal(0, r.FacesRewound);
    }

    // A deck with a RAISED EDGE: a floor at y with a rim of height `rim` all round, every face wound toward the tray's
    // own centre - the floor up, the rim inward - which is how a deck with bulwarks, a hatch coaming or a boat's
    // interior is seen from above. Oriented by construction (each quad turned to face the centre), so the fixture
    // cannot be hand-wound wrong.
    static Part Tray(string name, float x0, float x1, float z0, float z1, float y, float rim)
    {
        var P = new List<float>(); void V(float x, float yy, float z) { P.Add(x); P.Add(yy); P.Add(z); }
        V(x0, y, z0); V(x1, y, z0); V(x1, y, z1); V(x0, y, z1);                       // 0..3 floor
        V(x0, y + rim, z0); V(x1, y + rim, z0); V(x1, y + rim, z1); V(x0, y + rim, z1); // 4..7 rim top
        double cx = (x0 + x1) / 2.0, cy = y + rim / 2.0, cz = (z0 + z1) / 2.0;
        var I = new List<int>();
        void Q(int a, int b, int c, int d)
        {
            double ax = P[3*a], ay = P[3*a+1], az = P[3*a+2];
            double ux = P[3*b]-ax, uy = P[3*b+1]-ay, uz = P[3*b+2]-az, vx = P[3*c]-ax, vy = P[3*c+1]-ay, vz = P[3*c+2]-az;
            double nx = uy*vz - uz*vy, ny = uz*vx - ux*vz, nz = ux*vy - uy*vx;            // normal of (a,b,c)
            bool towardCentre = nx*(cx-ax) + ny*(cy-ay) + nz*(cz-az) > 0;
            if (towardCentre) I.AddRange(new[] { a, b, c, a, c, d }); else I.AddRange(new[] { a, c, b, a, d, c });
        }
        Q(0, 1, 2, 3);                                                   // floor
        Q(0, 1, 5, 4); Q(1, 2, 6, 5); Q(2, 3, 7, 6); Q(3, 0, 4, 7);     // rim walls
        return new Part { Name = name, Positions = P.ToArray(), Indices = I.ToArray() };
    }

    [Fact]
    public void A_deck_with_a_raised_edge_is_not_turned_over_by_its_own_volume()
    {
        // SMS Wespe, 2026-09-22: the fuse turned a quarter of the gun deck's faces over, and the Lab showed the hull's
        // insides through the deck. Its deck sheets carry rims - bulwark and coaming faces - so each is a shallow tray
        // whose floor faces up toward its own centroid: a consistent NEGATIVE signed volume, the shape of a bowl seen
        // from inside, and above the thickness gate ("volume agreement -1.00 thickness -0.0533, inside-out score
        // +0.51: reversed whole"). The deck exemption existed for exactly this sheet and was never consulted, because
        // the confident-volume rule ran first. A level sheet already facing up is a deck, whatever its volume says.
        // ABOVE the belly line (ShipWithSails puts it near +6): a raised gun platform, as on the Wespe
        var deck = Tray("Deck", 2, 18, 1, 5, 10f, 1f);
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(ShipWithSails(deck)), new[] { 0 }, 0.0);
        // the fixture reached the volume rule: a confident, negative volume on an open sheet ...
        Assert.Contains(r.Details, d => d.Contains("open, volume agreement -") && d.Contains("thickness -0.0"));
        // ... and it was KEPT, floor still up
        var normals = FusedNormals(r, "Deck_Fused");
        Assert.All(normals.Where(n => Math.Abs(n[1]) > 0.5), n => Assert.True(n[1] > 0, "the deck floor still faces up"));
        Assert.Equal(0, r.FacesRewound);
    }

    [Fact]
    public void The_same_tray_at_the_floor_is_an_inside_out_hull_and_is_still_turned()
    {
        // The boundary of the exemption above. A bowl seen from inside and an open hull wound inside out are the same
        // surface; what separates them is height. At the model's floor, below the belly line, this is a hull whose
        // plating faces INTO the ship, and the volume verdict must still turn it.
        var hull = Tray("Hull", 0, 20, 0, 6, -5f, 2f);
        var sail = Quad("Sail", 0, 20, 5, 40, 0);
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(hull, sail), new[] { 0 }, 0.0);
        Assert.Contains(r.Details, d => d.Contains("open, volume agreement -") && d.Contains("reversed whole"));
        Assert.All(FusedNormals(r, "Hull_Fused").Where(n => Math.Abs(n[1]) > 0.5), n => Assert.True(n[1] < 0, "the hull bottom now faces down"));
    }

    [Fact]
    public void An_inverted_shallow_hull_with_a_low_rim_is_still_turned()
    {
        // PR #80 review, P2: the deck exemption judged height by the whole sheet's mean, which a rim lifts - so an
        // inward-wound 20 x 6 hull with a 0.5 rim and nothing else in the model averaged to just above the floor,
        // read as a level deck (its floor is 82 % of its area), and kept its plating pointing into the ship. The
        // height that matters is the FLOOR's: those faces sit at the model's floor, and a hull's own bottom can never
        // be above it.
        var hull = Tray("Hull", 0, 20, 0, 6, -5f, 0.5f);
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(hull), new[] { 0 }, 0.0);
        Assert.Contains(r.Details, d => d.Contains("open, volume agreement -") && d.Contains("reversed whole"));
        Assert.All(FusedNormals(r, "Hull_Fused").Where(n => Math.Abs(n[1]) > 0.5), n => Assert.True(n[1] < 0, "the hull bottom now faces down"));
    }

    // A V-BOTTOM hull seen from inside: a keel line at keelY along the length, two panels sloping up to edgeY at the
    // sides, a vertical rim to rimY. Every face wound toward the hull's own centre, like Tray.
    static Part VHull(string name, float x0, float x1, float z0, float z1, float keelY, float edgeY, float rimY)
    {
        var P = new List<float>(); void V(float x, float y, float z) { P.Add(x); P.Add(y); P.Add(z); }
        float zm = (z0 + z1) / 2f;
        V(x0, keelY, zm); V(x1, keelY, zm);                       // 0,1 keel
        V(x0, edgeY, z0); V(x1, edgeY, z0); V(x1, edgeY, z1); V(x0, edgeY, z1);   // 2..5 bottom edges
        V(x0, rimY, z0); V(x1, rimY, z0); V(x1, rimY, z1); V(x0, rimY, z1);       // 6..9 rim top
        double cx = (x0 + x1) / 2.0, cy = (keelY + rimY) / 2.0, cz = zm;
        var I = new List<int>();
        void Q(int a, int b, int c, int d)
        {
            double ax = P[3*a], ay = P[3*a+1], az = P[3*a+2];
            double ux = P[3*b]-ax, uy = P[3*b+1]-ay, uz = P[3*b+2]-az, vx = P[3*c]-ax, vy = P[3*c+1]-ay, vz = P[3*c+2]-az;
            double nx = uy*vz - uz*vy, ny = uz*vx - ux*vz, nz = ux*vy - uy*vx;
            bool toward = nx*(cx-ax) + ny*(cy-ay) + nz*(cz-az) > 0;
            if (toward) I.AddRange(new[] { a, b, c, a, c, d }); else I.AddRange(new[] { a, c, b, a, d, c });
        }
        Q(0, 1, 3, 2); Q(0, 5, 4, 1);                            // the two sloping bottom panels
        Q(2, 3, 7, 6); Q(3, 4, 8, 7); Q(4, 5, 9, 8); Q(5, 2, 6, 9); // rim walls
        return new Part { Name = name, Positions = P.ToArray(), Indices = I.ToArray() };
    }

    [Fact]
    public void An_inverted_V_bottom_hull_is_still_turned()
    {
        // PR #80 review, second P2: the floor faces' MEAN height cleared the floor for a V-bottom whose panels slope
        // up from the keel - keel 0, bottom edges 0.3, rim 0.8 - and the hull kept its plating pointing inward. What a
        // hull floor does that a deck never does is reach the model's floor: the evidence is the lowest vertex of the
        // up-facing faces, and this one's is the keel.
        var hull = VHull("Hull", 0, 20, 0, 6, 0f, 0.3f, 0.8f);
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(hull), new[] { 0 }, 0.0);
        Assert.Contains(r.Details, d => d.Contains("open, volume agreement -") && d.Contains("reversed whole"));
        Assert.All(FusedNormals(r, "Hull_Fused").Where(n => Math.Abs(n[1]) > 0.5), n => Assert.True(n[1] < 0, "the hull bottom now faces down"));
    }

    // ---- parity repair: a face's colour is the one most of its edges support (2026-09-23, SMS Wespe) ----
    [Fact]
    public void A_bridge_face_reached_through_a_reversed_patch_takes_its_neighbours_colour()
    {
        // Faces 0,1 = the correct majority (colour 1); 2 = a reversed patch face (colour 0, joined to the majority by a
        // same-way edge, as a reversed patch is); 3 = the BRIDGE, consistent ("opp") with all three, reached first
        // through the patch and so coloured 0. Its two majority edges are unsatisfied, its patch edge satisfied.
        var parity = new[] { 1, 1, 0, 0 };
        var edges = new List<(int a, int b, bool same)> { (0, 1, false), (2, 0, true), (3, 0, false), (3, 1, false), (3, 2, false) };
        int n = GlbDisconnectedParts.RepairParity(parity, new[] { 0, 1, 2, 3 }, edges);
        Assert.Equal(1, n);
        Assert.Equal(new[] { 1, 1, 0, 1 }, parity);   // the bridge joined the majority; the patch is still the patch
    }

    static int Unsatisfied(int[] parity, IList<(int a, int b, bool same)> edges)
        => edges.Count(e => (parity[e.a] ^ parity[e.b]) != (e.same ? 1 : 0));

    [Fact]
    public void Parity_repair_leaves_a_consistent_sheet_alone_and_only_ever_lowers_the_unsatisfied_count()
    {
        var ok = new[] { 0, 1, 0, 1 };
        Assert.Equal(0, GlbDisconnectedParts.RepairParity(ok, new[] { 0, 1, 2, 3 }, new List<(int, int, bool)> { (0, 1, true), (1, 2, true), (2, 3, true) }));
        Assert.Equal(new[] { 0, 1, 0, 1 }, ok);
        // THE PROPERTY (PR #81 review): every recolouring converts a face's unsatisfied edges to satisfied and its
        // fewer satisfied ones to unsatisfied, so the sheet's count strictly falls with each step - it cannot cycle,
        // the round cap is never what stops it, and the end state has no face with more unsatisfied edges than
        // satisfied. An odd cycle of same-way edges (a Mobius band in three faces) can never reach zero.
        var m = new[] { 0, 1, 0 };
        var cyc = new List<(int a, int b, bool same)> { (0, 1, true), (1, 2, true), (2, 0, true) };
        int before = Unsatisfied(m, cyc);
        int n = GlbDisconnectedParts.RepairParity(m, new[] { 0, 1, 2 }, cyc);
        int after = Unsatisfied(m, cyc);
        Assert.True(after <= before, "the count never rises");
        Assert.True(after >= 1, "an odd cycle cannot be satisfied");
        foreach (int f in new[] { 0, 1, 2 })
        {
            int sat = cyc.Count(e => (e.a == f || e.b == f) && (m[e.a] ^ m[e.b]) == (e.same ? 1 : 0));
            int unsat = cyc.Count(e => (e.a == f || e.b == f) && (m[e.a] ^ m[e.b]) != (e.same ? 1 : 0));
            Assert.True(unsat <= sat, "no face is left outvoted by its own edges");
        }
        Assert.True(n <= before, "at most one recolouring per unit of count removed");
    }


    [Fact]
    public void A_correction_that_travels_against_the_face_order_is_carried_to_the_end()
    {
        // PR #81 review, P2: a flip is seen by later faces in the same round and by earlier faces only in the next, so
        // a cascade running backwards through the order costs one round per step, and a fixed 16-round cap left a
        // 565-face sheet unfinished. The cascade, built to order: a path 0..N where every face also holds one leaf edge
        // it cannot satisfy alone, and the LAST face holds two. Only N is outvoted at first; its flip unsettles N-1,
        // whose flip unsettles N-2 ... and faces are visited 0..N, so each step waits a round. N+1 rounds in all.
        const int N = 40;
        var faces = Enumerable.Range(0, N + 1).ToArray();
        var parity = new int[2 * (N + 1) + 1];                       // faces 0..N, then their leaves (not in `faces`: never recoloured)
        var edges = new List<(int a, int b, bool same)>();
        for (int k = 0; k < N; k++) edges.Add((k, k + 1, false));   // the path: satisfied while parities agree (all 0)
        for (int k = 0; k <= N; k++) edges.Add((k, N + 1 + k, true));   // one leaf each: same-way, parities equal -> unsatisfied
        edges.Add((N, 2 * N + 2, true));                             // the last face's second leaf: it alone is outvoted
        int unsat(int[] pr) => edges.Count(e => (pr[e.a] ^ pr[e.b]) != (e.same ? 1 : 0));
        var capped = (int[])parity.Clone();
        GlbDisconnectedParts.RepairParity(capped, faces, edges, 16);
        Assert.True(unsat(capped) > 0, "with 16 rounds the cascade is cut short (the reviewer's case)");
        var full = (int[])parity.Clone();
        int n = GlbDisconnectedParts.RepairParity(full, faces, edges);
        Assert.Equal(0, unsat(full));                               // run to the end, every edge is satisfied
        Assert.Equal(N + 1, n);                                     // one recolouring per face, no more
        Assert.All(faces, f => Assert.Equal(1, full[f]));
    }

    // ---- double-sided by duplication (2026-09-24, SMS Wespe's gun) ----
    static List<HashSet<uint>> OutputFaceSets(byte[] glb, string node)
    {
        var g = Read(glb); var sets = new List<HashSet<uint>>();
        foreach (JObject prim in g.Primitives(g.Node(node)))
        {
            var idx = g.Indices((int)prim["indices"]);
            for (int t = 0; t + 2 < idx.Length; t += 3) sets.Add(new HashSet<uint> { idx[t], idx[t + 1], idx[t + 2] });
        }
        return sets;
    }

    [Fact]
    public void A_part_doubled_for_two_sidedness_is_fused_as_two_copies_not_one_soup()
    {
        // The gun: every face twice, wound both ways, each copy with its own vertices. Welded together the copies made
        // every edge a four-face edge: no partners, every face its own sheet, each judged alone - and then both copies
        // landed on one output index triple, which Blender's importer collapses to one at random. Kept apart, each copy
        // is one sheet judged whole, and the output carries both on their own vertices.
        var up = Quad("Plate", 0, 4, 0, 4, 1);                   // a 4 x 4 plate at z = 1 facing +Z ...
        var down = Quad("Plate", 0, 4, 0, 4, 1, inward: true);   // ... and the same plate again, wound the other way, its own 4 vertices
        var hull = Box("Hull", 6, -1, -1, -3, inward: false);    // something for the belly axis
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(hull, up, down), new[] { 1, 2 }, 0.0);
        Assert.Equal(4, r.OutputTriangles);
        Assert.StartsWith("Fused ", r.Details[0], StringComparison.Ordinal);   // the Workshop shows Details[0]: the summary, never a diagnostic (review of PR #82, P3)
        Assert.Contains(r.Details, d => d.StartsWith("twins: 2 face(s)", StringComparison.Ordinal));
        // two sheets of two faces - not four sheets of one
        Assert.Equal(2, r.IslandLines.Count(l => l.TrimStart().StartsWith("2 faces", StringComparison.Ordinal)));
        Assert.DoesNotContain(r.IslandLines, l => l.TrimStart().StartsWith("1 faces", StringComparison.Ordinal));
        // both copies keep their AUTHORED winding: two faces up, two down, whatever the direction rules thought of a
        // flat sheet at this height - an authored opposite pair is already two-sided (the Wespe's reinforce ring)
        var normals = FusedNormals(r, "Plate_Fused");
        Assert.Equal(2, normals.Count(n => n[2] > 0.9)); Assert.Equal(2, normals.Count(n => n[2] < -0.9));
        // the counts describe the OUTPUT (review of PR #82, P3): nothing is turned, so nothing is reported turned
        Assert.Equal(0, r.FacesRewound);
        Assert.Contains(r.Details, d => d.StartsWith("rewound by part: Plate 0/2; Plate 0/2", StringComparison.Ordinal));
        // and no two output faces share a vertex set: nothing for an importer to drop
        var sets = OutputFaceSets(r.Bytes, "Plate_Fused");
        for (int i = 0; i < sets.Count; i++) for (int j = i + 1; j < sets.Count; j++) Assert.False(sets[i].SetEquals(sets[j]));
        // a plate that is NOT doubled is untouched
        Assert.DoesNotContain(GlbDisconnectedParts.FuseNodes(BuildGlb(hull, up), new[] { 1 }, 0.0).Details, d => d.StartsWith("twins:", StringComparison.Ordinal));
        // and a same-way duplicate (a z-fighting copy) is not a twin: welded as before, no line
        var again = Quad("Plate", 0, 4, 0, 4, 1);
        Assert.DoesNotContain(GlbDisconnectedParts.FuseNodes(BuildGlb(hull, up, again), new[] { 1, 2 }, 0.0).Details, d => d.StartsWith("twins:", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_skins_within_the_weld_radius_are_a_seam_to_weld_not_a_doubled_face()
    {
        // Review of PR #82, P2: the twin test compared welded CLASSES, so any opposite-wound faces within the weld
        // radius passed as twins - a thin plate's two skins 0.05 apart under a wider weld were un-welded and exempted
        // from the winding rules, defeating the weld that was asked for. A twin must COINCIDE.
        var top = Quad("Plate", 0, 4, 0, 4, 1.05f, inward: true);   // two skins 0.05 apart, facing EACH OTHER (into the material)
        var bottom = Quad("Plate", 0, 4, 0, 4, 1f);
        var hull = Box("Hull", 6, -1, -1, -3, inward: false);
        // weld fraction 0.02 of the model's length (~10) is ~0.2: wider than the 0.05 gap, so the skins share every class
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(hull, top, bottom), new[] { 1, 2 }, 0.02);
        Assert.DoesNotContain(r.Details, d => d.StartsWith("twins:", StringComparison.Ordinal));
        Assert.Equal(1, r.IslandsAfter);                        // the weld did its job: one welded island, judged by the ordinary rules
    }

    [Fact]
    public void A_nearby_face_in_front_does_not_hide_a_doubled_pair_behind_it()
    {
        // Second review of PR #82, P2: within a welded-class group every face was compared only with the group's FIRST
        // face. Put a nearby, non-coincident plate first and the coincident opposite pair behind it was never
        // recognised: three quads, 12 vertices welded to 4, one copy rewound, duplicate faces left for the importer.
        var near = Quad("Plate", 0, 4, 0, 4, 1.05f);                // in front, within the weld, NOT coincident
        var up = Quad("Plate", 0, 4, 0, 4, 1f);                     // the doubled pair, coincident, wound both ways
        var down = Quad("Plate", 0, 4, 0, 4, 1f, inward: true);
        var hull = Box("Hull", 6, -1, -1, -3, inward: false);
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(hull, near, up, down), new[] { 1, 2, 3 }, 0.02);
        Assert.Contains(r.Details, d => d.StartsWith("twins: 2 face(s)", StringComparison.Ordinal));
        var sets = OutputFaceSets(r.Bytes, "Plate_Fused"); var normals = FusedNormals(r, "Plate_Fused");
        Assert.Equal(6, sets.Count);
        // The near plate and the pair's up copy are 0.05 apart under a 0.2 weld: the weld collapses them onto one face,
        // the same way round, and an importer dropping one of THOSE loses nothing. What must never happen is two faces
        // on one vertex set wound OPPOSITE ways - that is the pair collapsed, one side lost.
        for (int i = 0; i < sets.Count; i++) for (int j = i + 1; j < sets.Count; j++)
            if (sets[i].SetEquals(sets[j])) Assert.True(normals[i][2] * normals[j][2] > 0, "a doubled pair collapsed onto one vertex set: one side lost");
        Assert.True(normals.Count(n => n[2] < -0.9) >= 2, "the down copy of the pair is still down");
    }

    [Fact]
    public void A_level_plate_authored_facing_down_is_left_to_the_evidence()
    {
        // The rule is one-sided on purpose. The frigate carries two 11.4 x 5.2 zero-thickness plates over its boat
        // deck, authored facing DOWN to be seen from below; turning every level sheet up draped them over the deck as
        // a blank sheet (user: "the flat blanket"). A down-facing level sheet keeps going through the evidence that
        // judged it before — here there is none either way, so it stays as authored.
        var plate = Level("Plate", 2, 18, 1, 5, 0.5f, down: true);
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(ShipWithSails(plate)), new[] { 0 }, 0.0);
        Assert.All(FusedNormals(r, "Plate_Fused"), n => Assert.True(n[1] < 0, "still facing down"));
    }

    [Fact]
    public void Bottom_plating_above_a_keel_keeps_facing_down()
    {
        // Review of PR #68 at e75a91a: floorY comes from the WHOLE model's height, so a keel hanging below the hull
        // pushes legitimate bottom plating above that line. With the first, two-sided rule ("every level sheet above
        // the floor is a deck") the plating was turned up and its underside vanished under back-face culling. The rule
        // is one-sided now — only sheets ALREADY facing up are left alone — so a down-facing bottom is judged by the
        // evidence that judged it before, and below the belly line that evidence says it is right as it is.
        var bottom = Level("Bottom", 0, 20, 0, 6, 0f, down: true);       // plating at y = 0 …
        var keel = Quad("Keel", 0, 20, -5, 0, 3f);                       // … with a keel hanging to y = -5
        var sail = Quad("Sail", 0, 20, 5, 40, 0);                        // … and a rig up to y = 40
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(bottom, keel, sail), new[] { 0 }, 0.0);
        Assert.All(FusedNormals(r, "Bottom_Fused"), n => Assert.True(n[1] < 0, "the hull bottom still faces down"));
        Assert.Equal(0, r.FacesRewound);
    }

    [Fact]
    public void Bottom_plating_at_the_hull_floor_keeps_facing_down()
    {
        // The rule must not turn every level sheet upward: at the hull's floor, facing down is right. The bottom plate
        // is node 1 of the fixture, below the floor line, so the radial score decides it as before.
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(ShipWithSails(Level("Deck", 2, 18, 1, 5, 0.5f, down: false))), new[] { 1 }, 0.0);
        Assert.All(FusedNormals(r, "Bottom_Fused"), n => Assert.True(n[1] < 0, "the hull bottom still faces down"));
        Assert.Equal(0, r.FacesRewound);
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
        Assert.Contains("unsatisfied after at Band~Band (2-face edge)", res.Details[1]);   // …and WHERE it fails, by part pair (2026-09-18)
        Assert.Equal(0, res.FacesRewound);
        // PR #81 review: the parity REPAIR lowers a sheet's unsatisfied count, and a refused sheet must be refused on
        // the walk's own count — never argued under the threshold by a repair and then majority-flipped. So a refused
        // sheet is not repaired at all: nothing recoloured, exactly as authored.
        Assert.DoesNotContain("recoloured", res.Details[1]);
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
    public void A_double_skinned_solid_is_judged_by_which_side_its_other_skin_lies_on()
    {
        // 2026-09-17, the SS Romanic: a hull built as two skins 2 cm apart with opposite normals, wound inside-out as a
        // whole — the volume of the two skins cancels (agreement ~0, thickness ~0) and so does the radial score. The
        // rule that does not cancel: material lies BEHIND an outward face. Outer box 10 m, inner box 2 cm inside it.
        // the rule's domain: THIN sheets, where the volume is inconclusive. Two 10 x 10 skins 2 cm apart above a hull
        // box (the frame), each a flat sheet (volume 0) — wound to face EACH OTHER (into the material): both must turn
        var hull = Box("Hull", 6, 0, 0, 0, inward: false);
        var top = new Part { Name = "Top", Positions = new float[] { 0, 8.02f, 0,  10, 8.02f, 0,  10, 8.02f, 10,  0, 8.02f, 10 }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };   // (b-a)x(c-a) = (10,0,0)x(10,0,10) = (0,-100,0): DOWN, toward the bottom skin
        var bottom = new Part { Name = "Bottom", Positions = new float[] { 0, 8, 0,  10, 8, 0,  10, 8, 10,  0, 8, 10 }, Indices = new[] { 0, 2, 1, 0, 3, 2 } };            // UP, toward the top skin
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(hull, top, bottom), new[] { 1, 2 }, 0.0);
        Assert.Equal(2, r.IslandsAfter);
        Assert.True(r.FacesRewound == 4, "both skins must turn — " + r.Details[1]);
        Assert.Contains("double skin 100% twinned", r.Details[1]);
        // the same pair wound AWAY from each other (material between them, behind each face) is kept
        var topOk = new Part { Name = "Top", Positions = top.Positions, Indices = bottom.Indices };
        var bottomOk = new Part { Name = "Bottom", Positions = bottom.Positions, Indices = top.Indices };
        var ok = GlbDisconnectedParts.FuseNodes(BuildGlb(hull, topOk, bottomOk), new[] { 1, 2 }, 0.0);
        Assert.Equal(0, ok.FacesRewound);
        // A NESTED CAVITY SHELL (review of e595844): an outward outer box and an inward inner box 2 cm inside it — a
        // hollow solid, correct as authored. The inner shell's volume is negative, but every twin lies BEHIND it: that
        // vetoes the reversal (it already faces away from the material), while it could never cause one
        var hollow = GlbDisconnectedParts.FuseNodes(BuildGlb(Box("Outer", 10, 0, 0, 0, inward: false), Box("Inner", 9.96f, 0.02f, 0.02f, 0.02f, inward: true)), new[] { 0, 1 }, 0.0);
        Assert.Equal(0, hollow.FacesRewound);
        // NEARBY SEPARATE SOLIDS (review of 9cacd9f): four outward cubes 5 mm apart — from inside a gap, air looks like
        // a skin of material; each cube is closed with a confident volume, and that verdict wins over the twin evidence
        var cubes = new[] { Box("C0", 1, 0, 0, 0, inward: false), Box("C1", 1, 1.005f, 0, 0, inward: false), Box("C2", 1, 0, 0, 1.005f, inward: false), Box("C3", 1, -1.005f, 0, 0, inward: false) };
        var gaps = GlbDisconnectedParts.FuseNodes(BuildGlb(cubes), new[] { 0, 1, 2, 3 }, 0.0);
        Assert.Equal(0, gaps.FacesRewound);
        // …and the same four cubes wound INWARD are all corrected (review of a043f8e): the central one has twins behind
        // 6 of its 12 faces only — not enclosed, so no veto — and its negative volume turns it like the other three
        var cubesIn = new[] { Box("C0", 1, 0, 0, 0, inward: true), Box("C1", 1, 1.005f, 0, 0, inward: true), Box("C2", 1, 0, 0, 1.005f, inward: true), Box("C3", 1, -1.005f, 0, 0, inward: true) };
        var gapsIn = GlbDisconnectedParts.FuseNodes(BuildGlb(cubesIn), new[] { 0, 1, 2, 3 }, 0.0);
        Assert.Equal(48, gapsIn.FacesRewound);
        // …and SIX inward cubes around a seventh (review of 7307fe2): the centre has a twin behind every face, but they
        // belong to six islands and none contains it — no enclosure, no veto, all 84 corrected
        var ring = new[] { Box("C", 1, 0, 0, 0, inward: true),
            Box("X+", 1, 1.005f, 0, 0, inward: true), Box("X-", 1, -1.005f, 0, 0, inward: true), Box("Y+", 1, 0, 1.005f, 0, inward: true),
            Box("Y-", 1, 0, -1.005f, 0, inward: true), Box("Z+", 1, 0, 0, 1.005f, inward: true), Box("Z-", 1, 0, 0, -1.005f, inward: true) };
        var ringIn = GlbDisconnectedParts.FuseNodes(BuildGlb(ring), Enumerable.Range(0, 7).ToArray(), 0.0);
        Assert.Equal(84, ringIn.FacesRewound);
    }

    [Fact]
    public void Stray_geometry_far_from_the_model_does_not_move_the_belly()
    {
        // the Romanic's anchor chain: 144 links 3 km away and 190 m up put the belly above the deck. Hull box y 0..6,
        // a deck at y 8 facing DOWN (must be reversed), and a stray part 3 km away and 190 m up that is NOT fused.
        var hull = Box("Hull", 6, 0, 0, 0, inward: false);
        var deck = new Part { Name = "Deck", Positions = new float[] { 1, 8, 1,  5, 8, 1,  5, 8, 5,  1, 8, 5 }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };   // DOWN
        var stray = Box("Stray", 3, 0, 190, 3000, inward: false);
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(hull, deck, stray), new[] { 1 }, 0.0);
        Assert.Equal(2, r.FacesRewound);
        Assert.Contains("1 open sheet(s) judged, 1 reversed", r.Details[0]);
        string frame = r.Details.First(d => d.StartsWith("frame: "));
        Assert.DoesNotContain("belly height 4", frame);   // ~1.5 (a quarter up 0..6), not ~46
    }

    [Fact]
    public void A_dense_cabin_does_not_outweigh_a_coarse_hull_in_the_belly()
    {
        // review of 9cacd9f: a coarse 10-unit hull (12 triangles) under a cabin and thirty small roof fittings carried enough
        // VERTICES upstairs that vertex-density filtering discarded the hull; the frame is area-weighted now
        var parts = new List<Part> { Box("Hull", 10, 0, 0, 0, inward: false) };                        // y 0..10, area 600
        parts.Add(Box("Cabin", 2, 4, 10, 4, inward: false));                                                  // on the deck
        for (int i = 0; i < 30; i++) parts.Add(Box("Fit" + i, 0.3f, 4.1f + (i % 6) * 0.3f, 12, 4.1f + (i / 6) * 0.3f, inward: false));   // 30 tiny boxes on the cabin roof: 360 tris, little area
        var deck = new Part { Name = "Deck", Positions = new float[] { 1, 10.01f, 1,  9, 10.01f, 1,  9, 10.01f, 9,  1, 10.01f, 9 }, Indices = new[] { 0, 2, 1, 0, 3, 2 } };   // a deck plate facing UP (wound so): must be KEPT
        parts.Add(deck);
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(parts.ToArray()), new[] { parts.Count - 1 }, 0.0);
        Assert.Equal(0, r.FacesRewound);
        string frame = r.Details.First(d => d.StartsWith("frame: "));
        var m = System.Text.RegularExpressions.Regex.Match(frame, @"belly height (-?[\d.]+)");
        double belly = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(belly > 1 && belly < 5, "belly must sit in the hull, not above the deck at 10 — " + frame);   // ~3: a quarter up 0..12
    }

    [Fact]
    public void A_coarse_tall_box_s_belly_sits_a_quarter_up_its_height_not_at_its_faces_centres()
    {
        // review of e595844: a 100 x 100 x 1 box is six pairs of huge triangles; sampled at their CENTRES the height
        // percentiles ran 33..67 and the belly landed at 42 — an upward platform at 35 then flipped. Corners now.
        var slab = Box("Slab", 1, 0, 0, 0, inward: false);
        slab.Positions = new float[] { 0, 0, 0,  100, 0, 0,  100, 100, 0,  0, 100, 0,  0, 0, 1,  100, 0, 1,  100, 100, 1,  0, 100, 1 };   // 100 x 100 x 1 (y up to 100)
        var platform = new Part { Name = "Platform", Positions = new float[] { 10, 35, 0.5f,  20, 35, 0.5f,  20, 35, 0.6f,  10, 35, 0.6f }, Indices = new[] { 0, 2, 1, 0, 3, 2 } };   // UP: (b-a)x(c-a) with the swapped order = +Y
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(slab, platform), new[] { 1 }, 0.0);
        Assert.Equal(0, r.FacesRewound);
        string frame = r.Details.First(d => d.StartsWith("frame: "));
        var m = System.Text.RegularExpressions.Regex.Match(frame, @"belly height (-?[\d.]+)");
        double belly = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(belly > 20 && belly < 30, "a quarter up 0..100 — " + frame);
    }

    [Fact]
    public void One_stray_unsatisfied_edge_does_not_refuse_a_whole_hull()
    {
        // 95 % of the same-way edges resolved is orientable enough: a plate seam (2-colourable) plus one T-fin that
        // leaves a single contradiction. The Romanic hull: 1 unsatisfied of 92 same-way in 59,000 edges.
        byte[] src = BuildGlb(Quad("A", 0, 1, 0, 1, 0), Quad("B", 1, 2, 0, 1, 0, inward: true));   // the plain seam: B inverted
        var r = GlbDisconnectedParts.FuseNodes(src, new[] { 0, 1 }, 0.001);
        Assert.Equal(2, r.FacesRewound);
        Assert.DoesNotContain("not orientable", r.Details[0]);
    }

    [Fact]
    public void The_path_guard_refuses_output_equal_to_source()
    {
        Assert.Throws<InvalidOperationException>(() => GlbDisconnectedParts.GuardPaths(@"C:\models\ship.glb", @"C:/models/SHIP.GLB"));
        GlbDisconnectedParts.GuardPaths(@"C:\models\ship.glb", @"C:\models\ship_fused.glb");
    }

    // ---- orientation SHEETS (2026-09-18, the SS Romanic's group D) ----

    // a plate hinged on the cube's top-front edge (a three-face junction) and extending forward at the cube's top height,
    // wound to face DOWN; its 4 corners are the cube's v6/v7 and two new ones
    static Part HingedPlate(string name, bool down)
    {
        var p = new Part { Name = name, Positions = new float[] { 0, 1, 1,  1, 1, 1,  1, 1, 2,  0, 1, 2 } };
        p.Indices = down ? new[] { 0, 1, 3,  1, 2, 3 } : new[] { 0, 3, 1,  1, 3, 2 };
        return p;
    }

    [Fact]
    public void A_branch_at_a_three_face_junction_is_judged_as_its_own_sheet()
    {
        // an outward cube with a downward plate hinged on one of its edges: cube + plate are ONE island (they share the
        // edge) but two sheets (the edge has three faces, so parity crosses it nowhere). Judged as one island the cube's
        // confident volume kept everything and the plate stayed inside out; per sheet the plate (open, no volume) goes to
        // the inside-out score against the belly and is reversed, the cube (open at that edge, confident volume) is kept
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(Box("Cube", 1, 0, 0, 0, inward: false), HingedPlate("Plate", down: true)), new[] { 0, 1 }, 0.0);
        Assert.Equal(1, r.IslandsAfter);
        Assert.Equal(2, r.FacesRewound);
        Assert.Contains("2 open sheet(s) judged, 1 reversed", r.Details[0]);
        Assert.Contains("rewound by part: Cube 0/12; Plate 2/2", r.Details.First(d => d.StartsWith("rewound by part")));
        // …and the same plate wound UP is left alone, the cube too
        var ok = GlbDisconnectedParts.FuseNodes(BuildGlb(Box("Cube", 1, 0, 0, 0, inward: false), HingedPlate("Plate", down: false)), new[] { 0, 1 }, 0.0);
        Assert.Equal(0, ok.FacesRewound);
        // the island line names the sheets, largest first, and each sheet's line carries its own verdict
        Assert.Equal(2, r.IslandLines.Count);
        Assert.StartsWith("12 faces (", r.IslandLines[0]);
        Assert.Contains("kept", r.IslandLines[0]);
        Assert.StartsWith("2 faces (", r.IslandLines[1]);
        Assert.Contains("reversed whole", r.IslandLines[1]);
    }

    [Fact]
    public void Authored_normals_take_the_sign_of_the_final_winding_not_the_flip_count()
    {
        // The Romanic's port side: a mirrored node whose spec front face points DOWN while its authored normals point UP.
        // The direction pass turns the deck up; "negate the normal when every face flipped" then pointed the normals DOWN
        // — the deck rendered lit from below. The normal keeps the artist's smoothing and takes the sign of the winding.
        // The frame needs a belly below the deck: a tall box elsewhere in the file (not in the group) supplies it.
        var deck = new Part { Name = "Deck", Positions = new float[] { 0, 0.9f, 0,  1, 0.9f, 0,  0, 0.9f, 1,  1, 0.9f, 1 },
            Indices = new[] { 0, 1, 2,  1, 3, 2 },   // local winding faces -Y; under the mirror the spec front face is -Y too (det < 0 flips it back)
            Normals = new float[] { 0, 1, 0,  0, 1, 0,  0, 1, 0,  0, 1, 0 }, Scale = new double[] { -1, 1, 1 } };
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(deck, Box("Hull", 1, 2, 0, 0, inward: false)), new[] { 0 }, 0.0);
        Assert.Equal(2, r.FacesRewound);   // the deck faced down above the belly: reversed
        var g = Read(r.Bytes);
        var prim = (JObject)g.Primitives(g.Node("Deck_Fused"))[0];
        float[] n = g.Floats(((JObject)prim["attributes"]).Value<int>("NORMAL"), 3);
        for (int i = 0; i < n.Length / 3; i++) Assert.True(n[i * 3 + 1] > 0.99f, "vertex " + i + " normal must point +Y with the final winding, got " + n[i * 3 + 1]);
        var fn = FaceNormals(g, prim);
        foreach (double[] f in fn) Assert.True(f[1] > 0, "the written winding faces +Y");
    }

    [Fact]
    public void Fusing_groups_in_parallel_writes_exactly_what_chaining_them_writes()
    {
        // two groups planned at once and applied in order must give the bytes that FuseNodes(FuseNodes(A)) gives
        var a1 = Quad("A1", 0, 1, 0, 1, 0); var a2 = Quad("A2", 1, 2, 0, 1, 0, inward: true);
        var b1 = Box("B1", 1, 5, 0, 0, inward: true); var b2 = Box("B2", 1, 5, 0, 1.5f, inward: false);
        byte[] src = BuildGlb(a1, a2, b1, b2);
        var chainA = GlbDisconnectedParts.FuseNodes(src, new[] { 0, 1 }, 0.001, "Fused_A_A1");
        var chainB = GlbDisconnectedParts.FuseNodes(chainA.Bytes, new[] { 2, 3 }, 0.001, "Fused_B_B1");
        byte[] both = GlbDisconnectedParts.FuseGroups(src, new List<GlbDisconnectedParts.FuseJob> {
            new GlbDisconnectedParts.FuseJob { NodeIndices = new[] { 0, 1 }, Name = "Fused_A_A1" }, new GlbDisconnectedParts.FuseJob { NodeIndices = new[] { 2, 3 }, Name = "Fused_B_B1" } }, 0.001, out var results, null);
        Assert.Equal(chainB.Bytes, both);
        Assert.Equal(2, results.Count);
        Assert.Equal(chainA.FacesRewound, results[0].FacesRewound); Assert.Equal(chainB.FacesRewound, results[1].FacesRewound);
        Assert.StartsWith("Fused 2 part(s) -> 'Fused_A_A1'", results[0].Details[0]);
        Assert.StartsWith("Fused 2 part(s) -> 'Fused_B_B1'", results[1].Details[0]);
        Assert.True(results[0].Changed && results[1].Changed);
        Assert.Null(results[0].Bytes);   // one output for all: the caller gets the bytes, not each result
        var gb = Read(both);   // each result names its shell by node index and name: the fused output's sidecar is written from these
        Assert.Equal("Fused_A_A1", results[0].FusedNodeName); Assert.Equal("Fused_A_A1", (string)gb.Root["nodes"][results[0].FusedNodeIndex]["name"]);
        Assert.Equal("Fused_B_B1", results[1].FusedNodeName); Assert.Equal("Fused_B_B1", (string)gb.Root["nodes"][results[1].FusedNodeIndex]["name"]);
        // a failing group surfaces its own exception, not an AggregateException
        var skinned = Quad("S", 0, 1, 0, 1, 0); skinned.Skinned = true;
        Assert.Throws<System.IO.InvalidDataException>(() => GlbDisconnectedParts.FuseGroups(BuildGlb(a1, skinned), new List<GlbDisconnectedParts.FuseJob> { new GlbDisconnectedParts.FuseJob { NodeIndices = new[] { 1 } } }, 0.001, out _, null));
        // a node in two groups is refused before anything runs (review of 6d8bb08: it fused twice, four triangles from two)
        var overlap = Assert.Throws<ArgumentException>(() => GlbDisconnectedParts.FuseGroups(src, new List<GlbDisconnectedParts.FuseJob> {
            new GlbDisconnectedParts.FuseJob { NodeIndices = new[] { 0, 1 } }, new GlbDisconnectedParts.FuseJob { NodeIndices = new[] { 1, 2 } } }, 0.001, out _, null));
        Assert.Contains("Node 1 is in group 0 and group 1", overlap.Message);
        // the timing line charges the parse and the weld where they run
        string timingLine = results[0].Details.First(d => d.StartsWith("timing: "));
        Assert.StartsWith("timing: parse ", timingLine); Assert.Contains("; weld ", timingLine);
        // the tick reports the planned count on the calling thread and ends at the total
        int last = -1; int ticks = 0;
        GlbDisconnectedParts.FuseGroups(src, new List<GlbDisconnectedParts.FuseJob> { new GlbDisconnectedParts.FuseJob { NodeIndices = new[] { 0, 1 } } }, 0.001, out _, n => { last = n; ticks++; });
        Assert.Equal(1, last); Assert.True(ticks >= 1);
    }

    [Fact]
    public void RemoveMeshes_strips_the_marked_nodes_meshes_and_nothing_else()
    {
        var a = Quad("A", 0, 1, 0, 1, 0); var b = Quad("B", 2, 3, 0, 1, 0); b.Translation = new double[] { 5, 0, 0 };
        var r = GlbDisconnectedParts.RemoveMeshes(BuildGlb(a, b), new HashSet<int> { 1 });
        Assert.True(r.Changed); Assert.Equal(1, r.NodesSplit);
        Assert.Equal("Removed 1 part(s): B", r.Details[0]);
        var g = Read(r.Bytes);
        Assert.NotNull(g.Node("A")["mesh"]);
        Assert.Null(g.Node("B")["mesh"]); Assert.Equal(5.0, (double)g.Node("B")["translation"][0]);   // the node stays, with its transform
        Assert.Equal(2, ((JArray)g.Root["meshes"]).Count);   // the mesh data is left in the file (an orphan), not compacted
        // a node without a mesh, or out of range: reported, nothing written
        var none = GlbDisconnectedParts.RemoveMeshes(r.Bytes, new HashSet<int> { 1, 7 });
        Assert.False(none.Changed); Assert.Null(none.Bytes); Assert.Equal(2, none.Warnings.Count);
        Assert.Throws<ArgumentException>(() => GlbDisconnectedParts.RemoveMeshes(r.Bytes, new HashSet<int>()));
    }

    [Fact]
    public void A_neighbour_a_metre_above_a_roof_is_not_its_twin()
    {
        // 2026-09-19, the Romanic's bridge deck: a roof region facing UP and correct as authored found the deckhouse's
        // undersides 0.7 % of the length above it and the twin rule turned it over ("twin in front") while its own
        // inside-out score read +0.55. Every genuine double skin measured on the ship has its twin within a third of the
        // old 1 % reach; the reach is 0.5 % now. Roof 10 x 10 at y = 8 above a hull box (the frame); a downward
        // "ceiling" 0.07 above it — 0.7 % of the 10 m length — is a neighbour: the roof keeps its winding.
        var hull = Box("Hull", 6, 0, 0, 0, inward: false);
        var roof = new Part { Name = "Roof", Positions = new float[] { 0, 8, 0,  10, 8, 0,  10, 8, 10,  0, 8, 10 }, Indices = new[] { 0, 2, 1, 0, 3, 2 } };            // faces UP
        var ceiling = new Part { Name = "Ceiling", Positions = new float[] { 0, 8.07f, 0,  10, 8.07f, 0,  10, 8.07f, 10,  0, 8.07f, 10 }, Indices = new[] { 0, 1, 2, 0, 2, 3 } };   // faces DOWN, 0.07 above
        var r = GlbDisconnectedParts.FuseNodes(BuildGlb(hull, roof, ceiling), new[] { 1, 2 }, 0.0);
        var g = Read(r.Bytes);
        var prim = (JObject)g.Primitives(g.Node("Roof_Fused"))[0];
        // the fused mesh carries both parts; the roof's two triangles are the ones at y = 8 — every one must still face UP
        var fn = FaceNormals(g, prim);
        float[] pos = g.Floats(((JObject)prim["attributes"]).Value<int>("POSITION"), 3);
        var idx = g.Indices(prim.Value<int>("indices"));
        int roofUp = 0, roofDown = 0;
        for (int t = 0; t < fn.Count; t++)
        {
            float y = pos[(int)idx[t * 3] * 3 + 1];
            if (Math.Abs(y - 8f) > 1e-4f) continue;
            if (fn[t][1] > 0) roofUp++; else roofDown++;
        }
        Assert.Equal(2, roofUp); Assert.Equal(0, roofDown);
    }
}
