using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

// THE BINARY FBX NODE TREE (2026-10-31, step 1 of the native FBX reader - the programme the user named on 2026-10-10:
// "we suddenly get access to a lot of free 3d models"; ufbx is the guide). This is the FORMAT layer only: the file's
// record tree, every property typed, every array decoded - no meaning attached yet. The object graph (Geometry,
// Model, Deformer, Animation*, Connections) and its mapping onto HafModel are the next steps and read THIS.
//
// THE FORMAT (Kaydara FBX Binary, as ufbx and Blender's io_scene_fbx read it): a 23-byte magic, a uint32 version at
// offset 23 (7100..7700 seen in the wild; the inventory of 2026-10-31 over the user's sources found 7400, 7500 and
// 7700), then records from offset 27. A record is EndOffset, NumProperties, PropertyListLen (uint32 each before
// version 7500, uint64 from 7500 on), a one-byte name length, the name, the properties, then nested records up to
// EndOffset, closed by a NULL record (all zeros) when there are any. The top level ends with a null record; a footer
// follows that nothing here reads. Property types: Y int16, C bool, I int32, F float, D double, L int64; arrays
// f d l i b with (count, encoding, byteLength) - encoding 1 is a zlib stream; S string and R raw bytes with a uint32
// length.
//
// THE CONTRACT, as GlbReader's: what is read is read exactly, what is not understood is REFUSED BY NAME - an unknown
// property type, an array whose decoded size is not count x element size, a record that reaches past the file, an
// ASCII FBX (the text form; none of the user's files are one). Nothing is dropped in silence.
//
// Verified by tools/fbx_drill.sh: this reader's dump of every record, property and array (values as bits, arrays as
// hashes) equal to an independent Python reader's over every FBX the project holds, the recipes' sources, and
// generated fixtures of the shapes those files do not have (64-bit records, every property type, stored and
// compressed arrays, empty arrays, deep nesting, an empty name).
public sealed class FbxNode
{
    public string Name = "";
    public readonly List<object> Properties = new List<object>();   // short, bool, int, float, double, long, string, FbxRaw, int[], long[], float[], double[], bool[]
    public readonly List<FbxNode> Children = new List<FbxNode>();

    public FbxNode First(string name) { foreach (var c in Children) if (c.Name == name) return c; return null; }
    public IEnumerable<FbxNode> All(string name) { foreach (var c in Children) if (c.Name == name) yield return c; }
    public T Prop<T>(int i) => i < Properties.Count && Properties[i] is T t ? t : throw new InvalidDataException($"FBX node '{Name}': property {i} is not {typeof(T).Name} ({(i < Properties.Count ? Properties[i].GetType().Name : "absent")})");
    public override string ToString() => $"{Name} ({Properties.Count} props, {Children.Count} children)";
}

/// <summary>The bytes of an 'R' property, kept as the file holds them.</summary>
public sealed class FbxRaw
{
    public readonly byte[] Bytes;
    public FbxRaw(byte[] bytes) { Bytes = bytes; }
}

public sealed class FbxDocument
{
    public int Version;
    public readonly List<FbxNode> Nodes = new List<FbxNode>();
    public string SourcePath = "";

    public FbxNode First(string name) { foreach (var n in Nodes) if (n.Name == name) return n; return null; }

    static readonly byte[] Magic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0");

    public static bool IsBinaryFbx(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 27) return false;
        for (int i = 0; i < Magic.Length; i++) if (bytes[i] != Magic[i]) return false;
        return true;
    }

    public static FbxDocument Read(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("no path");
        var doc = Read(File.ReadAllBytes(path));
        doc.SourcePath = path;
        return doc;
    }

    public static FbxDocument Read(byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        if (!IsBinaryFbx(bytes))
        {
            if (bytes.Length > 0 && bytes[0] == (byte)';') throw new InvalidDataException("an ASCII FBX (the text form): not read - only the binary form is");
            throw new InvalidDataException("not a binary FBX: the file does not start with the Kaydara magic");
        }
        var doc = new FbxDocument { Version = (int)BitConverter.ToUInt32(bytes, 23) };
        var r = new Reader(bytes, doc.Version >= 7500);
        long pos = 27;
        while (pos < bytes.Length)
        {
            var node = r.ReadNode(ref pos, 0);
            if (node == null) break;   // the top level's null record; the footer follows
            doc.Nodes.Add(node);
        }
        return doc;
    }

    sealed class Reader
    {
        readonly byte[] b; readonly bool wide;
        public Reader(byte[] bytes, bool wideRecords) { b = bytes; wide = wideRecords; }

        long U32(ref long pos) { Need(pos, 4); uint v = BitConverter.ToUInt32(b, (int)pos); pos += 4; return v; }
        long U64(ref long pos) { Need(pos, 8); ulong v = BitConverter.ToUInt64(b, (int)pos); pos += 8; if (v > int.MaxValue) throw new InvalidDataException($"FBX record at {pos - 8}: a length of {v} is past what this reader holds"); return (long)v; }
        long Len(ref long pos) => wide ? U64(ref pos) : U32(ref pos);
        void Need(long pos, long n) { if (pos < 0 || pos + n > b.Length) throw new InvalidDataException($"FBX: a record at {pos} reaches past the file ({n} byte(s) wanted, {b.Length - pos} left)"); }

        public FbxNode ReadNode(ref long pos, int depth)
        {
            long start = pos;
            long end = Len(ref pos), nprops = Len(ref pos), plen = Len(ref pos);
            Need(pos, 1); int nameLen = b[pos]; pos += 1;
            Need(pos, nameLen); string name = Encoding.UTF8.GetString(b, (int)pos, nameLen); pos += nameLen;
            if (end == 0 && nprops == 0 && plen == 0 && nameLen == 0) return null;   // the null record
            if (end < pos || end > b.Length) throw new InvalidDataException($"FBX record '{name}' at {start}: end offset {end} is outside the file ({b.Length} bytes)");
            if (depth > 64) throw new InvalidDataException($"FBX record '{name}' at {start}: nested deeper than 64 levels");
            var node = new FbxNode { Name = name };
            long propEnd = pos + plen;
            if (propEnd > end) throw new InvalidDataException($"FBX record '{name}' at {start}: the property list ({plen} bytes) reaches past the record's end");
            for (long i = 0; i < nprops; i++) node.Properties.Add(ReadProperty(ref pos, name, end));
            if (pos != propEnd) throw new InvalidDataException($"FBX record '{name}' at {start}: the properties took {pos - (propEnd - plen)} bytes, the record says {plen}");
            while (pos < end)
            {
                var child = ReadNode(ref pos, depth + 1);
                if (child == null) break;
                node.Children.Add(child);
            }
            if (pos != end) throw new InvalidDataException($"FBX record '{name}' at {start}: its children end at {pos}, the record says {end}");
            return node;
        }

        object ReadProperty(ref long pos, string owner, long end)
        {
            Need(pos, 1); char t = (char)b[pos]; pos += 1;
            switch (t)
            {
                case 'Y': { Need(pos, 2); short v = BitConverter.ToInt16(b, (int)pos); pos += 2; return v; }
                case 'C': { Need(pos, 1); bool v = b[pos] != 0; pos += 1; return v; }
                case 'I': { Need(pos, 4); int v = BitConverter.ToInt32(b, (int)pos); pos += 4; return v; }
                case 'F': { Need(pos, 4); float v = BitConverter.ToSingle(b, (int)pos); pos += 4; return v; }
                case 'D': { Need(pos, 8); double v = BitConverter.ToDouble(b, (int)pos); pos += 8; return v; }
                case 'L': { Need(pos, 8); long v = BitConverter.ToInt64(b, (int)pos); pos += 8; return v; }
                case 'S': case 'R':
                {
                    long n = U32(ref pos); Need(pos, n);
                    if (pos + n > end) throw new InvalidDataException($"FBX record '{owner}': a {t} property of {n} bytes reaches past the record");
                    var raw = new byte[n]; Buffer.BlockCopy(b, (int)pos, raw, 0, (int)n); pos += n;
                    return t == 'S' ? (object)Encoding.UTF8.GetString(raw) : new FbxRaw(raw);
                }
                case 'f': case 'd': case 'l': case 'i': case 'b':
                {
                    long count = U32(ref pos), encoding = U32(ref pos), byteLen = U32(ref pos);
                    Need(pos, byteLen);
                    if (pos + byteLen > end) throw new InvalidDataException($"FBX record '{owner}': a {t} array of {byteLen} bytes reaches past the record");
                    int size = t == 'f' || t == 'i' ? 4 : t == 'b' ? 1 : 8;
                    byte[] data;
                    if (encoding == 0) { data = new byte[byteLen]; Buffer.BlockCopy(b, (int)pos, data, 0, (int)byteLen); }
                    else if (encoding == 1) data = Inflate(b, (int)pos, (int)byteLen, checked((int)(count * size)), owner, t);
                    else throw new InvalidDataException($"FBX record '{owner}': array encoding {encoding} is not known (0 stored, 1 zlib)");
                    pos += byteLen;
                    if (data.Length != count * size) throw new InvalidDataException($"FBX record '{owner}': a {t} array of {count} decoded to {data.Length} bytes, not {count * size}");
                    switch (t)
                    {
                        case 'f': { var a = new float[count]; Buffer.BlockCopy(data, 0, a, 0, data.Length); return a; }
                        case 'd': { var a = new double[count]; Buffer.BlockCopy(data, 0, a, 0, data.Length); return a; }
                        case 'l': { var a = new long[count]; Buffer.BlockCopy(data, 0, a, 0, data.Length); return a; }
                        case 'i': { var a = new int[count]; Buffer.BlockCopy(data, 0, a, 0, data.Length); return a; }
                        default: { var a = new bool[count]; for (int i = 0; i < a.Length; i++) a[i] = data[i] != 0; return a; }
                    }
                }
                default: throw new InvalidDataException($"FBX record '{owner}': property type '{t}' (0x{(int)t:X2}) is not known");
            }
        }

        // A zlib stream is a 2-byte header, the deflate data, and an Adler-32 of the plain bytes. DeflateStream reads
        // the middle; the header is skipped and the checksum verified here, so a corrupt array is refused, not read.
        static byte[] Inflate(byte[] src, int at, int len, int want, string owner, char t)
        {
            if (len < 6) throw new InvalidDataException($"FBX record '{owner}': a zlib {t} array of {len} bytes is too short to be one");
            if ((src[at] & 0x0F) != 8 || ((src[at] << 8) | src[at + 1]) % 31 != 0) throw new InvalidDataException($"FBX record '{owner}': a {t} array's zlib header is not one");
            var plain = new byte[want];
            using (var ms = new MemoryStream(src, at + 2, len - 6, false))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < want) { int n = ds.Read(plain, got, want - got); if (n <= 0) break; got += n; }
                if (got != want) throw new InvalidDataException($"FBX record '{owner}': a zlib {t} array inflated to {got} bytes, not {want}");
                var one = new byte[1];
                if (ds.Read(one, 0, 1) > 0) throw new InvalidDataException($"FBX record '{owner}': a zlib {t} array inflates to more than {want} bytes");
            }
            uint a = 1, s = 0;
            for (int i = 0; i < plain.Length; )   // the modulo once per 5552 bytes (the largest block that cannot overflow a uint), not per byte
            {
                int stop = Math.Min(plain.Length, i + 5552);
                for (; i < stop; i++) { a += plain[i]; s += a; }
                a %= 65521; s %= 65521;
            }
            uint adler = (s << 16) | a;
            uint stored = (uint)(src[at + len - 4] << 24 | src[at + len - 3] << 16 | src[at + len - 2] << 8 | src[at + len - 1]);
            if (adler != stored) throw new InvalidDataException($"FBX record '{owner}': a zlib {t} array's Adler-32 does not match (corrupt)");
            return plain;
        }
    }
}
