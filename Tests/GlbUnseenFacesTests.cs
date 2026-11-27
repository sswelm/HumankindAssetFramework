using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

// The fuse's optional unseen-faces pass (GlbDisconnectedParts.Unseen.cs): every face no camera of the game (28 degrees
// above the horizon and up) sees from either side is left out of the parts that stand still and are drawn.
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
    static byte[] BuildGlb(JArray materials, params Part[] parts) => BuildGlb(materials, null, parts);
    // `edit`: change the JSON (and append to the BIN) before the file is written
    static byte[] BuildGlb(JArray materials, Action<JObject, List<byte>> edit, params Part[] parts)
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
        edit?.Invoke(root, bin);
        root["buffers"][0]["byteLength"] = bin.Count;
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
        Assert.Equal(new[] { "Hull", "Gun" }, r.Hiders);   // what could hide: the Lab asks about these before it generates
    }

    // ---- where the game's camera looks from (review round 2) ----

    // a floor (front up) under a roof `h` above it that overhangs it by `overhang` on every side: a camera sees the floor
    // only from below atan(h / overhang) or so, past the roof's edge
    static (Part floor, Part roof) Porch(double h, double overhang) => (
        new Part { Name = "Floor", Faces = new Soup().Quad("floor", V(0, 0, 0), V(0, 0, 2), V(2, 0, 2), V(2, 0, 0)) },
        new Part { Name = "Roof", Faces = new Soup().Quad("roof", V(-overhang, h, -overhang), V(-overhang, h, 2 + overhang), V(2 + overhang, h, 2 + overhang), V(2 + overhang, h, -overhang)) });

    [Fact]
    public void A_face_only_a_camera_below_28_degrees_could_see_goes()
    {
        // under the roof up to atan(1 / 2.03) = 26 degrees: the game's camera tilts no lower than 28 (its data's
        // DefaultCameraProfile); the hidden twins' 15-degree ring would have seen it
        var (floor, roof) = Porch(1.0, 2.0);
        var r = Run(BuildGlb(null, floor, roof));
        Assert.Empty(KeptTags(r.Bytes, floor));
        Assert.Equal(AllTags(roof), KeptTags(r.Bytes, roof));
        Assert.Equal(28.0, GlbDisconnectedParts.UnseenLowestCamera);
        Assert.Contains("(28 degrees and up)", r.Details[0]);
    }

    [Fact]
    public void The_lowest_camera_is_the_callers_to_set()
    {
        // user 2026-11-27: "could you make it configurable?" - a face seen only at 28 degrees goes when the lowest is 45;
        // one seen only below 28 stays when it is 15
        var (floor, roof) = Porch(0.6, 1.0);
        var at45 = GlbDisconnectedParts.LeaveOutUnseenFaces(BuildGlb(null, floor, roof), (i, n) => GlbDisconnectedParts.UnseenRole.Still, -1, 0, lowestCamera: 45);
        Assert.Empty(KeptTags(at45.Bytes, floor));
        Assert.Contains("(45 degrees and up)", at45.Details[0]);
        var (low, lowRoof) = Porch(1.0, 2.0);
        var at15 = GlbDisconnectedParts.LeaveOutUnseenFaces(BuildGlb(null, low, lowRoof), (i, n) => GlbDisconnectedParts.UnseenRole.Still, -1, 0, lowestCamera: 15);
        Assert.Equal(AllTags(low), KeptTags(at15.Bytes ?? BuildGlb(null, low, lowRoof), low));
        foreach (double bad in new[] { 14.9, 85.1, double.NaN })
            Assert.Throws<ArgumentOutOfRangeException>(() => GlbDisconnectedParts.LeaveOutUnseenFaces(BuildGlb(null, low, lowRoof), (i, n) => GlbDisconnectedParts.UnseenRole.Still, lowestCamera: bad));
    }

    [Fact]
    public void A_face_seen_only_at_28_degrees_near_its_corners_stays()
    {
        // seen at 28 degrees only from the points 0.95 of the way to its corners (0.6 / tan 28 = 1.13 reaches past an
        // overhang of 1 from there, not from the points at 0.7), and at a slant: facing 0.47 - a camera that steep counts
        var (floor, roof) = Porch(0.6, 1.0);
        var r = Run(BuildGlb(null, floor, roof));
        Assert.Equal(AllTags(floor), KeptTags(r.Bytes ?? BuildGlb(null, floor, roof), floor));
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
    public void A_part_not_opted_in_hides_and_keeps_every_face()
    {
        // user 2026-11-27: "an opt in per group ... it will not try to mess with other groups" - the hull is not opted in:
        // it still hides the gun, and keeps its own bottom, which no camera sees
        Part hull = Hull(), gun = Gun();
        var r = Run(BuildGlb(null, hull, gun), n => n == "Hull" ? GlbDisconnectedParts.UnseenRole.Kept : GlbDisconnectedParts.UnseenRole.Still);
        Assert.Empty(KeptTags(r.Bytes, gun));
        Assert.Equal(AllTags(hull), KeptTags(r.Bytes, hull));
        Assert.Contains("Hull", r.Hiders);
        Assert.Contains("1 not opted in: kept whole", r.Details[0]);
        var none = Run(BuildGlb(null, hull, gun), n => GlbDisconnectedParts.UnseenRole.Kept);
        Assert.False(none.Changed);
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
    public void The_rim_works_on_every_part_not_only_the_first()
    {
        // the gun is the first node asked: the hull's rim is found past its faces (review round 2: with the node cursor
        // lost, the rim kept nothing past the first part - on the real ship as many holes as no rim at all)
        Part hull = Hull(), gun = Gun();
        var rim = GlbDisconnectedParts.LeaveOutUnseenFaces(BuildGlb(null, gun, hull), (i, n) => GlbDisconnectedParts.UnseenRole.Still);
        Assert.Equal(AllTags(hull), KeptTags(rim.Bytes, hull));
        Assert.Empty(KeptTags(rim.Bytes, gun));
    }

    [Fact]
    public void The_rim_is_two_rings_deep()
    {
        // the hull's bottom in three strips across: the outer two touch the sides' corners (ring 1), the middle one only
        // the outer strips (ring 2)
        var soup = new Soup().Box("hull", V(0, 0, 0), V(20, 10, 40), false, "bottom");
        soup.Quad("bottom:front", V(0, 0, 0), V(20, 0, 0), V(20, 0, 10), V(0, 0, 10));
        soup.Quad("bottom:middle", V(0, 0, 10), V(20, 0, 10), V(20, 0, 30), V(0, 0, 30));
        soup.Quad("bottom:back", V(0, 0, 30), V(20, 0, 30), V(20, 0, 40), V(0, 0, 40));
        var hull = new Part { Name = "Hull", Faces = soup };
        byte[] glb = BuildGlb(null, hull);
        Assert.DoesNotContain("bottom:middle", KeptTags(Run(glb, rimRings: 1).Bytes, hull));
        Assert.Contains("bottom:front", KeptTags(Run(glb, rimRings: 1).Bytes, hull));
        var byDefault = GlbDisconnectedParts.LeaveOutUnseenFaces(glb, (i, n) => GlbDisconnectedParts.UnseenRole.Still);
        Assert.False(byDefault.Changed);   // two rings: every strip stays
        Assert.Equal(2, GlbDisconnectedParts.UnseenDefaultRimRings);
    }

    [Fact]
    public void A_skinned_or_morphing_part_hides_nothing_and_loses_nothing()
    {
        Part hull = Hull(), gun = Gun();
        byte[] skinned = BuildGlb(null, (root, bin) => { root["nodes"][1]["skin"] = 0; root["skins"] = new JArray(new JObject { ["joints"] = new JArray(1) }); }, hull, gun);
        Assert.Equal(AllTags(gun), KeptTags(Run(skinned).Bytes, gun));
        byte[] morphing = BuildGlb(null, (root, bin) =>
        {
            var prim = PrimitiveOf(root, "Gun");
            prim["targets"] = new JArray(new JObject { ["POSITION"] = prim["attributes"]["POSITION"] });
        }, hull, gun);
        var r = Run(morphing);
        Assert.Equal(AllTags(gun), KeptTags(r.Bytes, gun));
        Assert.DoesNotContain("Gun", r.Hiders);
    }

    [Fact]
    public void A_glass_part_inside_an_opaque_hull_keeps_every_face()
    {
        var materials = new JArray(new JObject { ["name"] = "steel" }, new JObject { ["name"] = "glass", ["alphaMode"] = "BLEND" });
        Part hull = Hull(), gun = Gun(); gun.Material = 1;
        var r = Run(BuildGlb(materials, hull, gun));
        Assert.Equal(AllTags(gun), KeptTags(r.Bytes, gun));   // the hull still loses its bottom
        Assert.DoesNotContain("Gun", r.Hiders);
    }

    [Fact]
    public void A_shared_mesh_is_named_among_what_could_hide()
    {
        Part hull = Hull(), gun = Gun(), twin = new Part { Name = "GunTwin", Faces = Gun().Faces, SharesPrevious = true };
        var r = Run(BuildGlb(null, hull, gun, twin));
        Assert.Equal(new[] { "Hull", "Gun", "GunTwin" }, r.Hiders);
    }

    [Fact]
    public void A_child_is_judged_where_its_parents_put_it()
    {
        // the hull is turned a quarter round and moved off; the gun hangs under it with no transform of its own. Read
        // without the parents (or not reached from the scene), the gun would stand 100 units from the hull, in plain view.
        Part hull = Hull(), gun = Gun();
        double s = Math.Sqrt(0.5);
        byte[] glb = BuildGlb(null, (root, bin) =>
        {
            root["scenes"][0]["nodes"] = new JArray(0);
            root["nodes"][0]["children"] = new JArray(1);
            root["nodes"][0]["translation"] = new JArray(100.0, 0, 0);
            root["nodes"][0]["rotation"] = new JArray(0.0, s, 0, s);
        }, hull, gun);
        var r = Run(glb);
        Assert.Contains(1, r.NodesEmptied);
    }

    // ---- the written file (review of the first cut) ----

    static (JObject root, int binAt) Json(byte[] glb) { int n = BitConverter.ToInt32(glb, 12); return (JObject.Parse(Encoding.UTF8.GetString(glb, 20, n)), 20 + n + 8); }
    static JObject PrimitiveOf(JObject root, string node) => (JObject)root["meshes"][root["nodes"].OfType<JObject>().First(n => (string)n["name"] == node).Value<int>("mesh")]["primitives"][0];
    static int Offset(JObject root, int binAt, int acc) { var a = (JObject)root["accessors"][acc]; var v = (JObject)root["bufferViews"][a.Value<int>("bufferView")]; return binAt + (v.Value<int?>("byteOffset") ?? 0) + (a.Value<int?>("byteOffset") ?? 0); }

    [Fact]
    public void A_copied_attribute_keeps_every_element_on_a_four_byte_boundary()
    {
        // a ubyte RGB colour (3 bytes an element, stride 4 in the source): copied tight, the elements would sit 3 apart
        Part hull = Hull(false, null, 0, "top"), gun = Gun(); gun.Uvs = true;
        byte[] glb = BuildGlb(null, (root, bin) =>
        {
            var prim = PrimitiveOf(root, "Gun"); int verts = gun.Faces.Count * 3;
            while ((bin.Count & 3) != 0) bin.Add(0);
            int off = bin.Count;
            for (int v = 0; v < verts; v++) { bin.Add((byte)v); bin.Add((byte)(2 * v)); bin.Add(7); bin.Add(0); }
            ((JArray)root["bufferViews"]).Add(new JObject { ["buffer"] = 0, ["byteOffset"] = off, ["byteLength"] = 4 * verts, ["byteStride"] = 4, ["target"] = 34962 });
            ((JArray)root["accessors"]).Add(new JObject { ["bufferView"] = root["bufferViews"].Count() - 1, ["componentType"] = 5121, ["normalized"] = true, ["count"] = verts, ["type"] = "VEC3" });
            prim["attributes"]["COLOR_0"] = root["accessors"].Count() - 1;
        }, hull, gun);
        var r = Run(glb);
        var (root2, binAt) = Json(r.Bytes);
        var p = PrimitiveOf(root2, "Gun");
        int colour = p["attributes"].Value<int>("COLOR_0"), uv = p["attributes"].Value<int>("TEXCOORD_0");
        var view = (JObject)root2["bufferViews"][root2["accessors"][colour].Value<int>("bufferView")];
        Assert.Equal(4, view.Value<int>("byteStride"));
        Assert.Equal(0, (Offset(root2, binAt, colour) - binAt) % 4);
        Assert.True(root2["accessors"][colour].Value<bool>("normalized"));
        int count = root2["accessors"][colour].Value<int>("count");
        for (int v = 0; v < count; v++)
        {
            int source = (int)BitConverter.ToSingle(r.Bytes, Offset(root2, binAt, uv) + 8 * v);
            int at = Offset(root2, binAt, colour) + 4 * v;
            Assert.Equal(new[] { (byte)source, (byte)(2 * source), (byte)7 }, new[] { r.Bytes[at], r.Bytes[at + 1], r.Bytes[at + 2] });
        }
    }

    [Fact]
    public void A_quantized_position_keeps_its_stride_and_its_bounds()
    {
        // KHR_mesh_quantization: POSITION as shorts, 6 bytes an element at stride 8
        Part hull = Hull(false, null, 0, "top"), gun = Gun();
        byte[] glb = BuildGlb(null, (root, bin) =>
        {
            var prim = PrimitiveOf(root, "Gun"); var c = gun.Faces.Corners; int verts = c.Count / 3;
            while ((bin.Count & 3) != 0) bin.Add(0);
            int off = bin.Count;
            for (int v = 0; v < verts; v++) { for (int a = 0; a < 3; a++) bin.AddRange(BitConverter.GetBytes((short)c[3 * v + a])); bin.Add(0); bin.Add(0); }
            ((JArray)root["bufferViews"]).Add(new JObject { ["buffer"] = 0, ["byteOffset"] = off, ["byteLength"] = 8 * verts, ["byteStride"] = 8, ["target"] = 34962 });
            ((JArray)root["accessors"]).Add(new JObject { ["bufferView"] = root["bufferViews"].Count() - 1, ["componentType"] = 5122, ["count"] = verts, ["type"] = "VEC3", ["min"] = new JArray(5, 2, 10), ["max"] = new JArray(15, 6, 30) });
            prim["attributes"]["POSITION"] = root["accessors"].Count() - 1;
            root["extensionsUsed"] = new JArray("KHR_mesh_quantization"); root["extensionsRequired"] = new JArray("KHR_mesh_quantization");
        }, hull, gun);
        var r = Run(glb);
        Assert.True(r.Changed);
        var (root2, _) = Json(r.Bytes);
        var pos = (JObject)root2["accessors"][PrimitiveOf(root2, "Gun")["attributes"].Value<int>("POSITION")];
        Assert.Equal(5122, pos.Value<int>("componentType"));
        Assert.Equal(8, root2["bufferViews"][pos.Value<int>("bufferView")].Value<int>("byteStride"));
        Assert.Equal(new long[] { 5, 2, 10 }, pos["min"].Select(t => (long)t));
        Assert.Equal(new long[] { 15, 6, 30 }, pos["max"].Select(t => (long)t));
        Assert.DoesNotContain("gun:bottom", KeptTags(r.Bytes, gun));
    }

    [Fact]
    public void An_accessor_byte_offset_is_read()
    {
        // the gun's positions 8 bytes into their view (an interleaved or packed file does this)
        Part hull = Hull(false, null, 0, "top"), gun = Gun();
        byte[] glb = BuildGlb(null, (root, bin) =>
        {
            var prim = PrimitiveOf(root, "Gun"); var c = gun.Faces.Corners; int verts = c.Count / 3;
            while ((bin.Count & 3) != 0) bin.Add(0);
            int off = bin.Count;
            for (int b = 0; b < 8; b++) bin.Add(0xEE);
            foreach (double d in c) bin.AddRange(BitConverter.GetBytes((float)d));
            ((JArray)root["bufferViews"]).Add(new JObject { ["buffer"] = 0, ["byteOffset"] = off, ["byteLength"] = 8 + 12 * verts, ["target"] = 34962 });
            var old = (JObject)root["accessors"][prim["attributes"].Value<int>("POSITION")];
            ((JArray)root["accessors"]).Add(new JObject { ["bufferView"] = root["bufferViews"].Count() - 1, ["byteOffset"] = 8, ["componentType"] = 5126, ["count"] = verts, ["type"] = "VEC3", ["min"] = old["min"], ["max"] = old["max"] });
            prim["attributes"]["POSITION"] = root["accessors"].Count() - 1;
        }, hull, gun);
        var r = Run(glb);
        Assert.Equal(new[] { "gun:east", "gun:north", "gun:south", "gun:top", "gun:west" }, KeptTags(r.Bytes, gun));
    }

    [Fact]
    public void Position_bounds_are_the_kept_vertices_own_to_the_bit()
    {
        // a triangle above the hull whose FIRST vertex alone holds the largest y and z, and one hidden inside it
        var flag = new Part
        {
            Name = "Flag",
            Faces = new Soup().Tri("flag", V(1.5, 12.25, 3.75), V(0.5, 11.5, 2.25), V(2.5, 11.75, 2.5)).Tri("inside", V(5, 3, 15), V(6, 3, 15), V(5, 3, 16)),
        };
        var r = Run(BuildGlb(null, Hull(), flag));
        Assert.Equal(new[] { "flag" }, KeptTags(r.Bytes, flag));
        var (root, _) = Json(r.Bytes);
        var pos = (JObject)root["accessors"][PrimitiveOf(root, "Flag")["attributes"].Value<int>("POSITION")];
        Assert.Equal(new[] { 0.5, 11.5, 2.25 }, pos["min"].Select(t => (double)t));
        Assert.Equal(new[] { 2.5, 12.25, 3.75 }, pos["max"].Select(t => (double)t));
    }

    [Fact]
    public void A_primitive_that_loses_every_face_leaves_its_mesh()
    {
        // the gun's mesh has a second primitive: a block inside the closed gun, which no camera sees
        Part hull = Hull(false, null, 0, "top"), gun = Gun();
        var inner = new Part { Name = "Inner", Faces = new Soup().Box("inner", V(7, 3, 15), V(13, 5, 25)) };
        byte[] glb = BuildGlb(null, (root, bin) =>
        {
            var gunMesh = (JArray)root["meshes"][root["nodes"][1].Value<int>("mesh")]["primitives"];
            gunMesh.Add(root["meshes"][root["nodes"][2].Value<int>("mesh")]["primitives"][0].DeepClone());
            ((JObject)root["nodes"][2]).Remove("mesh");
        }, hull, gun, inner);
        var r = Run(glb);
        var (root2, _) = Json(r.Bytes);
        Assert.Single((JArray)root2["meshes"][root2["nodes"][1].Value<int>("mesh")]["primitives"]);
        var both = new Part { Name = "Gun", Faces = new Soup() };
        both.Faces.Corners.AddRange(gun.Faces.Corners); both.Faces.Tags.AddRange(gun.Faces.Tags);
        both.Faces.Corners.AddRange(inner.Faces.Corners); both.Faces.Tags.AddRange(inner.Faces.Tags);
        Assert.DoesNotContain(KeptTags(r.Bytes, both), t => t.StartsWith("inner", StringComparison.Ordinal));
        Assert.Equal(12 + 2, r.LeftOutByNode[1]);   // the block, and the gun's bottom
    }

    [Fact]
    public void Kept_vertices_past_65535_take_32_bit_indices()
    {
        // a 256 x 256 grid of shared vertices on the hull's deck - 65,536 kept, and one triangle inside to lose: index
        // 65535 does not fit a 16-bit index (glTF reserves it)
        var r = Run(GridOnDeck());
        Assert.Equal(1, r.LeftOutByNode[1]);
        var (root2, _) = Json(r.Bytes);
        var prim = PrimitiveOf(root2, "Grid");
        Assert.Equal(65536, root2["accessors"][prim["attributes"].Value<int>("POSITION")].Value<int>("count"));
        Assert.Equal(5125, root2["accessors"][prim.Value<int>("indices")].Value<int>("componentType"));
    }

    [Fact]
    public void Cancel_leaves_every_face_in_and_a_bar_that_never_cancels_changes_nothing()
    {
        byte[] glb = GridOnDeck();
        var plain = Run(glb);
        int calls = 0;
        var watched = GlbDisconnectedParts.LeaveOutUnseenFaces(glb, (i, n) => GlbDisconnectedParts.UnseenRole.Still, 1, 0, p => { calls++; Assert.InRange(p, 0, 1); return true; });
        Assert.True(calls >= 1);   // asked at once, and every quarter second after
        Assert.Equal(plain.Bytes, watched.Bytes);
        var cancelled = GlbDisconnectedParts.LeaveOutUnseenFaces(glb, (i, n) => GlbDisconnectedParts.UnseenRole.Still, 1, 0, p => false);
        Assert.True(cancelled.Cancelled);
        Assert.False(cancelled.Changed);
        Assert.Null(cancelled.Bytes);
        Assert.StartsWith("unseen faces: cancelled after", cancelled.Details[0]);
    }

    // a 256 x 256 grid of shared vertices (32-bit indices) on a closed hull's deck, and one triangle inside the hull
    static byte[] GridOnDeck()
    {
        return BuildGlb(null, (root, bin) =>
        {
            const int n = 256;
            while ((bin.Count & 3) != 0) bin.Add(0);
            int pOff = bin.Count;
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) { bin.AddRange(BitConverter.GetBytes((float)(j * 0.05))); bin.AddRange(BitConverter.GetBytes(10.5f)); bin.AddRange(BitConverter.GetBytes((float)(i * 0.05))); }
            foreach (var p in new[] { V(5, 3, 15), V(6, 3, 15), V(5, 3, 16) }) foreach (double d in p) bin.AddRange(BitConverter.GetBytes((float)d));
            int verts = n * n + 3;
            int iOff = bin.Count; var idx = new List<uint>();
            for (int i = 0; i + 1 < n; i++) for (int j = 0; j + 1 < n; j++)
            {
                uint a = (uint)(i * n + j), b = a + 1, c = a + n, d = c + 1;
                idx.AddRange(new[] { a, c, b, b, c, d });   // front up
            }
            idx.AddRange(new uint[] { (uint)(n * n), (uint)(n * n + 1), (uint)(n * n + 2) });
            foreach (uint u in idx) bin.AddRange(BitConverter.GetBytes(u));
            var views = (JArray)root["bufferViews"]; var acc = (JArray)root["accessors"];
            views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = pOff, ["byteLength"] = 12 * verts, ["target"] = 34962 });
            acc.Add(new JObject { ["bufferView"] = views.Count - 1, ["componentType"] = 5126, ["count"] = verts, ["type"] = "VEC3", ["min"] = new JArray(0.0, 3, 0), ["max"] = new JArray(12.75, 10.5, 16) });
            views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = iOff, ["byteLength"] = 4 * idx.Count, ["target"] = 34963 });
            acc.Add(new JObject { ["bufferView"] = views.Count - 1, ["componentType"] = 5125, ["count"] = idx.Count, ["type"] = "SCALAR" });
            ((JArray)root["meshes"]).Add(new JObject { ["primitives"] = new JArray(new JObject { ["attributes"] = new JObject { ["POSITION"] = acc.Count - 2 }, ["indices"] = acc.Count - 1, ["material"] = 0 }) });
            ((JArray)root["nodes"]).Add(new JObject { ["name"] = "Grid", ["mesh"] = root["meshes"].Count() - 1 });
            ((JArray)root["scenes"][0]["nodes"]).Add(root["nodes"].Count() - 1);
        }, Hull());
    }

    [Fact]
    public void A_part_that_loses_every_face_loses_its_mesh_and_the_mesh_stays_valid()
    {
        // an extension the compaction does not know: it is refused, and the mesh no node uses any more stays in the file -
        // with its primitives, since glTF forbids a mesh with none
        Part hull = Hull(), gun = Gun();
        var r = Run(BuildGlb(null, (root, bin) => root["extensionsUsed"] = new JArray("EXT_some_vendor_thing"), hull, gun));
        var (root2, _) = Json(r.Bytes);
        var gunNode = root2["nodes"].OfType<JObject>().First(n => (string)n["name"] == "Gun");
        Assert.Null(gunNode["mesh"]);
        Assert.All(root2["meshes"].OfType<JObject>(), m => Assert.NotEmpty((JArray)m["primitives"]));
    }

    [Fact]
    public void A_node_no_scene_reaches_hides_nothing()
    {
        Part hull = Hull(), gun = Gun();
        var r = Run(BuildGlb(null, (root, bin) => root["scenes"][0]["nodes"] = new JArray(1), hull, gun));   // only the gun is in the scene
        Assert.Contains("gun:top", KeptTags(r.Bytes ?? BuildGlb(null, hull, gun), gun));
        Assert.DoesNotContain("Hull", r.Hiders);
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
    [InlineData(new int[0], false, GlbDisconnectedParts.UnseenRole.Still)]           // no role given (the Fuser passes the Lab's name guess for a part with no record)
    public void Lab_roles_map_to_what_hides_and_what_is_asked(int[] roles, bool placed, GlbDisconnectedParts.UnseenRole expected)
    {
        Assert.Equal(expected, GlbDisconnectedParts.UnseenRoleOfLab(roles, placed));
    }

    [Fact]
    public void Every_Lab_role_has_its_place()
    {
        // all 27 (VehicleLabWindow.Role): what the game draws standing still, what it does not draw, and what moves
        var still = new[] { 0 /*Body*/, 4 /*Default*/, 15 /*Rigging*/, 16 /*Structure*/, 19 /*Preserve*/, 21 /*Detail*/, 22 /*Shroud*/, 23 /*Window*/, 24 /*Railing*/ };
        for (int role = 0; role <= 26; role++)
        {
            var expected = role == 3 ? GlbDisconnectedParts.UnseenRole.NotDrawn : still.Contains(role) ? GlbDisconnectedParts.UnseenRole.Still : GlbDisconnectedParts.UnseenRole.Moves;
            Assert.True(expected == GlbDisconnectedParts.UnseenRoleOfLab(new[] { role }, false), "role " + role);
        }
    }

    [Fact]
    public void A_part_with_no_record_takes_a_marked_role_of_its_parent_as_the_Lab_does()
    {
        var records = new List<(string name, int role, bool placed)> { ("Gun", 7, false), ("Hull", 4, false), ("Deck", 0, true) };
        int Guess(string n) => n.Contains("sail") ? 14 : 4;
        Assert.Equal(GlbDisconnectedParts.UnseenRole.Moves, GlbDisconnectedParts.UnseenRoleFromLabRecords("Gun_Part_003", records, Guess));   // Gun's role
        Assert.Equal(GlbDisconnectedParts.UnseenRole.Still, GlbDisconnectedParts.UnseenRoleFromLabRecords("Hull_Part_001", records, Guess));  // Default is not inherited: the guess, Default
        Assert.Equal(GlbDisconnectedParts.UnseenRole.Moves, GlbDisconnectedParts.UnseenRoleFromLabRecords("Hull_Part_001_sail", records, Guess)); // no parent by the Cutter's tails: the guess
        Assert.Equal(GlbDisconnectedParts.UnseenRole.Moves, GlbDisconnectedParts.UnseenRoleFromLabRecords("Deck_CutA", records, Guess));     // a placed parent
        Assert.Equal(GlbDisconnectedParts.UnseenRole.Still, GlbDisconnectedParts.UnseenRoleFromLabRecords("Hull_2", records, Guess));        // a bare number is not ancestry
        Assert.Equal(GlbDisconnectedParts.UnseenRole.Still, GlbDisconnectedParts.UnseenRoleFromLabRecords("Hull", records, Guess));
    }

    [Theory]
    [InlineData("Fused_P_Propeller", VehicleLabNameGuess.Rotor)]       // review round 3: "propeller" contains "rope" - it came out Rigging, a still role
    [InlineData("Propeller_Part_002", VehicleLabNameGuess.Rotor)]
    [InlineData("TailPropeller", VehicleLabNameGuess.TailRotor)]
    [InlineData("Rope_Ladder", VehicleLabNameGuess.Rigging)]
    [InlineData("Ratline_Rope", VehicleLabNameGuess.Shroud)]
    [InlineData("OarBlade", VehicleLabNameGuess.Oar)]
    [InlineData("Hull", VehicleLabNameGuess.Default)]
    [InlineData(null, VehicleLabNameGuess.Default)]
    public void The_Lab_guesses_a_role_from_a_name(string name, int role)
    {
        Assert.Equal(role, VehicleLabNameGuess.RoleOf(name));
        Assert.Equal(role == VehicleLabNameGuess.Default || role == VehicleLabNameGuess.Rigging || role == VehicleLabNameGuess.Shroud ? GlbDisconnectedParts.UnseenRole.Still : GlbDisconnectedParts.UnseenRole.Moves,
            GlbDisconnectedParts.UnseenRoleFromLabRecords(name, null, VehicleLabNameGuess.RoleOf));
    }

    [Fact]
    public void A_gun_nothing_moves_stands_still()
    {
        // the Saleg's Revenge: every cannon one Gun part, no turret, no recoil or deploy raise, gunElevMax 0 in the pack
        var still = new List<(float, string)> { (0f, "") };
        Assert.True(GlbDisconnectedParts.LabGunStands(new[] { 0, 7, 4 }, 0f, 0f, still));
        Assert.False(GlbDisconnectedParts.LabGunStands(new[] { 2, 7 }, 0f, 0f, still));                                   // it rides a turret
        Assert.False(GlbDisconnectedParts.LabGunStands(new[] { 7 }, 10f, 0f, still));                                     // raised on deploy
        Assert.False(GlbDisconnectedParts.LabGunStands(new[] { 7 }, 0f, 0.2f, still));                                    // recoils
        Assert.False(GlbDisconnectedParts.LabGunStands(new[] { 7 }, 0f, 0f, new List<(float, string)> { (0f, ""), (15f, "") }));   // a pack model elevates it
        Assert.False(GlbDisconnectedParts.LabGunStands(new[] { 7 }, 0f, 0f, new List<(float, string)> { (0f, "Turret") }));        // the game aims a turret bone
        Assert.False(GlbDisconnectedParts.LabGunStands(new[] { 7 }, 0f, 0f, new List<(float, string)>()));               // not in the pack: unknown, so it moves
        Assert.Equal(4, GlbDisconnectedParts.UnseenLabRole(7, true));
        Assert.Equal(4, GlbDisconnectedParts.UnseenLabRole(11, true));
        Assert.Equal(4, GlbDisconnectedParts.UnseenLabRole(12, true));
        Assert.Equal(7, GlbDisconnectedParts.UnseenLabRole(7, false));
        Assert.Equal(2, GlbDisconnectedParts.UnseenLabRole(2, true));   // a turret itself turns
        Assert.Equal(GlbDisconnectedParts.UnseenRole.Still, GlbDisconnectedParts.UnseenRoleOfLab(new[] { GlbDisconnectedParts.UnseenLabRole(7, true), 4 }, false));
    }

    [Fact]
    public void The_pack_names_the_guns_settings_of_the_Labs_output()
    {
        string pack = "{\"models\":[{\"modelFile\":\"D:/3DModels/Ship/ship_Spin.glb\",\"gunElevMax\":0.0,\"turretBone\":\"\"}," +
                      "{\"modelFile\":\"D:/3DModels/Tank/tank_Spin.glb\",\"gunElevMax\":12.5,\"turretBone\":\"Turret\"}]}";
        bool Same(string a, string b) => string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { (0f, "") }, GlbDisconnectedParts.PackGunSettings(pack, "d:\\3DModels\\Ship\\ship_Spin.glb", Same));
        Assert.Equal(new[] { (12.5f, "Turret") }, GlbDisconnectedParts.PackGunSettings(pack, "D:/3DModels/Tank/tank_Spin.glb", Same));
        Assert.Empty(GlbDisconnectedParts.PackGunSettings(pack, "D:/elsewhere.glb", Same));
        Assert.Empty(GlbDisconnectedParts.PackGunSettings("{ not json", "D:/3DModels/Ship/ship_Spin.glb", Same));
        Assert.Empty(GlbDisconnectedParts.PackGunSettings("{\"models\":[{\"modelFile\":\"D:/3DModels/Ship/ship_Spin.glb\",\"gunElevMax\":\"high\"}]}", "D:/3DModels/Ship/ship_Spin.glb", Same));
    }

    [Fact]
    public void The_Lab_checks_its_second_model_against_the_second_list()
    {
        // the Lab names the second model's parts with "B_"; the list beside that model names them as the model does
        var parts = new[] { ("B_Fused_A_Object_9", 1, false), ("Fused_A_Hull", 1, false), ("B_Deck", 4, false) };
        Assert.Equal(new[] { "B_Fused_A_Object_9" }, GlbDisconnectedParts.UnseenConflicts(parts, null, new[] { "Fused_A_Object_9", "Deck" }));
        Assert.Equal(new[] { "Fused_A_Hull" }, GlbDisconnectedParts.UnseenConflicts(parts, new[] { "Fused_A_Hull" }, null));
        Assert.Equal("Fused_A_Object_9", GlbDisconnectedParts.LabSecondModelName("B_Fused_A_Object_9"));
        Assert.Null(GlbDisconnectedParts.LabSecondModelName("Fused_A_Hull"));
        Assert.Null(GlbDisconnectedParts.LabSecondModelName("B_"));
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
