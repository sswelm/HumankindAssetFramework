// HafNativeKernels.cs - the native plugin behind the probe's heaviest kernels (2026-10-21): the custom-normal encode and
// decode (VehicleProbe.CustomNormals.cs), the visibility BVH's constructor and BuildPartMesh's world transform
// (VehicleProbe.Visibility.cs), and the prep's edge collapse (HafNativeKernels.Collapse.cs). Measured in Unity 2021.3's Mono before this: the Saleg's Revenge probe took 2.86 s, of which
// the fan walk alone was 5.4 s of CPU; the same C++ (native/hafnormals.cpp, native/hafkernels.cpp) runs it in 0.59 s and
// the whole probe in 0.87 s, every output hash equal (docs/Testing.md, Native kernels). Mono's code generation, not the
// algorithm, was the cost: the same C# runs 2.5-3.4x faster on the .NET Framework's JIT.
//
// Every kernel keeps its managed twin: the C# is the specification, the drill oracle (tools/native_kernels_drill.sh holds
// the DLL to it byte for byte on the recipe sources) and the fallback when the DLL is absent, 32-bit, or switched off
// (HAF_NATIVE_KERNELS=0). A caller asks Available ONCE per call site and takes one path or the other whole; nothing here
// mixes the two inside a mesh. Status says which path runs and why; the drills print it, so a managed run can never pass
// for a native one (the "guards subtract what they cannot see" rule).
//
// Loading: Unity resolves DllImport("hafkernels") to the package's native plugin (editor/Plugins/x86_64/hafkernels.dll,
// Editor-only, x86_64). Outside Unity - the tests and the drills, each compiled into its own folder - the DLL is loaded
// by path first: HAF_KERNELS_DLL, or hafkernels.dll beside this assembly; the import by name then binds to the module
// already loaded. The DLL's haf_kernels_version must match Version, which also means ucrtbase's cosf/sinf resolved (the decode
// computes with Blender's C runtime, as BlenderTrig does); any other answer keeps the managed path.
//
// The arrays cross as pinned pointers (blittable float/int/short), the sharp-face flags as one byte each; no unsafe code,
// so every drill's csc line compiles this file unchanged. A native DLL loaded by the Unity editor is never unloaded:
// rebuilding it (tools/build_native_kernels.sh) needs an editor restart to take effect.
using System;
using System.IO;
using System.Runtime.InteropServices;

public static partial class HafNativeKernels
{
    public const string DllName = "hafkernels";
    public const int Version = 7;   // 7: the reduce stage's BMesh build, double finder and limited dissolve (hafbmesh.cpp); 6: the weld classes and the side ladder (hafweld.cpp, hafcolumn.cpp); 5: segmented column-build ABI

    /// <summary>True when the kernels run natively: the DLL loaded, 64-bit, version matched, trig resolved, not switched off. Set once
    /// here; mutable so Bake Tests and Tests/NativeKernelsTests.cs can call both twins in one host (the JIT bakes a static
    /// readonly bool into code compiled after type initialization, and reflection would then change nothing).</summary>
    public static bool Available;
    /// <summary>What the loader found, once: "native (hafkernels.dll version ...)" or "managed (...)" with the reason.</summary>
    public static readonly string LoadStatus;
    /// <summary>One line saying which path RUNS NOW and why. Equal to LoadStatus unless a caller switched Available off while
    /// the DLL is loaded (the Bake Tests' "C# only" pass, the unit tests' twin comparisons): then it says so, so a log line
    /// written during such a pass ("kernels managed (switched off ...)") cannot claim the native path ran.</summary>
    public static string Status => Available || LoadStatus == null || !LoadStatus.StartsWith("native", StringComparison.Ordinal)
        ? LoadStatus : "managed (switched off by the caller; loaded: " + LoadStatus + ")";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)] static extern int haf_kernels_version();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_world_transform(float[] Pb, float[] blenderN, float[] mb, float[] inverse, int first, int last, float[] Local, float[] World, float[] Normal, float[] PreviewNormal);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_bvh_build(float[] v, int[] tris, int nt, int workers, float[] bmin, float[] bmax, int[] left, int[] start, int[] count, int[] tOut);
    // The sharp-face flags cross as a byte array: a bool[] with MarshalAs(LPArray, I1) is marshalled one byte per flag by
    // the .NET Framework and FOUR bytes per flag by Unity's Mono, which ignores the sub-type - the C++ then read every
    // fourth flag and the Khelandion's and Triconter's hashes differed in Unity only (2026-10-21, caught after the first
    // push because the drill ran on .NET alone; the drill's Unity leg exists for this).
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_encode_custom(float[] P, int nv, int[] tris, int nf, float[] N, byte[] sharpFace, short[] d0, short[] d1, int threads);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void haf_decode_custom(float[] P, int nv, int[] tris, int nf, byte[] sharpFace, short[] d0, short[] d1, float[] result, int threads);
    static byte[] Flags(bool[] sharpFace, int nf) { var bytes = new byte[nf]; for (int f = 0; f < nf; f++) bytes[f] = sharpFace[f] ? (byte)1 : (byte)0; return bytes; }

    // The C++ has no bounds checks: a malformed input that the managed twin answers with IndexOutOfRangeException (a corner
    // index past the vertices, a shorts array shorter than the corners - Tests/ParallelNormalsTests plants one) would read
    // or write past a buffer there. Every entry checks what the kernel will index, and throws the twin's exception first.
    static void Need(bool ok, string what) { if (!ok) throw new IndexOutOfRangeException("native kernels: " + what); }
    static void CheckFaces(int[] tris, int nv)
    {
        Need(tris.Length % 3 == 0, "triangles: " + tris.Length + " corners is not a multiple of 3");
        for (int c = 0; c < tris.Length; c++) if ((uint)tris[c] >= (uint)nv) throw new IndexOutOfRangeException("native kernels: corner " + c + " names vertex " + tris[c] + " of " + nv);
    }

    /// <summary>EncodeCustomShorts: N holds 3 floats per vertex (NaN x = no file normal), sharpFace one flag per face, d0/d1 one short per corner.</summary>
    internal static void EncodeCustom(float[] P, int[] tris, float[] N, bool[] sharpFace, short[] d0, short[] d1, int threads)
    {
        int nv = P.Length / 3, nf = tris.Length / 3;
        CheckFaces(tris, nv);
        Need(N.Length >= nv * 3, "normals: " + N.Length + " floats for " + nv + " vertices");
        Need(sharpFace.Length >= nf, "sharp faces: " + sharpFace.Length + " flags for " + nf + " faces");
        Need(d0.Length >= nf * 3 && d1.Length >= nf * 3, "custom shorts: " + d0.Length + "/" + d1.Length + " for " + nf * 3 + " corners");
        haf_encode_custom(P, nv, tris, nf, N, Flags(sharpFace, nf), d0, d1, threads);
    }

    /// <summary>DecodeCustomShorts without sharp edges: result holds 3 floats per vertex.</summary>
    internal static void DecodeCustom(float[] P, int[] tris, bool[] sharpFace, short[] d0, short[] d1, float[] result, int threads)
    {
        int nv = P.Length / 3, nf = tris.Length / 3;
        CheckFaces(tris, nv);
        Need(sharpFace.Length >= nf, "sharp faces: " + sharpFace.Length + " flags for " + nf + " faces");
        Need(d0.Length >= nf * 3 && d1.Length >= nf * 3, "custom shorts: " + d0.Length + "/" + d1.Length + " for " + nf * 3 + " corners");
        Need(result.Length >= nv * 3, "result: " + result.Length + " floats for " + nv + " vertices");
        haf_decode_custom(P, nv, tris, nf, Flags(sharpFace, nf), d0, d1, result, threads);
    }

    /// <summary>TriangleBvh's constructor: the per-node arrays hold capacity = max(1, 2 nt - 1) slots, tOut the triangles in leaf order.</summary>
    internal static void BvhBuild(float[] v, int[] tris, int workers, float[] bmin, float[] bmax, int[] left, int[] start, int[] count, int[] tOut)
    {
        int nv = v.Length / 3, nt = tris.Length / 3, capacity = Math.Max(1, 2 * nt - 1);
        CheckFaces(tris, nv);
        Need(bmin.Length >= capacity * 3 && bmax.Length >= capacity * 3 && left.Length >= capacity && start.Length >= capacity && count.Length >= capacity, "bvh slots for " + nt + " triangles");
        Need(tOut.Length >= nt * 3, "bvh triangles: " + tOut.Length + " for " + nt * 3 + " corners");
        haf_bvh_build(v, tris, nt, workers, bmin, bmax, left, start, count, tOut);
    }

    /// <summary>BuildPartMesh's world transform of vertices [first, last): 3 floats per vertex in every array, 16 in both matrices.</summary>
    internal static void WorldTransform(float[] Pb, float[] blenderN, float[] mb, float[] inverse, int first, int last, float[] Local, float[] World, float[] Normal, float[] PreviewNormal)
    {
        Need(first >= 0 && last >= first, "vertex range " + first + ".." + last);
        Need(mb.Length >= 16 && inverse.Length >= 16, "matrices");
        foreach (var a in new[] { Pb, blenderN, Local, World, Normal, PreviewNormal }) Need(a.Length >= last * 3, "vertex buffers: " + a.Length + " floats for vertices up to " + last);
        haf_world_transform(Pb, blenderN, mb, inverse, first, last, Local, World, Normal, PreviewNormal);
    }

    static HafNativeKernels()
    {
        Available = false;
        if (Environment.GetEnvironmentVariable("HAF_NATIVE_KERNELS") == "0") { LoadStatus = "managed (HAF_NATIVE_KERNELS=0)"; return; }
        if (IntPtr.Size != 8) { LoadStatus = "managed (32-bit process; the DLL is x86_64)"; return; }
        string loaded = null;
        foreach (var candidate in Candidates())
        {
            if (candidate == null || !File.Exists(candidate)) continue;
            try { if (LoadLibraryW(candidate) != IntPtr.Zero) { loaded = candidate; break; } } catch { }
        }
        try
        {
            int version = haf_kernels_version();
            if (version == Version) { Available = true; LoadStatus = "native (" + DllName + ".dll version " + version + (loaded != null ? ", " + loaded : "") + ")"; }
            else LoadStatus = "managed (" + DllName + ".dll answered version " + version + ", expected " + Version + (version == -1 ? ": ucrtbase cosf/sinf did not resolve" : "") + ")";
        }
        catch (Exception e) { LoadStatus = "managed (" + e.GetType().Name + ": " + e.Message + ")"; }
    }

    static System.Collections.Generic.IEnumerable<string> Candidates()
    {
        yield return Environment.GetEnvironmentVariable("HAF_KERNELS_DLL");
        string beside = null;
        try { beside = Path.Combine(Path.GetDirectoryName(typeof(HafNativeKernels).Assembly.Location) ?? "", DllName + ".dll"); } catch { }
        yield return beside;
    }
}
