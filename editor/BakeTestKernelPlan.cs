using System;

// WHICH CODE THE BAKE TESTS EXERCISE: the C++ kernels or their C# twins (2026-10-28, user: "it should now also test
// with c++" ... "perhaps we should make it configurable with a combobox"). Every kernel keeps a managed twin and the
// callers take one path or the other whole on HafNativeKernels.Available, a process-wide switch. The window used to run
// whatever the switch happened to hold, and the report never said which: a stale or missing DLL silently made every row a
// managed run that still passed. This plan is the pure half of the window's "Kernels" dropdown - no Unity in it, so the
// unit tests hold the table (Tests/BakeTestKernelPlanTests.cs):
//   - Native: the rows run with the kernels; when the DLL did not load they run managed and SAY SO in their titles.
//   - Managed: the rows that can reach a kernel run with the switch off ("[C# only]"), the oracle is the same.
//   - Both: a kernel row runs twice, kernels first, then managed - the goldens and Blender comparisons are the oracle for
//     each pass, so two green passes mean both paths agree with the reference (byte-exact where the golden is).
// A row that cannot reach a kernel runs once in every mode, untouched. The loader row at the top of the window answers
// "did the DLL load?" from HafNativeKernels.LoadStatus (what the loader found, not the live switch) and fails when it did
// not, except for the deliberate HAF_NATIVE_KERNELS=0.
public enum BakeTestKernelMode { Native = 0, Managed = 1, Both = 2 }

public static class BakeTestKernelPlan
{
    public static readonly string[] Labels = { "C++ kernels (native)", "C# only (managed)", "Both - kernel rows twice" };
    public const string PrefsKey = "HAF.BakeTests.Kernels";

    public struct Pass
    {
        public bool native;      // HafNativeKernels.Available for this pass
        public string suffix;    // appended to the row's title in the window and the report ("" = the plain row)
    }

    const string NativeSuffix = "  [C++ kernels]", ManagedSuffix = "  [C# only]",
                 NotLoadedSuffix = "  [C# only: the C++ kernels did not load]";

    /// <summary>True when the status line names the native path (HafNativeKernels.LoadStatus starts with "native").</summary>
    public static bool IsNative(string status) => status != null && status.StartsWith("native", StringComparison.Ordinal);

    /// <summary>True when the status says the operator switched the kernels off on purpose (HAF_NATIVE_KERNELS=0).</summary>
    public static bool SwitchedOff(string status) => status != null && status.Contains("HAF_NATIVE_KERNELS=0");

    public static BakeTestKernelMode ParseMode(int stored) =>
        stored >= 0 && stored < Labels.Length ? (BakeTestKernelMode)stored : BakeTestKernelMode.Native;

    /// <summary>The passes one row runs: <paramref name="kernelRow"/> says its pipeline can reach a kernel,
    /// <paramref name="loaded"/> whether the DLL loaded in this editor process.</summary>
    public static Pass[] Passes(BakeTestKernelMode mode, bool kernelRow, bool loaded)
    {
        if (!kernelRow) return new[] { new Pass { native = loaded, suffix = "" } };
        if (!loaded) return new[] { new Pass { native = false, suffix = mode == BakeTestKernelMode.Managed ? ManagedSuffix : NotLoadedSuffix } };
        switch (mode)
        {
            case BakeTestKernelMode.Managed: return new[] { new Pass { native = false, suffix = ManagedSuffix } };
            case BakeTestKernelMode.Both: return new[] { new Pass { native = true, suffix = NativeSuffix }, new Pass { native = false, suffix = ManagedSuffix } };
            default: return new[] { new Pass { native = true, suffix = "" } };
        }
    }

    /// <summary>The loader row's verdict: pass/fail/skip counts and the body, from the status line and the chosen mode.</summary>
    public static void Loader(string status, BakeTestKernelMode mode, out int pass, out int fail, out int skip, out string body)
    {
        pass = fail = skip = 0;
        string modeLine = "Kernels mode for this run: " + Labels[(int)mode] + ".";
        if (IsNative(status))
        {
            pass = 1;
            body = "PASS: " + status + "\n" + modeLine
                 + (mode == BakeTestKernelMode.Managed ? " The rows that can reach a kernel ran the C# twins; the DLL was loaded but not used." : "");
            return;
        }
        if (SwitchedOff(status))
        {
            skip = 1;
            body = "SKIP: " + status + " - switched off on purpose for this editor process; the rows ran the C# twins.\n" + modeLine;
            return;
        }
        fail = 1;
        body = "FAIL: the C++ kernels did not load - status: " + (status ?? "(none)") + "\n" + modeLine
             + " Every row that can reach a kernel ran the C# twins and says so in its title.\n"
             + "Remedy: a version mismatch means the editor still holds an older hafkernels.dll - restart Unity; a DllNotFoundException "
             + "means editor/Plugins/x86_64/hafkernels.dll is missing from the installed package; a 32-bit or non-Windows host has no native build.";
    }

    /// <summary>The report header's line.</summary>
    public static string HeaderLine(BakeTestKernelMode mode, string status) =>
        "Kernels: " + Labels[(int)mode] + "; loaded in this editor process: " + (status ?? "(none)");
}
