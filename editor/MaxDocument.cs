// MaxDocument.cs - the container layer of the native 3ds Max reader (2026-11-21): a .max file is a Microsoft compound
// file (the format of old Office documents) holding a handful of streams - Scene, ClassDirectory (or ClassDirectory2 /
// ClassDirectory3), DllDirectory, ... - and each stream is a tree of chunks: a 16-bit id and a 32-bit size whose top bit
// says "this chunk holds chunks" (a size of 0 means a 64-bit size follows, with the same top bit). The size counts the
// header. Newer 3ds Max versions gzip each stream; that is unpacked here.
//
// Pure C#: no Unity. Written from the files themselves (the user's four models, read byte by byte with a Python probe
// first) and from the public descriptions of the format; no code of another reader is in here. What does not hold - a
// file that is not a compound file, a sector chain that leaves the file or loops, a chunk that runs past its parent - is
// refused by name with InvalidDataException, never read around.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

public sealed class MaxChunk
{
    public ushort Id;
    public byte[] Buf;                // the stream this chunk lives in (shared, never copied)
    public int Start, Length;         // the chunk's DATA (after its header) for a leaf; unused for a container
    public List<MaxChunk> Kids;       // null for a leaf
    public bool IsContainer => Kids != null;

    public MaxChunk First(int id)
    {
        if (Kids != null) foreach (var k in Kids) if (k.Id == id) return k;
        return null;
    }
    public IEnumerable<MaxChunk> All(int id)
    {
        if (Kids != null) foreach (var k in Kids) if (k.Id == id) yield return k;
    }
    /// <summary>A leaf's value at a byte offset; a read past the chunk's data is refused by name.</summary>
    public uint U32(int at) { Need(at, 4); return BitConverter.ToUInt32(Buf, Start + at); }
    public int I32(int at) { Need(at, 4); return BitConverter.ToInt32(Buf, Start + at); }
    public ushort U16(int at) { Need(at, 2); return BitConverter.ToUInt16(Buf, Start + at); }
    public float F32(int at) { Need(at, 4); return BitConverter.ToSingle(Buf, Start + at); }
    void Need(int at, int n)
    {
        if (Kids != null) throw new InvalidDataException($"chunk {Id:X4} holds chunks, not a value");
        if (at < 0 || at > Length - n) throw new InvalidDataException($"chunk {Id:X4}: {n} bytes at {at} asked of {Length}");
    }
    /// <summary>A leaf's bytes as UTF-16 text, trailing NULs dropped (every name in the files read so far).</summary>
    public string Text()
    {
        if (Kids != null) return "";
        return Encoding.Unicode.GetString(Buf, Start, Length & ~1).TrimEnd('\0');
    }
}

public sealed class MaxDocument
{
    /// <summary>Every stream of the file by name, unpacked.</summary>
    public readonly Dictionary<string, byte[]> Streams = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    /// <summary>True when at least one stream was gzip-packed (3ds Max 2010 and later write them so).</summary>
    public bool Compressed;

    const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF, MaxRegular = 0xFFFFFFFA;
    public const long MaxFileBytes = 1L << 30;         // 1 GB: the container is held in one array
    public const long MaxStreamBytes = 1L << 30;       // an unpacked stream stops here too
    public const int MaxDepth = 64;                    // chunk nesting

    public static MaxDocument Read(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("the .max file does not exist", path);
        if (fi.Length > MaxFileBytes) throw new InvalidDataException($"the .max file is {fi.Length / 1e9:0.0} GB; this reader stops at 1 GB");
        return Read(File.ReadAllBytes(path));
    }

    public static MaxDocument Read(byte[] b)
    {
        if (b == null || b.Length < 512) throw new InvalidDataException("not a .max file: shorter than a compound-file header");
        byte[] sig = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        for (int i = 0; i < 8; i++) if (b[i] != sig[i]) throw new InvalidDataException("not a .max file: no compound-file signature (a .max is a compound file; this may be another format with a .max name)");
        int major = BitConverter.ToUInt16(b, 0x1A);
        int sectorShift = BitConverter.ToUInt16(b, 0x1E), miniShift = BitConverter.ToUInt16(b, 0x20);
        if (sectorShift != 9 && sectorShift != 12) throw new InvalidDataException($"compound file: sector shift {sectorShift} (9 or 12 expected)");
        if (miniShift != 6) throw new InvalidDataException($"compound file: mini sector shift {miniShift} (6 expected)");
        int ss = 1 << sectorShift, mss = 1 << miniShift;
        uint nFat = BitConverter.ToUInt32(b, 0x2C), dirStart = BitConverter.ToUInt32(b, 0x30), cutoff = BitConverter.ToUInt32(b, 0x38);
        uint miniFatStart = BitConverter.ToUInt32(b, 0x3C), difat = BitConverter.ToUInt32(b, 0x44), nDifat = BitConverter.ToUInt32(b, 0x48);
        long sectors = ((long)b.Length - ss) / ss;      // whole sectors after the header's
        if (sectors <= 0) throw new InvalidDataException("compound file: no sectors after the header");

        long Off(uint sect)
        {
            if (sect >= sectors) throw new InvalidDataException($"compound file: sector {sect} is outside the file ({sectors} sectors)");
            return ((long)sect + 1) * ss;
        }
        // the sectors that hold the allocation table: 109 in the header, the rest through the DIFAT chain
        // A file cannot hold more allocation sectors than it has sectors: without this bound a 1 MB file whose DIFAT
        // names one sector over and over asked for gigabytes (review 2026-11-21).
        if (nFat > sectors) throw new InvalidDataException($"compound file: {nFat} allocation sectors declared in a file of {sectors} sectors");
        var fatSects = new List<uint>();
        for (int i = 0; i < 109; i++) { uint s = BitConverter.ToUInt32(b, 0x4C + 4 * i); if (s < MaxRegular) fatSects.Add(s); }
        int per = ss / 4;
        for (uint guard = 0; difat < MaxRegular; guard++)
        {
            if (guard > nDifat || guard > sectors) throw new InvalidDataException("compound file: the DIFAT chain is longer than its count (a loop)");
            long o = Off(difat);
            for (int i = 0; i < per - 1; i++) { uint s = BitConverter.ToUInt32(b, (int)o + 4 * i); if (s < MaxRegular) fatSects.Add(s); }
            if (fatSects.Count > sectors) throw new InvalidDataException($"compound file: more allocation sectors listed than the file has sectors ({sectors})");
            difat = BitConverter.ToUInt32(b, (int)o + 4 * (per - 1));
        }
        if (nFat > fatSects.Count) throw new InvalidDataException($"compound file: {nFat} allocation sectors declared, {fatSects.Count} listed");
        var fat = new uint[(long)nFat * per > int.MaxValue ? throw new InvalidDataException("compound file: allocation table too large") : (int)nFat * per];
        for (int f = 0; f < nFat; f++) Buffer.BlockCopy(b, (int)Off(fatSects[f]), fat, f * ss, ss);

        byte[] Chain(uint start, long size, string what)
        {
            // size < 0 = the whole chain (the directory, the mini FAT)
            using (var ms = new MemoryStream())
            {
                uint s = start; long n = 0;
                while (s < MaxRegular)
                {
                    if (++n > sectors) throw new InvalidDataException($"compound file: the sector chain of {what} loops");
                    ms.Write(b, (int)Off(s), ss);
                    if (s >= fat.Length) throw new InvalidDataException($"compound file: sector {s} of {what} has no allocation entry");
                    s = fat[s];
                }
                if (s != EndOfChain) throw new InvalidDataException($"compound file: the sector chain of {what} ends in {s:X8}, not in the end mark");
                if (size >= 0 && ms.Length < size) throw new InvalidDataException($"compound file: {what} declares {size} bytes, its chain holds {ms.Length}");
                var all = ms.ToArray();
                if (size < 0 || size == all.Length) return all;
                var cut = new byte[size]; Buffer.BlockCopy(all, 0, cut, 0, (int)size); return cut;
            }
        }

        byte[] dir = Chain(dirStart, -1, "the directory");
        var entries = new List<(string name, int type, uint start, long size)>();
        for (int i = 0; i + 128 <= dir.Length; i += 128)
        {
            int nameLen = BitConverter.ToUInt16(dir, i + 0x40), type = dir[i + 0x42];
            if (type == 0 || nameLen < 2 || nameLen > 64) continue;
            string name = Encoding.Unicode.GetString(dir, i, nameLen - 2);
            uint start = BitConverter.ToUInt32(dir, i + 0x74);
            long size = major >= 4 ? BitConverter.ToInt64(dir, i + 0x78) : BitConverter.ToUInt32(dir, i + 0x78);
            if (size < 0 || size > MaxStreamBytes) throw new InvalidDataException($"compound file: stream '{name}' declares {size} bytes");
            entries.Add((name, type, start, size));
        }
        int root = entries.FindIndex(e => e.type == 5);
        if (root < 0) throw new InvalidDataException("compound file: no root entry");
        byte[] mini = entries[root].size > 0 && entries[root].start < MaxRegular ? Chain(entries[root].start, entries[root].size, "the mini stream") : new byte[0];
        uint[] miniFat = new uint[0];
        if (miniFatStart < MaxRegular) { var mf = Chain(miniFatStart, -1, "the mini allocation table"); miniFat = new uint[mf.Length / 4]; Buffer.BlockCopy(mf, 0, miniFat, 0, miniFat.Length * 4); }

        var doc = new MaxDocument();
        long claimed = 0;
        foreach (var e in entries)
        {
            if (e.type != 2) continue;
            // two entries of one name: the first wins (no file read so far has it), and the second is not even walked
            if (doc.Streams.ContainsKey(e.name)) continue;
            // Streams do not share sectors, so together they cannot hold more bytes than the file: without this bound
            // 4,095 entries naming one chain cost 2.5 GB from a 529 KB file (review 2026-11-21).
            claimed += e.size;
            if (claimed > b.Length) throw new InvalidDataException($"compound file: its streams declare more bytes together than the file holds ({b.Length}) - sectors named by more than one stream");
            byte[] raw;
            if (e.size == 0) raw = new byte[0];
            else if (e.size < cutoff)
            {
                if (e.size > mini.Length) throw new InvalidDataException($"compound file: stream '{e.name}' declares {e.size} bytes, the mini stream holds {mini.Length}");
                raw = new byte[e.size];
                uint s = e.start; long done = 0, n = 0;
                while (done < e.size)
                {
                    if (s >= MaxRegular) throw new InvalidDataException($"compound file: stream '{e.name}' declares {e.size} bytes, its mini chain holds {done}");
                    if (++n > miniFat.Length + 1L) throw new InvalidDataException($"compound file: the mini chain of '{e.name}' loops");
                    long from = (long)s * mss;
                    int take = (int)Math.Min(mss, e.size - done);
                    if (from + take > mini.Length) throw new InvalidDataException($"compound file: mini sector {s} of '{e.name}' is outside the mini stream");
                    Buffer.BlockCopy(mini, (int)from, raw, (int)done, take);
                    done += take;
                    if (s >= miniFat.Length) throw new InvalidDataException($"compound file: mini sector {s} of '{e.name}' has no allocation entry");
                    s = miniFat[s];
                }
            }
            else raw = Chain(e.start, e.size, "stream '" + e.name + "'");
            if (raw.Length >= 2 && raw[0] == 0x1F && raw[1] == 0x8B) { raw = Gunzip(raw, e.name); doc.Compressed = true; }
            doc.Streams[e.name] = raw;
        }
        return doc;
    }

    static byte[] Gunzip(byte[] raw, string name)
    {
        try
        {
            using (var src = new MemoryStream(raw))
            using (var gz = new GZipStream(src, CompressionMode.Decompress))
            using (var outp = new MemoryStream())
            {
                var buf = new byte[1 << 16];
                for (;;)
                {
                    int n = gz.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    if (outp.Length + n > MaxStreamBytes) throw new InvalidDataException($"stream '{name}' unpacks to more than 1 GB");
                    outp.Write(buf, 0, n);
                }
                return outp.ToArray();
            }
        }
        catch (InvalidDataException) { throw; }
        catch (Exception e) { throw new InvalidDataException($"stream '{name}' is gzip-packed and does not unpack: {e.Message}"); }
    }

    /// <summary>The chunk tree of a stream; an absent stream gives null.</summary>
    public List<MaxChunk> Chunks(string stream)
    {
        if (!Streams.TryGetValue(stream, out var buf)) return null;
        int count = 0;
        return ParseChunks(buf, 0, buf.Length, 0, stream, ref count);
    }

    public static List<MaxChunk> ParseChunks(byte[] buf) { int count = 0; return ParseChunks(buf, 0, buf.Length, 0, "the stream", ref count); }

    /// <summary>A stream's chunk tree stops here: the tree is built whole, and a gzip-packed stream of empty chunks
    /// (179 KB packed, 20 million chunks) cost 1.6 GB before this bound (review 2026-11-21). The largest of the four
    /// sample scenes holds 11,981 chunks for its 152 parts - about 80 a part, so the bound leaves room for a scene of
    /// some 25,000 parts.</summary>
    public const int MaxChunks = 2000000;

    static List<MaxChunk> ParseChunks(byte[] buf, int lo, int hi, int depth, string where, ref int count)
    {
        if (depth > MaxDepth) throw new InvalidDataException($"{where}: chunks nested deeper than {MaxDepth}");
        var list = new List<MaxChunk>();
        while (lo < hi)
        {
            if (++count > MaxChunks) throw new InvalidDataException($"{where}: more than {MaxChunks} chunks; this reader stops there");
            if (hi - lo < 6) throw new InvalidDataException($"{where}: {hi - lo} stray byte(s) at {lo} where a chunk header belongs");
            ushort id = BitConverter.ToUInt16(buf, lo);
            uint s32 = BitConverter.ToUInt32(buf, lo + 2);
            int head = 6; long size; bool container;
            if (s32 == 0)
            {
                if (hi - lo < 14) throw new InvalidDataException($"{where}: chunk {id:X4} at {lo} has a 64-bit size that runs past its parent");
                ulong s64 = BitConverter.ToUInt64(buf, lo + 6);
                head = 14; container = (s64 >> 63) != 0; size = (long)(s64 & 0x7FFFFFFFFFFFFFFFUL);
            }
            else { container = (s32 >> 31) != 0; size = s32 & 0x7FFFFFFF; }
            if (size < head || size > hi - lo) throw new InvalidDataException($"{where}: chunk {id:X4} at {lo} declares {size} bytes, {hi - lo} remain in its parent");
            var c = new MaxChunk { Id = id, Buf = buf };
            if (container) c.Kids = ParseChunks(buf, lo + head, lo + (int)size, depth + 1, where, ref count);
            else { c.Start = lo + head; c.Length = (int)size - head; }
            list.Add(c);
            lo += (int)size;
        }
        return list;
    }
}
