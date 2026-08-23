using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using static HumankindAssetFramework.UniversalInject;   // GetMember/SetMember/BF, LoadAmpliAsset/ParseGuid4

namespace HumankindAssetFramework
{
    // SPIKE (wip-wonder-affinity): one-shot dump of the game's AssetReferenceRepository — the criteria-matrix
    // database that maps (affinity / district / ArtificialWonder-name / culture / era / ...) to the selector and
    // material assets a tile renders. Decompile (scratchpad decomp/) showed vanilla artificial wonders register
    // their completed model here as plain AssetReferenceDatabaseContent datatable rows keyed by WONDER NAME —
    // so the goal is to learn the exact database + row shape a custom wonder must mimic, then register our own.
    // Gated by DistrictDebug; logs once per app run.
    internal static partial class DistrictInject
    {
        static bool repoDumped;
        static int wonderRowTick;
        [SessionScoped(Scope = SessionScope.District, Manual = "wonder reset, DistrictInject.Scoped.cs")] static readonly HashSet<string> wonderCellFilled = new HashSet<string>();   // wonders whose repository cell is filled (latched; cleared on session reset)
        static MethodInfo wonderTryLoadAsync, wonderNextIdx;                         // FxEvolverMaterial.TryLoadAsync / NextDoublonAvoidanceIndex, resolved once
        static bool wonderRowsAllDone;                                            // every configured wonder latched -> PollWonderRows is a no-op
        static bool axisProbed;
        [SessionScoped(Scope = SessionScope.District, Manual = "wonder reset, DistrictInject.Scoped.cs")] static readonly HashSet<string> wonderRowLogged = new HashSet<string>();

        // SPIKE (dedicated-visual, axis-growth probe): the dedicated-selector path needs a NEW BuildingVisualAffinity value
        // added to a criteria axis. The wonder work only ever FILLED existing empty cells (every wonder name pre-existed on
        // the axis). This probe answers the make-or-break question: does matrix.Add GROW a 1D criteria axis with a brand-new
        // value, and does it PERSIST to the real matrix? (The matrix is a boxed struct; a resize on the box could be lost
        // unless written back — so we compare a fresh box read vs a written-back box.) One-shot, DistrictDebug-gated,
        // mutates only a throwaway test value. Tests the known 'ArtificialWonder' matrix + an affinity matrix.
        internal static void ProbeAxisGrowth()
        {
            if (axisProbed || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;
            try
            {
                var repoType = AccessTools.TypeByName("Amplitude.Mercury.Data.Presentation.AssetReferenceRepository");
                var inst = repoType?.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.Invoke(null, null);
                if (inst == null) return;
                if (!(AccessTools.Property(inst.GetType(), "Loaded")?.GetValue(inst) is bool b) || !b) return;
                if (!(AccessTools.Field(inst.GetType(), "databaseMatrices1D")?.GetValue(inst) is Array arr)) return;
                axisProbed = true;
                var ssType = AccessTools.TypeByName("Amplitude.StaticString");
                var testGuid = ParseGuid4("1,2,3,4");
                int Axis(object mm) => (AccessTools.Property(mm.GetType(), "CriteriaNames")?.GetValue(mm) as Array)?.Length ?? -1;
                bool Has(object mm, string nm) { if (AccessTools.Property(mm.GetType(), "CriteriaNames")?.GetValue(mm) is Array ax) foreach (var v in ax) if (v?.ToString() == nm) return true; return false; }
                foreach (var target in new[] { "ArtificialWonder", "*/District/Construction" })
                {
                    bool hit = false;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var m = arr.GetValue(i);
                        if (m == null || AccessTools.Field(m.GetType(), "Name")?.GetValue(m)?.ToString() != target) continue;
                        hit = true;
                        var mt = m.GetType();
                        var addM = mt.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "Add" && x.GetParameters().Length == 3);
                        if (addM == null || ssType == null) { Plugin.Log.LogWarning($"[AxisProbe] '{target}': no Add(3)/StaticString — can't probe"); break; }
                        string testName = "HAF_AxisProbe";
                        int before = Axis(m);
                        addM.Invoke(m, new object[] { Activator.CreateInstance(ssType, testName), testGuid, null });
                        int afterBox = Axis(m);                                   // same box (Add mutated it)
                        var mFresh = arr.GetValue(i);                             // fresh box — did Add persist to the real matrix?
                        int afterFresh = Axis(mFresh); bool foundFresh = Has(mFresh, testName);
                        arr.SetValue(m, i);                                       // write the mutated box back
                        var mWB = arr.GetValue(i);
                        int afterWB = Axis(mWB); bool foundWB = Has(mWB, testName);
                        Plugin.Log.LogInfo($"[AxisProbe] '{target}': before={before} afterBox={afterBox} afterFresh={afterFresh}(found={foundFresh}) afterWriteback={afterWB}(found={foundWB})");
                        break;
                    }
                    if (!hit) Plugin.Log.LogWarning($"[AxisProbe] '{target}': matrix not found in databaseMatrices1D");
                }
            }
            catch (Exception ex) { axisProbed = true; Plugin.Log.LogError("[AxisProbe] " + ex); }
        }

        // SPIKE step 2, SWAP-FIRST sequencing (the "wipe Artemis clean" rule): the template material is loaded
        // PLUGIN-SIDE (never via the repository cell), the walker builds the private leaf from the stash and
        // repoints the channel — and only THEN is the wonder's cell filled (fallback/consistency only). The
        // native selector therefore never has a drawable template on our tile: blank for a moment, then OUR
        // model, every load. Config format: "WonderName=a,b,c,d;Other=..." .
        // Re-arms itself: the repository rebuilds its matrices on session reload, wiping late-added cells.
        [SessionScoped(Scope = SessionScope.District, Manual = "wonder reset, DistrictInject.Scoped.cs")] static readonly Dictionary<string, object> wonderTemplates = new Dictionary<string, object>();
        [SessionScoped(Scope = SessionScope.District, Manual = "wonder reset, DistrictInject.Scoped.cs")] static readonly Dictionary<string, object> wonderTemplateReqs = new Dictionary<string, object>();   // pending AssetBundleRequest per name

        internal static void ResetWonderTemplates()   // called from ResetDistrictSessionState — assets are corpses after a reload
        {
            wonderCellFilled.Clear(); wonderRowsAllDone = false;   // the repository is rebuilt per session -> re-fill (and re-latch) next session
            wonderTemplates.Clear();
            wonderTemplateReqs.Clear();
            wonderRowLogged.Clear();
            districtMainLogged.Clear(); districtMainTick = 0; districtReresolved = false;   // re-arm the */District/Main registration + re-resolve on reload
            axisProbed = false;   // re-probe the axis-growth question each session (the matrix rebuilds on reload)
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
            if (wonderRowsAllDone) return;            // all cells filled this session (see the latch below)
            if (++wonderRowTick % 30 != 1) return;   // ~2x/second is plenty; every step below is idempotent
            try
            {
                // resolved ONCE: AccessTools.TypeByName is an uncached walk of every type in every assembly, and this ran it
                // (plus a LINQ method search) every 30 frames — ~40 ms a run, 1.35 ms/frame averaged (FrameCost 2026-08-21)
                if (wonderTryLoadAsync == null || wonderNextIdx == null)
                {
                    var fxmType = GameBinding.FxEvolverMaterial;
                    wonderTryLoadAsync = fxmType?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(x => x.Name == "TryLoadAsync" && x.GetParameters().Length == 2);
                    wonderNextIdx = fxmType?.GetMethod("NextDoublonAvoidanceIndex", BindingFlags.Public | BindingFlags.Static);
                }
                var tryLoadAsync = wonderTryLoadAsync; var nextIdx = wonderNextIdx;
                if (tryLoadAsync == null || nextIdx == null) return;
                bool anyWork = false;

                foreach (var part in cfg.Split(';'))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    string wname = part.Substring(0, eq).Trim();
                    if (wonderCellFilled.Contains(wname)) continue;   // DONE (latched): no template / repository work until the next session reset
                    anyWork = true;
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
                    // a SCOPED district (selectorGuid) registers through the selector-tile path; the isolate swap that this
                    // cell fill waits on never happens for it — latch, or the poll retries forever (the Oracle save)
                    if (entry != null && entry.selectorGuid != null) { wonderCellFilled.Add(wname); continue; }
                    if (entry == null || entry.privateLeaf == null) continue;   // swap not established yet — cell stays empty
                    FillWonderCell(wname, guid);
                }
                // every configured wonder latched -> stop polling altogether (was ~40 ms per run, every 30 frames, forever:
                // a TypeByName assembly scan + a LINQ method search + a boxed copy of every repository matrix, all to
                // discover "already filled" at the end — FrameCost 2026-08-21: 1.35 ms/frame)
                if (!anyWork) wonderRowsAllDone = true;
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
                if (curGuid != null && curGuid.Equals(guid)) { wonderCellFilled.Add(wname); return; }   // already filled -> latch
                addM.Invoke(m, new object[] { Activator.CreateInstance(ssType, wname), guid, null });
                wonderCellFilled.Add(wname);
                if (wonderRowLogged.Add(wname + ":filled"))
                    Plugin.Diag($"[WonderRow] '{wname}': cell filled AFTER swap went live (fallback only — the tile draws our private leaf)");
                return;
            }
        }

        // DEDICATED-VISUAL hybrid (register step): fill the cell for `criteriaName` -> `guid` in every 1D matrix whose Name
        // CONTAINS `nameContains`. Generalizes FillWonderCell (ArtificialWonder-only). matrix.Add fills an EXISTING axis
        // value's cell (proven — the axis-growth probe showed Add only fills, never grows), which is exactly what we want:
        // the reactor's affinity already exists on the */District/Main axis; we point its cell at our baked selector.
        static int Contains(Array axis, string val) { if (axis != null) for (int i = 0; i < axis.Length; i++) if (axis.GetValue(i)?.ToString() == val) return i; return -1; }
        static int FillMatrixCells(object inst, string nameContains, string criteriaName, object guid)
        {
            int filled = 0;
            var ssType = AccessTools.TypeByName("Amplitude.StaticString");
            if (ssType == null) return 0;
            // matrices live across databaseMatrices1D + databaseMatrices2D (the main visual db is 2D: DistrictState x affinity)
            foreach (var fieldName in new[] { "databaseMatrices1D", "databaseMatrices2D" })
            {
                if (!(AccessTools.Field(inst.GetType(), fieldName)?.GetValue(inst) is Array arr)) continue;
                for (int mi = 0; mi < arr.Length; mi++)
                {
                    var m = arr.GetValue(mi); if (m == null) continue;
                    var mt = m.GetType();
                    var name = AccessTools.Field(mt, "Name")?.GetValue(m)?.ToString();
                    if (name == null || !name.Contains(nameContains)) continue;

                    // 1D: CriteriaNames + Add(StaticString, guid, comment)
                    if (AccessTools.Property(mt, "CriteriaNames")?.GetValue(m) is Array axis1 && axis1.Length > 0)
                    {
                        int idx = Contains(axis1, criteriaName);
                        var add3 = mt.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "Add" && x.GetParameters().Length == 3);
                        if (idx >= 0 && add3 != null) { add3.Invoke(m, new object[] { Activator.CreateInstance(ssType, criteriaName), guid, null }); filled++; }
                        else if (districtMainLogged.Add(name + ":1d")) Plugin.Log.LogWarning($"[DistrictMain] 1D '{name}': affinity idx={idx} add3={(add3 != null)} — skipped");
                        continue;
                    }
                    // 2D: FirstCriteriaNames x SecondCriteriaNames — the affinity is one axis, DistrictState the other.
                    var firstAxis = AccessTools.Property(mt, "FirstCriteriaNames")?.GetValue(m) as Array;
                    var secondAxis = AccessTools.Property(mt, "SecondCriteriaNames")?.GetValue(m) as Array;
                    // DatabaseMatrix2D.AddCell(ref StaticString first, ref StaticString second, Guid guid, AssetReferenceDatabaseContent element)
                    var add4 = mt.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "AddCell" && x.GetParameters().Length == 4);
                    if (firstAxis == null || secondAxis == null || add4 == null)
                    { if (districtMainLogged.Add(name + ":2dshape")) Plugin.Log.LogWarning($"[DistrictMain] 2D '{name}': first={(firstAxis != null)} second={(secondAxis != null)} add4={(add4 != null)} — can't fill"); continue; }
                    bool affinityIsSecond = Contains(secondAxis, criteriaName) >= 0;
                    bool affinityIsFirst = Contains(firstAxis, criteriaName) >= 0;
                    if (!affinityIsSecond && !affinityIsFirst)
                    { if (districtMainLogged.Add(name + ":noaff")) Plugin.Log.LogWarning($"[DistrictMain] 2D '{name}': '{criteriaName}' on neither axis (first[0]={firstAxis.GetValue(0)}, second[0]={secondAxis.GetValue(0)})"); continue; }
                    // fill (state, affinity) for EVERY state on the OTHER axis
                    var otherAxis = affinityIsSecond ? firstAxis : secondAxis;
                    for (int oi = 0; oi < otherAxis.Length; oi++)
                    {
                        var otherName = otherAxis.GetValue(oi)?.ToString();
                        var a = Activator.CreateInstance(ssType, otherName);
                        var b = Activator.CreateInstance(ssType, criteriaName);
                        // param order = (first, second, guid, comment)
                        add4.Invoke(m, affinityIsSecond ? new object[] { a, b, guid, null } : new object[] { b, a, guid, null });
                        filled++;
                    }
                }
            }
            return filled;
        }

        // Register our data-authored district selector by filling the */District/Main.Level1+Level2 cells for an affinity.
        // Config DistrictMainRows: "AffinityName=a,b,c,d;...". Re-armed on reload (matrices rebuild); idempotent.
        static int districtMainTick;
        static bool districtReresolved;   // force the post-fill re-resolve only once per session
        [SessionScoped(Scope = SessionScope.District, Manual = "wonder reset, DistrictInject.Scoped.cs")] static readonly HashSet<string> districtMainLogged = new HashSet<string>();
        internal static void PollDistrictMainRows()
        {
            var cfg = Plugin.DistrictMainRows?.Value?.Trim();
            if (string.IsNullOrEmpty(cfg)) return;
            if (++districtMainTick % 30 != 1) return;
            if (distFxManager == null) return;   // wait until the district machinery is up (repository loaded)
            try
            {
                var repoType = AccessTools.TypeByName("Amplitude.Mercury.Data.Presentation.AssetReferenceRepository");
                var inst = repoType?.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.Invoke(null, null);
                if (inst == null) return;
                if (!(AccessTools.Property(inst.GetType(), "Loaded")?.GetValue(inst) is bool b) || !b) return;
                int totalFilled = 0;
                foreach (var part in cfg.Split(';'))
                {
                    var eq = part.IndexOf('='); if (eq <= 0) continue;
                    string affinity = part.Substring(0, eq).Trim();
                    var guid = ParseGuid4(part.Substring(eq + 1).Trim());
                    if (guid == null) { if (districtMainLogged.Add(affinity + ":badguid")) Plugin.Log.LogWarning($"[DistrictMain] '{affinity}': unparseable guid"); continue; }
                    int n = FillMatrixCells(inst, "District/Main", affinity, guid);
                    totalFilled += n;
                    if (districtMainLogged.Add(affinity + ":n" + n)) Plugin.Log.LogInfo($"[DistrictMain] '{affinity}': filled {n} */District/Main cell(s) -> our selector");
                    // VERIFY the selector GUID actually loads from the bundle (else the cell points at nothing and the game
                    // keeps the cached vanilla visual — the classic "mod not rebuilt after the GUID changed").
                    if (districtMainLogged.Add(affinity + ":load"))
                    {
                        var mat = TryLoadMaterial(guid);
                        if (mat == null) Plugin.Log.LogWarning($"[DistrictMain] our selector GUID does NOT load — rebuild the mod bundle (the selector asset / its GUID isn't in it).");
                        else Plugin.Log.LogInfo($"[DistrictMain] our selector GUID loads OK: {mat.GetType().Name} '{GetMember(mat, "name")}'");
                    }
                }
                // The cell now points at our selector, but districts already built resolved+cached the vanilla visual
                // BEFORE this fill could run. Replay UpdateLevelBuild on them ONCE so they re-read the filled cell.
                // Districts built AFTER this point resolve the filled cell natively and need no replay.
                if (totalFilled > 0 && !districtReresolved) { districtReresolved = true; ForceDistrictReresolve(); }
                // Then bind a real building output layer onto our (null-outputLayer) element so the reactor mesh draws on
                // top of the footprint. Selectors load async after the re-resolve, so retry each poll until it binds once.
                if (districtReresolved) BindReactorBuilding(null);   // legacy shared DistrictMainRows path (no per-district S)
            }
            catch (Exception ex) { if (districtMainLogged.Add("ex")) Plugin.Log.LogError("[DistrictMain] " + ex); }
        }

        // SCOPED dedicated-visual: put our DATA-AUTHORED selector on ONLY the named district's own tile, matched by
        // ConstructibleDefinitionName — leaving the shared visual affinity (Base_Industry) and every other district
        // using it untouched, so a player WITHOUT the plugin still sees the vanilla fallback. Runs every frame with a
        // cheap ReferenceEquals guard (the game re-resolves the channel on its own UpdateLevelBuild; we re-assert). The
        // building element's output layer is bound via the shared BindReactorBuilding once our selector is on a channel.
        [ProcessLived("districtName -> guid parsed from the pack; names are process-lived")] static readonly Dictionary<string, object> selectorTileGuid = new Dictionary<string, object>();   // districtName -> parsed guid
        [SessionScoped(Scope = SessionScope.District)] static readonly Dictionary<object, string> districtNameCache = new Dictionary<object, string>();   // PresentationDistrict -> ConstructibleDefinitionName (perf 2026-08-21)
        static string selectorTileParsedFrom;                                                             // config string we parsed
        static int selectorTileRegCount = -1;                                                             // distModels count at last (re)build — rebuild the map when the registry changes
        [SessionScoped(Scope = SessionScope.District)] static readonly Dictionary<string, object> loadedSelectorByKey = new Dictionary<string, object>();// guidKey -> loaded+Loaded selector
        [SessionScoped(Scope = SessionScope.District)] static readonly HashSet<string> selectorTileLogged = new HashSet<string>();
        internal static void PollDistrictSelectorTile()
        {
            if (distFxManager == null || trackedDistricts.Count == 0) return;
            try
            {
                long tCfg = FrameCost.Begin(); EnsureDistrictConfig(); FrameCost.End(FrameCost.SelTileCfg, tCfg);   // populates distModels (per-entry atlasGuid + selectorGuid) even with DistrictRepoint off
                var cfg = Plugin.DistrictSelectorTile?.Value?.Trim() ?? "";
                // (re)build the scoped map from the CONFIG **and** any registry entry that baked a selectorGuid (editor-driven,
                // no config line needed) — rebuild when the config text OR the registry set changes.
                if (selectorTileParsedFrom != cfg || selectorTileRegCount != distModels.Count)
                {
                    selectorTileGuid.Clear();
                    foreach (var part in cfg.Split(';'))
                    {
                        var eq = part.IndexOf('='); if (eq <= 0) continue;
                        var name = part.Substring(0, eq).Trim();
                        var g = ParseGuid4(part.Substring(eq + 1).Trim());
                        if (g != null) selectorTileGuid[name] = g;
                        else if (selectorTileLogged.Add(name + ":badguid")) Plugin.Log.LogWarning($"[DistrictTile] '{name}': unparseable guid");
                    }
                    foreach (var dm in distModels)   // registry-authored scoped districts (the migration path)
                        if (dm.selectorGuid != null && dm.district.Length > 0 && !selectorTileGuid.ContainsKey(dm.district)) selectorTileGuid[dm.district] = dm.selectorGuid;
                    selectorTileParsedFrom = cfg; selectorTileRegCount = distModels.Count;
                }
                if (selectorTileGuid.Count == 0) return;
                long tLoop = FrameCost.Begin();
                foreach (var d in trackedDistricts)
                {
                    if (d is UnityEngine.Object duo && duo == null) continue;
                    // name resolved ONCE per PresentationDistrict (a reflection read + a StaticString ToString alloc, ×17
                    // districts × 60 fps before — perf pass 2026-08-21); the cache is cleared with trackedDistricts on reset
                    if (!districtNameCache.TryGetValue(d, out var name)) districtNameCache[d] = name = GetMember(d, "ConstructibleDefinitionName")?.ToString();
                    if (string.IsNullOrEmpty(name) || !selectorTileGuid.TryGetValue(name, out var guid)) continue;
                    S = ScopedFor(name);   // PER-DISTRICT: point the scoped-state proxies at THIS district before any scoped work (texture / B&W / flatten no longer clash between the reactor and the Oracle)
                    // resolve THIS district's own baked albedo atlas from the registry (for the scoped texture bind)
                    if (scopedAtlasGuid == null)
                        foreach (var dm in distModels) if (dm.district == name && dm.atlasGuid != null) { scopedAtlasGuid = dm.atlasGuid; break; }
                    // NOTE: no ground-paint re-assert here. Fighting the game's native GroundMaterialDefinition every N
                    // frames just flickers the surface (Dry_03 <-> native = a visible twitch), and the ground index was
                    // never the lever for the Industry look anyway — that comes from the native selector's paving, which
                    // we address by grafting, not by overriding ApplyGroundMaterialDefinition.
                    // load + fully Load our selector once (cached), so its decal subtree + element are live
                    string key = name;
                    if (!loadedSelectorByKey.TryGetValue(key, out var sel) || (sel is UnityEngine.Object suo && suo == null))
                    {
                        sel = TryLoadMaterial(guid);
                        if (sel == null) { if (selectorTileLogged.Add(name + ":noload")) Plugin.Log.LogWarning($"[DistrictTile] '{name}': selector GUID does NOT load — rebuild the mod bundle."); continue; }
                        LoadFxMaterial(sel);
                        CenterScopedBuilding(sel, name);   // our reactor sits in the template's off-center slot — re-center to tile origin (match the preview)
                        GraftFootprint(sel, name);         // RUNTIME footprint choice: swap our selector's decals for a chosen donor's
                        DumpDecalBinding(sel, name);       // DIAGNOSTIC: are the gravel decals' visualOutput layers bound? masked by terrain?
                        DumpSchematicAtlas(sel, name);     // DE-RISK: dump the SchematicView output layer's atlas structure (can we inject our silhouette?)
                        InjectReactorFootprint(sel, name); // UNIQUE footprint: inject our model silhouette as the SchematicView mask (config DistrictFootprintMask)
                        KeepDistrictMeshAtStrategicZoom(sel, name); // MESH footprint: keep the 3D building mesh visible at strategic zoom (config DistrictFootprintMesh)
                        // UnmaskPavingDecals refuted: decals are bound AND unmasked yet still don't draw close-zoom — the
                        // cause is elsewhere (render-pass / emitter-vs-selector), so we no longer mutate the shared decals.
                        loadedSelectorByKey[key] = sel;
                        Plugin.Log.LogInfo($"[DistrictTile] '{name}': loaded our selector {sel.GetType().Name} '{GetMember(sel, "name")}'.");
                    }
                    var plbc = (fiDistrictPlbc ?? (fiDistrictPlbc = AccessTools.Field(d.GetType(), "presentationLevelBuildComponent")))?.GetValue(d);
                    if (plbc == null) continue;
                    DumpPlbcLevers(plbc);   // one-shot (DistrictDebug): the real channel/refresh/content methods + EventNameEnum values
                    if (fiPlbcChannels == null) fiPlbcChannels = AccessTools.Field(plbc.GetType(), "channels");
                    if (!(fiPlbcChannels?.GetValue(plbc) is Array channels)) continue;
                    DumpAllChannels(channels, name);   // DIAGNOSTIC: is the rocky exploitation ground on a separate channel we can swap?
                    DumpGroundMatchers();              // DIAGNOSTIC: find the terrain match entries keyed on Exploitation + what they render
                    int layer = ResolveMainLayer(d);
                    if (layer < 0 || layer >= channels.Length) continue;
                    var box = channels.GetValue(layer);
                    if (box == null) continue;
                    if (fiChanEvolverMaterial == null) fiChanEvolverMaterial = GF(box.GetType(), "evolverMaterial");
                    if (fiChanEvolverMaterial == null) continue;
                    var curMat = fiChanEvolverMaterial.GetValue(box);
                    // DIAGNOSTIC: the channel still holds the NATIVE Industry selector the first frame — dump its element
                    // tree next to ours so we can see the ground element the old (isolate) path kept and we lost.
                    if (curMat != null && !ReferenceEquals(curMat, sel)) { DumpSelectorElements(curMat, "NATIVE", name); DumpSelectorElements(sel, "OURS", name); DumpNativeGroundCandidates(curMat, name); }
                    if (!ReferenceEquals(curMat, sel))   // not ours yet — (re)place our selector on this district's channel
                    {
                        // TWITCH DIAG: the channel ISN'T ours — the game reset it and we're re-emitting (RefreshChannel
                        // re-renders the whole selector incl. footprint). Steady logging here = the footprint re-emitting
                        // every frame = the twitch the user spotted.
                        if (Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value && scopedReemitLog < 40)
                        { scopedReemitLog++; Plugin.Log.LogInfo($"[TwitchDiag] '{name}': channel wasn't ours ('{GetMember(curMat, "name")}') — re-emitting selector @ frame {UnityEngine.Time.frameCount} (re-emit #{scopedReemitLog})"); }
                        fiChanEvolverMaterial.SetValue(box, sel);
                        channels.SetValue(box, layer);   // write the mutated struct back into the array
                        if (miRefreshChannel == null)
                            miRefreshChannel = plbc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                                .FirstOrDefault(m => m.Name == "RefreshChannel" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                        if (miRefreshChannel != null)
                        {
                            var ra = new object[] { layer, System.Enum.ToObject(miRefreshChannel.GetParameters()[1].ParameterType, 0) };
                            try { miRefreshChannel.Invoke(plbc, ra); } catch { }
                        }
                        if (selectorTileLogged.Add(name + ":set")) Plugin.Log.LogInfo($"[DistrictTile] '{name}': our selector placed on channel {layer} (this tile only; shared affinity untouched).");
                    }
                    // PER-DISTRICT (S = this district's state): bind its element to its OWN donor-layer clone, then bind its
                    // albedo + drive its flatten. These ran AFTER the loop before — so only the LAST scoped district got
                    // processed (a 2nd district rendered untextured / shared the first's layer). Now each owns its state.
                    // UNBOUND: the bind walks every leaf of every tracked district looking for a donor layer — 42 ms/frame
                    // during a load, every frame, until the leaves exist (FrameCost 2026-08-21). Retry twice a second;
                    // once bound (S.donorClone set) the call is a no-op anyway.
                    long tb = FrameCost.Begin();
                    if (S.donorClone != null || (UnityEngine.Time.frameCount % 30) == 0) BindReactorBuilding(name);
                    FrameCost.End(FrameCost.SelTileBind, tb);
                    tb = FrameCost.Begin(); ApplyScopedAlbedo();  FrameCost.End(FrameCost.SelTileAlbedo, tb);
                    tb = FrameCost.Begin(); UpdateMeshFlatness(); FrameCost.End(FrameCost.SelTileFlat, tb);
                }
                FrameCost.End(FrameCost.SelTileLoop, tLoop);   // the whole district loop (head cost = loop − bind − albedo − flat)
            }
            catch (Exception ex) { if (selectorTileLogged.Add("ex")) Plugin.Log.LogError("[DistrictTile] " + ex); }
        }

        // Prepare our selector before it goes on the tile: (1) CENTER — our reactor sits in the NuclearTest template's
        // MAIN-building slot, placed OFF-CENTER within its multi-building layout (LevelBuildItem.Position); a single
        // reactor belongs at tile origin like the preview, so zero that item's Position (the only root item whose child
        // is an Element with an `fxMesh` field). (2) INSTANT APPEAR — every node's `fadeInOutMode` is the reveal-ramp
        // (Stepped/Smooth ramp in over ~1 s); set the whole tree (building + footprint decals) to Instant so the
        // footprint shows the moment the tile draws, not a second later. One re-emit applies both. Runs once at load.
        static void CenterScopedBuilding(object sel, string name)
        {
            try
            {
                bool changed = false;
                changed |= SetInstantAppear(sel, 0, new HashSet<object>());   // kill the ~1s reveal ramp across the whole tree (twitch-test ruled this out)
                var itemsF = GF(sel.GetType(), "levelBuildItems");
                if (itemsF?.GetValue(sel) is Array items)
                    for (int i = 0; i < items.Length; i++)
                    {
                        var it = items.GetValue(i); if (it == null) continue;
                        var itt = it.GetType();
                        var child = GF(itt, "loadedEvolverMaterial")?.GetValue(it);
                        if (child == null || GF(child.GetType(), "fxMesh") == null) continue;   // not our building Element
                        var pf = GF(itt, "Position");
                        if (pf?.GetValue(it) is UnityEngine.Vector3 p && p != UnityEngine.Vector3.zero)
                        {
                            pf.SetValue(it, UnityEngine.Vector3.zero);
                            items.SetValue(it, i);   // write the mutated struct back into the array
                            changed = true;
                            if (selectorTileLogged.Add(name + ":center")) Plugin.Log.LogInfo($"[DistrictTile] '{name}': centered reactor on the tile (template slot was at pos={p}).");
                        }
                    }
                if (changed) LoadFxMaterial(sel);   // re-emit so the child is centered + the ramp is Instant
            }
            catch (Exception ex) { if (selectorTileLogged.Add(name + ":centerex")) Plugin.Log.LogWarning("[DistrictTile] center: " + ex.Message); }
        }

        // Recursively set every node's fadeInOutMode -> Instant (building elements + footprint decals + nested selectors/
        // emitters), so the strategic footprint appears the instant the tile draws instead of ramping in over ~1 s. Walks
        // the same child links as CollectLeaves (levelBuildItems -> loadedEvolverMaterial, selector cache Entries).
        static bool SetInstantAppear(object mat, int depth, HashSet<object> visited)
        {
            if (mat == null || depth > 8 || !visited.Add(mat)) return false;
            bool changed = false;
            var t = mat.GetType();
            var fm = GF(t, "fadeInOutMode");
            if (fm != null)
            {
                try { var inst = Enum.Parse(fm.FieldType, "Instant"); if (!Equals(fm.GetValue(mat), inst)) { fm.SetValue(mat, inst); changed = true; } }
                catch { }
            }
            // GFA (memoized AccessTools resolution) — CollectLeaves' twin recursion, same reason. See GF/GFA in DistrictInject.cs.
            if (GFA(t, "levelBuildItems")?.GetValue(mat) is Array items)
                foreach (var it in items) if (it != null) changed |= SetInstantAppear(GFA(it.GetType(), "loadedEvolverMaterial")?.GetValue(it), depth + 1, visited);
            var cache = GFA(t, "fxMaterialCacheEntries")?.GetValue(mat);
            if (cache != null && GFA(cache.GetType(), "Entries")?.GetValue(cache) is Array entries)
                foreach (var e in entries) if (e != null) changed |= SetInstantAppear(GFA(e.GetType(), "FxMaterial")?.GetValue(e), depth + 1, visited);
            return changed;
        }

        // One-shot: dump the live plbc's channel/refresh/content methods + the EventNameEnum values, so we can find a
        // HEAVIER refresh than RefreshChannel(0) that trips the decal content rebuild (pre-warm the footprint).
        static bool plbcDumped;
        [ProcessLived("diagnostic: enum names seen once per process")] static readonly HashSet<string> plbcEnumsSeen = new HashSet<string>();
        static void DumpPlbcLevers(object plbc)
        {
            if (plbcDumped || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;
            plbcDumped = true;
            try
            {
                var t = plbc.GetType();
                Plugin.Log.LogInfo($"[PlbcLevers] type = {t.FullName}");
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var n = m.Name;
                    if (n.IndexOf("Channel", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Refresh", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Content", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Rebuild", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Invalidate", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Dirty", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("SetChannel", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Ask", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var pars = m.GetParameters();
                    var ps = string.Join(", ", pars.Select(p => p.ParameterType.Name + " " + p.Name));
                    Plugin.Log.LogInfo($"[PlbcLevers]   {m.ReturnType.Name} {n}({ps})");
                    // dump any enum param's values (RefreshChannel/SetChannel's event/mode enums = the lever candidates)
                    foreach (var p in pars)
                        if (p.ParameterType.IsEnum && plbcEnumsSeen.Add(p.ParameterType.FullName))
                            Plugin.Log.LogInfo($"[PlbcLevers]     enum {p.ParameterType.Name} = {string.Join(", ", Enum.GetNames(p.ParameterType))}");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[PlbcLevers] " + ex.Message); }
        }

        // RUNTIME footprint choice: replace our selector's DECAL items with a chosen donor selector's decals, so the
        // strategic footprint can be picked in-game (config DistrictFootprint) without a re-bake — the building stays
        // ours. Keeps our building-Element item(s) (child has an `fxMesh`), drops our decal items, appends the donor's
        // decal items (collected recursively, skipping the donor's BUILDINGS). Then re-emits. Best-effort: some donors'
        // nested decals may not transfer cleanly — logged, and the un-grafted selector still renders its baked footprint.
        [SessionScoped(Scope = SessionScope.District, Manual = "footprint graft pass, DistrictInject.Scoped.cs")] static readonly Dictionary<string, object> footprintDonor = new Dictionary<string, object>();
        static string footprintParsedFrom;
        [SessionScoped(Scope = SessionScope.District, Manual = "per graft pass, DistrictInject.Scoped.cs")] static readonly HashSet<string> graftDedup = new HashSet<string>();        // (decal name|position) dedup across culture variants
        [SessionScoped(Scope = SessionScope.District, Manual = "per graft pass, DistrictInject.Scoped.cs")] static readonly HashSet<string> graftDecalNames = new HashSet<string>();   // distinct decal names grafted (for the log)
        static void GraftFootprint(object sel, string name)
        {
            try
            {
                // REGISTRY first: a distModels entry's footprintDonor (picked in the District Factory) wins over the config.
                object donorGuid = null;
                foreach (var e in distModels) if (e.district == name && e.footprintDonor != null) { donorGuid = e.footprintDonor; break; }
                // Fallback: the global DistrictFootprint config ("name=a,b,c,d;name=..."), parsed once and cached.
                if (donorGuid == null)
                {
                    var cfg = Plugin.DistrictFootprint?.Value?.Trim();
                    if (!string.IsNullOrEmpty(cfg))
                    {
                        if (footprintParsedFrom != cfg)
                        {
                            footprintDonor.Clear();
                            foreach (var part in cfg.Split(';'))
                            {
                                var eq = part.IndexOf('='); if (eq <= 0) continue;
                                var g = ParseGuid4(part.Substring(eq + 1).Trim());
                                if (g != null) footprintDonor[part.Substring(0, eq).Trim()] = g;
                            }
                            footprintParsedFrom = cfg;
                        }
                        footprintDonor.TryGetValue(name, out donorGuid);
                    }
                }
                if (donorGuid == null) return;
                var donor = TryLoadMaterial(donorGuid);
                if (donor == null) { if (selectorTileLogged.Add(name + ":fpnoload")) Plugin.Log.LogWarning($"[DistrictTile] '{name}': footprint donor GUID does NOT load."); return; }
                LoadFxMaterial(donor);
                var donorDecals = new List<object>();
                graftDedup.Clear(); graftDecalNames.Clear();
                CollectDecalItems(donor, donorDecals, 0, new HashSet<object>());
                // SURFACE-TEXTURE filter (configurable, DistrictFootprintDrop): drop decals whose name contains any of the
                // listed substrings — by default the gravel + battlement-rubble "rocks" layers that render at close 3D zoom
                // and TWITCH at the strategic<->3D boundary. Blank config = keep ALL donor decals (rock texture included).
                var dropCfg = Plugin.DistrictFootprintDrop?.Value?.Trim();
                if (!string.IsNullOrEmpty(dropCfg))
                {
                    var pats = dropCfg.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                    int beforeFilter = donorDecals.Count;
                    donorDecals.RemoveAll(it =>
                    {
                        var child = GF(it.GetType(), "loadedEvolverMaterial")?.GetValue(it) ?? TryLoadMaterial(GF(it.GetType(), "EvolverMaterialGuid")?.GetValue(it));
                        var nm = child != null ? GetMember(child, "name")?.ToString() : null;
                        if (nm == null) return false;
                        foreach (var p in pats) if (nm.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        return false;
                    });
                    if (beforeFilter != donorDecals.Count && selectorTileLogged.Add(name + ":norocks"))
                        Plugin.Log.LogInfo($"[DistrictTile] '{name}': dropped {beforeFilter - donorDecals.Count} footprint decal(s) matching DistrictFootprintDrop='{dropCfg}' (surface-texture filter).");
                }
                if (donorDecals.Count == 0) { if (selectorTileLogged.Add(name + ":fpnodecals")) Plugin.Log.LogWarning($"[DistrictTile] '{name}': footprint donor has no collectable decal items."); return; }
                if (selectorTileLogged.Add(name + ":fpnames"))
                    Plugin.Log.LogInfo($"[DistrictTile] '{name}': footprint decals ({graftDecalNames.Count} distinct): {string.Join(", ", System.Linq.Enumerable.Take(graftDecalNames, 16))}");
                var itemsF = GF(sel.GetType(), "levelBuildItems");
                if (!(itemsF?.GetValue(sel) is Array items)) return;
                var kept = new List<object>();   // our building-element item(s) only — drop our own decals/emitters
                foreach (var it in items)
                {
                    if (it == null) continue;
                    var child = GF(it.GetType(), "loadedEvolverMaterial")?.GetValue(it);
                    if (child != null && GF(child.GetType(), "fxMesh") != null) kept.Add(it);   // building Element = our reactor
                }
                var elemType = items.GetType().GetElementType();
                var arr = Array.CreateInstance(elemType, kept.Count + donorDecals.Count);
                int k = 0;
                foreach (var it in kept) arr.SetValue(it, k++);
                foreach (var it in donorDecals) arr.SetValue(it, k++);
                itemsF.SetValue(sel, arr);
                LoadFxMaterial(sel);   // re-emit with our building + the donor's footprint
                if (selectorTileLogged.Add(name + ":fpgraft")) Plugin.Log.LogInfo($"[DistrictTile] '{name}': grafted {donorDecals.Count} footprint decal item(s) from the chosen donor (kept {kept.Count} building slot(s)).");
            }
            catch (Exception ex) { if (selectorTileLogged.Add(name + ":fpex")) Plugin.Log.LogError("[DistrictTile] footprint graft: " + ex); }
        }

        // Collect a donor selector's DECAL items (the footprint), skipping BuildElement (building) items. Walks the FULL
        // tree like CollectLeaves — donors are either flat EMITTERs (NuclearTest: decals in levelBuildItems) OR SELECTORs
        // (MissileSilo/city districts: decals reached via pairs / fxMaterialCacheEntries → nested emitters). A child whose
        // type name contains "Decal" → collect the ITEM; anything else is recursed into. `visited` dedups shared sub-trees
        // (so a culture-agnostic national project's one shared emitter isn't collected per culture).
        static void CollectDecalItems(object mat, List<object> outItems, int depth, HashSet<object> visited)
        {
            if (mat == null || depth > 12 || !visited.Add(mat)) return;
            var t = mat.GetType();
            // EMITTER: levelBuildItems — collect decal items, recurse into non-decal children (emitters/selectors)
            if (GF(t, "levelBuildItems")?.GetValue(mat) is Array items)
                foreach (var it in items)
                {
                    if (it == null) continue;
                    var itt = it.GetType();
                    var child = GF(itt, "loadedEvolverMaterial")?.GetValue(it) ?? TryLoadMaterial(GF(itt, "EvolverMaterialGuid")?.GetValue(it));
                    if (child == null) continue;
                    if (child.GetType().Name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // DEDUP by decal name + position — a CityMapSelector repeats the same footprint decals across
                        // every culture variant (why MissileSilo collected 207); keep one per distinct (name, position).
                        var nm = GetMember(child, "name")?.ToString() ?? "?";
                        var pos = GF(itt, "Position")?.GetValue(it);
                        if (graftDedup.Add(nm + "|" + (pos?.ToString() ?? ""))) { outItems.Add(it); graftDecalNames.Add(nm); }
                    }
                    else CollectDecalItems(child, outItems, depth + 1, visited);
                }
            // SELECTOR: loaded cache entries + the pairs variant table + default/invalid fallbacks (reach nested emitters)
            var cache = GF(t, "fxMaterialCacheEntries")?.GetValue(mat);
            if (cache != null && AccessTools.Field(cache.GetType(), "Entries")?.GetValue(cache) is Array entries)
                foreach (var e in entries) if (e != null) CollectDecalItems(AccessTools.Field(e.GetType(), "FxMaterial")?.GetValue(e), outItems, depth + 1, visited);
            if (GF(t, "pairs")?.GetValue(mat) is Array pairs)
                foreach (var pr in pairs) if (pr != null) { var g = PairGuid(pr); if (!GuidIsNull(g)) CollectDecalItems(TryLoadMaterial(g), outItems, depth + 1, visited); }
            foreach (var fn in new[] { "defaultMaterial", "invalidNameMaterial" })
            { var g = GF(t, fn)?.GetValue(mat); if (g != null && !GuidIsNull(g)) CollectDecalItems(TryLoadMaterial(g), outItems, depth + 1, visited); }
        }

        // DIAGNOSTIC (DistrictDebug): dump a selector's element tree — each child's type + name + whether it carries an
        // fxMesh (a building or GROUND mesh) or is a Decal. Comparing the NATIVE Industry selector (which had the paved
        // ground the old isolate path kept) with OUR scoped template selector reveals the exact ground element to graft.
        [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> selDumpLogged = new HashSet<string>();
        static void DumpSelectorElements(object mat, string label, string name)
        {
            if (Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value || mat == null) return;
            string key = name + "|" + label;
            if (selDumpLogged.Contains(key)) return;
            try
            {
                var lines = new List<string>();
                WalkSelectorElements(mat, 0, new HashSet<object>(), lines);
                if (lines.Count == 0) return;   // still loading — try again next frame (don't lock the one-shot yet)
                selDumpLogged.Add(key);
                Plugin.Log.LogInfo($"[SelDump] '{name}' {label}: {mat.GetType().Name} '{GetMember(mat, "name")}' ({lines.Count} elems)");
                foreach (var ln in lines) Plugin.Log.LogInfo("[SelDump]   " + ln);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[SelDump] " + ex.Message); }
        }
        static void WalkSelectorElements(object mat, int depth, HashSet<object> visited, List<string> outLines)
        {
            if (mat == null || depth > 8 || !visited.Add(mat)) return;
            var t = mat.GetType();
            if (GF(t, "levelBuildItems")?.GetValue(mat) is Array items)
                foreach (var it in items)
                {
                    if (it == null) continue;
                    var itt = it.GetType();
                    var child = GF(itt, "loadedEvolverMaterial")?.GetValue(it) ?? TryLoadMaterial(GF(itt, "EvolverMaterialGuid")?.GetValue(it));
                    if (child == null) continue;
                    var ct = child.GetType();
                    bool hasMesh = GF(ct, "fxMesh") != null;
                    bool isDecal = ct.Name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0;
                    var pos = GF(itt, "Position")?.GetValue(it);
                    outLines.Add($"{new string(' ', depth * 2)}{ct.Name} '{GetMember(child, "name")}' mesh={hasMesh} decal={isDecal} pos={pos}");
                    if (!isDecal) WalkSelectorElements(child, depth + 1, visited, outLines);
                }
            var cache = GF(t, "fxMaterialCacheEntries")?.GetValue(mat);
            if (cache != null && AccessTools.Field(cache.GetType(), "Entries")?.GetValue(cache) is Array entries)
                foreach (var e in entries) if (e != null) WalkSelectorElements(AccessTools.Field(e.GetType(), "FxMaterial")?.GetValue(e), depth + 1, visited, outLines);
            if (GF(t, "pairs")?.GetValue(mat) is Array pairs)
                foreach (var pr in pairs) if (pr != null) { var g = PairGuid(pr); if (!GuidIsNull(g)) WalkSelectorElements(TryLoadMaterial(g), depth + 1, visited, outLines); }
        }

        // DIAGNOSTIC (DistrictDebug): for each distinct decal in our selector, report whether its visualOutput FxOutputLayer
        // is bound (OutputLayerIndex / LoadedOutputLayer) and whether it's maskedByTerrain. A gravel decal with an unbound
        // visualOutput writes NO render data (FxEvolverMaterialLevelBuildDecal.AddDataTo early-outs on OutputLayerIndex<0),
        // which would explain why the Industry paving never draws at close zoom while the schematic footprint does.
        [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> decalBindLogged = new HashSet<string>();
        static void DumpDecalBinding(object sel, string name)
        {
            if (Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value || sel == null) return;
            if (!decalBindLogged.Add(name)) return;
            try
            {
                var decals = new List<object>();
                CollectDecalMaterials(sel, decals, 0, new HashSet<object>());
                var seen = new HashSet<string>(); int shown = 0;
                Plugin.Log.LogInfo($"[DecalBind] '{name}': {decals.Count} decal material(s) in our selector");
                foreach (var dc in decals)
                {
                    var nm = GetMember(dc, "name")?.ToString() ?? "?";
                    if (!seen.Add(nm) || shown >= 16) continue;
                    shown++;
                    var vo = GF(dc.GetType(), "visualOutput")?.GetValue(dc);
                    var oli = vo != null ? GetMember(vo, "OutputLayerIndex") : null;
                    var loaded = vo != null ? GetMember(vo, "LoadedOutputLayer") : null;
                    bool loadedNull = loaded == null || (loaded is UnityEngine.Object luo && luo == null);
                    var layerName = loadedNull ? "NULL" : (GetMember(loaded, "name")?.ToString() ?? "?");
                    var masked = GF(dc.GetType(), "maskedByTerrain")?.GetValue(dc);
                    Plugin.Log.LogInfo($"[DecalBind]   {nm}: OutputLayerIndex={oli} layer='{layerName}' maskedByTerrain={masked}");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[DecalBind] " + ex.Message); }
        }
        // EXPERIMENT: the Industry gravel/paving decals are maskedByTerrain=True — they draw only where the tile terrain is
        // "cleared" (a normal city tile). A native Industry tile is cleared, so its identical gravel shows; our reactor is a
        // DEPOSIT district whose tile keeps its natural terrain, so the mask hides the gravel and bare rock shows through.
        // Clearing the mask on our paving (CityBricks_*) decals forces them to draw regardless of the terrain-clear state.
        // NOTE: these decal materials are shared game assets — this mutates them process-wide, but forcing false on gravel
        // that already shows on cleared tiles is visually a no-op there. If this proves the fix, switch to private clones.
        [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> unmaskLogged = new HashSet<string>();
        static void UnmaskPavingDecals(object sel, string name)
        {
            try
            {
                var decals = new List<object>();
                CollectDecalMaterials(sel, decals, 0, new HashSet<object>());
                int changed = 0;
                foreach (var dc in decals)
                {
                    var nm = GetMember(dc, "name")?.ToString() ?? "";
                    if (nm.IndexOf("CityBricks", StringComparison.OrdinalIgnoreCase) < 0) continue;   // only the paving decals
                    var f = GF(dc.GetType(), "maskedByTerrain");
                    if (f != null && f.GetValue(dc) is bool b && b) { f.SetValue(dc, false); changed++; }
                }
                if (changed > 0)
                {
                    LoadFxMaterial(sel);   // re-emit so the cleared mask flag reaches the GPU render data
                    if (unmaskLogged.Add(name)) Plugin.Log.LogInfo($"[DistrictTile] '{name}': cleared maskedByTerrain on {changed} paving decal(s) so the Industry gravel draws over the deposit terrain.");
                }
            }
            catch (Exception ex) { if (unmaskLogged.Add(name + ":ex")) Plugin.Log.LogWarning("[DistrictTile] unmask: " + ex.Message); }
        }
        // DIAGNOSTIC (DistrictDebug): dump EVERY channel on the reactor's plbc + the evolver material each holds. The rocky
        // exploitation ground may render on a channel other than the main level-build one; if so we can swap/clear it the way
        // we swap the main channel's selector. Prints channel index, evolver type + name.
        [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> chanDumpLogged = new HashSet<string>();
        static void DumpAllChannels(Array channels, string name)
        {
            if (Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value || !chanDumpLogged.Add(name)) return;
            try
            {
                Plugin.Log.LogInfo($"[Chans] '{name}': {channels.Length} channel(s)");
                for (int i = 0; i < channels.Length; i++)
                {
                    var box = channels.GetValue(i);
                    if (box == null) { Plugin.Log.LogInfo($"[Chans]   [{i}] <null box>"); continue; }
                    if (fiChanEvolverMaterial == null) fiChanEvolverMaterial = GF(box.GetType(), "evolverMaterial");
                    var em = fiChanEvolverMaterial?.GetValue(box);
                    var emName = em == null ? null : GetMember(em, "name")?.ToString();
                    Plugin.Log.LogInfo($"[Chans]   [{i}] {(em == null ? "<null evolver>" : em.GetType().Name + " '" + emName + "'")}");
                    // dump the tree of the SMALL side channels (fence / additional layers) — a ground element would live here
                    if (em != null && emName != null && emName != "CityMapSelector_Industry_00")
                    {
                        var lines = new List<string>();
                        WalkSelectorElements(em, 0, new HashSet<object>(), lines);
                        foreach (var ln in System.Linq.Enumerable.Take(lines, 24)) Plugin.Log.LogInfo("[Chans]       " + ln);
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Chans] " + ex.Message); }
        }
        // DIAGNOSTIC (DistrictDebug): enumerate every loaded FxEvolverMaterialLevelBuildMatching and print the LevelBuildMatch
        // entries whose conditions set Exploitation / District (ChoiceEnum != NotSet), plus what emitter each renders. This
        // locates the exact terrain match rule that draws the rocky exploitation ground — the rule we'd override to clear it.
        static bool matchDumped;
        static int scopedReemitLog;   // TWITCH DIAG counter (scoped channel re-emits = game resetting the channel)
        internal static void DumpGroundMatchers()
        {
            if (matchDumped || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;
            matchDumped = true;
            try
            {
                var matchType = AccessTools.TypeByName("Amplitude.Mercury.Terrain.Fx.FxEvolverMaterialLevelBuildMatching");
                if (matchType == null) { Plugin.Log.LogWarning("[Match] FxEvolverMaterialLevelBuildMatching type not found"); return; }
                var all = UnityEngine.Resources.FindObjectsOfTypeAll(matchType);
                Plugin.Log.LogInfo($"[Match] {all.Length} loaded matching material(s); scanning for Exploitation/District entries");
                var fElements = GF(matchType, "elements");
                int reported = 0;
                foreach (var mm in all)
                {
                    if (!(fElements?.GetValue(mm) is Array elems)) continue;
                    var mmName = GetMember(mm, "name")?.ToString();
                    foreach (var lbm in elems)
                    {
                        if (lbm == null) continue;
                        var lt = lbm.GetType();
                        if (!(GF(lt, "levelBuildMatchElements")?.GetValue(lbm) is Array conds)) continue;
                        string flags = "";
                        foreach (var c in conds)
                        {
                            if (c == null) continue;
                            var ct = c.GetType();
                            var expl = GF(ct, "Exploitation")?.GetValue(c)?.ToString();
                            var dist = GF(ct, "District")?.GetValue(c)?.ToString();
                            if (!string.IsNullOrEmpty(expl) && expl != "NotSet") flags += $" Exploitation={expl}";
                            if (!string.IsNullOrEmpty(dist) && dist != "NotSet") flags += $" District={dist}";
                        }
                        if (flags.IndexOf("Exploitation", StringComparison.Ordinal) < 0) continue;
                        var nm = GF(lt, "name")?.GetValue(lbm)?.ToString();
                        var emitterGuid = GF(lt, "emitter")?.GetValue(lbm);
                        string emName = "?";
                        try { var em = TryLoadMaterial(emitterGuid); if (em != null) emName = GetMember(em, "name")?.ToString(); } catch { }
                        if (reported++ < 50)
                            Plugin.Log.LogInfo($"[Match] mat='{mmName}' entry='{nm}'{flags} -> emitter='{emName}'");
                    }
                }
                Plugin.Log.LogInfo($"[Match] done — {reported} entry(ies) reference Exploitation");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Match] " + ex); }
        }
        // DIAGNOSTIC (DistrictDebug): scan the NATIVE industry selector for terrain-conforming GROUND/paving MESH elements
        // (fxMesh != null AND a ground-ish name) — the close-zoom cover we discard when we swap the selector. Deduped by name,
        // with position, so we can identify the exact element to graft back into our reactor selector.
        [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> nativeGroundLogged = new HashSet<string>();
        [ProcessLived("literal substring vocabulary")] static readonly string[] groundHints = { "Brick", "Ground", "Floor", "Pave", "Concrete", "Asphalt", "Gravel", "Dirt", "Terrain", "Plaza", "Road", "Tarmac", "Slab", "Cobble", "Bricks" };
        static void DumpNativeGroundCandidates(object mat, string name)
        {
            if (Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value || mat == null || !nativeGroundLogged.Add(name)) return;
            try
            {
                var found = new Dictionary<string, string>();
                WalkGroundCandidates(mat, 0, new HashSet<object>(), found);
                Plugin.Log.LogInfo($"[NativeGround] '{name}': {found.Count} ground-candidate mesh element(s) in the native selector");
                int shown = 0;
                foreach (var kv in found) { if (shown++ >= 40) break; Plugin.Log.LogInfo($"[NativeGround]   {kv.Key} {kv.Value}"); }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[NativeGround] " + ex.Message); }
        }
        static void WalkGroundCandidates(object mat, int depth, HashSet<object> visited, Dictionary<string, string> found)
        {
            if (mat == null || depth > 10 || !visited.Add(mat)) return;
            var t = mat.GetType();
            if (GF(t, "levelBuildItems")?.GetValue(mat) is Array items)
                foreach (var it in items)
                {
                    if (it == null) continue;
                    var itt = it.GetType();
                    var child = GF(itt, "loadedEvolverMaterial")?.GetValue(it) ?? TryLoadMaterial(GF(itt, "EvolverMaterialGuid")?.GetValue(it));
                    if (child == null) continue;
                    bool hasMesh = GF(child.GetType(), "fxMesh") != null;
                    var nm = GetMember(child, "name")?.ToString() ?? "?";
                    if (hasMesh && !found.ContainsKey(nm))
                    {
                        bool isGround = false;
                        foreach (var h in groundHints) if (nm.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) { isGround = true; break; }
                        if (isGround) found[nm] = "pos=" + (GF(itt, "Position")?.GetValue(it)?.ToString() ?? "?");
                    }
                    WalkGroundCandidates(child, depth + 1, visited, found);
                }
            var cache = GF(t, "fxMaterialCacheEntries")?.GetValue(mat);
            if (cache != null && AccessTools.Field(cache.GetType(), "Entries")?.GetValue(cache) is Array entries)
                foreach (var e in entries) if (e != null) WalkGroundCandidates(AccessTools.Field(e.GetType(), "FxMaterial")?.GetValue(e), depth + 1, visited, found);
            if (GF(t, "pairs")?.GetValue(mat) is Array pairs)
                foreach (var pr in pairs) if (pr != null) { var g = PairGuid(pr); if (!GuidIsNull(g)) WalkGroundCandidates(TryLoadMaterial(g), depth + 1, visited, found); }
        }
        // DE-RISK PROBE (DistrictDebug): dump the SchematicView decal's output layer + atlas structure, so we know whether we
        // can clone it and inject our own silhouette texture at runtime (step 4 of the custom-footprint plan). Generic member
        // walk (fields+props, recursing into anything named *Atlas*) — reveals the backing Texture + the element/UV table.
        static bool schematicAtlasDumped;
        static void DumpSchematicAtlas(object sel, string name)
        {
            if (schematicAtlasDumped || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value || sel == null) return;
            try
            {
                var decals = new List<object>();
                CollectDecalMaterials(sel, decals, 0, new HashSet<object>());
                object schem = null;
                foreach (var d in decals) { var nm = GetMember(d, "name")?.ToString(); if (nm != null && nm.IndexOf("SchematicView", StringComparison.OrdinalIgnoreCase) >= 0) { schem = d; break; } }
                if (schem == null) { Plugin.Log.LogWarning("[SchematicAtlas] no SchematicView decal in our selector yet"); return; }
                var vo = GF(schem.GetType(), "visualOutput")?.GetValue(schem);
                var ol = vo != null ? GetMember(vo, "LoadedOutputLayer") : null;
                if (ol == null || (ol is UnityEngine.Object ou && ou == null)) { Plugin.Log.LogWarning("[SchematicAtlas] output layer not loaded yet — retry"); return; }
                schematicAtlasDumped = true;
                Plugin.Log.LogInfo($"[SchematicAtlas] decal='{GetMember(schem, "name")}' outputLayer={ol.GetType().FullName} '{GetMember(ol, "name")}'");
                DumpMembersShallow(ol, "  ", 0);
            }
            catch (Exception ex) { Plugin.Log.LogError("[SchematicAtlas] " + ex); }
        }
        static string DescribeVal(object v)
        {
            if (v == null) return "null";
            if (v is UnityEngine.Object uo && uo == null) return "null(destroyed)";
            if (v is UnityEngine.Texture tex) return $"Texture '{tex.name}' {tex.width}x{tex.height}";
            if (v is Array a) return $"{v.GetType().GetElementType()?.Name}[{a.Length}]";
            var t = v.GetType();
            if (t.IsPrimitive || v is string || t.IsEnum) return v.ToString();
            return t.Name;
        }
        static void DumpMembersShallow(object obj, string indent, int depth)
        {
            if (obj == null || depth > 2) return;
            var t = obj.GetType();
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object v = null; try { v = f.GetValue(obj); } catch { }
                Plugin.Log.LogInfo($"[SchematicAtlas] {indent}f {f.FieldType.Name} {f.Name} = {DescribeVal(v)}");
                if (v != null && f.Name.IndexOf("tlas", StringComparison.OrdinalIgnoreCase) >= 0 && depth < 2)
                {
                    if (v is Array arr) { for (int i = 0; i < Math.Min(arr.Length, 2); i++) DumpMembersShallow(arr.GetValue(i), indent + "    ", depth + 1); }
                    else DumpMembersShallow(v, indent + "    ", depth + 1);
                }
            }
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object v = null; try { v = p.GetValue(obj); } catch { }
                Plugin.Log.LogInfo($"[SchematicAtlas] {indent}p {p.PropertyType.Name} {p.Name} = {DescribeVal(v)}");
                if (v != null && p.Name.IndexOf("tlas", StringComparison.OrdinalIgnoreCase) >= 0 && depth < 2)
                {
                    if (v is Array arr) { for (int i = 0; i < Math.Min(arr.Length, 2); i++) DumpMembersShallow(arr.GetValue(i), indent + "    ", depth + 1); }
                    else DumpMembersShallow(v, indent + "    ", depth + 1);
                }
            }
        }
        // UNIQUE footprint (config DistrictFootprintMask): build a private 1-entry mask atlas from our silhouette PNG, clone the
        // SchematicView output layer to point its mask atlas (atlases[0]) at ours, and re-bind one SchematicView decal's
        // visualOutput + maskTexture at it — so the strategic footprint shows the district's OWN top-down shape. Per-step
        // logging so a failed reflection point is obvious. Runs once.
        static bool footprintMaskInjected; static UnityEngine.Texture2D reactorMaskTex;
        internal static void InjectReactorFootprint(object sel, string name)
        {
            var maskPath = Plugin.DistrictFootprintMask?.Value?.Trim();
            if (string.IsNullOrEmpty(maskPath) || footprintMaskInjected || sel == null) return;
            try
            {
                if (reactorMaskTex == null)
                {
                    if (!System.IO.File.Exists(maskPath)) { if (selectorTileLogged.Add(name + ":masknofile")) Plugin.Log.LogWarning($"[Footprint] mask PNG not found: {maskPath}"); return; }
                    reactorMaskTex = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, false);
                    var loadImg = AccessTools.TypeByName("UnityEngine.ImageConversion")?.GetMethod("LoadImage", new[] { typeof(UnityEngine.Texture2D), typeof(byte[]) });
                    bool ok = loadImg != null && (bool)loadImg.Invoke(null, new object[] { reactorMaskTex, System.IO.File.ReadAllBytes(maskPath) });
                    if (!ok) { Plugin.Log.LogWarning("[Footprint] LoadImage failed (ImageConversion missing?)"); reactorMaskTex = null; return; }
                    reactorMaskTex.name = "ReactorFootprintMask"; reactorMaskTex.wrapMode = UnityEngine.TextureWrapMode.Clamp;
                    Plugin.Log.LogInfo($"[Footprint] step1: loaded mask {reactorMaskTex.width}x{reactorMaskTex.height}");
                }
                // host SchematicView decal + its loaded output layer
                var decals = new List<object>();
                CollectDecalMaterials(sel, decals, 0, new HashSet<object>());
                object host = null;
                foreach (var d in decals) { var nm = GetMember(d, "name")?.ToString(); if (nm != null && nm.IndexOf("SchematicView", StringComparison.OrdinalIgnoreCase) >= 0) { host = d; break; } }
                if (host == null) { if (selectorTileLogged.Add(name + ":fpnohost")) Plugin.Log.LogWarning("[Footprint] no SchematicView decal to host our mask"); return; }
                var voField = GF(host.GetType(), "visualOutput");
                var voBox = voField?.GetValue(host);
                var ol = voBox != null ? GetMember(voBox, "LoadedOutputLayer") : null;
                if (ol == null || (ol is UnityEngine.Object olu && olu == null)) { if (selectorTileLogged.Add(name + ":fpnool")) Plugin.Log.LogWarning("[Footprint] host output layer not loaded yet — retry"); return; }

                // build our private mask atlas (FxTextureAtlas : GenericTextureAtlas<FxTextureAtlasStruct> : AbstractTextureAtlas)
                var atlasType = AccessTools.TypeByName("Amplitude.Graphics.Fx.FxTextureAtlas");
                var absType = AccessTools.TypeByName("Amplitude.Graphics.Atlas.AbstractTextureAtlas");
                var structType = AccessTools.TypeByName("Amplitude.Graphics.Fx.FxTextureAtlasStruct");
                var entryType = absType.GetNestedType("AtlasEntry");
                var outEntryType = absType.GetNestedType("OutputEntry");
                // GUID decides block-vs-shape. INVALID -> new Guid()->Null -> FillLayerData skips the mask -> solid quad
                // block (default). VALID hex (DistrictFootprintMaskCut=true) -> the mask cuts to the PNG's shape (e.g. circle).
                string maskGuidStr = (Plugin.DistrictFootprintMaskCut != null && Plugin.DistrictFootprintMaskCut.Value == "true")
                    ? "deadbeef000000000000000000000001" : "reactorfootprintmask000000000001";
                var ourAtlas = UnityEngine.ScriptableObject.CreateInstance(atlasType);
                ourAtlas.name = "ReactorFootprint_MaskAtlas";
                // atlasEntries[1]  (GUID -> Index 0)
                var entryArr = Array.CreateInstance(entryType, 1);
                var entry = Activator.CreateInstance(entryType);
                entryType.GetField("Guid").SetValue(entry, maskGuidStr); entryType.GetField("FullPath").SetValue(entry, ""); entryType.GetField("ShortPath").SetValue(entry, "ReactorFootprint"); entryType.GetField("Index").SetValue(entry, 0);
                entryArr.SetValue(entry, 0);
                AccessTools.Field(atlasType, "atlasEntries").SetValue(ourAtlas, entryArr);
                // elementData[1]  (Uvs = full texture)
                var edArr = Array.CreateInstance(structType, 1);
                var ed = Activator.CreateInstance(structType);
                structType.GetField("Uvs").SetValue(ed, new UnityEngine.Vector4(0f, 0f, 1f, 1f));
                edArr.SetValue(ed, 0);
                AccessTools.Field(atlasType, "elementData").SetValue(ourAtlas, edArr);
                // outputEntries[1]  (our silhouette texture)
                var outArr = Array.CreateInstance(outEntryType, 1);
                var outE = Activator.CreateInstance(outEntryType);
                outEntryType.GetField("Name").SetValue(outE, "ReactorFootprint");
                AccessTools.Field(outEntryType, "unityTextureRef").SetValue(outE, reactorMaskTex);
                outArr.SetValue(outE, 0);
                AccessTools.Field(atlasType, "outputEntries").SetValue(ourAtlas, outArr);
                AccessTools.Field(atlasType, "owner").SetValue(ourAtlas, "reactorfootprint");
                Plugin.Log.LogInfo("[Footprint] step2: built private mask atlas (1 entry, uv 0..1)");

                // clone the output layer; point its mask atlas array at ours (keep main 'atlas' for the fill)
                var olClone = ClonePrivateOutputLayer((UnityEngine.Object)ol);
                if (olClone == null) { if (selectorTileLogged.Add(name + ":fpnoclone")) Plugin.Log.LogWarning("[Footprint] output-layer clone failed"); return; }
                var atlasesArr = Array.CreateInstance(atlasType, 1); atlasesArr.SetValue(ourAtlas, 0);
                AccessTools.Field(olClone.GetType(), "atlases").SetValue(olClone, atlasesArr);
                Plugin.Log.LogInfo("[Footprint] step3: cloned output layer, mask atlas -> ours");

                // CLONE the SchematicView decal so we mutate a PRIVATE copy — modifying the SHARED game material leaked our
                // silhouette to EVERY district's footprint. Rebind + size on the clone; point our tile's item at it.
                // PRIVATE: clone the SchematicView decal so our silhouette + size stay on THIS district only (mutating the
                // shared decal leaked to every district's footprint).
                var hostClone = UnityEngine.Object.Instantiate((UnityEngine.Object)host);
                hostClone.name = "ReactorFootprint_Decal";
                var voT = voBox.GetType();
                var voBox2 = GF(hostClone.GetType(), "visualOutput").GetValue(hostClone);
                GF(voT, "loadedOutputLayer").SetValue(voBox2, olClone);
                GF(voT, "loadedOutputLayerGUID").SetValue(voBox2, GF(voT, "outputLayer").GetValue(voBox2));
                GF(hostClone.GetType(), "visualOutput").SetValue(hostClone, voBox2);
                var l0Field = GF(hostClone.GetType(), "layer0");
                var l0 = l0Field.GetValue(hostClone);
                var guidType = AccessTools.TypeByName("Amplitude.Framework.Guid");
                var ourGuid = Activator.CreateInstance(guidType, new object[] { maskGuidStr });
                GF(l0.GetType(), "maskTexture").SetValue(l0, ourGuid);
                var maskModeType = l0.GetType().GetField("maskOption", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType;
                if (maskModeType != null) GF(l0.GetType(), "maskOption").SetValue(l0, Enum.Parse(maskModeType, "Alpha"));
                l0Field.SetValue(hostClone, l0);
                float fpSize = 3.0f; float.TryParse(Plugin.DistrictFootprintMaskSize?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fpSize);
                if (fpSize <= 0f) fpSize = 3.0f;
                GF(hostClone.GetType(), "defaultSize")?.SetValue(hostClone, new UnityEngine.Vector3(1f, 0.25f, 1f));
                GF(hostClone.GetType(), "bboxOverride")?.SetValue(hostClone, new UnityEngine.Bounds(UnityEngine.Vector3.zero, new UnityEngine.Vector3(fpSize * 2f, fpSize * 2f, fpSize * 2f)));
                Plugin.Log.LogInfo($"[Footprint] step4: cloned decal + rebound private copy (size {fpSize})");

                // Instantiate did NOT copy the [NonSerialized] descriptor, so the clone has no resolved outputLayerIndex and
                // AddDataTo writes NO render data (-> a pixel). Resolve the clone's dependencies (descriptor + visualOutput)
                // THEN Load — same order the game's own load pipeline uses.
                if (distFxManager != null)
                {
                    if (fxNextDoublon == null) fxNextDoublon = GameBinding.FxEvolverMaterial?.GetMethod("NextDoublonAvoidanceIndex", BindingFlags.Static | BindingFlags.Public);
                    uint doublon = fxNextDoublon != null ? (uint)fxNextDoublon.Invoke(null, null) : 0u;
                    // Instantiate didn't copy the base [NonSerialized] evolverDescriptorInstance -> ResolveDependencies NREs.
                    // Copy it from the original (it's the shared descriptor singleton) so resolve/load succeed.
                    var ediF = AccessTools.Field(hostClone.GetType(), "evolverDescriptorInstance");
                    if (ediF != null) { ediF.SetValue(hostClone, ediF.GetValue(host)); Plugin.Log.LogInfo("[Footprint] copied evolverDescriptorInstance to clone"); }
                    var resolveM = hostClone.GetType().GetMethod("ResolveDependencies", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (resolveM != null && resolveM.GetParameters().Length == 2) { try { resolveM.Invoke(hostClone, new object[] { distFxManager, doublon }); } catch (Exception re) { Plugin.Log.LogWarning("[Footprint] clone Resolve INNER: " + (re.InnerException ?? re)); } }
                    var loadM = hostClone.GetType().GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (loadM != null && loadM.GetParameters().Length == 2) { try { loadM.Invoke(hostClone, new object[] { distFxManager, doublon }); } catch (Exception le) { Plugin.Log.LogWarning("[Footprint] clone Load INNER: " + (le.InnerException ?? le)); } }
                }

                // make our clone THE footprint: keep building item(s) + ONE decal item repointed at our clone (centred), drop the rest
                var itemsF2 = GF(sel.GetType(), "levelBuildItems");
                if (itemsF2?.GetValue(sel) is Array allItems)
                {
                    var guidNull = guidType.GetField("Null", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                    var keep = new List<object>(); int dropped = 0; bool placed = false;
                    foreach (var it in allItems)
                    {
                        if (it == null) continue;
                        var child = GF(it.GetType(), "loadedEvolverMaterial")?.GetValue(it);
                        if (child == null) { keep.Add(it); continue; }
                        if (GF(child.GetType(), "fxMesh") != null) { keep.Add(it); continue; }   // building element
                        if (!placed && child.GetType().Name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var itBox = it;
                            GF(it.GetType(), "loadedEvolverMaterial")?.SetValue(itBox, hostClone);          // point at our private clone
                            var egF = GF(it.GetType(), "EvolverMaterialGuid"); if (egF != null && guidNull != null) egF.SetValue(itBox, guidNull);   // don't reload the shared decal
                            var lgF = GF(it.GetType(), "loadedEvolverMaterialGuid"); if (lgF != null && guidNull != null) lgF.SetValue(itBox, guidNull);   // stop the emit reloading the ORIGINAL over our clone
                            var pf = GF(it.GetType(), "Position"); if (pf != null) pf.SetValue(itBox, UnityEngine.Vector3.zero);
                            var lsF = GF(it.GetType(), "LocalScale"); if (lsF != null) lsF.SetValue(itBox, new UnityEngine.Vector3(fpSize, 1f, fpSize));   // was 0.04 = shrink; drive size here (per-item, scoped)
                            // ROTATE: spin the decal's orientation (AxeY up, AxeZ forward) by DistrictFootprintMaskRotation° clockwise about vertical
                            float fpRot = 0f; float.TryParse(Plugin.DistrictFootprintMaskRotation?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fpRot);
                            if (fpRot != 0f)
                            {
                                var q = UnityEngine.Quaternion.AngleAxis(fpRot, UnityEngine.Vector3.up);
                                var azF = GF(it.GetType(), "AxeZ"); if (azF != null) azF.SetValue(itBox, q * new UnityEngine.Vector3(0f, 0f, 1f));
                                var ayF = GF(it.GetType(), "AxeY"); if (ayF != null) ayF.SetValue(itBox, UnityEngine.Vector3.up);
                                Plugin.Log.LogInfo($"[Footprint] rotated footprint {fpRot}° (AxeZ -> {q * new UnityEngine.Vector3(0f, 0f, 1f)})");
                            }
                            Plugin.Log.LogInfo($"[Footprint] item set: LocalScale={lsF?.GetValue(itBox)}, loadedGuid nulled, material={GetMember(GF(it.GetType(), "loadedEvolverMaterial")?.GetValue(itBox), "name")}");
                            keep.Add(itBox); placed = true;
                        }
                        else dropped++;
                    }
                    var elemType = allItems.GetType().GetElementType();
                    var arr = Array.CreateInstance(elemType, keep.Count);
                    for (int i = 0; i < keep.Count; i++) arr.SetValue(keep[i], i);
                    itemsF2.SetValue(sel, arr);
                    Plugin.Log.LogInfo($"[Footprint] step5: silhouette is THE footprint (placed={placed}, dropped {dropped}, private clone — other districts untouched).");
                }
                footprintMaskInjected = true;
                LoadFxMaterial(sel);
                Plugin.Log.LogInfo("[Footprint] done — zoom out to see the reactor silhouette footprint.");
            }
            catch (Exception ex) { if (selectorTileLogged.Add(name + ":fpex")) Plugin.Log.LogError("[Footprint] " + ex); }
        }
        // MESH footprint (config DistrictFootprintMesh): keep the district's own 3D building mesh rendering at strategic zoom
        // instead of demoting to a flat decal. The fade is a per-element GPU gate: FxEvolverMaterialLevelBuildElement carries a
        // RenderFeatureSelector whose SelectionFlags0 bitmask decides which camera zoom-bands ("render features") draw it. The
        // reactor's building elements carry a close-band-only mask, so they vanish at strategic zoom. SelectionFlags0 == 0 means
        // AlwaysEnabled (RenderFeatureFlags.AlwaysEnabled) -> the SAME geometry renders in every band, strategic included. The
        // value only reaches the GPU via WriteToGPUData, so we nudge OnEditionChange() then re-emit the selector (LoadFxMaterial).
        // Scoped + safe: the reactor's element is its own custom asset (unique to this district); AlwaysEnabled only ADDS the
        // strategic band, close zoom is unchanged. No mesh re-bake, no LOD change.
        [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> meshPersistLogged = new HashSet<string>();
        internal static void KeepDistrictMeshAtStrategicZoom(object sel, string name)
        {
            ResolveScopedFootprint(name);   // per-entry registry values, or the global config fallback — always resolve so the B&W/flat pollers have them
            if (!fpMesh || sel == null) return;
            if (!meshPersistLogged.Add(name)) return;   // once per district
            try
            {
                scopedFlatSel = sel;   // remember our selector so UpdateMeshFlatness can lift its mesh items when flat
                var elements = new List<object>();
                CollectMeshElements(sel, elements, 0, new HashSet<object>());
                int changed = 0;
                foreach (var el in elements)
                {
                    var rfsF = GF(el.GetType(), "renderFeatureSelector");
                    if (rfsF == null) continue;
                    var rfs = rfsF.GetValue(el);                              // boxed RenderFeatureSelector struct
                    if (rfs == null) continue;
                    var flagsF = rfs.GetType().GetField("SelectionFlags0");
                    if (flagsF == null) continue;
                    uint cur = (uint)flagsF.GetValue(rfs);
                    if (cur == 0u) { continue; }                             // already AlwaysEnabled
                    flagsF.SetValue(rfs, 0u);                                 // 0 = AlwaysEnabled -> render in EVERY zoom band
                    rfsF.SetValue(el, rfs);                                   // write the struct back onto the element
                    InvokeNoArg(el, "OnEditionChange");                       // game's "field changed, rebuild GPU data" signal
                    changed++;
                    Plugin.Log.LogInfo($"[FootprintMesh] '{name}': element '{GetMember(el, "name")}' SelectionFlags0 {cur} -> 0 (AlwaysEnabled)");
                }
                bool dirty = changed > 0;
                // The mesh is now the footprint, so drop the template's baked footprint DECAL item(s) — the inherited
                // donor outline (e.g. the MissileSilo silhouette) that otherwise shows THROUGH/beneath our flat mesh.
                if (fpHideDecal)
                {
                    var itemsF = GF(sel.GetType(), "levelBuildItems");
                    if (itemsF?.GetValue(sel) is Array allItems)
                    {
                        var keep = new List<object>(); int dropped = 0;
                        foreach (var it in allItems)
                        {
                            if (it == null) continue;
                            var child = GF(it.GetType(), "loadedEvolverMaterial")?.GetValue(it) ?? TryLoadMaterial(GF(it.GetType(), "EvolverMaterialGuid")?.GetValue(it));
                            if (child != null && child.GetType().Name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0) { dropped++; continue; }   // drop footprint decals
                            keep.Add(it);
                        }
                        if (dropped > 0)
                        {
                            var elemType = allItems.GetType().GetElementType();
                            var arr = Array.CreateInstance(elemType, keep.Count);
                            for (int i = 0; i < keep.Count; i++) arr.SetValue(keep[i], i);
                            itemsF.SetValue(sel, arr);
                            dirty = true;
                            Plugin.Log.LogInfo($"[FootprintMesh] '{name}': dropped {dropped} template footprint decal item(s) — the mesh is the footprint now.");
                        }
                    }
                }
                if (dirty) LoadFxMaterial(sel);                               // re-emit so WriteToGPUData pushes the new selector + dropped items
                if (meshPersistLogged.Add(name + ":done")) Plugin.Log.LogInfo($"[FootprintMesh] '{name}': {changed} building element(s) now render at strategic zoom ({elements.Count} element(s) scanned).");
            }
            catch (Exception ex) { if (meshPersistLogged.Add(name + ":ex")) Plugin.Log.LogWarning("[FootprintMesh] " + ex); }
        }
        // Recursively gather every FxEvolverMaterialLevelBuildElement (mesh-bearing leaf) under a selector. Mirrors
        // CollectDecalMaterials' traversal (levelBuildItems + cache entries + pairs).
        static void CollectMeshElements(object mat, List<object> outEls, int depth, HashSet<object> visited)
        {
            if (mat == null || depth > 10 || !visited.Add(mat)) return;
            var t = mat.GetType();
            if (GF(t, "renderFeatureSelector") != null && GF(t, "fxMesh") != null) { outEls.Add(mat); return; }
            if (GF(t, "levelBuildItems")?.GetValue(mat) is Array items)
                foreach (var it in items) if (it != null)
                    CollectMeshElements(GF(it.GetType(), "loadedEvolverMaterial")?.GetValue(it) ?? TryLoadMaterial(GF(it.GetType(), "EvolverMaterialGuid")?.GetValue(it)), outEls, depth + 1, visited);
            var cache = GF(t, "fxMaterialCacheEntries")?.GetValue(mat);
            if (cache != null && AccessTools.Field(cache.GetType(), "Entries")?.GetValue(cache) is Array entries)
                foreach (var e in entries) if (e != null) CollectMeshElements(AccessTools.Field(e.GetType(), "FxMaterial")?.GetValue(e), outEls, depth + 1, visited);
            if (GF(t, "pairs")?.GetValue(mat) is Array pairs)
                foreach (var pr in pairs) if (pr != null) { var g = PairGuid(pr); if (!GuidIsNull(g)) CollectMeshElements(TryLoadMaterial(g), outEls, depth + 1, visited); }
        }
        // Invoke a 0-arg method by name anywhere in the type hierarchy (public or non-public); silent no-op if absent.
        static void InvokeNoArg(object obj, string method)
        {
            for (var ty = obj?.GetType(); ty != null; ty = ty.BaseType)
            {
                var m = ty.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m != null) { try { m.Invoke(obj, null); } catch { } return; }
            }
        }
        static void CollectDecalMaterials(object mat, List<object> outDecals, int depth, HashSet<object> visited)
        {
            if (mat == null || depth > 10 || !visited.Add(mat)) return;
            var t = mat.GetType();
            if (t.Name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0) { outDecals.Add(mat); return; }
            if (GF(t, "levelBuildItems")?.GetValue(mat) is Array items)
                foreach (var it in items) if (it != null)
                    CollectDecalMaterials(GF(it.GetType(), "loadedEvolverMaterial")?.GetValue(it) ?? TryLoadMaterial(GF(it.GetType(), "EvolverMaterialGuid")?.GetValue(it)), outDecals, depth + 1, visited);
            var cache = GF(t, "fxMaterialCacheEntries")?.GetValue(mat);
            if (cache != null && AccessTools.Field(cache.GetType(), "Entries")?.GetValue(cache) is Array entries)
                foreach (var e in entries) if (e != null) CollectDecalMaterials(AccessTools.Field(e.GetType(), "FxMaterial")?.GetValue(e), outDecals, depth + 1, visited);
            if (GF(t, "pairs")?.GetValue(mat) is Array pairs)
                foreach (var pr in pairs) if (pr != null) { var g = PairGuid(pr); if (!GuidIsNull(g)) CollectDecalMaterials(TryLoadMaterial(g), outDecals, depth + 1, visited); }
        }

        // Resolve a district's main level-build channel index (static field mainLevelBuildComponantLayer; the shared
        // mainLayerCached is only set on the injection path, so compute it here for the scoped path too).
        static int ResolveMainLayer(object district)
        {
            if (mainLayerCached >= 0) return mainLayerCached;
            var lf = district.GetType().GetField("mainLevelBuildComponantLayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            mainLayerCached = lf?.GetValue(null) is int li ? li : 0;
            return mainLayerCached;
        }

        // GROUND MATERIAL under a custom district (the "maintained grass field"): vanilla resolves a
        // GroundMaterialDefinition from (Biome × ConstructibleVisualAffinity) and calls ApplyGroundMaterialDefinition
        // (index into criteria 24). Our wonder's affinity has no row for this biome → index 0 → bare sand. This
        // postfix forces a chosen ground-material index for our registry districts — the game's own terrain paint,
        // blended, not a flat mesh. Also dumps the ground-material vocabulary once (DistrictDebug) so a name can be picked.
        static bool groundNamesDumped; static int groundApplyCount; [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> groundLogged = new HashSet<string>();
        [ProcessLived("GroundMaterialDefinition vocabulary from the game database - stable for the process")] static string[] groundNames;   // GroundMaterialDefinition vocabulary, indexed (from the criteria-24 dump)
        static string GroundNameForIndex(int idx) => (groundNames != null && idx >= 0 && idx < groundNames.Length) ? groundNames[idx] : ("idx" + idx);

        // GROUND PROBE (DistrictDebug): log what ground index each district hands to ApplyGroundMaterialDefinition — the
        // NATIVE resolve for non-registry districts (e.g. what a normal Industry tile uses = the "deadzone" we want to
        // match), and our override for registry ones. Answers "which GroundMaterialDefinition is the Industry cleared look".
        [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> groundProbeLogged = new HashSet<string>();
        internal static void GroundApplyProbe(object district, object idxObj)
        {
            if (Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value || groundProbeLogged.Count > 120) return;
            try
            {
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString();
                if (string.IsNullOrEmpty(name)) return;
                int idx = idxObj is int i ? i : -1;
                if (groundProbeLogged.Add(name + "=" + idx))
                    Plugin.Log.LogInfo($"[GroundProbe] '{name}' -> ApplyGroundMaterialDefinition(idx={idx}) = '{GroundNameForIndex(idx)}'");
            }
            catch { }
        }
        internal static void DistrictApplyGroundMaterial(object district)
        {
            try
            {
                EnsureDistrictConfig();
                // NOT gated by distOn (DistrictRepoint) any more: this per-district registry setting must apply for the
                // SCOPED path (DistrictSelectorTile, DistrictRepoint=false) too — otherwise the preview honors the registry
                // but in-game the district sits on bare terrain. The entry==null guard keeps it to our registered districts.
                if (distModels.Count == 0) return;
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
                if (!groundNamesDumped)
                {
                    groundNamesDumped = true;
                    var namesM = repoType.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "Names" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
                    if (namesM?.Invoke(inst, new object[] { GroundCriteria }) is Array arr)
                    {
                        var list = new List<string>(); foreach (var s in arr) list.Add(s?.ToString());
                        groundNames = list.ToArray();   // index -> name, so the ground probe can name what native tiles resolve to
                        if (Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value)
                            Plugin.Log.LogInfo($"[Ground] GroundMaterialDefinition names ({list.Count}): {string.Join(", ", list)}");
                        DumpGroundColors(list);   // write haf_ground_colors.json for the editor preview (each material's true tint)
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
                // APPLY-ONCE: the prefix (GroundApplyOverride) now rewrites every ApplyGroundMaterialDefinition call to our
                // index, so it HOLDS without this postfix re-applying. Re-applying each UpdateGroundMaterial just restarts the
                // terrain blend => the slight twitch. So apply once for the initial set, then leave it to the prefix.
                if (apply != null && apply.GetParameters().Length == 1 && !entry.groundApplied)
                {
                    apply.Invoke(district, new object[] { entry.groundIdx });
                    entry.groundApplied = true;
                    // DIAGNOSTIC (DistrictDebug): log the first ~90 applies with frame numbers — a BURST of consecutive
                    // frames = we're re-applying every frame (oscillation); sparse/one = it's settling. Tells us the real cause.
                    if (Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value && groundApplyCount < 90)
                    { groundApplyCount++; Plugin.Log.LogInfo($"[Ground] '{name}': applied '{want}' idx={entry.groundIdx} @ frame {UnityEngine.Time.frameCount} (call #{groundApplyCount})"); }
                    if (groundLogged.Add(name)) Plugin.Diag($"[Ground] '{name}': forced ground material '{want}' (index {entry.groundIdx}) — maintained field under the district.");
                }
            }
            catch (Exception ex) { if (groundLogged.Add("ex")) Plugin.Log.LogError("[Ground] " + ex); }
        }

        // PREFIX companion to DistrictApplyGroundMaterial's postfix. The postfix sets our ground AFTER UpdateGroundMaterial,
        // but the game also calls ApplyGroundMaterialDefinition DIRECTLY (a DEPOSIT tile like the reactor re-resolves to its
        // natural terrain) with no postfix to follow — so we get reverted to rock. Rewriting the index in the PREFIX makes
        // EVERY caller land on our material: the paint holds with no per-frame re-assert and no blend twitch. Uses the index
        // the postfix already resolved+cached (entry.groundIdx); until that first resolve we pass through (postfix applies once).
        [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> groundOverrideLogged = new HashSet<string>();
        // Returns TRUE to let the original ApplyGroundMaterialDefinition run, FALSE to SKIP it. For our districts: rewrite the
        // index to our paint the FIRST time (let it run to set it), then on every subsequent call SKIP — because the game
        // re-calls this every frame on a deposit tile, and each real call restarts the terrain blend (the twitch). Once our
        // paint is set (groundApplied, flipped by the postfix's one apply) we drop the redundant calls so the blend settles.
        internal static bool GroundApplyOverride(object district, ref int idx)
        {
            try
            {
                if (distModels.Count == 0) return true;
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString();
                if (string.IsNullOrEmpty(name)) return true;
                foreach (var e in distModels)
                    if (e.district == name)
                    {
                        if (string.IsNullOrEmpty(e.groundMaterial) || e.groundIdx <= 0) return true;   // no override yet (unresolved/none)
                        if (e.groundApplied)
                        {
                            if (Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value && groundOverrideLogged.Add(name + ":skip"))
                                Plugin.Log.LogInfo($"[Ground] '{name}': holding idx {e.groundIdx} — skipping the game's redundant ApplyGroundMaterialDefinition calls (no blend restart / twitch).");
                            return false;   // already holding our paint — skip so the blend doesn't restart
                        }
                        if (idx != e.groundIdx)
                        {
                            if (Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value && groundOverrideLogged.Add(name))
                                Plugin.Log.LogInfo($"[Ground] '{name}': prefix set idx {idx} -> {e.groundIdx} ('{e.groundMaterial}'); further calls will be skipped so it holds without twitch.");
                            idx = e.groundIdx;
                        }
                        return true;   // let this one run to actually set our material
                    }
            }
            catch { }
            return true;
        }

        // Dump each GroundMaterialDefinition's representative Color (from its GroundMaterialAuthoringData) to
        // BepInEx/config/haf_ground_colors.json — the District Factory tints its preview tile with the TRUE per-
        // material colour instead of a guessed family colour. Chain: Databases<GroundMaterialDefinition>.GetValue(name)
        // -> .GroundMaterialAuthoringData (Guid) -> AssetDatabase.TryLoadAsset<GroundMaterialAuthoringData> -> .Color.
        static bool groundColorsDumped;
        static void DumpGroundColors(List<string> names)
        {
            if (groundColorsDumped) return; groundColorsDumped = true;
            try
            {
                var gmdType = AccessTools.TypeByName("Amplitude.Mercury.Terrain.GroundMaterialDefinition");
                var gmadType = AccessTools.TypeByName("Amplitude.Mercury.Terrain.GroundMaterialAuthoringData");
                var dbType = AccessTools.TypeByName("Amplitude.Framework.Databases");
                if (gmdType == null || gmadType == null || dbType == null) { Plugin.Log.LogWarning($"[Ground] color dump: type(s) not found (def={gmdType != null}, auth={gmadType != null}, db={dbType != null})"); return; }
                // non-generic GetDatabase(Type) — avoids the generic overload's optional-bool-param signature mismatch
                var getDb = dbType.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "GetDatabase" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Type));
                var db = getDb?.Invoke(null, new object[] { gmdType });
                if (db == null) { Plugin.Log.LogWarning("[Ground] color dump: GroundMaterialDefinition database null"); return; }
                var getVal = db.GetType().GetMethods().FirstOrDefault(m => m.Name == "GetValue" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
                          ?? db.GetType().GetMethods().FirstOrDefault(m => m.Name == "GetValue" && m.GetParameters().Length == 1);
                var ssType = AccessTools.TypeByName("Amplitude.StaticString");
                var authGuidP = AccessTools.Property(gmdType, "GroundMaterialAuthoringData");
                var colorP = AccessTools.Property(gmadType, "Color");
                var texType = AccessTools.TypeByName("Amplitude.Mercury.Terrain.GroundMaterialAuthoringData+GroundMaterialTextureData") ?? AccessTools.TypeByName("Amplitude.Mercury.Terrain.GroundMaterialTextureData");
                var oneLayerP = AccessTools.Property(gmadType, "GroundMaterialOneLayer");
                var layer0P = AccessTools.Property(gmadType, "GroundMaterialLayer0");
                var atlasElemF = texType?.GetField("AtlasElement", BindingFlags.Public | BindingFlags.Instance);
                var atlasF = texType?.GetField("Atlas", BindingFlags.Public | BindingFlags.Instance);
                var defAtlasType = AccessTools.TypeByName("Amplitude.Graphics.Atlas.DefaultTextureAtlas");
                var texDir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "haf_ground_tex");
                System.IO.Directory.CreateDirectory(texDir);

                var sb = new System.Text.StringBuilder("{\n");
                int n = 0, tn = 0;
                var inv = System.Globalization.CultureInfo.InvariantCulture;   // dot decimals — the system locale (e.g. nl-NL) writes commas, breaking the JSON
                foreach (var name in names)
                {
                    if (string.IsNullOrEmpty(name) || name == "None") continue;
                    object key = getVal.GetParameters()[0].ParameterType == typeof(string) ? (object)name : Activator.CreateInstance(ssType, name);
                    var def = getVal.Invoke(db, new[] { key });
                    if (def == null) continue;
                    var guid = authGuidP?.GetValue(def);
                    if (guid == null) continue;
                    var auth = LoadAmpliAsset(gmadType, guid);   // the proven 1-arg Amplitude asset loader
                    if (auth == null) continue;

                    // COLOUR (fallback tint)
                    if (colorP?.GetValue(auth) is UnityEngine.Color c)
                    {
                        if (n++ > 0) sb.Append(",\n");
                        sb.Append($"  \"{name}\": [{c.r.ToString("0.###", inv)}, {c.g.ToString("0.###", inv)}, {c.b.ToString("0.###", inv)}]");
                    }

                    // TEXTURE — the actual ground image is a TILE inside a shared DefaultTextureAtlas. Chain: pick a
                    // layer (oneLayer, else layer0) with a non-null Atlas + AtlasElement; load the atlas; GUIDToIndex
                    // (AtlasElement) -> tile index; GetElementData(index) -> the tile's UV rect (Vector4); grab the
                    // atlas page from OutputEntries[0].GetTexture; blit-crop that UV region -> the material's tile.
                    bool diag = n <= 4 && Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value;
                    if (atlasElemF != null && atlasF != null && defAtlasType != null)
                    {
                        object elemGuid = null, atlasGuid = null; string via = "";
                        foreach (var (lp, ln) in new[] { (oneLayerP, "oneLayer"), (layer0P, "layer0") })
                        {
                            var td = lp?.GetValue(auth); if (td == null) continue;
                            var eg = atlasElemF.GetValue(td); var ag = atlasF.GetValue(td);
                            if (eg != null && !GuidIsNull4(eg) && ag != null && !GuidIsNull4(ag)) { elemGuid = eg; atlasGuid = ag; via = ln; break; }
                        }
                        if (elemGuid != null)
                        {
                            var atlas = LoadAmpliAsset(defAtlasType, atlasGuid);
                            var agt = atlasGuid.GetType();
                            if (diag) Plugin.Log.LogInfo($"[GroundTex] '{name}' via {via}: atlasGuid={agt.GetField("a", BF)?.GetValue(atlasGuid)},{agt.GetField("d", BF)?.GetValue(atlasGuid)} atlas={(atlas == null ? "NULL" : atlas.GetType().Name)}");
                            if (atlas != null)
                            {
                                var g2i = defAtlasType.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "GUIDToIndex" && m.GetParameters().Length == 1);
                                int idx = g2i != null ? (int)g2i.Invoke(atlas, new[] { elemGuid }) : -1;
                                var getElem = atlas.GetType().GetMethod("GetElementData", BindingFlags.Public | BindingFlags.Instance);
                                var outsP = AccessTools.Property(atlas.GetType(), "OutputEntries");
                                var outs = outsP?.GetValue(atlas) as Array;
                                if (diag) Plugin.Log.LogInfo($"[GroundTex]   index={idx} outputEntries={(outs?.Length ?? -1)}");
                                if (idx >= 0 && getElem != null && outs != null && outs.Length > 0)
                                {
                                    var uv = getElem.Invoke(atlas, new object[] { idx });   // Vector4 rect (offset.xy, scale.zw)
                                    var getTex = outs.GetValue(0).GetType().GetMethod("GetTexture", BindingFlags.Public | BindingFlags.Instance);
                                    var page = getTex?.Invoke(outs.GetValue(0), new object[] { (uint)256 }) as UnityEngine.Texture2D;
                                    if (diag) Plugin.Log.LogInfo($"[GroundTex]   uv={uv} page={(page == null ? "NULL" : page.width + "x" + page.height)}");
                                    if (page != null && uv is UnityEngine.Vector4 r)
                                    {
                                        var png = CropAtlasTile(page, r);
                                        if (png != null) { System.IO.File.WriteAllBytes(System.IO.Path.Combine(texDir, name + ".png"), png); tn++; }
                                    }
                                }
                            }
                        }
                    }
                }
                sb.Append("\n}\n");
                System.IO.File.WriteAllText(System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "haf_ground_colors.json"), sb.ToString());
                Plugin.Log.LogInfo($"[Ground] wrote {n} colour(s) + {tn} texture PNG(s) -> haf_ground_colors.json + haf_ground_tex/ (editor preview)");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Ground] color dump: " + ex.Message); }
        }

        // HEXAGON SCULPTING under a custom wonder (the raised platform + strategic-zoom footprint). Vanilla resolves
        // a HexagonSculptingDefinition from the ArtificialWonder database and calls ApplyHexagonSculptingDefinition;
        // our wonder's cell is empty -> index -1 -> flat terrain, no footprint. This postfix forces a chosen index.
        // Mirrors DistrictApplyGroundMaterial. Criteria 27 = HexagonSculptingDefinitionCriteriaIndex.
        static bool hexNamesDumped; [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> hexLogged = new HashSet<string>();
        [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> hexNativeLogged = new HashSet<string>();
        // Diagnostic (DistrictDebug): log the hexagon-sculpting shape each district NATIVELY resolves to — so a modder
        // can read which EmblematicAndCityCenter* a real district/city-center uses and copy it to a custom wonder.
        internal static void DumpNativeHexSculpt(object district)
        {
            try
            {
                if (Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString();
                if (string.IsNullOrEmpty(name) || !hexNativeLogged.Add(name)) return;
                var idxObj = AccessTools.Field(district.GetType(), "hexagonSculptingDefinitionIndex")?.GetValue(district);
                if (!(idxObj is int idx)) return;
                string shape = "?";
                var repoType = AccessTools.TypeByName("Amplitude.Mercury.Data.Presentation.AssetReferenceRepository");
                var inst = repoType?.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.Invoke(null, null);
                var namesM = repoType?.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "Names" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
                if (inst != null && namesM?.Invoke(inst, new object[] { 27 }) is Array arr && idx >= 0 && idx < arr.Length) shape = arr.GetValue(idx)?.ToString();
                Plugin.Log.LogInfo($"[HexSculpt] NATIVE '{name}' -> index {idx} = '{shape}'");
            }
            catch { }
        }
        [SessionScoped(Scope = SessionScope.District)] static readonly List<object> hexDistricts = new List<object>();   // districts we've sculpted — re-applied by the live dial
        static string lastHexDial; static int hexDialTick;

        // LIVE dial: edit BepInEx/config/haf_hexsculpt.txt with a HexagonSculptingDefinition name and every sculpted
        // district re-carves to it WITHOUT a relaunch — cycle the ~40 shapes in seconds to find the right footprint,
        // then set the winner in the Factory's Footprint field to ship it. (Mirrors the turnease/hugterrain dials.)
        internal static void PollHexSculptDial()
        {
            if (++hexDialTick % 20 != 1) return;   // ~3x/second
            try
            {
                var path = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "haf_hexsculpt.txt");
                if (!System.IO.File.Exists(path)) return;
                var want = System.IO.File.ReadAllText(path).Trim();
                if (want == lastHexDial) return;
                lastHexDial = want;
                if (string.IsNullOrEmpty(want)) return;

                var repoType = AccessTools.TypeByName("Amplitude.Mercury.Data.Presentation.AssetReferenceRepository");
                var inst = repoType?.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.Invoke(null, null);
                if (inst == null || !(AccessTools.Property(inst.GetType(), "Loaded")?.GetValue(inst) is bool ld) || !ld) return;
                var ssType = AccessTools.TypeByName("Amplitude.StaticString");
                var idxM = repoType.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "IndexOf" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                if (idxM == null || ssType == null) return;
                int idx = (int)idxM.Invoke(inst, new object[] { 27, Activator.CreateInstance(ssType, want) });
                if (idx <= 0) { Plugin.Log.LogWarning($"[HexSculpt] dial '{want}' not found in the vocabulary (index {idx})."); return; }

                int applied = 0;
                for (int i = hexDistricts.Count - 1; i >= 0; i--)
                {
                    var d = hexDistricts[i];
                    if (d is UnityEngine.Object uo && uo == null) { hexDistricts.RemoveAt(i); continue; }   // razed
                    var apply = d.GetType().GetMethod("ApplyHexagonSculptingDefinition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    apply?.Invoke(d, new object[] { idx }); applied++;
                }
                Plugin.Log.LogInfo($"[HexSculpt] dial -> '{want}' (index {idx}) live-applied to {applied} district(s).");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[HexSculpt] dial: " + ex.Message); }
        }
        internal static void DistrictApplyHexSculpt(object district)
        {
            try
            {
                EnsureDistrictConfig();
                // NOT gated by distOn (DistrictRepoint) any more: this per-district registry setting must apply for the
                // SCOPED path (DistrictSelectorTile, DistrictRepoint=false) too — otherwise the preview honors the registry
                // but in-game the district sits on bare terrain. The entry==null guard keeps it to our registered districts.
                if (distModels.Count == 0) return;
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString();
                if (string.IsNullOrEmpty(name)) return;
                DistrictModel entry = null; foreach (var e in distModels) if (e.district == name) { entry = e; break; }
                if (entry == null) return;

                var repoType = AccessTools.TypeByName("Amplitude.Mercury.Data.Presentation.AssetReferenceRepository");
                var inst = repoType?.GetMethod("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.Invoke(null, null);
                if (inst == null) return;
                if (!(AccessTools.Property(inst.GetType(), "Loaded")?.GetValue(inst) is bool ld) || !ld) return;

                const int HexCriteria = 27;
                if (!hexNamesDumped && Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value)
                {
                    hexNamesDumped = true;
                    var namesM = repoType.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "Names" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
                    if (namesM?.Invoke(inst, new object[] { HexCriteria }) is Array arr)
                    {
                        var list = new List<string>(); foreach (var s in arr) list.Add(s?.ToString());
                        Plugin.Log.LogInfo($"[HexSculpt] HexagonSculptingDefinition names ({list.Count}): {string.Join(", ", list)}");
                    }
                }

                var want = !string.IsNullOrEmpty(entry.hexSculpt) ? entry.hexSculpt : Plugin.DistrictHexSculpt?.Value?.Trim();
                if (string.IsNullOrEmpty(want)) return;

                if (entry.hexIdx == int.MinValue)
                {
                    var ssType = AccessTools.TypeByName("Amplitude.StaticString");
                    var idxM = repoType.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "IndexOf" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                    if (idxM == null || ssType == null) { entry.hexIdx = -1; return; }
                    entry.hexIdx = (int)idxM.Invoke(inst, new object[] { HexCriteria, Activator.CreateInstance(ssType, want) });
                    if (entry.hexIdx <= 0) Plugin.Log.LogWarning($"[HexSculpt] '{want}' not in the HexagonSculptingDefinition vocabulary (index {entry.hexIdx}) — set DistrictDebug=true to log valid names.");
                }
                if (entry.hexIdx <= 0) return;

                var apply = district.GetType().GetMethod("ApplyHexagonSculptingDefinition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (apply != null && apply.GetParameters().Length == 1)
                {
                    apply.Invoke(district, new object[] { entry.hexIdx });
                    if (!hexDistricts.Contains(district)) hexDistricts.Add(district);   // remember it for the live dial
                    if (hexLogged.Add(name)) Plugin.Diag($"[HexSculpt] '{name}': forced hexagon sculpting '{want}' (index {entry.hexIdx}) — raised platform + strategic footprint.");
                }
            }
            catch (Exception ex) { if (hexLogged.Add("ex")) Plugin.Log.LogError("[HexSculpt] " + ex); }
        }

        static bool GuidIsNull4(object g)
        { var t = g.GetType(); return (int)(t.GetField("a", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("b", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("c", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("d", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g) ?? 0) == 0; }

        // Crop one atlas tile (its UV rect within the shared page) to a readable 256² PNG. The rect is a Vector4
        // (offsetU, offsetV, scaleU, scaleV); Graphics.Blit with a scale/offset samples exactly that sub-region.
        static byte[] CropAtlasTile(UnityEngine.Texture2D page, UnityEngine.Vector4 uv)
        {
            // the Vector4 is a MIN/MAX UV rect (minU, minV, maxU, maxV) — scale = extent, offset = min. (The
            // first pass read it as offset/scale, so V sampled past 1.0 and wrapped: black + several tiles.)
            var scale = new UnityEngine.Vector2(uv.z - uv.x, uv.w - uv.y);
            var offset = new UnityEngine.Vector2(uv.x, uv.y);
            if (scale.x <= 0f || scale.y <= 0f) { scale = new UnityEngine.Vector2(1, 1); offset = UnityEngine.Vector2.zero; }   // degenerate rect -> whole page
            int sz = 256;
            // try/finally (the fourth readback site, 2026-08-21 — the 08-21 sweep hardened the other three): a throw in
            // Blit / ReadPixels / Apply must NOT leave RenderTexture.active pointing at our temp RT (corrupts the next
            // draw) nor leak the RT + Texture2D. Restore + release + destroy run whatever happens.
            UnityEngine.RenderTexture rt = null, prev = null; UnityEngine.Texture2D t = null;
            try
            {
                rt = UnityEngine.RenderTexture.GetTemporary(sz, sz, 0, UnityEngine.RenderTextureFormat.ARGB32, UnityEngine.RenderTextureReadWrite.sRGB);
                prev = UnityEngine.RenderTexture.active;
                UnityEngine.Graphics.Blit(page, rt, scale, offset);
                UnityEngine.RenderTexture.active = rt;
                t = new UnityEngine.Texture2D(sz, sz, UnityEngine.TextureFormat.RGBA32, false);
                t.ReadPixels(new UnityEngine.Rect(0, 0, sz, sz), 0, 0); t.Apply();
                return UnityEngine.ImageConversion.EncodeToPNG(t);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[GroundTex] crop: " + ex.Message); return null; }
            finally
            {
                if (rt != null) { UnityEngine.RenderTexture.active = prev; UnityEngine.RenderTexture.ReleaseTemporary(rt); }
                if (t != null) UnityEngine.Object.Destroy(t);
            }
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
