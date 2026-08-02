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
        static int CurrentEra()
        {
            if (UnityEngine.Time.time - lastEraPoll < 2f) return cachedEra;
            lastEraPoll = UnityEngine.Time.time;
            int era = -1;
            try
            {
                var sbType = AccessTools.TypeByName("Amplitude.Mercury.Sandbox.Sandbox");
                int count = 0;
                try { count = Convert.ToInt32(AccessTools.Field(sbType, "NumberOfMajorEmpires")?.GetValue(null) ?? 0); } catch { }
                var empires = AccessTools.Field(sbType, "MajorEmpires")?.GetValue(null) as Array;

                for (int d = 0; d < domainEra.Length; d++) domainEra[d] = -1;
                techEra = -1;

                if (empires != null)
                    for (int i = 0; i < count && i < empires.Length; i++)
                    {
                        var emp = empires.GetValue(i);

                        // what this empire has BUILT (armies at sea/on land + air squadrons)
                        foreach (var collName in new[] { "Armies", "Squadrons" })
                        {
                            var groups = GetMember(emp, collName);
                            if (groups == null) continue;
                            foreach (var group in WalkCollection(groups, collName))
                            {
                                var units = GetMember(group, "Units");
                                if (units == null) continue;
                                foreach (var unit in WalkCollection(units, "Units"))
                                {
                                    var def = GetMember(unit, "UnitDefinition");
                                    if (def == null) continue;
                                    int uEra = EraFromName(GetMember(def, "Name")?.ToString());
                                    if (uEra < 0) continue;
                                    int dom = 0;
                                    try { dom = Convert.ToInt32(GetMember(def, "SpawnType")); } catch { }
                                    if (dom >= 0 && dom < domainEra.Length && uEra > domainEra[dom]) domainEra[dom] = uEra;
                                    if (uEra > era) era = uEra;
                                }
                            }
                        }

                        // research-based fallback: the most advanced empire's technological era
                        var dos = GetMember(emp, "DepartmentOfScience");
                        if (dos == null) continue;
                        try
                        {
                            var m = AccessTools.Method(dos.GetType(), "GetTechnologicalEra");
                            int t = m != null ? Convert.ToInt32(m.Invoke(dos, null))
                                              : Convert.ToInt32(GetMember(dos, "CurrentTechnologicalEraIndex") ?? -1);
                            if (t > techEra) techEra = t;
                        }
                        catch { }
                    }

                var timeline = AccessTools.Field(sbType, "Timeline")?.GetValue(null);
                if (timeline != null)
                    aggregateEra = Convert.ToInt32(AccessTools.Method(timeline.GetType(), "GetGlobalEraIndex")?.Invoke(timeline, null) ?? -1);
                if (era < 0) era = techEra;
                if (era < 0) era = aggregateEra;
            }
            catch (Exception ex) { if (!eraApiLogged) { eraApiLogged = true; Plugin.Log.LogWarning("[Resize] era read failed: " + ex.Message); } }
            if (!eraApiLogged && era >= 0)
            {
                eraApiLogged = true;
                Plugin.Log.LogInfo($"[Resize] era anchoring live — built-unit frontier: land {domainEra[0]}, naval {domainEra[1]}, air {domainEra[2]} (tech era {techEra}, aggregate Timeline {aggregateEra}); {eraGridRows.Count} authored grid row(s)");
            }
            if (era != cachedEra && cachedEra >= 0 && era >= 0)
                Plugin.Log.LogInfo($"[Resize] ERA CHANGED {cachedEra} -> {era} — scaled units re-anchor live (an era-1 unit now renders x{EraAnchorFor(1, era):0.###})");
            cachedEra = era;
            return era;
        }

        // THE GRID LOOKUP: modifier[unit's era][world's era]. A unit is authored once at its own era's size and only
        // DRIFTS as the world moves past it, so anything at or before its own era is 1.0 (unchanged) — an era-5 ship
        // appearing in era 5 renders exactly as authored, while an era-1 hull recedes by whatever the grid says.
        // An un-authored cell is 1.0 — NO invented curve (user rule): every number that changes a unit's size comes
        // from the Global Era Lab, so an empty grid means "sizes behave exactly as the Resize Lab rules say".
        // Amplitude's simulation collections (ReferenceCollection<T>, entity collections) do NOT all implement
        // IEnumerable — the first frontier build foreach'd them and silently walked NOTHING (log read
        // "naval -1" while an era-6 cruiser was on screen). So walk defensively: IEnumerable if offered, else
        // Count + indexer, else a backing Data array. Logs the type once per collection name when it finds no way in,
        // so a future engine change is diagnosable instead of silent.
        static HashSet<string> walkFailLogged;
        static IEnumerable<object> WalkCollection(object coll, string label)
        {
            if (coll == null) yield break;
            if (coll is System.Collections.IEnumerable en)
            {
                foreach (var x in en) if (x != null) yield return x;
                yield break;
            }
            var t = coll.GetType();
            int n = -1;
            try { n = Convert.ToInt32(GetMember(coll, "Count") ?? -1); } catch { }
            var getItem = t.GetMethod("get_Item", new[] { typeof(int) });
            if (n >= 0 && getItem != null)
            {
                for (int i = 0; i < n; i++)
                {
                    object v = null;
                    try { v = getItem.Invoke(coll, new object[] { i }); } catch { }
                    if (v != null) yield return v;
                }
                yield break;
            }
            if (GetMember(coll, "Data") is Array data)
            {
                int lim = n >= 0 && n <= data.Length ? n : data.Length;
                for (int i = 0; i < lim; i++)
                {
                    var v = data.GetValue(i);
                    if (v != null) yield return v;
                }
                yield break;
            }
            if (walkFailLogged == null) walkFailLogged = new HashSet<string>();
            if (walkFailLogged.Add(label))
                Plugin.Log.LogWarning($"[Resize] can't walk '{label}' ({t.FullName}) — no IEnumerable, no Count+indexer, no Data array; era frontier will fall back");
        }

        // Amplitude names every definition with its era ("Era1_Common_Biremes_01") — the cheapest reliable era source.
        static int EraFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            var m = Regex.Match(name, "Era(\\d+)", RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(m.Groups[1].Value, out int e) ? e : -1;
        }

        // The era a unit is measured against = MAX(its own domain's built frontier, the world's own era).
        //
        // Combining the two rather than preferring one (user) covers both failure modes: the built frontier alone
        // says nothing in a game where nobody bothered to build ships — the trireme would stay huge into the
        // Contemporary age — while the world era alone lags what is actually sailing. Taking the higher of the two
        // means the anchor only ever moves forward: ships pull it up the moment a modern hull exists, and general
        // progress carries it even at an empty sea.
        //
        // The FLOOR is deliberately the aggregate Timeline index, not the empires' technological era: the tech era
        // overshoots (fame-driven era advancement, no units to match), and inside a max() an overshooting floor
        // would undo the whole point of measuring what was built. The tech era stays a last resort if the aggregate
        // is unavailable.
        static int WorldEraFor(int domain)
        {
            CurrentEra();                                   // keep the 2s poll fresh
            int built = domain >= 0 && domain < domainEra.Length ? domainEra[domain] : -1;
            int floor = aggregateEra >= 0 ? aggregateEra : techEra;
            int era = built > floor ? built : floor;
            return era >= 0 ? era : cachedEra;
        }

        // Presentation profile -> UnitSpawnType domain: Boat(7) = Maritime(1), Plane(14)/Missile(15) = Air(2)/Missile(3),
        // everything else Land(0). The profile is what we already read when resolving the rule.
        static int DomainFromProfile(int prof) => prof == 7 ? 1 : prof == 14 ? 2 : prof == 15 ? 3 : 0;

        // NAVAL-ONLY FOR NOW (user rule, 2026-07-29): only ships age with the world. Ships are where the mismatch is
        // glaring (a trireme beside a battleship) and where scaling is safe — single-pawn, no formation spacing, no
        // gear anchors. Land and air keep their authored size in every era: crucially this leaves the cave-bear case
        // intact (an animal IS a land unit, and the user wants those scalable), it just doesn't drift with the eras.
        // Lifting the gate later is this one line, plus deciding whether the grid needs per-domain rows.
        const int NavalDomain = 1;                       // UnitSpawnType.Maritime
        static float EraAnchor(UnitScaleInfo info)
            => info.domain == NavalDomain ? EraAnchorFor(info.homeEra, WorldEraFor(info.domain)) : 1f;

        static readonly string[] EraNames = { "Neolithic", "Ancient", "Classical", "Medieval", "Early Modern", "Industrial", "Contemporary" };
        static string EraName(int era) => era >= 0 && era < EraNames.Length ? EraNames[era] : "?";

        // LIVE READOUT for the F8 window: the global era plus, per scaled unit, how its size is being composed
        // (rule x era-grid modifier = effective) and what the mesh buffer currently carries. Reading the same
        // statics the runtime uses means the window can't drift from the behaviour it reports.
        internal static IEnumerable<string> ResizeStatusLines()
        {
            var lines = new List<string>();
            int era = CurrentEra();
            string Front(int d) => domainEra[d] >= 0 ? $"{domainEra[d]} {EraName(domainEra[d])}" : "none";
            lines.Add(era < 0
                ? "World era: not in a game yet"
                : $"Anchor = max(built frontier, world era {aggregateEra}) — built: naval {Front(1)} | land {Front(0)} | air {Front(2)}   (tech era {techEra})");
            lines.Add($"era-grid rows authored: {eraGridRows.Count}   |   scaled units: {unitScaleByDesc.Count}");
            string[] domName = { "land", "naval", "air", "missile" };
            foreach (var kv in unitScaleByDesc)
            {
                var info = kv.Value;
                float mod = EraAnchor(info);
                string name = unitScaleNameByDesc.TryGetValue(kv.Key, out var n) ? n : $"desc {kv.Key}";
                string applied = descApplied.TryGetValue(kv.Key, out float a) ? $"applied x{a:0.###}" : "not drawn yet";
                string dom = info.domain >= 0 && info.domain < domName.Length ? domName[info.domain] : "?";
                string how = info.domain == NavalDomain
                    ? $"vs naval frontier {WorldEraFor(info.domain)} {EraName(WorldEraFor(info.domain))} -> x{mod:0.###}"
                    : "era ageing off (naval only for now)";
                lines.Add($"  {name}: rule x{info.scale:0.###} (own era {info.homeEra} {EraName(info.homeEra)}, {dom}) {how} = x{info.scale * mod:0.###}   [{applied}]");
            }
            if (unitScaleByDesc.Count == 0 && unitScaleRules.Count > 0)
                lines.Add($"  {unitScaleRules.Count} rule(s) loaded, none matched a live unit yet");
            return lines;
        }

        // Split from EraAnchor so the era POLL can log a sample without re-entering CurrentEra().
        static float EraAnchorFor(int homeEra, int now)
        {
            if (now < 1) now = 1;                       // Neolithic (0) / unknown: treat as the first era
            if (homeEra < 1) homeEra = 1;
            if (now <= homeEra) return 1f;              // its own age or earlier — nothing has aged yet
            if (eraGridRows.TryGetValue(homeEra, out var row) && now < row.Length && row[now] > 0f) return row[now];
            return 1f;                                  // un-authored cell = leave the unit alone
        }

        // ---- FORMATION BY SIZE (Global Era Lab second table) ----
        static readonly List<KeyValuePair<float, string>> formationBySize = new List<KeyValuePair<float, string>>();   // sorted asc: first threshold >= effective scale wins
        static readonly Dictionary<int, string> sizeFormApplied = new Dictionary<int, string>();       // descId -> formation currently applied ("" = the unit's own)
        static readonly Dictionary<string, string> sizeFormOriginal = new Dictionary<string, string>(StringComparer.Ordinal);   // unitDefName -> its original formation (for restore)
        static readonly HashSet<string> sizeFormWarned = new HashSet<string>(StringComparer.Ordinal);

        // Called from the per-frame scale path with the freshly computed effective scale. Cheap steady-state
        // (name cache + threshold walk + dictionary hit); the definition repoint + live re-form run only when the
        // desired formation actually CHANGES (i.e. the era anchor moved the unit across a threshold).
        // Thresholds are PER UNIT (Formation Override window, `sizeFormations` on the unit's link — user ruling
        // 2026-07-30); the legacy GLOBAL table from the Era Lab remains a fallback for units without their own.
        static readonly Dictionary<int, string> sizeFormUnitName = new Dictionary<int, string>();   // descId -> unit def name
        static void MaybeSwapFormationBySize(int descId, float effScale)
        {
            // resolve (and cache) the unit definition name for this descriptor
            if (!sizeFormUnitName.TryGetValue(descId, out var unitName))
            {
                try
                {
                    var pmT = AccessTools.TypeByName("Amplitude.Mercury.Animation.PawnManager");
                    var pmI = pmT?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                              ?? AccessTools.Field(pmT, "Instance")?.GetValue(null);
                    var defs = AccessTools.Field(pmT, "pawnDefinitions")?.GetValue(pmI) as System.Collections.IList;
                    var pawnDef = (defs != null && descId >= 0 && descId < defs.Count) ? defs[descId] : null;
                    if (pawnDef == null) return;                        // registration not settled — retry next frame
                    var uRef = AccessTools.Field(pawnDef.GetType(), "PresentationUnitDefinition")?.GetValue(pawnDef);
                    unitName = uRef?.GetType().GetProperty("XmlSerializableElementName")?.GetValue(uRef) as string ?? "";
                    sizeFormUnitName[descId] = unitName;
                }
                catch { return; }
            }
            if (unitName.Length == 0) return;                            // pawn def carries no unit — nothing to swap

            var table = FormationOverride.SizeThresholdsFor(unitName)
                        ?? (formationBySize.Count > 0 ? formationBySize : null);
            if (table == null) return;

            string desired = null;                                       // null = the unit's own formation
            for (int i = 0; i < table.Count; i++)
                if (effScale <= table[i].Key) { desired = table[i].Value; break; }
            var key = desired ?? "";
            if (sizeFormApplied.TryGetValue(descId, out var cur) && cur == key) return;

            try
            {

                var udb = Prober.ResolveDatabase("Amplitude.Mercury.Data.World.PresentationUnitDefinition");
                object unitDef = null;
                if (udb != null) foreach (var el in udb) if ((el as UnityEngine.Object)?.name == unitName) { unitDef = el; break; }
                if (unitDef == null) return;

                // remember the unit's own formation the first time we touch it (for the restore path)
                if (!sizeFormOriginal.TryGetValue(unitName, out var original))
                {
                    var fr = AccessTools.Field(unitDef.GetType(), "PresentationFormationDefinition")?.GetValue(unitDef);
                    original = fr?.GetType().GetProperty("XmlSerializableElementName")?.GetValue(fr) as string ?? "";
                    sizeFormOriginal[unitName] = original;
                }
                var targetFormation = desired ?? original;
                if (string.IsNullOrEmpty(targetFormation)) { sizeFormApplied[descId] = key; return; }

                // the formation must exist in the live database (vanilla, or injected by the Formation axis)
                var fdb = Prober.ResolveDatabase("Amplitude.Mercury.Data.PresentationFormationDefinition");
                bool found = false;
                if (fdb != null) foreach (var el in fdb) if ((el as UnityEngine.Object)?.name == targetFormation) { found = true; break; }
                if (!found)
                {
                    if (sizeFormWarned.Add(targetFormation))
                        Plugin.Log.LogWarning($"[Resize] formation-by-size: '{targetFormation}' not in the live formation database — " +
                                              "author/link it via the Formation Override window (a saved entry injects it at load). Swap skipped.");
                    sizeFormApplied[descId] = key;   // don't retry every frame; a relaunch with the formation present fixes it
                    return;
                }

                FormationOverride.SetFreshElementReference(unitDef, "PresentationFormationDefinition", targetFormation);
                int reformed = ReformLiveUnitsOf(unitName);
                sizeFormApplied[descId] = key;
                Plugin.Log.LogInfo($"[Resize] formation-by-size: '{unitName}' at effective x{effScale:0.###} -> " +
                                   (desired == null ? $"restored own formation '{targetFormation}'" : $"'{targetFormation}'") +
                                   $" ({reformed} live unit(s) re-formed).");
            }
            catch (Exception ex) { if (sizeFormWarned.Add("EX")) Plugin.Log.LogError("[Resize] formation-by-size: " + ex); }
        }

        // Re-run the game's own UpdatePawns on every live unit of the given definition (the FormationOverride
        // re-instantiation idiom) so a mid-game formation swap shows without a save/load.
        static int ReformLiveUnitsOf(string unitDefName)
        {
            int n = 0;
            try
            {
                var presType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.Presentation");
                var factory = presType == null ? null : AccessTools.Field(presType, "PresentationEntityFactoryController")?.GetValue(null);
                var armies = factory == null ? null : GetMember(factory, "PresentationArmyEntities") as Array;
                if (armies == null) return 0;
                foreach (var army in armies)
                {
                    var unit = army == null ? null : GetMember(army, "PresentationUnit");
                    if (unit == null) continue;
                    var pdef = GetMember(unit, "PresentationUnitDefinition");
                    var pdn = (pdef as UnityEngine.Object)?.name ?? "";
                    if (!string.Equals(pdn, unitDefName, StringComparison.OrdinalIgnoreCase)) continue;
                    bool loaded = true; try { loaded = Convert.ToBoolean(GetMember(unit, "IsLoaded")); } catch { }
                    if (!loaded) continue;
                    bool naval = false; try { naval = Convert.ToBoolean(GetMember(unit, "IsNaval")); } catch { }
                    try { AccessTools.Method(unit.GetType(), "UpdatePawns", new[] { typeof(bool) })?.Invoke(unit, new object[] { naval }); n++; }
                    catch { }
                }
            }
            catch { }
            return n;
        }

        static void ApplyVanillaScale(PawnCtx ctx, UnitScaleInfo info)
        {
            try
            {
                float target = info.scale * EraAnchor(info);
                MaybeSwapFormationBySize(ctx.descId, target);

                // PLACEMENT half — every frame (the game rebuilds pawnEntries[] from scratch each frame)
                var oss = GetMember(ctx.entry, "ObjectSpace");
                SetMember(oss, "Scale", Convert.ToSingle(GetMember(oss, "Scale")) * target);
                SetMember(ctx.entry, "ObjectSpace", oss);
                ctx.pawnEntries.SetValue(ctx.entry, ctx.idx);

                // GEOMETRY half — only when the target actually differs from what the buffer already carries
                if (!descApplied.TryGetValue(ctx.descId, out float cur) || Math.Abs(cur - target) > 1e-4f)
                    ScaleDescriptorMeshes(ctx.descId, target);
            }
            catch (Exception ex) { if (!poseErrLogged) { poseErrLogged = true; Plugin.Log.LogError("[Resize] " + ex); } }
        }

        static void ScaleDescriptorMeshes(int descId, float target)
        {
            try
            {
                var am = animMgrRef;
                if (am == null) return;   // registration pass not seen yet — the per-frame path retries
                var pmType = AccessTools.TypeByName("Amplitude.Mercury.Animation.PawnManager");
                var pm = pmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                         ?? AccessTools.Field(pmType, "Instance")?.GetValue(null);
                if (pm == null) return;
                var descs = AccessTools.Field(pmType, "gpuPawnDescriptorEntries")?.GetValue(pm) as Array;
                var gfrags = AccessTools.Field(pmType, "gpuPawnDescriptorFragmentEntries")?.GetValue(pm) as Array;
                if (descs == null || gfrags == null || descId < 0 || descId >= descs.Length) return;

                var mcm = GetMember(am, "FxComponentMeshContentManager");
                var layerObj = GetMember(am, "FXMeshLayerIndex");
                int layerIdx = layerObj is int li ? li : Convert.ToInt32(layerObj ?? 0);
                var layersArr = AccessTools.Field(mcm?.GetType(), "layers")?.GetValue(mcm) as Array;
                if (layersArr == null || layerIdx < 0 || layerIdx >= layersArr.Length) { Plugin.Log.LogWarning("[Resize] mesh layers unreachable"); return; }
                var layer = layersArr.GetValue(layerIdx);
                var meshTable = GetMember(layer, "HxFxOneMeshComputeBufferData") as Array;   // FxOneMeshStruct[]
                var vertBufObj = AccessTools.Field(layer.GetType(), "vertexBuffer")?.GetValue(layer);
                var meshBufObj = AccessTools.Field(layer.GetType(), "hxFxOneMeshComputeBuffer")?.GetValue(layer);
                var verts = vertBufObj == null ? null : GetMember(vertBufObj, "WriteContent") as Array;
                if (meshTable == null || verts == null) { Plugin.Log.LogWarning("[Resize] mesh/vertex buffers unreachable"); return; }

                // SAFETY: only the Bones format stores Pos as raw floats — anything else would be corrupted
                var posF = verts.GetType().GetElementType().GetField("Pos");
                if (posF == null || posF.FieldType != typeof(UnityEngine.Vector3))
                { Plugin.Log.LogWarning($"[Resize] layer {layerIdx} vertex format has no raw Pos — skipping (format {verts.GetType().GetElementType().Name})"); return; }

                var dEntry = descs.GetValue(descId);
                var dT = dEntry.GetType();
                uint start = (uint)dT.GetField("StartFragment").GetValue(dEntry);
                uint count = (uint)dT.GetField("FragmentCount").GetValue(dEntry);
                var feType = gfrags.GetType().GetElementType();
                var encGpuF = feType.GetField("EncodedMeshAndVisualParticleCountFxMeshIndex");
                var msType = meshTable.GetType().GetElementType();
                var siF = msType.GetField("StartIndex"); var svF = msType.GetField("StartVertex");
                var vcF = msType.GetField("VertexCount");
                var mbMinF = msType.GetField("BBoxMin"); var mbMaxF = msType.GetField("BBoxMax");

                int meshesScaled = 0, vertsScaled = 0;
                float ratio = 1f;                 // the ratio actually applied to geometry this pass (bbox follows it)
                for (uint fi = 0; fi < count && start + fi < gfrags.Length; fi++)
                {
                    uint enc = Convert.ToUInt32(encGpuF.GetValue(gfrags.GetValue((int)(start + fi))));
                    if (enc == 0) continue;                         // hidden / none
                    uint startIndex = enc & 0xFFFFFF;               // low 24 bits = the mesh's index-buffer start
                    for (int mi = 1; mi < meshTable.Length; mi++)   // 0 = the none mesh
                    {
                        var mEntry = meshTable.GetValue(mi);
                        if (Convert.ToUInt32(siF.GetValue(mEntry)) != startIndex) continue;
                        int vc = Convert.ToInt32(vcF.GetValue(mEntry));
                        if (vc <= 0) break;
                        long key = ((long)layerIdx << 32) | (uint)mi;
                        uint sv = Convert.ToUInt32(svF.GetValue(mEntry));
                        if (sv >= verts.Length) break;
                        var probeNow = (UnityEngine.Vector3)posF.GetValue(verts.GetValue((int)sv));

                        // trust the record only if the buffer still holds the data we wrote (see MeshScale)
                        float applied = 1f;
                        if (meshApplied.TryGetValue(key, out var st) && (st.probe - probeNow).sqrMagnitude < 1e-8f)
                            applied = st.factor;
                        else if (meshApplied.ContainsKey(key))
                            Plugin.Log.LogInfo($"[Resize] mesh {mi} came back unscaled (Fx content reloaded) — re-scaling from 1");

                        ratio = target / applied;                       // only the DIFFERENCE — re-scaling never compounds
                        if (Math.Abs(ratio - 1f) > 1e-4f)
                        {
                            for (uint v = sv; v < sv + vc && v < verts.Length; v++)
                            {
                                var vert = verts.GetValue((int)v);
                                posF.SetValue(vert, (UnityEngine.Vector3)posF.GetValue(vert) * ratio);
                                verts.SetValue(vert, (int)v);
                            }
                            mbMinF.SetValue(mEntry, (UnityEngine.Vector3)mbMinF.GetValue(mEntry) * ratio);
                            mbMaxF.SetValue(mEntry, (UnityEngine.Vector3)mbMaxF.GetValue(mEntry) * ratio);
                            meshTable.SetValue(mEntry, mi);
                            meshesScaled++; vertsScaled += vc;
                        }
                        meshApplied[key] = new MeshScale { factor = target, probe = probeNow * ratio };
                        break;
                    }
                }

                // BBox = culling only, so it deliberately errs LARGE: follow the geometry ratio when we moved
                // vertices; if the geometry was already at target but this descriptor is new to us (a fresh
                // session rebuilt it with a vanilla bbox), take the full target once.
                float descRatio = meshesScaled > 0 ? ratio : (descApplied.ContainsKey(descId) ? 1f : target);
                if (meshesScaled > 0 || Math.Abs(descRatio - 1f) > 1e-4f)
                {
                    AccessTools.Method(vertBufObj.GetType(), "Apply", Type.EmptyTypes)?.Invoke(vertBufObj, null);
                    AccessTools.Method(meshBufObj?.GetType(), "Apply", Type.EmptyTypes)?.Invoke(meshBufObj, null);
                    // descriptor bbox (culling) + re-upload — same ratio discipline as the vertices
                    dT.GetField("BBoxMin")?.SetValue(dEntry, (UnityEngine.Vector3)dT.GetField("BBoxMin").GetValue(dEntry) * descRatio);
                    dT.GetField("BBoxMax")?.SetValue(dEntry, (UnityEngine.Vector3)dT.GetField("BBoxMax").GetValue(dEntry) * descRatio);
                    descs.SetValue(dEntry, descId);
                    AccessTools.Field(pmType, "descriptorBufferDirty")?.SetValue(pm, true);
                }
                descApplied[descId] = target;   // records the target even when nothing moved, so the per-frame path stops re-entering
                // Log the anchor ACTUALLY used, not the cached world era — the first build printed cachedEra (the
                // tech-era fallback, 6) while the modifier had come from the aggregate floor (5), which made a
                // correct-looking line describe the wrong arithmetic.
                int anchorUsed = unitScaleByDesc.TryGetValue(descId, out var dInfo) ? WorldEraFor(dInfo.domain) : cachedEra;
                Plugin.Log.LogInfo($"[Resize] desc {descId} -> x{target:0.###} (anchor era {anchorUsed} {EraName(anchorUsed)}): {meshesScaled} mesh(es), {vertsScaled} vert(s) re-scaled by {descRatio:0.###}x + per-pawn placement x{target:0.###}");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Resize] mesh scale: " + ex); }
        }

    }
}
