using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace HumankindAssetFramework
{
    // SPIKE (wip-wonder-affinity): one-shot dump of the game's AssetReferenceRepository — the criteria-matrix
    // database that maps (affinity / district / ArtificialWonder-name / culture / era / ...) to the selector and
    // material assets a tile renders. Decompile (scratchpad decomp/) showed vanilla artificial wonders register
    // their completed model here as plain AssetReferenceDatabaseContent datatable rows keyed by WONDER NAME —
    // so the goal is to learn the exact database + row shape a custom wonder must mimic, then register our own.
    // Gated by DistrictDebug; logs once per app run.
    internal static partial class UniversalInject
    {
        static bool repoDumped;
        static int wonderRowTick;
        static readonly HashSet<string> wonderRowLogged = new HashSet<string>();

        // SPIKE step 2, SWAP-FIRST sequencing (the "wipe Artemis clean" rule): the template material is loaded
        // PLUGIN-SIDE (never via the repository cell), the walker builds the private leaf from the stash and
        // repoints the channel — and only THEN is the wonder's cell filled (fallback/consistency only). The
        // native selector therefore never has a drawable template on our tile: blank for a moment, then OUR
        // model, every load. Config format: "WonderName=a,b,c,d;Other=..." .
        // Re-arms itself: the repository rebuilds its matrices on session reload, wiping late-added cells.
        static readonly Dictionary<string, object> wonderTemplates = new Dictionary<string, object>();
        static readonly Dictionary<string, object> wonderTemplateReqs = new Dictionary<string, object>();   // pending AssetBundleRequest per name

        internal static void ResetWonderTemplates()   // called from ResetDistrictSessionState — assets are corpses after a reload
        {
            wonderTemplates.Clear();
            wonderTemplateReqs.Clear();
            wonderRowLogged.Clear();
        }

        // Leaf source for wonder entries: the plugin-loaded template material (never the repository cell).
        internal static object WonderTemplate(string wonderName)
        {
            return wonderTemplates.TryGetValue(wonderName, out var m) ? m : null;
        }

        internal static void PollWonderRows()
        {
            var cfg = Plugin.WonderNativeRows?.Value?.Trim();
            if (string.IsNullOrEmpty(cfg)) return;
            if (++wonderRowTick % 30 != 1) return;   // ~2x/second is plenty; every step below is idempotent
            try
            {
                var fxmType = AccessTools.TypeByName("Amplitude.Graphics.Fx.FxEvolverMaterial");
                var tryLoadAsync = fxmType?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(x => x.Name == "TryLoadAsync" && x.GetParameters().Length == 2);
                var nextIdx = fxmType?.GetMethod("NextDoublonAvoidanceIndex", BindingFlags.Public | BindingFlags.Static);
                if (tryLoadAsync == null || nextIdx == null) return;

                foreach (var part in cfg.Split(';'))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    string wname = part.Substring(0, eq).Trim();
                    var guid = ParseGuid4(part.Substring(eq + 1).Trim());
                    if (guid == null) { if (wonderRowLogged.Add(wname + ":badguid")) Plugin.Log.LogWarning($"[WonderRow] '{wname}': unparseable guid"); continue; }

                    // 1) load the template material ourselves and stash it once fully Loaded.
                    // HARD-WON LAW (two deadlocks): DO NOT reach for the render context / FxManager before the
                    // district machinery has tracked one (distFxManager) — RenderContextAccess.GetInstance from a
                    // plugin Update tick during the load sequence deadlocks the loading screen, with EITHER load
                    // variant. Behind-the-screen template loading is falsified; the reveal-on-load is handled
                    // elsewhere (event capture), not by racing the loading screen.
                    if (!wonderTemplates.ContainsKey(wname))
                    {
                        var fxm = distFxManager;                        // the FxManager the district machinery tracks — never earlier
                        if (fxm == null) continue;                      // not up yet — retry next tick
                        wonderTemplateReqs.TryGetValue(wname, out var pending);
                        var asyncArgs = new object[] { guid, pending };
                        var mat = tryLoadAsync.Invoke(null, asyncArgs);
                        wonderTemplateReqs[wname] = asyncArgs[1];       // keep the AssetBundleRequest for the next poll
                        if (mat == null) continue;                      // still streaming — retry next tick
                        var loadIfn = mat.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "LoadIFN" && x.GetParameters().Length == 2);
                        loadIfn?.Invoke(mat, new object[] { fxm, nextIdx.Invoke(null, null) });
                        if (!(AccessTools.Property(mat.GetType(), "Loaded")?.GetValue(mat) is bool ld) || !ld) continue;   // still loading — retry
                        wonderTemplates[wname] = mat;
                        wonderTemplateReqs.Remove(wname);
                        Plugin.Diag($"[WonderRow] '{wname}': template loaded plugin-side ({mat.GetType().Name}, async)");
                    }

                    // 2) fill the repository cell ONLY once the entry's swap is live (the player never sees the template)
                    var entry = distModels.FirstOrDefault(d => d.district == wname);
                    if (entry == null || entry.privateLeaf == null) continue;   // swap not established yet — cell stays empty
                    FillWonderCell(wname, guid);
                }
            }
            catch (Exception ex) { if (wonderRowLogged.Add("ex")) Plugin.Log.LogError("[WonderRow] " + ex); }
        }

        static void FillWonderCell(string wname, object guid)
        {
            var repoType = AccessTools.TypeByName("Amplitude.Mercury.Data.Presentation.AssetReferenceRepository");
            var inst = repoType?.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.Invoke(null, null);
            if (inst == null) return;
            if (!(AccessTools.Property(inst.GetType(), "Loaded")?.GetValue(inst) is bool b) || !b) return;
            if (!(AccessTools.Field(inst.GetType(), "databaseMatrices1D")?.GetValue(inst) is Array arr)) return;
            // the 'ArtificialWonder' 1D matrix (boxed struct copy — its cells/criterionNames arrays are shared,
            // so Add() on the box mutates the real matrix; only value-type fields would be lost, and we touch none)
            foreach (var m in arr)
            {
                if (m == null || AccessTools.Field(m.GetType(), "Name")?.GetValue(m)?.ToString() != "ArtificialWonder") continue;
                var mt = m.GetType();
                if (!(AccessTools.Property(mt, "CriteriaNames")?.GetValue(m) is Array axis)) return;
                var cells = AccessTools.Field(mt, "cells")?.GetValue(m) as Array;
                var ssType = AccessTools.TypeByName("Amplitude.StaticString");
                var addM = mt.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "Add" && x.GetParameters().Length == 3);
                if (cells == null || ssType == null || addM == null) return;
                int idx = -1;
                for (int i = 0; i < axis.Length; i++) if (axis.GetValue(i)?.ToString() == wname) { idx = i; break; }
                if (idx < 0) { if (wonderRowLogged.Add(wname + ":noaxis")) Plugin.Log.LogWarning($"[WonderRow] '{wname}': not in the criteria axis (definition not loaded?)"); return; }
                var cell = cells.GetValue(idx);
                var curGuid = AccessTools.Field(cell.GetType(), "Guid")?.GetValue(cell);
                if (curGuid != null && curGuid.Equals(guid)) return;   // already filled
                addM.Invoke(m, new object[] { Activator.CreateInstance(ssType, wname), guid, null });
                if (wonderRowLogged.Add(wname + ":filled"))
                    Plugin.Diag($"[WonderRow] '{wname}': cell filled AFTER swap went live (fallback only — the tile draws our private leaf)");
                return;
            }
        }

        // GROUND MATERIAL under a custom district (the "maintained grass field"): vanilla resolves a
        // GroundMaterialDefinition from (Biome × ConstructibleVisualAffinity) and calls ApplyGroundMaterialDefinition
        // (index into criteria 24). Our wonder's affinity has no row for this biome → index 0 → bare sand. This
        // postfix forces a chosen ground-material index for our registry districts — the game's own terrain paint,
        // blended, not a flat mesh. Also dumps the ground-material vocabulary once (DistrictDebug) so a name can be picked.
        static bool groundNamesDumped; static readonly HashSet<string> groundLogged = new HashSet<string>();
        internal static void DistrictApplyGroundMaterial(object district)
        {
            try
            {
                EnsureDistrictConfig();
                if (!distOn) return;
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString();
                if (string.IsNullOrEmpty(name)) return;
                DistrictModel entry = null; foreach (var e in distModels) if (e.district == name) { entry = e; break; }
                if (entry == null) return;

                var repoType = AccessTools.TypeByName("Amplitude.Mercury.Data.Presentation.AssetReferenceRepository");
                var inst = repoType?.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.Invoke(null, null);
                if (inst == null) return;
                if (!(AccessTools.Property(inst.GetType(), "Loaded")?.GetValue(inst) is bool ld) || !ld) return;

                // 24 = GroundMaterialDefinitionCriteriaIndex — dump the vocabulary once so the user can pick a grass name
                const int GroundCriteria = 24;
                if (!groundNamesDumped && Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value)
                {
                    groundNamesDumped = true;
                    var namesM = repoType.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "Names" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
                    if (namesM?.Invoke(inst, new object[] { GroundCriteria }) is Array arr)
                    {
                        var list = new List<string>(); foreach (var s in arr) list.Add(s?.ToString());
                        Plugin.Log.LogInfo($"[Ground] GroundMaterialDefinition names ({list.Count}): {string.Join(", ", list)}");
                    }
                }

                // per-ENTRY terrain paint (the Factory's Ground field), falling back to the global config default
                var want = !string.IsNullOrEmpty(entry.groundMaterial) ? entry.groundMaterial : Plugin.DistrictGroundMaterial?.Value?.Trim();
                if (string.IsNullOrEmpty(want)) return;

                if (entry.groundIdx == int.MinValue)
                {
                    var ssType = AccessTools.TypeByName("Amplitude.StaticString");
                    var idxM = repoType.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "IndexOf" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                    if (idxM == null || ssType == null) { entry.groundIdx = -1; return; }
                    var args = new object[] { GroundCriteria, Activator.CreateInstance(ssType, want) };
                    entry.groundIdx = (int)idxM.Invoke(inst, args);
                    if (entry.groundIdx <= 0) Plugin.Log.LogWarning($"[Ground] '{want}' not found in the GroundMaterialDefinition vocabulary (index {entry.groundIdx}) — set DistrictDebug=true to log the valid names.");
                }
                if (entry.groundIdx <= 0) return;

                var apply = district.GetType().GetMethod("ApplyGroundMaterialDefinition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (apply != null && apply.GetParameters().Length == 1)
                {
                    apply.Invoke(district, new object[] { entry.groundIdx });
                    if (groundLogged.Add(name)) Plugin.Diag($"[Ground] '{name}': forced ground material '{want}' (index {entry.groundIdx}) — maintained field under the district.");
                }
            }
            catch (Exception ex) { if (groundLogged.Add("ex")) Plugin.Log.LogError("[Ground] " + ex); }
        }

        internal static void PollRepoDump()
        {
            if (repoDumped || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;
            try
            {
                var repoType = AccessTools.TypeByName("Amplitude.Mercury.Data.Presentation.AssetReferenceRepository");
                if (repoType == null) { repoDumped = true; Plugin.Log.LogWarning("[RepoDump] repository type not found"); return; }
                var inst = repoType.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.Invoke(null, null);
                if (inst == null) return;                                   // not created yet — retry next frame
                var loaded = AccessTools.Property(inst.GetType(), "Loaded")?.GetValue(inst);
                if (!(loaded is bool b) || !b) return;                       // not loaded yet — retry next frame
                repoDumped = true;
                DumpMatrices(inst, "databaseMatrices0D");
                DumpMatrices(inst, "databaseMatrices1D");
                DumpMatrices(inst, "databaseMatrices2D");
                Plugin.Log.LogInfo("[RepoDump] done");
            }
            catch (Exception ex) { repoDumped = true; Plugin.Log.LogError("[RepoDump] " + ex); }
        }

        // Matches: criteria-axis names worth expanding row-by-row (wonders, holy sites, our own content).
        static bool RepoInteresting(string s) =>
            s != null && (s.IndexOf("Wonder", StringComparison.OrdinalIgnoreCase) >= 0
                       || s.IndexOf("HolySite", StringComparison.OrdinalIgnoreCase) >= 0
                       || s.IndexOf("Oracle", StringComparison.OrdinalIgnoreCase) >= 0);

        static void DumpMatrices(object repo, string fieldName)
        {
            var arr = AccessTools.Field(repo.GetType(), fieldName)?.GetValue(repo) as Array;
            if (arr == null) { Plugin.Log.LogInfo($"[RepoDump] {fieldName}: <null>"); return; }
            Plugin.Log.LogInfo($"[RepoDump] == {fieldName}: {arr.Length} database(s)");
            foreach (var m in arr)
            {
                if (m == null) continue;
                var mt = m.GetType();
                string name = AccessTools.Field(mt, "Name")?.GetValue(m)?.ToString() ?? "?";
                string crit = "";
                foreach (var cf in new[] { "Criteria", "FirstCriteria", "SecondCriteria" })
                {
                    var cv = AccessTools.Field(mt, cf)?.GetValue(m) ?? AccessTools.Property(mt, cf)?.GetValue(m);
                    if (cv != null) crit += $" {cf}={cv}";
                }
                // content type from the matrix's Definition (AssetReferenceDatabaseDefinition)
                var def = (AccessTools.Field(mt, "Definition") ?? AccessTools.Field(mt, "definition"))?.GetValue(m)
                          ?? AccessTools.Property(mt, "Definition")?.GetValue(m);
                string content = def != null ? AccessTools.Field(def.GetType(), "ContentTypeName")?.GetValue(def)?.ToString() : null;
                Plugin.Log.LogInfo($"[RepoDump] db '{name}'{crit} content={content ?? "?"}");

                // axis names: 1D CriteriaNames / 2D FirstCriteriaNames+SecondCriteriaNames (public properties)
                foreach (var pn in new[] { "CriteriaNames", "FirstCriteriaNames", "SecondCriteriaNames" })
                {
                    if (!(AccessTools.Property(mt, pn)?.GetValue(m) is Array names)) continue;
                    var all = names.Cast<object>().Select(x => x?.ToString() ?? "").ToArray();
                    var hits = all.Where(RepoInteresting).ToArray();
                    Plugin.Log.LogInfo($"[RepoDump]   {pn}: {all.Length} value(s){(hits.Length > 0 ? "; wonder-ish: " + string.Join(", ", hits) : "")}");
                    // small axes: print everything — these are the criteria vocabularies we must key rows by
                    if (all.Length > 0 && all.Length <= 24) Plugin.Log.LogInfo($"[RepoDump]     [{string.Join(", ", all)}]");
                    // wonder-ish names on a 1D axis: resolve each row's guid via the matrix's own GetValue
                    if (pn == "CriteriaNames" && hits.Length > 0) DumpRows(m, mt, hits);
                }
            }
        }

        static void DumpRows(object matrix, Type mt, string[] names)
        {
            // public bool GetValue(ref StaticString name, out Guid guid) — invoke via reflection with a boxed args array
            var get = mt.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(x => x.Name == "GetValue" && x.GetParameters().Length == 2);
            if (get == null) return;
            var ssType = AccessTools.TypeByName("Amplitude.StaticString");
            foreach (var n in names)
            {
                try
                {
                    var args = new object[] { Activator.CreateInstance(ssType, n), null };
                    var ok = get.Invoke(matrix, args);
                    var g = args[1];
                    string guid = "?";
                    if (g != null)
                    {
                        var gt = g.GetType();
                        guid = $"{gt.GetField("a", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g)},{gt.GetField("b", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g)},{gt.GetField("c", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g)},{gt.GetField("d", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g)}";
                    }
                    Plugin.Log.LogInfo($"[RepoDump]     row '{n}' -> {guid} (found={ok})");
                }
                catch (Exception ex) { Plugin.Log.LogInfo($"[RepoDump]     row '{n}' -> <error {ex.GetType().Name}>"); }
            }
        }
    }
}
