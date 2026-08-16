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
        // One custom district model, from the haf_districts.json registry (written by the District Factory window).
        // Runtime state lives here too, so any number of districts can carry custom models simultaneously.
        internal class DistrictModel
        {
            public string district = "";     // ConstructibleDefinitionName to match
            public object fxMeshGuid;        // parsed Amplitude Guid of the baked FxMesh
            public object atlasGuid;         // parsed Amplitude Guid of the baked albedo atlas (null = untextured, pre-2.0 entries)
            public object footprintDonor;   // parsed Guid of the runtime strategic-footprint donor selector (registry-driven), null = none
            public object selectorGuid;      // parsed Guid of this district's baked SCOPED CityMapSelector (data-authored path). Non-null -> routed through the scoped path (like the reactor), NOT the isolate/repoint path.
            public object normalAtlasGuid;   // baked normal atlas (null = neutral flat) — same rects as the albedo
            public object roughAtlasGuid;    // baked roughness atlas (null = neutral matte)
            public bool isolate = true;      // true = private per-instance leaf (this tile only); false = global shared-leaf swap
            public string groundMaterial = ""; // per-entry terrain paint (GroundMaterialDefinition name, e.g. Prairie_Grassland) — "" falls back to the global DistrictGroundMaterial config
            public int groundIdx = int.MinValue; // resolved ground-material index cache (MinValue = unresolved, -1 = name not found)
            public bool groundApplied;           // ground paint applied once this session (re-applying restarts the blend → never settles)
            public string hexSculpt = "";    // per-entry hexagon sculpting (HexagonSculptingDefinition name) — the raised platform + strategic footprint; "" falls back to the global DistrictHexSculpt config
            public int hexIdx = int.MinValue; // resolved hexagon-sculpting index cache
            // MESH strategic footprint (per-entry, authored in the District Factory). footprintMesh=false -> the global
            // DistrictFootprintMesh… config stays in charge for the scoped district; true -> these values are authoritative.
            public bool footprintMesh, footprintMeshBW, footprintMeshFlat;
            public bool footprintMeshHideDecal = true;
            public float footprintMeshFlatHeight = 0.17f;
            // runtime — PER-INSTANCE targeting: a district can be BUILT ON MANY TILES (one PresentationDistrict each);
            // the old single plbc slot made ownership ping-pong between instances (only the most recently updated tile
            // showed the custom model — the review's architectural finding). The private leaf + layer clone + texture
            // bindings stay ONE PER ENTRY and are shared: a leaf is just a material, and the game's own shared
            // selectors are pointed to by many channels at once. Only the channels we repoint are per-tile.
            public class TileState { public object plbc; public int layer; public bool pointedLogged; public int wait; }
            public readonly List<TileState> tiles = new List<TileState>();
            public object privateLeaf;                                   // isolate mode: the Instantiated leaf (SHARED by all tiles)
            public System.Type selectorType;                             // the close-up level-build selector's type; defensive: only re-assert our leaf over THAT, don't fight a foreign material the game may put on the channel
            // FOOTPRINT preservation (isolate mode): isolate replaces channel [0]'s selector (building + decals) with our
            // single leaf, amputating the footprint decals. Re-host a CLONE of the original selector on an empty channel so
            // its decals draw at strategic zoom. origSelector captured when the leaf is first built (pre-replacement).
            public object origSelector;                                  // the native channel-[0] selector, captured before we replaced it
            public object decalSelector;                                 // the clone re-hosted on a free channel to keep the footprint
            public int decalChannel = -1; public bool decalLogged, decalGaveUp;
            // DEEP-CLONE mode (footprint fix): a fully-private copy of channel [0]'s selector tree — every non-decal node
            // Instantiated once (memoized), building ELEMENT leaves' fxMesh swapped to ours, DECAL leaves left shared. Put
            // on channel [0] so our mesh + the surviving footprint decals both render, scoped to this tile only.
            public object clonedSelector; public System.Collections.Generic.Dictionary<object, object> cloneMap;
            public bool cloneLogged; public int cloneReassert;
            public object deepLayer;   // deep-clone: ONE private FxOutputLayer shared by every swapped reactor element (our albedo bound on it)
            public int domeCounter;    // deep-clone: running count of building-slot emissions, for thinning the reactor-dome count
            public readonly List<object> leaves = new List<object>();    // global mode: collected shared leaves
            public bool collected, matchLogged;
            // texture injection runtime (isolate mode)
            public UnityEngine.Texture2D texAlbedo;                      // our baked albedo, bound on the PRIVATE output layer
            public UnityEngine.Texture2D texNormal, texRough;            // baked surface maps (null = the neutral stand-ins)
            public bool texApplied; public int texWait;
            public int texErrors;                                        // bounded retry: a transient exception must not permanently kill texture (the old catch latched texApplied on first throw)
            // PERF: the (runtime material, texture property) slots the albedo is bound on, captured by the first full
            // walk — the periodic re-assert then compares one reference per slot instead of re-discovering properties
            // (GetTexturePropertyNames allocates a string[] per material per call). Cleared when a material dies
            // (res switch rebuilds the layer's materials) or on session reset — the full walk then runs again.
            public readonly List<(UnityEngine.Material mat, string prop)> boundSlots = new List<(UnityEngine.Material, string)>();
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
                var regPath = Path.Combine(Paths.ConfigPath, "haf_districts.json");
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
                                footprintDonor = ParseGuid4((string)d["footprintDonor"] ?? ""),   // registry-driven strategic-footprint donor (null = none)
                                // SPIKE: DistrictIsolate config (default true) now OVERRIDES the JSON when set false, so we
                                // can force a registry entry into TRUE global mode for the footprint test. Without this the
                                // reactor's JSON isolate=true silently won every "global" test. Otherwise honour the JSON.
                                isolate = (Plugin.DistrictIsolate != null && !Plugin.DistrictIsolate.Value) ? false : ((bool?)d["isolate"] ?? true),
                                groundMaterial = (string)d["groundMaterial"] ?? "",
                                hexSculpt = (string)d["hexSculpt"] ?? "",
                                footprintMesh = (bool?)d["footprintMesh"] ?? false,
                                footprintMeshBW = (bool?)d["footprintMeshBW"] ?? false,
                                footprintMeshFlat = (bool?)d["footprintMeshFlat"] ?? false,
                                footprintMeshFlatHeight = (float?)d["footprintMeshFlatHeight"] ?? 0.17f,
                                footprintMeshHideDecal = (bool?)d["footprintMeshHideDecal"] ?? true,
                                selectorGuid = ParseGuid4((string)d["selectorGuid"] ?? ""),   // baked scoped CityMapSelector -> the scoped rendering path
                            };
                            if (e.district.Length > 0 && e.fxMeshGuid != null) distModels.Add(e);
                            else Plugin.Log.LogWarning($"[District] registry entry skipped (district='{e.district}', bad fxMeshGuid?)");
                        }
                        Plugin.Log.LogInfo($"[District] registry: {distModels.Count} district model(s) from haf_districts.json");
                    }
                    catch (Exception rex) { Plugin.Log.LogError("[District] haf_districts.json parse: " + rex); }
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
        static bool UseDeepClone = false;   // SPIKE: deep-clone footprint hack — PARKED. The proper path is a data-authored district visual (see District-Dedicated-Visual-Feasibility.md), not runtime selector surgery.
        static float DeepCloneBuildingMinSize = 0.35f;   // deep-clone: swap building slots this big (bbox max dim) to our mesh; hide smaller props
        static int DeepCloneKeepEvery = 1;               // deep-clone: keep 1 in N large building slots as our reactor (1 = swap ALL large, no thinning → no mid-zoom gaps)
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
                // FALSIFIED (footprint cheap shot, 08-09): forcing a small bbox (useCustomBBox + Bounds 0.15^3) on the
                // swapped leaves did NOT summon the footprint at strategic zoom. So the element->decal selection is NOT
                // driven by the element's BBoxMin/BBoxMax the way we hoped — bbox is ruled out as the gate.
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
                    lines.Add($"  '{e.district}' isolate={e.isolate} tiles={e.tiles.Count} leaf={(leaf != null ? "meshIndex=" + mi : "not built")} | {meshInfo}");
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
                TrackDistrictClone(clone);   // OWN it — freed on the next session reset (leak fix)
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
                        TrackDistrictClone(layerClone);   // the leaf's private output-layer clone — OWN it (leak fix)
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
                        // RAISE THE PER-MESH PRIMITIVE CEILING (the grove fix). A district mesh renders as sub-particles:
                        // count = ceil(primitiveCount / PrimitivePerParticleCount), packed into 8 bits -> HARD-CLAMPED at
                        // 255 (GetEncodedMeshAndVisualParticleCount). Above it the extra primitives are silently NOT DRAWN
                        // (the 4-tree grove: temple + 1 tree rendered, the rest dropped). The mesh is fully STORED
                        // (FillMeshVertexAndBufferContent ignores PPC — encoding is complete); only the render clamp bites.
                        // PPC is a per-LAYER value and this is our private clone, so multiplying it lifts the ceiling
                        // (255 x PPC) with the SAME total GPU work — fewer particles, each covering more primitives. No
                        // re-bake needed. Config DistrictMeshDensityBoost (default 8 -> ~8x headroom); 0/1 = vanilla.
                        int boost = Plugin.DistrictMeshDensityBoost != null ? Plugin.DistrictMeshDensityBoost.Value : 8;
                        if (boost > 1)
                        {
                            var ppcF = layerClone.GetType().GetField("primitivePerParticleCount", BF);
                            if (ppcF?.GetValue(layerClone) is int ppc && ppc > 0)
                            {
                                ppcF.SetValue(layerClone, ppc * boost);
                                Plugin.Diag($"[DistrictTex] private layer primitivePerParticleCount {ppc} -> {ppc * boost} (per-mesh ceiling now ~{255L * ppc * boost} primitives; the grove fix)");
                            }
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
        // PERF: reflection handles for the per-frame hot path — the types are stable within a session, so resolve
        // once and reuse (AccessTools.Field/GetMethods re-walk the type's members on every call otherwise).
        static FieldInfo fiPlbcChannels, fiChanEvolverMaterial;
        static MethodInfo miRefreshChannel; static object[] refreshArgs;
        static readonly HashSet<string> distBackoffLogged = new HashSet<string>();   // one-shot log per district when we back off a foreign channel material

        // ISOLATE mode, per TILE: keep this instance's channel pointed at the entry's (shared) private leaf +
        // re-spawned particle. Re-applied each frame (the game reloads the shared selector into the channel on
        // UpdateLevelBuild). The leaf builds lazily ONCE per entry, sourced from whichever tile's selector loads first.
        static void PointTileAtPrivateLeaf(DistrictModel e, DistrictModel.TileState t)
        {
            try
            {
                if (t.plbc == null) return;
                if (fiPlbcChannels == null) fiPlbcChannels = AccessTools.Field(t.plbc.GetType(), "channels");
                if (!(fiPlbcChannels?.GetValue(t.plbc) is Array channels) || t.layer >= channels.Length) return;
                var box = channels.GetValue(t.layer);
                if (fiChanEvolverMaterial == null) fiChanEvolverMaterial = GF(box.GetType(), "evolverMaterial");
                var evf = fiChanEvolverMaterial;
                if (evf == null) return;
                // build the private leaf lazily — the selector's sub-materials load async, so retry until they're ready.
                if (e.privateLeaf == null)
                {
                    var sel = evf.GetValue(box);
                    if (sel == null) return;
                    if (e.selectorType == null) e.selectorType = sel.GetType();   // the close-up selector we're allowed to override
                    if (e.origSelector == null && sel is UnityEngine.Object) e.origSelector = sel;   // capture BEFORE we replace it (footprint source)
                    e.privateLeaf = BuildPrivateLeaf(sel, e.fxMeshGuid, e.atlasGuid);
                    // WONDER path: a database-fed selector (fillMode LevelBuildDatabase) has no inline leaves to walk —
                    // source them from the PLUGIN-LOADED template material instead (swap-first sequencing: the wonder's
                    // repository cell stays empty until this swap is live, so the template is never drawn on the tile).
                    if (e.privateLeaf == null)
                    {
                        var wm = WonderTemplate(e.district);
                        if (wm != null) e.privateLeaf = BuildPrivateLeaf(wm, e.fxMeshGuid, e.atlasGuid, instantAppear: true);
                    }
                    if (e.privateLeaf == null) { if (t.wait++ % 300 == 0) Plugin.Diag($"[District] '{e.district}': waiting for leaves to load..."); return; }
                }
                var curMat = evf.GetValue(box);
                if (ReferenceEquals(curMat, e.privateLeaf)) return;   // already ours this frame
                // DEFENSIVE: only re-assert our leaf over the close-up SELECTOR the game resets to; never fight a foreign
                // material it might put on the channel. (Measured on the reactor: its channel is only ever selector ->
                // our leaf and never swaps at zoom, so this doesn't fire there — the clean zoom-out disappear comes from
                // the Base_Industry AFFINITY, not this. Kept as a safe guard for districts whose channel DOES swap.)
                if (curMat != null && e.selectorType != null && curMat.GetType() != e.selectorType) return;
                evf.SetValue(box, e.privateLeaf);
                channels.SetValue(box, t.layer);   // write the mutated struct back into the array
                // re-spawn the particle so PatchParticle picks up the private leaf's MaterialIndex
                if (miRefreshChannel == null)
                {
                    miRefreshChannel = t.plbc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(m => m.Name == "RefreshChannel" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                    if (miRefreshChannel != null)
                        refreshArgs = new object[] { 0, System.Enum.ToObject(miRefreshChannel.GetParameters()[1].ParameterType, 0) };
                }
                if (miRefreshChannel != null) { refreshArgs[0] = t.layer; miRefreshChannel.Invoke(t.plbc, refreshArgs); }
                if (!t.pointedLogged) { t.pointedLogged = true; Plugin.Diag($"[District] '{e.district}' ISOLATED: channel {t.layer} -> the private leaf (this tile only)."); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] point channel: " + ex); }
        }

        // Load a freshly-Instantiated FxEvolverMaterial (reset load state so LoadIFN actually re-runs and rebuilds its
        // private runtime tree). Mirrors the load tail of BuildPrivateLeaf.
        static void LoadFxMaterial(object mat)
        {
            if (mat == null || distFxManager == null) return;
            var t = mat.GetType();
            var ls = GF(t, "loadingStatus"); if (ls != null) ls.SetValue(mat, System.Enum.ToObject(ls.FieldType, 0));
            if (fxNextDoublon == null) fxNextDoublon = GameBinding.FxEvolverMaterial?.GetMethod("NextDoublonAvoidanceIndex", BindingFlags.Static | BindingFlags.Public);
            uint doublon = fxNextDoublon != null ? (uint)fxNextDoublon.Invoke(null, null) : 0u;
            var loadIFN = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "LoadIFN" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType.Name.Contains("FxManager"));
            if (loadIFN != null)
            {
                var pars = loadIFN.GetParameters();
                var args = pars.Length == 1 ? new[] { distFxManager } : new object[] { distFxManager, doublon };
                loadIFN.Invoke(mat, args);
            }
        }

        // FOOTPRINT preservation (isolate mode): our private leaf on channel [0] loses the decal subtree the native
        // selector carried. Re-host a CLONE of that native selector on a FREE (null) channel so its decals still draw —
        // at strategic zoom the clone's building elements demote and only the footprint decals remain. (First cut: the
        // clone still carries its buildings, so close-up may show the donor buildings behind our reactor; if so, the next
        // step neutralizes the clone's building leaves and keeps only the decals.)
        static void PreserveFootprintChannel(DistrictModel e, DistrictModel.TileState t)
        {
            try
            {
                if (e.decalGaveUp || e.origSelector == null || t.plbc == null) return;
                if (fiPlbcChannels == null) fiPlbcChannels = AccessTools.Field(t.plbc.GetType(), "channels");
                if (!(fiPlbcChannels?.GetValue(t.plbc) is Array channels)) return;
                if (e.decalSelector == null)
                {
                    if (!(e.origSelector is UnityEngine.Object selUO) || selUO == null) { e.decalGaveUp = true; return; }
                    var clone = UnityEngine.Object.Instantiate(selUO);
                    TrackDistrictClone(clone);   // footprint decal-selector clone — OWN it (leak fix)
                    clone.name = selUO.name + "_HAFfoot";
                    LoadFxMaterial(clone);
                    e.decalSelector = clone;
                    Plugin.Diag($"[District] '{e.district}': cloned footprint selector '{clone.name}'.");
                }
                // pick a free (null-material) channel to host the footprint, once
                if (e.decalChannel < 0)
                {
                    for (int i = 0; i < channels.Length; i++)
                    {
                        var b = channels.GetValue(i); if (b == null) continue;
                        if (fiChanEvolverMaterial == null) fiChanEvolverMaterial = GF(b.GetType(), "evolverMaterial");
                        if (fiChanEvolverMaterial?.GetValue(b) == null) { e.decalChannel = i; break; }
                    }
                    if (e.decalChannel < 0) { e.decalGaveUp = true; Plugin.Diag($"[District] '{e.district}': no free channel for footprint — gave up."); return; }
                }
                var box = channels.GetValue(e.decalChannel); if (box == null) { e.decalGaveUp = true; return; }
                var ef = fiChanEvolverMaterial; if (ef == null) return;
                if (ReferenceEquals(ef.GetValue(box), e.decalSelector)) return;   // already hosted this frame
                ef.SetValue(box, e.decalSelector);
                channels.SetValue(box, e.decalChannel);
                if (miRefreshChannel == null)
                {
                    miRefreshChannel = t.plbc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(m => m.Name == "RefreshChannel" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                    if (miRefreshChannel != null) refreshArgs = new object[] { 0, System.Enum.ToObject(miRefreshChannel.GetParameters()[1].ParameterType, 0) };
                }
                if (miRefreshChannel != null) { refreshArgs[0] = e.decalChannel; miRefreshChannel.Invoke(t.plbc, refreshArgs); }
                if (!e.decalLogged) { e.decalLogged = true; Plugin.Diag($"[District] '{e.district}': footprint selector hosted on channel {e.decalChannel}."); }
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] preserve footprint: " + ex); }
        }

        // Clone an FxOutputLayer private (the texture-injection layer): opt out of hi-res streaming (null the mid/high
        // material GUIDs so the game never stomps our binding) and raise the per-mesh primitive ceiling. Same recipe as
        // BuildPrivateLeaf's inline layer clone, factored out so the deep-clone reactor elements can share ONE such layer.
        static UnityEngine.Object ClonePrivateOutputLayer(UnityEngine.Object srcLayer)
        {
            var layerClone = UnityEngine.Object.Instantiate(srcLayer);
            TrackDistrictClone(layerClone);   // deepLayer / scoped donorClone — OWN it (leak fix)
            layerClone.name = srcLayer.name + "_HAF";
            if (GetMember(layerClone, "RenderOutputs") is Array ros)
                foreach (var ro in ros)
                    foreach (var gn in new[] { "midResMaterialGuid", "highResMaterialGuid" })
                    { var gf2 = ro?.GetType().GetField(gn, BF); if (gf2 != null) gf2.SetValue(ro, Activator.CreateInstance(gf2.FieldType)); }
            int boost = Plugin.DistrictMeshDensityBoost != null ? Plugin.DistrictMeshDensityBoost.Value : 8;
            if (boost > 1)
            {
                var ppcF = layerClone.GetType().GetField("primitivePerParticleCount", BF);
                if (ppcF?.GetValue(layerClone) is int ppc && ppc > 0) ppcF.SetValue(layerClone, ppc * boost);
            }
            return layerClone;
        }

        // DEEP CLONE (footprint fix): recursively Instantiate a fully-private copy of the selector tree. Every non-decal
        // node is cloned once (memoized by original, so a leaf shared N times → one private clone repointed everywhere);
        // building ELEMENT leaves get our fxMesh; DECAL leaves are kept SHARED (unmodified — that's the footprint). Loaded
        // child references (selector cache Entries[].FxMaterial, emitter levelBuildItems[].loadedEvolverMaterial) are
        // repointed at the clones. Load runs per node so element meshIndex re-resolves and selector caches build first.
        static object DeepCloneMat(DistrictModel e, object mat, object fxGuid, System.Collections.Generic.Dictionary<object, object> map, int depth)
        {
            if (mat == null || depth > 10) return mat;
            if (map.TryGetValue(mat, out var done)) return done;
            if (!(mat is UnityEngine.Object uo) || uo == null) { map[mat] = mat; return mat; }
            if (mat.GetType().Name.Contains("Decal")) { map[mat] = mat; return mat; }   // keep the footprint decals shared/unmodified
            var clone = UnityEngine.Object.Instantiate(uo);
            TrackDistrictClone(clone);   // deep-clone material node — OWN it (leak fix)
            map[mat] = clone;
            var t = clone.GetType();
            bool swappedReactor = false;
            if (t.Name.Contains("BuildElement"))
            {
                // The donor district emits MANY building slots (factory sprawl). Swap the LARGE slots (main buildings) to
                // our reactor mesh, HIDE the small props (size -> 0). Reactor slots share ONE private output layer so our
                // albedo (bound by DistrictApplyTexture) textures them all — otherwise they'd wear the donor's sheet (the
                // garbled multi-colour look).
                float maxDim = 0f;
                var bboxV = GF(t, "bbox")?.GetValue(clone);
                if (bboxV != null) { try { if (bboxV.GetType().GetProperty("size", BF)?.GetValue(bboxV) is UnityEngine.Vector3 sz) maxDim = Math.Max(sz.x, Math.Max(sz.y, sz.z)); } catch { } }
                // Thin the reactor count PROPORTIONALLY: the ~349 distinct donor slots are spread across the district, so a
                // walk-order cap kept the wrong (off-hex) ones. Instead keep every DeepCloneKeepEvery-th LARGE slot as our
                // reactor and HIDE the rest (size -> 0) — the visible subset thins evenly. Small props always hidden.
                bool bigEnough = maxDim >= DeepCloneBuildingMinSize;
                if (bigEnough && (++e.domeCounter % DeepCloneKeepEvery) == 0)
                {
                    (GF(t, "fxMesh") ?? GF(t, "mesh"))?.SetValue(clone, fxGuid);
                    swappedReactor = true;
                    if (e.atlasGuid != null)
                    {
                        var olF = GF(t, "outputLayer");
                        if (e.deepLayer == null && olF?.GetValue(clone) is UnityEngine.Object src && src != null) e.deepLayer = ClonePrivateOutputLayer(src);
                        if (e.deepLayer != null) olF?.SetValue(clone, e.deepLayer);
                        if (e.privateLeaf == null) e.privateLeaf = clone;   // representative leaf: DistrictApplyTexture/BindAlbedo bind our albedo on e.deepLayer
                    }
                }
                else GF(t, "size")?.SetValue(clone, UnityEngine.Vector3.zero);   // thinned-out large slot, or a small prop -> hide
            }
            LoadFxMaterial(clone);   // element: re-resolve meshIndex from our mesh; selector/emitter: build the cache from GUIDs
            if (swappedReactor && e.deepLayer != null) GF(t, "textureIndex")?.SetValue(clone, 1);   // sample the full-texture slot [0,1] -> our bound sheet
            var cache = GF(t, "fxMaterialCacheEntries")?.GetValue(clone);
            if (cache != null && GF(cache.GetType(), "Entries")?.GetValue(cache) is Array entries)
                for (int i = 0; i < entries.Length; i++)
                {
                    var en = entries.GetValue(i); if (en == null) continue;
                    var fmF = GF(en.GetType(), "FxMaterial"); var child = fmF?.GetValue(en);
                    if (child != null) { fmF.SetValue(en, DeepCloneMat(e, child, fxGuid, map, depth + 1)); entries.SetValue(en, i); }
                }
            if (GF(t, "levelBuildItems")?.GetValue(clone) is Array items)
                for (int i = 0; i < items.Length; i++)
                {
                    var it = items.GetValue(i); if (it == null) continue;
                    var lmF = GF(it.GetType(), "loadedEvolverMaterial"); var child = lmF?.GetValue(it);
                    if (child != null) { lmF.SetValue(it, DeepCloneMat(e, child, fxGuid, map, depth + 1)); items.SetValue(it, i); }
                }
            return clone;
        }

        // Reload/async defense: the selector resolves its `pairs` variants into the cache ASYNCHRONOUSLY — many building
        // elements land AFTER the initial DeepCloneMat walk, so they stay shared donor (the mid-zoom LOD leak: 408 donor
        // meshes vs 75 of ours). Walk the clone and for EVERY resolved child, ensure it's our private clone: memoized ones
        // are repointed instantly, newcomers are DeepCloneMat'd on the spot (clone + swap/hide + thin, all memoized so each
        // distinct element is done once). Also re-asserts if the game rebuilds a cache from GUIDs.
        static void EnsurePrivate(DistrictModel e, object mat, object fxGuid, System.Collections.Generic.Dictionary<object, object> map, HashSet<object> visited, int depth)
        {
            if (mat == null || depth > 10 || !visited.Add(mat)) return;
            var t = mat.GetType();
            var cache = GF(t, "fxMaterialCacheEntries")?.GetValue(mat);
            if (cache != null && GF(cache.GetType(), "Entries")?.GetValue(cache) is Array entries)
                for (int i = 0; i < entries.Length; i++)
                {
                    var en = entries.GetValue(i); if (en == null) continue;
                    var fmF = GF(en.GetType(), "FxMaterial"); var child = fmF?.GetValue(en);
                    if (child == null) continue;
                    var repl = map.TryGetValue(child, out var cl) ? cl : DeepCloneMat(e, child, fxGuid, map, depth + 1);
                    if (repl != null && !ReferenceEquals(child, repl)) { fmF.SetValue(en, repl); entries.SetValue(en, i); }
                    EnsurePrivate(e, repl ?? child, fxGuid, map, visited, depth + 1);
                }
            if (GF(t, "levelBuildItems")?.GetValue(mat) is Array items)
                for (int i = 0; i < items.Length; i++)
                {
                    var it = items.GetValue(i); if (it == null) continue;
                    var lmF = GF(it.GetType(), "loadedEvolverMaterial"); var child = lmF?.GetValue(it);
                    if (child == null) continue;
                    var repl = map.TryGetValue(child, out var cl) ? cl : DeepCloneMat(e, child, fxGuid, map, depth + 1);
                    if (repl != null && !ReferenceEquals(child, repl)) { lmF.SetValue(it, repl); items.SetValue(it, i); }
                    EnsurePrivate(e, repl ?? child, fxGuid, map, visited, depth + 1);
                }
        }

        // ISOLATE + deep-clone: point this tile's channel [0] at a fully-private clone of the selector (our building mesh +
        // the surviving footprint decals), re-asserted per frame; periodic RepointFromMemo defends against reloads.
        static void PointTileAtClonedSelector(DistrictModel e, DistrictModel.TileState t)
        {
            try
            {
                if (t.plbc == null) return;
                if (fiPlbcChannels == null) fiPlbcChannels = AccessTools.Field(t.plbc.GetType(), "channels");
                if (!(fiPlbcChannels?.GetValue(t.plbc) is Array channels) || t.layer >= channels.Length) return;
                var box = channels.GetValue(t.layer);
                if (fiChanEvolverMaterial == null) fiChanEvolverMaterial = GF(box.GetType(), "evolverMaterial");
                var evf = fiChanEvolverMaterial; if (evf == null) return;
                if (e.clonedSelector == null)
                {
                    var sel = evf.GetValue(box); if (sel == null) return;
                    if (e.selectorType == null) e.selectorType = sel.GetType();
                    if (!(sel is UnityEngine.Object)) return;
                    e.cloneMap = new System.Collections.Generic.Dictionary<object, object>();
                    var cl = DeepCloneMat(e, sel, e.fxMeshGuid, e.cloneMap, 0);
                    if (cl == null || ReferenceEquals(cl, sel)) { if (t.wait++ % 300 == 0) Plugin.Diag($"[District] '{e.district}': deep-clone not ready, retry..."); return; }
                    e.clonedSelector = cl;
                    Plugin.Diag($"[District] '{e.district}': deep-cloned selector — {e.cloneMap.Count} node(s) privatized.");
                }
                var curMat = evf.GetValue(box);
                if (!ReferenceEquals(curMat, e.clonedSelector))
                {
                    if (curMat != null && e.selectorType != null && curMat.GetType() != e.selectorType) return;   // don't fight a foreign material
                    evf.SetValue(box, e.clonedSelector);
                    channels.SetValue(box, t.layer);
                    if (miRefreshChannel == null)
                    {
                        miRefreshChannel = t.plbc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            .FirstOrDefault(m => m.Name == "RefreshChannel" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                        if (miRefreshChannel != null) refreshArgs = new object[] { 0, System.Enum.ToObject(miRefreshChannel.GetParameters()[1].ParameterType, 0) };
                    }
                    if (miRefreshChannel != null) { refreshArgs[0] = t.layer; miRefreshChannel.Invoke(t.plbc, refreshArgs); }
                    if (!e.cloneLogged) { e.cloneLogged = true; Plugin.Diag($"[District] '{e.district}' DEEP-CLONE: channel {t.layer} -> private selector (footprint preserved)."); }
                }
                // frequent early (async variants still resolving), then sparse once stable
                int every = e.cloneReassert < 900 ? 15 : 120;
                // EnsurePrivate DISABLED for the un-thinned mid-zoom test: it tanked perf and didn't reach the pairs-resolved
                // mid-LOD anyway. This isolates whether the mid-zoom donor is inherent (pairs LOD) or was a thinning artifact.
                // if (++e.cloneReassert % every == 0) EnsurePrivate(e, e.clonedSelector, e.fxMeshGuid, e.cloneMap, new HashSet<object>(), 0);
                _ = every;
            }
            catch (Exception ex) { Plugin.Log.LogError("[District] cloned selector: " + ex); }
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
                if (e.tiles.Count == 0) return;
                var plbc = e.tiles[0].plbc; int layer = e.tiles[0].layer;   // shared leaves: any live instance's channel will do
                if (plbc == null) return;
                if (!e.collected)
                {
                    if (!(AccessTools.Field(plbc.GetType(), "channels")?.GetValue(plbc) is Array channels) || layer >= channels.Length) return;
                    var mat = GF(channels.GetValue(layer).GetType(), "evolverMaterial")?.GetValue(channels.GetValue(layer));
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

        // ---- DEDICATED-VISUAL HYBRID: force a district re-resolve after the */District/Main cell fill lands ----
        static readonly List<object> trackedDistricts = new List<object>();   // live district instances (for the replay)
        static object lastLevelBuildEvent;            // the real HgFxAnchorComponent.EventNameEnum arg the game passes
        static bool haveLevelBuildEvent;              // guard: only replay once we've captured a genuine arg value
        static bool inForcedReresolve;                // re-entry guard while WE re-invoke UpdateLevelBuild
        static MethodInfo miUpdateLevelBuild;         // cached UpdateLevelBuild(EventNameEnum)
        internal static void CaptureLevelBuildEvent(object ev)
        {
            if (inForcedReresolve || ev == null) return;   // don't capture our own replayed arg (identical, but be safe)
            lastLevelBuildEvent = ev; haveLevelBuildEvent = true;
        }

        // Replay UpdateLevelBuild on every tracked district so the game re-reads the now-filled */District/Main cell and
        // loads OUR selector. Called ONCE after PollDistrictMainRows reports a successful fill. Uses the captured real
        // event arg so we drive the exact resolution path the game uses itself. Re-entrant-safe via inForcedReresolve.
        internal static void ForceDistrictReresolve()
        {
            if (inForcedReresolve || !haveLevelBuildEvent || trackedDistricts.Count == 0) return;
            try
            {
                inForcedReresolve = true;
                var snapshot = trackedDistricts.ToArray();   // Postfix re-adds are skipped by the guard, but snapshot anyway
                int ok = 0, dead = 0;
                foreach (var d in snapshot)
                {
                    if (d is UnityEngine.Object uo && uo == null) { dead++; continue; }
                    if (miUpdateLevelBuild == null)
                        miUpdateLevelBuild = d.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "UpdateLevelBuild" && m.GetParameters().Length == 1);
                    if (miUpdateLevelBuild == null) break;
                    try { miUpdateLevelBuild.Invoke(d, new[] { lastLevelBuildEvent }); ok++; }
                    catch (Exception ex) { Plugin.Diag("[DistrictMain] re-resolve invoke failed: " + ex.Message); }
                }
                Plugin.Log.LogInfo($"[DistrictMain] forced re-resolve on {ok} district(s) ({dead} dead) via UpdateLevelBuild({lastLevelBuildEvent}) — cells re-read, our selector should load now.");
            }
            catch (Exception ex) { Plugin.Log.LogError("[DistrictMain] force re-resolve: " + ex); }
            finally { inForcedReresolve = false; }
        }

        // DEDICATED-VISUAL HYBRID, final piece: our data-authored selector renders its footprint DECALS (they carry
        // bundle output-layers) but its ONE building element was baked with outputLayer=null (FxOutputLayer is an
        // un-authorable bundle asset — we could not serialize a reference). So the mesh draws nothing. Here we find our
        // element (the ONLY leaf with a null outputLayer — every vanilla leaf has one), borrow a real BUILDING output
        // layer from a vanilla leaf, clone it PRIVATE, bind it on, and re-Load so meshIndex + outputLayerIndex resolve.
        // PER-DISTRICT (onlyName != null): targets come ONLY from `onlyName`'s tiles and bind to THIS district's own clone
        // (S.donorClone), so a 2nd scoped district gets its own layer instead of sharing the first's. `onlyName` is the
        // current S's district; gated by S.donorClone (bound once per district). onlyName == null = the legacy shared
        // DistrictMainRows path (one selector for a whole affinity, no per-district S) — bind all targets once. The donor
        // layer is borrowed from ANY vanilla building (our scoped selector has none — its leaves are our element + decals).
        static bool reactorBoundGlobal;   // legacy DistrictMainRows (onlyName==null) once-flag
        internal static bool BindReactorBuilding(string onlyName)
        {
            if ((onlyName == null ? reactorBoundGlobal : S.donorClone != null) || distFxManager == null || trackedDistricts.Count == 0) return false;
            try
            {
                object donorLayer = null;                          // a vanilla building's outputLayer (non-null)
                var targets = new List<object>();                  // our null-outputLayer element(s)
                var refreshPlbcs = new List<object>();             // plbcs hosting a target, for RefreshChannel
                var visited = new HashSet<object>();
                if (fiPlbcChannels == null && trackedDistricts.Count > 0)
                {
                    var p0 = (fiDistrictPlbc ?? (fiDistrictPlbc = AccessTools.Field(trackedDistricts[0].GetType(), "presentationLevelBuildComponent")))?.GetValue(trackedDistricts[0]);
                    if (p0 != null) fiPlbcChannels = AccessTools.Field(p0.GetType(), "channels");
                }
                foreach (var d in trackedDistricts)
                {
                    if (d is UnityEngine.Object duo && duo == null) continue;
                    var nm = GetMember(d, "ConstructibleDefinitionName")?.ToString();   // only THIS district contributes targets
                    var plbc = (fiDistrictPlbc ?? (fiDistrictPlbc = AccessTools.Field(d.GetType(), "presentationLevelBuildComponent")))?.GetValue(d);
                    if (plbc == null || !(fiPlbcChannels?.GetValue(plbc) is Array channels)) continue;
                    int layer = mainLayerCached >= 0 ? mainLayerCached : 0;
                    if (layer >= channels.Length) continue;
                    var box = channels.GetValue(layer);
                    if (fiChanEvolverMaterial == null) fiChanEvolverMaterial = GF(box.GetType(), "evolverMaterial");
                    var sel = fiChanEvolverMaterial?.GetValue(box);
                    if (sel == null) continue;
                    var leaves = new List<object>();
                    CollectLeaves(sel, leaves, 0, visited);
                    bool thisPlbcHasTarget = false;
                    foreach (var leaf in leaves)
                    {
                        var olF = GF(leaf.GetType(), "outputLayer"); if (olF == null) continue;
                        var ol = olF.GetValue(leaf) as UnityEngine.Object;
                        if (ol == null) { if (onlyName == null || nm == onlyName) { targets.Add(leaf); thisPlbcHasTarget = true; } }   // OUR element (this district's, or all in the legacy path)
                        else if (donorLayer == null) donorLayer = ol;   // borrow the first real building output layer from ANY district
                    }
                    if (thisPlbcHasTarget) refreshPlbcs.Add(plbc);
                }
                if (targets.Count == 0) { if (bindLog.Add("notgt")) Plugin.Diag("[DistrictMain] bind: no null-outputLayer element found yet (selectors still loading?)."); return false; }
                if (donorLayer == null) { if (bindLog.Add("nodonor")) Plugin.Diag("[DistrictMain] bind: found our element(s) but no vanilla building output layer to borrow yet."); return false; }
                var donorClone = ClonePrivateOutputLayer((UnityEngine.Object)donorLayer);
                int bound = 0;
                foreach (var leaf in targets)
                {
                    var olF = GF(leaf.GetType(), "outputLayer");
                    olF.SetValue(leaf, donorClone);
                    try
                    {
                        var load = leaf.GetType().GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (load != null && load.GetParameters().Length == 2)
                        {
                            if (fxNextDoublon == null) fxNextDoublon = GameBinding.FxEvolverMaterial?.GetMethod("NextDoublonAvoidanceIndex", BindingFlags.Static | BindingFlags.Public);
                            uint doublon = fxNextDoublon != null ? (uint)fxNextDoublon.Invoke(null, null) : 0u;
                            load.Invoke(leaf, new object[] { distFxManager, doublon });
                            var mi = AccessTools.Field(leaf.GetType(), "meshIndex")?.GetValue(leaf);
                            var oli = AccessTools.Field(leaf.GetType(), "outputLayerIndex")?.GetValue(leaf);
                            if (bound < 3) Plugin.Diag($"[DistrictMain]   bound element '{GetMember(leaf, "Name")}' -> meshIndex={mi} outputLayerIndex={oli}");
                        }
                        bound++;
                    }
                    catch (Exception ex) { Plugin.Diag("[DistrictMain] bind Load failed: " + ex.Message); }
                }
                // re-spawn each hosting channel so the render re-reads the now-resolved element indices
                foreach (var plbc in refreshPlbcs)
                {
                    if (miRefreshChannel == null)
                        miRefreshChannel = plbc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            .FirstOrDefault(m => m.Name == "RefreshChannel" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                    if (miRefreshChannel != null)
                    {
                        var ra = new object[] { mainLayerCached >= 0 ? mainLayerCached : 0, System.Enum.ToObject(miRefreshChannel.GetParameters()[1].ParameterType, 0) };
                        try { miRefreshChannel.Invoke(plbc, ra); } catch { }
                    }
                }
                if (onlyName != null)
                {
                    scopedDonorClone = donorClone;                  // THIS district's own private layer (S.donorClone) — bind OUR albedo on it
                    scopedElements.Clear(); scopedElements.AddRange(targets);
                    scopedRefreshPlbcs.Clear(); scopedRefreshPlbcs.AddRange(refreshPlbcs);
                }
                else reactorBoundGlobal = true;                    // legacy shared path: elements hold the clone via their outputLayer; no per-district S
                Plugin.Log.LogInfo($"[DistrictMain] '{onlyName ?? "MainRows(shared)"}': bound {bound} building element(s) across {refreshPlbcs.Count} tile(s) (donor='{donorLayer}') — renders on the footprint.");
                return true;
            }
            catch (Exception ex) { Plugin.Log.LogError("[DistrictMain] bind reactor building: " + ex); return false; }
        }
        static readonly HashSet<string> bindLog = new HashSet<string>();

        // ---- SCOPED texture: bind the district's OWN baked albedo onto the borrowed (brick) donor output-layer clone,
        // so the reactor wears its own texture instead of the donor's. Mirrors DistrictApplyTexture/BindAlbedo but drives
        // the scoped element(s) + their shared donorClone (this path has no DistrictModel/privateLeaf). ----
        // PER-DISTRICT scoped state. The scoped path was first built for ONE district (the reactor) with these as global
        // statics; once a SECOND district is scoped (the Oracle, migrated off the isolate path) they'd clash (each would
        // wear the last-processed district's texture / B&W / flatten). So the state now lives per-district in ScopedState,
        // and `S` is the CURRENT district's state — set once per district in PollDistrictSelectorTile before any scoped
        // work runs. Every `scopedX`/`fpX`/`mesh*` name below is a PROXY property onto `S`, so all the function bodies that
        // read/write them stay byte-identical; only which ScopedState they hit changes with `S`.
        internal class ScopedState
        {
            public UnityEngine.Object donorClone;
            public readonly List<object> elements = new List<object>();
            public readonly List<object> refreshPlbcs = new List<object>();
            public object atlasGuid;
            public UnityEngine.Texture2D albedo, albedoGray;
            public readonly List<(UnityEngine.Material mat, string prop)> boundSlots = new List<(UnityEngine.Material, string)>();
            public bool texApplied; public int texWait, texErrors;
            public bool fpResolved, fpMesh, fpBW, fpFlat, fpHideDecal;
            public float fpFlatHeight = 0.17f;
            public bool? flatState; public float lastFlatHeight = float.NaN;
            public object flatSel;
            public readonly Dictionary<object, UnityEngine.Vector3> origSize = new Dictionary<object, UnityEngine.Vector3>();
        }
        static readonly Dictionary<string, ScopedState> scopedStates = new Dictionary<string, ScopedState>();
        static ScopedState S = new ScopedState();   // current scoped district's state (never null)
        static ScopedState ScopedFor(string name) { if (!scopedStates.TryGetValue(name, out var s)) scopedStates[name] = s = new ScopedState(); return s; }

        static UnityEngine.Object scopedDonorClone { get => S.donorClone; set => S.donorClone = value; }   // the private layer we bound on the element(s)
        static List<object> scopedElements => S.elements;                  // our element(s) sharing that layer
        static List<object> scopedRefreshPlbcs => S.refreshPlbcs;          // tiles to re-spawn after a texture flush
        static object scopedAtlasGuid { get => S.atlasGuid; set => S.atlasGuid = value; }   // the district's baked albedo atlas (from the registry)
        static UnityEngine.Texture2D scopedAlbedo { get => S.albedo; set => S.albedo = value; }   // loaded atlas texture
        static List<(UnityEngine.Material mat, string prop)> scopedBoundSlots => S.boundSlots;
        static bool scopedTexApplied { get => S.texApplied; set => S.texApplied = value; }
        static int scopedTexWait { get => S.texWait; set => S.texWait = value; }
        static int scopedTexErrors { get => S.texErrors; set => S.texErrors = value; }
        static bool HasAlpha(UnityEngine.TextureFormat f) => f == UnityEngine.TextureFormat.DXT5 || f == UnityEngine.TextureFormat.RGBA32 || f == UnityEngine.TextureFormat.ARGB32 || f == UnityEngine.TextureFormat.BC7 || f == UnityEngine.TextureFormat.RGBAHalf || f == UnityEngine.TextureFormat.RGBAFloat;
        internal static int scopedRebindLog;   // TWITCH DIAG counter (albedo rebinds = game resetting our texture)
        static readonly HashSet<string> scopedTexLog = new HashSet<string>();
        internal static void ApplyScopedAlbedo()
        {
            if (scopedDonorClone == null || scopedElements.Count == 0 || scopedAtlasGuid == null) return;
            try
            {
                // fast re-assert once applied — a res switch rebuilds the layer's runtime materials + drops our binding
                if (scopedTexApplied)
                {
                    if ((++scopedTexWait % 15) != 0) return;
                    BindScopedSheet(false); return;
                }
                // load OUR albedo atlas by GUID (retry until the bundle asset resolves)
                if (scopedAlbedo == null)
                {
                    scopedAlbedo = LoadAmpliAsset(typeof(UnityEngine.Texture2D), scopedAtlasGuid) as UnityEngine.Texture2D;
                    if (scopedAlbedo == null) { if ((++scopedTexWait % 300) == 1) Plugin.Diag("[DistrictTile] albedo atlas not loadable by GUID yet"); return; }
                }
                // for each element: force the full-texture path so mesh UVs sample our sheet [0,1], and register a null
                // atlas-info slot so the game's own re-resolve keeps returning full-texture for our layer
                foreach (var leaf in scopedElements)
                {
                    var t = leaf.GetType();
                    int layerIdx = GF(t, "outputLayerIndex")?.GetValue(leaf) is int li ? li : -1;
                    if (layerIdx < 0) { if ((++scopedTexWait % 300) == 1) Plugin.Diag("[DistrictTile] element layer not registered yet"); return; }
                    var desc = GetMember(leaf, "FxEvolverDescriptor");
                    var texMgr = desc != null ? AccessTools.Field(desc.GetType(), "assetContentManagerTexture")?.GetValue(desc) : null;
                    texMgr?.GetType().GetMethod("AddNullAtlasInfo", BindingFlags.Instance | BindingFlags.Public)?.Invoke(texMgr, new object[] { layerIdx });
                    GF(t, "textureIndex")?.SetValue(leaf, 1);
                }
                if (!BindScopedSheet(true)) { if ((++scopedTexWait % 300) == 1) Plugin.Diag("[DistrictTile] donor layer has no runtime materials yet"); return; }
                scopedTexApplied = true;
                // flush: mark material data changed + re-spawn each hosting tile so nothing keeps a stale texture index
                foreach (var leaf in scopedElements)
                {
                    var desc = GetMember(leaf, "FxEvolverDescriptor");
                    if (desc != null) AccessTools.Field(desc.GetType(), "materialDataHasChanged")?.SetValue(desc, true);
                }
                foreach (var plbc in scopedRefreshPlbcs)
                    if (miRefreshChannel != null)
                    {
                        var ra = new object[] { ResolveMainLayerFromPlbc(plbc), System.Enum.ToObject(miRefreshChannel.GetParameters()[1].ParameterType, 0) };
                        try { miRefreshChannel.Invoke(plbc, ra); } catch { }
                    }
                Plugin.Log.LogInfo("[DistrictTile] bound the district's OWN albedo atlas onto the reactor — it should now be textured, not white brick.");
            }
            catch (Exception ex)
            {
                if (++scopedTexErrors >= 3) { scopedTexApplied = true; Plugin.Log.LogError("[DistrictTile] texture apply failed 3x — giving up until reload: " + ex); }
                else Plugin.Log.LogWarning("[DistrictTile] texture apply (will retry): " + ex.Message);
            }
        }
        static int ResolveMainLayerFromPlbc(object plbc) => mainLayerCached >= 0 ? mainLayerCached : 0;

        // Bind scopedAlbedo onto the donor layer clone's runtime materials (same _MainTex/largest-sheet pick as BindAlbedo,
        // minus the DistrictModel surface-map handling — this district ships no baked normal/rough, so keep the donor maps).
        static bool BindScopedSheet(bool log)
        {
            if (scopedAlbedo == null || scopedDonorClone == null) return false;
            var desired = DesiredScopedAlbedo();   // colour normally; a greyscale copy when the mesh footprint is on the strategic map (DistrictFootprintMeshBW)
            if (!log && scopedBoundSlots.Count > 0)
            {
                bool stale = false;
                for (int i = 0; i < scopedBoundSlots.Count && !stale; i++)
                {
                    var (mat, prop) = scopedBoundSlots[i];
                    if (mat == null) { stale = true; break; }
                    if (!ReferenceEquals(mat.GetTexture(prop), desired))
                    {
                        mat.SetTexture(prop, desired);
                        // TWITCH DIAG: a rebind here means the GAME reset our albedo since last tick — if this logs steadily,
                        // the model/base ("rock") texture is alternating game<->ours = the twitch.
                        if (Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value && scopedRebindLog < 40)
                        { scopedRebindLog++; Plugin.Log.LogInfo($"[TwitchDiag] scoped albedo REBOUND (game had reset it) @ frame {UnityEngine.Time.frameCount} on '{prop}' (rebind #{scopedRebindLog})"); }
                    }
                }
                if (!stale) return true;
                scopedBoundSlots.Clear();
            }
            if (!(GetMember(scopedDonorClone, "RenderOutputs") is Array ros)) return false;
            bool dump = log && Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value;
            int n = 0; scopedBoundSlots.Clear();
            foreach (var ro in ros)
                foreach (var fld in new[] { "currentRenderMaterial", "runTimeRenderMaterial", "highResRunTimeRenderMaterial" })
                    if (GetMember(ro, fld) is UnityEngine.Material mat && mat != null)
                    {
                        // FOLIAGE ALPHA FIX (guarded): the scoped path borrows an OPAQUE building material (base 'Particle
                        // Implementation' shader, _Mode=0, no _ALPHATEST_ON, queue 2000) so composed foliage leaf-cards
                        // render SOLID. It exposes the Standard cutout API (_Cutoff/_Mode), so flip it to CUTOUT exactly like
                        // the bake's preview does. Scoped to alpha atlases (foliage) so the opaque reactor is untouched.
                        if (scopedAlbedo != null && HasAlpha(scopedAlbedo.format) && mat.HasProperty("_Cutoff") && !mat.IsKeywordEnabled("_ALPHATEST_ON"))
                        {
                            mat.SetFloat("_Mode", 1f);                 // Standard: Cutout
                            mat.EnableKeyword("_ALPHATEST_ON");
                            mat.SetFloat("_Cutoff", 0.5f);
                            mat.renderQueue = 2450;                    // AlphaTest queue
                            if (log) Plugin.Log.LogInfo($"[FootprintMesh] '{fld}' -> alpha-cutout foliage (Cutout mode + _ALPHATEST_ON, queue 2450)");
                        }
                        string pick = null; UnityEngine.Texture2D biggest = null; string biggestProp = null, alreadyProp = null;
                        bool hasMainTex = false, hasVisualContent = false;
                        foreach (var pn in mat.GetTexturePropertyNames())
                        {
                            var cur = mat.GetTexture(pn);
                            if (dump) Plugin.Diag($"[DistrictTile]   {fld}('{mat.shader?.name}').{pn} = {(cur != null ? $"'{cur.name}' {cur.width}x{cur.height}" : "null")}");
                            if (ReferenceEquals(cur, desired)) { alreadyProp = pn; continue; }
                            if (pn == "_MainTex") hasMainTex = true; else if (pn == "_VisualContent") hasVisualContent = true;
                            if (!(cur is UnityEngine.Texture2D t2)) continue;
                            if (pn == "_MainTex") pick = pn;
                            if (biggest == null || t2.width * t2.height > biggest.width * biggest.height) { biggest = t2; biggestProp = pn; }
                        }
                        if (alreadyProp != null) { n++; scopedBoundSlots.Add((mat, alreadyProp)); continue; }
                        if (pick == null) pick = biggestProp;
                        if (pick == null && hasMainTex) pick = "_MainTex";
                        if (pick == null && hasVisualContent) pick = "_VisualContent";
                        if (pick != null)
                        {
                            mat.SetTexture(pick, desired); n++;
                            scopedBoundSlots.Add((mat, pick));
                            if (log) Plugin.Diag($"[DistrictTile] albedo bound on {fld}.{pick}");
                        }
                    }
            if (log) Plugin.Diag($"[DistrictTile] albedo bound on {n} material slot(s) of the donor layer clone");
            return n > 0;
        }

        // ---- B&W footprint (config DistrictFootprintMeshBW): when the persistent MESH footprint is on the STRATEGIC map
        // (zoomed out), bind a greyscale copy of the reactor albedo instead of the colour one; full colour up close.
        // "Am I on the strategic map?" is answered EXACTLY (no zoom-threshold guessing) by asking the engine's
        // RenderFeatureProvider for the CURRENT 0..1 visibility of the TOPOGRAPHIC band (SelectionFlags0 = 2 =
        // TopographicTerrain = the schematic/strategic look): ~0 while zoomed in, rises to ~1 as the schematic map
        // takes over. (First cut keyed the RealisticTerrain/close band, but it stays "on" well past the schematic
        // crossover, so the reactor kept its colour zoomed out — Topographic is the band that tracks the schematic map.)
        // EFFECTIVE footprint-mesh settings for the scoped district: the per-entry registry values when the entry authored
        // footprintMesh=true, otherwise the plugin's global DistrictFootprintMesh… config (back-compat — a district works
        // before it's authored per-entry). Resolved once by KeepDistrictMeshAtStrategicZoom; the pollers read these.
        internal static bool fpResolved { get => S.fpResolved; set => S.fpResolved = value; }
        internal static bool fpMesh { get => S.fpMesh; set => S.fpMesh = value; }
        internal static bool fpBW { get => S.fpBW; set => S.fpBW = value; }
        internal static bool fpFlat { get => S.fpFlat; set => S.fpFlat = value; }
        internal static bool fpHideDecal { get => S.fpHideDecal; set => S.fpHideDecal = value; }
        static float fpFlatHeight { get => S.fpFlatHeight; set => S.fpFlatHeight = value; }
        internal static void ResolveScopedFootprint(string name)
        {
            DistrictModel e = null;
            foreach (var dm in distModels) if (dm.district == name) { e = dm; break; }
            if (e != null && e.footprintMesh)   // the entry is authoritative
            {
                fpMesh = true; fpBW = e.footprintMeshBW; fpFlat = e.footprintMeshFlat;
                fpFlatHeight = e.footprintMeshFlatHeight > 0f ? e.footprintMeshFlatHeight : 0.17f; fpHideDecal = e.footprintMeshHideDecal;
            }
            else   // fall back to the global config
            {
                fpMesh = Plugin.DistrictFootprintMesh != null && Plugin.DistrictFootprintMesh.Value == "true";
                fpBW = Plugin.DistrictFootprintMeshBW != null && Plugin.DistrictFootprintMeshBW.Value == "true";
                fpFlat = Plugin.DistrictFootprintMeshFlat != null && Plugin.DistrictFootprintMeshFlat.Value == "true";
                fpHideDecal = Plugin.DistrictFootprintMeshHideDecal == null || Plugin.DistrictFootprintMeshHideDecal.Value != "false";
                float h = 0.17f; float.TryParse(Plugin.DistrictFootprintMeshFlatHeight?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out h); fpFlatHeight = h;
            }
            fpResolved = true;
        }
        static UnityEngine.Texture2D scopedAlbedoGray { get => S.albedoGray; set => S.albedoGray = value; }
        static object rfpInstance; static MethodInfo miComputeRenderState; static object topoSelectorBox;
        static int bwDiagThrottle;
        // Current 0..1 visibility of the TOPOGRAPHIC (schematic/strategic) band — ~0 zoomed in, ~1 on the strategic map.
        // Returns -1 if the RenderFeatureProvider isn't loaded yet. Shared by the B&W and FLAT footprint options.
        static float SchematicVis()
        {
            try
            {
                if (rfpInstance == null || (rfpInstance is UnityEngine.Object ruo && ruo == null))
                {
                    var rfpType = AccessTools.TypeByName("Amplitude.Mercury.Fx.RenderFeatureProvider");
                    var selType = AccessTools.TypeByName("Amplitude.Mercury.Fx.RenderFeatureSelector");
                    if (rfpType == null || selType == null) return -1f;
                    var found = UnityEngine.Resources.FindObjectsOfTypeAll(rfpType);
                    if (found == null || found.Length == 0) return -1f;   // not loaded yet
                    rfpInstance = found[0];
                    miComputeRenderState = rfpType.GetMethod("ComputeRenderState", new[] { selType });
                    var box = Activator.CreateInstance(selType);
                    selType.GetField("SelectionFlags0").SetValue(box, 2u);   // TopographicTerrain (the schematic/strategic band)
                    selType.GetField("FadingOptions").SetValue(box, 1u);
                    topoSelectorBox = box;
                }
                if (miComputeRenderState == null || topoSelectorBox == null) return -1f;
                return (float)miComputeRenderState.Invoke(rfpInstance, new[] { topoSelectorBox });
            }
            catch { return -1f; }
        }
        static UnityEngine.Texture2D DesiredScopedAlbedo()
        {
            bool bw = fpResolved ? fpBW : (Plugin.DistrictFootprintMeshBW != null && Plugin.DistrictFootprintMeshBW.Value == "true");
            if (!bw || scopedAlbedo == null) return scopedAlbedo;
            float topoVis = SchematicVis();
            if (topoVis < 0f) return scopedAlbedo;                       // provider not ready -> colour
            if (Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value && (++bwDiagThrottle % 30) == 1)
                Plugin.Log.LogInfo($"[FootprintMesh] topographic band vis = {topoVis:0.00} -> {(topoVis >= 0.5f ? "GREY" : "colour")}");
            if (topoVis < 0.5f) return scopedAlbedo;                     // schematic not active yet -> colour
            if (scopedAlbedoGray == null) scopedAlbedoGray = MakeGrayCopy(scopedAlbedo);   // build once, lazily
            return scopedAlbedoGray ?? scopedAlbedo;
        }
        // FLAT footprint (config DistrictFootprintMeshFlat): squash the reactor mesh element(s) to ~0 height while the
        // schematic map is active, restore full height up close. Driven by the same Topographic-band signal as the B&W
        // swap. `size` scales the element (the scoped setup already uses size=0 to HIDE props — line ~702), so size.y->~0
        // collapses the mesh into a flat sheet. size feeds GPU via WriteToGPUData, so re-emit on the crossover only.
        static bool? meshFlatState { get => S.flatState; set => S.flatState = value; }
        static float lastFlatHeight { get => S.lastFlatHeight; set => S.lastFlatHeight = value; }
        static float runtimeFlatHeight = float.NaN;   // in-game F8-window override; NaN = use the config value (GLOBAL — one manual tuning knob)
        internal static object scopedFlatSel { get => S.flatSel; set => S.flatSel = value; }   // the scoped district's selector (set by KeepDistrictMeshAtStrategicZoom)
        static Dictionary<object, UnityEngine.Vector3> meshOrigSize => S.origSize;
        // Flatten HEIGHT = the size.y multiplier used on the strategic map: ~0.02 = paper-flat (but coplanar with terrain,
        // so its edges drown when the tile's ground rises over them), up toward 1 = full 3D. The sweet spot reads flat yet
        // still pokes clear of the terrain. This is the lever that actually reaches the GPU (unlike item Position.y).
        internal static float FlatHeightValue()   // PER-DISTRICT: reads S — only valid with S set (called from UpdateMeshFlatness). NOT for the F8 window.
        {
            if (!float.IsNaN(runtimeFlatHeight)) return runtimeFlatHeight;   // live F8 override wins (global, all scoped districts)
            if (fpResolved) return fpFlatHeight;                             // THIS district's per-entry value (or its config fallback)
            float v = 0.17f; float.TryParse(Plugin.DistrictFootprintMeshFlatHeight?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v); return v;
        }
        // F8-window (S-INDEPENDENT) accessors: the flat-height slider is a GLOBAL live override across every scoped district
        // (the window has no per-district selection). These never touch S, so the readout/tuning stays meaningful with two
        // or more scoped districts. Overriding is opt-in — Reset clears it and each district falls back to its own value.
        internal static bool FlatHeightOverriding() => !float.IsNaN(runtimeFlatHeight);
        internal static float FlatHeightOverrideValue() => float.IsNaN(runtimeFlatHeight) ? 0.17f : runtimeFlatHeight;
        internal static void ClearFlatHeightOverride() { runtimeFlatHeight = float.NaN; Plugin.Log.LogInfo("[FootprintMesh] flat-height override cleared — each district uses its own value again."); }
        internal static void NudgeFlatHeight(float delta) => SetFlatHeight(FlatHeightOverrideValue() + delta);
        internal static void SetFlatHeight(float value)
        {
            runtimeFlatHeight = UnityEngine.Mathf.Clamp(value, 0.02f, 1f);
            Plugin.Log.LogInfo($"[FootprintMesh] flat-height override -> {runtimeFlatHeight:0.00} (all scoped districts)");
        }
        internal static void UpdateMeshFlatness()
        {
            bool flatOn = fpResolved ? fpFlat : (Plugin.DistrictFootprintMeshFlat != null && Plugin.DistrictFootprintMeshFlat.Value == "true");
            if (!flatOn || scopedElements.Count == 0) return;
            float topoVis = SchematicVis();
            if (topoVis < 0f) return;                                    // provider not ready
            bool flat = topoVis >= 0.5f;
            float height = FlatHeightValue();
            // re-apply on a band change OR (while flat) a live height-tuning change
            if (meshFlatState.HasValue && meshFlatState.Value == flat && !(flat && height != lastFlatHeight)) return;
            try
            {
                int changed = 0;
                foreach (var el in scopedElements)
                {
                    var t = el.GetType();
                    var sizeF = GF(t, "size");
                    if (sizeF == null) continue;
                    if (!meshOrigSize.ContainsKey(el)) { if (sizeF.GetValue(el) is UnityEngine.Vector3 os) meshOrigSize[el] = os; else continue; }
                    var orig = meshOrigSize[el];
                    sizeF.SetValue(el, flat ? new UnityEngine.Vector3(orig.x, orig.y * height, orig.z) : orig);   // squash to `height` (tunable) on the strategic map
                    var desc = GetMember(el, "FxEvolverDescriptor");
                    if (desc != null) AccessTools.Field(desc.GetType(), "materialDataHasChanged")?.SetValue(desc, true);
                    InvokeNoArg(el, "OnEditionChange");
                    changed++;
                }
                lastFlatHeight = height;
                // re-spawn the hosting channels so the new size reaches the render data
                foreach (var plbc in scopedRefreshPlbcs)
                {
                    if (plbc == null) continue;
                    if (miRefreshChannel == null)
                        miRefreshChannel = plbc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            .FirstOrDefault(m => m.Name == "RefreshChannel" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
                    if (miRefreshChannel != null)
                    {
                        var ra = new object[] { mainLayerCached >= 0 ? mainLayerCached : 0, System.Enum.ToObject(miRefreshChannel.GetParameters()[1].ParameterType, 0) };
                        try { miRefreshChannel.Invoke(plbc, ra); } catch { }
                    }
                }
                meshFlatState = flat;
                Plugin.Log.LogInfo($"[FootprintMesh] reactor mesh -> {(flat ? "FLAT (strategic footprint)" : "3D (close-up)")} on {changed} element(s)");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[FootprintMesh] flatten: " + ex.Message); }
        }
        // Blit the (possibly non-CPU-readable) albedo through a RenderTexture, read it back, and desaturate it fully
        // (AdjustSkin desat=1 = luminance greyscale). Same readback pattern as BuildAdjustedAtlas.
        static UnityEngine.Texture2D MakeGrayCopy(UnityEngine.Texture2D src)
        {
            try
            {
                int w = src.width, h = src.height;
                var rt = UnityEngine.RenderTexture.GetTemporary(w, h, 0, UnityEngine.RenderTextureFormat.ARGB32, UnityEngine.RenderTextureReadWrite.sRGB);
                var prev = UnityEngine.RenderTexture.active;
                UnityEngine.Graphics.Blit(src, rt);
                UnityEngine.RenderTexture.active = rt;
                var t = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGBA32, false) { name = "ReactorAlbedo_Gray" };
                t.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0); t.Apply();
                UnityEngine.RenderTexture.active = prev; UnityEngine.RenderTexture.ReleaseTemporary(rt);
                AdjustSkin(t, 1f, 1f, 0f, 0f, 0f);   // full greyscale, no brightness/tint change
                TrackDistrictClone(t);   // our runtime gray copy — OWN it, freed on session reset (leak fix)
                Plugin.Log.LogInfo($"[FootprintMesh] built greyscale albedo {w}x{h} for the strategic-zoom B&W footprint.");
                return t;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[FootprintMesh] grey copy failed: " + e.Message); return null; }
        }

        // Postfix (per district UpdateLevelBuild): match against the registry and cache each entry's component + layer.
        // PERF: this fires for EVERY district on the map (city refreshes touch dozens) — cache the reflection handles.
        static FieldInfo fiDistrictPlbc; static int mainLayerCached = -1;
        internal static void DistrictApplyEntries(object district)
        {
            try
            {
                DumpAnyDistrictTree(district);   // spike: dump vanilla + our districts' presentation trees to diff the footprint decal
                EnsureDistrictConfig();
                // Track the FxManager for ANY district, even with injection off (distOn=false) — the dedicated-visual
                // hybrid (PollDistrictMainRows) and wonder rows need it to run without the runtime swap fighting them.
                if (distFxManager == null)
                {
                    if (fiDistrictPlbc == null) fiDistrictPlbc = AccessTools.Field(district.GetType(), "presentationLevelBuildComponent");
                    var plbc0 = fiDistrictPlbc?.GetValue(district);
                    var fm0 = plbc0 != null ? GetMember(plbc0, "FxManager") : null;
                    if (fm0 != null) distFxManager = fm0;
                }
                // DEDICATED-VISUAL HYBRID: track every live district INSTANCE (even with injection off) so, once the
                // */District/Main cell fill lands, we can replay UpdateLevelBuild on them to force a re-resolve of the
                // now-filled cell. The game resolves the selector ONCE at district build (before our fill can run, since
                // distFxManager only comes up AT the first district's build) and caches it — the cell edit alone is inert
                // until something re-reads it. Skip while WE are the ones re-invoking (guard against re-entry/recursion).
                bool wantTrack = (Plugin.DistrictMainRows != null && !string.IsNullOrEmpty(Plugin.DistrictMainRows.Value))
                              || (Plugin.DistrictSelectorTile != null && !string.IsNullOrEmpty(Plugin.DistrictSelectorTile.Value));
                if (!inForcedReresolve && wantTrack)
                {
                    bool seen = false;
                    for (int i = 0; i < trackedDistricts.Count && !seen; i++) seen = ReferenceEquals(trackedDistricts[i], district);
                    if (!seen) trackedDistricts.Add(district);
                }
                if (!distOn || distModels.Count == 0) return;
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString();
                if (string.IsNullOrEmpty(name)) return;
                if (IsScopedDistrict(name)) return;   // handled by the SCOPED path (DistrictSelectorTile) — don't also isolate-inject it
                foreach (var e in distModels)
                {
                    if (e.district != name || e.fxMeshGuid == null) continue;
                    if (fiDistrictPlbc == null) fiDistrictPlbc = AccessTools.Field(district.GetType(), "presentationLevelBuildComponent");
                    var plbc = fiDistrictPlbc?.GetValue(district);
                    if (plbc == null) continue;
                    // FRESH-FIRST, never `??`-cached: a second game in the same app run replaces the FxManager, and a
                    // stale cached one has fxComponents == null — every leaf LoadIFN then NREs (the Oracle incident:
                    // the wonder class was innocent, the corpse manager was the whole failure).
                    var fm = GetMember(plbc, "FxManager");
                    if (fm != null) distFxManager = fm;
                    if (mainLayerCached < 0)
                    {
                        var lf = district.GetType().GetField("mainLevelBuildComponantLayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                        mainLayerCached = lf?.GetValue(null) is int li ? li : 0;
                    }
                    // one TileState per live district INSTANCE — never overwrite (a district can be built in many cities)
                    bool known = false;
                    for (int i = 0; i < e.tiles.Count && !known; i++) known = ReferenceEquals(e.tiles[i].plbc, plbc);
                    if (!known)
                    {
                        e.tiles.Add(new DistrictModel.TileState { plbc = plbc, layer = mainLayerCached });
                        Plugin.Diag($"[District] registry matched '{e.district}' — tile #{e.tiles.Count} (isolate={e.isolate}).");
                        e.matchLogged = true;
                    }
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
        // DISTRICT RUNTIME-CLONE OWNERSHIP (leak fix, 2026-08-16). Every runtime Object.Instantiate / new Texture2D on
        // the district axis (private leaves, cloned selectors, cloned output layers, deep-clone material nodes, the B&W
        // gray albedo) is tracked here as it is CREATED — never a LoadAmpliAsset'd bundle asset (those are shared game
        // assets; Destroying one would unload it, the red-skin class of bug). On a session reset we move the whole owned
        // set to a pending-destroy queue and free it on the MAIN thread (ResetDistrictSessionState can run off the main
        // thread via the Sandbox.Load hook; UnityEngine.Object.Destroy is main-thread-only). Mirrors the model axis's
        // texOwned/isolatedLayer discipline — before this, ResetDistrictSessionState only NULLED these clones, and Unity's
        // unused-asset sweep does not collect them, so each in-session reload leaked a native FxOutputLayer + N cloned
        // FxEvolverMaterials + a gray texture per scoped district.
        static readonly object districtDestroyGate = new object();
        static readonly List<UnityEngine.Object> districtOwnedClones = new List<UnityEngine.Object>();   // live clones this session
        static readonly List<UnityEngine.Object> districtPendingDestroy = new List<UnityEngine.Object>(); // moved here on reset, freed on Update
        static void TrackDistrictClone(UnityEngine.Object o) { if (o) lock (districtDestroyGate) districtOwnedClones.Add(o); }

        // Main thread (Plugin.Update). Free the district clones queued by a session reset.
        internal static void DrainDistrictDestroys()
        {
            UnityEngine.Object[] batch = null;
            lock (districtDestroyGate)
            {
                if (districtPendingDestroy.Count == 0) return;
                batch = districtPendingDestroy.ToArray();
                districtPendingDestroy.Clear();
            }
            int n = 0;
            foreach (var o in batch)
                if (o) { try { UnityEngine.Object.Destroy(o); n++; } catch { } }
            if (n > 0) Plugin.Diag($"[District] freed {n} runtime clone(s) from the previous session (leak fix).");
        }

        internal static void ResetDistrictSessionState()
        {
            // hand this session's runtime clones to the main-thread destroy queue (this method may run off the main thread)
            lock (districtDestroyGate)
            {
                districtPendingDestroy.AddRange(districtOwnedClones);
                districtOwnedClones.Clear();
            }
            distFxManager = null;
            trackedDistricts.Clear(); lastLevelBuildEvent = null; haveLevelBuildEvent = false;   // instances/args reference the dead session
            bindLog.Clear();   // re-bind the building output layers against the new session's leaves
            loadedSelectorByKey.Clear(); selectorTileLogged.Clear();   // loaded selectors reference the dead session's FxManager
            scopedStates.Clear(); S = new ScopedState();   // ALL per-district scoped state (donorClone/albedo/elements/B&W/flatten) referenced the dead session
            reactorBoundGlobal = false; scopedTexLog.Clear();   // legacy once-flag + global diag throttle
            foreach (var d in distModels)
            {
                d.tiles.Clear(); d.privateLeaf = null; d.leaves.Clear(); d.collected = false;
                d.matchLogged = false;
                d.origSelector = null; d.decalSelector = null; d.decalChannel = -1; d.decalLogged = false; d.decalGaveUp = false;
                d.clonedSelector = null; d.cloneMap = null; d.cloneLogged = false; d.cloneReassert = 0; d.deepLayer = null; d.domeCounter = 0;
                d.texApplied = false; d.texWait = 0; d.texErrors = 0; d.texAlbedo = null; d.texNormal = null; d.texRough = null;
                d.boundSlots.Clear();   // the cached (material, property) bind slots are corpses with the old layer
                d.groundIdx = int.MinValue; d.groundApplied = false;   // re-resolve + re-apply the ground paint once against the new session
                d.hexIdx = int.MinValue;
            }
            postSwapTicks = 0; postSwapDumped = false;   // re-arm the post-swap tree dump for the new session
            ResetWonderTemplates();   // plugin-loaded wonder templates are corpses after a reload; re-load + re-fill swap-first
            // re-parse the registry too: a reload then picks up haf_districts.json edits (new/changed entries) without
            // a game restart. NOTE the honest limit: baked ASSETS ship in the mod bundle, which the game loads once per
            // app run — a re-BAKE still needs a restart; only registry-value changes arrive on reload.
            distParsed = false;
            Plugin.Diag("[District] session state reset (new game or save-reload) — registry re-parses, leaves + texture bindings rebuild");
        }

        // A district named in DistrictSelectorTile is rendered by the SCOPED path (our data-authored selector on its
        // channel). The old ISOLATE path (this file's per-frame private-leaf swap) must leave those alone or the two
        // fight for channel[0]. Lets DistrictRepoint stay ON for isolate-path districts (e.g. the Oracle wonder) while a
        // scoped district (the reactor) coexists in the same registry.
        static bool IsScopedDistrict(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var cfg = Plugin.DistrictSelectorTile?.Value;
            if (!string.IsNullOrEmpty(cfg))
                foreach (var part in cfg.Split(';'))
                { var eq = part.IndexOf('='); if (eq > 0 && part.Substring(0, eq).Trim() == name) return true; }
            foreach (var dm in distModels)   // registry-authored scoped districts (selectorGuid baked in the District Factory)
                if (dm.district == name && dm.selectorGuid != null) return true;
            return false;
        }

        // Per-frame (Plugin.Update): drive every registry entry, each across ALL of its live tiles.
        internal static void TickDistrictMeshSwap()
        {
            if (distModels.Count == 0) return;
            foreach (var e in distModels)
            {
                if (IsScopedDistrict(e.district)) continue;   // the SCOPED path owns this district's channel — don't fight it with an isolate leaf
                // prune tiles whose component died (razed district / recycled entity) — Unity fake-null on the Component
                for (int i = e.tiles.Count - 1; i >= 0; i--)
                    if (e.tiles[i].plbc is UnityEngine.Object uo && uo == null)
                    { e.tiles.RemoveAt(i); Plugin.Diag($"[District] '{e.district}': tile component destroyed — pruned ({e.tiles.Count} left)."); }
                if (e.isolate)
                {
                    // PreserveFootprintChannel FALSIFIED (08-09): hosting a decal-selector clone on a free channel renders
                    // nothing — the plbc has ONE composited level-build content channel (mainLevelBuildComponantLayer; the
                    // native reactor dump shows "1 channel(s)"). Decals can't live on a separate channel; building + decals
                    // must share the main selector. So the footprint needs the deep-clone/privatize path, not a side channel.
                    for (int i = 0; i < e.tiles.Count; i++)
                    {
                        if (UseDeepClone) PointTileAtClonedSelector(e, e.tiles[i]);
                        else PointTileAtPrivateLeaf(e, e.tiles[i]);
                    }
                    DistrictApplyTexture(e);   // both paths: e.privateLeaf points at a reactor element sharing e.deepLayer, so our albedo binds to all swapped slots
                }
                else GlobalSwapEntry(e);
            }
            DumpDecalDescriptor();
            DumpPostSwapTrees();
        }

        // POST-SWAP tree dump (spike, DistrictDebug): the [Tree] dump from DistrictApplyEntries fires on UpdateLevelBuild
        // BEFORE this per-frame swap, so it only ever captured the PRE-swap (native) tree — a "native vs swapped" diff came
        // back byte-identical purely from timing. Re-dump here ONCE, ~300 ticks (~5 s at 60 fps, past respawnAfterLoad) after
        // the swap has settled, tagged [TreePost] so it's separable in the log. Answers: does the swap keep the 231 decal
        // drawers or drop them, and which elements now carry our mesh.
        static int postSwapTicks; static bool postSwapDumped; static string treeTag = "Tree";
        static void DumpPostSwapTrees()
        {
            if (postSwapDumped || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;
            if (++postSwapTicks < 300) return;
            postSwapDumped = true;
            treeTag = "TreePost";
            try
            {
                foreach (var e in distModels)
                {
                    if (e.tiles.Count == 0) { Plugin.Log.LogInfo($"[TreePost] '{e.district}': no live tile to dump"); continue; }
                    var plbc = e.tiles[0].plbc;
                    if (plbc is UnityEngine.Object uo && uo == null) continue;
                    DumpPlbcTree(plbc, e.district + " POSTSWAP (isolate=" + e.isolate + ")");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[TreePost] " + ex.Message); }
            finally { treeTag = "Tree"; }
        }

        // FOOTPRINT probe (gated DistrictDebug): the DECAL isn't in the district's plbc (dumped: Element/Emitter/Matching/
        // Selector, no Decal). Like the impostor, it's a GLOBAL FxEvolverDescriptorLevelBuildDecal singleton holding decal
        // materials per district type — each with a decalMesh (the footprint geometry) + texture layers. Reach it via its
        // static GetInstance(bool) and dump the registry: that names the vanilla footprint assets and whether ours is there.
        static bool decalDumped;
        internal static void DumpDecalDescriptor()
        {
            try
            {
                if (decalDumped || distFxManager == null || Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;
                var tDesc = HarmonyLib.AccessTools.TypeByName("Amplitude.Mercury.Terrain.Fx.FxEvolverDescriptorLevelBuildDecal");
                if (tDesc == null) { decalDumped = true; Plugin.Log.LogWarning("[Decal] FxEvolverDescriptorLevelBuildDecal not found"); return; }
                var getInst = tDesc.GetMethod("GetInstance", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(bool) }, null);
                if (getInst == null) { decalDumped = true; Plugin.Log.LogWarning("[Decal] GetInstance(bool) not found"); return; }
                var desc = getInst.Invoke(null, new object[] { true });
                if (desc == null) return;   // not ready — retry
                var dt = desc.GetType();
                object matsObj = null;
                for (var ct = dt; ct != null && matsObj == null; ct = ct.BaseType)
                    matsObj = ct.GetProperty("EvolverMaterials", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(desc)
                            ?? AccessTools.Field(ct, "EvolverMaterials")?.GetValue(desc)
                            ?? AccessTools.Field(ct, "evolverMaterials")?.GetValue(desc);
                if (!(matsObj is System.Collections.IList mats)) { decalDumped = true; Plugin.Log.LogWarning($"[Decal] EvolverMaterials list not found on {dt.FullName}"); return; }
                decalDumped = true;
                Plugin.Log.LogInfo($"[Decal] {dt.Name}: {mats.Count} decal material(s):");
                for (int i = 0; i < mats.Count; i++)
                {
                    var m = mats[i]; if (m == null) continue;
                    var mt = m.GetType();
                    var dm = GF(mt, "decalMesh")?.GetValue(m);
                    var nm = GetMember(m, "Name") ?? GetMember(m, "name");
                    Plugin.Log.LogInfo($"[Decal]  [{i}] {mt.Name} name='{nm}' decalMesh={dm}");
                }
            }
            catch (Exception ex) { decalDumped = true; Plugin.Log.LogWarning("[Decal] " + ex.Message); }
        }

        // FOOTPRINT probe (gated DistrictDebug): the district's plbc has MULTIPLE channels — the building (the one we
        // inject) AND a DECAL channel (FxEvolverMaterialLevelBuildDecal = decalMesh + texture layers = the footprint).
        // Dump every channel's evolverMaterial type, and for any Decal one its decalMesh GUID — that names the vanilla
        // footprint asset. This shows whether the decal channel exists for our district and what it holds.
        // Dump the channel + material tree of ANY district by name — call for vanilla AND ours to diff what a normal
        // district's presentation has (a decal drawer for the footprint?) that the reactor's lacks. First ~12 distinct.
        static readonly HashSet<string> treeDumpedNames = new HashSet<string>();
        internal static void DumpAnyDistrictTree(object district)
        {
            try
            {
                if (Plugin.DistrictDebug == null || !Plugin.DistrictDebug.Value) return;
                var name = GetMember(district, "ConstructibleDefinitionName")?.ToString();
                if (string.IsNullOrEmpty(name) || treeDumpedNames.Contains(name)) return;
                // ALWAYS dump the reactor (so we can compare its native tree to vanilla); cap the others.
                if (!name.Contains("BreederReactor") && !name.Contains("Industry") && treeDumpedNames.Count >= 14) return;
                if (fiDistrictPlbc == null) fiDistrictPlbc = AccessTools.Field(district.GetType(), "presentationLevelBuildComponent");
                var plbc = fiDistrictPlbc?.GetValue(district);
                if (plbc == null) return;   // no plbc yet — retry a later frame (don't mark seen)
                treeDumpedNames.Add(name);
                DumpPlbcTree(plbc, name);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Tree] " + ex.Message); }
        }

        static void DumpPlbcTree(object plbc, string label)
        {
            if (fiPlbcChannels == null) fiPlbcChannels = AccessTools.Field(plbc.GetType(), "channels");
            if (!(fiPlbcChannels?.GetValue(plbc) is Array channels)) return;
            Plugin.Log.LogInfo($"[{treeTag}] '{label}' plbc: {channels.Length} channel(s)");
            var seen = new HashSet<object>();
            for (int i = 0; i < channels.Length; i++)
            {
                var box = channels.GetValue(i);
                var mat = box != null ? GF(box.GetType(), "evolverMaterial")?.GetValue(box) : null;
                Plugin.Log.LogInfo($"[{treeTag}]  [{i}] {(mat?.GetType().Name ?? "null")}");
                DumpMatTree(mat, 2, seen);
            }
        }

        // recurse a level-build material tree (emitter items + selector cache entries), logging each material type and
        // any DECAL's mesh — the nested footprint drawer.
        static void DumpMatTree(object mat, int depth, HashSet<object> seen)
        {
            if (mat == null || depth > 10 || !seen.Add(mat)) return;
            var t = mat.GetType();
            string extra = "";
            if (t.Name.Contains("Decal"))
            {
                // The decal renders only if its visualOutput resolves: OutputLayerIndex>=0 AND LoadedOutputLayer.Atlas!=null
                // (FxEvolverMaterialLevelBuildDecal.ResolveDependencies / AddDataTo). If our injection nulls this on the
                // reactor's decals while Food/Science keep it, the gate is C#-fixable; if identical, it's GPU-shader only.
                var vo = GF(t, "visualOutput")?.GetValue(mat);
                string vos = "visualOutput=null";
                if (vo != null)
                {
                    var vt = vo.GetType();
                    object OliGet(string p) => vt.GetProperty(p, BF)?.GetValue(vo) ?? GF(vt, p)?.GetValue(vo);
                    var oli = OliGet("OutputLayerIndex") ?? OliGet("outputLayerIndex");
                    object lol = null; try { lol = vt.GetProperty("LoadedOutputLayer", BF)?.GetValue(vo); } catch { }
                    object atlas = null;
                    if (lol != null) { try { atlas = lol.GetType().GetProperty("Atlas", BF)?.GetValue(lol) ?? lol.GetType().GetField("atlas", BF)?.GetValue(lol); } catch { } }
                    vos = $"outLayerIdx={oli} loadedLayer={(lol != null)} atlas={(atlas != null)}";
                }
                var lec = GF(t, "layerEntryCount")?.GetValue(mat);
                var rdi = GF(t, "levelBuildDecalRenderDataEntryIndex")?.GetValue(mat);
                var ld = GF(t, "loadingStatus")?.GetValue(mat) ?? GF(t, "loadedStatus")?.GetValue(mat);
                extra = $"  <<< DECAL {vos} layerEntryCount={lec} renderDataIdx={rdi} load={ld}";
            }
            else if (t.Name.Contains("BuildElement"))
            {
                // The building Element uploads BBoxMin/Max + LodData to the GPU selection shader (WriteToGPUData ~43350).
                // In global mode we keep the donor's bbox but our Load re-resolves lodData/meshIndexLod from our LOD-less
                // mesh. Dump both so we can see empirically what our swap changes vs an untouched (Food) element.
                object bbox = GF(t, "bbox")?.GetValue(mat);
                string bs = "?"; object useC = GF(t, "useCustomBBox")?.GetValue(mat);
                if (bbox != null) { try { var bt = bbox.GetType(); bs = $"{bt.GetProperty("min", BF)?.GetValue(bbox)}..{bt.GetProperty("max", BF)?.GetValue(bbox)}"; } catch { } }
                var lodD = GF(t, "lodData")?.GetValue(mat);
                var mi = GF(t, "meshIndex")?.GetValue(mat);
                var mi0 = GF(t, "meshIndexLod0")?.GetValue(mat);
                var mi1 = GF(t, "meshIndexLod1")?.GetValue(mat);
                var sz = GF(t, "size")?.GetValue(mat);
                extra = $"  <<< ELEMENT bbox={bs} useCustomBBox={useC} lodData={lodD} meshIdx={mi} lod0={mi0} lod1={mi1} size={sz}";
            }
            Plugin.Log.LogInfo($"[{treeTag}] {new string(' ', depth * 2)}{t.Name}{extra}");
            // emitter: levelBuildItems[].loadedEvolverMaterial   (GF, not AccessTools.Field — the latter warn-spams on miss)
            if (GF(t, "levelBuildItems")?.GetValue(mat) is Array items)
                foreach (var it in items) if (it != null) DumpMatTree(GF(it.GetType(), "loadedEvolverMaterial")?.GetValue(it), depth + 1, seen);
            // selector: loaded cache entries
            var cache = GF(t, "fxMaterialCacheEntries")?.GetValue(mat);
            if (cache != null && GF(cache.GetType(), "Entries")?.GetValue(cache) is Array entries)
                foreach (var en in entries) if (en != null) DumpMatTree(GF(en.GetType(), "FxMaterial")?.GetValue(en), depth + 1, seen);
            // selector: the pairs VARIANT TABLE + defaultMaterial/invalidNameMaterial — where the district's MAIN BUILDING
            // element actually lives (CollectLeaves walks this; the old dump didn't, so it only caught shared props).
            if (GF(t, "pairs")?.GetValue(mat) is Array pairs)
                foreach (var pr in pairs) if (pr != null) { var g = PairGuid(pr); if (!GuidIsNull(g)) DumpMatTree(TryLoadMaterial(g), depth + 1, seen); }
            foreach (var fn in new[] { "defaultMaterial", "invalidNameMaterial" })
            { var g = GF(t, fn)?.GetValue(mat); if (g != null && !GuidIsNull(g)) DumpMatTree(TryLoadMaterial(g), depth + 1, seen); }
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
                if (miRefreshChannel != null)
                    foreach (var t2 in e.tiles)   // every live instance re-spawns its particle with the fresh texture index
                        if (t2.plbc != null) { refreshArgs[0] = t2.layer; miRefreshChannel.Invoke(t2.plbc, refreshArgs); }
            }
            catch (Exception ex)
            {
                // bounded retry, not a first-throw latch: the old `texApplied = true` here turned one transient
                // exception (asset mid-load, layer mid-rebuild) into a silent permanent untextured downgrade.
                if (++e.texErrors >= 3) { e.texApplied = true; Plugin.Log.LogError($"[DistrictTex] apply failed {e.texErrors}x — giving up for '{e.district}' until the next session reset: " + ex); }
                else Plugin.Log.LogWarning($"[DistrictTex] apply (attempt {e.texErrors}, will retry): " + ex.Message);
            }
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

                // FAST re-assert (the % 15 tick): rebind only the CACHED slots — one reference compare per slot,
                // zero allocation. A destroyed material (res switch rebuilt the layer's materials) invalidates the
                // cache and drops through to the full walk, which is exactly when re-discovery is needed.
                if (!log && e.boundSlots.Count > 0)
                {
                    bool stale = false;
                    for (int i = 0; i < e.boundSlots.Count && !stale; i++)
                    {
                        var (mat, prop) = e.boundSlots[i];
                        if (mat == null) { stale = true; break; }   // Unity fake-null: material was destroyed
                        if (!ReferenceEquals(mat.GetTexture(prop), e.texAlbedo))
                        { mat.SetTexture(prop, e.texAlbedo); NeutralizeSurfaceMaps(e, mat); }
                    }
                    if (!stale) return true;
                    e.boundSlots.Clear();
                }

                var ol = GF(e.privateLeaf.GetType(), "outputLayer")?.GetValue(e.privateLeaf);
                if (!(GetMember(ol, "RenderOutputs") is Array ros)) return false;
                int n = 0;
                bool dump = log && Plugin.DistrictDebug != null && Plugin.DistrictDebug.Value;
                e.boundSlots.Clear();
                foreach (var ro in ros)
                    foreach (var fld in new[] { "currentRenderMaterial", "runTimeRenderMaterial", "highResRunTimeRenderMaterial" })
                        if (GetMember(ro, fld) is UnityEngine.Material mat && mat != null)
                        {
                            string pick = null; UnityEngine.Texture2D biggest = null; string biggestProp = null;
                            string alreadyProp = null; bool hasMainTex = false, hasVisualContent = false;
                            foreach (var pn in mat.GetTexturePropertyNames())
                            {
                                var cur = mat.GetTexture(pn);
                                if (dump) Plugin.Diag($"[DistrictTex]   {fld}('{mat.shader?.name}').{pn} = {(cur != null ? $"'{cur.name}' {cur.width}x{cur.height}" : "null")}");
                                if (ReferenceEquals(cur, e.texAlbedo)) { alreadyProp = pn; continue; }
                                if (pn == "_MainTex") hasMainTex = true; else if (pn == "_VisualContent") hasVisualContent = true;
                                if (!(cur is UnityEngine.Texture2D t2)) continue;
                                if (pn == "_MainTex") pick = pn;
                                if (biggest == null || t2.width * t2.height > biggest.width * biggest.height) { biggest = t2; biggestProp = pn; }
                            }
                            if (alreadyProp != null) { n++; NeutralizeSurfaceMaps(e, mat); e.boundSlots.Add((mat, alreadyProp)); continue; }
                            if (pick == null) pick = biggestProp;
                            // ATLAS-managed layer (the Base_Industry city building): ALL material texture slots are null —
                            // nothing to replace. Bind our sheet on the shader's own albedo slot anyway; with the full-
                            // texture path (textureIndex=1) already forced, the mesh UVs then sample it.
                            if (pick == null && hasMainTex) pick = "_MainTex";
                            if (pick == null && hasVisualContent) pick = "_VisualContent";
                            if (pick != null)
                            {
                                mat.SetTexture(pick, e.texAlbedo); n++;
                                NeutralizeSurfaceMaps(e, mat);
                                e.boundSlots.Add((mat, pick));
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
        // BepInEx/config/haf_atlas_dump/<layer>.png, so a unit's skin can be found by its layer name and used as a
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
                string dir = Path.Combine(Paths.ConfigPath, "haf_atlas_dump");
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
