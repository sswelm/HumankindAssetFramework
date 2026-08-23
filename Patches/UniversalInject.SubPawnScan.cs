using System;
using System.Collections.Generic;

namespace HumankindAssetFramework
{
    // THE LIVE SUB-PAWNS THAT BELONG TO OUR UNITS — one shared source for every consumer (ProcessEngineAudio,
    // ProcessSubPawnVisuals), perf pass 2026-08-21, driven by the FrameCost meter.
    //
    // Two ways to find them:
    //   SCENE SCAN  — UnityEngine.Object.FindObjectsOfType(PresentationSubPawn) + a name match per component. A FULL
    //                 scene walk: ~50 ms on a busy map. Two pollers each ran their own (every 2 s / 3 s) = ~1.7 ms/frame
    //                 averaged, delivered as stalls; one shared scan every 5 s still averaged ~0.33 ms/frame.
    //   WALK        — the presentation tree the plugin already walks elsewhere: armies (+ battle units) -> PresentationUnit
    //                 -> entry (cached per unit, ResolveUnitEntry) -> Pawns -> SubPawns[0..SubPawnCount). Touches only OUR
    //                 units' pawns: ~100 reflection reads, not a scene scan. Runs every 2 s again (the old latency).
    // The walk is SELF-VERIFIED against the scene scan: the first non-empty scan of a session runs both and compares;
    // if the walk misses anything the scene scan stays in charge for the session and the log names it ([SubPawnScan]).
    // Consumers filter the shared list by their own entry flags and watch `version` to run their per-rescan bookkeeping
    // exactly once per rescan. Main-thread only (Plugin.Update).
    internal static partial class UniversalInject
    {
        internal const float SubPawnScanMaxAge = 2f;
        [SessionScoped(Manual = "MarkSubPawnsDirtyAndReverify")] static List<KeyValuePair<UnityEngine.Object, ModelEntry>> _subPawnScan;
        static float _subPawnScanAt;
        static bool _subPawnScanDirty = true;
        static int _subPawnScanVersion;
        static bool _subPawnWalkVerified, _subPawnWalkTrusted = true;

        internal static void MarkSubPawnsDirty() => _subPawnScanDirty = true;
        // `_subPawnScan = null` added 2026-08-23. The dirty flag alone forces a REBUILD before the next read, so this was
        // never a correctness bug — but the sole caller is the model session reset, whose own comment says "session-1
        // sub-pawn components are corpses", and clearing only the flag left the list holding references to every one of
        // those destroyed Unity objects until something happened to ask for the scan again. Drop them here, which also
        // makes the field's [SessionScoped(Manual = "MarkSubPawnsDirtyAndReverify")] annotation true rather than
        // approximately true. Found by the guard written for the footprint latch, not by hand.
        internal static void MarkSubPawnsDirtyAndReverify() { _subPawnScan = null; _subPawnScanDirty = true; _subPawnWalkVerified = false; _subPawnWalkTrusted = true; }

        internal static List<KeyValuePair<UnityEngine.Object, ModelEntry>> OurSubPawns(List<ModelEntry> list, float now, out int version)
        {
            // the trusted walk is cheap -> 2 s; the scene-scan fallback is the old 50 ms stall -> the old 5 s cadence
            if (_subPawnScan == null || _subPawnScanDirty || now - _subPawnScanAt > (_subPawnWalkTrusted ? SubPawnScanMaxAge : 5f))
            {
                _subPawnScan = Scan(list);
                _subPawnScanAt = now; _subPawnScanDirty = false; _subPawnScanVersion++;
            }
            version = _subPawnScanVersion;
            return _subPawnScan;
        }

        static List<KeyValuePair<UnityEngine.Object, ModelEntry>> Scan(List<ModelEntry> list)
        {
            if (list == null) return new List<KeyValuePair<UnityEngine.Object, ModelEntry>>();
            if (!_subPawnWalkTrusted) return SceneScan(list);
            var byWalk = WalkSubPawns(list);
            if (!_subPawnWalkVerified)
            {
                // ONE-TIME self-check per session, deferred until something is actually on the map (both empty proves nothing)
                var byScene = SceneScan(list);
                if (byWalk.Count == 0 && byScene.Count == 0) return byWalk;
                _subPawnWalkVerified = true;
                var walkIds = new HashSet<int>(); var sceneIds = new HashSet<int>();
                foreach (var p in byWalk) if (p.Key != null) walkIds.Add(p.Key.GetInstanceID());
                foreach (var p in byScene) if (p.Key != null) sceneIds.Add(p.Key.GetInstanceID());
                int missed = 0; var missedNames = new List<string>(); var walkOnly = new List<string>();
                foreach (var p in byScene)
                    if (p.Key != null && !walkIds.Contains(p.Key.GetInstanceID()))
                    {
                        missed++;
                        if (missedNames.Count < 12) missedNames.Add($"{(p.Key as UnityEngine.Component)?.gameObject.name}→{p.Value.resourceName}");
                        // name the HOLDER: the component types up the missed sub-pawn's transform chain (first 3 misses)
                        if (missed <= 3 && p.Key is UnityEngine.Component mc && mc != null)
                        {
                            var chain = new List<string>();
                            for (var tr = mc.transform; tr != null && chain.Count < 8; tr = tr.parent)
                            {
                                var comps = tr.GetComponents<UnityEngine.Component>();
                                var names = new List<string>();
                                foreach (var c in comps) if (c != null && !(c is UnityEngine.Transform)) names.Add(c.GetType().Name);
                                chain.Add($"{tr.name}[{string.Join("+", names)}]");
                            }
                            Plugin.Log.LogInfo($"[SubPawnScan] missed '{mc.gameObject.name}' parent chain: {string.Join(" <- ", chain)}");
                            // and the parent pawn's own view of it: SubPawns length vs SubPawnCount, and whether its unit resolves to an entry
                            var ppawn = mc.transform.parent != null ? (object)mc.transform.parent.GetComponent(GameBinding.PresentationPawn) : null;
                            if (ppawn != null)
                            {
                                int len = (GetMember(ppawn, "SubPawns") as Array)?.Length ?? -1; object cntObj = GetMember(ppawn, "SubPawnCount");
                                var punit = GetMember(ppawn, "PresentationUnit"); var pe = ResolveUnitEntry(punit);
                                int unitPawns = -1; if (GetMember(punit, "Pawns") is System.Collections.IEnumerable pp) { unitPawns = 0; foreach (var _ in pp) unitPawns++; }
                                Plugin.Log.LogInfo($"[SubPawnScan]   parent pawn: SubPawns.Length={len} SubPawnCount={cntObj ?? "?"} unit->entry={(pe?.resourceName ?? "NULL")} unit.Pawns={unitPawns}");
                            }
                        }
                    }
                foreach (var p in byWalk)
                    if (p.Key != null && !sceneIds.Contains(p.Key.GetInstanceID()) && walkOnly.Count < 12)
                        walkOnly.Add($"{(p.Key as UnityEngine.Component)?.gameObject.name}→{p.Value.resourceName}");
                if (missed > 0)
                {
                    _subPawnWalkTrusted = false;
                    Plugin.Log.LogInfo("[SubPawnScan] walk census: " + WalkCensus(list));   // what each holder list actually yielded
                    Plugin.Log.LogWarning($"[SubPawnScan] walk found {byWalk.Count} sub-pawn(s), scene scan {byScene.Count}, walk MISSED {missed} — using the scene scan this session (5 s cadence). " +
                                          $"missed: {string.Join(", ", missedNames)} | walk-only: {string.Join(", ", walkOnly)}");
                    return byScene;
                }
                Plugin.Log.LogInfo($"[SubPawnScan] walk verified against the scene scan: {byWalk.Count} sub-pawn(s), none missed (scene scan {byScene.Count}) — walk in charge, 2 s cadence");
            }
            return byWalk;
        }


        // ON-DEMAND AUDIT for the smoke test (2026-08-21): re-run the walk-vs-scene comparison NOW, regardless of the
        // once-per-session self-verify (which may have run before a later-spawning unit type existed). Returns the
        // two counts and the missed sub-pawns by name. One FindObjectsOfType — fine on a button, never per frame.
        internal static void AuditSubPawnWalk(List<ModelEntry> list, out int walk, out int scene, List<string> missed)
        {
            walk = scene = 0;
            if (list == null) return;
            var byWalk = WalkSubPawns(list); var byScene = SceneScan(list);
            walk = byWalk.Count; scene = byScene.Count;
            var walkIds = new HashSet<int>();
            foreach (var p in byWalk) if (p.Key != null) walkIds.Add(p.Key.GetInstanceID());
            foreach (var p in byScene)
                if (p.Key != null && !walkIds.Contains(p.Key.GetInstanceID()) && missed.Count < 12)
                    missed.Add($"{(p.Key as UnityEngine.Component)?.gameObject.name}→{p.Value.resourceName}");
        }

        // The old method, kept as the verification oracle + the fallback.
        static List<KeyValuePair<UnityEngine.Object, ModelEntry>> SceneScan(List<ModelEntry> list)
        {
            var fresh = new List<KeyValuePair<UnityEngine.Object, ModelEntry>>();
            var spType = GameBinding.PresentationSubPawn;
            if (spType != null)
                foreach (var o in UnityEngine.Object.FindObjectsOfType(spType))
                {
                    if (!(o is UnityEngine.Component c) || c == null) continue;
                    var m = LongestMatch(list, c.gameObject.name, x => x.pawnDescription);
                    if (m != null) fresh.Add(new KeyValuePair<UnityEngine.Object, ModelEntry>(o, m));
                }
            return fresh;
        }

        // The targeted walk: map armies + battle units -> our units only -> their pawns' SubPawns.
        static List<KeyValuePair<UnityEngine.Object, ModelEntry>> WalkSubPawns(List<ModelEntry> list)
        {
            var result = new List<KeyValuePair<UnityEngine.Object, ModelEntry>>();
            try
            {
                var presType = GameBinding.Presentation;
                if (presType == null) return result;
                var factory = CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                if (GetMember(factory, "PresentationArmyEntities") is Array armies)
                    foreach (var army in armies)
                        if (army != null) AddUnitSubPawns(army, GetMember(army, "PresentationUnit"), list, result);
                // AIR units are SQUADRONS, a sibling list to the armies (the first drill's 20 missed sub-pawns were all
                // zeppelins; tools/typeprobe showed presentationSquadronEntities : PresentationSquadron[] with a PresentationUnit).
                // A squadron's PresentationUnit reports 0 Pawns (drill census) — its sub-pawns live under the holder's
                // transform, so AddUnitSubPawns falls back to a subtree component search on the holder.
                if (GetMember(factory, "presentationSquadronEntities") is Array squadrons)
                    foreach (var sq in squadrons)
                        if (sq != null) AddUnitSubPawns(sq, GetMember(sq, "PresentationUnit"), list, result);
                // …and a squadron's PAWNS hang off its AIR FORMATION, not its PresentationUnit (drill census: 0 pawns there):
                // Presentation.PresentationAirPatrolController.presentationAirFormations -> airFormationUnits (PresentationAirUnit,
                // each with its own PresentationUnit for the entry match and a MainPawn carrying the SubPawns).
                var airCtl = CachedField(presType, "PresentationAirPatrolController")?.GetValue(null);
                if (GetMember(airCtl, "presentationAirFormations") is System.Collections.IEnumerable formations)
                    foreach (var f in formations)
                        if (GetMember(f, "airFormationUnits") is System.Collections.IEnumerable airUnits)
                            foreach (var au in airUnits)
                            {
                                AddPawnSubPawns(GetMember(au, "MainPawn"), ResolveUnitEntry(GetMember(au, "PresentationUnit")), list, result);   // null entry -> per-sub-pawn name match
                            }
                var bctl = CachedField(presType, "PresentationBattleReportController")?.GetValue(null);
                if (GetMember(bctl, "Battles") is System.Collections.IEnumerable battles)
                    foreach (var b in battles)
                        if (GetMember(b, "AllUnits") is System.Collections.IEnumerable allUnits)
                            foreach (var bu in allUnits) AddUnitSubPawns(bu, GetMember(bu, "PresentationUnit"), list, result);
            }
            catch (Exception ex) { Plugin.LogOnceWarning("subpawn-walk", "[SubPawnScan] walk failed (" + ex.Message + ") — scene scan takes over"); _subPawnWalkTrusted = false; }
            return result;
        }

        // ONE-TIME diagnostic on a verification miss: per holder list, how many holders, how many resolved to one of OUR
        // entries, and for those the pawn / sub-pawn counts — so a missed unit type names the list or the hop it hides behind.
        static string WalkCensus(List<ModelEntry> list)
        {
            try
            {
                var presType = GameBinding.Presentation;
                var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
                if (factory == null) return "no factory";
                var sb = new System.Text.StringBuilder();
                foreach (var listName in new[] { "PresentationArmyEntities", "presentationSquadronEntities" })
                {
                    var arr = GetMember(factory, listName) as Array;
                    if (arr == null) { sb.Append(listName).Append("=<null> "); continue; }
                    int holders = 0, ours = 0, pawns = 0, subs = 0; var names = new List<string>();
                    foreach (var h in arr)
                    {
                        if (h == null) continue; holders++;
                        var unit = GetMember(h, "PresentationUnit");
                        var e = ResolveUnitEntry(unit);
                        if (e == null) continue;
                        ours++;
                        if (names.Count < 6) names.Add(e.resourceName);
                        if (GetMember(unit, "Pawns") is System.Collections.IEnumerable ps)
                            foreach (var p in ps)
                            {
                                pawns++;
                                if (GetMember(p, "SubPawns") is Array sa) { int c = sa.Length; try { var cc = GetMember(p, "SubPawnCount"); if (cc != null) c = Math.Min(c, Convert.ToInt32(cc)); } catch { } subs += c; }
                            }
                    }
                    sb.Append(listName).Append($"={holders} holders, {ours} ours ({string.Join("/", names)}), {pawns} pawns, {subs} sub-pawns; ");
                }
                // the air formations (where a squadron's pawns actually live)
                var airCtl = CachedField(presType, "PresentationAirPatrolController")?.GetValue(null);
                int forms = 0, aus = 0, ourAus = 0, auSubs = 0;
                if (GetMember(airCtl, "presentationAirFormations") is System.Collections.IEnumerable formations)
                    foreach (var fm in formations)
                    {
                        forms++;
                        if (!(GetMember(fm, "airFormationUnits") is System.Collections.IEnumerable units)) continue;
                        foreach (var au in units)
                        {
                            aus++;
                            if (ResolveUnitEntry(GetMember(au, "PresentationUnit")) == null) continue;
                            ourAus++;
                            if (GetMember(GetMember(au, "MainPawn"), "SubPawns") is Array sa) auSubs += sa.Length;
                        }
                    }
                sb.Append($"airFormations={forms} ({(airCtl == null ? "controller NULL" : "ok")}), airUnits={aus}, ours={ourAus}, their sub-pawns={auSubs}");
                return sb.ToString();
            }
            catch (Exception ex) { return "census failed: " + ex.Message; }
        }

        // `e` may be null: a unit whose DEFINITION name does not contain its pawnDescription (drill 2026-08-21: the
        // hovercraft and the drones — 'Era6_Common_Hovercrafts_01' pawns under a differently-named unit) resolves to
        // nothing by unit name, so each sub-pawn is then matched by the SAME criterion the scene scan uses — its
        // GameObject name against pawnDescription — and the two methods cannot disagree.
        static void AddPawnSubPawns(object pawn, ModelEntry e, List<ModelEntry> list, List<KeyValuePair<UnityEngine.Object, ModelEntry>> result)
        {
            if (pawn == null || !(GetMember(pawn, "SubPawns") is Array subs)) return;
            // NO SubPawnCount cap (drill 2026-08-21: hovercraft / drone sub-pawns sat in SubPawns[] while the count read 0) — every
            // non-null slot counts; a destroyed one reads as Unity fake-null and is skipped
            for (int i = 0; i < subs.Length; i++)
            {
                if (!(subs.GetValue(i) is UnityEngine.Object o) || !o) continue;
                var m = e ?? (o is UnityEngine.Component c ? LongestMatch(list, c.gameObject.name, x => x.pawnDescription) : null);
                if (m != null) result.Add(new KeyValuePair<UnityEngine.Object, ModelEntry>(o, m));
            }
        }

        static void AddUnitSubPawns(object holder, object unit, List<ModelEntry> list, List<KeyValuePair<UnityEngine.Object, ModelEntry>> result)
        {
            var e = ResolveUnitEntry(unit);   // null = not ours BY UNIT NAME; the sub-pawns still get the name match below
            int before = result.Count;
            if (GetMember(unit, "Pawns") is System.Collections.IEnumerable pawns)
                foreach (var pawn in pawns) AddPawnSubPawns(pawn, e, list, result);
            if (e == null) return;
            // no sub-pawns through the pawn list (a squadron): search the holder's own transform subtree instead —
            // a bounded Unity walk of ONE unit's hierarchy, not the scene
            if (result.Count == before && holder is UnityEngine.Component hc && hc != null && GameBinding.PresentationSubPawn is Type spType)
                foreach (var o in hc.GetComponentsInChildren(spType, true))
                    if (o != null) result.Add(new KeyValuePair<UnityEngine.Object, ModelEntry>(o, e));
        }
    }
}
