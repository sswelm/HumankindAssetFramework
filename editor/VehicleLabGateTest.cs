// VehicleLabGateTest.cs — run via Tools ▸ HAF ▸ Bake Tests… (BakeTestRunnerWindow).
// THE VEHICLE LAB'S GENERATE, UNDER TEST (2026-09-25, user: "could you add ... extra generate test to the bake test").
// Until now nothing automated ran vehicle_rig.py end to end: the Bake Tests' rig rows exercise the BAKER's conversion
// (rig_anim.py), the C# suite the Lab window's rules, the python suite the script's pure helpers. A broken reduce tier,
// a lost Gun bone or a changed oar stroke passed every row. These rows drive the SAME Vehicleize the Generate button
// runs (VehicleLabWindow.GenerateHeadless: a window instance with no dialogs, no preview, every file under
// Logs/bake_tests/lab and nothing under Assets/) on the saved recipes, and per recipe:
//   1. require the script's own completion marker and the output GLB (the Generate button's success rule),
//   2. require the armature within Amplitude's 256-bone cap,
//   3. diff the run's deterministic summary lines (BakeGoldenRules.LabSnapshotLines: every "VEHICLE ..." line but the
//      timings, the output path cut off) against a blessed golden, Tools/lab_golden/<recipe>.txt — the deploy row's
//      golden-master idea. A missing golden is captured from the run and reported as such (not a pass); to re-bless
//      after an intended change, delete the golden and run again; a mismatch leaves the run's lines in
//      Logs/bake_tests/lab/<recipe>.candidate.txt.
// Two rows, one exclusive group: the REPRESENTATIVES (the first recipe marking oars, a gun, wheels, sails, a rotor,
// tracks — BakeGoldenRules.Representatives) for the quick set, EVERY recipe before a release or after touching
// vehicle_rig.py. A recipe whose source model is gone is skipped.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class VehicleLabGateTest
{
    const string RecipesDir = "Assets/FactorySource/VehicleLab/Recipes";
    const string GoldenDir = "Tools/lab_golden";
    const string WorkDir = "Logs/bake_tests/lab";   // project-relative: the headless Lab's names files, preview FBX and output GLB land here

    public static BakeTestSection RunRepresentativesSection() => Run("Vehicle Lab — representative recipes", representatives: true);
    public static BakeTestSection RunAllSection() => Run("Vehicle Lab — every recipe", representatives: false);

    static BakeTestSection Run(string title, bool representatives)
    {
        BakeTestSection Bad(string why) { Debug.LogError("[LabGate] " + why); return new BakeTestSection { title = title, fail = 1, body = "FAIL: " + why }; }
        BakeTestSection Skip(string why) { Debug.LogWarning("[LabGate] " + why); return new BakeTestSection { title = title, skip = 1, body = "SKIP — " + why }; }
        // Absent prerequisites are a SKIP in an installed package (Tools/ does not ship yet; Blender may not be
        // installed) and a loud FAIL at home, where their absence means the dev machine is broken (as in ConversionGateTest).
        if (string.IsNullOrEmpty(UniversalBaker.FindBlender()))
            return HafPackageContext.RunningAsPackage ? Skip("Blender was not found on this machine.") : Bad("Blender not found — the Vehicle Lab rows need it");
        if (!File.Exists(HafPackageContext.ToolPath("vehicle_rig.py")))
            return HafPackageContext.RunningAsPackage ? Skip("the Blender helper scripts (Tools/) are not in the installed package yet.") : Bad("Tools/vehicle_rig.py missing");
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        string recipesDir = Path.Combine(projRoot, RecipesDir);
        if (!Directory.Exists(recipesDir)) return Skip("no Vehicle Lab recipes (" + RecipesDir + ") — nothing to generate.");
        var recipes = Directory.GetFiles(recipesDir, "*.json").OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
        if (recipes.Count == 0) return Skip("no Vehicle Lab recipes (" + RecipesDir + ") — nothing to generate.");
        if (representatives)
        {
            var byName = recipes.ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => p, StringComparer.OrdinalIgnoreCase);
            var picked = BakeGoldenRules.Representatives(byName.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                n => VehicleLabWindow.ReadRecipe(byName[n], out _, out var roles) ? roles : new HashSet<string>());
            recipes = picked.Select(n => byName[n]).ToList();
            if (recipes.Count == 0) return Skip("no recipe marks oars, a gun, wheels, sails, a rotor or tracks — nothing representative to generate.");
        }
        string goldDir = Path.Combine(projRoot, GoldenDir);
        string workDir = Path.Combine(projRoot, WorkDir);
        Directory.CreateDirectory(workDir);
        int pass = 0, fail = 0, skip = 0;
        var lines = new List<string>();
        string current = null;
        try
        {
            for (int i = 0; i < recipes.Count; i++)
            {
                string recipe = recipes[i]; string name = Path.GetFileNameWithoutExtension(recipe); current = name;
                BakeTestRunnerWindow.Progress.Step($"{name} ({i + 1}/{recipes.Count}) — Generate through Blender…", (float)i / recipes.Count);
                if (!VehicleLabWindow.ReadRecipe(recipe, out string src, out _)) { lines.Add($"SKIP {name} — not a Vehicle Lab recipe"); skip++; continue; }
                if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) { lines.Add($"SKIP {name} — source model missing ({src})"); skip++; continue; }
                var w = System.Diagnostics.Stopwatch.StartNew();
                bool ok = VehicleLabWindow.GenerateHeadless(recipe, WorkDir, out string stdout, out string status, out string outGlb);
                w.Stop();
                string took = FormattableString.Invariant($"{w.Elapsed.TotalSeconds:0}s");
                try
                {
                    if (!ok) { lines.Add($"FAIL {name} ({took}) — Generate did not complete: {FirstLine(status)}"); fail++; continue; }
                    var snap = BakeGoldenRules.LabSnapshotLines(stdout);
                    int bones = BakeGoldenRules.ArmatureBones(snap);
                    if (bones < 0) { lines.Add($"FAIL {name} ({took}) — the run printed no armature line"); fail++; continue; }
                    if (bones > 256) { lines.Add($"FAIL {name} ({took}) — {bones} bones, over Amplitude's 256"); fail++; continue; }
                    string goldFile = Path.Combine(goldDir, name + ".txt");
                    if (!File.Exists(goldFile))
                    {
                        Directory.CreateDirectory(goldDir);
                        File.WriteAllLines(goldFile, snap);
                        lines.Add($"CAPTURED {name} ({took}, {bones} bones) — no golden yet; this run's {snap.Length} summary line(s) are now {GoldenDir}/{name}.txt (not a pass — run again to verify)");
                        skip++; continue;
                    }
                    string diff = BakeGoldenRules.Diff(File.ReadAllLines(goldFile), snap);
                    if (diff == null) { lines.Add($"PASS {name} ({took}, {bones} bones, golden match)"); pass++; }
                    else
                    {
                        File.WriteAllLines(Path.Combine(workDir, name + ".candidate.txt"), snap);
                        lines.Add($"FAIL {name} ({took}) — the rig CHANGED vs golden: {diff}. Candidate in {WorkDir}/{name}.candidate.txt; delete {GoldenDir}/{name}.txt and run again to re-bless an intended change.");
                        fail++;
                    }
                }
                finally { Cleanup(workDir, name); }
                BakeTestRunnerWindow.Progress.ThrowIfCancelled();
            }
        }
        catch (OperationCanceledException)
        { lines.Add($"CANCELLED by the user at {current ?? "start"} — the recipes above are complete, the rest did not run"); }
        Debug.Log($"[LabGate] {pass} pass, {fail} fail, {skip} skipped/captured (of {recipes.Count} recipes).");
        return new BakeTestSection { title = title, pass = pass, fail = fail, skip = skip, body = string.Join("\n", lines) };
    }

    static string FirstLine(string s) => (s ?? "").Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(no status)";

    // The run's files — <name>_Spin.glb, <name>_Spin_<role>.txt, <name>_Spin_preview.fbx — go; a candidate stays.
    static void Cleanup(string workDir, string name)
    {
        try
        {
            foreach (var f in Directory.GetFiles(workDir, name + "_Spin*"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
