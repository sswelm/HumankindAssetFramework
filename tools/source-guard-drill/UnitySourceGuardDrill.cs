using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

// Runs in the throwaway project created by vehicle_source_guard_unity_drill.sh. Every source is a sentinel in
// that project; a refusal must precede deleting it, writing sidecars or starting either Generate backend.
public static class UnitySourceGuardDrill
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string link, string existing, IntPtr reserved);

    public static void Run()
    {
        var log = new System.Text.StringBuilder(); int failed = 0;
        void Check(string name, bool ok) { log.AppendLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }
        string root = Directory.GetParent(Application.dataPath).FullName;
        try
        {
            string aliases = Path.Combine(root, "Logs/source_alias"); Directory.CreateDirectory(aliases);
            string sourceAlias = Path.Combine(aliases, "source.glb").Replace('\\', '/');
            string outputAlias = Path.Combine(aliases, "alias_Spin.glb").Replace('\\', '/');
            string recipeAlias = Path.Combine(aliases, "alias.json");
            File.WriteAllText(sourceAlias, "SOURCE");
            if (!CreateHardLink(outputAlias, sourceAlias, IntPtr.Zero)) throw new IOException("could not create a private hard-link fixture");
            File.WriteAllText(recipeAlias, JsonConvert.SerializeObject(new { srcFile = sourceAlias, outGlb = "unused.glb", parts = new object[0] }));
            bool aliasOk = VehicleLabWindow.GenerateHeadless(recipeAlias, "Logs/source_alias", out _, out string aliasStatus, out _);
            Check("headless filesystem alias refused under Unity Mono", !aliasOk && aliasStatus.Contains("filesystem alias") && File.ReadAllText(sourceAlias) == "SOURCE" && File.Exists(outputAlias));
            foreach (bool second in new[] { false, true })
            {
                string label = second ? "second" : "raw", work = "Logs/source_guard_" + label;
                string dir = Path.Combine(root, work); Directory.CreateDirectory(dir);
                string output = Path.Combine(dir, "headless_Spin.glb").Replace('\\', '/');
                string other = Path.Combine(dir, "other.glb").Replace('\\', '/');
                File.WriteAllText(output, "SOURCE"); File.WriteAllText(other, "OTHER");
                string recipe = Path.Combine(dir, "headless.json");
                File.WriteAllText(recipe, JsonConvert.SerializeObject(new { srcFile = second ? other : output, srcFile2 = second ? output : "", outGlb = "unused.glb", parts = new object[0] }));
                bool ok = VehicleLabWindow.GenerateHeadless(recipe, work, out _, out string status, out _);
                Check("headless " + label + " source preserved before output deletion", !ok && status.Contains("Refused") && File.Exists(output) && File.ReadAllText(output) == "SOURCE");
            }
            foreach (bool second in new[] { false, true })
            {
                string label = second ? "second" : "raw", work = "Logs/preview_guard_" + label;
                string dir = Path.Combine(root, work); Directory.CreateDirectory(dir);
                string preview = Path.Combine(dir, "rig_preview.fbx").Replace('\\', '/');
                string other = Path.Combine(dir, "other.glb").Replace('\\', '/');
                File.WriteAllText(preview, "SOURCE"); File.WriteAllText(other, "OTHER");
                var w = ScriptableObject.CreateInstance<VehicleLabWindow>();
                try
                {
                    void Set(string name, object value) => typeof(VehicleLabWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, value);
                    Set("headless", true); Set("headlessDir", work);
                    Set("srcFile", second ? other : preview); Set("srcFile2", second ? preview : "");
                    Set("outGlb", Path.Combine(dir, "rig.glb"));
                    typeof(VehicleLabWindow).GetMethod("Vehicleize", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(w, null);
                    string status = (string)typeof(VehicleLabWindow).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(w);
                    Check("preview " + label + " source refused before Generate", status.Contains("Refused") && status.Contains("preview FBX") && File.ReadAllText(preview) == "SOURCE"
                        && !File.Exists(Path.Combine(dir, "rig_wheels.txt")));
                }
                finally { UnityEngine.Object.DestroyImmediate(w); }
            }
        }
        catch (Exception ex) { failed++; log.AppendLine("FAIL " + ex); }
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-hafParityOutput") File.WriteAllText(args[i + 1], log.ToString());
        Debug.Log(log.ToString()); EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
