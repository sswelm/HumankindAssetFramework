using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

// Run in the temporary project made by vehiclelab_filter_unity_drill.sh, against the real window and JsonUtility.
public static class UnityVehicleLabFilterTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static FieldInfo Field(string name) => typeof(VehicleLabWindow).GetField(name, Private);
    static MethodInfo Method(string name) => typeof(VehicleLabWindow).GetMethod(name, Private);

    public static void Run()
    {
        var log = new System.Text.StringBuilder(); int failed = 0;
        void Check(string name, bool ok) { log.AppendLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }
        try
        {
            foreach (bool fast in new[] { false, true })
            {
                string kind = fast ? "source bones" : "mesh parts";
                string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "filter_recipe.json");
                var size = new { x = 1f, y = 1f, z = 1f };
                var rows = new[] {
                    new { name = "Wheel", verts = 3, center = new { x = 0f, y = -2500f, z = 2500f }, size, role = 1, vis = 1 },
                    new { name = "Interior", verts = 20, center = new { x = 0f, y = 3000f, z = -3000f }, size, role = 3, vis = 0 },
                    new { name = "Body", verts = 200, center = new { x = 0f, y = 0f, z = 0f }, size, role = 0, vis = -1 }
                };
                File.WriteAllText(path, JsonConvert.SerializeObject(new {
                    srcFile = "unused.glb", outGlb = "unused_Spin.glb", minVerts = 100,
                    parts = rows, boneParts = rows, useSourceRig = fast
                }));
                var w = ScriptableObject.CreateInstance<VehicleLabWindow>();
                try
                {
                    void Restrict()
                    {
                        Field("minVerts").SetValue(w, 2000); Field("minPartSize").SetValue(w, 2f);
                        Field("minHeight").SetValue(w, 5f); Field("maxHeight").SetValue(w, 6f);
                        Field("minWidth").SetValue(w, 5f); Field("maxWidth").SetValue(w, 6f);
                        Field("minFlatPct").SetValue(w, 100f);
                        Field("partFilter").SetValue(w, 1); Field("visFilter").SetValue(w, 1);
                    }
                    object[] Parts() => ((IEnumerable)Field(fast ? "boneParts" : "parts").GetValue(w)).Cast<object>().ToArray();
                    bool AllShown()
                    {
                        var parts = Parts();
                        return parts.Length == rows.Length && parts.All(p =>
                            (bool)Method("VisiblePart").Invoke(w, new[] { p }) &&
                            (bool)Method("MatchesFilter").Invoke(w, new[] { p.GetType().GetField("role").GetValue(p) }) &&
                            (bool)Method("MatchesVis").Invoke(w, new[] { p }));
                    }
                    int[] Roles() => Parts().Select(p => (int)p.GetType().GetField("role").GetValue(p)).ToArray();
                    Restrict();
                    Method("LoadRecipeFromPath").Invoke(w, new object[] { path });
                    Check(kind + ": recipe loaded", (bool)Field("recipeReadThisSession").GetValue(w));
                    Check(kind + ": load opens the saved vertex threshold", (int)Field("minVerts").GetValue(w) == 1);
                    Check(kind + ": load shows every part including distant parts", AllShown());
                    Check(kind + ": loaded recipe starts clean", (bool)Method("RecipeIsSavedClean").Invoke(w, null));
                    var roles = Roles();
                    Restrict();
                    Check(kind + ": fixture filters actually hide parts", !AllShown());
                    Method("ShowAllParts").Invoke(w, null);
                    Check(kind + ": Show all reveals every part", AllShown());
                    Check(kind + ": Show all retains marked roles", Roles().SequenceEqual(roles));
                    Check(kind + ": generation arguments still match the saved recipe", w.ArgsDisagreeWithReader(path) == null);
                    Check(kind + ": opening view filters leaves the loaded recipe clean", (bool)Method("RecipeIsSavedClean").Invoke(w, null));
                }
                finally { UnityEngine.Object.DestroyImmediate(w); }
            }
        }
        catch (Exception ex) { failed++; log.AppendLine("FAIL " + ex); }
        log.AppendLine("TOTAL failed " + failed);
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-hafFilterOutput");
        if (at >= 0 && at + 1 < args.Length) File.WriteAllText(args[at + 1], log.ToString());
        Debug.Log(log.ToString()); EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
