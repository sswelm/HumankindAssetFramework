using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Bake smoke test — the integration guard for the Model Factory.
//
// It re-bakes ONE representative model per bake-path (animated × material mode — that pairing selects the code path) and
// asserts each bake (a) completes without throwing and (b) produces non-empty _ModelMesh / _Skeleton / _Atlas assets. It
// bakes through ModelFactoryWindow.ConfigFor — the SAME config path the Bake button uses — so it can't drift from what
// ships. This is the check that would have caught the multi-material tangent regression (which threw inside Amplitude's
// MeshCollection.ImportMeshes) before it left the editor: unit tests can't reach that Unity/Amplitude seam, a real bake can.
//
// NON-DESTRUCTIVE: it bakes each model under a THROWAWAY resource name ("__smoketest__<name>") and deletes that output
// afterwards, so your real _ModelMesh/_Skeleton/_Atlas assets AND the registry are never touched. (An in-place re-bake
// would be destructive — the baker mints new asset GUIDs on each bake, and only the Bake button writes them back to the
// registry; a test that re-baked in place would desync the registry and break the models.)
//
// It is SLOW (each bake runs the full pipeline incl. Blender) and needs the source model file present. Run it before
// committing baker changes, not every save.
public static class BakeSmokeTest
{
    internal const string PREFIX = "__smoketest__";   // internal since 2026-08-23: the delete guard excludes fixtures BY THIS CONSTANT, not by a copy of it

    // No [MenuItem]s here any more — run these through Tools ▸ HAF ▸ Bake Tests… (BakeTestRunnerWindow), which
    // explains what each test does, shows the result per row, and writes the durable report.
    public static BakeTestSection RunRepresentativesSection()
    {
        var defs = ModelRegistry.Load();
        if (defs == null || defs.Count == 0) return NoModels("Smoke — one per bake path");
        // The path key includes the CONVERSION dimension (2026-07-19): an animated model with convertRig set goes
        // through the raw-rig conversion (rest normalization, rename, scale fold, unit-clean export) while the flag
        // off is the byte-identical legacy pipeline — two genuinely different code paths that each need a
        // representative (before this, whichever sorted first shadowed the other). (The trigger used to be
        // 'rotation != 0'; the explicit convertRig flag replaced it on 2026-07-18.)
        var reps = defs.Where(d => !d.resourceName.StartsWith(PREFIX))
                       .GroupBy(d => (d.animated, d.materialMode, converted: d.animated && d.convertRig))
                       .Select(g => g.First()).ToList();
        return Run(reps, "Smoke — one per bake path");
    }

    public static BakeTestSection RunAllSection() => RunAllSection(false);

    // NO MODEL IS BAKED TWICE IN ONE RUN (2026-08-22). With everything selected, this row and the conversion row
    // both baked every animated+convertRig model — 12 of them on the current registry, and a conversion bake is the
    // slowest kind there is. `skipConverted` is set by the runner ONLY when the conversion row is also selected, and
    // that row now runs this row's asset check on the bake it already does, so coverage is identical and the skipped
    // models are named in the body. Selected alone, this row still bakes everything, exactly as its title says.
    public static BakeTestSection RunAllSection(bool skipConverted)
    {
        var defs = ModelRegistry.Load();
        if (defs == null || defs.Count == 0) return NoModels("Smoke — ALL models");
        var all = defs.Where(d => !d.resourceName.StartsWith(PREFIX)).ToList();
        if (!skipConverted) return Run(all, "Smoke — ALL models");
        var handedOver = all.Where(IsConverted).Select(d => d.resourceName).OrderBy(x => x).ToList();
        var section = Run(all.Where(d => !IsConverted(d)).ToList(), "Smoke — ALL models");
        if (handedOver.Count > 0)
            section.body += $"\n\n{handedOver.Count} converted model(s) baked by the conversion row instead of twice — " +
                            "it asserts these same assets there: " + string.Join(", ", handedOver);
        return section;
    }

    // The conversion row's fixture set, defined once so the two rows cannot drift apart on what "converted" means.
    internal static bool IsConverted(ModelDef d) => d != null && d.animated && d.convertRig;

    static BakeTestSection NoModels(string title) => new BakeTestSection { title = title, fail = 1, body = "No models in the registry." };

    static BakeTestSection Run(List<ModelDef> models, string title)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0, skip = 0;
        try
        {
            for (int i = 0; i < models.Count; i++)
            {
                var src = models[i];
                string tag = (src.animated ? (src.convertRig ? "animated-conv" : "animated-legacy") : "static") + "/" + src.materialMode;
                string result;

                // TEXTURE-ONLY overrides (Unit Retexture entries, e.g. "Retex_<pawn>"): no model file and no extracted
                // source — there is nothing to bake, by design. Skipping is correct, not a failure.
                bool textureOnly = string.IsNullOrWhiteSpace(src.modelFile)
                    && !Directory.Exists(Path.Combine(Application.dataPath, "FactorySource", src.resourceName));
                if (textureOnly)
                {
                    sb.AppendLine($"[{tag}] {src.resourceName}: SKIP (texture-only override — nothing to bake)");
                    skip++;   // counted honestly as a skip, not smuggled into the pass count
                    continue;
                }

                if (src.reuseExtracted)
                {
                    // The model is deliberately cached (reuseExtracted) — forcing a fresh extraction here would bake it a
                    // way the user never does. Validate its EXISTING shipped assets instead (non-destructive, no re-bake).
                    EditorUtility.DisplayProgressBar("Bake Smoke Test", $"Checking {src.resourceName} ({i + 1}/{models.Count})…", (float)i / models.Count);
                    var missing = MissingAssets(src.resourceName, src.animated);
                    result = missing.Count == 0 ? "PASS (validated existing — reuseExtracted)" : "FAIL — missing/empty: " + string.Join(", ", missing);
                }
                else
                {
                    // reuseExtracted=false: bake fresh under a THROWAWAY name so the real assets + registry are untouched.
                    string testName = PREFIX + src.resourceName;
                    EditorUtility.DisplayProgressBar("Bake Smoke Test", $"Baking {src.resourceName} ({i + 1}/{models.Count})…", (float)i / models.Count);
                    try
                    {
                        var clone = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(src));   // clone: never mutate the real ModelDef
                        clone.resourceName = testName;
                        var cfg = ModelFactoryWindow.ConfigFor(clone);
                        var r = cfg.animated ? UniversalBaker.BuildAnimated(cfg) : UniversalBaker.Build(cfg);
                        if (!r.ok) result = "FAIL — bake error: " + r.error;
                        else
                        {
                            var missing = MissingAssets(testName, src.animated);
                            result = missing.Count == 0 ? "PASS (fresh bake)" : "FAIL — missing/empty: " + string.Join(", ", missing);
                        }
                    }
                    catch (Exception ex) { result = "FAIL — exception: " + ex.GetType().Name + ": " + ex.Message; }
                    finally { CleanupTestAssets(testName); }   // ALWAYS remove throwaway output, even on throw
                }

                if (result.StartsWith("PASS")) pass++; else fail++;
                sb.AppendLine($"[{tag}] {src.resourceName}: {result}");
            }
        }
        finally { EditorUtility.ClearProgressBar(); AssetDatabase.Refresh(); }

        return new BakeTestSection
        {
            title = title, pass = pass, fail = fail, skip = skip,
            body = sb.ToString().TrimEnd()
        };
    }

    // Passes only if the shipped assets exist and aren't empty stubs (a failed bake can leave a tiny/blank asset).
    // The ANIMATED path emits _Skeleton + _Atlas + THE ANIMATION (_Clips + its baked _ClipsPoseData bytes — checked
    // since 2026-07-19: an animated bake whose clip came out empty used to pass silently) but NO _ModelMesh (its mesh
    // lives in the skeleton); only the STATIC path writes _ModelMesh. 1 KB floor: real assets are far larger.
    // internal: the conversion row runs this same check on the bake it already does, so the catalog row can hand
    // those models over instead of baking them a second time (see RunAllSection(skipConverted)).
    internal static List<string> MissingAssets(string name, bool animated)
    {
        var bad = new List<string>();
        string root = Directory.GetParent(Application.dataPath).FullName;
        var required = animated ? new[] { "_Skeleton", "_Atlas", "_Clips" } : new[] { "_ModelMesh", "_Skeleton", "_Atlas" };
        foreach (var suffix in required)
        {
            string full = Path.Combine(root, "Assets", "Resources", name + suffix + ".asset");
            if (!File.Exists(full) || new FileInfo(full).Length < 1024) bad.Add(name + suffix);
        }
        if (animated)
        {
            // the pose stream is a .bytes TextAsset next to the _Clips asset — the actual animation data.
            // Floor is 32, NOT 1024: state-driven models freeze their primary clip to one frame (animClip
            // "Spin[0..0]") and ship pose streams as small as 48 bytes (VolleyGun/OrganGun, live in game).
            // The old 1 KB floor failed six such working models on 2026-08-20; 32 still catches empty stubs.
            string pose = Path.Combine(root, "Assets", "Resources", name + "_ClipsPoseData.bytes");
            if (!File.Exists(pose) || new FileInfo(pose).Length < 32) bad.Add(name + "_ClipsPoseData");
        }
        return bad;
    }

    // Delete every throwaway artifact for this test name: the baked Resources assets and the FactorySource extraction dir.
    static void CleanupTestAssets(string testName)
    {
        try
        {
            string resDir = Path.Combine(Application.dataPath, "Resources");
            if (Directory.Exists(resDir))
                foreach (var f in Directory.GetFiles(resDir, testName + "*"))
                    if (!f.EndsWith(".meta")) AssetDatabase.DeleteAsset("Assets/Resources/" + Path.GetFileName(f));   // removes the .asset AND its .meta
            AssetDatabase.DeleteAsset("Assets/FactorySource/" + testName);   // the whole extraction folder
        }
        catch (Exception ex) { Debug.LogWarning($"[SmokeTest] cleanup of '{testName}' left something behind: {ex.Message}"); }
    }
}
