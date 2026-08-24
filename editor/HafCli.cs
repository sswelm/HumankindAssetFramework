using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

// Headless CLI entry points for HAF authoring, callable via Unity batch mode. See docs/Headless-CLI-Design.md.
//
//   Unity.exe -batchmode -quit -projectPath <ENCReload> -logFile - -executeMethod HAF.Cli.RebuildModel -model <resourceName> [-fresh]
//   Unity.exe -batchmode -quit -projectPath <ENCReload> -logFile - -executeMethod HAF.Cli.RebuildModel -all
//   Unity.exe -batchmode -quit -projectPath <ENCReload> -logFile - -executeMethod HAF.Cli.CleanExport
//   Unity.exe -batchmode -quit -projectPath <ENCReload> -logFile - -executeMethod HAF.Cli.BuildMod [-strict]
//
// Each verb prints one JSON result line prefixed [HAF-CLI] and sets the process exit code:
//   0 = ok, 2 = not found / bad arg, 3 = bake or save failed, 4 = pre-ship validation failed (BuildMod -strict).
// RebuildModel reuses the EXACT path the Model Factory's Bake button and BakeSmokeTest use
// (ModelRegistry.Load -> ModelFactoryWindow.ConfigFor -> UniversalBaker.Build/BuildAnimated -> copy GUIDs -> Upsert),
// so it cannot drift from the GUI. BuildMod runs the game's own Mod Editor build+deploy headless (the three build
// steps past the batch-hostile DB gate, plus the version stamping the GUI panel normally does) — see BuildMod below.
namespace HAF
{
    public static class Cli
    {
        static string Arg(string key)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == key) return a[i + 1];
            return null;
        }
        static bool Flag(string key) => Environment.GetCommandLineArgs().Contains(key);
        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        static void Emit(int code, string json)
        {
            Debug.Log("[HAF-CLI] " + json);
            Console.Out.Flush();
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        // -executeMethod HAF.Cli.RebuildModel  -model <resourceName> [-fresh]   |   -all
        public static void RebuildModel()
        {
            try
            {
                var defs = ModelRegistry.Load();
                bool all = Flag("-all");
                string name = Arg("-model");
                if (!all && string.IsNullOrWhiteSpace(name)) { Emit(2, "{\"ok\":false,\"error\":\"pass -model <resourceName> or -all\"}"); return; }

                var targets = all
                    ? defs.Where(d => !string.IsNullOrWhiteSpace(d.modelFile)).ToList()
                    : defs.Where(d => d.resourceName == name).ToList();
                if (!all && targets.Count == 0) { Emit(2, "{\"ok\":false,\"error\":\"model '" + Esc(name) + "' not found in registry\"}"); return; }

                bool fresh = Flag("-fresh");
                int ok = 0, failed = 0;
                foreach (var cur in targets)
                {
                    if (string.IsNullOrWhiteSpace(cur.modelFile)) continue;
                    var cfg = ModelFactoryWindow.ConfigFor(cur);   // the one shared config path — can't drift from the GUI
                    if (fresh) cfg.reuseExtracted = false;         // force a full re-slim (default honours the entry's keep-texture setting)
                    var r = cfg.animated ? UniversalBaker.BuildAnimated(cfg) : UniversalBaker.Build(cfg);
                    if (!r.ok) { failed++; Debug.LogError("[HAF-CLI] bake FAILED " + cur.resourceName + ": " + r.error); continue; }

                    // Copy the baked GUIDs back exactly as ModelFactoryWindow.DoBake does.
                    cur.skel = ModelRegistry.ParseGuid(r.skeletonGuid);
                    cur.atlas = ModelRegistry.ParseGuid(r.atlasGuid);
                    cur.clip = cfg.animated ? ModelRegistry.ParseGuid(r.clipGuid) : new int[4];
                    bool sd = cfg.animated && cfg.animStateDriven;
                    cur.clipMove = sd ? ModelRegistry.ParseGuid(r.clipMoveGuid) : new int[4];
                    cur.clipAfter = sd && !string.IsNullOrEmpty(r.clipAfterGuid) ? ModelRegistry.ParseGuid(r.clipAfterGuid) : new int[4];
                    cur.clipAttack = sd && !string.IsNullOrEmpty(r.clipAttackGuid) ? ModelRegistry.ParseGuid(r.clipAttackGuid) : new int[4];
                    cur.clipCombat = sd && !string.IsNullOrEmpty(r.clipCombatGuid) ? ModelRegistry.ParseGuid(r.clipCombatGuid) : new int[4];
                    cur.clipPreMove = sd && !string.IsNullOrEmpty(r.clipPreMoveGuid) ? ModelRegistry.ParseGuid(r.clipPreMoveGuid) : new int[4];
                    cur.clipIdle = sd && !string.IsNullOrEmpty(r.clipIdleGuid) ? ModelRegistry.ParseGuid(r.clipIdleGuid) : new int[4];
                    cur.clipIdleAlt = sd && !string.IsNullOrEmpty(r.clipIdleAltGuid) ? ModelRegistry.ParseGuid(r.clipIdleAltGuid) : new int[4];
                    cur.clipIdleAlt2 = sd && !string.IsNullOrEmpty(r.clipIdleAlt2Guid) ? ModelRegistry.ParseGuid(r.clipIdleAlt2Guid) : new int[4];

                    if (!ModelRegistry.Upsert(cur)) { failed++; Debug.LogError("[HAF-CLI] registry save FAILED " + cur.resourceName); continue; }
                    ok++;
                    Debug.Log("[HAF-CLI] rebuilt " + cur.resourceName + (cfg.animated ? " (animated)" : " (static)"));
                }
                AssetDatabase.SaveAssets();
                Emit(failed == 0 ? 0 : 3, "{\"ok\":" + (failed == 0 ? "true" : "false") + ",\"rebuilt\":" + ok + ",\"failed\":" + failed + "}");
            }
            catch (Exception ex) { Emit(3, "{\"ok\":false,\"error\":\"" + Esc(ex.ToString()) + "\"}"); }
        }

        // -executeMethod HAF.Cli.CleanExport   removes the previous ENCReload export from Humankind's Community folder
        // (the "An error happens while trying to move your mod ... is denied" fix — mirrors Clean-ENCReload-Export.bat,
        // scoped to ENCReload's own mod GUID only). Run before a mod build.
        public static void CleanExport()
        {
            try
            {
                // Resolved per machine (HafPaths). BATCH MODE HAS NOBODY TO ASK, so an unknown folder fails
                // loudly with the fix named — the previous hardcoded const just found nothing and reported
                // "ok, removed 0", which is the same output as a successful clean.
                string community = HafPaths.CommunityDir;
                if (string.IsNullOrEmpty(community))
                {
                    Emit(2, "{\"ok\":false,\"error\":\"Humankind's Community folder not found. Open the editor once and " +
                            "use Tools > HAF > Ship Status > Locate..., or set the EditorPrefs string '" +
                            HafPaths.PrefCommunity + "' to its path.\"}");
                    return;
                }
                const string modGuid = "cd3480e932114f8084db755ddd65f2d8";
                int removed = 0;
                if (Directory.Exists(community))
                    foreach (var dir in Directory.GetDirectories(community, "ENCReload." + modGuid + ".*"))
                    {
                        Directory.Delete(dir, true);
                        removed++;
                        Debug.Log("[HAF-CLI] removed export " + Path.GetFileName(dir));
                    }
                Emit(0, "{\"ok\":true,\"removed\":" + removed + "}");
            }
            catch (Exception ex) { Emit(3, "{\"ok\":false,\"error\":\"" + Esc(ex.ToString()) + "\"}"); }
        }

        // -executeMethod HAF.Cli.BuildMod   FULL build + deploy — the game's own Mod Editor build, headless.
        //
        // ModuleEditor.BuildModification runs a pre-build DatabaseChecker and, in BATCH MODE, HARD-ABORTS on any DB error
        // — where the editor instead shows a "database has errors, build anyway?" dialog you click past. ENC trips a
        // SPURIOUS validation error (the pre-build check can't resolve UnitClass_FighterAircraft yet, so it NREs on the
        // first air unit, Biplanes — the data is actually fine; the real build resolves it). So we do exactly what
        // clicking "Build" does: SKIP the DB gate and call the three build steps that sit past it —
        // TryBuildModification -> DistributeModification -> CopyModification (the last copies the versioned module into
        // the game's Community folder). All via reflection (compile-check stays independent of the Mercury SDK DLL).
        //
        // VERSION STAMPING: the editor's version PANEL (OnApplicationVersionGUI) pre-loads two statics before any build —
        // targetMercuryApplicationVersion (the GAME exe's version, via LoadTargetMercuryApplicationVersionIFN) and
        // nextModificationVersion (current mod version + 1, via TryResetNextModificationVersion). TryBuildModification's
        // internal TryApplyNextModificationVersion then stamps runtimeModule.Version = nextModificationVersion and
        // runtimeModule.GameVersion = targetMercuryApplicationVersion. A batch build never draws that panel, so both
        // statics stay default(Version) = 0.0 and the module ships Version 0.0 / GameVersion 0.0 — which the game rejects
        // as "built using another game version". We therefore run those two prep calls ourselves first. (nextModification
        // Version is read from runtimeModule.Version — the asset is the source of truth, so the version self-increments
        // across builds exactly like the GUI.) See docs/Headless-CLI.md.
        public static void BuildMod()
        {
            try
            {
                // PRE-SHIP VALIDATE (2026-08-18, user: "I was referring to mod build"): the shared rule core over the
                // FULL registry, at the last gate before the pack leaves this machine. Default: report + continue
                // (fail-soft — the built mod still works degraded, exactly like it would in-game). Pass -strict to
                // FAIL the build instead (exit 4) — the CI-able mode where a warning is a stop-ship.
                string vDetail = ModelFactoryWindow.ValidatePackCore(out int vWarns, out int vErrors, out int vCount);
                if (vWarns + vErrors > 0)
                {
                    Debug.LogWarning($"[HAF-CLI] pre-ship validation: {vCount} entr(y/ies), {vWarns} warning(s), {vErrors} error(s)\n{vDetail}");
                    if (Flag("-strict"))
                    { Emit(4, "{\"ok\":false,\"step\":\"validate\",\"entries\":" + vCount + ",\"warnings\":" + vWarns + ",\"errors\":" + vErrors + ",\"note\":\"-strict: fix the issues listed in the log, or build without -strict\"}"); return; }
                }

                var meType = ResolveType("Amplitude.Mercury.Production.Modification.ModuleEditor");
                if (meType == null) { Emit(3, "{\"ok\":false,\"error\":\"ModuleEditor type not found\"}"); return; }
                var rmProp = meType.GetProperty("RuntimeModule", BindingFlags.Public | BindingFlags.Static);
                var rt = rmProp?.GetValue(null);
                if (rt == null) { Emit(3, "{\"ok\":false,\"error\":\"active RuntimeModule not found\"}"); return; }
                var rtType = rmProp.PropertyType;
                var tgt = BuildTarget.StandaloneWindows64;

                // --- version prep the GUI does before every build (a batch run skips the panel that runs these) ---
                InvokeStaticVoid(meType, "CheckMercuryFolderPath");                 // auto-locate the game folder (insurance)
                InvokeStaticVoid(meType, "LoadTargetMercuryApplicationVersionIFN"); // read the game exe version -> GameVersion stamp
                var mReset = meType.GetMethod("TryResetNextModificationVersion", BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mReset == null || !(bool)mReset.Invoke(null, null)) { Emit(3, "{\"ok\":false,\"step\":\"version\",\"error\":\"could not compute next mod version (TryResetNextModificationVersion)\"}"); return; }

                var mTry = meType.GetMethod("TryBuildModification", BindingFlags.NonPublic | BindingFlags.Static, null,
                               new[] { rtType, typeof(BuildTarget), typeof(string).MakeByRefType() }, null);
                var mDist = meType.GetMethod("DistributeModification", BindingFlags.NonPublic | BindingFlags.Static, null,
                                new[] { rtType, typeof(BuildTarget), typeof(bool) }, null);
                var mCopy = meType.GetMethod("CopyModification", BindingFlags.NonPublic | BindingFlags.Static, null,
                                new[] { rtType, typeof(BuildTarget), typeof(bool) }, null);
                if (mTry == null || mDist == null || mCopy == null) { Emit(3, "{\"ok\":false,\"error\":\"build steps not found (SDK changed?)\"}"); return; }

                var buildArgs = new object[] { rt, tgt, null };            // 3rd = out string outputMessage
                if (!(bool)mTry.Invoke(null, buildArgs)) { Emit(3, "{\"ok\":false,\"step\":\"build\",\"error\":\"" + Esc((buildArgs[2] as string) ?? "") + "\"}"); return; }
                if (!(bool)mDist.Invoke(null, new object[] { rt, tgt, false })) { Emit(3, "{\"ok\":false,\"step\":\"distribute\"}"); return; }
                if (!(bool)mCopy.Invoke(null, new object[] { rt, tgt, false })) { Emit(3, "{\"ok\":false,\"step\":\"deploy\"}"); return; }

                // read back what got stamped so the exit line proves the version without launching the game
                var ver = VerStr(GetMember(rt, "Version"));
                var gver = VerStr(GetMember(rt, "GameVersion"));
                Emit(0, "{\"ok\":true,\"version\":\"" + Esc(ver) + "\",\"gameVersion\":\"" + Esc(gver) + "\",\"note\":\"built + deployed to Community (DB gate bypassed; mod + game version stamped)\"}");
            }
            catch (Exception ex) { Emit(3, "{\"ok\":false,\"error\":\"" + Esc(ex.ToString()) + "\"}"); }
        }

        // Invoke a parameterless private static method by name (no-op if the SDK renamed it — prep is best-effort).
        static void InvokeStaticVoid(Type t, string name)
        {
            var m = t.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (m != null) m.Invoke(null, null);
        }

        // Read a public instance member (field OR property) by name — RuntimeModule exposes Version/GameVersion as fields.
        static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            return t.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj)
                ?? t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
        }

        // "Major.Minor" out of a Mercury Version struct (Major/Minor are fields; fall back to properties).
        static string VerStr(object v)
        {
            if (v == null) return "?";
            var t = v.GetType();
            object maj = t.GetField("Major")?.GetValue(v) ?? t.GetProperty("Major")?.GetValue(v);
            object min = t.GetField("Minor")?.GetValue(v) ?? t.GetProperty("Minor")?.GetValue(v);
            return maj + "." + min;
        }

        static Type ResolveType(string fullName)
        {
            var t = Type.GetType(fullName);
            if (t != null) return t;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { t = a.GetType(fullName); if (t != null) return t; }
            return null;
        }
    }
}
