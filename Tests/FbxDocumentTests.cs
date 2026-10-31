using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;

// The binary FBX node reader (editor/FbxDocument.cs, step 1 of the native FBX reader): the record tree, every property
// type, 32- and 64-bit records, zlib arrays, and the refusals by name. The files here are built byte by byte in the
// test; tools/fbx_drill.sh holds the reader to an independent Python reader over every real file and fixture.
public class FbxDocumentTests
{
    // ---- a tiny writer, independent of the reader --------------------------------------------------------------
    static byte[] Header(int version) => Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0").Concat(new byte[] { 0x1A, 0x00 }).Concat(BitConverter.GetBytes((uint)version)).ToArray();

    /// <summary>Writes a file: every record's end offset is absolute, resolved while encoding from the record's own start.</summary>
    static byte[] File_(int version, params Node[] nodes)
    {
        bool wide = version >= 7500;
        var o = new List<byte>(Header(version));
        foreach (var n in nodes) o.AddRange(Encode(n, o.Count, wide));
        o.AddRange(new byte[(wide ? 24 : 12) + 1]);
        o.AddRange(new byte[160]);   // a footer nothing reads
        return o.ToArray();
    }
    class Node { public string Name = ""; public byte[] Props = new byte[0]; public int NProps; public List<Node> Children = new List<Node>(); }
    static byte[] Encode(Node n, int at, bool wide)
    {
        var nameB = Encoding.UTF8.GetBytes(n.Name);
        int hdr = wide ? 24 : 12;
        int pos = at + hdr + 1 + nameB.Length + n.Props.Length;
        var kids = new List<byte>();
        foreach (var c in n.Children) { var k = Encode(c, pos, wide); kids.AddRange(k); pos += k.Length; }
        if (n.Children.Count > 0) { kids.AddRange(new byte[hdr + 1]); pos += hdr + 1; }
        var o = new List<byte>();
        if (wide) { o.AddRange(BitConverter.GetBytes((ulong)pos)); o.AddRange(BitConverter.GetBytes((ulong)n.NProps)); o.AddRange(BitConverter.GetBytes((ulong)n.Props.Length)); }
        else { o.AddRange(BitConverter.GetBytes((uint)pos)); o.AddRange(BitConverter.GetBytes((uint)n.NProps)); o.AddRange(BitConverter.GetBytes((uint)n.Props.Length)); }
        o.Add((byte)nameB.Length); o.AddRange(nameB); o.AddRange(n.Props); o.AddRange(kids);
        return o.ToArray();
    }
    static Node N(string name, byte[] props, int nprops, params Node[] children) => new Node { Name = name, Props = props, NProps = nprops, Children = children.ToList() };
    static byte[] P(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    static byte[] Y(short v) => new byte[] { (byte)'Y' }.Concat(BitConverter.GetBytes(v)).ToArray();
    static byte[] C(bool v) => new byte[] { (byte)'C', v ? (byte)1 : (byte)0 };
    static byte[] I(int v) => new byte[] { (byte)'I' }.Concat(BitConverter.GetBytes(v)).ToArray();
    static byte[] F(float v) => new byte[] { (byte)'F' }.Concat(BitConverter.GetBytes(v)).ToArray();
    static byte[] D(double v) => new byte[] { (byte)'D' }.Concat(BitConverter.GetBytes(v)).ToArray();
    static byte[] L(long v) => new byte[] { (byte)'L' }.Concat(BitConverter.GetBytes(v)).ToArray();
    static byte[] S(string v) { var b = Encoding.UTF8.GetBytes(v); return new byte[] { (byte)'S' }.Concat(BitConverter.GetBytes((uint)b.Length)).Concat(b).ToArray(); }
    static byte[] R(byte[] b) => new byte[] { (byte)'R' }.Concat(BitConverter.GetBytes((uint)b.Length)).Concat(b).ToArray();
    static byte[] Arr(char t, byte[] raw, int count, bool compress)
    {
        byte[] payload = raw;
        if (compress)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(raw, 0, raw.Length);
                uint a = 1, s = 0; foreach (var x in raw) { a = (a + x) % 65521; s = (s + a) % 65521; }
                uint adler = (s << 16) | a;
                payload = new byte[] { 0x78, 0x9C }.Concat(ms.ToArray()).Concat(new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler }).ToArray();
            }
        }
        return new byte[] { (byte)t }.Concat(BitConverter.GetBytes((uint)count)).Concat(BitConverter.GetBytes((uint)(compress ? 1 : 0))).Concat(BitConverter.GetBytes((uint)payload.Length)).Concat(payload).ToArray();
    }
    static byte[] Ints(params int[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
    static byte[] Doubles(params double[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();

    // ---- the reads ---------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(7400)]
    [InlineData(7500)]
    [InlineData(7700)]
    public void Every_property_type_reads_in_32_and_64_bit_records(int version)
    {
        var props = P(Y(-7), C(true), C(false), I(-123456), F(1.5f), D(double.Epsilon), L(-(1L << 40)), S("héllo\0\u0001world"), R(new byte[] { 0, 255, 7 }),
                      Arr('i', Ints(1, -2, 3), 3, false), Arr('d', Doubles(1e300, -1e-300), 2, true), Arr('b', new byte[] { 1, 0, 1 }, 3, true));
        var doc = FbxDocument.Read(File_(version, N("Types", props, 12), N("After", I(9), 1)));
        Assert.Equal(version, doc.Version);
        Assert.Equal(new[] { "Types", "After" }, doc.Nodes.Select(n => n.Name));
        var p = doc.Nodes[0].Properties;
        Assert.Equal((short)-7, p[0]); Assert.Equal(true, p[1]); Assert.Equal(false, p[2]); Assert.Equal(-123456, p[3]);
        Assert.Equal(1.5f, p[4]); Assert.Equal(double.Epsilon, p[5]); Assert.Equal(-(1L << 40), p[6]);
        Assert.Equal("héllo\0\u0001world", p[7]); Assert.Equal(new byte[] { 0, 255, 7 }, ((FbxRaw)p[8]).Bytes);
        Assert.Equal(new[] { 1, -2, 3 }, (int[])p[9]); Assert.Equal(new[] { 1e300, -1e-300 }, (double[])p[10]); Assert.Equal(new[] { true, false, true }, (bool[])p[11]);
        Assert.Equal(9, doc.Nodes[1].Prop<int>(0));
    }

    [Fact]
    public void Nesting_siblings_empty_names_and_empty_arrays_read()
    {
        var deep = N("L0", new byte[0], 0, N("L1", I(1), 1, N("L2", I(2), 1, N("", S(""), 1))));
        var sib = N("S", new byte[0], 0, N("A", I(1), 1), N("A", I(2), 1), N("B", Arr('i', new byte[0], 0, false), 1));
        var doc = FbxDocument.Read(File_(7400, deep, sib));
        Assert.Equal("", doc.Nodes[0].First("L1").First("L2").Children[0].Name);
        Assert.Equal("", doc.Nodes[0].First("L1").First("L2").Children[0].Prop<string>(0));
        Assert.Equal(new[] { 1, 2 }, doc.Nodes[1].All("A").Select(a => a.Prop<int>(0)));
        Assert.Empty(doc.Nodes[1].First("B").Prop<int[]>(0));
        Assert.Null(doc.Nodes[1].First("Z"));
    }

    [Fact]
    public void The_null_record_ends_the_top_level_and_the_footer_is_not_read()
    {
        var bytes = File_(7500, N("Only", D(3.25), 1));
        var doc = FbxDocument.Read(bytes);
        Assert.Single(doc.Nodes);
        Assert.Equal(3.25, doc.Nodes[0].Prop<double>(0));
        var empty = FbxDocument.Read(File_(7400));
        Assert.Empty(empty.Nodes);
    }

    // ---- the refusals, by name -----------------------------------------------------------------------------------
    [Fact]
    public void What_is_not_understood_is_refused_by_name()
    {
        var ascii = Encoding.ASCII.GetBytes("; FBX 7.4.0 project file\nFBXHeaderExtension: {\n}\n");
        Assert.Contains("ASCII FBX", Assert.Throws<InvalidDataException>(() => FbxDocument.Read(ascii)).Message);
        Assert.Contains("Kaydara magic", Assert.Throws<InvalidDataException>(() => FbxDocument.Read(new byte[40])).Message);
        var unknownType = File_(7400, N("X", new byte[] { (byte)'Q', 1, 2, 3, 4 }, 1));
        Assert.Contains("property type 'Q'", Assert.Throws<InvalidDataException>(() => FbxDocument.Read(unknownType)).Message);
        var badEncoding = File_(7400, N("X", new byte[] { (byte)'i' }.Concat(BitConverter.GetBytes(1u)).Concat(BitConverter.GetBytes(7u)).Concat(BitConverter.GetBytes(4u)).Concat(new byte[4]).ToArray(), 1));
        Assert.Contains("encoding 7", Assert.Throws<InvalidDataException>(() => FbxDocument.Read(badEncoding)).Message);
        var shortArray = File_(7400, N("X", Arr('d', new byte[12], 2, false), 1));   // 2 doubles need 16 bytes
        Assert.Contains("not 16", Assert.Throws<InvalidDataException>(() => FbxDocument.Read(shortArray)).Message);
        var good = File_(7400, N("X", Arr('i', Ints(5, 6, 7, 8), 4, true), 1));
        int at = Array.IndexOf(good, (byte)0x78);   // the zlib header; flip a byte of the deflate data after it
        var corrupt = (byte[])good.Clone(); corrupt[at + 3] ^= 0x55;
        var ex = Assert.Throws<InvalidDataException>(() => FbxDocument.Read(corrupt));
        Assert.True(ex.Message.Contains("Adler-32") || ex.Message.Contains("inflate") || ex.Message.Contains("zlib"), ex.Message);
        var truncated = good.Take(good.Length - 170).ToArray();   // the record's end is past the file
        Assert.Contains("past", Assert.Throws<InvalidDataException>(() => FbxDocument.Read(truncated)).Message);
    }

    [Fact]
    public void A_compressed_array_must_inflate_to_exactly_its_count()
    {
        var sixteen = Arr('i', Ints(1, 2, 3, 4), 4, true);
        // declare 3 elements for 4 elements' worth of deflate data
        Array.Copy(BitConverter.GetBytes(3u), 0, sixteen, 1, 4);
        var bytes = File_(7400, N("X", sixteen, 1));
        var ex = Assert.Throws<InvalidDataException>(() => FbxDocument.Read(bytes));
        Assert.Contains("more than 12", ex.Message);
    }
}
