// Real Bake Tests runner execution in a temporary Unity project; synthetic rows isolate mode control from slow bakes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
public static class UnityKernelRunnerTests
{
    static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly List<string> Lines = new List<string>(); static int failed;
    static void Check(string name, bool ok, string detail = "") { Lines.Add((ok ? "PASS " : "FAIL ") + name + ": " + detail); if (!ok) failed++; }
    static object Get(object o, string field) => o.GetType().GetField(field, Fields).GetValue(o);
    static void Set(object o, string field, object value) => o.GetType().GetField(field, Fields).SetValue(o, value);
    static object Row(string name, bool kernel, Func<BakeTestSection> run)
    {
        var type = typeof(BakeTestRunnerWindow).GetNestedType("TestRow", BindingFlags.NonPublic);
        object row = Activator.CreateInstance(type, true);
        Set(row, "name", name); Set(row, "kernels", kernel); Set(row, "on", true); Set(row, "run", run); return row;
    }
    static void ModeCase(BakeTestKernelMode mode, bool initial, bool kernel, bool throws = false, bool cancel = false, bool plainAfter = false)
    {
        var window = ScriptableObject.CreateInstance<BakeTestRunnerWindow>();
        bool saved = HafNativeKernels.Available;
        var calls = new List<bool>(); var statuses = new List<string>(); var plainCalls = new List<bool>();
        string label = mode + " initial=" + initial + " kernel=" + kernel + " throws=" + throws + " cancel=" + cancel + " plainAfter=" + plainAfter;
        try
        {
            bool loaded = BakeTestKernelPlan.IsNative(HafNativeKernels.LoadStatus);
            object row = Row("Synthetic pipeline", kernel, () =>
            {
                calls.Add(HafNativeKernels.Available); statuses.Add(HafNativeKernels.Status);
                if (cancel) typeof(BakeTestRunnerWindow.Progress).GetProperty("CancelRequested", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, true, null);
                if (throws) throw new InvalidOperationException("expected row failure");
                return new BakeTestSection { pass = 1, body = "kernel switch " + HafNativeKernels.Available };
            });
            var rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(row.GetType())); rows.Add(row);
            if (plainAfter) rows.Add(Row("Unrelated pipeline", false, () => { plainCalls.Add(HafNativeKernels.Available); return new BakeTestSection { pass = 1 }; }));
            Set(window, "rows", rows); Set(window, "kernelMode", mode); HafNativeKernels.Available = initial;
            typeof(BakeTestRunnerWindow).GetMethod("StartRun", Fields).Invoke(window, new object[] { true });
            bool[] expected = !kernel ? new[] { initial } : !loaded || mode == BakeTestKernelMode.Managed ? new[] { false } :
                mode == BakeTestKernelMode.Both && !cancel ? new[] { true, false } : new[] { true };
            Check("runner mode " + label, calls.SequenceEqual(expected), "actual " + string.Join(",", calls) + "; expected " + string.Join(",", expected));
            Check("restores caller switch " + label, HafNativeKernels.Available == initial);
            Check("live status " + label, calls.Select((active, i) => active ? statuses[i].StartsWith("native", StringComparison.Ordinal) : statuses[i].StartsWith("managed", StringComparison.Ordinal)).All(x => x));
            var results = ((IEnumerable)Get(row, "results")).Cast<BakeTestSection>().ToArray();
            string report = File.ReadAllText((string)Get(window, "lastReportPath"));
            Check("persists every pass " + label, results.Length == expected.Length && results.All(r => report.Contains("== " + r.title + ":")));
            Check("report header " + label, report.Contains(BakeTestKernelPlan.HeaderLine(mode, HafNativeKernels.LoadStatus)));
            if (plainAfter) Check("unrelated row preserves incoming switch " + label, plainCalls.SequenceEqual(new[] { initial }));
            if (kernel && loaded && mode == BakeTestKernelMode.Both && !cancel)
                Check("distinct native and managed reports " + label, results.Length == 2 && results[0].title.Contains("[C++ kernels]") && results[1].title.Contains("[C# only]"));
            if (throws) Check("row failures retained " + label, results.All(r => r.fail == 1 && r.body.Contains("expected row failure")));
            if (cancel) Check("cancellation retained " + label, ((string)Get(window, "lastVerdict")).Contains("CANCELLED"));
        }
        finally { HafNativeKernels.Available = saved; UnityEngine.Object.DestroyImmediate(window); }
    }
    public static void Run()
    {
        try
        {
            bool loaded = BakeTestKernelPlan.IsNative(HafNativeKernels.LoadStatus);
            bool disabled = Environment.GetEnvironmentVariable("HAF_NATIVE_KERNELS") == "0";
            Check("loader matches process setup", disabled ? !loaded : loaded, HafNativeKernels.LoadStatus);
            var window = ScriptableObject.CreateInstance<BakeTestRunnerWindow>();
            try
            {
                var rows = ((IEnumerable)Get(window, "rows")).Cast<object>();
                var lab = rows.Where(r => ((Func<BakeTestSection>)Get(r, "run")).Method.DeclaringType == typeof(VehicleLabGateTest)).ToArray();
                Check("Blender-only Lab rows run once without kernel labels", lab.Length == 2 && lab.All(r => !(bool)Get(r, "kernels")), "Lab rows " + lab.Length);
                var conversions = rows.Where(r =>
                {
                    var method = ((Func<BakeTestSection>)Get(r, "run")).Method;
                    return method.DeclaringType == typeof(BlenderDeployHeadlessTest) ||
                        (method.DeclaringType == typeof(ConversionGateTest) &&
                         (method.Name == nameof(ConversionGateTest.RunLitmusSection) || method.Name == nameof(ConversionGateTest.RunRegistryConvertedSection)));
                }).ToArray();
                Check("conversion rows include native normal kernels", conversions.Length == 3 && conversions.All(r => (bool)Get(r, "kernels")), "conversion rows " + conversions.Length);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
            ModeCase(BakeTestKernelMode.Both, loaded, true);
            ModeCase(BakeTestKernelMode.Both, false, true);
            ModeCase(BakeTestKernelMode.Native, false, true);
            ModeCase(BakeTestKernelMode.Managed, loaded, true);
            ModeCase(BakeTestKernelMode.Both, false, false);
            ModeCase(BakeTestKernelMode.Both, loaded, true, plainAfter: true);
            ModeCase(BakeTestKernelMode.Both, false, true, plainAfter: true);
            ModeCase(BakeTestKernelMode.Managed, loaded, true, plainAfter: true);
            ModeCase(BakeTestKernelMode.Both, loaded, true, throws: true);
            ModeCase(BakeTestKernelMode.Both, loaded, true, cancel: true);
        }
        catch (Exception e) { Lines.Add("FAIL exception " + e); failed++; }
        Lines.Add("TOTAL failed " + failed);
        var args = Environment.GetCommandLineArgs(); File.WriteAllLines(args[Array.IndexOf(args, "-hafKernelOutput") + 1], Lines);
        EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
