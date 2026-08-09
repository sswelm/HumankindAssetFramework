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
                if (!groundNamesDumped)
                {
                    groundNamesDumped = true;
                    var namesM = repoType.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "Names" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
                    if (namesM?.Invoke(inst, new object[] { GroundCriteria }) is Array arr)
                    {
                        var list = new List<string>(); foreach (var s in arr) list.Add(s?.ToString());
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
                if (apply != null && apply.GetParameters().Length == 1)
                {
                    apply.Invoke(district, new object[] { entry.groundIdx });
                    if (groundLogged.Add(name)) Plugin.Diag($"[Ground] '{name}': forced ground material '{want}' (index {entry.groundIdx}) — maintained field under the district.");
                }
            }
            catch (Exception ex) { if (groundLogged.Add("ex")) Plugin.Log.LogError("[Ground] " + ex); }
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

        static bool GuidIsNull4(object g)
        { var t = g.GetType(); return (int)(t.GetField("a", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("b", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("c", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("d", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g) ?? 0) == 0; }

        // Crop one atlas tile (its UV rect within the shared page) to a readable 256² PNG. The rect is a Vector4
        // (offsetU, offsetV, scaleU, scaleV); Graphics.Blit with a scale/offset samples exactly that sub-region.
        static byte[] CropAtlasTile(UnityEngine.Texture2D page, UnityEngine.Vector4 uv)
        {
            try
            {
                // the Vector4 is a MIN/MAX UV rect (minU, minV, maxU, maxV) — scale = extent, offset = min. (The
                // first pass read it as offset/scale, so V sampled past 1.0 and wrapped: black + several tiles.)
                var scale = new UnityEngine.Vector2(uv.z - uv.x, uv.w - uv.y);
                var offset = new UnityEngine.Vector2(uv.x, uv.y);
                if (scale.x <= 0f || scale.y <= 0f) { scale = new UnityEngine.Vector2(1, 1); offset = UnityEngine.Vector2.zero; }   // degenerate rect -> whole page
                int sz = 256;
                var rt = UnityEngine.RenderTexture.GetTemporary(sz, sz, 0, UnityEngine.RenderTextureFormat.ARGB32, UnityEngine.RenderTextureReadWrite.sRGB);
                var prev = UnityEngine.RenderTexture.active;
                UnityEngine.Graphics.Blit(page, rt, scale, offset);
                UnityEngine.RenderTexture.active = rt;
                var t = new UnityEngine.Texture2D(sz, sz, UnityEngine.TextureFormat.RGBA32, false);
                t.ReadPixels(new UnityEngine.Rect(0, 0, sz, sz), 0, 0); t.Apply();
                UnityEngine.RenderTexture.active = prev; UnityEngine.RenderTexture.ReleaseTemporary(rt);
                var png = UnityEngine.ImageConversion.EncodeToPNG(t);
                UnityEngine.Object.Destroy(t);
                return png;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[GroundTex] crop: " + ex.Message); return null; }
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
