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
        /// <summary>The pair form opens with the object's SLOT count (in the real files a node's is 6, a map table's 22), then (slot, index) pairs.</summary>
        static byte[] RefPairs(int slots, params (int slot, int index)[] r) => Leaf(0x2035, Cat(new[] { U32(slots) }.Concat(r.SelectMany(x => new[] { U32(x.slot), U32(x.index) })).ToArray()));

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
            int tm = Add("Texmaps", 0x1200, 0, 0x1080, RefPairs(22, pairs.ToArray()));
            return Add("Standard", 2, 0, 0xC00, RefArray(pb, tm), Box(0x4000, Leaf(0x4001, Txt(name))), Leaf(0x5000, U32(0x50000830u | (twoSided ? 8u : 0))));
        }
        /// <summary>A sub-material of -1 is an empty slot.</summary>
        public int Multi(params int[] subs) => Add("Multi/Sub-Object", 0x200, 0, 0xC00, RefArray(subs.Select(s => s < 0 ? (long)None : s).ToArray()));
        /// <summary>The same in the pair form: `slots` slots, of which only the named ones are filled.</summary>
        public int MultiPairs(int slots, params (int slot, int index)[] subs) => Add("Multi/Sub-Object", 0x200, 0, 0xC00, RefPairs(slots, subs));
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
        Assert.Contains(rep.Notes, n => n.StartsWith("1 part(s) may be misplaced") && n.Contains("'Euler XYZ' is not read") && n.EndsWith(": 'Bent'"));
        Assert.Contains(rep.Notes, n => n.StartsWith("1 modifier(s) sit on 1 part(s)") && n.EndsWith(": 'Bent'"));
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
        // no picture is shown. Three primitives: the faces whose picture is MISSING keep the UVs the file holds (V turned
        // upside down), so the picture can be put on later without converting again; the faces of an unread map and of
        // a material without a map show no picture and are written without UVs, by name
        Assert.Equal(3, m.Meshes[0].Primitives.Count);
        Assert.All(m.Meshes[0].Primitives, p => Assert.Equal(-1, m.Materials[p.Material].BaseColorTexture));
        Assert.Equal(new[] { "Gone:uv", "Cloud:-", "Glass:-" }, m.Meshes[0].Primitives.Select(p => m.Materials[p.Material].Name + (p.Uv0 != null ? ":uv" : ":-")).ToArray());
        Near(m.Meshes[0].Primitives[0].Uv0, 0, 1, 1, 1, 0, 0);
        Assert.Equal(1, rep.PartsWithUv); Assert.Equal(1, rep.PartsWithFileUv);
        Assert.Contains(rep.Notes, n => n.StartsWith("1 part(s) have UVs in the file on faces that show no picture") && n.EndsWith(": 'P'"));
        Assert.Equal(new[] { "nowhere.jpg" }, rep.MissingTextures.ToArray());
        Assert.Contains(rep.Notes, n => n.Contains("'Cloud'") && n.Contains("'Noise' map is not read"));
        Assert.Contains(rep.Notes, n => n.Contains("'Glass' is 40 % opaque"));
        Assert.Contains("Textures not found beside the file, one folder down or one up (faces that have UVs keep them", rep.Summary()); Assert.EndsWith(": nowhere.jpg (material 'Gone')", rep.Summary().Split('\n').First(l => l.StartsWith("Textures not found")));
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
        Assert.Null(prims[1].Uv0);                                                 // and no UVs - no picture is on it, and far-away UVs of one face move every other one's in the Factory's converter
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
        Assert.Contains("faces that show no picture", rep.Notes.Single());      // the one thing said: the face beside the decal is written without its UVs
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
        // TGA, run-length packed 32 bit, 2 x 2, rows top-down: a run of three pixels, then one raw pixel. Its alpha is zero
        // everywhere, which counts as no alpha at all (a declared alpha that is not zero everywhere is kept: see below)
        var rle = Cat(new byte[] { 0, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, U16(2), U16(2), new byte[] { 32, 0x28 }, new byte[] { 0x82, 30, 20, 10, 0, 0x00, 60, 50, 40, 0 });
        Assert.True(MaxImages.Tga(rle, out w, out h, out px, out _));
        Assert.Equal(new byte[] { 10, 20, 30, 255, 10, 20, 30, 255, 10, 20, 30, 255, 40, 50, 60, 255 }, px);
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
        s.Node("Mirror", root, s.Prs(Id, NoRot, new float[] { -1, 1, 1 }), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), s.Std("M", new[] { 1f, 1f, 1f }, s.Bitmap("gone.png")));
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
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2, flags: 7 | 8).Concat(Face(0, 1, 2)).ToArray(), tv, new[] { 0, 1, 2, 3, 4, 5 }), s.Std("M", new[] { 1f, 1f, 1f }, s.Bitmap("gone.png")));
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
        Assert.DoesNotContain(rep.Notes, n => n.Contains("misplaced") && n.Contains("'One'"));
        Assert.Contains(rep.Notes, n => n.StartsWith("1 part(s) may be misplaced") && n.Contains("list controller with 2 entries, of which only the first is read") && n.EndsWith(": 'Two'"));
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
        Assert.Contains(rep.Notes, n => n.StartsWith("1 part(s) have no material") && n.EndsWith(": 'C'"));
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
        Assert.Contains(rep.Notes, n => n == "1 part(s) may be misplaced - the node above, 'Aim': its transform controller 'Look At' is not read: 'Barrel'");
        Assert.Equal(1, rep.PartsUnplaced);
        Assert.StartsWith("WARNING: 1 of 3 part(s) may stand in the wrong place", rep.Summary()); Assert.Contains("look at them", rep.Summary().Split('\n')[0]);
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
    public void A_picture_s_own_alpha_lets_the_material_s_colour_show_and_that_is_said()
    {
        var px = new byte[] { 200, 0, 0, 255, 0, 200, 0, 0 };                      // 2 x 1: an opaque pixel and a transparent one
        System.IO.File.WriteAllBytes(Path.Combine(dir, "cut.png"), PngCodec.Encode(2, 1, px, true));
        var s = new Sc(); int root = s.Root();
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), s.Std("Cut", new[] { 0f, 0f, 1f }, s.Bitmap("cut.png", false, false)));
        var m = Build(s, out var rep);
        var o = PngCodec.Decode(m.Images.Single().Bytes, out _, out _);
        Assert.Equal(new byte[] { 200, 0, 0, 0, 0, 255 }, new[] { 0, 1, 2, 4, 5, 6 }.Select(i => o[i]).ToArray());   // the opaque pixel, then the material's blue where the picture is transparent
        Assert.Contains(rep.Notes, n => n.Contains("'Cut'") && n.Contains("'cut.png' has an alpha channel (50 % of it is not opaque)"));

        // the same picture where it REPEATS (tiling on, nothing else to compose): not embedded as it is - the transparent
        // pixel would then reach the tools as picture colour - but composed over the material's colour all the same
        var t = new Sc(); int troot = t.Root();
        t.Node("P", troot, t.Prs(Id, NoRot, One), t.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), t.Std("Cut", new[] { 0f, 0f, 1f }, t.Bitmap("cut.png")));
        var tiled = Build(t, out var trep, "t2.max");
        Assert.Equal("cut_mixed", tiled.Images.Single().Name);
        var to = PngCodec.Decode(tiled.Images[0].Bytes, out _, out _);
        Assert.Equal(new byte[] { 200, 0, 0, 0, 0, 255 }, new[] { 0, 1, 2, 4, 5, 6 }.Select(i => to[i]).ToArray());
        Assert.Contains(trep.Notes, n => n.Contains("has an alpha channel"));
        // and a PNG that has an alpha channel with nothing transparent in it is embedded as it is, without a word
        string solid = Path.Combine(dir, "solid.png");
        System.IO.File.WriteAllBytes(solid, PngCodec.Encode(2, 1, new byte[] { 200, 0, 0, 255, 0, 200, 0, 255 }, true));
        var u = new Sc(); int uroot = u.Root();
        u.Node("P", uroot, u.Prs(Id, NoRot, One), u.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), u.Std("Solid", new[] { 0f, 0f, 1f }, u.Bitmap("solid.png")));
        var whole = Build(u, out var urep, "t3.max");
        Assert.Equal(System.IO.File.ReadAllBytes(solid), whole.Images.Single().Bytes); Assert.Empty(urep.Notes);
    }

    [Fact]
    public void A_mirrored_map_repeats_and_is_said_and_a_UV_list_without_its_faces_is_said()
    {
        // the placement's flags with the tile bits clear and the mirror bits set (4 and 8): the map repeats, so it is no
        // decal - embedded as it is, UVs untouched - and the report says the mirroring is written as plain tiling
        string png = Png("mir.png", 1, 1, 10, 20, 30);
        byte[] Pm(int ix, float v) => Box(0x0002, Leaf(0x0003, U32(ix)), Leaf(0x0004), Leaf(0x0100, F(v)));
        var s = new Sc(); int root = s.Root();
        int pb = s.Add("ParamBlock", 8, 0, 8, Pm(0, 0), Pm(1, 0), Pm(2, 1), Pm(3, 1), Pm(4, 0));
        int pl = s.Add("Placement", 0x100, 0, 0xC20, Leaf(0x2034, U32(pb)), Leaf(0x9002, U32(0x5000000Cu)));
        int bm = s.Add("Bitmap", 0x240, 0, 0xC10, Leaf(0x2034, U32(pl)), Box(0x5010, Leaf(0x1230, Txt("mir.png"))));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { 0, 0, 0, 3, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }), s.Std("Mirror", new[] { 1f, 1f, 1f }, bm));
        // a part whose mesh has a UV list (0x0916) and no UV faces (0x0918)
        s.Node("Half", root, s.Prs(Id, NoRot, One), s.Add("Editable Mesh", 0xE44F10B3, 0, 0x10, Box(0x08FE,
            Leaf(0x0914, U32(3), F(Tri)), Leaf(0x0912, U32(1), U32(0), U32(1), U32(2), U32(1), U16(7), U16(0)), Leaf(0x0916, U32(3), F(Uv3)))));
        var m = Build(s, out var rep);
        Assert.Equal(System.IO.File.ReadAllBytes(png), m.Images.Single().Bytes); Assert.Equal("mir", m.Images[0].Name);
        Near(m.Meshes[0].Primitives[0].Uv0, 0, 1, 3, 1, 0, 0);
        Assert.Contains(rep.Notes, n => n.Contains("'Mirror'") && n.Contains("mirrored in 3ds Max") && n.Contains("plain tiling"));
        Assert.Null(m.Meshes[1].Primitives[0].Uv0);
        Assert.Contains(rep.Notes, n => n.Contains("part 'Half' is read without UVs") && n.Contains("a UV list and no UV faces"));
    }

    [Fact]
    public void Parts_without_a_material_are_counted_as_parts_also_when_they_share_a_name()
    {
        var s = new Sc(); int root = s.Root();
        for (int i = 0; i < 3; i++) s.Node("Box01", root, s.Prs(new float[] { i, 0, 0 }, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        Build(s, out var rep);
        Assert.Contains(rep.Notes, n => n == "3 part(s) have no material and are drawn in plain grey: 'Box01', 'Box01', 'Box01'");
    }

    // =================================================================================================================
    // Added in round 2 of the review: defects in round 1's own fixes, and rules whose planted defect still passed.
    // =================================================================================================================
    [Fact]
    public void Many_parts_that_could_not_be_placed_are_one_note_per_reason_not_one_per_part_and_ancestor()
    {
        // 300 parts, each the child of the one before, each on a controller that is not read. A note per part and
        // ancestor would be 45,150 notes; the nearest reason per part, grouped, is one.
        const int N = 300;
        var s = new Sc(); int root = s.Root();
        int look = s.Add("Look At", 0x2006, 0, 0x9008), mesh = s.Mesh(Tri, Face(0, 1, 2)), parent = root;
        for (int i = 0; i < N; i++) parent = s.Node("L" + i, parent, look, mesh);
        Build(s, out var rep);
        Assert.Equal(N, rep.Parts); Assert.Equal(N, rep.PartsUnplaced);
        var said = rep.Notes.Where(n => n.Contains("misplaced")).ToArray();
        Assert.Single(said);
        Assert.StartsWith("300 part(s) may be misplaced - its transform controller 'Look At' is not read: 'L0', 'L1', ", said[0]); Assert.EndsWith(", 'L299'", said[0]);
        Assert.True(rep.Notes.Count < 10, rep.Notes.Count + " notes");
    }

    [Fact]
    public void A_part_that_is_left_out_is_not_also_counted_as_misplaced()
    {
        // two parts on an unread controller under a node standing at 1e31: they do not fit a GLB's float and are left
        // out by name - the WARNING counts parts of the MODEL, and the one part written is placed
        var s = new Sc(); int root = s.Root();
        int far = s.Node("Far", root, s.Prs(new float[] { 1e31f, 0, 0 }, NoRot, One), -1);
        int look = s.Add("Look At", 0x2006, 0, 0x9008);
        s.Node("A", far, look, s.Mesh(Tri, Face(0, 1, 2))); s.Node("B", far, look, s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("Good", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        Build(s, out var rep);
        Assert.Equal(1, rep.Parts); Assert.Equal(0, rep.PartsUnplaced); Assert.DoesNotContain("WARNING", rep.Summary());
        Assert.Equal(2, rep.PartsLeft.Count(l => l.Contains("its place, or that of a node above it, does not fit a 32-bit float")));
        Assert.DoesNotContain(rep.Notes, n => n.Contains("misplaced"));
    }

    [Fact]
    public void A_sheet_with_several_triangles_a_side_keeps_a_normal_per_side_too()
    {
        // a planar quad, both its sides in one group, turned and scaled so that nothing in it is a round number: at a
        // corner the four face normals cancel to a rounding residue, not to zero - and a residue has a direction
        var s = new Sc(); int root = s.Root();
        float[] quad = { 0, 0, 0, 1, 0, 0.3f, 1, 1, 0.7f, 0, 1, 0.4f };            // on the plane z = 0.3 x + 0.4 y
        var faces = Face(0, 1, 2).Concat(Face(0, 2, 3)).Concat(Face(0, 2, 1)).Concat(Face(0, 3, 2)).ToArray();
        s.Node("Sheet", root, s.Prs(new float[] { 0.1f, 0.2f, 0.3f }, new[] { 0.1825742f, 0.3651484f, 0.5477226f, 0.7302967f }, new float[] { 0.3f, 0.7f, 1.1f }), s.Mesh(quad, faces));
        var p = Build(s, out _).Meshes[0].Primitives.Single();
        for (int t = 0; t < p.Indices.Length; t += 3)
        {
            int a = p.Indices[t] * 3, b = p.Indices[t + 1] * 3, c = p.Indices[t + 2] * 3;
            double ux = p.Positions[b] - p.Positions[a], uy = p.Positions[b + 1] - p.Positions[a + 1], uz = p.Positions[b + 2] - p.Positions[a + 2];
            double vx = p.Positions[c] - p.Positions[a], vy = p.Positions[c + 1] - p.Positions[a + 1], vz = p.Positions[c + 2] - p.Positions[a + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            foreach (int k in new[] { a, b, c })
            {
                double dot = (p.Normals[k] * nx + p.Normals[k + 1] * ny + p.Normals[k + 2] * nz) / len;
                Assert.True(dot > 0.999, $"triangle {t / 3}: a corner's normal is at {dot:0.000} to its own face");
            }
        }
    }

    [Fact]
    public void Material_ids_count_the_declared_slots_in_the_pair_form_of_the_references()
    {
        // three slots declared, the first two filled: id 2 has no material and id 3 wraps to slot 0 - counted over the
        // largest slot named, ids 2 and 3 wrapped over two slots (Red, Green)
        var s = new Sc(); int root = s.Root();
        int multi = s.MultiPairs(3, (0, s.Std("Red", new[] { 1f, 0, 0 })), (1, s.Std("Green", new[] { 0, 1f, 0 })));
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(verts, Face(0, 1, 2, mat: 0).Concat(Face(1, 3, 2, mat: 1)).Concat(Face(0, 1, 3, mat: 2)).Concat(Face(0, 3, 2, mat: 3)).ToArray()), multi);
        var m = Build(s, out var rep);
        Assert.Equal(new[] { "Red:2", "Green:1", "NoMaterial:1" }, m.Meshes[0].Primitives.Select(p => m.Materials[p.Material].Name + ":" + p.Indices.Length / 3).ToArray());
        Assert.Contains(rep.Notes, n => n.Contains("no sub-material in slot 3"));
    }

    [Fact]
    public void Only_the_streams_the_reader_reads_are_unpacked_and_a_stream_that_unpacks_too_far_is_refused()
    {
        byte[] Gz(byte[] d) { using (var ms = new MemoryStream()) { using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(d, 0, d.Length); return ms.ToArray(); } }
        var packed = Gz(Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray());
        var doc = MaxDocument.Read(Cfb(("Scene", Gz(Box(0x2003, Leaf(0x11, U32(5))))), ("Other", packed)));
        Assert.True(doc.Compressed);
        Assert.Equal(5u, doc.Chunks("Scene")[0].First(0x11).U32(0));             // the Scene stream: unpacked
        Assert.Equal(packed, doc.Streams["Other"]);                               // a stream of another name: as the file has it
        // 65 MB of zeros pack to a few KB: past both bounds (64 MB, and 200 times the packed size)
        var bomb = Gz(new byte[65 << 20]);
        Assert.True((long)bomb.Length * MaxDocument.MaxUnpackRatio < MaxDocument.UnpackFloor);
        var e = Assert.Throws<InvalidDataException>(() => MaxDocument.Read(Cfb(("Scene", bomb))));
        Assert.Contains("unpacks to more than 64 MB", e.Message);
    }

    [Fact]
    public void A_map_s_offset_and_tiling_reach_the_UVs_and_the_report_also_when_its_picture_is_missing()
    {
        // u' = (u - 0.25 - 0.5) * 2 + 0.5: A (0,0) -> -1, B (1,0) -> 1, C (0,1) -> -1; v unchanged, then upside down
        var s = new Sc(); int root = s.Root();
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), s.Std("Lost", new[] { 1f, 1f, 1f }, s.Bitmap("gone.png", true, true, 0.25f, 0, 2, 1)));
        var m = Build(s, out var rep);
        Assert.Empty(m.Images); Near(m.Meshes[0].Primitives[0].Uv0, -1, 1, 1, 1, -1, 0);
        Assert.Contains(rep.Notes, n => n.Contains("'Lost'") && n.Contains("offset/tiling (0.25, 0 / 2, 1)"));
    }

    [Fact]
    public void The_axis_system_counts_whenever_any_two_factors_differ()
    {
        // factors (1, 1, 3) in an axis system turned 45 degrees about X (+Y towards +Z): the stretch of 3 runs along
        // d = (0, -1, 1)/sqrt 2. (0,1,0) has -1/sqrt 2 of it, so it moves by 2 * (-1/sqrt 2) * d = (0, 1, -1) to (0, 2, -1);
        // (1,0,0) has none of it and stays. Only the first two factors compared, the axis would be skipped.
        float sn = (float)Math.Sin(Math.PI / 8), cs = (float)Math.Cos(Math.PI / 8);
        var s = new Sc(); int root = s.Root();
        s.Node("P", root, s.Prs(Id, NoRot, new float[] { 1, 1, 3 }, new[] { -sn, 0, 0, cs }), s.Mesh(Tri, Face(0, 1, 2)));
        Near(Build(s, out _).Meshes[0].Primitives[0].Positions, 0, 0, 0, 1, 0, 0, 0, -1, -2);      // (0, 2, -1) -> GLB (0, -1, -2)
    }

    [Fact]
    public void Small_things_the_plants_showed_unheld()
    {
        // a UV beyond any picture's range is no placement: the part is read without UVs, by name
        var s = new Sc(); int root = s.Root();
        s.Node("Wild", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { 0, 0, 0, 2e6f, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }));
        // a material reference that names an object the scene does not have
        s.Node("Dangling", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), material: 9999);
        // an amount a hair under 100 %: the picture is embedded as it is, not mixed
        string png = Png("near.png", 1, 1, 10, 20, 30);
        s.Node("Near", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), s.Std("Near", new[] { 0f, 0f, 0f }, s.Bitmap("near.png"), amount: 0.999f));
        var m = Build(s, out var rep);
        Assert.Null(m.Meshes[0].Primitives[0].Uv0); Assert.Contains(rep.Notes, n => n.Contains("part 'Wild' is read without UVs") && n.Contains("not a usable number"));
        Assert.Contains(rep.Notes, n => n.StartsWith("1 reference(s) in the file name an object the scene does not have"));
        Assert.Equal(System.IO.File.ReadAllBytes(png), m.Images.Single().Bytes); Assert.Equal("near", m.Images[0].Name);

        // more chunks than the reader builds a tree for
        var many = new byte[(MaxDocument.MaxChunks + 1) * 6];
        for (int i = 0; i < many.Length; i += 6) many[i + 2] = 6;                  // id 0, size 6: an empty leaf
        Assert.Contains("more than " + MaxDocument.MaxChunks + " chunks", Assert.Throws<InvalidDataException>(() => MaxDocument.ParseChunks(many)).Message);

        // two entries of one name, the second with a chain that leaves the file: it is not walked, so the file reads
        var file = Cfb(("Twin", new byte[9000]), ("Twi2", new byte[9000]));
        int second = -1; var nb = Txt("Twi2\0");
        for (int i = 512; i + nb.Length <= file.Length; i += 128) if (file.Skip(i).Take(nb.Length).SequenceEqual(nb)) second = i;
        Txt("Twin").CopyTo(file, second); U32(0x00FFFFF0).CopyTo(file, second + 0x74);
        Assert.Equal(9000, MaxDocument.Read(file).Streams["Twin"].Length);
    }

    [Fact]
    public void A_TGA_s_alpha_is_kept_when_its_header_declares_one_and_shows_the_material_s_colour()
    {
        // 32 bit, 2 x 1, rows bottom-up, header: 8 alpha bits. Blue-green-red-alpha in the file.
        var tga = Cat(new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, U16(2), U16(1), new byte[] { 32, 8 }, new byte[] { 3, 2, 1, 255, 6, 5, 4, 0 });
        Assert.True(MaxImages.Tga(tga, out _, out _, out var px, out _));
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 0 }, px);
        tga[17] = 0;                                                               // the same bytes, no alpha bits declared: opaque
        Assert.True(MaxImages.Tga(tga, out _, out _, out px, out _));
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, px);
        tga[17] = 8; tga[21] = 0;                                                  // alpha zero everywhere: never written, opaque
        Assert.True(MaxImages.Tga(tga, out _, out _, out px, out _));
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, px);

        // on a material, tiling on: the transparent pixel shows the material's colour and the report says how much
        tga[21] = 255;
        System.IO.File.WriteAllBytes(Path.Combine(dir, "hull.tga"), tga);
        var s = new Sc(); int root = s.Root();
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), s.Std("Hull", new[] { 0f, 1f, 0f }, s.Bitmap("hull.tga")));
        var m = Build(s, out var rep);
        var o = PngCodec.Decode(m.Images.Single().Bytes, out var info, out _);
        Assert.Equal((2, 1), (info.Width, info.Height)); Assert.Equal("hull_mixed", m.Images[0].Name);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 255, 0 }, new[] { 0, 1, 2, 4, 5, 6 }.Select(i => o[i]).ToArray());
        Near(m.Meshes[0].Primitives[0].Uv0, 0, 1, 1, 1, 0, 0);                    // it repeats: the UVs are not moved onto a canvas
        Assert.Contains(rep.Notes, n => n.Contains("'Hull'") && n.Contains("'hull.tga' has an alpha channel (50 % of it is not opaque)"));
    }

    [Fact]
    public void A_mask_taken_from_another_folder_is_said_too()
    {
        Directory.CreateDirectory(Path.Combine(dir, "model", "maps"));
        System.IO.File.Move(Png("m.png", 1, 1, 255, 0, 0), Path.Combine(dir, "model", "m.png"));
        System.IO.File.Move(Png("mN.png", 1, 1, 255, 255, 255), Path.Combine(dir, "model", "maps", "mN.png"));
        var s = new Sc(); int root = s.Root();
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), s.Std("Flag", new[] { 0f, 0f, 1f }, s.Mask(s.Bitmap("m.png", false, false), s.Bitmap("mN.png", false, false))));
        Build(s, out var rep, Path.Combine("model", "t.max"));
        Assert.Contains(rep.Notes, n => n.Contains("'mN.png' is not beside the .max") && n.Contains("'maps'"));
        Assert.DoesNotContain(rep.Notes, n => n.Contains("'m.png' is not beside"));
    }

    // =================================================================================================================
    // Fixtures from round 2's planting reviewer: 141 rules of the reader were changed one at a time against the tests
    // as they stood after round 1, and 59 changes passed all of them (the drill caught one of the 59). Each fixture
    // below holds one or more of those rules; its expectation is worked out from the rule's statement, in the comment
    // beside it. 30 of the reviewer's 31 fixtures are here, two of them adapted to what round 2 changed.
    // =================================================================================================================
    static byte[] RefsOf(params long[] r) => Leaf(0x2034, r.Select(U32).ToArray());
    string Raw(string rel, byte r, byte g, byte b)
    {
        string p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p));
        System.IO.File.WriteAllBytes(p, PngCodec.Encode(1, 1, new byte[] { r, g, b, 255 }, false)); return p;
    }

    [Fact]
    public void A_picture_beside_wins_over_one_folder_down_which_wins_over_one_folder_up()
    {
        // the search order is: beside the .max, then one folder down, then one folder up; the first of a name wins
        string beside = Raw(Path.Combine("model", "a.png"), 1, 1, 1); Raw(Path.Combine("model", "maps", "a.png"), 2, 2, 2); Raw("a.png", 3, 3, 3);
        string down = Raw(Path.Combine("model", "maps", "b.png"), 4, 4, 4); Raw("b.png", 5, 5, 5);
        var s = new Sc(); int root = s.Root();
        int multi = s.Multi(s.Std("A", new[] { 1f, 1f, 1f }, s.Bitmap("a.png")), s.Std("B", new[] { 1f, 1f, 1f }, s.Bitmap("b.png")));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2, mat: 0).Concat(Face(0, 1, 2, mat: 1)).ToArray(), Uv3, new[] { 0, 1, 2, 0, 1, 2 }), multi);
        var m = Build(s, out var rep, Path.Combine("model", "t.max"));
        Assert.Equal(System.IO.File.ReadAllBytes(beside), m.Images.Single(i => i.Name == "a").Bytes);
        Assert.Equal(System.IO.File.ReadAllBytes(down), m.Images.Single(i => i.Name == "b").Bytes);
        Assert.DoesNotContain(rep.Notes, n => n.Contains("'a.png' is not beside"));
        Assert.Contains(rep.Notes, n => n.Contains("'b.png' is not beside the .max") && n.Contains("'maps'"));
    }

    [Fact]
    public void Rotation_and_scale_lists_give_their_first_entry_too()
    {
        // a list controller gives its first entry: a Rotation List holding the quarter turn of the quaternion test, a
        // Scale List holding a scale of 2 - the same positions as those controllers give directly
        float h = (float)Math.Sqrt(0.5);
        var s = new Sc(); int root = s.Root();
        int rot = s.Rot(0, 0, -h, h), rl = s.Add("Rotation List", 0x4B4B1003, 0, 0x900C, RefsOf(rot));
        int scl = s.Scl(new float[] { 2, 2, 2 }), sl = s.Add("Scale List", 0x4B4B1004, 0, 0x900D, RefsOf(scl));
        s.Node("Turned", root, s.PrsOf(s.Pos(0, 0, 0), rl, s.Scl(One)), s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("Grown", root, s.PrsOf(s.Pos(0, 0, 0), s.Rot(NoRot), sl), s.Mesh(Tri, Face(0, 1, 2)));
        var m = Build(s, out var rep);
        Near(m.Meshes[0].Primitives[0].Positions, 0, 0, 0, 0, 0, -1, -1, 0, 0);
        Near(m.Meshes[1].Primitives[0].Positions, 0, 0, 0, 2, 0, 0, 0, 0, -2);
        Assert.Equal(0, rep.PartsUnplaced);
    }

    [Fact]
    public void A_face_far_above_and_below_a_decal_is_held_at_the_canvas_edge_in_V_too()
    {
        // v reaches ten pictures below and ten above; the canvas holds MaxPad (4) to each side, so (rows running down)
        // 1 - v is held at -4 and 5 and lands on the canvas's edges 0 and 1; v = 0.5 lands in its middle. u stays 0..1.
        Png("dv.png", 1, 1, 7, 7, 7);
        var s = new Sc(); int root = s.Root();
        int mat = s.Std("Tall", new[] { 0f, 0f, 0f }, s.Bitmap("dv.png", false, false));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { 0, -10, 0, 0, 11, 0, 1, 0.5f, 0 }, new[] { 0, 1, 2 }), mat);
        var m = Build(s, out var rep);
        PngCodec.Decode(m.Images.Single().Bytes, out var info, out _);
        Assert.Equal((1, 2 * MaxModel.MaxPad + 1), (info.Width, info.Height));
        Near(m.Meshes[0].Primitives[0].Uv0, 0, 1, 0, 0, 1, 0.5);
        Assert.Contains(rep.Notes, n => n.Contains("'Tall'") && n.Contains("held at the canvas edge"));
    }

    [Fact]
    public void A_mask_placed_differently_in_any_one_setting_is_said()
    {
        Png("m.png", 2, 2, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0); Png("mN.png", 2, 2, 255, 255, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0);
        MaxReport With(Func<Sc, int> mask)
        {
            var s = new Sc(); int root = s.Root();
            int mat = s.Std("Flag", new[] { 0f, 0f, 1f }, s.Mask(s.Bitmap("m.png", false, false), mask(s)));
            s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), mat);
            Build(s, out var rep, "m" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".max"); return rep;
        }
        Assert.DoesNotContain(With(s => s.Bitmap("mN.png", false, false)).Notes, n => n.Contains("placed differently"));     // the control
        Assert.Contains(With(s => s.Bitmap("mN.png", true, false)).Notes, n => n.Contains("placed differently"));           // tile U
        Assert.Contains(With(s => s.Bitmap("mN.png", false, true)).Notes, n => n.Contains("placed differently"));           // tile V
        Assert.Contains(With(s => s.Bitmap("mN.png", false, false, offV: 0.5f)).Notes, n => n.Contains("placed differently"));
        Assert.Contains(With(s => s.Bitmap("mN.png", false, false, tilU: 2)).Notes, n => n.Contains("placed differently"));
        Assert.Contains(With(s => s.Bitmap("mN.png", false, false, tilV: 2)).Notes, n => n.Contains("placed differently"));
    }

    [Fact]
    public void A_reference_past_the_object_list_is_read_as_none_and_counted_once()
    {
        // a Multi/Sub-Object of two slots whose second names object 9999: ids 0 and 2 draw with Red, ids 1 and 3 have no
        // material; the file holds ONE such reference, however often the material is looked at (four ids here)
        var s = new Sc(); int root = s.Root();
        int red = s.Std("Red", new[] { 1f, 0, 0 }), multi = s.Add("Multi/Sub-Object", 0x200, 0, 0xC00, RefsOf(red, 9999));
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(verts, Face(0, 1, 2, mat: 0).Concat(Face(1, 3, 2, mat: 1)).Concat(Face(0, 1, 3, mat: 2)).Concat(Face(0, 3, 2, mat: 3)).ToArray()), multi);
        var m = Build(s, out var rep);
        Assert.Equal(new[] { "Red:2", "NoMaterial:2" }, m.Meshes[0].Primitives.Select(p => m.Materials[p.Material].Name + ":" + p.Indices.Length / 3).ToArray());
        Assert.Contains(rep.Notes, n => n.StartsWith("1 reference(s) in the file name an object the scene does not have"));
    }

    [Fact]
    public void A_mask_s_unusable_placement_is_said_too()
    {
        Png("m.png", 1, 1, 255, 0, 0); Png("mN.png", 1, 1, 255, 255, 255);
        var s = new Sc(); int root = s.Root();
        int mat = s.Std("Flag", new[] { 0f, 0f, 1f }, s.Mask(s.Bitmap("m.png", false, false), s.Bitmap("mN.png", false, false, offU: float.NaN)));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), mat);
        Build(s, out var rep);
        Assert.Contains(rep.Notes, n => n.Contains("'Flag'") && n.Contains("cannot be used"));
    }

    [Fact]
    public void More_than_half_the_parts_unplaced_says_the_model_is_not_assembled()
    {
        // two of three parts hang on a controller that is not read: more than half
        var s = new Sc(); int root = s.Root();
        s.Node("A", root, s.Add("Look At", 0x2006, 0, 0x9008), s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("B", root, s.Add("Look At", 0x2006, 0, 0x9008), s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("C", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        Build(s, out var rep);
        Assert.Equal(2, rep.PartsUnplaced);
        Assert.StartsWith("WARNING: 2 of 3 part(s)", rep.Summary()); Assert.Contains("most likely not assembled", rep.Summary().Split('\n')[0]);
    }

    [Fact]
    public void A_picture_no_face_has_UVs_for_is_not_embedded_and_that_is_said()
    {
        Png("tex.png", 1, 1, 10, 20, 30);
        var s = new Sc(); int root = s.Root();
        s.Node("Bare", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), s.Std("Wood", new[] { 0.5f, 0.5f, 0.5f }, s.Bitmap("tex.png")));
        var m = Build(s, out var rep);
        Assert.Empty(m.Images); Assert.Equal("Wood", m.Materials.Single().Name);
        Assert.Contains(rep.Notes, n => n.Contains("'Wood'") && n.Contains("'tex.png' is not embedded") && n.Contains("has UVs"));
    }

    [Fact]
    public void A_map_turned_by_an_angle_is_said_and_not_applied()
    {
        Png("ang.png", 1, 1, 10, 20, 30);
        byte[] Pm(int ix, float v) => Box(0x0002, Leaf(0x0003, U32(ix)), Leaf(0x0004), Leaf(0x0100, F(v)));
        var s = new Sc(); int root = s.Root();
        int pb = s.Add("ParamBlock", 8, 0, 8, Pm(0, 0), Pm(1, 0), Pm(2, 1), Pm(3, 1), Pm(4, 0.5f));
        int pl = s.Add("Placement", 0x100, 0, 0xC20, RefsOf(pb), Leaf(0x9002, U32(0x50000003u)));
        int bm = s.Add("Bitmap", 0x240, 0, 0xC10, RefsOf(pl), Box(0x5010, Leaf(0x1230, Txt("ang.png"))));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), s.Std("Turned", new[] { 1f, 1f, 1f }, bm));
        var m = Build(s, out var rep);
        Near(m.Meshes[0].Primitives[0].Uv0, 0, 1, 1, 1, 0, 0);
        Assert.Contains(rep.Notes, n => n.Contains("'Turned'") && n.Contains("turned by an angle"));
    }

    [Fact]
    public void A_mask_that_is_missing_or_of_another_class_is_said_and_the_map_is_shown_whole()
    {
        string png = Png("m.png", 1, 1, 255, 0, 0);
        var s = new Sc(); int root = s.Root();
        int multi = s.Multi(s.Std("Gone", new[] { 0f, 0f, 1f }, s.Mask(s.Bitmap("m.png"), s.Bitmap("gone.png"))), s.Std("Cloud", new[] { 0f, 0f, 1f }, s.Mask(s.Bitmap("m.png"), s.Noise())));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2, mat: 0).Concat(Face(0, 1, 2, mat: 1)).ToArray(), Uv3, new[] { 0, 1, 2, 0, 1, 2 }), multi);
        var m = Build(s, out var rep);
        Assert.Equal(System.IO.File.ReadAllBytes(png), m.Images.Single().Bytes);        // the map as it is, once
        Assert.Equal(new[] { "gone.png" }, rep.MissingTextures.ToArray());
        Assert.Contains(rep.Notes, n => n.Contains("'Gone'") && n.Contains("mask picture is missing"));
        Assert.Contains(rep.Notes, n => n.Contains("'Cloud'") && n.Contains("its mask is a 'Noise' map"));
    }

    [Fact]
    public void Chunks_nested_sixty_four_deep_read_and_one_deeper_is_refused()
    {
        byte[] Nest(int n) { var b = new byte[0]; for (int i = 0; i < n; i++) b = Box(1, b); return b; }
        MaxDocument.ParseChunks(Nest(MaxDocument.MaxDepth));                        // 64 containers inside each other read
        Assert.Contains("nested deeper", Assert.Throws<InvalidDataException>(() => MaxDocument.ParseChunks(Nest(MaxDocument.MaxDepth + 1))).Message);
    }

    [Fact]
    public void Allocation_sectors_listed_in_a_DIFAT_sector_are_read()
    {
        // the writer lists its one allocation sector in the header; here it is moved to a DIFAT sector appended to the
        // file (the header's list left empty), as a file of more than 109 allocation sectors lists the rest
        var data = Enumerable.Range(0, 9000).Select(i => (byte)(i * 13 + 1)).ToArray();
        var file = Cfb(("B", data));
        uint fatSect = BitConverter.ToUInt32(file, 0x4C);
        int newSect = (file.Length - 512) / 512;
        var difat = new byte[512];
        for (int i = 0; i < 512; i += 4) U32(0xFFFFFFFF).CopyTo(difat, i);
        U32(fatSect).CopyTo(difat, 0); U32(0xFFFFFFFE).CopyTo(difat, 508);
        U32(0xFFFFFFFF).CopyTo(file, 0x4C); U32(newSect).CopyTo(file, 0x44); U32(1).CopyTo(file, 0x48);
        Assert.Equal(data, MaxDocument.Read(Cat(file, difat)).Streams["B"]);
    }

    [Fact]
    public void A_container_of_4096_byte_sectors_is_read()
    {
        // version 4: sectors of 4,096 bytes (the header fills the first), 64-bit stream sizes. Sectors 0-2 hold the
        // stream, 3 the directory, 4 the allocation table.
        const int SS = 4096; const uint End = 0xFFFFFFFE, Free = 0xFFFFFFFF;
        var data = Enumerable.Range(0, 9000).Select(i => (byte)(i * 7 + 3)).ToArray();
        var f = new byte[SS * 6];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(f, 0);
        U16(0x3E).CopyTo(f, 0x18); U16(4).CopyTo(f, 0x1A); U16(0xFFFE).CopyTo(f, 0x1C); U16(12).CopyTo(f, 0x1E); U16(6).CopyTo(f, 0x20);
        U32(1).CopyTo(f, 0x28); U32(1).CopyTo(f, 0x2C); U32(3).CopyTo(f, 0x30); U32(4096).CopyTo(f, 0x38);
        U32(End).CopyTo(f, 0x3C); U32(0).CopyTo(f, 0x40); U32(End).CopyTo(f, 0x44); U32(0).CopyTo(f, 0x48);
        for (int i = 0; i < 109; i++) U32(i == 0 ? 4 : Free).CopyTo(f, 0x4C + 4 * i);
        Buffer.BlockCopy(data, 0, f, SS, data.Length);
        void Entry(int at, string name, int type, uint start, long size)
        {
            var nb = Encoding.Unicode.GetBytes(name); Buffer.BlockCopy(nb, 0, f, at, nb.Length);
            U16(nb.Length + 2).CopyTo(f, at + 0x40); f[at + 0x42] = (byte)type; f[at + 0x43] = 1;
            for (int k = 0x44; k <= 0x4C; k += 4) U32(Free).CopyTo(f, at + k);
            U32(start).CopyTo(f, at + 0x74); BitConverter.GetBytes(size).CopyTo(f, at + 0x78);
        }
        Entry(SS * 4, "Root Entry", 5, End, 0); Entry(SS * 4 + 128, "B", 2, 0, data.Length);
        uint[] fat = { 1, 2, End, End, 0xFFFFFFFD };
        for (int i = 0; i < SS / 4; i++) U32(i < fat.Length ? fat[i] : Free).CopyTo(f, SS * 5 + 4 * i);
        Assert.Equal(data, MaxDocument.Read(f).Streams["B"]);
    }

    [Fact]
    public void Each_bad_number_alone_is_held()
    {
        Png("t.png", 1, 1, 1, 2, 3);
        var s = new Sc(); int root = s.Root();
        int nan = s.Std("NanOffset", new[] { 1f, 1f, 1f }, s.Bitmap("t.png", offV: float.NaN));
        int inf = s.Std("InfTiling", new[] { 1f, 1f, 1f }, s.Bitmap("t.png", tilU: float.PositiveInfinity));
        s.Node("A", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), nan);
        s.Node("B", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), inf);
        // a UV that is a number but far past any picture (MaxUv is 1e6)
        s.Node("Wild", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { 0, 0, 0, 1e30f, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }));
        // a vertex that is no number: the part is named, the rest is read
        s.Node("NoNumber", root, s.Prs(Id, NoRot, One), s.Mesh(new float[] { 0, 0, 0, float.NaN, 0, 0, 0, 1, 0 }, Face(0, 1, 2)));
        var m = Build(s, out var rep);
        Assert.Equal(3, rep.Parts);
        Near(m.Meshes[0].Primitives[0].Uv0, 0, 1, 1, 1, 0, 0); Near(m.Meshes[1].Primitives[0].Uv0, 0, 1, 1, 1, 0, 0);     // placed as if the map had no offset or tiling
        Assert.Contains(rep.Notes, n => n.Contains("'NanOffset'") && n.Contains("cannot be used")); Assert.Contains(rep.Notes, n => n.Contains("'InfTiling'") && n.Contains("cannot be used"));
        Assert.Null(m.Meshes[2].Primitives[0].Uv0); Assert.Contains(rep.Notes, n => n.Contains("part 'Wild' is read without UVs") && n.Contains("not a usable number"));
        Assert.Contains(rep.PartsLeft, l => l.Contains("'NoNumber'") && l.Contains("a vertex is not a finite number"));
        GlbWriter.Write(m);
    }

    [Fact]
    public void The_size_spans_the_parts_where_their_pivots_stand()
    {
        // P1 at the origin reaches x 0..1, y 0..5; P2 stands at (10, 10, 0) and reaches x 10..11, y 10..11:
        // the model is 11 wide (x), 0 high (z) and 11 long (y)
        var s = new Sc(); int root = s.Root();
        s.Node("P1", root, s.Prs(Id, NoRot, One), s.Mesh(new float[] { 0, 0, 0, 1, 0, 0, 0, 5, 0 }, Face(0, 1, 2)));
        s.Node("P2", root, s.Prs(new float[] { 10, 10, 0 }, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        Build(s, out var rep);
        Near(11, rep.Size[0]); Near(0, rep.Size[1]); Near(11, rep.Size[2]);
    }

    [Fact]
    public void A_map_s_V_offset_and_V_tiling_are_folded_into_the_UVs()
    {
        Png("t.png", 1, 1, 1, 2, 3);
        var s = new Sc(); int root = s.Root();
        // v' = v - 0.25, written upside down as 1 - v': 1.25 for v = 0, 0.25 for v = 1
        s.Node("Moved", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), s.Std("Moved", new[] { 1f, 1f, 1f }, s.Bitmap("t.png", offV: 0.25f)));
        // v' = (v - 0.5) * 2 + 0.5 (about the centre): -0.5 for v = 0, 1.5 for v = 1; written 1.5 and -0.5
        s.Node("Tiled", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), s.Std("Tiled", new[] { 1f, 1f, 1f }, s.Bitmap("t.png", tilV: 2)));
        var m = Build(s, out _);
        Near(m.Meshes[0].Primitives[0].Uv0, 0, 1.25, 1, 1.25, 0, 0.25);
        Near(m.Meshes[1].Primitives[0].Uv0, 0, 1.5, 1, 1.5, 0, -0.5);
    }

    [Fact]
    public void A_face_above_a_decal_shows_the_colour_only_and_does_not_widen_the_canvas()
    {
        Png("d.png", 1, 1, 255, 0, 0);
        var s = new Sc(); int root = s.Root();
        int mat = s.Std("D", new[] { 0f, 0f, 1f }, s.Bitmap("d.png", false, false));
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
        float[] tv = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 2, 0, 1, 2, 0, 0, 3, 0 };     // the second face lies two pictures ABOVE the decal
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(verts, Face(0, 1, 2).Concat(Face(1, 3, 2)).ToArray(), tv, new[] { 0, 1, 2, 3, 4, 5 }), mat);
        var m = Build(s, out _);
        Assert.Equal(new[] { "D:1", "D_plain:1" }, m.Meshes[0].Primitives.Select(p => m.Materials[p.Material].Name + ":" + p.Indices.Length / 3).ToArray());
        PngCodec.Decode(m.Images.Single().Bytes, out var info, out _);
        Assert.Equal((1, 1), (info.Width, info.Height));
    }

    [Fact]
    public void A_part_is_scaled_before_it_is_turned_and_a_quaternion_s_length_does_not_count()
    {
        // Translate * Rotate * Scale: (1,0,0) is stretched to (2,0,0) and THEN turned a quarter about Z onto (0,2,0);
        // (0,1,0) stays and is turned onto (-1,0,0). The quaternion is the quarter turn's, twice as long.
        var s = new Sc(); int root = s.Root();
        s.Node("P", root, s.Prs(Id, new float[] { 0, 0, -1, 1 }, new float[] { 2, 1, 1 }), s.Mesh(Tri, Face(0, 1, 2)));
        Near(Build(s, out _).Meshes[0].Primitives[0].Positions, 0, 0, 0, 0, 0, -2, -1, 0, 0);
    }

    [Fact]
    public void A_bitmap_without_a_name_is_said_and_a_path_with_forward_slashes_gives_its_file_name()
    {
        string png = Png("tex.png", 1, 1, 10, 20, 30);
        var s = new Sc(); int root = s.Root();
        int multi = s.Multi(s.Std("Nameless", new[] { 1f, 1f, 1f }, s.Bitmap("")), s.Std("Slashed", new[] { 1f, 1f, 1f }, s.Bitmap("C:/old/disk/maps/tex.png")));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2, mat: 0).Concat(Face(0, 1, 2, mat: 1)).ToArray(), Uv3, new[] { 0, 1, 2, 0, 1, 2 }), multi);
        var m = Build(s, out var rep);
        Assert.Empty(rep.MissingTextures);
        Assert.Contains(rep.Notes, n => n.Contains("'Nameless'") && n.Contains("names no file"));
        Assert.Equal(System.IO.File.ReadAllBytes(png), m.Images.Single().Bytes);
    }

    [Fact]
    public void The_decal_canvas_stays_within_MaxCanvas_and_a_hair_over_the_edge_adds_no_picture()
    {
        // a picture 1,024 wide whose face reaches four pictures to each side: nine pictures are 9,216 pixels, over
        // MaxCanvas (8,192), so three are added to each side (7,168); u is held at -3 and 4
        Png("wide.png", 1024, 1, Enumerable.Repeat((byte)7, 1024 * 3).ToArray());
        Png("small.png", 2, 2, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9);
        var s = new Sc(); int root = s.Root();
        s.Node("W", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { -4, 0, 0, 5, 0, 0, 0.5f, 1, 0 }, new[] { 0, 1, 2 }), s.Std("Wide", new[] { 0f, 0f, 0f }, s.Bitmap("wide.png", false, false)));
        // 0.00005 of a picture past the edge is the edge (the hair is 1e-4): no picture is added and nothing is said
        s.Node("H", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { 0, 0, 0, 1.00005f, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }), s.Std("Hair", new[] { 0f, 0f, 0f }, s.Bitmap("small.png", false, false)));
        var m = Build(s, out var rep);
        PngCodec.Decode(m.Images.Single(i => i.Name == "wide_decal").Bytes, out var wide, out _); PngCodec.Decode(m.Images.Single(i => i.Name == "small_decal").Bytes, out var small, out _);
        Assert.Equal((7168, 1), (wide.Width, wide.Height));
        Near(m.Meshes[0].Primitives[0].Uv0, 0, 1, 1, 1, 0.5, 0);
        Assert.Contains(rep.Notes, n => n.Contains("'Wide'") && n.Contains("held at the canvas edge"));
        Assert.Equal((2, 2), (small.Width, small.Height));
        Assert.DoesNotContain(rep.Notes, n => n.Contains("'Hair'"));
    }

    [Fact]
    public void One_picture_used_by_two_materials_is_embedded_once()
    {
        Png("tex.png", 1, 1, 10, 20, 30); Png("dec.png", 1, 1, 40, 50, 60);
        var s = new Sc(); int root = s.Root();
        int multi = s.Multi(s.Std("A", new[] { 1f, 1f, 1f }, s.Bitmap("tex.png")), s.Std("B", new[] { 1f, 1f, 1f }, s.Bitmap("TEX.PNG")),
            s.Std("C", new[] { 0f, 0f, 1f }, s.Bitmap("dec.png", false, false)), s.Std("D", new[] { 0f, 0f, 1f }, s.Bitmap("dec.png", false, false)));
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2, mat: 0).Concat(Face(0, 1, 2, mat: 1)).Concat(Face(0, 1, 2, mat: 2)).Concat(Face(0, 1, 2, mat: 3)).ToArray(), Uv3, new[] { 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2 }), multi);
        var m = Build(s, out _);
        Assert.Equal(new[] { "tex", "dec_decal" }, m.Images.Select(i => i.Name).ToArray());
        Assert.Equal(4, m.Meshes[0].Primitives.Count);
    }

    [Fact]
    public void A_node_that_names_itself_as_its_parent_is_a_root()
    {
        var s = new Sc(); int root = s.Root();
        int prs = s.Prs(new float[] { 3, 0, 0 }, NoRot, One), mesh = s.Mesh(Tri, Face(0, 1, 2));
        s.Node("Self", s.Count, prs, mesh);                                        // its parent field holds its own index
        var m = Build(s, out _);
        Assert.Equal(new[] { 0 }, m.Scenes[0].Nodes); Assert.Equal(new double[] { 3, 0, 0 }, m.Nodes[0].Translation.Select(v => v + 0.0).ToArray());
    }

    [Fact]
    public void A_Multi_Sub_inside_a_Multi_Sub_takes_its_first_sub_material()
    {
        // THE READER'S RULE (not checked against 3ds Max): the id picks the outer slot, the inner one starts at 0.
        // id 3 -> outer slot 0 of three -> the inner material's slot 0 = Red (the id carried on would give 3 % 2 = Blue)
        var s = new Sc(); int root = s.Root();
        int inner = s.Multi(s.Std("Red", new[] { 1f, 0, 0 }), s.Std("Blue", new[] { 0, 0, 1f })), green = s.Std("Green", new[] { 0, 1f, 0 });
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2, mat: 3)), s.Multi(inner, green, green));
        var m = Build(s, out _);
        Assert.Equal("Red", m.Materials[m.Meshes[0].Primitives.Single().Material].Name);
    }

    [Fact]
    public void A_scale_of_three_numbers_without_an_axis_system_is_read()
    {
        var s = new Sc(); int root = s.Root();
        int sc = s.Add("Bezier Scale", 0x2010, 0, 0x900D, Leaf(0x2505, F(2, 3, 4)));
        s.Node("Ctrl", root, s.PrsOf(s.Pos(0, 0, 0), s.Rot(NoRot), sc), s.Mesh(Tri, Face(0, 1, 2)));
        int prs = s.Prs(Id, NoRot, One), mesh = s.Mesh(Tri, Face(0, 1, 2));
        s.Add("Node", 1, 0, 1, RefsOf(prs, mesh, None, None), Leaf(0x0960, U32(root), U32(0)), Leaf(0x0962, Txt("Off")), Leaf(0x096C, F(2, 2, 2)));
        var m = Build(s, out var rep);
        Near(m.Meshes[0].Primitives[0].Positions, 0, 0, 0, 2, 0, 0, 0, 0, -3);       // (1,0,0) -> (2,0,0); (0,1,0) -> (0,3,0) -> GLB (0, 0, -3)
        Near(m.Meshes[1].Primitives[0].Positions, 0, 0, 0, 2, 0, 0, 0, 0, -2);
        Assert.Equal(0, rep.PartsUnplaced);
    }

    [Fact]
    public void A_target_is_nothing_to_draw_and_a_part_scaled_to_nothing_is_named()
    {
        var s = new Sc(); int root = s.Root();
        s.Node("Aim", root, s.Prs(Id, NoRot, One), s.Add("Target", 0x1020, 0, 0x10));
        s.Node("Flat", root, s.Prs(Id, NoRot, new float[] { 1, 1, 0 }), s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("Hull", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        Build(s, out var rep);
        Assert.Equal(1, rep.Parts); Assert.Equal(1, rep.NotGeometry["Target"]);
        Assert.Single(rep.PartsLeft); Assert.Contains(rep.PartsLeft, l => l.Contains("'Flat'") && l.Contains("flattens"));
    }

    [Fact]
    public void A_part_that_cannot_be_written_is_named_and_the_rest_is_read()
    {
        var s = new Sc(); int root = s.Root();
        s.Node("BadUv", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 7 }));
        s.Node("NoPlace", root, s.Prs(new float[] { float.NaN, 0, 0 }, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("TooFar", root, s.Prs(Id, NoRot, new float[] { 10, 10, 10 }), s.Mesh(new float[] { 1e38f, 0, 0, 0, 1e38f, 0, 0, 0, 1e38f }, Face(0, 1, 2)));
        s.Node("Hull", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        var m = Build(s, out var rep);
        Assert.Equal(1, rep.Parts); Assert.Equal(3, rep.PartsLeft.Count);
        Assert.Contains(rep.PartsLeft, l => l.Contains("'BadUv'") && l.Contains("UV vertex 7 of 3"));
        Assert.Contains(rep.PartsLeft, l => l.Contains("'NoPlace'") && l.Contains("not a finite number"));
        Assert.Contains(rep.PartsLeft, l => l.Contains("'TooFar'") && l.Contains("do not fit a 32-bit float"));
        GlbWriter.Write(m);
    }

    [Fact]
    public void Two_flat_faces_in_one_plane_share_their_corners()
    {
        // a square of two triangles without a smoothing group: the two shared corners have one normal, so four vertices
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
        var s = new Sc(); int root = s.Root();
        s.Node("Quad", root, s.Prs(Id, NoRot, One), s.Mesh(verts, Face(0, 1, 2, 0).Concat(Face(1, 3, 2, 0)).ToArray()));
        Assert.Equal(4, Build(s, out _).Meshes[0].Primitives.Single().VertexCount);
    }

    [Fact]
    public void The_other_TGA_and_BMP_layouts_decode()
    {
        // 59a: TGA 24 bit, 2 x 1, stored right to left: the first pixel of the file is the right one
        var rtl = Cat(new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, U16(2), U16(1), new byte[] { 24, 0x10 }, new byte[] { 3, 2, 1, 6, 5, 4 });
        Assert.True(MaxImages.Tga(rtl, out _, out _, out var px, out _)); Assert.Equal(new byte[] { 4, 5, 6, 255, 1, 2, 3, 255 }, px);
        // 59b: TGA 16 bit, 1 x 1: five bits each, red in bits 10-14 - 0x7C00 is full red
        var hi = Cat(new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, U16(1), U16(1), new byte[] { 16, 0 }, new byte[] { 0x00, 0x7C });
        Assert.True(MaxImages.Tga(hi, out _, out _, out px, out _)); Assert.Equal(new byte[] { 255, 0, 0, 255 }, px);
        // 59c: TGA colour-mapped, a map of two 24-bit entries whose first is index 10: pixel 11 is the second entry
        var map = Cat(new byte[] { 0, 1, 1 }, U16(10), U16(2), new byte[] { 24, 0, 0, 0, 0 }, U16(1), U16(1), new byte[] { 8, 0 }, new byte[] { 3, 2, 1, 6, 5, 4 }, new byte[] { 11 });
        Assert.True(MaxImages.Tga(map, out _, out _, out px, out _)); Assert.Equal(new byte[] { 4, 5, 6, 255 }, px);
        // 59d: TGA grey, 8 bit, 2 x 1
        var grey = Cat(new byte[] { 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, U16(2), U16(1), new byte[] { 8, 0 }, new byte[] { 7, 9 });
        Assert.True(MaxImages.Tga(grey, out _, out _, out px, out _)); Assert.Equal(new byte[] { 7, 7, 7, 255, 9, 9, 9, 255 }, px);
        // 59e: BMP 8 bit, 1 x 1, a palette of two (blue, green, red, 0): pixel 0 is the first entry
        var pal = Cat(new byte[] { (byte)'B', (byte)'M' }, U32(66), U32(0), U32(62), U32(40), U32(1), U32(1), U16(1), U16(8), U32(0), U32(4), U32(0), U32(0), U32(2), U32(0),
            new byte[] { 3, 2, 1, 0, 9, 9, 9, 0 }, new byte[] { 0, 0, 0, 0 });
        Assert.True(MaxImages.Bmp(pal, out _, out _, out px, out _)); Assert.Equal(new byte[] { 1, 2, 3, 255 }, px);
        // 59f: BMP 24 bit, 1 x 2 with a NEGATIVE height: the first row of the file is the top row
        var top = Cat(new byte[] { (byte)'B', (byte)'M' }, U32(62), U32(0), U32(54), U32(40), U32(1), BitConverter.GetBytes(-2), U16(1), U16(24), U32(0), U32(8), U32(0), U32(0), U32(0), U32(0),
            new byte[] { 3, 2, 1, 0, 6, 5, 4, 0 });
        Assert.True(MaxImages.Bmp(top, out _, out int h, out px, out _)); Assert.Equal(2, h); Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, px);
    }

    [Fact]
    public void A_material_is_matte_and_a_textured_one_keeps_its_two_sides()
    {
        Png("tex.png", 1, 1, 10, 20, 30);
        var s = new Sc(); int root = s.Root();
        s.Node("A", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), Uv3, new[] { 0, 1, 2 }), s.Std("Sheet", new[] { 1f, 1f, 1f }, s.Bitmap("tex.png"), twoSided: true));
        s.Node("B", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), s.Std("Paint", new[] { 1f, 1f, 1f }));
        var m = Build(s, out _);
        var sheet = m.Materials.Single(x => x.Name == "Sheet"); var paint = m.Materials.Single(x => x.Name == "Paint");
        Assert.True(sheet.BaseColorTexture >= 0); Assert.True(sheet.DoubleSided);
        Near(0.5, sheet.RoughnessFactor); Near(0.5, paint.RoughnessFactor); Near(0, sheet.MetallicFactor); Near(0, paint.MetallicFactor);
    }

    [Fact]
    public void Sub_materials_named_by_slot_pairs_count_their_empty_slots()
    {
        // the pair form of a reference list: slot 0 = Red, slot 2 = Blue, slot 1 named by nobody. Three slots:
        // id 0 Red, id 1 nothing, id 2 Blue, id 3 wraps to Red
        var s = new Sc(); int root = s.Root();
        int red = s.Std("Red", new[] { 1f, 0, 0 }), blue = s.Std("Blue", new[] { 0, 0, 1f });
        int multi = s.Add("Multi/Sub-Object", 0x200, 0, 0xC00, Leaf(0x2035, U32(3), U32(0), U32(red), U32(2), U32(blue)));
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(verts, Face(0, 1, 2, mat: 0).Concat(Face(1, 3, 2, mat: 1)).Concat(Face(0, 1, 3, mat: 2)).Concat(Face(0, 3, 2, mat: 3)).ToArray()), multi);
        var m = Build(s, out _);
        Assert.Equal(new[] { "Red:2", "NoMaterial:1", "Blue:1" }, m.Meshes[0].Primitives.Select(p => m.Materials[p.Material].Name + ":" + p.Indices.Length / 3).ToArray());
        // and a list that declares FEWER slots than it names (two, with slot 2 named) still has the slot it names
        var t = new Sc(); int troot = t.Root();
        int tred = t.Std("Red", new[] { 1f, 0, 0 }), tblue = t.Std("Blue", new[] { 0, 0, 1f });
        int tmulti = t.Add("Multi/Sub-Object", 0x200, 0, 0xC00, Leaf(0x2035, U32(2), U32(0), U32(tred), U32(2), U32(tblue)));
        t.Node("P", troot, t.Prs(Id, NoRot, One), t.Mesh(verts, Face(0, 1, 2, mat: 0).Concat(Face(1, 3, 2, mat: 1)).Concat(Face(0, 1, 3, mat: 2)).Concat(Face(0, 3, 2, mat: 3)).ToArray()), tmulti);
        var tm = Build(t, out _, "t2.max");
        Assert.Equal(new[] { "Red:2", "NoMaterial:1", "Blue:1" }, tm.Meshes[0].Primitives.Select(p => tm.Materials[p.Material].Name + ":" + p.Indices.Length / 3).ToArray());
    }

    // =================================================================================================================
    // Added in round 3 of the review: what the documented workflow showed (the tools after the reader), and what a
    // reader who had seen none of the earlier rounds found.
    // =================================================================================================================
    [Fact]
    public void Names_are_written_without_the_spaces_at_their_ends()
    {
        // the tools after the reader key their marks on names and trim them: 'PALO POPA ' must arrive as 'PALO POPA'
        var s = new Sc(); int root = s.Root();
        s.Node(" PALO POPA ", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), s.Std(" Paint ", new[] { 1f, 0, 0 }));
        var m = Build(s, out _);
        Assert.Equal("PALO POPA", m.Nodes[0].Name); Assert.Equal("PALO POPA", m.Meshes[0].Name); Assert.Equal("Paint", m.Materials[0].Name);
    }

    [Fact]
    public void UVs_are_written_where_a_picture_is_shown_or_missing_and_nowhere_else()
    {
        // four materials on one part: a picture that is there, one that is missing, a procedural map, no map. And a
        // second part with UVs and no material at all. The model's UVs stay within what the pictures use: the far-away
        // UVs of the faces without a picture (u = 500 here) are not written.
        Png("tex.png", 1, 1, 10, 20, 30);
        var s = new Sc(); int root = s.Root();
        int multi = s.Multi(s.Std("Shown", new[] { 1f, 1f, 1f }, s.Bitmap("tex.png")), s.Std("Missing", new[] { 1f, 1f, 1f }, s.Bitmap("gone.png")),
            s.Std("Cloud", new[] { 1f, 1f, 1f }, s.Noise()), s.Std("Plain", new[] { 1f, 1f, 1f }));
        float[] tv = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 500, 0, 0, 501, 0, 0, 500, 1, 0 };
        var faces = Face(0, 1, 2, mat: 0).Concat(Face(0, 1, 2, mat: 1)).Concat(Face(0, 1, 2, mat: 2)).Concat(Face(0, 1, 2, mat: 3)).ToArray();
        s.Node("Four", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, faces, tv, new[] { 0, 1, 2, 0, 1, 2, 3, 4, 5, 3, 4, 5 }), multi);
        s.Node("Bare", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), tv, new[] { 3, 4, 5 }));
        var m = Build(s, out var rep);
        Assert.Equal(new[] { "Shown:uv", "Missing:uv", "Cloud:-", "Plain:-" }, m.Meshes[0].Primitives.Select(p => m.Materials[p.Material].Name + (p.Uv0 != null ? ":uv" : ":-")).ToArray());
        Assert.Null(m.Meshes[1].Primitives.Single().Uv0);
        Assert.True(m.Meshes.SelectMany(x => x.Primitives).Where(p => p.Uv0 != null).All(p => p.Uv0.All(v => v >= 0 && v <= 1)));
        Assert.Equal(1, rep.PartsWithUv); Assert.Equal(2, rep.PartsWithFileUv);
        Assert.Contains(rep.Notes, n => n.StartsWith("2 part(s) have UVs in the file on faces that show no picture") && n.EndsWith(": 'Four', 'Bare'"));
    }

    [Fact]
    public void A_file_named_like_a_picture_that_is_none_is_not_embedded_as_one()
    {
        // an empty file named .png, a web page named .jpeg, a GIF named .png: none of them is a picture this reader
        // knows by its bytes - the plain colour, by name; and a real JPEG under a .png name is still a JPEG
        System.IO.File.WriteAllBytes(Path.Combine(dir, "empty.png"), new byte[0]);
        System.IO.File.WriteAllText(Path.Combine(dir, "page.jpeg"), "<html>404</html>");
        System.IO.File.WriteAllBytes(Path.Combine(dir, "gif.png"), Encoding.ASCII.GetBytes("GIF89a\u0001\u0000\u0001\u0000"));
        System.IO.File.WriteAllBytes(Path.Combine(dir, "real.png"), new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });
        var s = new Sc(); int root = s.Root();
        int multi = s.Multi(s.Std("E", new[] { 1f, 1f, 1f }, s.Bitmap("empty.png")), s.Std("P", new[] { 1f, 1f, 1f }, s.Bitmap("page.jpeg")),
            s.Std("G", new[] { 1f, 1f, 1f }, s.Bitmap("gif.png")), s.Std("R", new[] { 1f, 1f, 1f }, s.Bitmap("real.png")));
        var faces = Face(0, 1, 2, mat: 0).Concat(Face(0, 1, 2, mat: 1)).Concat(Face(0, 1, 2, mat: 2)).Concat(Face(0, 1, 2, mat: 3)).ToArray();
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, faces, Uv3, new[] { 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2 }), multi);
        var m = Build(s, out var rep);
        Assert.Equal("image/jpeg", m.Images.Single().MimeType); Assert.Equal("real", m.Images[0].Name);
        foreach (string f in new[] { "empty.png", "page.jpeg", "gif.png" }) Assert.Contains(rep.Notes, n => n.Contains("texture '" + f + "' could not be decoded") && n.Contains("the plain colour is used"));
        Assert.Equal(4, m.Meshes[0].Primitives.Count(p => p.Uv0 != null));       // their faces keep their UVs: the real picture can be put on later
    }

    [Fact]
    public void A_part_under_a_node_that_stands_too_far_is_left_by_name_and_the_rest_is_read()
    {
        // B stands at 3e38 under a parent scaled by 2 (its place in the world is past a float); C under it comes back to
        // x = 0, so its own place is fine - and B's is still in the model as C's parent. C is named; Hull is read.
        var s = new Sc(); int root = s.Root();
        int a = s.Node("A", root, s.Prs(Id, NoRot, new float[] { 2, 2, 2 }), -1);
        int b = s.Node("B", a, s.Prs(new float[] { 3e38f, 0, 0 }, NoRot, One), -1);
        s.Node("C", b, s.Prs(new float[] { -3e38f, 0, 0 }, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("Hull", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        var m = Build(s, out var rep);
        Assert.Equal(1, rep.Parts); Assert.Equal("Hull", m.Nodes.Single().Name);
        Assert.Contains(rep.PartsLeft, l => l.Contains("'C'") && l.Contains("that of a node above it, does not fit a 32-bit float"));
        GlbWriter.Write(m);
    }

    [Fact]
    public void A_mesh_under_too_many_derived_objects_or_one_without_a_stored_mesh_is_named_not_filed_with_the_lights()
    {
        var s = new Sc(); int root = s.Root();
        int mod = s.Add("Bend", 0x10, 0x20, 0x810);
        int Stack(int depth) { int o = s.Mesh(Tri, Face(0, 1, 2)); for (int i = 0; i < depth; i++) o = s.Derived(mod, o); return o; }
        s.Node("Sixteen", root, s.Prs(Id, NoRot, One), Stack(MaxScene.MaxDerived));
        s.Node("Seventeen", root, s.Prs(Id, NoRot, One), Stack(MaxScene.MaxDerived + 1));
        s.Node("Hollow", root, s.Prs(Id, NoRot, One), s.Add("Editable Mesh", 0xE44F10B3, 0, 0x10));
        Build(s, out var rep);
        Assert.Equal(1, rep.Parts); Assert.Equal(MaxScene.MaxDerived, rep.ModifiersSkipped);
        Assert.Contains(rep.PartsLeft, l => l.Contains("'Seventeen'") && l.Contains("more than 16 derived objects"));
        Assert.Contains(rep.PartsLeft, l => l.Contains("'Hollow'") && l.Contains("holds no stored mesh"));
        Assert.Empty(rep.NotGeometry);
    }

    [Fact]
    public void A_part_whose_controller_names_nothing_is_said_to_be_possibly_misplaced()
    {
        var s = new Sc(); int root = s.Root();
        int noPos = s.Add("Position/Rotation/Scale", 0x2005, 0, 0x9008, Leaf(0x2034, U32(None), U32(s.Rot(NoRot)), U32(s.Scl(One))));
        s.Node("NoPosition", root, noPos, s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("NoController", root, -1, s.Mesh(Tri, Face(0, 1, 2)));
        s.Node("Fine", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)));
        Build(s, out var rep);
        Assert.Equal(2, rep.PartsUnplaced);
        Assert.Contains(rep.Notes, n => n == "1 part(s) may be misplaced - its position has no controller: 'NoPosition'");
        Assert.Contains(rep.Notes, n => n == "1 part(s) may be misplaced - it has no transform controller: 'NoController'");
    }

    [Fact]
    public void The_third_UV_coordinate_is_not_judged()
    {
        // w (the third number of a UV vertex) is not written, so a w that is no number does not cost the part its UVs
        var s = new Sc(); int root = s.Root();
        s.Node("W", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2), new float[] { 0, 0, float.NaN, 1, 0, 3e6f, 0, 1, 0 }, new[] { 0, 1, 2 }), s.Std("M", new[] { 1f, 1f, 1f }, s.Bitmap("gone.png")));
        var m = Build(s, out var rep);
        Near(m.Meshes[0].Primitives[0].Uv0, 0, 1, 1, 1, 0, 0); Assert.DoesNotContain(rep.Notes, n => n.Contains("without UVs"));
    }

    [Fact]
    public void A_loop_inside_a_stream_s_declared_size_is_refused_and_a_chain_is_walked_no_further_than_that_size()
    {
        // a stream of 9,000 bytes (18 sectors) whose third sector points back at its second: the walk stops at the
        // stream's size, so the loop must be seen by the sector it meets twice
        var file = Cfb(("B", new byte[9000]));
        int fatAt = file.Length - 512;
        U32(1).CopyTo(file, fatAt + 8);                                           // sector 2 -> 1
        Assert.Contains("loops", Assert.Throws<InvalidDataException>(() => MaxDocument.Read(file)).Message);
        // a stream is walked no further than its declared size: here the stream declares one sector of an 18-sector chain
        // whose 17th entry is broken (it points outside the file) - the 512 bytes are read and nobody trips over the tail
        var data = Enumerable.Range(0, 9000).Select(i => (byte)(i + 1)).ToArray();
        file = Cfb(("Big", data));
        int big = -1; var nb = Txt("Big ");
        for (int i = 512; i + 128 <= file.Length; i += 128) if (file.Skip(i).Take(nb.Length).SequenceEqual(nb)) big = i;
        uint start = BitConverter.ToUInt32(file, big + 0x74);
        U32(0).CopyTo(file, 0x38);                                                // no mini stream: every stream lies in sectors
        U32(512).CopyTo(file, big + 0x78);
        U32(0x00FFFFF0).CopyTo(file, file.Length - 512 + 4 * ((int)start + 16));
        Assert.Equal(data.Take(512).ToArray(), MaxDocument.Read(file).Streams["Big"]);
        U32(9000).CopyTo(file, big + 0x78);                                       // declared whole again: the broken tail is met, and named
        Assert.Contains("is outside the file", Assert.Throws<InvalidDataException>(() => MaxDocument.Read(file)).Message);
    }

    [Fact]
    public void A_vertex_has_a_bound_on_its_smoothing_sums_and_past_it_a_corner_takes_its_face_s_normal()
    {
        // a cone of 70 faces round its apex, every face in a group combination of its own, all sharing one bit: the sum
        // at the apex is over all 70 faces whichever combination asks (the ring is closed, so it points straight up),
        // and it is asked for under 70 different combinations. The first MaxSmoothingSums (64) are worked out; the
        // corners of the six faces after them take their own face's normal, which leans outwards.
        const int N = 70;
        var verts = new List<float> { 0, 0, 1 };
        for (int i = 0; i < N; i++) { double a = 2 * Math.PI * i / N; verts.Add((float)Math.Cos(a)); verts.Add((float)Math.Sin(a)); verts.Add(0); }
        var faces = new List<int>();
        for (int i = 0; i < N; i++) faces.AddRange(Face(0, 1 + i, 1 + (i + 1) % N, sg: 0x40000000 | (i + 1)));
        var s = new Sc(); int root = s.Root();
        s.Node("Cone", root, s.Prs(Id, NoRot, One), s.Mesh(verts.ToArray(), faces.ToArray()));
        var p = Build(s, out _).Meshes[0].Primitives.Single();
        Assert.Equal(N * 3, p.Indices.Length);
        double Up(int face) => p.Normals[p.Indices[face * 3] * 3 + 1];             // the apex corner's normal, its up component (GLB: y)
        for (int f = 0; f < MaxModel.MaxSmoothingSums; f++) Near(1, Up(f), 1e-4);     // the smooth sum: straight up
        // a face's own normal: (sin b - sin a, cos a - cos b, sin(b - a)) with b - a = 2 pi / 70 - up by cos of the cone's
        // half angle at the face, 0.707 for this cone of height 1 and radius 1 (a little more: the face is a chord)
        for (int f = MaxModel.MaxSmoothingSums; f < N; f++) Assert.InRange(Up(f), 0.70, 0.72);
    }

    [Fact]
    public void The_model_and_one_object_s_references_have_bounds_that_are_named()
    {
        // one mesh of 2,000 faces under 5,001 nodes: 10,002,000 triangles, past MaxTriangles - refused before they are made
        var s = new Sc(); int root = s.Root();
        var faces = new List<int>(); for (int i = 0; i < 2000; i++) faces.AddRange(Face(0, 1, 2));
        int prs = s.Prs(Id, NoRot, One), mesh = s.Mesh(Tri, faces.ToArray());
        for (int i = 0; i <= MaxModel.MaxTriangles / 2000; i++) s.Node("I" + i, root, prs, mesh);
        Assert.Contains("more than 10,000,000 triangles", Assert.Throws<InvalidDataException>(() => Build(s, out _)).Message);
        // an object that lists more references than the reader keeps a table of
        var t = new Sc(); int troot = t.Root();
        int huge = t.Add("Multi/Sub-Object", 0x200, 0, 0xC00, Leaf(0x2034, new byte[4 * (MaxScene.MaxReferences + 1)]));
        t.Node("P", troot, t.Prs(Id, NoRot, One), t.Mesh(Tri, Face(0, 1, 2)), huge);
        Assert.Contains("references; this reader stops at " + MaxScene.MaxReferences, Assert.Throws<InvalidDataException>(() => Build(t, out _, "t2.max")).Message);
    }

    [Fact]
    public void The_map_table_is_read_as_far_as_its_slots_go()
    {
        // a map table names maps in slot 8 (bump) and in a "slot 70": a Standard material has no such slot (the samples'
        // tables have 11), and a table of millions of references is not millions of maps - slots past MaxMapSlots are
        // not looked at
        var s = new Sc(); int root = s.Root();
        s.Node("P", root, s.Prs(Id, NoRot, One), s.Mesh(Tri, Face(0, 1, 2)), s.Std("M", new[] { 1f, 1f, 1f }, otherSlots: new[] { (8, s.Noise()), (MaxScene.MaxMapSlots + 6, s.Noise()) }));
        Build(s, out var rep);
        Assert.Contains(rep.Notes, n => n.StartsWith("a bump map is not read on 1 material(s)"));
        Assert.DoesNotContain(rep.Notes, n => n.Contains("slot " + (MaxScene.MaxMapSlots + 6)));
        Assert.Single(rep.Notes.Where(n => n.Contains("map is not read")));
    }

    [Fact]
    public void A_BMP_whose_header_claims_an_absurd_size_is_refused_not_thrown_on()
    {
        // an 8-bit BMP whose info header says it is int.MaxValue bytes long: the palette's place overflowed, and the
        // decoder threw where it answers false with the reason
        var bmp = Cat(new byte[] { (byte)'B', (byte)'M' }, U32(66), U32(0), U32(62), BitConverter.GetBytes(int.MaxValue), U32(1), U32(1), U16(1), U16(8), U32(0), U32(4), U32(0), U32(0), U32(2), U32(0),
            new byte[] { 3, 2, 1, 0, 9, 9, 9, 0 }, new byte[] { 0, 0, 0, 0 });
        Assert.False(MaxImages.Bmp(bmp, out _, out _, out var px, out string why)); Assert.Null(px); Assert.Contains("BMP header of", why);
    }
}
