// HafNativeKernels.Column.cs - the Workshop fuse's column index and ray query (GlbDisconnectedParts.cs, BuildOccluders and
// Column) in native/hafcolumn.cpp. On the native path the fuse hands the occluder corners and the group's face corners
// across as doubles, once per group, and the C++ registers them into the ladder of column grids exactly as BuildOccluders
// does (the same rung rule, the same cells, the candidates of a cell in registration order through a stable sort) - no
// managed dictionary is built. A query then crosses as blittable arrays and comes back as the distance to the nearest thing
// in the column, +inf for nothing, from the same rungs, the same candidates in the same order, the same rejections and the
// same Moeller-Trumbore arithmetic, so the fuse's verdicts do not move. The corner arrays stay pinned for the index's life
// (the C++ reads them at every query); Release frees them, the finalizer does if nothing else did. Measured on the Saleg's
// Revenge (docs/Testing.md).
using System;
using System.Runtime.InteropServices;

public static partial class HafNativeKernels
{
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr haf_column_build(int levels, double[] cells, IntPtr occ, int nOcc, IntPtr P, int nFaces, double top, double bottom);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)] static extern void haf_column_free(IntPtr handle);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_column_query(IntPtr handle, int nQueries, int[] faces, byte[] upward, int[] skip, int nSkip, double[] best);

    /// <summary>A fuse group's column ladder, built and queried natively.</summary>
    internal sealed class ColumnIndex
    {
        IntPtr handle; GCHandle occPin, facePin; readonly int faceCount;
        internal static long BuildTicks, QueryTicks, QueryCalls;   // the harness and the drill read these

        /// <summary>occluders: 9 doubles per occluder triangle; faces: 9 doubles per group face; cells: the rung sizes, finest last.</summary>
        internal ColumnIndex(double[] cells, double[] occluders, double[] faces, double top, double bottom)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Need(cells.Length >= 1 && cells.Length <= 16, "column rungs: " + cells.Length);
            Need(occluders.Length % 9 == 0 && faces.Length % 9 == 0, "column corners: " + occluders.Length + " and " + faces.Length + " doubles");
            faceCount = faces.Length / 9;
            occPin = GCHandle.Alloc(occluders, GCHandleType.Pinned); facePin = GCHandle.Alloc(faces, GCHandleType.Pinned);
            handle = haf_column_build(cells.Length, cells, occPin.AddrOfPinnedObject(), occluders.Length / 9, facePin.AddrOfPinnedObject(), faceCount, top, bottom);
            System.Threading.Interlocked.Add(ref BuildTicks, clock.ElapsedTicks);
        }
        ~ColumnIndex() { Release(); }
        internal void Release()
        {
            if (handle != IntPtr.Zero) { haf_column_free(handle); handle = IntPtr.Zero; }
            if (occPin.IsAllocated) occPin.Free();
            if (facePin.IsAllocated) facePin.Free();
        }

        /// <summary>A skip set as the sorted array the query reads, checked once here (a query does not re-check it: a sheet's set
        /// can hold every face of the group, and the fuse asks tens of thousands of times).</summary>
        internal int[] SkipArray(System.Collections.Generic.ICollection<int> set)
        {
            var sorted = new int[set.Count]; set.CopyTo(sorted, 0); Array.Sort(sorted);
            for (int i = 0; i < sorted.Length; i++) Need((uint)sorted[i] < (uint)faceCount && (i == 0 || sorted[i - 1] < sorted[i]), "column skip set: distinct group faces");
            return sorted;
        }

        /// <summary>Column(f, upward, skip) for one face: skip from SkipArray, or null.</summary>
        internal double Query(int f, bool upward, int[] skip)
        {
            var best = new double[1];
            Query(new[] { f }, new[] { upward ? (byte)1 : (byte)0 }, skip, best);
            return best[0];
        }

        internal void Query(int[] faces, byte[] upward, int[] skip, double[] best)
        {
            int n = faces.Length;
            Need(handle != IntPtr.Zero, "column index released");
            Need(upward.Length >= n && best.Length >= n, "column query buffers for " + n);
            for (int i = 0; i < n; i++) Need((uint)faces[i] < (uint)faceCount, "column query face " + faces[i] + " of " + faceCount);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            haf_column_query(handle, n, faces, upward, skip ?? new int[1], skip == null ? 0 : skip.Length, best);
            System.Threading.Interlocked.Add(ref QueryTicks, clock.ElapsedTicks); System.Threading.Interlocked.Add(ref QueryCalls, n);
        }
    }
}
