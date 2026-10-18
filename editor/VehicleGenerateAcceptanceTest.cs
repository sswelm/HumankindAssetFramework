// Generate on both backends through the actual window entry point. The Blender run owns the
// golden's provenance; an automatic fallback is reported, never counted as C# acceptance.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class VehicleGenerateAcceptanceTest
{
    public static BakeTestSection RunSection()
    {
        string project = Directory.GetParent(Application.dataPath).FullName;
        string recipes = Path.Combine(project, "Assets/FactorySource/VehicleLab/Recipes");
        if (!Directory.Exists(recipes)) return new BakeTestSection { title = "Vehicle Generate backend acceptance", skip = 1, body = "SKIP: no saved Vehicle Lab recipes" };
        return RunRecipes(Directory.GetFiles(recipes, "*.json"), "Logs/bake_tests/lab_routes", Path.Combine(project, "Tools/lab_golden"));
    }

    internal static BakeTestSection RunRecipes(IEnumerable<string> recipes, string workRelative, string goldenDir)
    {
        var result = new BakeTestSection { title = "Vehicle Generate: Blender and C# outputs, logs, gun sidecars and Lab goldens" };
        var lines = new List<string>();
        if (string.IsNullOrEmpty(UniversalBaker.FindBlender()) || !File.Exists(HafPackageContext.ToolPath("vehicle_rig.py")))
        { result.fail++; result.body = "FAIL: Blender and vehicle_rig.py are required for backend acceptance"; return result; }
        var jobs = recipes.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
        string project = Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName);
        string runRelative = workRelative.TrimEnd('/', '\\') + "/" + Guid.NewGuid().ToString("N");
        string work = Path.GetFullPath(Path.Combine(project, runRelative));
        if (!work.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("acceptance output must stay in the project");
        bool retain = false;
        try
        {
            foreach (string recipe in jobs)
            {
                string name = Path.GetFileNameWithoutExtension(recipe);
                try
                {
                    if (!VehicleLabWindow.ReadRecipe(recipe, out string source, out _) || !File.Exists(source))
                    { result.skip++; lines.Add("SKIP " + name + ": source unavailable"); continue; }
                    BakeTestRunnerWindow.Progress.Step(name + " — Generate on both backends", (float)jobs.IndexOf(recipe) / Math.Max(1, jobs.Count));
                    bool a = VehicleLabWindow.GenerateHeadless(recipe, runRelative + "/blender", "blender", out string logA, out string statusA, out string pathA, out string routeA, out _);
                    bool b = VehicleLabWindow.GenerateHeadless(recipe, runRelative + "/csharp", "csharp", out string logB, out string statusB, out string pathB, out string routeB, out string fallback);
                    if (!a || !b) { result.fail++; retain = true; lines.Add("FAIL " + name + ": Generate failed: " + (!a ? statusA : statusB)); continue; }
                    var problems = CompareOutputs(pathA, logA, pathB, logB, out var snapshot);
                    if (routeA != "blender") problems.Add("the reference run did not use Blender");
                    bool usedFallback = routeB != "csharp";
                    if (usedFallback && (string.IsNullOrEmpty(fallback) || fallback.StartsWith("the C# rig run failed", StringComparison.Ordinal))) problems.Add("C# failed unexpectedly: " + fallback);
                    if (problems.Count > 0) { result.fail++; retain = true; lines.Add("FAIL " + name + ": " + string.Join("; ", problems)); continue; }
                    string golden = Path.Combine(goldenDir, name + ".txt");
                    var inputs = GoldenInputs(recipe, source);
                    if (!File.Exists(golden))
                    { BakeGoldenFiles.Capture(golden, snapshot, inputs); result.skip++; lines.Add("CAPTURED " + name + ": backend outputs match; first Blender golden captured, rerun to verify"); continue; }
                    var verdict = BakeGoldenFiles.Judge(golden, snapshot, inputs, out string reason, out string note);
                    if (verdict != BakeGoldenRules.GoldenVerdict.Match)
                    {
                        string candidate = Path.Combine(work, name + ".candidate.txt"); BakeGoldenFiles.Capture(candidate, snapshot, inputs);
                        result.fail++; retain = true; lines.Add("FAIL " + name + ": Lab golden — " + reason + "; candidate " + candidate); continue;
                    }
                    if (usedFallback) { result.skip++; lines.Add("FALLBACK " + name + ": Blender outputs and golden match; C# acceptance not exercised — " + fallback); continue; }
                    result.pass++; lines.Add("PASS " + name + ": byte-identical GLB, diagnostics and gun sidecar; read-back rig and Lab golden match" + note);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { result.fail++; retain = true; lines.Add("FAIL " + name + ": " + e.GetType().Name + ": " + e.Message); }
                BakeTestRunnerWindow.Progress.ThrowIfCancelled();
            }
        }
        finally
        {
            if (!retain && Directory.Exists(work)) try { Directory.Delete(work, true); } catch (IOException e) { lines.Add("Cleanup retained " + work + ": " + e.Message); }
        }
        if (jobs.Count == 0) { result.skip++; lines.Add("SKIP: no recipes"); }
        if (retain) lines.Add("Evidence retained in " + work);
        result.body = string.Join("\n", lines); return result;
    }

    internal static Dictionary<string,string> GoldenInputs(string recipe, string source)
    {
        var inputs = new Dictionary<string,string> { ["source"] = BakeGoldenRules.InputHash(File.ReadAllBytes(source)), ["recipe"] = BakeGoldenRules.InputHash(File.ReadAllBytes(recipe)) };
        string second = ((string)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(recipe))["srcFile2"] ?? "").Trim();
        if (second.Length > 0 && File.Exists(second)) inputs["source2"] = BakeGoldenRules.InputHash(File.ReadAllBytes(second));
        return inputs;
    }

    internal static List<string> CompareOutputs(string pathA, string logA, string pathB, string logB, out string[] snapshot)
    {
        var problems = new List<string>();
        var a = File.ReadAllBytes(pathA); var b = File.ReadAllBytes(pathB);
        if (!a.SequenceEqual(b)) problems.Add("output GLB bytes differ");
        GlbReader.Read(pathA); GlbReader.Read(pathB); // validate both actual written files
        GlbDisconnectedParts.RigSummary(a, out int skins, out int joints, out int meshes, out int meshNodes, out int rigged);
        if (rigged == 0 || joints > 256) problems.Add("the written GLB lacks a bound rig or exceeds 256 joints");
        var linesA = BakeGoldenRules.LabSnapshotLines(logA); var linesB = BakeGoldenRules.LabSnapshotLines(logB);
        if (!linesA.SequenceEqual(linesB)) problems.Add("VEHICLE summary lines differ");
        int announced = BakeGoldenRules.ArmatureBones(linesA);
        if (announced >= 0 && announced != joints) problems.Add("announced bone total differs from the written skin");
        string sideA = pathA + ".gun.txt", sideB = pathB + ".gun.txt";
        if (File.Exists(sideA) != File.Exists(sideB) || (File.Exists(sideA) && !File.ReadAllBytes(sideA).SequenceEqual(File.ReadAllBytes(sideB)))) problems.Add("gun span sidecars differ");
        snapshot = linesA.Concat(new[] { $"GLB skins={skins} joints={joints} meshes={meshes} meshNodes={meshNodes} rigged={rigged}" }).ToArray();
        return problems;
    }
}
