using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

// The fuse's optional unseen-faces pass (GlbDisconnectedParts.Unseen.cs): every face no camera above the horizon sees
// from either side is left out of the parts that stand still and are drawn.
public class GlbUnseenFacesTests
{
    // ---- fixtures: faces by tag, written as one node per part ----

    sealed class Soup
    {
        public readonly List<double> Corners = new List<double>(); public readonly List<string> Tags = new List<string>();
        public int Count => Corners.Count / 9;
        public Soup Quad(string tag, double[] a, double[] b, double[] c, double[] d) { Tri(tag, a, b, c); Tri(tag, a, c, d); return this; }
        public Soup Tri(string tag, double[] a, double[] b, double[] c) { Corners.AddRange(a); Corners.AddRange(b); Corners.AddRange(c); Tags.Add(tag); return this; }
        // the six faces of the box lo..hi, wound outward (or inward); `skip` leaves faces out by name
        public Soup Box(string tag, double[] lo, double[] hi, bool inward = false, params string[] skip)
        {
            double x0 = lo[0], y0 = lo[1], z0 = lo[2], x1 = hi[0], y1 = hi[1], z1 = hi[2];
            void Face(string name, double[] a, double[] b, double[] c, double[] d)
            {
                if (skip.Contains(name)) return;
                if (inward) Quad(tag + ":" + name, a, d, c, b); else Quad(tag + ":" + name, a, b, c, d);
            }
            Face("top", V(x0, y1, z1), V(x1, y1, z1), V(x1, y1, z0), V(x0, y1, z0));
            Face("bottom", V(x0, y0, z0), V(x1, y0, z0), V(x1, y0, z1), V(x0, y0, z1));
            Face("south", V(x0, y0, z1), V(x1, y0, z1), V(x1, y1, z1), V(x0, y1, z1));
            Face("north", V(x1, y0, z0), V(x0, y0, z0), V(x0, y1, z0), V(x1, y1, z0));
            Face("east", V(x1, y0, z1), V(x1, y0, z0), V(x1, y1, z0), V(x1, y1, z1));
            Face("west", V(x0, y0, z0), V(x0, y0, z1), V(x0, y1, z1), V(x0, y1, z0));
            return this;
        }
    }
    static double[] V(double x, double y, double z) => new[] { x, y, z };

    sealed class Part
    {
        public string Name; public Soup Faces; public int Material; public double[] Scale; public bool SharesPrevious; public bool Uvs;
    }

    // one node per part, every face its own three vertices (indexed), a TEXCOORD_0 holding (vertex number, tag number)
    // when asked, so a test can tell which source vertex an output vertex came from
    static byte[] BuildGlb(JArray materials, params Part[] parts)
    {
        var bin = new List<byte>();
        var accessors = new JArray(); var views = new JArray(); var meshes = new JArray(); var nodes = new JArray(); var sceneNodes = new JArray();
        int View(int offset, int length, int target) { views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length, ["target"] = target }); return views.Count - 1; }
        foreach (Part part in parts)
        {
            if (!part.SharesPrevious)
            {
                var c = part.Faces.Corners; int vcount = c.Count / 3;
                int off = bin.Count; foreach (double d in c) bin.AddRange(BitConverter.GetBytes((float)d));
                double[] mins = { double.MaxValue, double.MaxValue, double.MaxValue }, maxs = { double.MinValue, double.MinValue, double.MinValue };
                for (int i = 0; i < vcount; i++) for (int a = 0; a < 3; a++) { mins[a] = Math.Min(mins[a], (float)c[i * 3 + a]); maxs[a] = Math.Max(maxs[a], (float)c[i * 3 + a]); }
                accessors.Add(new JObject { ["bufferView"] = View(off, vcount * 12, 34962), ["componentType"] = 5126, ["count"] = vcount, ["type"] = "VEC3", ["min"] = new JArray(mins), ["max"] = new JArray(maxs) });
                var attributes = new JObject { ["POSITION"] = accessors.Count - 1 };
                if (part.Uvs)
                {
                    off = bin.Count;
                    for (int i = 0; i < vcount; i++) { bin.AddRange(BitConverter.GetBytes((float)i)); bin.AddRange(BitConverter.GetBytes((float)(i / 3))); }
                    accessors.Add(new JObject { ["bufferView"] = View(off, vcount * 8, 34962), ["componentType"] = 5126, ["count"] = vcount, ["type"] = "VEC2" });
                    attributes["TEXCOORD_0"] = accessors.Count - 1;
                }
                off = bin.Count; for (int i = 0; i < vcount; i++) bin.AddRange(BitConverter.GetBytes((ushort)i));
                while ((bin.Count & 3) != 0) bin.Add(0);
                accessors.Add(new JObject { ["bufferView"] = View(off, vcount * 2, 34963), ["componentType"] = 5123, ["count"] = vcount, ["type"] = "SCALAR" });
                meshes.Add(new JObject { ["name"] = part.Name + "Mesh", ["primitives"] = new JArray(new JObject { ["attributes"] = attributes, ["indices"] = accessors.Count - 1, ["material"] = part.Material }) });
            }
            var node = new JObject { ["name"] = part.Name, ["mesh"] = meshes.Count - 1 };
            if (part.Scale != null) node["scale"] = new JArray(part.Scale);
            nodes.Add(node); sceneNodes.Add(nodes.Count - 1);
        }
        var root = new JObject
        {
            ["asset"] = new JObject { ["version"] = "2.0" }, ["scene"] = 0,
            ["scenes"] = new JArray { new JObject { ["nodes"] = sceneNodes } },
            ["nodes"] = nodes, ["meshes"] = meshes, ["accessors"] = accessors, ["bufferViews"] = views,
            ["buffers"] = new JArray { new JObject { ["byteLength"] = bin.Count } }, ["materials"] = materials ?? new JArray(new JObject { ["name"] = "m0" }),
        };
        byte[] json = Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
        int jsonPad = (4 - json.Length % 4) % 4, binPad = (4 - bin.Count % 4) % 4;
        var bytes = new List<byte>();
        void U32(uint v) { bytes.AddRange(BitConverter.GetBytes(v)); }
        U32(0x46546C67); U32(2); U32((uint)(12 + 8 + json.Length + jsonPad + 8 + bin.Count + binPad));
        U32((uint)(json.Length + jsonPad)); U32(0x4E4F534A); bytes.AddRange(json); for (int i = 0; i < jsonPad; i++) bytes.Add(0x20);
        U32((uint)(bin.Count + binPad)); U32(0x004E4942); bytes.AddRange(bin); for (int i = 0; i < binPad; i++) bytes.Add(0);
        return bytes.ToArray();
    }

    // the faces a node keeps, by tag (read back through the extractor, so the written file is what is judged)
    static string[] KeptTags(byte[] glb, Part part)
    {
        var all = GlbDisconnectedParts.ExtractAll(glb);
        var g = all.FirstOrDefault(p => p.NodeName == part.Name);
        if (g == null) return new string[0];
        var tags = new List<string>();
        for (int t = 0; t < g.Triangles.Length / 3; t++)
        {
            int f = Enumerable.Range(0, part.Faces.Count).First(i => Enumerable.Range(0, 3).All(k =>
                Enumerable.Range(0, 3).All(a => (float)part.Faces.Corners[9 * i + 3 * k + a] == g.Positions[3 * g.Triangles[3 * t + k] + a])));
            tags.Add(part.Faces.Tags[f]);
        }
        return tags.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }
    static string[] AllTags(Part part) => part.Faces.Tags.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // the rule itself, with no rim (the rim is pinned by its own test below)
    static GlbDisconnectedParts.UnseenResult Run(byte[] glb, Func<string, GlbDisconnectedParts.UnseenRole> role = null, int rimRings = 0) =>
        GlbDisconnectedParts.LeaveOutUnseenFaces(glb, (i, name) => role == null ? GlbDisconnectedParts.UnseenRole.Still : role(name), -1, rimRings);

    static Part Hull(bool inward = false, double[] scale = null, int material = 0, params string[] skip) =>
        new Part { Name = "Hull", Faces = new Soup().Box("hull", V(0, 0, 0), V(20, 10, 40), inward, skip), Scale = scale, Material = material };
    static Part Gun() => new Part { Name = "Gun", Faces = new Soup().Box("gun", V(5, 2, 10), V(15, 6, 30)) };

    // ---- the rule ----

    [Fact]
    public void A_part_inside_a_closed_hull_loses_every_face_and_the_hull_keeps_what_shows()
    {
        Part hull = Hull(), gun = Gun();
        var r = Run(BuildGlb(null, hull, gun));
        Assert.True(r.Changed);
        Assert.Equal(new[] { "Gun" }, r.NodesEmptied.Select(i => i == 1 ? "Gun" : "Hull"));   // the gun lost every face, and its mesh
        Assert.Empty(KeptTags(r.Bytes, gun));
        // the hull's bottom faces down: no camera above the horizon sees its front, and its back looks into the closed hull
        Assert.Equal(new[] { "hull:east", "hull:north", "hull:south", "hull:top", "hull:west" }, KeptTags(r.Bytes, hull));
        Assert.Equal(12 + 2, r.FacesLeftOut);
    }

    [Fact]
    public void What_an_open_top_lets_a_camera_see_stays()
    {
        Part hull = Hull(false, null, 0, "top"), gun = Gun();
        var r = Run(BuildGlb(null, hull, gun));
        Assert.Contains("gun:top", KeptTags(r.Bytes, gun));
        Assert.DoesNotContain("gun:bottom", KeptTags(r.Bytes, gun));
        Assert.Contains("hull:bottom", KeptTags(r.Bytes, hull));   // its back is seen through the open top
    }

    [Fact]
    public void A_face_whose_back_a_camera_sees_stays()
    {
        // a roof wound DOWN over an open box: no camera sees its front, every camera above sees its back - the Lab's
        // inside-out fix or a double-sided option would show exactly that side
        var roof = new Part { Name = "Roof", Faces = new Soup().Quad("roof", V(0, 12, 0), V(20, 12, 0), V(20, 12, 40), V(0, 12, 40)) };
        var r = Run(BuildGlb(null, roof));
        Assert.False(r.Changed);
        Assert.Equal(AllTags(roof), KeptTags(BuildGlb(null, roof), roof));
    }

    [Fact]
    public void A_part_the_game_does_not_draw_hides_nothing_and_loses_nothing()
    {
        Part hull = Hull(), gun = Gun();
        var r = Run(BuildGlb(null, hull, gun), n => n == "Hull" ? GlbDisconnectedParts.UnseenRole.NotDrawn : GlbDisconnectedParts.UnseenRole.Still);
        Assert.Equal(new[] { "gun:east", "gun:north", "gun:south", "gun:top", "gun:west" }, KeptTags(r.Bytes ?? BuildGlb(null, hull, gun), gun));
        Assert.Equal(AllTags(hull), KeptTags(r.Bytes ?? BuildGlb(null, hull, gun), hull));
    }

    [Fact]
    public void A_part_that_moves_hides_nothing_and_loses_nothing()
    {
        Part hull = Hull(), gun = Gun();
        byte[] glb = BuildGlb(null, hull, gun);
        var gunMoves = Run(glb, n => n == "Gun" ? GlbDisconnectedParts.UnseenRole.Moves : GlbDisconnectedParts.UnseenRole.Still);
        Assert.Equal(AllTags(gun), KeptTags(gunMoves.Bytes, gun));   // a turret turns: its insides may come round
        var hullMoves = Run(glb, n => n == "Hull" ? GlbDisconnectedParts.UnseenRole.Moves : GlbDisconnectedParts.UnseenRole.Still);
        Assert.Equal(AllTags(hull), KeptTags(hullMoves.Bytes ?? glb, hull));
        Assert.Contains("gun:top", KeptTags(hullMoves.Bytes ?? glb, gun));   // what is behind a moving part shows when it moves
    }

    [Fact]
    public void Glass_hides_nothing_and_is_never_left_out()
    {
        var materials = new JArray(new JObject { ["name"] = "glass", ["alphaMode"] = "BLEND" }, new JObject { ["name"] = "steel" });
        Part hull = Hull(false, null, 0), gun = Gun(); gun.Material = 1;
        var r = Run(BuildGlb(materials, hull, gun));
        Assert.Equal(AllTags(hull), KeptTags(r.Bytes ?? BuildGlb(materials, hull, gun), hull));
        Assert.Contains("gun:top", KeptTags(r.Bytes ?? BuildGlb(materials, hull, gun), gun));
    }

    [Fact]
    public void A_mirrored_hull_hides_with_the_winding_it_renders_with()
    {
        // authored OUTWARD under a negative scale: glTF renders a mirrored node as authored (the determinant reverses the
        // front), so it hides the gun. Its world positions alone wind every face INWARD: read that way, it would show its
        // backs to every camera and hide nothing.
        Part hull = new Part { Name = "Hull", Faces = new Soup().Box("hull", V(-20, 0, 0), V(0, 10, 40)), Scale = new[] { -1.0, 1, 1 } };
        Part gun = Gun();
        var r = Run(BuildGlb(null, hull, gun));
        Assert.Empty(KeptTags(r.Bytes, gun));
    }

    [Fact]
    public void A_shared_mesh_hides_but_is_never_cut()
    {
        Part hull = Hull(), gun = Gun(), twin = new Part { Name = "GunTwin", Faces = Gun().Faces, SharesPrevious = true };
        var r = Run(BuildGlb(null, hull, gun, twin));
        Assert.Equal(AllTags(gun), KeptTags(r.Bytes ?? BuildGlb(null, hull, gun, twin), gun));
        Assert.Equal(AllTags(gun), KeptTags(r.Bytes ?? BuildGlb(null, hull, gun, twin), twin));
    }

    [Fact]
    public void Kept_faces_keep_their_own_vertices_and_attributes_and_nothing_else()
    {
        Part hull = Hull(false, null, 0, "top"); hull.Uvs = true;   // open top: the bottom's back shows, the gun's bottom does not
        Part gun = Gun(); gun.Uvs = true;
        var r = Run(BuildGlb(null, hull, gun));
        Assert.True(r.Changed);
        JObject root = JObject.Parse(Encoding.UTF8.GetString(r.Bytes, 20, BitConverter.ToInt32(r.Bytes, 12)));
        int binAt = 20 + BitConverter.ToInt32(r.Bytes, 12) + 8;
        var node = root["nodes"].OfType<JObject>().First(n => (string)n["name"] == "Gun");
        var prim = (JObject)root["meshes"][node.Value<int>("mesh")]["primitives"][0];
        int Offset(int acc) { var a = (JObject)root["accessors"][acc]; var v = (JObject)root["bufferViews"][a.Value<int>("bufferView")]; return binAt + (v.Value<int?>("byteOffset") ?? 0) + (a.Value<int?>("byteOffset") ?? 0); }
        int pos = prim["attributes"].Value<int>("POSITION"), uv = prim["attributes"].Value<int>("TEXCOORD_0"), idx = prim.Value<int>("indices");
        int vcount = root["accessors"][pos].Value<int>("count"), icount = root["accessors"][idx].Value<int>("count");
        Assert.Equal(vcount, root["accessors"][uv].Value<int>("count"));
        Assert.Equal(icount, vcount);   // every face had its own three vertices: the kept ones, and no others
        Assert.Equal(10 * 3, icount);   // five sides of two faces each: the bottom went
        for (int v = 0; v < vcount; v++)
        {
            int source = (int)BitConverter.ToSingle(r.Bytes, Offset(uv) + 8 * v);   // the uv holds the source vertex number
            for (int a = 0; a < 3; a++)
                Assert.Equal((float)gun.Faces.Corners[3 * source + a], BitConverter.ToSingle(r.Bytes, Offset(pos) + 12 * v + 4 * a));
        }
        var min = root["accessors"][pos]["min"].Select(t => (double)t).ToArray(); var max = root["accessors"][pos]["max"].Select(t => (double)t).ToArray();
        Assert.Equal(new[] { 5.0, 2, 10 }, min); Assert.Equal(new[] { 15.0, 6, 30 }, max);
    }

    [Fact]
    public void The_rim_keeps_what_touches_a_face_that_shows()
    {
        // the hull's bottom shares its corners with the sides, which show: one ring keeps it. The gun touches nothing
        // that shows and goes whatever the rim.
        Part hull = Hull(), gun = Gun();
        byte[] glb = BuildGlb(null, hull, gun);
        var none = Run(glb, rimRings: 0);
        Assert.DoesNotContain("hull:bottom", KeptTags(none.Bytes, hull));
        var rim = GlbDisconnectedParts.LeaveOutUnseenFaces(glb, (i, n) => GlbDisconnectedParts.UnseenRole.Still);   // the default
        Assert.Equal(AllTags(hull), KeptTags(rim.Bytes, hull));
        Assert.Empty(KeptTags(rim.Bytes, gun));
        Assert.Equal(2, rim.RimKept);
        Assert.Contains("2 kept beside faces that show (the rim)", rim.Details[0]);
    }

    [Fact]
    public void A_second_pass_finds_nothing_more()
    {
        var first = Run(BuildGlb(null, Hull(), Gun()));
        var second = Run(first.Bytes);
        Assert.False(second.Changed);
        Assert.Contains("unseen faces: none", second.Details[0]);
    }

    // ---- the Vehicle Lab's roles ----

    [Theory]
    [InlineData(new[] { 0 }, false, GlbDisconnectedParts.UnseenRole.Still)]          // Body
    [InlineData(new[] { 4, 16 }, false, GlbDisconnectedParts.UnseenRole.Still)]      // Default in one record, Structure in the other
    [InlineData(new[] { 0 }, true, GlbDisconnectedParts.UnseenRole.Moves)]           // placed in the Lab
    [InlineData(new[] { 3 }, false, GlbDisconnectedParts.UnseenRole.NotDrawn)]       // Ignore
    [InlineData(new[] { 3, 0 }, false, GlbDisconnectedParts.UnseenRole.Moves)]       // Ignore in one record, Body in the other: hide nothing, lose nothing
    [InlineData(new[] { 1 }, false, GlbDisconnectedParts.UnseenRole.Moves)]          // Wheel
    [InlineData(new[] { 7 }, false, GlbDisconnectedParts.UnseenRole.Moves)]          // Gun
    [InlineData(new[] { 14 }, false, GlbDisconnectedParts.UnseenRole.Moves)]         // Sail
    [InlineData(new[] { 20 }, false, GlbDisconnectedParts.UnseenRole.Moves)]         // Flip turns its faces round
    [InlineData(new[] { 25 }, false, GlbDisconnectedParts.UnseenRole.Moves)]         // TwoSided shows backs
    [InlineData(new int[0], false, GlbDisconnectedParts.UnseenRole.Still)]           // the Lab has not seen it: it would be Default
    public void Lab_roles_map_to_what_hides_and_what_is_asked(int[] roles, bool placed, GlbDisconnectedParts.UnseenRole expected)
    {
        Assert.Equal(expected, GlbDisconnectedParts.UnseenRoleOfLab(roles, placed));
    }

    [Fact]
    public void The_list_beside_the_output_is_read_only_for_the_file_it_was_written_with()
    {
        var lines = new[] { GlbDisconnectedParts.UnseenSidecarHeader + "|1234", "hid|Fused_G_Hull", "hid|Mast" };
        Assert.Equal(new[] { "Fused_G_Hull", "Mast" }, GlbDisconnectedParts.ReadUnseenSidecar(lines, 1234));
        Assert.Null(GlbDisconnectedParts.ReadUnseenSidecar(lines, 1235));   // the output was written over since
        Assert.Null(GlbDisconnectedParts.ReadUnseenSidecar(new string[0], 1234));
        Assert.Equal("x.glb.unseen.txt", GlbDisconnectedParts.UnseenSidecarPath("x.glb"));
    }

    [Fact]
    public void The_Lab_names_every_part_that_hid_faces_and_is_no_longer_still()
    {
        var hiders = new[] { "Fused_G_Hull", "Fused_K_Crane", "Fused_Q_Inner" };
        var parts = new[] {
            ("Fused_G_Hull", 4, false),     // Default, still: fine
            ("Fused_K_Crane2", 7, false),   // the K group, now a Gun: it moves
            ("Fused_Q_Inner", 3, false),    // now ignored: not drawn
            ("Fused_B_Boat", 3, false),     // ignored, but hid nothing
            ("Fused_G_Hull", 0, true),      // placed in the Lab
        };
        Assert.Equal(new[] { "Fused_K_Crane2", "Fused_Q_Inner", "Fused_G_Hull" }, GlbDisconnectedParts.UnseenConflicts(parts, hiders));
        Assert.Empty(GlbDisconnectedParts.UnseenConflicts(parts, null));   // no list (or a stale one): nothing to ask
    }

    [Fact]
    public void A_fused_shell_matches_the_Lab_by_its_group_letter()
    {
        Assert.True(GlbDisconnectedParts.LabNameMatches("Fused_G_Material2_39_Part_001", "Fused_G_Material2_39_Part_001"));
        Assert.True(GlbDisconnectedParts.LabNameMatches("Fused_G_Material2_39_Part_002", "Fused_G_Material2_39_Part_001"));   // re-split: another first part
        Assert.False(GlbDisconnectedParts.LabNameMatches("Fused_G_Hull", "Fused_GH_Hull"));
        Assert.False(GlbDisconnectedParts.LabNameMatches("Material2_39", "Material2_3"));
        Assert.True(GlbDisconnectedParts.LabNameMatches("Material2_39", "Material2_39"));
    }
}
