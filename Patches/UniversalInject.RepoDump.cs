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

        // SPIKE step 2: fill a wonder's empty cell in the 'ArtificialWonder' database (name -> FxEvolverMaterial guid).
        // The dump proved our wonder's name is already in the criteria axis with a NULL guid — the whole reason the
        // native ArtificialWonder affinity rendered nothing. Config format: "WonderName=a,b,c,d;Other=..." .
        // Re-arms itself: the repository rebuilds its matrices on session reload, wiping late-added cells.
        internal static void PollWonderRows()
        {
            var cfg = Plugin.WonderNativeRows?.Value?.Trim();
            if (string.IsNullOrEmpty(cfg)) return;
            if (++wonderRowTick % 30 != 1) return;   // ~2x/second is plenty; the fill is idempotent
            try
            {
                var repoType = AccessTools.TypeByName("Amplitude.Mercury.Data.Presentation.AssetReferenceRepository");
                var inst = repoType?.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.Invoke(null, null);
                if (inst == null) return;
                if (!(AccessTools.Property(inst.GetType(), "Loaded")?.GetValue(inst) is bool b) || !b) return;
                if (!(AccessTools.Field(inst.GetType(), "databaseMatrices1D")?.GetValue(inst) is Array arr)) return;

                // find the 'ArtificialWonder' 1D matrix (boxed struct copy — its cells/criterionNames arrays are shared,
                // so Add() on the box mutates the real matrix; only value-type fields would be lost, and we touch none)
                object matrix = null;
                foreach (var m in arr)
                    if (m != null && AccessTools.Field(m.GetType(), "Name")?.GetValue(m)?.ToString() == "ArtificialWonder") { matrix = m; break; }
                if (matrix == null) return;
                var mt = matrix.GetType();
                if (!(AccessTools.Property(mt, "CriteriaNames")?.GetValue(matrix) is Array axis)) return;
                var cells = AccessTools.Field(mt, "cells")?.GetValue(matrix) as Array;
                var ssType = AccessTools.TypeByName("Amplitude.StaticString");
                var addM = mt.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "Add" && x.GetParameters().Length == 3);
                if (cells == null || ssType == null || addM == null) return;

                foreach (var part in cfg.Split(';'))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    string wname = part.Substring(0, eq).Trim();
                    var guid = ParseGuid4(part.Substring(eq + 1).Trim());
                    if (guid == null) { if (wonderRowLogged.Add(wname + ":badguid")) Plugin.Log.LogWarning($"[WonderRow] '{wname}': unparseable guid"); continue; }

                    int idx = -1;
                    for (int i = 0; i < axis.Length; i++) if (axis.GetValue(i)?.ToString() == wname) { idx = i; break; }
                    if (idx < 0) { if (wonderRowLogged.Add(wname + ":noaxis")) Plugin.Log.LogWarning($"[WonderRow] '{wname}': not in the criteria axis (definition not loaded?)"); continue; }

                    // already filled with our guid AND loaded? then nothing to do this tick
                    var cell = cells.GetValue(idx);
                    var ct = cell.GetType();
                    var curGuid = AccessTools.Field(ct, "Guid")?.GetValue(cell);
                    var curAsset = AccessTools.Field(ct, "Asset")?.GetValue(cell);
                    bool guidSet = curGuid != null && curGuid.Equals(guid);
                    if (guidSet && curAsset != null) continue;

                    if (!guidSet)
                        addM.Invoke(matrix, new object[] { Activator.CreateInstance(ssType, wname), guid, null });

                    // force the cell's asset load — the repo's own LoadAssets coroutine has long finished by now
                    var fxm = distFxManager;   // the terrain FxManager the district machinery already tracks
                    if (fxm != null)
                    {
                        var loadM = ct.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "LoadAssets" && x.GetParameters().Length == 5);
                        var next = AccessTools.TypeByName("Amplitude.Graphics.Fx.FxEvolverMaterial")?.GetMethod("NextDoublonAvoidanceIndex", BindingFlags.Public | BindingFlags.Static);
                        var contentType = AccessTools.TypeByName("Amplitude.Graphics.Fx.FxEvolverMaterial");
                        if (loadM != null && next != null)
                        {
                            cell = cells.GetValue(idx);   // re-read: Add mutated it
                            var args = new object[] { AccessTools.Field(mt, "Name").GetValue(matrix), axis.GetValue(idx), fxm, next.Invoke(null, null), contentType };
                            var ok = loadM.Invoke(cell, args);
                            cells.SetValue(cell, idx);    // write the boxed struct (Asset/loadingStatus) back
                            var asset = AccessTools.Field(ct, "Asset")?.GetValue(cell);
                            if (wonderRowLogged.Add(wname + ":filled"))
                                Plugin.Log.LogInfo($"[WonderRow] '{wname}' cell filled -> loaded={ok} asset={(asset != null ? asset.GetType().Name + " '" + asset + "'" : "<null>")}");
                        }
                    }
                    else if (wonderRowLogged.Add(wname + ":nofxm"))
                        Plugin.Log.LogInfo($"[WonderRow] '{wname}' guid set; waiting for FxManager to force the asset load");
                }
            }
            catch (Exception ex) { if (wonderRowLogged.Add("ex")) Plugin.Log.LogError("[WonderRow] " + ex); }
        }

        // Leaf source for wonder entries: the loaded FxEvolverMaterial in the entry's own 'ArtificialWonder' DB cell.
        // Returns null until the cell's asset is loaded (the repoint loop simply retries next frame).
        internal static object WonderDbMaterial(string wonderName)
        {
            try
            {
                var repoType = AccessTools.TypeByName("Amplitude.Mercury.Data.Presentation.AssetReferenceRepository");
                var inst = repoType?.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.Invoke(null, null);
                if (inst == null) return null;
                if (!(AccessTools.Field(inst.GetType(), "databaseMatrices1D")?.GetValue(inst) is Array arr)) return null;
                foreach (var m in arr)
                {
                    if (m == null || AccessTools.Field(m.GetType(), "Name")?.GetValue(m)?.ToString() != "ArtificialWonder") continue;
                    if (!(AccessTools.Property(m.GetType(), "CriteriaNames")?.GetValue(m) is Array axis)) return null;
                    if (!(AccessTools.Field(m.GetType(), "cells")?.GetValue(m) is Array cells)) return null;
                    for (int i = 0; i < axis.Length; i++)
                    {
                        if (axis.GetValue(i)?.ToString() != wonderName) continue;
                        var cell = cells.GetValue(i);
                        return AccessTools.Field(cell.GetType(), "Asset")?.GetValue(cell);
                    }
                    return null;
                }
            }
            catch { }
            return null;
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
