// BlenderTrig.cs - cosf and sinf as Blender computes them (step 5 of replacing Blender, milestone c, 2026-10-05). Blender's
// C and C++ call the Windows C runtime's float functions, and those are NOT the double cosine rounded to float: on the
// 64-bit runtime 1,665 of 3.3 million sampled floats in (0, 2 pi] differ by one ulp (measured with .NET 8 against
// ucrtbase.dll 10.0.26100; Blender's own shipped copy, 10.0.22000, gives the same results on the whole sample). One ulp
// in a decoded custom normal is one ulp in a vertex normal, which moves an edge's place in the Decimate heap: the Bremen's
// railing (Object_40) collapsed a different rung, 17 of 14,043 vertex normals being that ulp off.
// So the port calls the same function. The 32-bit runtime exports no cosf at all (there it is a macro over cos), so a
// 32-bit process - the drills' Mono - cannot be exact: Exact is false, the fallback is the rounded double, and callers that
// need Blender's bits (BlenderReduce on a mesh with normals) keep Blender. Unity's editor is a 64-bit process.
using System;
using System.Runtime.InteropServices;

public static class BlenderTrig
{
    [DllImport("ucrtbase.dll", EntryPoint = "cosf", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    static extern float ucrt_cosf(float x);

    [DllImport("ucrtbase.dll", EntryPoint = "sinf", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    static extern float ucrt_sinf(float x);

    [DllImport("ucrtbase.dll", EntryPoint = "atan2f", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    static extern float ucrt_atan2f(float y, float x);

    [DllImport("ucrtbase.dll", EntryPoint = "_hypotf", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    static extern float ucrt_hypotf(float x, float y);

    [DllImport("ucrtbase.dll", EntryPoint = "asinf", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    static extern float ucrt_asinf(float x);

    [DllImport("ucrtbase.dll", EntryPoint = "acosf", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    static extern float ucrt_acosf(float x);

    /// <summary>True when cosf and sinf here ARE the 64-bit Windows C runtime's, the ones Blender calls.</summary>
    public static readonly bool Exact = Probe();

    static bool Probe()
    {
        if (IntPtr.Size != 8) return false;
        try
        {
            // 0x3afdfdfd is one of the inputs where the runtime's cosf (0x3f7fffe0) is not the rounded double (0x3f7fffe1)
            float x = BitConverter.ToSingle(BitConverter.GetBytes(0x3afdfdfdu), 0);
            return BitConverter.ToUInt32(BitConverter.GetBytes(ucrt_cosf(x)), 0) == 0x3f7fffe0u && ucrt_sinf(0f) == 0f;
        }
        catch (Exception) { return false; }   // no such library or entry point: not Windows, or a runtime without them
    }

    public static float Cosf(float x) => Exact ? ucrt_cosf(x) : (float)Math.Cos((double)x);
    public static float Sinf(float x) => Exact ? ucrt_sinf(x) : (float)Math.Sin((double)x);
    public static float Atan2f(float y, float x) => Exact ? ucrt_atan2f(y, x) : (float)Math.Atan2((double)y, (double)x);
    public static float Hypotf(float x, float y) => Exact ? ucrt_hypotf(x, y) : (float)Math.Sqrt((double)x * x + (double)y * y);
    public static float Asinf(float x) => Exact ? ucrt_asinf(x) : (float)Math.Asin((double)x);
    public static float Acosf(float x) => Exact ? ucrt_acosf(x) : (float)Math.Acos((double)x);

    // the double functions Blender's cubic solver calls (fcurve.cc: solve_cubic, sqrt3d)
    [DllImport("ucrtbase.dll", EntryPoint = "exp", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_exp(double x);

    [DllImport("ucrtbase.dll", EntryPoint = "log", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_log(double x);

    [DllImport("ucrtbase.dll", EntryPoint = "acos", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_acos(double x);

    [DllImport("ucrtbase.dll", EntryPoint = "cos", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_cos(double x);

    public static double Exp(double x) => Exact ? ucrt_exp(x) : Math.Exp(x);
    public static double Log(double x) => Exact ? ucrt_log(x) : Math.Log(x);
    public static double Acos(double x) => Exact ? ucrt_acos(x) : Math.Acos(x);
    public static double Cos(double x) => Exact ? ucrt_cos(x) : Math.Cos(x);
}
