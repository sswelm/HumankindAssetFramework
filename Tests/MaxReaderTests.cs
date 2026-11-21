using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;

// The native 3ds Max reader (editor/MaxDocument.cs, MaxScene.cs, MaxModel.cs): the compound-file container, the chunk
// tree, the scene's objects and references, and the model the tools read. The files here are built byte by byte in the
// test (a compound-file writer and a scene builder of their own); tools/max_drill.sh holds the reader to a second
// reader in Python over the real files, and tools/max_unity_drill.sh runs the door inside a Unity editor.
public class MaxReaderTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "haf-max-tests-" + Guid.NewGuid().ToString("N").Substring(0, 10));
    public MaxReaderTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch (Exception) { } }

    // ---- bytes ----------------------------------------------------------------------------------------------------
    static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);
    static byte[] U32(long v) => BitConverter.GetBytes((uint)v);
    static byte[] F(params float[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
    static byte[] Txt(string s) => Encoding.Unicode.GetBytes(s);
    static byte[] Leaf(int id, params byte[][] parts) { var d = Cat(parts); return Cat(U16(id), U32(d.Length + 6), d); }
    static byte[] Box(int id, params byte[][] kids) { var d = Cat(kids); return Cat(U16(id), U32((uint)(d.Length + 6) | 0x80000000u), d); }
    const uint None = 0xFFFFFFFF;

    // ---- a compound-file writer, independent of the reader: small streams in the mini stream, large ones in sectors ---
    static byte[] Cfb(params (string name, byte[] data)[] streams)
    {
        const int SS = 512, MS = 64, Cut = 4096; const uint End = 0xFFFFFFFE, Free = 0xFFFFFFFF, FatSect = 0xFFFFFFFD;
        var mini = new MemoryStream(); var miniFat = new List<uint>();
        var entries = new List<(string name, int type, uint start, long size)> { ("Root Entry", 5, End, 0) };
        var big = new List<(int entry, byte[] data)>();
        foreach (var (name, data) in streams)
        {
            if (data.Length > 0 && data.Length < Cut)
            {
                uint start = (uint)miniFat.Count; int n = (data.Length + MS - 1) / MS;
                for (int i = 0; i < n; i++) miniFat.Add(i == n - 1 ? End : start + (uint)i + 1);
                mini.Write(data, 0, data.Length); mini.Write(new byte[n * MS - data.Length], 0, n * MS - data.Length);
                entries.Add((name, 2, start, data.Length));
            }
            else { entries.Add((name, 2, End, data.Length)); big.Add((entries.Count - 1, data)); }
        }
        var sectors = new List<byte[]>(); var fat = new List<uint>();
        uint Chain(byte[] data)
        {
            if (data.Length == 0) return End;
            uint start = (uint)sectors.Count; int n = (data.Length + SS - 1) / SS;
            for (int i = 0; i < n; i++)
            {
                var s = new byte[SS]; Buffer.BlockCopy(data, i * SS, s, 0, Math.Min(SS, data.Length - i * SS));
                sectors.Add(s); fat.Add(i == n - 1 ? End : start + (uint)i + 1);
            }
            return start;
        }
        entries[0] = ("Root Entry", 5, Chain(mini.ToArray()), mini.Length);
        foreach (var (e, data) in big) entries[e] = (entries[e].name, 2, Chain(data), data.Length);
        uint miniFatStart = Chain(miniFat.SelectMany(BitConverter.GetBytes).ToArray());
        int miniFatSectors = (miniFat.Count * 4 + SS - 1) / SS;
        var dirBytes = new byte[(entries.Count * 128 + SS - 1) / SS * SS];
        for (int i = 0; i < entries.Count; i++)
        {
            var nb = Encoding.Unicode.GetBytes(entries[i].name); int at = i * 128;
            Buffer.BlockCopy(nb, 0, dirBytes, at, nb.Length);
            BitConverter.GetBytes((ushort)(nb.Length + 2)).CopyTo(dirBytes, at + 0x40);
            dirBytes[at + 0x42] = (byte)entries[i].type; dirBytes[at + 0x43] = 1;
            for (int k = 0x44; k <= 0x4C; k += 4) BitConverter.GetBytes(Free).CopyTo(dirBytes, at + k);
            BitConverter.GetBytes(entries[i].start).CopyTo(dirBytes, at + 0x74);
            BitConverter.GetBytes((uint)entries[i].size).CopyTo(dirBytes, at + 0x78);
        }
        uint dirStart = Chain(dirBytes);
        int nFat = 1; while (nFat * (SS / 4) < sectors.Count + nFat) nFat++;
        uint fatStart = (uint)sectors.Count;
        for (int i = 0; i < nFat; i++) fat.Add(FatSect);
        while (fat.Count < nFat * (SS / 4)) fat.Add(Free);
        for (int i = 0; i < nFat; i++) sectors.Add(fat.Skip(i * (SS / 4)).Take(SS / 4).SelectMany(BitConverter.GetBytes).ToArray());
        var h = new byte[SS];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(h, 0);
        U16(0x3E).CopyTo(h, 0x18); U16(3).CopyTo(h, 0x1A); U16(0xFFFE).CopyTo(h, 0x1C); U16(9).CopyTo(h, 0x1E); U16(6).CopyTo(h, 0x20);
        U32(nFat).CopyTo(h, 0x2C); U32(dirStart).CopyTo(h, 0x30); U32(Cut).CopyTo(h, 0x38);
        U32(miniFatStart).CopyTo(h, 0x3C); U32(miniFatSectors).CopyTo(h, 0x40); U32(End).CopyTo(h, 0x44); U32(0).CopyTo(h, 0x48);
        for (int i = 0; i < 109; i++) U32(i < nFat ? fatStart + (uint)i : Free).CopyTo(h, 0x4C + 4 * i);
        return Cat(h, sectors.SelectMany(s => s).ToArray());
    }

    // ---- a scene builder --------------------------------------------------------------------------------------------
    sealed class Sc
    {
        readonly List<(string name, uint a, uint b, uint sup)> classes = new List<(string, uint, uint, uint)>();
        readonly List<byte[]> objects = new List<byte[]>();
        public bool Gzip; public string Directory = "ClassDirectory2";
        int Cls(string name, uint a, uint b, uint sup)
        {
            int i = classes.FindIndex(c => c.a == a && c.b == b && c.sup == sup);
            if (i < 0) { classes.Add((name, a, b, sup)); i = classes.Count - 1; }
            return i;
        }
        public int Add(string name, uint a, uint b, uint sup, params byte[][] kids) { objects.Add(Box(Cls(name, a, b, sup), kids)); return objects.Count - 1; }
        public int AddRaw(int chunkId, params byte[][] kids) { objects.Add(Box(chunkId, kids)); return objects.Count - 1; }
        static byte[] RefArray(params long[] r) => Leaf(0x2034, r.Select(U32).ToArray());
        static byte[] RefPairs(params (int slot, int index)[] r) => Leaf(0x2035, Cat(new[] { U32(r.Length) }.Concat(r.SelectMany(x => new[] { U32(x.slot), U32(x.index) })).ToArray()));

        public int Root() => Add("RootNode", 2, 0, 1);
        public int Pos(params float[] pos) => Add("Bezier Position", 0x2008, 0, 0x900B, Leaf(0x2503, F(pos)));
        public int Rot(params float[] quat) => Add("TCB Rotation", 0x442313, 0, 0x900C, Leaf(0x2504, F(quat)));
        /// <summary>scaleAxis: the quaternion after the three factors (the axis system the factors apply in).</summary>
        public int Scl(float[] scale, float[] scaleAxis = null) => Add("Bezier Scale", 0x2010, 0, 0x900D, Leaf(0x2505, F(scale), F(scaleAxis ?? new float[] { 0, 0, 0, 1 })));
        public int PrsOf(int p, int r, int s) => Add("Position/Rotation/Scale", 0x2005, 0, 0x9008, RefArray(p, r, s));
        public int Prs(float[] pos, float[] quat, float[] scale, float[] scaleAxis = null) => PrsOf(Pos(pos), Rot(quat), Scl(scale, scaleAxis));
        public int PositionList(params int[] entries) => Add("Position List", 0x4B4B1002, 0, 0x900B, RefArray(entries.Select(e => (long)e).ToArray()));
        /// <summary>faces: a, b, c, smoothing group, flags, material id per face; tfaces: three UV indices per face.</summary>
        public static byte[] MeshChunk(float[] verts, int[] faces, float[] tverts = null, int[] tfaces = null)
        {
            var kids = new List<byte[]> { Leaf(0x0914, U32(verts.Length / 3), F(verts)) };
            var fb = new List<byte[]> { U32(faces.Length / 6) };
            for (int i = 0; i < faces.Length; i += 6) { fb.Add(U32(faces[i])); fb.Add(U32(faces[i + 1])); fb.Add(U32(faces[i + 2])); fb.Add(U32(faces[i + 3])); fb.Add(U16(faces[i + 4])); fb.Add(U16(faces[i + 5])); }
            kids.Add(Leaf(0x0912, fb.ToArray()));
            if (tverts != null) { kids.Add(Leaf(0x0916, U32(tverts.Length / 3), F(tverts))); kids.Add(Leaf(0x0918, tfaces.Select(t => U32(t)).ToArray())); }
            return Box(0x08FE, kids.ToArray());
        }
        public int Mesh(float[] verts, int[] faces, float[] tverts = null, int[] tfaces = null)
            => Add("Editable Mesh", 0xE44F10B3, 0, 0x10, MeshChunk(verts, faces, tverts, tfaces));
        public int Bitmap(string file, bool tileU = true, bool tileV = true, float offU = 0, float offV = 0, float tilU = 1, float tilV = 1)
        {
            byte[] P(int ix, float v) => Box(0x0002, Leaf(0x0003, U32(ix)), Leaf(0x0004), Leaf(0x0100, F(v)));
            int pb = Add("ParamBlock", 8, 0, 8, P(0, offU), P(1, offV), P(2, tilU), P(3, tilV), P(4, 0));
            int pl = Add("Placement", 0x100, 0, 0xC20, RefArray(pb), Leaf(0x9002, U32(0x50000000u | (tileU ? 1u : 0) | (tileV ? 2u : 0))));
            return Add("Bitmap", 0x240, 0, 0xC10, RefArray(pl), Box(0x5010, Leaf(0x1230, Txt(file))));
        }
        public int Mask(int map, int mask) => Add("Mask", 0x220, 0, 0xC10, RefArray(map, mask));
        public int Noise() => Add("Noise", 0x234, 0, 0xC10);
        /// <summary>otherSlots: maps in slots other than diffuse, as (slot, map object) - slot n's map is reference 2n + 1 of the map table.</summary>
        public int Std(string name, float[] rgb, int map = -1, float amount = 1, float opacity = 1, bool twoSided = false, (int slot, int map)[] otherSlots = null)
        {
            int pb = Add("ParamBlock", 8, 0, 8,
                Box(0x0002, Leaf(0x0003, U32(1)), Leaf(0x0004), Leaf(0x0102, F(rgb))),
                Box(0x0002, Leaf(0x0003, U32(6)), Leaf(0x0004), Leaf(0x0100, F(opacity))));
            var pairs = new List<(int, int)>();
            if (map >= 0)
            {
                int am = Add("Bezier Float", 0x2007, 0, 0x9003, Leaf(0x2501, F(amount)));
                pairs.Add((2, am)); pairs.Add((3, map));
            }
            foreach (var o in otherSlots ?? new (int, int)[0]) pairs.Add((2 * o.slot + 1, o.map));
            int tm = Add("Texmaps", 0x1200, 0, 0x1080, RefPairs(pairs.ToArray()));
            return Add("Standard", 2, 0, 0xC00, RefArray(pb, tm), Box(0x4000, Leaf(0x4001, Txt(name))), Leaf(0x5000, U32(0x50000830u | (twoSided ? 8u : 0))));
        }
        /// <summary>A sub-material of -1 is an empty slot.</summary>
        public int Multi(params int[] subs) => Add("Multi/Sub-Object", 0x200, 0, 0xC00, RefArray(subs.Select(s => s < 0 ? (long)None : s).ToArray()));
        /// <summary>parent: the OBJECT index of the parent node. offsetScale may hold seven numbers (factors, then the axis quaternion).</summary>
        public int Node(string name, int parent, int ctrl, int obj, int material = -1, float[] offsetPos = null, float[] offsetQuat = null, float[] offsetScale = null)
            => Add("Node", 1, 0, 1, RefArray(ctrl < 0 ? (long)None : ctrl, obj < 0 ? (long)None : obj, None, material < 0 ? (long)None : material),
                Leaf(0x0960, U32(parent), U32(0)), Leaf(0x0962, Txt(name)),
                Leaf(0x096A, F(offsetPos ?? new float[] { 0, 0, 0 })), Leaf(0x096B, F(offsetQuat ?? new float[] { 0, 0, 0, 1 })),
                Leaf(0x096C, offsetScale == null ? Cat(F(1, 1, 1), F(0, 0, 0, 1)) : offsetScale.Length == 3 ? Cat(F(offsetScale), F(0, 0, 0, 1)) : F(offsetScale)));
        public int Derived(params int[] refsBaseLast) => AddRaw(0x2032, RefArray(refsBaseLast.Select(s => (long)s).ToArray()));
        /// <summary>The other derived-object chunk (0x2033: one with a world-space modifier on it).</summary>
        public int WsmDerived(params int[] refsBaseLast) => AddRaw(0x2033, RefArray(refsBaseLast.Select(s => (long)s).ToArray()));
        public int Count => objects.Count;

        public byte[] File()
        {
            var cd = Cat(classes.Select(c => Box(0x2040, Leaf(0x2060, BitConverter.GetBytes(-1), U32(c.a), U32(c.b), U32(c.sup)), Leaf(0x2042, Txt(c.name)))).ToArray());
            var scene = Box(0x2003, objects.ToArray());
            if (Gzip)
            {
                using (var ms = new MemoryStream()) { using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(scene, 0, scene.Length); scene = ms.ToArray(); }
            }
            return Cfb(("Scene", scene), (Directory, cd), ("DllDirectory", new byte[0]));
        }
    }

    static readonly float[] Id = { 0, 0, 0 }, NoRot = { 0, 0, 0, 1 }, One = { 1, 1, 1 };
    static readonly float[] Tri = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };            // normal +Z in 3ds Max axes
    static int[] Face(int a, int b, int c, int sg = 1, int flags = 7, int mat = 0) => new[] { a, b, c, sg, flags, mat };

    HafModel Build(Sc s, out MaxReport rep, string name = "t.max", MaxImageDecoder dec = null)
    {
        string path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        System.IO.File.WriteAllBytes(path, s.File());
        return MaxModel.Read(path, dec, out rep);
    }
    static void Near(double expected, double actual, double tol = 1e-5) => Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");
    static void Near(float[] actual, params double[] expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) <= 1e-5, $"value {i}: expected {expected[i]}, got {actual[i]} (all: {string.Join(", ", actual)})");
    }
    string Png(string name, int w, int h, params byte[] rgb)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = rgb[i * 3]; px[i * 4 + 1] = rgb[i * 3 + 1]; px[i * 4 + 2] = rgb[i * 3 + 2]; px[i * 4 + 3] = 255; }
        string p = Path.Combine(dir, name); System.IO.File.WriteAllBytes(p, PngCodec.Encode(w, h, px, false)); return p;
    }

    // ---- the container ----------------------------------------------------------------------------------------------
    [Fact]
    public void Container_reads_small_and_large_streams_and_refuses_what_is_not_a_compound_file()
    {
        var small = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();          // mini stream, 5 mini sectors
        var large = Enumerable.Range(0, 9000).Select(i => (byte)(i * 13 + 1)).ToArray();    // 18 sectors
        var doc = MaxDocument.Read(Cfb(("A", small), ("B", large), ("Empty", new byte[0]), ("C", new byte[] { 1, 2, 3 })));
        Assert.Equal(small, doc.Streams["A"]); Assert.Equal(large, doc.Streams["B"]); Assert.Empty(doc.Streams["Empty"]); Assert.Equal(new byte[] { 1, 2, 3 }, doc.Streams["C"]);
        Assert.False(doc.Compressed);
        Assert.Contains("compound-file signature", Assert.Throws<InvalidDataException>(() => MaxDocument.Read(new byte[2048])).Message);
        Assert.Contains("shorter", Assert.Throws<InvalidDataException>(() => MaxDocument.Read(new byte[10])).Message);
    }

    [Fact]
    public void Container_refuses_a_looping_sector_chain()
    {
        var file = Cfb(("B", new byte[9000]));
        // the allocation table is the last sector: make the first data sector point at itself
        int fatAt = file.Length - 512;
        uint first = BitConverter.ToUInt32(file, fatAt);
        Assert.Equal(1u, first);                                   // sector 0 -> 1 as written
        U32(0).CopyTo(file, fatAt);
        Assert.Contains("loops", Assert.Throws<InvalidDataException>(() => MaxDocument.Read(file)).Message);
    }

    [Fact]
    public void Chunks_parse_nested_and_wide_sizes_and_refuse_overruns()
    {
        var wide = Cat(U16(0x1234), U32(0), BitConverter.GetBytes((ulong)(14 + 4)), U32(77));                       // a 64-bit size, leaf
        var wideBox = Cat(U16(0x2222), U32(0), BitConverter.GetBytes((ulong)(14 + wide.Length) | 1UL << 63), wide);   // a 64-bit size, container
        var tree = MaxDocument.ParseChunks(Cat(Box(0x10, Leaf(0x11, U32(5)), Leaf(0x12, Txt("ab\0"))), wideBox));
        Assert.Equal(2, tree.Count);
        Assert.Equal(5u, tree[0].First(0x11).U32(0)); Assert.Equal("ab", tree[0].First(0x12).Text());
        Assert.Equal(77u, tree[1].First(0x1234).U32(0));
        Assert.Contains("declares", Assert.Throws<InvalidDataException>(() => MaxDocument.ParseChunks(Cat(U16(1), U32(60), new byte[4]))).Message);
        Assert.Contains("stray", Assert.Throws<InvalidDataException>(() => MaxDocument.ParseChunks(Cat(Leaf(1, U32(1)), new byte[3]))).Message);
        Assert.Contains("asked of", Assert.Throws<InvalidDataException>(() => tree[0].First(0x11).U32(2)).Message);
    }

    // ---- geometry ---------------------------------------------------------------------------------------------------
    [Fact]
    public void A_triangle_arrives_in_GLB_axes_with_its_normal_and_colour()
    {
        var s = new Sc(); int root = s.Root();
        int mat = s.Std("Paint", new[] { 0.5f, 0.2f, 1f }, twoSided: true);
        s.Node("Plate", root, s.Prs(Id, NoRot, One), s.Mesh(new float[] { 1, 2, 3, 4, 2, 3, 1, 6, 3 }, Face(0, 1, 2)), mat);
        var m = Build(s, out var rep);
        Assert.Single(m.Nodes); Assert.Equal("Plate", m.Nodes[0].Name); Assert.Equal(new[] { 0 }, m.Scenes[0].Nodes);
        var p = m.Meshes[0].Primitives.Single();
        Near(p.Positions, 1, 3, -2, 4, 3, -2, 1, 3, -6);            // (x, y, z) -> (x, z, -y)
        Near(p.Normals, 0, 1, 0, 0, 1, 0, 0, 1, 0);                 // +Z in 3ds Max is up
        Assert.Equal(new[] { 0, 1, 2 }, p.Indices); Assert.Null(p.Uv0);
        var hm = m.Materials[p.Material];
        Assert.Equal("Paint", hm.Name); Assert.True(hm.DoubleSided); Assert.Equal(-1, hm.BaseColorTexture);
        Near(MaxModel.Linear(0.5), hm.BaseColorFactor[0], 1e-9); Near(MaxModel.Linear(0.2f), hm.BaseColorFactor[1], 1e-9); Near(1, hm.BaseColorFactor[2], 1e-9);
        Near(0.21404114, MaxModel.Linear(0.5), 1e-7);
        Assert.Equal(1, rep.Parts); Assert.Equal(1, rep.Triangles); Assert.Equal(0, rep.PartsWithUv); Assert.Empty(rep.Notes);
        Near(3, rep.Size[0]); Near(0, rep.Size[1]); Near(4, rep.Size[2]);
    }

    [Fact]
    public void A_stored_quaternion_is_the_inverse_of_the_usual_one()
    {
        // a quarter turn about Z that takes +X to +Y is (0, 0, sin 45, cos 45) in the usual convention; the file holds its inverse
        float h = (float)Math.Sqrt(0.5);
        var s = new Sc(); int root = s.Root();
        s.Node("Turned", root, s.Prs(Id, new[] { 0, 0, -h, h }, One), s.Mesh(Tri, Face(0, 1, 2)));
        var p = Build(s, out _).Meshes[0].Primitives[0];
        Near(p.Positions, 0, 0, 0, 0, 0, -1, -1, 0, 0);            // (1,0,0) -> (0,1,0) -> GLB (0,0,-1); (0,1,0) -> (-1,0,0)
        Near(p.Normals, 0, 1, 0, 0, 1, 0, 0, 1, 0);
    }

    [Fact]
    public void A_mirrored_part_is_rewound_so_its_faces_still_face_out()
    {
        var s = new Sc(); int root = s.Root();
        s.Node("Mirror", root, s.Prs(Id, NoRot, new float[] { -1, 1, 1 }), s.Mesh(Tri, Face(0, 1, 2)));
        var p = Build(s, out _).Meshes[0].Primitives[0];
        // world: (0,0,0), (-1,0,0), (0,1,0); re-wound to v2, v1, v0
        Near(p.Positions, 0, 0, -1, -1, 0, 0, 0, 0, 0);
        Near(p.Normals, 0, 1, 0, 0, 1, 0, 0, 1, 0);                 // still up: a mirror in X does not turn a +Z face over
        Assert.Equal(new[] { 0, 1, 2 }, p.Indices);
    }

    [Fact]
    public void Children_follow_their_parents_and_the_object_offset_is_not_inherited()
    {
        var s = new Sc(); int root = s.Root();
        int parent = s.Node("P", root, s.Prs(new float[] { 10, 0, 0 }, NoRot, new float[] { 2, 2, 2 }), -1);
        int child = s.Node("C", parent, s.Prs(new float[] { 1, 0, 0 }, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), offsetPos: new float[] { 0, 0, 5 });
        s.Node("G", child, s.Prs(new float[] { 0, 1, 0 }, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        var m = Build(s, out var rep);
        Assert.Equal(new[] { "P", "C", "G" }, m.Nodes.Select(n => n.Name).ToArray());
        Assert.Equal(-1, m.Nodes[0].Parent); Assert.Equal(0, m.Nodes[1].Parent); Assert.Equal(1, m.Nodes[2].Parent);
        Assert.Equal(new[] { 1 }, m.Nodes[0].Children.ToArray()); Assert.Equal(-1, m.Nodes[0].Mesh);
        Assert.Equal(new double[] { 10, 0, 0 }, m.Nodes[0].Translation.Select(v => v + 0.0).ToArray());   // + 0.0: a negative zero is zero
        Assert.Equal(new double[] { 2, 0, 0 }, m.Nodes[1].Translation.Select(v => v + 0.0).ToArray());    // the parent's scale carries the child's place
        Assert.Equal(new double[] { 0, 0, -2 }, m.Nodes[2].Translation.Select(v => v + 0.0).ToArray());
        // C's vertices: scaled by the parent's 2, lifted by its own offset (2 * 5), relative to its pivot
        Near(m.Meshes[m.Nodes[1].Mesh].Primitives[0].Positions, 0, 10, 0, 2, 10, 0, 0, 10, -2);
        // G does not inherit C's offset
        Near(m.Meshes[m.Nodes[2].Mesh].Primitives[0].Positions, 0, 0, 0, 2, 0, 0, 0, 0, -2);
        Assert.Equal(2, rep.Parts);
    }

    [Fact]
    public void Smoothing_groups_decide_which_corners_share_a_normal()
    {
        // two triangles of equal area sharing the edge (0,0,0)-(0,1,0): one facing +Z, one facing +X
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        HafPrimitive Prim(int sgA, int sgB)
        {
            var s = new Sc(); int root = s.Root();
            s.Node("Fold", root, s.Prs(Id, NoRot, One), s.Mesh(verts, Face(0, 1, 2, sgA).Concat(Face(0, 2, 3, sgB)).ToArray()));
            return Build(s, out _).Meshes[0].Primitives.Single();
        }
        double d = Math.Sqrt(0.5);
        var smooth = Prim(1, 1);
        Assert.Equal(4, smooth.VertexCount);                          // the two shared corners are one vertex each
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, smooth.Indices);
        Near(smooth.Normals, d, d, 0, 0, 1, 0, d, d, 0, 1, 0, 0);     // shared: between +Z (up) and +X; the others their own face's
        var apart = Prim(1, 2);
        Assert.Equal(6, apart.VertexCount);
        Near(apart.Normals, 0, 1, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0);
        Assert.Equal(6, Prim(0, 0).VertexCount);                      // no group at all: flat
        Assert.Equal(4, Prim(3, 2).VertexCount);                      // one common bit is enough
    }

    [Fact]
    public void Hidden_faces_are_left_out_and_counted_and_a_broken_part_is_named_while_the_rest_is_read()
    {
        var s = new Sc(); int root = s.Root();
        s.Node("Good", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2).Concat(Face(0, 2, 1, flags: 7 | 8)).ToArray()));
        s.Node("Broken", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 9)));
        s.Node("AllHidden", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2, flags: 8)));
        var m = Build(s, out var rep);
        Assert.Single(m.Meshes); Assert.Equal(1, rep.Triangles); Assert.Equal(2, rep.HiddenFaces);
        Assert.Contains(rep.PartsLeft, l => l.Contains("'Broken'") && l.Contains("vertex 9 of 3"));
        Assert.Contains(rep.PartsLeft, l => l.Contains("'AllHidden'") && l.Contains("no visible faces"));
        Assert.Contains(rep.Notes, n => n.Contains("2 face(s) are marked hidden"));
    }

    [Fact]
    public void What_the_reader_does_not_read_is_named_and_a_file_without_a_mesh_is_refused()
    {
        var s = new Sc(); int root = s.Root();
        s.Node("Poly", root, s.Prs(Id, NoRot, One), s.Add("Editable Poly", 0x1BF8338D, 0x192F6098, 0x10));
        s.Node("Box01", root, s.Prs(Id, NoRot, One), s.Add("Box", 0x10, 0, 0x10));
        s.Node("Lamp", root, s.Prs(Id, NoRot, One), s.Add("Target Spot", 0x1012, 0, 0x30));
        var e = Assert.Throws<InvalidDataException>(() => Build(s, out _));
        Assert.Contains("no mesh this reader can read", e.Message); Assert.Contains("Editable Poly object is not read yet", e.Message); Assert.Contains("stored as its parameters", e.Message);

        // with one readable part beside them: read, and the rest named
        int mod = s.Add("Bend", 0x10, 0x20, 0x810);
        int baseMesh = s.Mesh(Tri, Face(0, 1, 2));
        s.Node("Bent", root, s.Add("Euler XYZ", 0x2012, 0, 0x900C), s.Derived(mod, baseMesh));
        var m = Build(s, out var rep, "t2.max");
        Assert.Single(m.Meshes); Assert.Equal(1, rep.ModifiersSkipped);
        Assert.Equal(2, rep.PartsLeft.Count); Assert.Equal(1, rep.NotGeometry["Target Spot"]);
        Assert.Contains(rep.Notes, n => n.Contains("1 modifier(s)") && n.Contains("BEFORE the modifier"));
        Assert.Contains(rep.Notes, n => n.Contains("'Bent' may be misplaced") && n.Contains("'Euler XYZ' is not read"));
        Assert.Contains("NOT READ (2)", rep.Summary());
    }

    // ---- materials and textures ---------------------------------------------------------------------------------------
    [Fact]
    public void Material_ids_pick_sub_materials_and_wrap_around()
    {
        var s = new Sc(); int root = s.Root();
        int multi = s.Multi(s.Std("Red", new[] { 1f, 0, 0 }), s.Std("Blue", new[] { 0, 0, 1f }));
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
        s.Node("Two", root, s.Prs(Id, NoRot, One), s.Mesh(verts, Face(0, 1, 2, mat: 1).Concat(Face(1, 3, 2, mat: 0)).Concat(Face(0, 1, 3, mat: 2)).ToArray()), multi);
        s.Node("Bare", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        var m = Build(s, out var rep);
        var prims = m.Meshes[0].Primitives;
        Assert.Equal(2, prims.Count);
        Assert.Equal("Blue", m.Materials[prims[0].Material].Name); Assert.Equal(1, prims[0].Indices.Length / 3);   // id 1
        Assert.Equal("Red", m.Materials[prims[1].Material].Name); Assert.Equal(2, prims[1].Indices.Length / 3);    // ids 0 and 2 (2 wraps around the two sub-materials to 0)
        Assert.Equal("NoMaterial", m.Materials[m.Meshes[1].Primitives[0].Material].Name);
        Assert.Equal(3, rep.Materials);
    }

    [Fact]
    public void A_tiled_picture_is_embedded_as_it_is_and_the_UVs_turn_upside_down()
    {
        string png = Png("tex.png", 1, 1, 10, 20, 30);
        var s = new Sc(); int root = s.Root();
        int mat = s.Std("Wood", new[] { 0.5f, 0.5f, 0.5f }, s.Bitmap(@"C:\old\disk\maps\TEX.PNG"));
        s.Node("Deck", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { 0, 0, 0, 1, 0, 0, 0.25f, 1, 0 }, new[] { 0, 1, 2 }), mat);
        var m = Build(s, out var rep);
        var p = m.Meshes[0].Primitives.Single();
        Near(p.Uv0, 0, 1, 1, 1, 0.25, 0);
        var hm = m.Materials[p.Material];
        Assert.Equal(new double[] { 1, 1, 1, 1 }, hm.BaseColorFactor);
        Assert.Equal(System.IO.File.ReadAllBytes(png), m.Images[m.Textures[hm.BaseColorTexture].Source].Bytes);
        Assert.Equal("image/png", m.Images[0].MimeType); Assert.Equal("tex", m.Images[0].Name);
        Assert.Equal(1, rep.PartsWithUv); Assert.Equal(1, rep.Images); Assert.Empty(rep.MissingTextures); Assert.Empty(rep.Notes);
    }

    [Fact]
    public void A_missing_picture_and_an_unread_map_fall_back_to_the_colour_by_name()
    {
        var s = new Sc(); int root = s.Root();
        int multi = s.Multi(s.Std("Gone", new[] { 1f, 1f, 1f }, s.Bitmap("nowhere.jpg")), s.Std("Cloud", new[] { 1f, 1f, 1f }, s.Noise()), s.Std("Glass", new[] { 0f, 1f, 0f }, opacity: 0.4f));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2, mat: 0).Concat(Face(0, 1, 2, mat: 1)).Concat(Face(0, 1, 2, mat: 2)).ToArray(), new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2, 0, 1, 2, 0, 1, 2 }), multi);
        var m = Build(s, out var rep);
        Assert.Empty(m.Images);
        // no picture is shown, and the UVs the file holds are still written (3 primitives: the missing picture, the
        // unread map, no map at all), V turned upside down - the picture can be put on later without converting again
        Assert.Equal(3, m.Meshes[0].Primitives.Count);
        Assert.All(m.Meshes[0].Primitives, p => { Assert.Equal(-1, m.Materials[p.Material].BaseColorTexture); Near(p.Uv0, 0, 1, 1, 1, 0, 0); });
        Assert.Equal(1, rep.PartsWithUv);
        Assert.Equal(new[] { "nowhere.jpg" }, rep.MissingTextures.ToArray());
        Assert.Contains(rep.Notes, n => n.Contains("'Cloud'") && n.Contains("'Noise' map is not read"));
        Assert.Contains(rep.Notes, n => n.Contains("'Glass' is 40 % opaque"));
        Assert.Contains("Textures not found beside the file", rep.Summary()); Assert.Contains("keep their UVs", rep.Summary()); Assert.EndsWith(": nowhere.jpg", rep.Summary().Split('\n').First(l => l.StartsWith("Textures not found")));
    }

    [Fact]
    public void A_decal_is_shown_once_on_a_canvas_of_the_plain_colour()
    {
        Png("flag.png", 2, 2, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0);        // all red
        Png("flagN.png", 2, 2, 255, 255, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0);    // top row white (shows the map), bottom row black
        var s = new Sc(); int root = s.Root();
        int mat = s.Std("Flag", new[] { 0f, 0f, 1f }, s.Mask(s.Bitmap("flag.png", false, false), s.Bitmap("flagN.png", false, false)));
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
        float[] tv = { -0.5f, -0.5f, 0, 1.5f, -0.5f, 0, -0.5f, 1.5f, 0, 2, 0, 0, 3, 0, 0, 2, 1, 0 };
        s.Node("Hull", root, s.Prs(Id, NoRot, One), s.Mesh(verts, Face(0, 1, 2).Concat(Face(1, 3, 2)).ToArray(), tv, new[] { 0, 1, 2, 3, 4, 5 }), mat);
        var m = Build(s, out var rep);
        var prims = m.Meshes[0].Primitives;
        Assert.Equal(2, prims.Count);
        Assert.Equal("Flag", m.Materials[prims[0].Material].Name); Assert.Equal("Flag_plain", m.Materials[prims[1].Material].Name);
        Assert.Equal(-1, m.Materials[prims[1].Material].BaseColorTexture);         // the face wholly beside the decal: colour only,
        Near(prims[1].Uv0, 2, 1, 3, 1, 2, 0);                                      // with its UVs as the file has them (V upside down), not the canvas's
        Near(MaxModel.Linear(1), m.Materials[prims[1].Material].BaseColorFactor[2], 1e-9);
        // the face on the decal reaches half a picture outside: one picture is added to each side (3 x 3)
        Near(prims[0].Uv0, 0.5 / 3, 2.5 / 3, 2.5 / 3, 2.5 / 3, 0.5 / 3, 0.5 / 3);
        var img = m.Images.Single(); Assert.Equal("flag_decal", img.Name); Assert.Equal("image/png", img.MimeType);
        var px = PngCodec.Decode(img.Bytes, out var info, out _);
        Assert.Equal(6, info.Width); Assert.Equal(6, info.Height);
        byte[] At(int x, int y) => new[] { px[(y * 6 + x) * 4], px[(y * 6 + x) * 4 + 1], px[(y * 6 + x) * 4 + 2] };
        Assert.Equal(new byte[] { 255, 0, 0 }, At(2, 2)); Assert.Equal(new byte[] { 255, 0, 0 }, At(3, 2));   // the map where the mask is white
        Assert.Equal(new byte[] { 0, 0, 255 }, At(2, 3));                                                    // the colour where it is black
        Assert.Equal(new byte[] { 0, 0, 255 }, At(0, 0)); Assert.Equal(new byte[] { 0, 0, 255 }, At(5, 5)); Assert.Equal(new byte[] { 0, 0, 255 }, At(4, 2));   // and around the decal
        Assert.Empty(rep.Notes);
    }

    [Fact]
    public void A_map_below_full_amount_mixes_with_the_colour_and_its_placement_is_folded_into_the_UVs()
    {
        Png("white.png", 1, 1, 255, 255, 255);
        var s = new Sc(); int root = s.Root();
        int mat = s.Std("Half", new[] { 0f, 0f, 0f }, s.Bitmap("white.png", true, true, 0.25f, 0, 2, 1), amount: 0.5f);
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }), mat);
        var m = Build(s, out var rep);
        var px = PngCodec.Decode(m.Images.Single().Bytes, out _, out _);
        Assert.Equal(new byte[] { 128, 128, 128 }, px.Take(3).ToArray());
        Assert.Equal("white_mixed", m.Images[0].Name);
        // u' = (u - 0.25 - 0.5) * 2 + 0.5; v unchanged, then turned upside down
        Near(m.Meshes[0].Primitives[0].Uv0, -1, 1, 1, 1, -1, 0);
        Assert.Contains(rep.Notes, n => n.Contains("'Half'") && n.Contains("offset/tiling"));
    }

    [Fact]
    public void A_JPEG_goes_through_the_callers_decoder_or_is_embedded_as_it_is_by_name()
    {
        string jpg = Path.Combine(dir, "pic.jpg");
        System.IO.File.WriteAllBytes(jpg, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });
        Sc Scene(float reach)
        {
            var s = new Sc(); int root = s.Root();
            int mat = s.Std("Decal", new[] { 0f, 1f, 0f }, s.Bitmap("pic.jpg", false, false));
            s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { 0, 0, 0, reach, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }), mat);
            return s;
        }
        var raw = Build(Scene(2), out var rep);                                   // the face reaches a whole picture beside the decal
        Assert.Equal("image/jpeg", raw.Images.Single().MimeType); Assert.Equal(System.IO.File.ReadAllBytes(jpg), raw.Images[0].Bytes);
        Assert.Contains(rep.Notes, n => n.Contains("could not be decoded") && n.Contains("REPEATS where 3ds Max shows it once"));
        Build(Scene(1), out var repOn, "t1.max");                                 // every face on the picture: nothing repeats, and the note does not say so
        Assert.Contains(repOn.Notes, n => n.Contains("could not be decoded")); Assert.DoesNotContain(repOn.Notes, n => n.Contains("REPEATS"));

        bool Dec(byte[] f, string n, out int w, out int h, out byte[] rgba) { w = 1; h = 1; rgba = new byte[] { 9, 8, 7, 255 }; return true; }
        var composed = Build(Scene(1), out var rep2, "t2.max", Dec);
        Assert.Equal("image/png", composed.Images.Single().MimeType);
        Assert.Equal(new byte[] { 9, 8, 7 }, PngCodec.Decode(composed.Images[0].Bytes, out _, out _).Take(3).ToArray());
        Assert.Empty(rep2.Notes);
    }

    [Fact]
    public void TGA_and_BMP_pictures_decode()
    {
        // TGA, true colour 24 bit, 2 x 1, rows bottom-up: blue-green-red in the file
        var tga = Cat(new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, U16(2), U16(1), new byte[] { 24, 0 }, new byte[] { 3, 2, 1, 6, 5, 4 });
        Assert.True(MaxImages.Tga(tga, out int w, out int h, out var px, out _));
        Assert.Equal((2, 1), (w, h)); Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, px);
        // TGA, run-length packed 32 bit, 2 x 2, rows top-down: a run of three pixels, then one raw pixel
        var rle = Cat(new byte[] { 0, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, U16(2), U16(2), new byte[] { 32, 0x28 }, new byte[] { 0x82, 30, 20, 10, 0, 0x00, 60, 50, 40, 0 });
        Assert.True(MaxImages.Tga(rle, out w, out h, out px, out _));
        Assert.Equal(new byte[] { 10, 20, 30, 255, 10, 20, 30, 255, 10, 20, 30, 255, 40, 50, 60, 255 }, px);   // alpha dropped: 3ds Max does not use it for a diffuse map
        Assert.False(MaxImages.Tga(rle.Take(20).ToArray(), out _, out _, out px, out string why)); Assert.Null(px); Assert.Contains("run past", why);
        // BMP, 24 bit, 2 x 2, rows bottom-up, each row padded to four bytes
        var bmp = Cat(new byte[] { (byte)'B', (byte)'M' }, U32(70), U32(0), U32(54), U32(40), U32(2), U32(2), U16(1), U16(24), U32(0), U32(16), U32(0), U32(0), U32(0), U32(0),
            new byte[] { 3, 2, 1, 6, 5, 4, 0, 0, 9, 8, 7, 12, 11, 10, 0, 0 });
        Assert.True(MaxImages.Decode(bmp, "x.bmp", null, out w, out h, out px, out _));
        Assert.Equal(new byte[] { 7, 8, 9, 255, 10, 11, 12, 255, 1, 2, 3, 255, 4, 5, 6, 255 }, px);
        Assert.False(MaxImages.Decode(new byte[] { 1, 2, 3 }, "x.tif", null, out _, out _, out px, out why)); Assert.Contains("no decoder", why);
    }

    // ---- the file as a whole ----------------------------------------------------------------------------------------
    [Fact]
    public void A_gzip_packed_scene_and_the_other_directory_names_read_the_same()
    {
        Sc Scene(bool gzip, string directory)
        {
            var s = new Sc { Gzip = gzip, Directory = directory }; int root = s.Root();
            s.Node("Plate", root, s.Prs(new float[] { 1, 2, 3 }, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), s.Std("M", new[] { 1f, 0, 0 }));
            return s;
        }
        var plain = Build(Scene(false, "ClassDirectory2"), out var rep);
        var packed = Build(Scene(true, "ClassDirectory3"), out var rep2, "t2.max");
        var old = Build(Scene(false, "ClassDirectory"), out _, "t3.max");
        Assert.False(rep.Compressed); Assert.True(rep2.Compressed);
        foreach (var m in new[] { packed, old })
        {
            Assert.Equal(plain.Meshes[0].Primitives[0].Positions, m.Meshes[0].Primitives[0].Positions);
            Assert.Equal(plain.Nodes[0].Translation, m.Nodes[0].Translation); Assert.Equal(plain.Materials[0].Name, m.Materials[0].Name);
        }
        var s2 = new Sc { Directory = "SomethingElse" }; s2.Root();
        System.IO.File.WriteAllBytes(Path.Combine(dir, "t4.max"), s2.File());
        Assert.Contains("no class directory stream", Assert.Throws<InvalidDataException>(() => MaxScene.Read(Path.Combine(dir, "t4.max"))).Message);
    }

    [Fact]
    public void The_conversion_writes_a_glb_that_reads_back_and_knows_its_own_files()
    {
        Png("tex.png", 1, 1, 10, 20, 30);
        var s = new Sc(); int root = s.Root();
        int mat = s.Std("Wood", new[] { 0.5f, 0.5f, 0.5f }, s.Bitmap("tex.png"));
        s.Node("Deck", root, s.Prs(new float[] { 5, 0, 0 }, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }), mat);
        string max = Path.Combine(dir, "Ship.max");
        System.IO.File.WriteAllBytes(max, s.File());
        string glb = MaxModel.DefaultGlbPath(max);
        Assert.EndsWith("/Ship_max.glb", glb);
        Assert.True(MaxModel.IsMax("a/B.MAX ")); Assert.False(MaxModel.IsMax("a/b.max.glb")); Assert.False(MaxModel.IsMax(null));
        Assert.False(MaxModel.IsOurs(glb));                                  // not there yet
        var rep = MaxModel.ConvertToGlb(max, glb, null);
        Assert.Equal(1, rep.Parts);
        var back = GlbReader.Read(glb);
        Assert.Equal(1, back.TriangleCount); Assert.Equal("Deck", back.Nodes[0].Name); Assert.Equal(new double[] { 5, 0, 0 }, back.Nodes[0].Translation.Select(v => v + 0.0).ToArray());
        Assert.Single(back.Images); Assert.Equal("Wood", back.Materials[0].Name);
        Assert.True(MaxModel.IsOurs(glb));
        string other = Path.Combine(dir, "other.glb");
        var plain = new HafModel(); plain.Nodes.Add(new HafNode { Name = "n" }); plain.Scenes.Add(new HafScene { Name = "Scene" }); plain.Scenes[0].Nodes.Add(0); plain.Scene = 0;
        GlbWriter.Write(plain, other);
        Assert.False(MaxModel.IsOurs(other));                                // a .glb somebody else wrote is never ours
        System.IO.File.WriteAllText(Path.Combine(dir, "junk.glb"), "not a glb");
        Assert.False(MaxModel.IsOurs(Path.Combine(dir, "junk.glb")));
    }

    // =================================================================================================================
    // Added after the review of 2026-11-21: each of these is a rule the reviewers changed in a copy of the reader with
    // all nineteen tests above still passing, or a defect they found. Expectations are worked out from the input and
    // the rule's statement, in the comment beside them.
    // =================================================================================================================
    static readonly float[] Uv3 = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };            // UV vertices A (0,0), B (1,0), C (0,1)

    [Fact]
    public void The_scale_s_axis_system_is_applied_to_unequal_factors()
    {
        // The factors (3, 1, 1) apply in an axis system turned 45 degrees about Z (+X towards +Y): the part is stretched
        // by 3 along d = (1,1,0)/sqrt 2 and not across it. (1,0,0) = (d + e)/sqrt 2 with e = (1,-1,0)/sqrt 2, so it lands
        // on (3d + e)/sqrt 2 = (2, 1, 0); (0,1,0) = (d - e)/sqrt 2 lands on (1, 2, 0). The file holds the inverse of the
        // usual quaternion (0, 0, sin 22.5, cos 22.5).
        float sn = (float)Math.Sin(Math.PI / 8), cs = (float)Math.Cos(Math.PI / 8);
        var s = new Sc(); int root = s.Root();
        s.Node("Sheared", root, s.Prs(Id, NoRot, new float[] { 3, 1, 1 }, new[] { 0, 0, -sn, cs }), s.Mesh(Tri, Face(0, 1, 2)));
        // the same through the object offset's scale (seven numbers there too)
        s.Node("ByOffset", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), offsetScale: new[] { 3, 1, 1, 0, 0, -sn, cs });
        // three equal factors: the axis system changes nothing
        s.Node("Even", root, s.Prs(Id, NoRot, new float[] { 2, 2, 2 }, new[] { 0, 0, -sn, cs }), s.Mesh(Tri, Face(0, 1, 2)));
        var m = Build(s, out _);
        Near(m.Meshes[0].Primitives[0].Positions, 0, 0, 0, 2, 0, -1, 1, 0, -2);      // (2,1,0) -> GLB (2, 0, -1); (1,2,0) -> (1, 0, -2)
        Near(m.Meshes[1].Primitives[0].Positions, 0, 0, 0, 2, 0, -1, 1, 0, -2);
        Near(m.Meshes[2].Primitives[0].Positions, 0, 0, 0, 2, 0, 0, 0, 0, -2);
    }

    [Fact]
    public void The_object_offset_turns_and_scales_the_geometry_about_the_pivot()
    {
        // offset = move (0,0,5) * quarter turn about Z (+X to +Y) * scale 2: (1,0,0) -> (2,0,0) -> (0,2,0) -> (0,2,5);
        // (0,1,0) -> (0,2,0) -> (-2,0,0) -> (-2,0,5); the origin -> (0,0,5). The pivot itself stays where the node is.
        float h = (float)Math.Sqrt(0.5);
        var s = new Sc(); int root = s.Root();
        s.Node("Off", root, s.Prs(new float[] { 7, 0, 0 }, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), offsetPos: new float[] { 0, 0, 5 }, offsetQuat: new[] { 0, 0, -h, h }, offsetScale: new float[] { 2, 2, 2 });
        var m = Build(s, out _);
        Assert.Equal(new double[] { 7, 0, 0 }, m.Nodes[0].Translation.Select(v => v + 0.0).ToArray());
        Near(m.Meshes[0].Primitives[0].Positions, 0, 5, 0, 0, 5, -2, -2, 5, 0);      // (x, y, z) -> (x, z, -y), relative to the pivot
    }

    [Fact]
    public void A_mirrored_part_keeps_each_corner_s_own_UV()
    {
        // mirrored in X the face is re-wound to corners 2, 1, 0 - and each corner still carries ITS UV: C, B, A
        var s = new Sc(); int root = s.Root();
        s.Node("Mirror", root, s.Prs(Id, NoRot, new float[] { -1, 1, 1 }), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }));
        var p = Build(s, out _).Meshes[0].Primitives[0];
        Near(p.Positions, 0, 0, -1, -1, 0, 0, 0, 0, 0);            // corners 2, 1, 0 in the world
        Near(p.Uv0, 0, 0, 1, 1, 0, 1);                             // C (0,1) -> (0, 0); B (1,0) -> (1, 1); A (0,0) -> (0, 1): V upside down
    }

    [Fact]
    public void A_face_after_a_hidden_one_takes_its_own_UV_face()
    {
        // the UV faces are stored per face of the FILE: the visible second face must take the second UV face (D, E, F),
        // not the first one because it is the first face kept
        float[] tv = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0.5f, 0.5f, 0, 0.75f, 0.5f, 0, 0.5f, 0.25f, 0 };
        var s = new Sc(); int root = s.Root();
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2, flags: 7 | 8).Concat(Face(0, 1, 2)).ToArray(), tv, new[] { 0, 1, 2, 3, 4, 5 }));
        var m = Build(s, out var rep);
        Assert.Equal(1, rep.HiddenFaces);
        Near(m.Meshes[0].Primitives.Single().Uv0, 0.5, 0.5, 0.75, 0.5, 0.5, 0.75);
    }

    [Fact]
    public void Two_sidedness_is_each_material_s_own()
    {
        var s = new Sc(); int root = s.Root();
        s.Node("A", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), s.Std("Sheet", new[] { 1f, 1f, 1f }, twoSided: true));
        s.Node("B", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), s.Std("Solid", new[] { 1f, 1f, 1f }));
        var m = Build(s, out _);
        Assert.True(m.Materials.Single(x => x.Name == "Sheet").DoubleSided); Assert.False(m.Materials.Single(x => x.Name == "Solid").DoubleSided);
    }

    [Fact]
    public void A_shared_normal_leans_towards_the_larger_face()
    {
        // a face of area 0.5 facing +Z and one of area 2 facing +X share an edge and a group: the sum of the two face
        // normals weighted by area is (4, 0, 1) twice over, so the shared corners look along (4, 0, 1)/sqrt 17 - in GLB
        // axes (4, 1, 0)/sqrt 17. Unweighted it would be (1, 1, 0)/sqrt 2.
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 4 };
        var s = new Sc(); int root = s.Root();
        s.Node("Fold", root, s.Prs(Id, NoRot, One), s.Mesh(verts, Face(0, 1, 2).Concat(Face(0, 2, 3)).ToArray()));
        var p = Build(s, out _).Meshes[0].Primitives.Single();
        double a = 4 / Math.Sqrt(17), b = 1 / Math.Sqrt(17);
        Near(p.Normals, a, b, 0, 0, 1, 0, a, b, 0, 1, 0, 0);
    }

    [Fact]
    public void A_sheet_with_both_its_sides_in_one_group_keeps_a_normal_per_side()
    {
        // front and back on the same three vertices, one smoothing group: the sum cancels, and each side keeps its own
        // normal (the first version handed the back face the front face's)
        var s = new Sc(); int root = s.Root();
        s.Node("Sheet", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2).Concat(Face(0, 2, 1)).ToArray()));
        var p = Build(s, out _).Meshes[0].Primitives.Single();
        Assert.Equal(6, p.VertexCount);
        Near(p.Normals, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, -1, 0, 0, -1, 0);
    }

    [Fact]
    public void A_picture_that_repeats_one_way_only_is_padded_the_other_way_only()
    {
        // tiling off in U, on in V; the face reaches half a picture beside it in U and three pictures up in V: one
        // picture is added to each side in U (canvas 6 x 2 from a 2 x 2 picture), V is left to repeat
        Png("stripe.png", 2, 2, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9);
        var s = new Sc(); int root = s.Root();
        int mat = s.Std("Stripe", new[] { 0f, 0f, 0f }, s.Bitmap("stripe.png", tileU: false, tileV: true));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { -0.5f, 0, 0, 1.5f, 0, 0, -0.5f, 3, 0 }, new[] { 0, 1, 2 }), mat);
        var m = Build(s, out _);
        PngCodec.Decode(m.Images.Single().Bytes, out var info, out _);
        Assert.Equal((6, 2), (info.Width, info.Height));
        Near(m.Meshes[0].Primitives[0].Uv0, 0.5 / 3, 1, 2.5 / 3, 1, 0.5 / 3, -2);     // u: (u + 1) / 3; v: 1 - v, untouched
    }

    [Fact]
    public void Pictures_are_found_one_folder_down_and_one_up_and_that_is_said()
    {
        Directory.CreateDirectory(Path.Combine(dir, "model", "maps"));
        System.IO.File.Copy(Png("up.png", 1, 1, 1, 2, 3), Path.Combine(dir, "up_copy.png"));     // dir is one folder above the model's
        System.IO.File.Move(Png("down.png", 1, 1, 4, 5, 6), Path.Combine(dir, "model", "maps", "down.png"));
        var s = new Sc(); int root = s.Root();
        int multi = s.Multi(s.Std("Up", new[] { 1f, 1f, 1f }, s.Bitmap("up.png")), s.Std("Down", new[] { 1f, 1f, 1f }, s.Bitmap("DOWN.PNG")));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2, mat: 0).Concat(Face(0, 1, 2, mat: 1)).ToArray(), Uv3, new[] { 0, 1, 2, 0, 1, 2 }), multi);
        var m = Build(s, out var rep, Path.Combine("model", "t.max"));
        Assert.Equal(2, m.Images.Count); Assert.Empty(rep.MissingTextures);
        Assert.Contains(rep.Notes, n => n.Contains("'up.png' is not beside the .max") && n.Contains("'..'"));
        Assert.Contains(rep.Notes, n => n.Contains("'DOWN.PNG' is not beside the .max") && n.Contains("'maps'"));
    }

    [Fact]
    public void A_list_controller_gives_its_first_entry_and_a_longer_list_is_said()
    {
        var s = new Sc(); int root = s.Root();
        s.Node("One", root, s.PrsOf(s.PositionList(s.Pos(10, 0, 0)), s.Rot(NoRot), s.Scl(One)), s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("Two", root, s.PrsOf(s.PositionList(s.Pos(20, 0, 0), s.Pos(0, 5, 0)), s.Rot(NoRot), s.Scl(One)), s.Mesh(Tri, Face(0, 1, 2)));
        var m = Build(s, out var rep);
        Assert.Equal(new double[] { 10, 0, 0 }, m.Nodes[0].Translation.Select(v => v + 0.0).ToArray());
        Assert.Equal(new double[] { 20, 0, 0 }, m.Nodes[1].Translation.Select(v => v + 0.0).ToArray());
        Assert.DoesNotContain(rep.Notes, n => n.Contains("'One'"));
        Assert.Contains(rep.Notes, n => n.Contains("'Two' may be misplaced") && n.Contains("list controller with 2 entries"));
        Assert.Equal(1, rep.PartsUnplaced);
    }

    [Fact]
    public void The_second_kind_of_derived_object_is_walked_to_its_base_object_too()
    {
        var s = new Sc(); int root = s.Root();
        int mod = s.Add("Ripple", 0x11, 0x20, 0x820);
        s.Node("Waved", root, s.Prs(Id, NoRot, One), s.WsmDerived(mod, s.Mesh(Tri, Face(0, 1, 2))));
        var m = Build(s, out var rep);
        Assert.Single(m.Meshes); Assert.Equal(1, rep.Triangles); Assert.Equal(1, rep.ModifiersSkipped);
    }

    [Fact]
    public void A_mask_of_another_size_is_stretched_over_its_map_and_a_mask_placed_differently_is_said()
    {
        // a 2 x 2 red map under a 4 x 2 mask whose left half is white: the map's left column shows, its right column
        // is the material's colour
        Png("m.png", 2, 2, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0);
        Png("mN.png", 4, 2, 255, 255, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0);
        var s = new Sc(); int root = s.Root();
        int mat = s.Std("Flag", new[] { 0f, 0f, 1f }, s.Mask(s.Bitmap("m.png", false, false), s.Bitmap("mN.png", false, false, offU: 0.5f)));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), mat);
        var m = Build(s, out var rep);
        var px = PngCodec.Decode(m.Images.Single().Bytes, out var info, out _);
        Assert.Equal((2, 2), (info.Width, info.Height));
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255, 255, 0, 0, 0, 0, 255 }, new[] { 0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14 }.Select(i => px[i]).ToArray());
        Assert.Contains(rep.Notes, n => n.Contains("'Flag'") && n.Contains("mask is placed differently"));
    }

    [Fact]
    public void A_face_far_beside_a_decal_is_held_at_the_canvas_edge_and_that_is_said()
    {
        // the face reaches ten pictures to the left and ten to the right; the canvas holds MaxPad (4) to each side, so
        // u is held at -4 and 5 and lands on the canvas's edges 0 and 1; u = 0.5 lands in its middle
        Png("d.png", 1, 1, 7, 7, 7);
        var s = new Sc(); int root = s.Root();
        int mat = s.Std("Wide", new[] { 0f, 0f, 0f }, s.Bitmap("d.png", false, false));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { -10, 0, 0, 11, 0, 0, 0.5f, 1, 0 }, new[] { 0, 1, 2 }), mat);
        var m = Build(s, out var rep);
        PngCodec.Decode(m.Images.Single().Bytes, out var info, out _);
        Assert.Equal((2 * MaxModel.MaxPad + 1, 1), (info.Width, info.Height));
        Near(m.Meshes[0].Primitives[0].Uv0, 0, 1, 1, 1, 0.5, 0);
        Assert.Contains(rep.Notes, n => n.Contains("'Wide'") && n.Contains("held at the canvas edge") && n.Contains("LARGER"));
    }

    [Fact]
    public void An_empty_sub_material_slot_does_not_move_the_ones_after_it()
    {
        // slots Red, (empty), Blue: id 2 is Blue and id 1 has no material - counted over the filled slots only, id 2 was Red
        var s = new Sc(); int root = s.Root();
        int multi = s.Multi(s.Std("Red", new[] { 1f, 0, 0 }), -1, s.Std("Blue", new[] { 0, 0, 1f }));
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(verts, Face(0, 1, 2, mat: 0).Concat(Face(1, 3, 2, mat: 1)).Concat(Face(0, 1, 3, mat: 2)).Concat(Face(0, 3, 2, mat: 3)).ToArray()), multi);
        var m = Build(s, out var rep);
        var names = m.Meshes[0].Primitives.Select(p => m.Materials[p.Material].Name + ":" + p.Indices.Length / 3).ToArray();
        Assert.Equal(new[] { "Red:2", "NoMaterial:1", "Blue:1" }, names);          // ids 0 and 3 (3 wraps around the THREE slots to 0), 1, 2
        Assert.Contains(rep.Notes, n => n.Contains("'P'") && n.Contains("no sub-material in slot 2"));
    }

    [Fact]
    public void Maps_in_the_other_slots_are_named_by_slot_and_a_colour_that_was_not_found_is_said()
    {
        var s = new Sc(); int root = s.Root();
        int holes = s.Std("Grille", new[] { 1f, 1f, 1f }, otherSlots: new[] { (6, s.Bitmap("holes.jpg")), (8, s.Noise()) });
        int bare = s.Add("Standard", 2, 0, 0xC00, Box(0x4000, Leaf(0x4001, Txt("Newer"))));       // no parameter block where this reader looks
        s.Node("A", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), holes);
        s.Node("B", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), bare);
        s.Node("C", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        Build(s, out var rep);
        Assert.Contains(rep.Notes, n => n.Contains("OPACITY map") && n.Contains("1 material(s): 'Grille' ('holes.jpg')"));
        Assert.Contains(rep.Notes, n => n.StartsWith("a bump map is not read on 1 material(s): 'Grille' (a Noise map)"));
        Assert.Contains(rep.Notes, n => n.Contains("'Newer'") && n.Contains("colour was not found"));
        Assert.DoesNotContain(rep.Notes, n => n.Contains("'Grille'") && n.Contains("colour was not found"));
        Assert.Contains(rep.Notes, n => n.StartsWith("1 part(s) have no material") && n.EndsWith(": C"));
    }

    [Fact]
    public void A_part_under_a_node_that_could_not_be_placed_says_so_and_the_report_opens_with_it()
    {
        var s = new Sc(); int root = s.Root();
        int aim = s.Node("Aim", root, s.Add("Look At", 0x2006, 0, 0x9008), -1);
        s.Node("Barrel", aim, s.Prs(new float[] { 1, 0, 0 }, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("Hull", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("Deck", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        Build(s, out var rep);
        Assert.Contains(rep.Notes, n => n.Contains("part 'Barrel' may be misplaced: the node above it, 'Aim'") && n.Contains("'Look At' is not read"));
        Assert.Equal(1, rep.PartsUnplaced);
        Assert.StartsWith("WARNING: 1 of 3 part(s) could NOT be placed", rep.Summary()); Assert.Contains("look at them", rep.Summary().Split('\n')[0]);
    }

    [Fact]
    public void The_same_conversion_twice_leaves_the_file_untouched_and_a_changed_scene_rewrites_it()
    {
        byte[] Scene(float x)
        {
            var s = new Sc(); int root = s.Root();
            s.Node("Deck", root, s.Prs(new float[] { x, 0, 0 }, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
            return s.File();
        }
        string max = Path.Combine(dir, "Ship.max"), glb = MaxModel.DefaultGlbPath(max);
        System.IO.File.WriteAllBytes(max, Scene(5));
        MaxModel.ConvertToGlb(max, glb, null, out bool unchanged);
        Assert.False(unchanged);
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        System.IO.File.SetLastWriteTimeUtc(glb, stamp);
        MaxModel.ConvertToGlb(max, glb, null, out unchanged);
        Assert.True(unchanged); Assert.Equal(stamp, System.IO.File.GetLastWriteTimeUtc(glb));      // not written again: a tool watching the file sees no change
        System.IO.File.WriteAllBytes(max, Scene(6));
        MaxModel.ConvertToGlb(max, glb, null, out unchanged);
        Assert.False(unchanged); Assert.NotEqual(stamp, System.IO.File.GetLastWriteTimeUtc(glb));
        Assert.Equal(new double[] { 6, 0, 0 }, GlbReader.Read(glb).Nodes[0].Translation.Select(v => v + 0.0).ToArray());
    }

    [Fact]
    public void A_container_that_claims_more_than_the_file_holds_is_refused_before_it_is_paid_for()
    {
        // more allocation sectors declared than the file has sectors
        var file = Cfb(("B", new byte[9000]));
        U32(100000).CopyTo(file, 0x2C);
        Assert.Contains("allocation sectors declared in a file of", Assert.Throws<InvalidDataException>(() => MaxDocument.Read(file)).Message);

        // six streams that all name one chain of 9,000 bytes: together more than the file holds
        var names = new[] { "S1", "S2", "S3", "S4", "S5", "S6" };
        file = Cfb(new[] { ("Big", new byte[9000]) }.Concat(names.Select(n => (n, new byte[] { 1 }))).ToArray());
        int At(string name) { var nb = Txt(name + "\0"); for (int i = 512; i + nb.Length <= file.Length; i += 128) if (file.Skip(i).Take(nb.Length).SequenceEqual(nb) && BitConverter.ToUInt16(file, i + 0x40) == nb.Length) return i; throw new Exception("no entry " + name); }
        int big = At("Big");
        foreach (var n in names) { int e = At(n); Buffer.BlockCopy(file, big + 0x74, file, e + 0x74, 4); U32(9000).CopyTo(file, e + 0x78); }
        Assert.True(7 * 9000 > file.Length);
        Assert.Contains("more bytes together than the file holds", Assert.Throws<InvalidDataException>(() => MaxDocument.Read(file)).Message);

        // a small stream that declares more than the mini stream holds
        file = Cfb(("Small", new byte[] { 1, 2, 3 }));                            // the mini stream is one mini sector of 64 bytes
        U32(200).CopyTo(file, At("Small") + 0x78);
        Assert.Contains("the mini stream holds", Assert.Throws<InvalidDataException>(() => MaxDocument.Read(file)).Message);

        // two entries of one name: the first is read, the second is not walked at all (its chain may be anything)
        file = Cfb(("Twin", new byte[] { 7 }), ("Twi2", new byte[] { 8 }));
        Txt("Twin").CopyTo(file, At("Twi2"));
        Assert.Equal(new byte[] { 7 }, MaxDocument.Read(file).Streams["Twin"]);
    }

    [Fact]
    public void One_bad_number_in_a_map_s_placement_or_in_a_UV_does_not_refuse_the_file()
    {
        Png("t.png", 1, 1, 1, 2, 3);
        var s = new Sc(); int root = s.Root();
        int bad = s.Std("Bad", new[] { 1f, 1f, 1f }, s.Bitmap("t.png", offV: float.NaN, tilU: float.PositiveInfinity));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), bad);
        s.Node("Q", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { 0, 0, 0, float.NaN, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }));
        s.Node("R", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2).Concat(Face(0, 2, 1)).ToArray(), Uv3, new[] { 0, 1, 2 }));   // two faces, one UV face
        var m = Build(s, out var rep);
        Assert.Equal(3, rep.Parts);
        Near(m.Meshes[0].Primitives[0].Uv0, 0, 1, 1, 1, 0, 0);                    // placed as if the map had no offset or tiling
        Assert.Contains(rep.Notes, n => n.Contains("'Bad'") && n.Contains("cannot be used"));
        Assert.Null(m.Meshes[1].Primitives[0].Uv0); Assert.Contains(rep.Notes, n => n.Contains("part 'Q' is read without UVs") && n.Contains("not a usable number"));
        Assert.Null(m.Meshes[2].Primitives[0].Uv0); Assert.Contains(rep.Notes, n => n.Contains("part 'R' is read without UVs") && n.Contains("2 faces need 24"));
        Assert.Equal(1, rep.PartsWithUv);
        GlbWriter.Write(m);                                                       // and the model can be written: no number in it is not finite
    }

    [Fact]
    public void A_long_chain_of_parents_is_read_children_first_and_a_real_cycle_is_refused()
    {
        // 5,000 nodes, each the child of the NEXT one in the file (so the first node read is the deepest), each one unit
        // along X from its parent: the first node stands at x = 5,000
        const int N = 5000;
        var s = new Sc(); int root = s.Root();
        int prs = s.Prs(new float[] { 1, 0, 0 }, NoRot, One), mesh = s.Mesh(Tri, Face(0, 1, 2));
        int first = s.Count;
        for (int i = 0; i < N; i++) s.Node("N" + i, i == N - 1 ? root : first + i + 1, prs, i == 0 ? mesh : -1);
        string path = Path.Combine(dir, "chain.max");
        System.IO.File.WriteAllBytes(path, s.File());
        var scene = MaxScene.Read(path);
        Assert.Equal(N, scene.Nodes.Count); Assert.Equal(N, scene.Nodes[0].World[3]); Assert.Equal(1, scene.Nodes[N - 1].World[3]);

        var c = new Sc(); c.Root();
        int cp = c.Prs(Id, NoRot, One), cm = c.Mesh(Tri, Face(0, 1, 2)), a = c.Count;
        c.Node("A", a + 1, cp, cm); c.Node("B", a, cp, -1);
        System.IO.File.WriteAllBytes(Path.Combine(dir, "cycle.max"), c.File());
        Assert.Contains("its own ancestor", Assert.Throws<InvalidDataException>(() => MaxScene.Read(Path.Combine(dir, "cycle.max"))).Message);
    }

    [Fact]
    public void The_dialog_s_report_names_twelve_unread_parts_and_counts_the_rest_and_the_Console_s_names_all()
    {
        var s = new Sc(); int root = s.Root();
        int box = s.Add("Box", 0x10, 0, 0x10);
        for (int i = 1; i <= 20; i++) s.Node("Crate" + i.ToString("00"), root, s.Prs(Id, NoRot, One), box);
        s.Node("Hull", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        Build(s, out var rep);
        Assert.Equal(20, rep.PartsLeft.Count);
        string dialog = rep.Summary(), console = rep.Summary(true);
        Assert.Contains("'Crate12'", dialog); Assert.DoesNotContain("'Crate13'", dialog); Assert.Contains("(8 more, all named in the Console)", dialog);
        for (int i = 1; i <= 20; i++) Assert.Contains("'Crate" + i.ToString("00") + "'", console);
    }

    [Fact]
    public void A_picture_s_own_alpha_is_not_used_and_that_is_said()
    {
        var px = new byte[] { 200, 0, 0, 255, 0, 200, 0, 0 };                      // 2 x 1: an opaque pixel and a transparent one
        System.IO.File.WriteAllBytes(Path.Combine(dir, "cut.png"), PngCodec.Encode(2, 1, px, true));
        var s = new Sc(); int root = s.Root();
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), s.Std("Cut", new[] { 0f, 0f, 1f }, s.Bitmap("cut.png", false, false)));
        var m = Build(s, out var rep);
        var o = PngCodec.Decode(m.Images.Single().Bytes, out _, out _);
        Assert.Equal(new byte[] { 200, 0, 0, 0, 200, 0 }, new[] { 0, 1, 2, 4, 5, 6 }.Select(i => o[i]).ToArray());   // both shown whole
        Assert.Contains(rep.Notes, n => n.Contains("'Cut'") && n.Contains("alpha channel of 'cut.png' is not used"));
    }
}
