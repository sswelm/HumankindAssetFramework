// FLAT TABLES FOR THE WELD STAGE (2026-10-27). The weld used to bucket its vertices through a Dictionary of cell ->
// List<int> and the twin pass its faces through a Dictionary of class triple -> List<int>: one List object and one
// growing array per cell and per face. On the Saleg's Revenge's largest group (9.7 million vertices) that is tens of
// millions of small objects, and the editor's stop-the-world collector makes every other group wait while it walks
// them (the largest group fused alone in 52 s, among the others in 112 s). These tables hold the same buckets in a
// handful of flat arrays, each bucket's members in ASCENDING index order, which is exactly the order the Lists held.
// Every caller sees the same members in the same order, so every union and every pairing happens as before and the
// fused bytes do not change.
//
// The grid is a counting sort by bucket, not a chain: Unity's Mono does not inline small methods or lift bounds
// checks, and a first version with a linked chain per bucket and three long compares per member ran the galley's two
// weld passes 30 % SLOWER there than the Dictionary had (13.1 s against 9.9 s), while faster on .NET. Here a cell is
// one packed long per vertex and a bucket is one contiguous range, which the weld loop reads without a call per member.
using System;
using System.Collections.Generic;

public static partial class GlbDisconnectedParts
{
    // The vertices of each cell of a hash grid, each bucket's members ascending. Cells are the CellOf() integer
    // coordinates; a vertex's cell is one packed long when the model's cells fit 63 bits (every fuse: the cell is at
    // least a millionth of the model's longest side, so at most 21 bits per axis), else - a model with non-finite
    // positions, or a caller asking for a finer cell - the three coordinates are kept and compared (`Wide`).
    internal sealed class CellGrid
    {
        public readonly long[] Keys;      // per vertex: its packed cell, or when Wide a 64-bit hash of its cell
        public readonly int[] Members;    // every vertex, grouped by bucket, ascending within each bucket
        public readonly bool Wide;
        readonly int[] start;             // per bucket: its first slot in Members; start[buckets] = vertex count
        readonly long[] cell;             // Wide only: the three cell coordinates per vertex
        readonly long minX, minY, minZ, maxX, maxY, maxZ;
        readonly int bitsX, bitsY, mask;

        public CellGrid(List<Vec3> pos, double cellSize, bool checkedCoordinates = false)
        {
            int n = pos.Count;
            var raw = new long[3 * n];
            minX = minY = minZ = long.MaxValue; maxX = maxY = maxZ = long.MinValue;
            for (int i = 0; i < n; i++)
            {
                PositionKey k = checkedCoordinates ? CheckedCellOf(pos[i], cellSize) : CellOf(pos[i], cellSize);
                raw[3 * i] = k.X; raw[3 * i + 1] = k.Y; raw[3 * i + 2] = k.Z;
                if (k.X < minX) minX = k.X; if (k.X > maxX) maxX = k.X;
                if (k.Y < minY) minY = k.Y; if (k.Y > maxY) maxY = k.Y;
                if (k.Z < minZ) minZ = k.Z; if (k.Z > maxZ) maxZ = k.Z;
            }
            bitsX = Bits(minX, maxX); bitsY = Bits(minY, maxY); int bitsZ = Bits(minZ, maxZ);
            Wide = n > 0 && bitsX + bitsY + bitsZ > 63;
            Keys = new long[n];
            if (Wide) { cell = raw; for (int i = 0; i < n; i++) Keys[i] = Hash3(raw[3 * i], raw[3 * i + 1], raw[3 * i + 2]); }
            else for (int i = 0; i < n; i++) Keys[i] = Pack(raw[3 * i], raw[3 * i + 1], raw[3 * i + 2]);
            int buckets = 16; while (buckets < n) buckets <<= 1;
            mask = buckets - 1;
            start = new int[buckets + 1]; Members = new int[n];
            for (int i = 0; i < n; i++) start[Bucket(Keys[i]) + 1]++;
            for (int b = 0; b < buckets; b++) start[b + 1] += start[b];
            var fill = new int[buckets]; Array.Copy(start, fill, buckets);
            for (int i = 0; i < n; i++) Members[fill[Bucket(Keys[i])]++] = i;   // in vertex order: every bucket ascends
        }

        // AnalyzeMesh historically rejected non-finite/out-of-range cells through checked casts. The fuse's existing
        // unchecked grid remains unchanged; the probe/split/tear caller opts into its original failure behaviour.
        static PositionKey CheckedCellOf(Vec3 p, double size) => new PositionKey {
            X = checked((long)Math.Floor(p.X / size)), Y = checked((long)Math.Floor(p.Y / size)), Z = checked((long)Math.Floor(p.Z / size))
        };

        // How many bits a coordinate in [lo, hi] takes relative to lo; 64 when the span does not fit a long at all.
        static int Bits(long lo, long hi)
        {
            if (hi < lo) return 0;   // no vertices
            ulong span = unchecked((ulong)hi - (ulong)lo);
            int bits = 0; while (bits < 64 && (span >> bits) != 0) bits++;
            return bits;
        }

        long Pack(long x, long y, long z) => (x - minX) | ((y - minY) << bitsX) | ((z - minZ) << (bitsX + bitsY));

        public void Cell(int i, out long x, out long y, out long z)
        {
            if (Wide) { x = cell[3 * i]; y = cell[3 * i + 1]; z = cell[3 * i + 2]; return; }
            long k = Keys[i];
            x = minX + (k & ((1L << bitsX) - 1)); y = minY + ((k >> bitsX) & ((1L << bitsY) - 1)); z = minZ + (k >> (bitsX + bitsY));
        }

        // The slots of Members that may hold the cell's vertices, and the key a member must carry to be one of them
        // (when Wide, a hash: confirm with SameCell). A cell outside the model's cells is empty.
        public long Range(long x, long y, long z, out int from, out int to)
        {
            if (!Wide && (x < minX || x > maxX || y < minY || y > maxY || z < minZ || z > maxZ)) { from = to = 0; return 0; }
            long key = Wide ? Hash3(x, y, z) : Pack(x, y, z);
            int b = Bucket(key); from = start[b]; to = start[b + 1];
            return key;
        }

        public bool SameCell(int j, long x, long y, long z) => cell[3 * j] == x && cell[3 * j + 1] == y && cell[3 * j + 2] == z;

        int Bucket(long key)
        {
            unchecked { ulong h = (ulong)key * 0x9E3779B97F4A7C15UL; h ^= h >> 29; h *= 0xC2B2AE3D27D4EB4FUL; return (int)(h >> 32) & mask; }
        }

        static long Hash3(long x, long y, long z)
        {
            unchecked
            {
                ulong h = (ulong)x * 0x9E3779B97F4A7C15UL;
                h ^= (ulong)y * 0xC2B2AE3D27D4EB4FUL;
                h ^= (ulong)z * 0x165667B19E3779F9UL;
                h ^= h >> 31; h *= 0x9E3779B97F4A7C15UL;
                return (long)h;
            }
        }
    }

    // The faces on each sorted class triple, walked in ascending face order; a face whose classes are not three
    // distinct ones (collapsed or a needle) is not registered and is its own group of nothing.
    internal sealed class TripleIndex
    {
        readonly int[] triple;   // 3 per face, sorted ascending; -1 for an unregistered face
        readonly int[] head, next;
        readonly int mask;

        public TripleIndex(int[] classes, List<int> tris, int faceCount)
        {
            triple = new int[3 * faceCount]; next = new int[faceCount];
            int buckets = 16; while (buckets < faceCount) buckets <<= 1;
            mask = buckets - 1;
            head = new int[buckets]; for (int b = 0; b < buckets; b++) head[b] = -1;
            for (int f = 0; f < faceCount; f++)
            {
                int a = classes[tris[f * 3]], b = classes[tris[f * 3 + 1]], c = classes[tris[f * 3 + 2]];
                if (a == b || b == c || a == c) { triple[3 * f] = triple[3 * f + 1] = triple[3 * f + 2] = -1; continue; }
                (int, int, int) key = SortedTriple(a, b, c);
                triple[3 * f] = key.Item1; triple[3 * f + 1] = key.Item2; triple[3 * f + 2] = key.Item3;
            }
            for (int f = faceCount - 1; f >= 0; f--)   // pushed highest first: every chain ascends
            {
                next[f] = -1;
                if (triple[3 * f] < 0) continue;
                int b = Bucket(triple[3 * f], triple[3 * f + 1], triple[3 * f + 2]);
                next[f] = head[b]; head[b] = f;
            }
        }

        public bool Registered(int f) => triple[3 * f] >= 0;

        // The lowest face on the same triple as `f` (f itself when it leads its group).
        public int First(int f) => Match(head[Bucket(triple[3 * f], triple[3 * f + 1], triple[3 * f + 2])], f);

        // The next higher face on the same triple as `f`, -1 after the last.
        public int Next(int f) => Match(next[f], f);

        int Match(int g, int f)
        {
            int a = triple[3 * f], b = triple[3 * f + 1], c = triple[3 * f + 2];
            while (g >= 0 && (triple[3 * g] != a || triple[3 * g + 1] != b || triple[3 * g + 2] != c)) g = next[g];
            return g;
        }

        int Bucket(int a, int b, int c)
        {
            unchecked
            {
                ulong h = (ulong)(uint)a * 0x9E3779B97F4A7C15UL;
                h ^= (ulong)(uint)b * 0xC2B2AE3D27D4EB4FUL;
                h ^= (ulong)(uint)c * 0x165667B19E3779F9UL;
                h ^= h >> 31; h *= 0x9E3779B97F4A7C15UL;
                return (int)(h >> 32) & mask;
            }
        }
    }
}
