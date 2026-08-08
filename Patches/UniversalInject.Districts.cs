using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json.Linq;             // provided by the game (mod.io); robust registry parse where JsonUtility no-ops in the game runtime

namespace HumankindAssetFramework
{
    internal static partial class UniversalInject
    {
        // ---- EXPERIMENTAL district-visual repoint (docs/District-Visuals.md) ----
        // Parsed once from config: the target district name + the two override modes. dGuid is a boxed Amplitude Guid or null.
        // One custom district model, from the enc_districts.json registry (written by the District Factory window).
        // Runtime state lives here too, so any number of districts can carry custom models simultaneously.
        internal class DistrictModel
        {
            public string district = "";     // ConstructibleDefinitionName to match
            public object fxMeshGuid;        // parsed Amplitude Guid of the baked FxMesh
            public object atlasGuid;         // parsed Amplitude Guid of the baked albedo atlas (null = untextured, pre-2.0 entries)
            public object normalAtlasGuid;   // baked normal atlas (null = neutral flat) — same rects as the albedo
            public object roughAtlasGuid;    // baked roughness atlas (null = neutral matte)
            public bool isolate = true;      // true = private per-instance leaf (this tile only); false = global shared-leaf swap
            // runtime
            public object plbc; public int layer;
            public object privateLeaf;                                   // isolate mode: the Instantiated leaf
            public readonly List<object> leaves = new List<object>();    // global mode: collected shared leaves
            public bool collected, matchLogged, pointedLogged; public int wait;
            // texture injection runtime (isolate mode)
            public UnityEngine.Texture2D texAlbedo;                      // our baked albedo, bound on the PRIVATE output layer
            public UnityEngine.Texture2D texNormal, texRough;            // baked surface maps (null = the neutral stand-ins)
            public bool texApplied; public int texWait;
        }
        internal static readonly List<DistrictModel> distModels = new List<DistrictModel>();

        static bool distParsed, distOn; static string distName, distAffinity; static object distGuid, distFxMeshGuid;
        static bool distSwapLogged, distGuidLogged;
        static object ParseGuidCsv(string gs)
        {
            if (string.IsNullOrEmpty(gs)) return null;
            // NO '-' in the separators: the components are SIGNED ints, and splitting on '-' silently strips the
            // sign (a negative 'a' produced a corrupted GUID and a catalog miss — found via the Sling_Collection).
            var p = gs.Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 4 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b) && int.TryParse(p[2], out var c) && int.TryParse(p[3], out var d))
                return MakeGuid(a, b, c, d);
            Plugin.Log.LogError($"[District] GUID must be four ints \"a,b,c,d\" (got '{gs}').");
            return null;
        }
        static void EnsureDistrictConfig()
        {
            if (distParsed) return; distParsed = true;
            try
            {
                distOn = Plugin.DistrictRepointOn != null && Plugin.DistrictRepointOn.Value;
                distName = Plugin.DistrictName?.Value?.Trim() ?? "";
                distAffinity = Plugin.DistrictAffinity?.Value?.Trim() ?? "";
                distGuid = ParseGuidCsv(Plugin.DistrictEvolverGuid?.Value?.Trim() ?? "");
                distFxMeshGuid = ParseGuidCsv(Plugin.DistrictFxMeshGuid?.Value?.Trim() ?? "");
                if (distOn) Plugin.Diag($"[District] repoint ACTIVE: name='{distName}' affinity='{distAffinity}' evolverGuid={(distGuid != null ? "set" : "none")} fxMeshGuid={(distFxMeshGuid != null ? "set" : "none")}");

                // The district REGISTRY (written by the District Factory window): any number of district models at once.
                distModels.Clear();
                var regPath = Path.Combine(Paths.ConfigPath, "enc_districts.json");
                if (File.Exists(regPath))
                {
                    try
                    {
                        var root = JObject.Parse(File.ReadAllText(regPath));
                        foreach (var d in (root["districts"] as JArray) ?? new JArray())
                        {
                            var e = new DistrictModel
                            {
                                district = (string)d["district"] ?? "",
                                fxMeshGuid = ParseGuidCsv((string)d["fxMeshGuid"] ?? ""),
                                atlasGuid = ParseGuid4((string)d["atlasGuid"] ?? ""),   // optional: entries baked before texture injection have none
                                normalAtlasGuid = ParseGuid4((string)d["normalAtlasGuid"] ?? ""),
                                roughAtlasGuid = ParseGuid4((string)d["roughAtlasGuid"] ?? ""),
                                isolate = (bool?)d["isolate"] ?? true,
                            };
                            if (e.district.Length > 0 && e.fxMeshGuid != null) distModels.Add(e);
                            else Plugin.Log.LogWarning($"[District] registry entry skipped (district='{e.district}', bad fxMeshGuid?)");
                        }
                        Plugin.Log.LogInfo($"[District] registry: {distModels.Count} district model(s) from enc_districts.json");
                    }
                    catch (Exception rex) { Plugin.Log.LogError("[District] enc_districts.json parse: " + rex); }
                }
                // legacy single-model config keeps working: synthesize an entry when the registry has none
                if (distModels.Count == 0 && !string.IsNullOrEmpty(distName) && distFxMeshGuid != null)
                    distModels.Add(new DistrictModel { district = distName, fxMeshGuid = distFxMeshGuid, isolate = Plugin.DistrictIsolate == null || Plugin.DistrictIsolate.Value });
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] config parse: " + ex); }
        }

        static readonly HashSet<string> distSeen = new HashSet<string>();
        // Diagnostic: log every distinct district name UpdateLevelBuild fires for, so we can see the ACTUAL
        // ConstructibleDefinitionName to target (an Extension_Base_* reactor may present under its host district's name).
        internal static void DistrictDiag(object district)
        {
            try
            {
                EnsureDistrictConfig();
                if (!distOn || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;   // investigation aid — off unless DistrictDebug
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString() ?? "<null>";
                if (distSeen.Add(name))
                    Plugin.Diag($"[District] saw district '{name}'{(name == distName ? "  <-- MATCHES DistrictName" : "")}");
            }
            catch { }
        }

        static readonly HashSet<string> distMatDumped = new HashSet<string>();
        // Diagnostic: after a district builds, read the FxEvolverMaterial GUID its main channel resolved to and log it as
        // "a,b,c,d" — ready to paste into another district's DistrictEvolverGuid (the clean SetChannel path), and the way to
        // grab a donor material for the bake. plbc.channels[layer].EvolverMaterialGuid (PerChannelData is a private struct).
        internal static void DistrictDumpMaterial(object district)
        {
            try
            {
                EnsureDistrictConfig();
                if (!distOn || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;   // investigation aid — off unless DistrictDebug
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString() ?? "<null>";
                if (!distMatDumped.Add(name)) return;
                var plbc = AccessTools.Field(district.GetType(), "presentationLevelBuildComponent")?.GetValue(district);
                if (plbc == null) return;
                int layer = 0;
                var lf = district.GetType().GetField("mainLevelBuildComponantLayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                if (lf?.GetValue(null) is int li) layer = li;
                if (!(AccessTools.Field(plbc.GetType(), "channels")?.GetValue(plbc) is Array channels) || layer >= channels.Length) return;
                var box = channels.GetValue(layer);
                var guid = box.GetType().GetProperty("EvolverMaterialGuid")?.GetValue(box);
                if (guid == null) return;
                var gt = guid.GetType();
                object a = gt.GetField("a", BF)?.GetValue(guid), b = gt.GetField("b", BF)?.GetValue(guid),
                       c = gt.GetField("c", BF)?.GetValue(guid), d = gt.GetField("d", BF)?.GetValue(guid);
                Plugin.Diag($"[DistrictMat] {name} -> material {a},{b},{c},{d}");
            }
            catch (Exception ex) { Plugin.Log.LogError("[DistrictMat] dump: " + ex); }
        }

        static readonly HashSet<string> distSubDumped = new HashSet<string>();
        // Diagnostic: the district's channel-0 material is an FxEvolverMaterialLevelBuildSelector that picks among a table
        // of PLAIN building-variant drawers (its `pairs` NameToGuidPair[] + `defaultMaterial`). Dump those sub-material
        // GUIDs — those are the plain drawers that actually render a mesh, and the clean thing to map an affinity at.
        internal static void DistrictDumpSubMaterials(object district)
        {
            try
            {
                EnsureDistrictConfig();
                if (!distOn || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;   // investigation aid — off unless DistrictDebug
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString() ?? "<null>";
                if (name != distName) return;                       // only the targeted district, to avoid spam
                if (!distSubDumped.Add(name)) return;
                var plbc = AccessTools.Field(district.GetType(), "presentationLevelBuildComponent")?.GetValue(district);
                if (plbc == null) return;
                if (!(AccessTools.Field(plbc.GetType(), "channels")?.GetValue(plbc) is Array channels) || channels.Length == 0) return;
                var box = channels.GetValue(0);
                var sel = AccessTools.Field(box.GetType(), "evolverMaterial")?.GetValue(box);
                if (sel == null) { Plugin.Log.LogInfo($"[DistrictSub] {name}: channel 0 material not loaded yet."); return; }
                Plugin.Log.LogInfo($"[DistrictSub] {name}: channel-0 material type = {sel.GetType().Name}");
                // defaultMaterial / deferredName / deferredTable give context
                foreach (var fn in new[] { "defaultMaterial", "invalidNameMaterial", "deferredTable" })
                {
                    var g = AccessTools.Field(sel.GetType(), fn)?.GetValue(sel);
                    if (g != null) { var gt = g.GetType(); Plugin.Log.LogInfo($"[DistrictSub]   {fn} = {gt.GetField("a", BF)?.GetValue(g)},{gt.GetField("b", BF)?.GetValue(g)},{gt.GetField("c", BF)?.GetValue(g)},{gt.GetField("d", BF)?.GetValue(g)}"); }
                }
                var dn = AccessTools.Field(sel.GetType(), "deferredName")?.GetValue(sel) as string;
                if (!string.IsNullOrEmpty(dn)) Plugin.Log.LogInfo($"[DistrictSub]   deferredName = '{dn}'");
                if (AccessTools.Field(sel.GetType(), "pairs")?.GetValue(sel) is Array pairs)
                {
                    Plugin.Log.LogInfo($"[DistrictSub]   pairs ({pairs.Length} building variants):");
                    foreach (var pr in pairs)
                    {
                        if (pr == null) continue;
                        var pt = pr.GetType();
                        var pn = pt.GetField("Name", BF)?.GetValue(pr) ?? pt.GetProperty("Name", BF)?.GetValue(pr);
                        var pg = pt.GetField("Guid", BF)?.GetValue(pr) ?? pt.GetField("Value", BF)?.GetValue(pr) ?? pt.GetField("guid", BF)?.GetValue(pr);
                        if (pg != null) { var gt = pg.GetType(); Plugin.Log.LogInfo($"[DistrictSub]     '{pn}' -> {gt.GetField("a", BF)?.GetValue(pg)},{gt.GetField("b", BF)?.GetValue(pg)},{gt.GetField("c", BF)?.GetValue(pg)},{gt.GetField("d", BF)?.GetValue(pg)}"); }
                        else Plugin.Log.LogInfo($"[DistrictSub]     '{pn}' -> (guid field not found on {pt.Name})");
                    }
                }
                else Plugin.Log.LogInfo($"[DistrictSub]   no 'pairs' field (uses a deferred table?).");
            }
            catch (Exception ex) { Plugin.Log.LogError("[DistrictSub] dump: " + ex); }
        }

        static bool DistrictMatches(object district, out object plbc)
        {
            plbc = null;
            EnsureDistrictConfig();
            if (!distOn || string.IsNullOrEmpty(distName)) return false;
            var name = GetMember(district, "ConstructibleDefinitionName")?.ToString();
            if (name != distName) return false;
            plbc = AccessTools.Field(district.GetType(), "presentationLevelBuildComponent")?.GetValue(district);
            return true;
        }

        // MODE 1 (zero-bake): before the request is built, swap the district's visualAffinity to another vanilla one so it
        // resolves to an existing building. Pure config, no custom asset — proves the hook + per-district scoping in-game.
        internal static void DistrictAffinitySwap(object district)
        {
            try
            {
                EnsureDistrictConfig();
                if (string.IsNullOrEmpty(distAffinity) || distGuid != null) return;  // cheap bail BEFORE any reflection — this legacy mode is usually off
                if (!DistrictMatches(district, out _)) return;
                // Derive the StaticString type from the field itself (it's Amplitude.StaticString, not Amplitude.Framework.*).
                var vf = AccessTools.Field(district.GetType(), "visualAffinityName");
                if (vf == null) { Plugin.Log.LogError("[District] visualAffinityName field not found."); return; }
                var ss = Activator.CreateInstance(vf.FieldType, new object[] { distAffinity });
                SetMember(district, "visualAffinityName", ss);
                SetMember(district, "initialVisualAffinityName", ss);
                if (!distSwapLogged) { distSwapLogged = true; Plugin.Diag($"[District] '{distName}' affinity -> '{distAffinity}' (zero-bake swap)"); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] affinity swap: " + ex); }
        }

        // MODE 3 (best render odds): keep the district's OWN loaded material (which already renders correctly in this
        // context) and swap only its mesh GUID to our baked FxMesh. Avoids the "foreign material doesn't render here"
        // problem — our model draws through the exact material/shader/output-layer the vanilla building already uses.
        // Recursively rewrite the `mesh` GUID of every LEAF drawer reachable from a material: a plain drawer has a `mesh`
        // Guid field (swap it); a SELECTOR/EMITTER holds its loaded sub-materials in `fxMaterialCacheEntries.Entries[].FxMaterial`
        // (+ InvalidNameEntry) — recurse into those. Reuses the game's OWN loaded drawers, so they keep the selector's context.
        static MethodInfo fxTryLoad;
        static object TryLoadMaterial(object guid)
        {
            try
            {
                if (fxTryLoad == null)
                {
                    var fmType = GameBinding.FxEvolverMaterial;
                    // prefer the SYNCHRONOUS overload TryLoad(Guid, bool synchrone) so we get the material now, not async-null.
                    fxTryLoad = fmType?.GetMethods(BindingFlags.Static | BindingFlags.Public)
                        .FirstOrDefault(m => m.Name == "TryLoad" && m.GetParameters().Length == 2
                            && m.GetParameters()[0].ParameterType.Name == "Guid" && m.GetParameters()[1].ParameterType == typeof(bool));
                }
                return fxTryLoad?.Invoke(null, new object[] { guid, true });
            }
            catch { return null; }
        }
        static bool GuidIsNull(object g) { if (g == null) return true; var t = g.GetType(); return (int)(t.GetField("a", BF)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("b", BF)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("c", BF)?.GetValue(g) ?? 0) == 0 && (int)(t.GetField("d", BF)?.GetValue(g) ?? 0) == 0; }
        static object PairGuid(object pair) { var pt = pair.GetType(); return pt.GetField("Guid", BF)?.GetValue(pair) ?? pt.GetField("Value", BF)?.GetValue(pair) ?? pt.GetField("guid", BF)?.GetValue(pair); }

        // The leaf that holds geometry is FxEvolverMaterialLevelBuildElement with an `fxMesh` Guid field. Reached via:
        //   Selector.pairs[culture] -> Emitter.levelBuildItems[].loadedEvolverMaterial -> Element(.fxMesh)  (Emitters nest).
        static readonly List<object> distLeaves = new List<object>();   // legacy shared list (single-model path)
        static FieldInfo GF(Type t, string n) => t.GetField(n, BF);      // no AccessTools warning-on-miss (probing spams the log)
        static void CollectLeaves(object mat, List<object> outLeaves, int depth, HashSet<object> visited)
        {
            if (mat == null || depth > 8 || !visited.Add(mat)) return;
            var t = mat.GetType();
            // a leaf: has an fxMesh (or mesh) Guid field
            var lf = GF(t, "fxMesh") ?? GF(t, "mesh");
            if (lf != null && lf.FieldType.Name == "Guid") { outLeaves.Add(mat); return; }
            // emitter: levelBuildItems[].loadedEvolverMaterial
            if (AccessTools.Field(t, "levelBuildItems")?.GetValue(mat) is Array items)
                foreach (var it in items) if (it != null) CollectLeaves(AccessTools.Field(it.GetType(), "loadedEvolverMaterial")?.GetValue(it), outLeaves, depth + 1, visited);
            // selector: loaded cache entries + the pairs variant table (load each distinct GUID)
            var cache = AccessTools.Field(t, "fxMaterialCacheEntries")?.GetValue(mat);
            if (cache != null && AccessTools.Field(cache.GetType(), "Entries")?.GetValue(cache) is Array entries)
                foreach (var e in entries) if (e != null) CollectLeaves(AccessTools.Field(e.GetType(), "FxMaterial")?.GetValue(e), outLeaves, depth + 1, visited);
            var seen = new HashSet<string>();
            void tryGuid(object g)
            {
                if (GuidIsNull(g)) return; var gt = g.GetType();
                if (!seen.Add($"{gt.GetField("a", BF)?.GetValue(g)},{gt.GetField("b", BF)?.GetValue(g)},{gt.GetField("c", BF)?.GetValue(g)},{gt.GetField("d", BF)?.GetValue(g)}")) return;
                CollectLeaves(TryLoadMaterial(g), outLeaves, depth + 1, visited);
            }
            if (AccessTools.Field(t, "pairs")?.GetValue(mat) is Array pairs)
                foreach (var pr in pairs) if (pr != null) tryGuid(PairGuid(pr));
            foreach (var fn in new[] { "defaultMaterial", "invalidNameMaterial" })
            { var g = AccessTools.Field(t, fn)?.GetValue(mat); if (g != null) tryGuid(g); }
        }
        static object distFxManager; static MethodInfo fxNextDoublon;
        // Re-point every collected leaf's fxMesh at our FxMesh. On the first pass, also call the leaf's own Load() so it
        // RE-RESOLVES meshIndex from our GUID (the render reads meshIndex, not fxMesh; uint.MaxValue is 'unresolved').
        static int ApplyLeaves(List<object> leaves, object fxGuid, bool resolve)
        {
            int n = 0, resolved = 0;
            foreach (var leaf in leaves)
            {
                var t = leaf.GetType();
                var lf = GF(t, "fxMesh") ?? GF(t, "mesh");
                if (lf == null) continue;
                lf.SetValue(leaf, fxGuid);   // persists our GUID so any game re-Load also uses ours
                n++;
                if (resolve && distFxManager != null)
                {
                    try
                    {
                        var load = t.GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (load != null && load.GetParameters().Length == 2)
                        {
                            if (fxNextDoublon == null)
                                fxNextDoublon = GameBinding.FxEvolverMaterial?.GetMethod("NextDoublonAvoidanceIndex", BindingFlags.Static | BindingFlags.Public);
                            uint doublon = fxNextDoublon != null ? (uint)fxNextDoublon.Invoke(null, null) : 0u;
                            load.Invoke(leaf, new object[] { distFxManager, doublon });
                            resolved++;
                            if (resolved <= 4)   // peek the resolved meshIndex: uint.MaxValue = GetMeshIndex FAILED to find our FxMesh
                            {
                                var mi = AccessTools.Field(t, "meshIndex")?.GetValue(leaf);
                                var oli = AccessTools.Field(t, "outputLayerIndex")?.GetValue(leaf);
                                Plugin.Diag($"[District]   leaf '{GetMember(leaf, "Name")}' after Load: meshIndex={mi} outputLayerIndex={oli}");
                            }
                        }
                    }
                    catch { }
                }
            }
            if (resolve) Plugin.Diag($"[District] '{distName}': re-pointed {n} leaves, re-resolved {resolved} via Load().");
            return n;
        }

        // (The old single-district MODE 3 cache was replaced by the registry-driven DistrictApplyEntries/Tick below.
        // Two dead investigation dumps — DumpOurFxMesh and DumpMaterialTree ([DistrictTree]) — were removed in the
        // post-breakthrough cleanup; recover from git history if a new district material chain ever needs mapping.)

        // Full district diagnostic for the F8 window: our FxMesh, the collected leaves, their resolved meshIndex, and the
        // DISTRICT mesh manager's per-layer buffer FILL (verts used / buffer size) — so we can SEE if our mesh doesn't fit.
        internal static List<string> DumpDistrictState()
        {
            var lines = new List<string>();
            try
            {
                EnsureDistrictConfig();
                lines.Add($"district registry: {distModels.Count} model(s)");
                var fxMeshType = GameBinding.FxMesh;
                var adb = GameBinding.AssetDatabase;
                var load = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
                var loadFx = fxMeshType != null && load != null ? load.MakeGenericMethod(fxMeshType) : null;
                object anyLeaf = null;
                foreach (var e in distModels)
                {
                    string meshInfo = "?";
                    if (loadFx != null && e.fxMeshGuid != null)
                    {
                        var fx = loadFx.Invoke(null, loadFx.GetParameters().Length == 1 ? new[] { e.fxMeshGuid } : new[] { e.fxMeshGuid, null });
                        var um = fx != null ? (fxMeshType.GetProperty("Mesh", BF)?.GetValue(fx) ?? GF(fxMeshType, "mesh")?.GetValue(fx)) as UnityEngine.Mesh : null;
                        meshInfo = fx == null ? "FxMesh NULL by GUID" : um == null ? "Mesh NULL" : um.vertexCount + " verts, bounds " + um.bounds.size;
                    }
                    var leaf = e.privateLeaf ?? (e.leaves.Count > 0 ? e.leaves[0] : null);
                    var mi = leaf != null ? GF(leaf.GetType(), "meshIndex")?.GetValue(leaf) : null;
                    lines.Add($"  '{e.district}' isolate={e.isolate} matched={(e.plbc != null)} leaf={(leaf != null ? "meshIndex=" + mi : "not built")} | {meshInfo}");
                    anyLeaf = anyLeaf ?? leaf;
                }
                // walk to the district mesh content manager and dump each layer's fill
                if (anyLeaf != null)
                {
                    var desc = GetMember(anyLeaf, "FxEvolverDescriptor");
                    var mgr = desc != null ? GetMember(desc, "AssetContentManagerMesh") : null;
                    if (mgr != null && GetMember(mgr, "Layers") is Array layers)
                    {
                        lines.Add($"mesh manager: {layers.Length} layer(s)");
                        for (int i = 0; i < layers.Length; i++)
                        {
                            var L = layers.GetValue(i); if (L == null) continue;
                            var nm = GetMember(L, "Name") ?? GetMember(L, "name");
                            var cv = GetMember(L, "currentVertexIndex");
                            var vb = GetMember(L, "vertexBuffer"); var vbSize = vb != null ? GetMember(vb, "Size") : null;
                            var cm = GetMember(L, "currentMeshAddedCount");
                            lines.Add($"  layer {i} '{nm}': verts {cv}/{vbSize}, meshes {cm}");
                        }
                    }
                }
            }
            catch (Exception ex) { lines.Add("dump error: " + ex.Message); }
            foreach (var l in lines) Plugin.Diag("[DistrictState] " + l);
            return lines;
        }

        // ISOLATION: instead of mutating the shared building leaves globally, give ONLY the target district a PRIVATE leaf.
        // Each PresentationLevelBuildComponent has its own channel + Shuriken particle, so pointing just this district's
        // channel at a private (Instantiated) leaf — and re-spawning its particle — scopes our mesh to this tile alone.
        static object BuildPrivateLeaf(object channelSelector, object fxGuid, object atlasGuid, bool instantAppear = false)
        {
            try
            {
                var found = new List<object>();
                CollectLeaves(channelSelector, found, 0, new HashSet<object>());
                if (found.Count == 0) return null;
                if (!(found[0] is UnityEngine.Object src) || src == null) return null;
                var clone = UnityEngine.Object.Instantiate(src);   // a private copy — mutating it won't touch the shared leaf
                var t = clone.GetType();
                (GF(t, "fxMesh") ?? GF(t, "mesh"))?.SetValue(clone, fxGuid);
                // REVEAL-RAMP lever (wonder path): fadeInOutMode {Stepped, Smooth, Instant} is the element's
                // appearance transition, encoded into its GPU data — Instant skips the bottom-to-roof build ramp.
                // Set BEFORE the Load below so the first GPU write already carries it.
                if (instantAppear)
                {
                    var fm = GF(t, "fadeInOutMode");
                    if (fm != null) { fm.SetValue(clone, Enum.Parse(fm.FieldType, "Instant")); Plugin.Diag("[District] private leaf fadeInOutMode -> Instant (reveal ramp skipped)"); }
                }
                // TEXTURE INJECTION step 1: point the leaf's texture at the LAYER's own missing-texture slot. That slot's
                // atlas rect is never rendered by vanilla content (everything real is bound), so its pixels are ours to
                // paint (step 2, DistrictApplyTexture). The Load below then resolves textureIndex to that rect via the
                // game's own RefreshIndices — no private-index poking.
                // TEXTURE INJECTION step 1: give this leaf a PRIVATE clone of its whole FxOutputLayer. The district layer
                // is a FULL-TEXTURE layer (textureIndex resolves to the fixed full-texture slot 1; no atlas manager entry
                // — measured): every element samples the layer material's bound sheet through its mesh UVs [0,1]. The
                // sheet binding is per-LAYER, so coloring our mesh without corrupting every building sharing the layer
                // needs a layer of our own. Instantiate resets the NonSerialized runtime state (layerIndex -1, unloaded),
                // and the leaf's own Load below hands the clone to FxComponentRenderer.GetLayerIndexAddItIFN — the game
                // registers and loads it like any authored layer, cloning private runtime materials we can then re-bind
                // (DistrictApplyTexture step 2).
                if (atlasGuid != null)
                {
                    var olF = GF(t, "outputLayer");
                    if (olF?.GetValue(clone) is UnityEngine.Object srcLayer && srcLayer != null)
                    {
                        var layerClone = UnityEngine.Object.Instantiate(srcLayer);
                        layerClone.name = srcLayer.name + "_HAF";
                        // OPT OUT OF TEXTURE STREAMING (the perfect->brown->corrupt fix, measured on the Oracle): the
                        // reduction system keeps loading proxy/mid/hi-res materials into the layer's RenderOutputs and
                        // each arrival stomps our albedo binding. Null the clone's mid/high material GUIDs — the game's
                        // own LoadOrContinueLoadingHighResRenderMaterial short-circuits on a null guid, so our layer
                        // simply never streams: one runtime material, bound once, stable forever.
                        int cleared = 0;
                        if (GetMember(layerClone, "RenderOutputs") is Array ros0)
                            foreach (var ro in ros0)
                                foreach (var gn in new[] { "midResMaterialGuid", "highResMaterialGuid" })
                                {
                                    var gf2 = ro?.GetType().GetField(gn, BF);
                                    if (gf2 != null) { gf2.SetValue(ro, Activator.CreateInstance(gf2.FieldType)); cleared++; }
                                }
                        olF.SetValue(clone, layerClone);
                        Plugin.Diag($"[DistrictTex] private output layer '{layerClone.name}' cloned from '{srcLayer.name}' (streaming opted out: {cleared} reduction guid(s) nulled)");
                    }
                    else Plugin.Log.LogWarning("[DistrictTex] leaf has no outputLayer to clone — texture injection unavailable.");
                }
                // reset load state so LoadIFN actually re-runs and resolves our mesh + assigns a MaterialIndex
                foreach (var ls in new[] { "loadingStatus" }) { var f = GF(t, ls); if (f != null) f.SetValue(clone, System.Enum.ToObject(f.FieldType, 0)); }
                var loadIFN = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "LoadIFN" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType.Name.Contains("FxManager"));
                if (loadIFN != null && distFxManager != null)
                {
                    if (fxNextDoublon == null) fxNextDoublon = GameBinding.FxEvolverMaterial?.GetMethod("NextDoublonAvoidanceIndex", BindingFlags.Static | BindingFlags.Public);
                    var pars = loadIFN.GetParameters();
                    var args = pars.Length == 1 ? new[] { distFxManager } : new object[] { distFxManager, fxNextDoublon != null ? fxNextDoublon.Invoke(null, null) : (uint)0 };
                    loadIFN.Invoke(clone, args);
                }
                // also force the mesh re-resolve directly (Load), in case LoadIFN short-circuited
                var load = t.GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (load != null && load.GetParameters().Length == 2 && distFxManager != null)
                    load.Invoke(clone, new object[] { distFxManager, fxNextDoublon != null ? fxNextDoublon.Invoke(null, null) : (uint)0 });
                Plugin.Diag($"[District] built PRIVATE leaf '{t.Name}': MaterialIndex={GF(t, "materialIndex")?.GetValue(clone)} meshIndex={GF(t, "meshIndex")?.GetValue(clone)} textureIndex={GF(t, "textureIndex")?.GetValue(clone)}");
                DumpLeafFields(clone);   // reveal-ramp hunt: what timing/growth levers does the clone carry?
                return clone;
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] build private leaf: " + ex); return null; }
        }
        // ISOLATE mode, per entry: keep this district's channel pointed at ITS private leaf + re-spawned particle.
        // Re-applied each frame (the game reloads the shared selector into the channel on UpdateLevelBuild).
        static void PointEntryAtPrivateLeaf(DistrictModel e)
        {
            try
            {
                if (e.plbc == null) return;
                if (!(AccessTools.Field(e.plbc.GetType(), "channels")?.GetValue(e.plbc) is Array channels) || e.layer >= channels.Length) return;
                var box = channels.GetValue(e.layer);
                var evf = GF(box.GetType(), "evolverMaterial");
                if (evf == null) return;
                // build the private leaf lazily — the selector's sub-materials load async, so retry until they're ready.
                if (e.privateLeaf == null)
                {
                    var sel = evf.GetValue(box);
                    if (sel == null) return;
                    e.privateLeaf = BuildPrivateLeaf(sel, e.fxMeshGuid, e.atlasGuid);
                    // WONDER path: a database-fed selector (fillMode LevelBuildDatabase) has no inline leaves to walk —
                    // source them from the PLUGIN-LOADED template material instead (swap-first sequencing: the wonder's
                    // repository cell stays empty until this swap is live, so the template is never drawn on the tile).
                    if (e.privateLeaf == null)
                    {
                        var wm = WonderTemplate(e.district);
                        if (wm != null) e.privateLeaf = BuildPrivateLeaf(wm, e.fxMeshGuid, e.atlasGuid, instantAppear: true);
                    }
                    if (e.privateLeaf == null) { if (e.wait++ % 300 == 0) Plugin.Diag($"[District] '{e.district}': waiting for leaves to load..."); return; }
                }
                if (ReferenceEquals(evf.GetValue(box), e.privateLeaf)) return;   // already ours this frame
                evf.SetValue(box, e.privateLeaf);
                channels.SetValue(box, e.layer);   // write the mutated struct back into the array
                // re-spawn the particle so PatchParticle picks up the private leaf's MaterialIndex
                var refresh = e.plbc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "RefreshChannel" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                if (refresh != null) refresh.Invoke(e.plbc, new object[] { e.layer, System.Enum.ToObject(refresh.GetParameters()[1].ParameterType, 0) });
                if (!e.pointedLogged) { e.pointedLogged = true; Plugin.Diag($"[District] '{e.district}' ISOLATED: channel {e.layer} -> its private leaf (this tile only)."); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] point channel: " + ex); }
        }

        // Diagnostic (DistrictDebug): dump every serializable field of the cloned leaf — the hunt for the
        // level-build reveal-ramp levers (duration/speed/curve fields we could zero on the load path).
        static bool leafFieldsDumped;
        static void DumpLeafFields(object clone)
        {
            try
            {
                if (leafFieldsDumped || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;
                leafFieldsDumped = true;
                var t = clone.GetType();
                Plugin.Log.LogInfo($"[LeafDump] === {t.FullName} (base {t.BaseType?.Name}) ===");
                for (var ct = t; ct != null && ct.Name != "Object" && ct.Name != "ScriptableObject"; ct = ct.BaseType)
                {
                    foreach (var f in ct.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        object v = null; try { v = f.GetValue(clone); } catch { }
                        string vs;
                        var ft = f.FieldType;
                        if (v == null) vs = "<null>";
                        else if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) vs = v.ToString();
                        else if (ft.IsValueType) vs = v.ToString();
                        else if (ft.IsArray) vs = $"{ft.GetElementType()?.Name}[{((Array)v).Length}]";
                        else vs = ft.Name;
                        Plugin.Log.LogInfo($"[LeafDump] {ct.Name}.{f.Name} : {ft.Name} = {vs}");
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[LeafDump] " + ex); }
        }

        // GLOBAL mode, per entry: collect the shared leaves once and re-point them all (affects every district sharing them).
        static void GlobalSwapEntry(DistrictModel e)
        {
            try
            {
                if (e.plbc == null) return;
                if (!e.collected)
                {
                    if (!(AccessTools.Field(e.plbc.GetType(), "channels")?.GetValue(e.plbc) is Array channels) || e.layer >= channels.Length) return;
                    var mat = GF(channels.GetValue(e.layer).GetType(), "evolverMaterial")?.GetValue(channels.GetValue(e.layer));
                    if (mat == null) return;
                    e.leaves.Clear();
                    CollectLeaves(mat, e.leaves, 0, new HashSet<object>());
                    if (e.leaves.Count == 0) return;   // async load — retry next frame
                    e.collected = true;
                    Plugin.Diag($"[District] '{e.district}': collected {e.leaves.Count} shared leaf Element(s) (GLOBAL swap).");
                    ApplyLeaves(e.leaves, e.fxMeshGuid, resolve: true);
                }
                else ApplyLeaves(e.leaves, e.fxMeshGuid, resolve: false);   // keep our GUID set in case the game re-Loads
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] global swap: " + ex); }
        }

        // Postfix (per district UpdateLevelBuild): match against the registry and cache each entry's component + layer.
        internal static void DistrictApplyEntries(object district)
        {
            try
            {
                EnsureDistrictConfig();
                if (!distOn || distModels.Count == 0) return;
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString();
                if (string.IsNullOrEmpty(name)) return;
                foreach (var e in distModels)
                {
                    if (e.district != name || e.fxMeshGuid == null) continue;
                    var plbc = AccessTools.Field(district.GetType(), "presentationLevelBuildComponent")?.GetValue(district);
                    if (plbc == null) continue;
                    // FRESH-FIRST, never `??`-cached: a second game in the same app run replaces the FxManager, and a
                    // stale cached one has fxComponents == null — every leaf LoadIFN then NREs (the Oracle incident:
                    // the wonder class was innocent, the corpse manager was the whole failure).
                    var fm = GetMember(plbc, "FxManager");
                    if (fm != null) distFxManager = fm;
                    int layer = 0;
                    var lf = district.GetType().GetField("mainLevelBuildComponantLayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                    if (lf?.GetValue(null) is int li) layer = li;
                    e.plbc = plbc; e.layer = layer;
                    if (!e.matchLogged) { e.matchLogged = true; Plugin.Diag($"[District] registry matched '{e.district}' (isolate={e.isolate})."); }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] apply entries: " + ex); }
        }

        // Session/save-load reset: everything the district machinery caches references the CURRENT world — the
        // FxManager, each entry's matched component, its private leaf (whose meshIndex points into the CURRENT
        // session's GPU buffers) and the texture bindings. A SAVE-RELOAD rebuilds the world WITHOUT firing the
        // AnimationLoad rearm (measured: the Oracle tile came up EMPTY because the per-frame repoint kept forcing the
        // NEW channel onto the SESSION-1 corpse leaf — stale meshIndex = draws nothing). Called from the rearm AND
        // from the Sandbox.Load postfix, so both full loads and in-session reloads rebuild fresh.
        internal static void ResetDistrictSessionState()
        {
            distFxManager = null;
            foreach (var d in distModels)
            {
                d.plbc = null; d.privateLeaf = null; d.leaves.Clear(); d.collected = false;
                d.matchLogged = d.pointedLogged = false; d.wait = 0;
                d.texApplied = false; d.texWait = 0; d.texAlbedo = null; d.texNormal = null; d.texRough = null;
            }
            ResetWonderTemplates();   // plugin-loaded wonder templates are corpses after a reload; re-load + re-fill swap-first
            Plugin.Diag("[District] session state reset (new game or save-reload) — leaves + texture bindings rebuild");
        }

        // Per-frame (Plugin.Update): drive every registry entry.
        internal static void TickDistrictMeshSwap()
        {
            if (distModels.Count == 0) return;
            foreach (var e in distModels)
            {
                if (e.isolate) { PointEntryAtPrivateLeaf(e); DistrictApplyTexture(e); }
                else GlobalSwapEntry(e);
            }
        }

        // ---- district TEXTURE injection (docs/District-Visuals.md) --------------------------------------------------
        // MEASURED (the breeder-reactor arc): the district building layer is a FULL-TEXTURE layer — it has NO atlas
        // manager entry, every leaf resolves textureIndex to the fixed full-texture slot (1), and the shader samples the
        // layer material's bound sheet straight through the mesh UVs. (An earlier rect-painting design targeting
        // FxComponentTextureAtlasManager was falsified by that trace and never shipped — the layer isn't atlas-managed.)
        // So texture = a per-LAYER material binding, shared by every building drawn through the layer. The unlock is one
        // step up from the leaf clone: BuildPrivateLeaf also clones the whole FxOutputLayer (the game registers + loads
        // it via GetLayerIndexAddItIFN during the leaf's own Load, creating PRIVATE runtime materials), and here we bind
        // our baked albedo on those materials. Our mesh's own [0,1] UVs then sample our albedo exactly — no rects, no
        // shared-sheet corruption. Re-asserted periodically: res switches rebuild the runtime materials.
        static void DistrictApplyTexture(DistrictModel e)
        {
            try
            {
                if (e.atlasGuid == null || e.privateLeaf == null) return;
                // tight re-assert: the game rebuilds the layer's runtime materials when hi-res textures stream in /
                // resolution switches, dropping our binding — at 120 ticks the wrong-texture window was ~2 s visible
                // on the Oracle; 15 ticks makes it a blink. Cheap: ReferenceEquals early-outs when already bound.
                if (e.texApplied) { if ((++e.texWait % 15) == 0) BindAlbedo(e, log: false); return; }
                var leaf = e.privateLeaf; var t = leaf.GetType();

                int layerIdx = GF(t, "outputLayerIndex")?.GetValue(leaf) is int li ? li : -1;
                if (layerIdx < 0)
                { if ((++e.texWait % 300) == 1) Plugin.Diag($"[DistrictTex] '{e.district}': private layer not registered yet"); return; }

                // give our private layer a NULL atlas info slot so the game's own resolve (RefreshIndices ->
                // GetTextureIndex) returns the full-texture slot for it on every future re-Load, then point the live
                // leaf there right now.
                var desc = GetMember(leaf, "FxEvolverDescriptor");
                var texMgr = desc != null ? AccessTools.Field(desc.GetType(), "assetContentManagerTexture")?.GetValue(desc) : null;
                texMgr?.GetType().GetMethod("AddNullAtlasInfo", BindingFlags.Instance | BindingFlags.Public)?.Invoke(texMgr, new object[] { layerIdx });
                GF(t, "textureIndex")?.SetValue(leaf, 1);   // TextureIndexForFullTexture — mesh UVs sample the sheet [0,1]

                // our shipped albedo (+ the optional surface-map atlases — null falls back to the neutral stand-ins)
                e.texAlbedo = LoadAmpliAsset(typeof(UnityEngine.Texture2D), e.atlasGuid) as UnityEngine.Texture2D;
                if (e.texAlbedo == null)
                { if ((++e.texWait % 300) == 1) Plugin.Diag($"[DistrictTex] '{e.district}': albedo atlas not loadable by GUID yet"); return; }
                if (e.normalAtlasGuid != null && e.texNormal == null)
                    e.texNormal = LoadAmpliAsset(typeof(UnityEngine.Texture2D), e.normalAtlasGuid) as UnityEngine.Texture2D;
                if (e.roughAtlasGuid != null && e.texRough == null)
                    e.texRough = LoadAmpliAsset(typeof(UnityEngine.Texture2D), e.roughAtlasGuid) as UnityEngine.Texture2D;

                if (!BindAlbedo(e, log: true))
                { if ((++e.texWait % 300) == 1) Plugin.Diag($"[DistrictTex] '{e.district}': private layer has no runtime materials yet"); return; }

                e.texApplied = true;
                // flush: mark the descriptor's material data changed (re-writes the element GPU data, incl. our
                // textureIndex + layer index) and re-spawn this district's particle so nothing keeps a stale index
                if (desc != null) AccessTools.Field(desc.GetType(), "materialDataHasChanged")?.SetValue(desc, true);
                if (e.plbc != null)
                {
                    var refresh = e.plbc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(m => m.Name == "RefreshChannel" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                    refresh?.Invoke(e.plbc, new object[] { e.layer, System.Enum.ToObject(refresh.GetParameters()[1].ParameterType, 0) });
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[DistrictTex] apply: " + ex); e.texApplied = true; }   // fail once, loudly — don't spam per frame
        }

        // Neutral PBR maps: the vanilla sheet's normal/roughness/metallic/AO stay bound under our albedo and paint the
        // donor building's surface detail (bricks, window bumps) over the custom texture — the "corrupt" look. Flat
        // stand-ins kill it: normal (128,128,255,a128 — flat in both standard and swizzled encodings), mid-grey
        // roughness (matte), black metallic, white AO. Created once, 4x4, uncompressed.
        static UnityEngine.Texture2D neuNormal, neuRough, neuMetal, neuAO;
        static UnityEngine.Texture2D NeutralTex(UnityEngine.Color32 c)
        {
            var t = new UnityEngine.Texture2D(4, 4, UnityEngine.TextureFormat.RGBA32, false);
            var px = new UnityEngine.Color32[16]; for (int i = 0; i < 16; i++) px[i] = c;
            t.SetPixels32(px); t.Apply(false, true);
            return t;
        }
        static void NeutralizeSurfaceMaps(DistrictModel e, UnityEngine.Material mat)
        {
            if (neuNormal == null)
            {
                neuNormal = NeutralTex(new UnityEngine.Color32(128, 128, 255, 128));
                neuRough = NeutralTex(new UnityEngine.Color32(140, 140, 140, 140));
                neuMetal = NeutralTex(new UnityEngine.Color32(0, 0, 0, 255));
                neuAO = NeutralTex(new UnityEngine.Color32(255, 255, 255, 255));
            }
            void Set(string prop, UnityEngine.Texture2D tex)
            {
                if (mat.HasProperty(prop) && !ReferenceEquals(mat.GetTexture(prop), tex)) mat.SetTexture(prop, tex);
            }
            // ONLY entries that BAKED surface maps get the full set (real normal/rough + neutral metal/AO — the
            // temple's verified combo). Entries WITHOUT baked maps keep the donor material's own vanilla maps —
            // the reactor's verified 08-06 look; blanket neutrals turned its grey palette into chrome domes and
            // near-black walls (the 08-08 "texture got scrambled" regression).
            if (e.texNormal == null && e.texRough == null) return;
            Set("_NormalMap", e.texNormal != null ? e.texNormal : neuNormal);
            Set("_RoughnessMap", e.texRough != null ? e.texRough : neuRough);
            Set("_MetallicMap", neuMetal);
            Set("_AmbiantOcclusionMap", neuAO);   // the game's own spelling
        }

        // Bind our albedo on the PRIVATE layer's runtime materials. The sheet property is picked per material: "_MainTex"
        // when present and bound, else the largest bound Texture2D (the building sheet dwarfs masks/LUTs). Under
        // DistrictDebug the first pass dumps every material's texture properties so a wrong pick is visible in the log.
        static bool BindAlbedo(DistrictModel e, bool log)
        {
            try
            {
                if (e.texAlbedo == null || e.privateLeaf == null) return false;
                var ol = GF(e.privateLeaf.GetType(), "outputLayer")?.GetValue(e.privateLeaf);
                if (!(GetMember(ol, "RenderOutputs") is Array ros)) return false;
                int n = 0;
                bool dump = log && Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value;
                foreach (var ro in ros)
                    foreach (var fld in new[] { "currentRenderMaterial", "runTimeRenderMaterial", "highResRunTimeRenderMaterial" })
                        if (GetMember(ro, fld) is UnityEngine.Material mat && mat != null)
                        {
                            string pick = null; UnityEngine.Texture2D biggest = null; string biggestProp = null;
                            bool already = false;
                            foreach (var pn in mat.GetTexturePropertyNames())
                            {
                                var cur = mat.GetTexture(pn);
                                if (dump) Plugin.Diag($"[DistrictTex]   {fld}('{mat.shader?.name}').{pn} = {(cur != null ? $"'{cur.name}' {cur.width}x{cur.height}" : "null")}");
                                if (ReferenceEquals(cur, e.texAlbedo)) { already = true; continue; }
                                if (!(cur is UnityEngine.Texture2D t2)) continue;
                                if (pn == "_MainTex") pick = pn;
                                if (biggest == null || t2.width * t2.height > biggest.width * biggest.height) { biggest = t2; biggestProp = pn; }
                            }
                            if (already) { n++; NeutralizeSurfaceMaps(e, mat); continue; }
                            if (pick == null) pick = biggestProp;
                            if (pick != null)
                            {
                                mat.SetTexture(pick, e.texAlbedo); n++;
                                NeutralizeSurfaceMaps(e, mat);
                                if (log) Plugin.Diag($"[DistrictTex] '{e.district}': albedo bound on {fld}.{pick} (+neutral surface maps)");
                            }
                        }
                if (log) Plugin.Diag($"[DistrictTex] '{e.district}': albedo bound on {n} material slot(s) of the private layer");
                return n > 0;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[DistrictTex] bind: " + ex.Message); return false; }
        }

        // ---- EXPERIMENTAL pawn PROP/attachment axis (custom weapons & gear on attachment slots; the sling experiment) ----
        // A pawn's Attachements[] slot references a PresentationPawnFragmentMesh (the EQ_* assets) = {ModelPrefab, ModelName,
        // MaterialRef}: a RIGID mesh glued to the slot's bone, GPU-encoded at spawn. The loader hard-gates on
        // AnimationManager.GetMeshCollection(ModelPrefab.Guid) finding a REGISTERED collection ("was not registered ...
        // please add it to AnimationManagerContent" -> draws nothing). AnimationManager.RegisterMeshCollection is PUBLIC and
        // also uploads the collection's meshes to the GPU content manager (LoadIFN) — so one call crosses the gate. Skeleton
        // DERIVES from MeshCollection, so our baked Skeleton assets qualify directly. Retry per-frame: the manager instance
        // and its internal list only exist once the presentation loads, and our bundle assets load async.
        // TIMING is the crux: pawn definitions resolve their fragments INSIDE the game's loading chunk, so an Update-tick
        // registration loses the race by construction — the pawn definition then fails its Load, never registers a pawn
        // id, and its units draw as pawn definition 0 (the MAMMOTH — observed). The real seam is Hk_PropRegister below:
        // a postfix on AnimationManager.AnimationLoad, which rebuilds the manager's collection list and registers the
        // game's own collections; we append ours right there, before any pawn resolves. The Update tick stays as a
        // late-repair safety net only.
        // ---- EXPERIMENTAL projectile axis (docs/Projectiles.md) ----
        // A unit's PresentationPawnDefinition.Projectile (a ProjectileAssetReference) is read at attack time to spawn the
        // flying FX. We load the pawn def AND our baked ProjectileAsset by GUID and re-point the reference's inner guid —
        // the same AssetReference-guid swap the prop axis uses for a fragment's ModelPrefab. Applied at AnimationLoad (data
        // is up, before combat); idempotent, so re-running each session just re-asserts it.
        static bool projParsed;
        static readonly List<(object pawnGuid, object projGuid, string raw)> projOverrides = new List<(object, object, string)>();
        internal static void RearmProjectileOverrides() { projParsed = false; projOverrides.Clear(); }

        // Comma-ONLY 4-int parser. (ParseGuidCsv splits on '-' too, which would corrupt the negative ints a projectile
        // GUID routinely has, e.g. -839228096.)
        static object ParseGuid4(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var p = s.Split(',');
            if (p.Length == 4 && int.TryParse(p[0].Trim(), out var a) && int.TryParse(p[1].Trim(), out var b)
                && int.TryParse(p[2].Trim(), out var c) && int.TryParse(p[3].Trim(), out var d))
                return MakeGuid(a, b, c, d);
            return null;
        }

        static MethodInfo adbLoadAsset;
        static object LoadAmpliAsset(Type assetType, object guid)
        {
            if (adbLoadAsset == null)
            {
                var adb = GameBinding.AssetDatabase;
                adbLoadAsset = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "TryLoadAsset" || m.Name == "LoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            }
            try { return adbLoadAsset?.MakeGenericMethod(assetType).Invoke(null, new[] { guid }); } catch { return null; }
        }

        // AssetReference<T> hides its guid on a private base — walk the chain (mirrors PropBaker.FindFieldDeep).
        static FieldInfo FindGuidField(Type t)
        {
            for (; t != null; t = t.BaseType)
                foreach (var n in new[] { "guid", "Guid" })
                {
                    var f = t.GetField(n, BF | BindingFlags.DeclaredOnly);
                    if (f != null && f.FieldType.Name == "Guid") return f;
                }
            return null;
        }

        internal static void ApplyProjectileOverrides(string cfg)
        {
            if (!projParsed)
            {
                projParsed = true;
                foreach (var entry in (cfg ?? "").Split(';'))
                {
                    var e = entry.Trim(); if (e.Length == 0) continue;
                    int eq = e.IndexOf('=');
                    if (eq < 0) { Plugin.Log.LogError($"[Projectile] override needs '<pawnDefGuid>=<projectileGuid>' (got '{e}')"); continue; }
                    var pawnGuid = ParseGuid4(e.Substring(0, eq));
                    var projGuid = ParseGuid4(e.Substring(eq + 1));
                    if (pawnGuid == null || projGuid == null) { Plugin.Log.LogError($"[Projectile] both sides must be four ints \"a,b,c,d\" (got '{e}')"); continue; }
                    projOverrides.Add((pawnGuid, projGuid, e));
                }
                if (projOverrides.Count > 0) Plugin.Diag($"[Projectile] {projOverrides.Count} override(s) to apply");
            }
            if (projOverrides.Count == 0) return;

            var pawnType = GameBinding.PresentationPawnDefinition;
            var projType = GameBinding.ProjectileAsset;
            if (pawnType == null || projType == null) { Plugin.Log.LogError("[Projectile] ProjectileAsset/PresentationPawnDefinition type not found (game update?)"); return; }
            var projField = AccessTools.Field(pawnType, "Projectile");   // ProjectileAssetReference (declared on the base; AccessTools walks it)
            if (projField == null) { Plugin.Log.LogError("[Projectile] pawn def has no 'Projectile' field (game update?)"); return; }

            foreach (var (pawnGuid, projGuid, raw) in projOverrides)
            {
                var pawnDef = LoadAmpliAsset(pawnType, pawnGuid) as UnityEngine.Object;
                if (pawnDef == null) { Plugin.Log.LogWarning($"[Projectile] pawn def GUID didn't resolve ('{raw}') — check the GUID / that its bundle is loaded."); continue; }
                var proj = LoadAmpliAsset(projType, projGuid) as UnityEngine.Object;
                if (proj == null) { Plugin.Log.LogWarning($"[Projectile] projectile GUID didn't resolve for '{pawnDef.name}' — is Projectile_KamikazeDrone in a BUILT, loaded bundle?"); continue; }
                var pref = projField.GetValue(pawnDef);
                if (pref == null) { pref = Activator.CreateInstance(projField.FieldType); projField.SetValue(pawnDef, pref); }
                var gf = FindGuidField(pref.GetType());
                if (gf == null) { Plugin.Log.LogError("[Projectile] ProjectileAssetReference has no guid field (layout changed?)"); continue; }
                gf.SetValue(pref, projGuid);
                Plugin.Diag($"[Projectile] '{pawnDef.name}'.Projectile -> '{proj.name}'  ({raw})");
            }
        }

        static readonly List<object> propPending = new List<object>();   // parsed GUIDs not yet registered (per-session)
        static bool propParsed; static int propWait; static bool propTickArmed; static int propTick;
        internal static void RearmPropRegistration() { propParsed = false; propPending.Clear(); propTickArmed = true; }   // AnimationLoad cleared the manager's list — register ours again
        static void ParsePropGuidsIFN()
        {
            if (propParsed) return;
            propParsed = true;
            foreach (var part in (Plugin.PropCollectionGuids?.Value ?? "").Split(';'))
            {
                var g = ParseGuidCsv(part.Trim());
                if (g != null) propPending.Add(g);
            }
            if (propPending.Count > 0) Plugin.Diag($"[Props] {propPending.Count} mesh collection(s) to register");
        }

        // Called from Hk_PropRegister's postfix (loud: this is THE moment it must work) and from the Update tick (quiet).
        internal static void RegisterPropCollections(object animationManager, bool loud)
        {
            ParsePropGuidsIFN();
            if (propPending.Count == 0 || animationManager == null) return;
            var amType = animationManager.GetType();
            var mcType = GameBinding.MeshCollection;
            var adb = GameBinding.AssetDatabase;
            var load = adb?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => (m.Name == "TryLoadAsset" || m.Name == "LoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)?.MakeGenericMethod(mcType);
            var reg = amType.GetMethod("RegisterMeshCollection", BindingFlags.Public | BindingFlags.Instance);
            if (load == null || reg == null) { Plugin.Log.LogError("[Props] reflection targets missing (LoadAsset / RegisterMeshCollection) — axis disabled this session."); propPending.Clear(); return; }
            for (int i = propPending.Count - 1; i >= 0; i--)
            {
                var mc = load.Invoke(null, new[] { propPending[i] });
                if (mc == null || (mc is UnityEngine.Object uo && !uo))
                    mc = LoadCollectionFromLoadedBundles(mcType, i);   // Amplitude's catalog misses our MeshCollection (type-specific) — pull it from the mounted Unity bundle by name instead
                if (mc == null || (mc is UnityEngine.Object uo2 && !uo2))
                {
                    if (loud) Plugin.Log.LogError("[Props] mesh collection NOT loadable at AnimationLoad time (GUID catalog miss AND no loaded bundle carries it by name).");
                    else if (++propWait % 600 == 0) Plugin.Log.LogWarning("[Props] a mesh collection isn't loadable yet — retrying.");
                    continue;
                }
                reg.Invoke(animationManager, new[] { mc });   // dedupes internally; also LoadIFNs the meshes into the GPU content manager
                Plugin.Diag($"[Props] registered mesh collection '{(mc as UnityEngine.Object)?.name}'" + (loud ? " (at AnimationLoad — before pawn resolution)" : " (late tick)"));
                propPending.RemoveAt(i);
            }
        }

        // FALLBACK loader: Amplitude's AssetDatabase resolves our FxMesh/Skeleton GUIDs from the community bundle fine,
        // but NOT a MeshCollection (type-specific catalog gap). The bundle itself is a plain Unity AssetBundle the game
        // has already mounted, so load the asset object BY NAME from the loaded bundles — RegisterMeshCollection takes
        // the object, and the collection's internal FxMesh GUID still resolves through the game's own (working) path.
        static object LoadCollectionFromLoadedBundles(Type mcType, int pendingIndex)
        {
            try
            {
                var names = (Plugin.PropCollectionNames?.Value ?? "").Split(';');
                string name = pendingIndex < names.Length ? names[pendingIndex].Trim() : "";
                if (name.Length == 0) return null;
                foreach (var b in UnityEngine.AssetBundle.GetAllLoadedAssetBundles())
                {
                    var a = b.LoadAsset(name);                 // short-name lookup; unique within our bundle
                    if (a != null && mcType.IsInstanceOfType(a)) return a;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Props] bundle-name fallback: " + ex.Message); }
            return null;
        }

        internal static void TickPropRegister()   // safety net: late registration/repair only
        {
            try
            {
                // Until the first AnimationLoad the mod bundle isn't mounted, so every catalog request is a
                // guaranteed miss that LogErrors into the Amplitude diagnostics (64+ red lines per boot). The
                // loud AnimationLoad postfix is the registration moment that works; this tick only repairs
                // late failures after it — armed there, and paced to ~1 attempt/second.
                if (!propTickArmed || (++propTick % 60) != 0) return;
                ParsePropGuidsIFN();
                if (propPending.Count == 0) return;
                var amType = GameBinding.AnimationManager;
                var inst = amType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                           ?? GF(amType, "Instance")?.GetValue(null);
                if (inst != null) RegisterPropCollections(inst, loud: false);
            }
            // The manager exists but its internals aren't built yet (Register throws before its Load) — retry next frame.
            catch (Exception ex) { if (++propWait % 600 == 0) Plugin.Log.LogWarning("[Props] tick (retrying): " + ex.Message); }
        }

        // MODE 2 (custom model): after UpdateLevelBuild loaded the vanilla material, override the main mesh channel with our
        // own baked FxEvolverMaterial via the game's public SetChannel(int layer, Guid, RenderMode, forceRefresh).
        internal static void DistrictGuidOverride(object district)
        {
            try
            {
                if (distGuid == null) return;                          // cheap bail when GUID mode is off
                if (!DistrictMatches(district, out var plbc) || plbc == null) return;
                // main level-build layer is a private static int on PresentationDistrict (= 0); read it, fall back to 0.
                int layer = 0;
                var lf = district.GetType().GetField("mainLevelBuildComponantLayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                if (lf?.GetValue(null) is int li) layer = li;
                var renderMode = GetMember(district, "RenderMode");    // boxed HgFxAnchorComponent.RenderModeEnum
                var setChannel = plbc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "SetChannel" && m.GetParameters().Length == 4
                        && m.GetParameters()[1].ParameterType.Name == "Guid");
                if (setChannel == null) { Plugin.Log.LogError("[District] SetChannel(int,Guid,...) overload not found (game update?)."); distGuid = null; return; }
                setChannel.Invoke(plbc, new object[] { layer, distGuid, renderMode, true });
                if (!distGuidLogged) { distGuidLogged = true; Plugin.Diag($"[District] '{distName}' mesh channel -> our FxEvolverMaterial (layer {layer})"); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] guid override: " + ex); }
        }

        // Diagnostic: dump the LIVE GPU mesh-content buffer usage per content layer. Answers the real scaling question
        // ("how many more models fit"): the Amplitude manager packs every registered skeleton/mesh-collection into a
        // fixed buffer sized 100k verts / 250k indices / 256 meshes PER ContentLayer, tracked by running cursors. Reading
        // those cursors tells us exactly how full each layer is and whether the mod's models are all resident at once or
        // only the active unit types. Bound to a hotkey — press in-game with custom units on the map.
        // Build the live budget readout as lines (shared by the F8 window and the Shift+F8 log dump).
        internal static System.Collections.Generic.List<string> MeshBudgetLines()
        {
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                var amType = GameBinding.AnimationManager;
                var inst = amType != null ? AccessTools.Property(amType, "Instance")?.GetValue(null) : null;
                if (inst == null) { lines.Add("AnimationManager.Instance is null — load a game first."); return lines; }
                var fxMgr = GetMember(inst, "FxComponentMeshContentManager");
                if (fxMgr == null) { lines.Add("FxComponentMeshContentManager is null."); return lines; }
                int pawnLayer = GetMember(inst, "FXMeshLayerIndex") is int pl ? pl : -1;
                if (!(GetMember(fxMgr, "Layers") is Array layers)) { lines.Add("Layers array not found."); return lines; }
                lines.Add($"GPU mesh buffer — {layers.Length} layer(s), pawn layer = {pawnLayer}:");
                for (int i = 0; i < layers.Length; i++)
                {
                    var L = layers.GetValue(i);
                    if (L == null) { lines.Add($"  layer {i}: <null>"); continue; }
                    string nm = GetMember(L, "name") as string ?? "?";
                    int v = ToInt(GetMember(L, "currentVertexIndex")),   vMax = ToInt(GetMember(L, "baseVertexBufferSize"));
                    int x = ToInt(GetMember(L, "currentIndexIndex")),    xMax = ToInt(GetMember(L, "baseIndexBufferSize"));
                    int m = ToInt(GetMember(L, "currentMeshAddedCount")), mMax = ToInt(GetMember(L, "maxMeshCount"));
                    // the PER-MESH ceiling: quads beyond this are SILENTLY dropped at encode (holes in the model). 0 = unlimited.
                    int mt = ToInt(GetMember(L, "maxMeshTriangleCount"));
                    string tag = i == pawnLayer ? "  <-- your models" : "";
                    lines.Add($"  L{i} '{nm}': verts {v:n0}/{vMax:n0} ({Pct(v, vMax)}%) | idx {Pct(x, xMax)}% | meshes {m}/{mMax} | maxTris/mesh {(mt == 0 ? "unlimited" : mt.ToString("n0"))}{tag}");
                }
            }
            catch (Exception ex) { lines.Add("budget read failed: " + ex.Message); }
            return lines;
        }

        internal static void DumpMeshBudget()   // Shift+F8: same readout, to the log
        {
            foreach (var l in MeshBudgetLines()) Plugin.Log.LogInfo("[Budget] " + l);
        }
        static int ToInt(object o) { try { return o == null ? -1 : Convert.ToInt32(o); } catch { return -1; } }
        static int Pct(int a, int b) { return b > 0 ? (int)(100.0 * a / b) : 0; }

        // ---- ATLAS DUMP (retexture aid) ------------------------------------------------------------------------------
        // Dump every currently-loaded unit output-layer atlas (its material's _MainTex) to
        // BepInEx/config/enc_atlas_dump/<layer>.png, so a unit's skin can be found by its layer name and used as a
        // paint canvas (e.g. to make a desaturated "grey" variant of a Common copy). Reuses ApplyTexture's Content walk
        // (Content -> OutputLayerEntries -> OutputLayerInstance) and TickOne's material fields; the host atlas isn't
        // CPU-readable, so each is blitted through a RenderTexture first. One PNG per layer. Bound to the F8 window's
        // "Dump Atlases" button — load a game with the target units visible, then click.
        internal static void DumpOutputLayerAtlases(string filter = null)
        {
            try
            {
                var amType = GameBinding.AnimationManager;
                var mgr = amType != null ? AccessTools.Property(amType, "Instance")?.GetValue(null) : null;
                if (mgr == null) { Plugin.Log.LogWarning("[AtlasDump] AnimationManager.Instance is null — load a game first."); return; }
                var content = GetMember(mgr, "Content");
                var list = content != null ? GetMember(content, "OutputLayerEntries") as Array : null;
                if (list == null) { Plugin.Log.LogWarning("[AtlasDump] no OutputLayerEntries found."); return; }
                string dir = Path.Combine(Paths.ConfigPath, "enc_atlas_dump");
                Directory.CreateDirectory(dir);
                var seen = new HashSet<string>();
                int n = 0;
                foreach (var entry in list)
                {
                    var ol = GetMember(entry, "OutputLayerInstance");
                    if (ol == null) continue;
                    string layer = (ol as UnityEngine.Object)?.name ?? "layer";
                    if (!string.IsNullOrEmpty(filter) && layer.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;   // only this unit (e.g. "Corvette")
                    if (!seen.Add(layer)) continue;   // one dump per layer
                    UnityEngine.Texture tex = null;
                    if (GetMember(ol, "RenderOutputs") is Array ros)
                        foreach (var ro in ros)
                        {
                            foreach (var fld in new[] { "currentRenderMaterial", "runTimeRenderMaterial" })
                                if (GetMember(ro, fld) is UnityEngine.Material mat && mat.GetTexture("_MainTex") is UnityEngine.Texture mt) { tex = mt; break; }
                            if (tex != null) break;
                        }
                    if (tex == null) continue;
                    var png = ToReadablePng(tex);
                    if (png == null) continue;
                    File.WriteAllBytes(Path.Combine(dir, SanitizeFile(layer) + ".png"), png);
                    n++;
                    Plugin.Log.LogInfo($"[AtlasDump] {layer} -> {SanitizeFile(layer)}.png ({tex.width}x{tex.height}, {tex.name})");
                }
                Plugin.Log.LogInfo($"[AtlasDump] wrote {n} atlas PNG(s){(string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'")} to {dir}");
            }
            catch (Exception e) { Plugin.Log.LogError("[AtlasDump] " + e); }
        }

        // Blit any (possibly non-readable / compressed) texture through a RenderTexture into a readable Texture2D and
        // PNG-encode it. PNG (vs TGA) round-trips cleanly with LoadImage — paint on the dumped canvas and the retexture
        // maps back exactly. Uses UnityEngine.ImageConversionModule (also referenced for the retexture skin-load).
        static byte[] ToReadablePng(UnityEngine.Texture src)
        {
            try
            {
                int w = src.width, h = src.height;
                var rt = UnityEngine.RenderTexture.GetTemporary(w, h, 0, UnityEngine.RenderTextureFormat.ARGB32, UnityEngine.RenderTextureReadWrite.sRGB);
                var prev = UnityEngine.RenderTexture.active;
                UnityEngine.Graphics.Blit(src, rt);
                UnityEngine.RenderTexture.active = rt;
                var t = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGBA32, false);
                t.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0); t.Apply();
                UnityEngine.RenderTexture.active = prev; UnityEngine.RenderTexture.ReleaseTemporary(rt);
                var png = UnityEngine.ImageConversion.EncodeToPNG(t);   // static form: no `using UnityEngine;` in this file
                UnityEngine.Object.DestroyImmediate(t);
                return png;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[AtlasDump] readable copy failed for '" + (src != null ? src.name : "?") + "': " + e.Message); return null; }
        }

        static string SanitizeFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return "layer";
            var sb = new System.Text.StringBuilder();
            foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return sb.ToString();
        }
    }
}
