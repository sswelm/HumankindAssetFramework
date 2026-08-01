// FormationOverridePatch.cs — FORMATION override (the fifth data axis; the first with ZERO baked assets).
//
// Links a unit (PresentationUnitDefinition name) to a CUSTOM formation authored in the editor project, changing how
// many pawn models the unit displays and where they stand. The custom PresentationFormationDefinition never ships in
// a bundle at all: a mod-bundle formation would never enter the game's datatable system (references resolve BY NAME
// via Databases.GetDatabase — the same catalog gap the prop axis hit with MeshCollections). Instead the editor's
// Formation Override window serializes the FULL formation data (dummies + the six hidden per-orientation
// ColumnsCountPerRow grids) into enc_formations.json, and this patch rebuilds it as a runtime ScriptableObject and
// adds it to the live database through the PUBLIC Database<T>.Add — instantly visible to every by-name lookup.
//
// Engine facts this rides on (decompiled 2026-07-27):
//   • pawn count on the map = ceil(healthRatio × Formation.DummyCount); Dummies.Length IS the max pawn count.
//   • PresentationUnit resolves its formation LAZILY at spawn via DatatableElementReference.GetDatatableElement,
//     which walks Database<T> datatables newest-first — an element Add'ed at AnimationLoad wins cleanly.
//   • the reference struct CACHES its resolved element (element + databaseRevision); repointing must install a
//     FRESH struct (element = null), not mutate the old one's name, or the stale cache keeps returning the old
//     formation until the next database Commit.
//   • BuildDummiesGrid indexes grid[orientation][x][y] straight from each dummy's CoordinatePerDirection against
//     the ColumnsCountPerRow arrays — inconsistent data throws during load (= the generic "mismatched mods" dialog),
//     so every entry is VALIDATED here and invalid ones are skipped loudly instead of crashing the session.
//
// The dummy-pool ceiling IS enforced: every pooled Formation3D is cloned from Formation3DPrefab, whose fixed set of
// dummy children caps DummyCount (BuildDummiesGrid indexes Dummies[k] beyond it = IndexOutOfRange during load). A
// prefix on PresentationGameObjectPoolController.DoStart (the pool's construction site) grows the prefab's Dummies
// by cloning its last dummy child until the registry's biggest formation fits — before the first pool clone exists.
// If the unit name or formation data is wrong nothing breaks: the entry logs and is dropped for the session.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HumankindAssetFramework
{
    internal static class FormationOverride
    {
        class Cell { public int x, y; }
        class Dummy { public Vector3 pos; public readonly List<Cell> coords = new List<Cell>(); }
        class Entry
        {
            public string unit = "", formation = "", lowSpec = "";
            public readonly List<Dummy> dummies = new List<Dummy>();
            public readonly int[][] columns = new int[6][];
            public float dummyOffset = -1f;   // RUNTIME override of the unit's random per-model jitter (CoordinationValues.DummyOffsetPosition). -1 = leave vanilla; >=0 sets BOTH axes (0 = perfectly on the dummy grid, small = tightly packed). Lets a custom formation read as a clean block instead of a loose scatter, no rebuild.
            public float scale = -1f;         // RUNTIME per-model scale multiplier (pawn root localScale; the GPU TRS path reads lossyScale). -1/1 = vanilla size; 0.7 = smaller models (denser formations read better), >1 = larger. EXPERIMENTAL: uniform only.
            public float layoutScale = -1f;   // RUNTIME multiplier on the DUMMY POSITIONS at injection. -1 = FOLLOW `scale` (the natural reading: scaling a formation shrinks models AND spacing together); set explicitly to decouple footprint from model size (e.g. small men, wide skirmish line).
            public string scaleMode = "transform";   // "transform" = pawn root localScale (v1: simple, decent bodies/spacing; rigid gear mis-anchors on humans) | "data" = cloned skeleton with scaled binds+meshes (v2: deep; humans WIP — procedural bone layers ignore it)
            public readonly List<KeyValuePair<float, string>> sizeForms = new List<KeyValuePair<float, string>>();   // formation-by-size rows (sorted asc): era ageing swaps this unit's formation when effective scale <= threshold
            public bool done;      // this session: injected (if data) + repointed, or dropped after a permanent error
        }

        static bool parsed;
        static readonly List<Entry> entries = new List<Entry>();
        // Injected formation SOs live for the whole process (HideAndDontSave keeps Unity's unused-asset sweep off
        // them); re-Add per session is cheap and guarded by a by-name DB lookup.
        static readonly Dictionary<string, ScriptableObject> created = new Dictionary<string, ScriptableObject>(StringComparer.Ordinal);
        static bool pending;       // armed by AnimationLoad; Tick() retries until the databases resolve
        static int lastTryFrame;
        static bool dbWaitLogged;
        // POST-APPLY RE-INSTANTIATION (2026-07-28): a unit that spawned BEFORE the override landed built its pawn grid off
        // the OLD formation (e.g. 9 dummies) and keeps it — the plugin grows the Formation3D prefab + overwrites the
        // formation, but an already-spawned unit doesn't rebuild (the AI armies that spawned later got 12; the player's
        // starting Warriors, spawned during load before the override applied, stayed 9). A bundle rebuild can't fix this —
        // the 9-slot cap is the vanilla Formation3D PREFAB, which only the plugin grows at runtime. So once the override is
        // applied, force each live unit of a repointed type to re-run the game's own UpdatePawns (ReleasePawns +
        // InstantiatePawns -> re-inits the formation via the grown prefab -> the full dummy count).
        static bool appliedAny;                                            // ≥1 entry applied this session — arm re-instantiation
        static int reformScanFrame;                                        // throttle counter for the live-unit scan
        static readonly HashSet<object> reformed = new HashSet<object>();  // units already re-formed (or logged) this session, once each
        static readonly HashSet<object> reformPresent = new HashSet<object>();  // reused each scan (Clear, not new) — live matched units this pass
        static bool reformSettled;                                         // catch-up complete this session -> stop the ~12x/s scan (a reload re-arms it)
        static int reformQuietScans;                                       // consecutive scans that handled nothing new
        const int ReformQuietLimit = 60;                                   // settle after ~5s of quiet (~12 body-scans/s), long enough to cover the load-time catch-up

        // Called from UniRegisterHook's AnimationLoad postfix — once per game session, before pawn resolution.
        internal static void OnAnimationLoad()
        {
            try
            {
                if (Plugin.FormationOverrideOn == null || !Plugin.FormationOverrideOn.Value) return;
                EnsureConfig();
                if (entries.Count == 0) return;
                foreach (var e in entries) e.done = false;   // fresh session: repoint again (defs reload per session)
                appliedAny = false; reformed.Clear();        // fresh session: re-arm + drop last session's unit objects
                reformSettled = false; reformQuietScans = 0;  // re-arm the re-instantiate catch-up for this session's load-time units
                fragDefsDone.Clear(); clonesRegisteredThisSession.Clear();   // equipment counter-scale: re-process defs + re-register clones (AnimationLoad cleared the manager)
                pending = true;
                TryApply();                                   // normally succeeds right here
            }
            catch (Exception ex) { Plugin.Log.LogError("[Formation] OnAnimationLoad: " + ex); }
        }

        // Per-frame (Plugin.Update): (1) if the databases weren't up yet at AnimationLoad, retry the apply ~1/s;
        // (2) once applied, catch up any units that spawned before the override landed (re-instantiate them to full count).
        internal static void Tick()
        {
            if (pending && Time.frameCount - lastTryFrame >= 60)
            {
                try { TryApply(); }
                catch (Exception ex) { Plugin.Log.LogError("[Formation] Tick: " + ex); pending = false; }
            }
            if (!pending && appliedAny && Plugin.FormationReinstantiateOn != null && Plugin.FormationReinstantiateOn.Value)
            {
                try { MaybeReinstantiate(); }
                catch (Exception ex) { Plugin.Log.LogError("[Formation] reinstantiate: " + ex); appliedAny = false; }
            }
        }

        static void EnsureConfig()
        {
            if (parsed) return; parsed = true;
            try
            {
                var regPath = Path.Combine(Paths.ConfigPath, "enc_formations.json");
                if (!File.Exists(regPath)) return;
                var root = JObject.Parse(File.ReadAllText(regPath));
                foreach (var l in (root["links"] as JArray) ?? new JArray())
                {
                    var e = new Entry
                    {
                        unit = ((string)l["unit"] ?? "").Trim(),
                        formation = ((string)l["formation"] ?? "").Trim(),
                        lowSpec = ((string)l["lowSpec"] ?? "").Trim(),
                        dummyOffset = (float?)l["dummyOffset"] ?? -1f,
                        scale = (float?)l["scale"] ?? -1f,
                        layoutScale = (float?)l["layoutScale"] ?? -1f,
                        scaleMode = ((string)l["scaleMode"] ?? "transform").Trim().ToLowerInvariant(),
                    };
                    foreach (var sf in (l["sizeFormations"] as JArray) ?? new JArray())
                    {
                        var fn = ((string)sf["formation"] ?? "").Trim();
                        var tv = (float?)sf["threshold"] ?? -1f;
                        if (fn.Length > 0 && tv > 0f) e.sizeForms.Add(new KeyValuePair<float, string>(tv, fn));
                    }
                    e.sizeForms.Sort((a, b) => a.Key.CompareTo(b.Key));
                    foreach (var d in (l["dummies"] as JArray) ?? new JArray())
                    {
                        var dm = new Dummy
                        {
                            pos = new Vector3((float?)d["position"]?["x"] ?? 0f,
                                              (float?)d["position"]?["y"] ?? 0f,
                                              (float?)d["position"]?["z"] ?? 0f)
                        };
                        foreach (var c in (d["coords"] as JArray) ?? new JArray())
                            dm.coords.Add(new Cell { x = (int?)c["x"] ?? 0, y = (int?)c["y"] ?? 0 });
                        e.dummies.Add(dm);
                    }
                    for (int i = 0; i < 6; i++)
                    {
                        var ja = l["columns" + i] as JArray;
                        var v = new int[ja?.Count ?? 0];
                        for (int j = 0; j < v.Length; j++) v[j] = (int?)ja[j] ?? 0;
                        e.columns[i] = v;
                    }

                    if (e.formation.Length == 0)
                    { Plugin.Log.LogWarning("[Formation] registry entry skipped (formation name empty)."); continue; }
                    if (e.unit.Length == 0 && e.dummies.Count == 0)
                    { Plugin.Log.LogWarning($"[Formation] MACRO replacement of '{e.formation}' skipped — no formation data in the entry."); continue; }
                    var err = Validate(e);
                    if (err != null)
                    { Plugin.Log.LogError($"[Formation] entry '{e.unit}' -> '{e.formation}' INVALID ({err}) — skipped. Re-save it from the Formation Override window."); continue; }
                    entries.Add(e);
                }
                if (entries.Count > 0)
                    Plugin.Log.LogInfo($"[Formation] registry: {entries.Count} link(s) from enc_formations.json");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Formation] enc_formations.json parse: " + ex); }
        }

        // The consistency rules BuildDummiesGrid enforces by crashing; we enforce them by skipping. Null = valid.
        // An entry with NO dummy data is a pure repoint (link the unit to a formation already in the database).
        static string Validate(Entry e)
        {
            int n = e.dummies.Count;
            if (n == 0) return null;
            foreach (var d in e.dummies)
                if (d.coords.Count != 6) return $"a dummy has {d.coords.Count} orientation coordinates (need 6)";
            for (int i = 0; i < 6; i++)
            {
                var cols = e.columns[i];
                if (cols == null || cols.Length == 0) return $"ColumnsCountPerRow{i} is empty";
                int total = 0; foreach (var c in cols) total += c;
                if (total != n) return $"ColumnsCountPerRow{i} cells ({total}) != dummy count ({n})";
                var seen = new HashSet<int>();
                foreach (var d in e.dummies)
                {
                    var c = d.coords[i];
                    if (c.x < 0 || c.x >= cols.Length) return $"orientation {i}: row {c.x} out of range (rows={cols.Length})";
                    if (c.y < 0 || c.y >= cols[c.x]) return $"orientation {i}: column {c.y} out of range (row {c.x} has {cols[c.x]})";
                    if (!seen.Add(c.x * 4096 + c.y)) return $"orientation {i}: duplicate cell ({c.x},{c.y})";
                }
            }
            return null;
        }

        // PresentationFormationDefinition lives in Amplitude.Mercury.Data (NOT .Data.World like the unit/pawn defs —
        // the .World guess cost the first in-game test: the DB lookup returned null forever, silently). Fallbacks
        // cover a future namespace move; the simple name works because HarmonyX's TypeByName matches t.Name last.
        static Type FormationDefType() =>
            AccessTools.TypeByName("Amplitude.Mercury.Data.PresentationFormationDefinition")
            ?? AccessTools.TypeByName("Amplitude.Mercury.Data.World.PresentationFormationDefinition")
            ?? AccessTools.TypeByName("PresentationFormationDefinition");

        static void TryApply()
        {
            lastTryFrame = Time.frameCount;
            var fdType = FormationDefType();
            var fdb = fdType != null ? Prober.ResolveDatabase(fdType) : null;
            var udb = Prober.ResolveDatabase("Amplitude.Mercury.Data.World.PresentationUnitDefinition");
            if (fdb == null || udb == null)
            {
                if (!dbWaitLogged)
                {
                    dbWaitLogged = true;
                    Plugin.Log.LogInfo($"[Formation] waiting for databases (formationType={(fdType != null ? "ok" : "MISSING")}, " +
                                       $"formationDb={(fdb != null ? "ok" : "null")}, unitDb={(udb != null ? "ok" : "null")}) — retrying in the background.");
                }
                return;   // stay pending; Tick() retries
            }

            bool allDone = true;
            foreach (var e in entries)
            {
                if (e.done) continue;
                try { ApplyOne(e, fdb, udb); }
                catch (Exception ex) { Plugin.Log.LogError($"[Formation] '{e.unit}' -> '{e.formation}': {ex}"); e.done = true; }
                if (!e.done) allDone = false;
            }
            if (allDone) { pending = false; if (entries.Count > 0) appliedAny = true; }
        }

        // Reflection reads go through the shared UniversalInject.GetMember (cached per (type,name), property-first, finds
        // non-public too). FormationOverride used to keep its own copy; consolidated onto the one implementation so the
        // caching/lookup strategy can't drift between the two. Main-thread only (Tick / AnimationLoad postfix).
        static object Mem(object o, string name) => UniversalInject.GetMember(o, name);

        // Walk the live armies (same path as UniversalInject's post-load respawn) and, for every unit whose
        // PresentationUnitDefinition matches a repointed entry, re-run the game's own UpdatePawns ONCE so it rebuilds its
        // pawn grid at the new dummy count (the plugin's Formation3D-prefab growth + formation overwrite are live by now).
        // Throttled; idempotent per unit (tracked in `reformed`); skips units already at/above the entry's count (they
        // spawned after the override — re-forming them would be a pointless visible pop).
        static void MaybeReinstantiate()
        {
            if (reformSettled) return;                              // catch-up complete this session — a reload re-arms it (OnAnimationLoad)
            if (++reformScanFrame % 5 != 0) return;                 // ~12x/s is ample; the frame counter still advances
            var presType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.Presentation");
            var factory = presType == null ? null : AccessTools.Field(presType, "PresentationEntityFactoryController")?.GetValue(null);
            var armies = factory == null ? null : Mem(factory, "PresentationArmyEntities") as Array;
            if (armies == null) return;

            reformPresent.Clear();                                  // reused across scans (no per-scan HashSet allocation)
            bool handledAny = false;
            foreach (var army in armies)
            {
                if (army == null) continue;
                var unit = Mem(army, "PresentationUnit");
                if (unit == null) continue;
                var pdef = Mem(unit, "PresentationUnitDefinition");
                string pdn = Mem(pdef, "name")?.ToString() ?? Mem(pdef, "Name")?.ToString() ?? "";
                if (pdn.Length == 0) continue;
                // macro replacements have no unit name — match those by the definition's own formation reference
                string fref = null;
                try
                {
                    var r = AccessTools.Field(pdef.GetType(), "PresentationFormationDefinition")?.GetValue(pdef);
                    fref = r?.GetType().GetProperty("XmlSerializableElementName")?.GetValue(r) as string;
                }
                catch { }
                Entry e = null;                                     // plain loop, not entries.FirstOrDefault — no per-army closure allocation at 12x/s
                foreach (var x in entries)
                    if (x.done && x.dummies.Count > 0
                        && (string.Equals(x.unit, pdn, StringComparison.OrdinalIgnoreCase)
                            || (x.unit.Length == 0 && string.Equals(x.formation, fref ?? "", StringComparison.OrdinalIgnoreCase))))
                    { e = x; break; }
                if (e == null) continue;                             // not one of our repointed/replaced units
                reformPresent.Add(unit);
                if (reformed.Contains(unit)) continue;               // already handled this session
                bool loaded = true; try { loaded = Convert.ToBoolean(Mem(unit, "IsLoaded")); } catch { }
                if (!loaded) continue;                                // nothing rendered yet — wait for the next scan
                // Per-matching-unit log (fires as each Warriors_Default appears, incl. manually-spawned ones after load):
                // formation name + the two counts, so we see whether the repoint took and whether a re-form is needed.
                var fo = Mem(unit, "Formation");
                string fn = Mem(fo, "name")?.ToString() ?? Mem(Mem(fo, "PresentationFormationDefinition"), "name")?.ToString() ?? "?";
                object dc = Mem(fo, "DummyCount");
                int pawns = (Mem(unit, "Pawns") as ICollection)?.Count ?? -1;
                reformed.Add(unit); handledAny = true;               // handle/log each unit once; mark BEFORE any call so a throw isn't retried forever
                if (pawns >= e.dummies.Count)                        // already full — spawned after the override won the race
                { Plugin.Log.LogInfo($"[Formation] '{pdn}' already {pawns}/{e.dummies.Count} (formation='{fn}' dummyCount={dc}) — no re-form needed"); continue; }
                bool naval = false; try { naval = Convert.ToBoolean(Mem(unit, "IsNaval")); } catch { }
                AccessTools.Method(unit.GetType(), "UpdatePawns", new[] { typeof(bool) })?.Invoke(unit, new object[] { naval });
                int after = (Mem(unit, "Pawns") as ICollection)?.Count ?? -1;
                object dc2 = Mem(Mem(unit, "Formation"), "DummyCount");
                Plugin.Log.LogInfo($"[Formation] re-instantiated '{pdn}': pawns {pawns} -> {after} (formation='{fn}', dummyCount {dc} -> {dc2}, target {e.dummies.Count}) — spawned before the override.");
            }
            reformed.RemoveWhere(u => !reformPresent.Contains(u));   // drop gone units so a genuinely new instance is handled again
            // TERMINATION: the catch-up only ever targets units that spawned BEFORE the override (all present within a few
            // seconds of load). Once a run of scans handles nothing new, STOP the ~12x/s walk for the rest of the session —
            // units built LATER already spawn at the overridden count (no re-form needed; they'd only get a diagnostic log).
            // A save-load re-arms via OnAnimationLoad. This removes the permanent per-frame cost the scan used to pay forever.
            if (handledAny) reformQuietScans = 0;
            else if (++reformQuietScans >= ReformQuietLimit) reformSettled = true;
        }

        static void ApplyOne(Entry e, IEnumerable fdb, IEnumerable udb)
        {
            // 1) make sure the formation exists in the live database
            var existing = DbFind(fdb, e.formation);
            if (existing == null)
            {
                if (e.dummies.Count == 0)
                {
                    Plugin.Log.LogError($"[Formation] '{e.formation}' not in the database and the registry entry carries no dummy data — " +
                                        $"link for '{e.unit}' skipped. Re-save the entry from a formation ASSET in the Formation Override window.");
                    e.done = true; return;
                }
                var fdType = FormationDefType();
                if (fdType == null) { Plugin.Log.LogError("[Formation] PresentationFormationDefinition type not found (game update?)."); e.done = true; return; }
                var so = BuildFormation(e, fdType);
                var add = AccessTools.Method(fdb.GetType(), "Add", new[] { fdType });
                if (add == null) { Plugin.Log.LogError("[Formation] Database.Add not found (game update?)."); e.done = true; return; }
                add.Invoke(fdb, new object[] { so });
                Plugin.Log.LogInfo($"[Formation] injected '{e.formation}' ({e.dummies.Count} dummies) into the live formation database.");
            }
            else if (e.dummies.Count > 0 && !(created.TryGetValue(e.formation, out var mine) && ReferenceEquals(existing, mine)))
            {
                // Name already in the DB. Field-proven cause (first in-game test): the MOD BUNDLE ships Assets/Databases
                // — including the user's UnitFormation folder — and bundled databases DO load into the runtime datatables;
                // a bundle built before the asset's last edit plants a STALE copy under the same name (a 9-dummy duplicate
                // wearing the _12 name = "9 pawns in a scatter layout"). The registry is the source of truth: overwrite
                // the existing element's data IN PLACE — every reference (even an already-cached one) points at the
                // patched object. Deliberate consequence: reusing a VANILLA formation name rewrites that formation for
                // every unit referencing it (usable as a macro override; the log is loud so it's never a surprise).
                var fdT = existing.GetType();
                FillFormationFields(existing, fdT, e);
                Plugin.Log.LogWarning($"[Formation] '{e.formation}' already existed in the database — its data was OVERWRITTEN in place " +
                                      $"from the registry ({e.dummies.Count} dummies). If that name is a vanilla formation, every unit using it is affected.");
            }

            // MACRO REPLACEMENT entry (no unit): the in-place overwrite above IS the whole job — every unit of
            // every mod whose definition references this name (resolved lazily by name at spawn) now gets this
            // layout. Per-unit link entries still repoint their units elsewhere and thus overrule it.
            if (e.unit.Length == 0)
            {
                Plugin.Log.LogInfo($"[Formation] MACRO replacement live: every unit referencing '{e.formation}' now fields {e.dummies.Count} pawns at full health.");
                e.done = true; return;
            }

            // 2) repoint the unit's formation reference (FRESH struct: the old one may cache the old element)
            var unitDef = DbFind(udb, e.unit);
            if (unitDef == null)
            {
                Plugin.Log.LogError($"[Formation] unit '{e.unit}' not found in PresentationUnitDefinition database — link skipped. " +
                                    "(The name must match the definition asset name, e.g. Era5_Common_Riflemen.)");
                e.done = true; return;
            }
            SetFreshElementReference(unitDef, "PresentationFormationDefinition", e.formation);
            Plugin.Log.LogInfo($"[Formation] '{e.unit}' now uses formation '{e.formation}'" +
                               (e.dummies.Count > 0 ? $" ({e.dummies.Count} pawns at full health)." : "."));

            // Optional: tighten the packing by overriding the unit's random per-model jitter. DummyOffsetPosition lives in
            // the CoordinationValues STRUCT on the unit def — box it, set the field, write the box back.
            if (e.dummyOffset >= 0f)
            {
                var cvField = AccessTools.Field(unitDef.GetType(), "CoordinationValues");
                object cv = cvField?.GetValue(unitDef);
                var offField = cv != null ? AccessTools.Field(cv.GetType(), "DummyOffsetPosition") : null;
                if (offField != null)
                {
                    offField.SetValue(cv, new Vector2(e.dummyOffset, e.dummyOffset));
                    cvField.SetValue(unitDef, cv);   // struct: write the mutated box back onto the def
                    Plugin.Log.LogInfo($"[Formation] '{e.unit}' dummy jitter -> {e.dummyOffset} (tighter packing).");
                }
            }
            e.done = true;
        }

        static ScriptableObject BuildFormation(Entry e, Type fdType)
        {
            if (created.TryGetValue(e.formation, out var have)) return have;
            var so = ScriptableObject.CreateInstance(fdType);
            so.name = e.formation;
            so.hideFlags = HideFlags.HideAndDontSave;   // keep Unity's unused-asset sweep off a no-asset-backed SO
            FillFormationFields(so, fdType, e);
            created[e.formation] = so;
            return so;
        }

        // Stamp the registry's formation data onto a PresentationFormationDefinition instance (fresh OR an existing
        // database element being overwritten in place).
        static void FillFormationFields(object so, Type fdType, Entry e)
        {
            var dummyType = fdType.GetNestedType("DummyData");
            var arr = Array.CreateInstance(dummyType, e.dummies.Count);
            var fPos = AccessTools.Field(dummyType, "Position");
            var fCpd = AccessTools.Field(dummyType, "CoordinatePerDirection");
            // footprint: explicit layoutScale wins; otherwise the model `scale` shrinks the spacing WITH the models
            // (scaling a formation means the whole formation); 1/-1 = positions as authored
            float posMul = e.layoutScale > 0f ? e.layoutScale : (e.scale > 0f ? e.scale : 1f);
            for (int i = 0; i < e.dummies.Count; i++)
            {
                object d = Activator.CreateInstance(dummyType);   // boxed struct
                fPos.SetValue(d, e.dummies[i].pos * posMul);
                var v = new Vector2Int[e.dummies[i].coords.Count];
                for (int j = 0; j < v.Length; j++) v[j] = new Vector2Int(e.dummies[i].coords[j].x, e.dummies[i].coords[j].y);
                fCpd.SetValue(d, v);
                arr.SetValue(d, i);
            }
            AccessTools.Field(fdType, "Dummies").SetValue(so, arr);
            for (int i = 0; i < 6; i++)
                AccessTools.Field(fdType, "ColumnsCountPerRow" + i).SetValue(so, e.columns[i] ?? new int[0]);

            // low-spec graphics fall back to this reference — Formation_1 (vanilla 1-dummy) unless the asset said otherwise
            SetFreshElementReference(so, "LowSpecFormationDefinition", string.IsNullOrEmpty(e.lowSpec) ? "Formation_1" : e.lowSpec);
            AccessTools.Method(fdType, "Initialize")?.Invoke(so, null);   // Name StaticString + ref string init
        }

        // Install a brand-new DatatableElementReference naming `element` — never mutate the existing struct: its
        // private cache (element + databaseRevision) would keep resolving to the OLD target until the next Commit.
        // Formation-by-size (Global Era Lab runtime, size table authored PER UNIT in the Formation Override
        // window): the resize engine asks for this unit's thresholds; null = no per-unit table (the engine may
        // then fall back to the legacy global table from enc_models.json).
        internal static List<KeyValuePair<float, string>> SizeThresholdsFor(string unitDefName)
        {
            if (string.IsNullOrEmpty(unitDefName)) return null;
            EnsureConfig();
            foreach (var e in entries)
                if (e.sizeForms.Count > 0 && string.Equals(e.unit, unitDefName, StringComparison.OrdinalIgnoreCase))
                    return e.sizeForms;
            return null;
        }

        internal static void SetFreshElementReference(object owner, string fieldName, string element)
        {
            var f = AccessTools.Field(owner.GetType(), fieldName);
            object boxed = Activator.CreateInstance(f.FieldType);
            f.FieldType.GetProperty("XmlSerializableElementName").SetValue(boxed, element);
            f.SetValue(owner, boxed);
        }

        // TEMP diagnostic (Hk_FormationSpawnDiag): the spawn math, read back right after InstantiatePawns ran.
        internal static void SpawnDiag(object presentationUnit)
        {
            try
            {
                if (Plugin.FormationOverrideOn == null || !Plugin.FormationOverrideOn.Value) return;
                var formation = AccessTools.Field(presentationUnit.GetType(), "Formation")?.GetValue(presentationUnit);
                if (formation == null) return;
                int dummyCount = (int)(AccessTools.Field(formation.GetType(), "DummyCount")?.GetValue(formation) ?? 0);
                if (dummyCount <= 9) return;   // vanilla-sized: not ours, keep quiet
                var pawns = AccessTools.Field(presentationUnit.GetType(), "Pawns")?.GetValue(presentationUnit) as ICollection;
                object holder = AccessTools.Field(presentationUnit.GetType(), "PresentationEntityHolder")?.GetValue(presentationUnit)
                                ?? AccessTools.Property(presentationUnit.GetType(), "PresentationEntityHolder")?.GetValue(presentationUnit);
                object health = holder != null ? AccessTools.Method(holder.GetType(), "GetHealthRatio")?.Invoke(holder, null) : null;
                Plugin.Log.LogInfo($"[Formation] spawn: dummies={dummyCount} pawns={pawns?.Count ?? -1} healthRatio={health ?? "?"} — pawns should be ceil(dummies × health).");
                // Per-pawn placement dump: 12 spawned but only 9 visible = either 3 draw invisibly or 3 stand
                // somewhere unexpected — this shows each pawn's dummy slot, active state and world position.
                int k = 0;
                foreach (var pw in (pawns as IEnumerable) ?? new object[0])
                {
                    var dummy = AccessTools.Field(pw.GetType(), "Dummy")?.GetValue(pw)
                                ?? AccessTools.Property(pw.GetType(), "Dummy")?.GetValue(pw);
                    var dtr = dummy != null ? AccessTools.Field(dummy.GetType(), "Transform")?.GetValue(dummy) as Transform : null;
                    var go = (pw as Component)?.gameObject;
                    Plugin.Log.LogInfo($"[Formation]   pawn{k++}: dummyLocal={(dtr != null ? dtr.localPosition.ToString("F2") : "?")} " +
                                       $"active={(go != null ? go.activeInHierarchy.ToString() : "?")} world={(go != null ? go.transform.position.ToString("F1") : "?")}");
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Formation] spawn diag: " + ex); }
        }

        // ---------- MODEL SCALE v2 = SCALE-IN-THE-DATA (skeleton-clone architecture, 2026-07-28) ----------
        // Root-transform scaling is ENGINE-HOSTILE for pawns: the GPU skinning, the rigid-fragment path and the
        // procedural weapon slots each pick a root scale up DIFFERENTLY (field campaign: gear double-scaled and
        // slid off its anchors at 0.8; at 1.25 helmets floated 0.4m high and the BODY itself shriveled). v2 puts
        // the scale into the DATA and leaves every transform at 1, so no subsystem ever sees a scale at all:
        //   • clone the definition's Skeleton; multiply every bone's BindPose/Local TRANSLATION by s. Rotations
        //     untouched — clips are rotation-only (Law 5), so vanilla animations replay correctly on a scaled
        //     bind BY CONSTRUCTION.
        //   • scale every hosted mesh's pre-encoded vertices by s (positions = first 3 floats of each record;
        //     verticesBytesCrc ZEROED — the corruption guard rejects modified bytes, 0 skips it by design; FRESH
        //     guid per mesh — the encoder caches slots per guid, the original would shadow our data).
        //   • same treatment for each rigid EQ fragment collection (helmet/shield/weapon meshes are bone-local;
        //     scaled bones sit at scaled positions, so the gear scales in place — no anchor math needed).
        //   • swap the addon onto the clones exactly like the custom-model repoint does (Skeleton/MeshCollection
        //     members + FragmentEntry rebuild + surgical descriptor repoint preserving SkinnedMeshIndex).
        // Runs in the AddOn.Load postfix window (UniRepointHook), where the model axis proved the swap is safe.

        static readonly Dictionary<string, UnityEngine.Object> scaledCollections = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);   // "(instId)|s" -> collection/skeleton clone (process-lived)
        static readonly HashSet<string> fragDefsDone = new HashSet<string>(StringComparer.Ordinal);            // defNames processed (cleared per session)
        static readonly HashSet<string> clonesRegisteredThisSession = new HashSet<string>(StringComparer.Ordinal);

        internal static void MaybeScaleFragments(object addon, object animMgr)   // entry-point name kept (wired in UniRepointHook)
        {
            try
            {
                if (Plugin.FormationOverrideOn == null || !Plugin.FormationOverrideOn.Value || entries.Count == 0 || addon == null || animMgr == null) return;
                var def = UniversalInject.GetMember(addon, "Definition");
                var defName = (def as UnityEngine.Object)?.name;
                if (string.IsNullOrEmpty(defName)) return;
                var unitRef = AccessTools.Field(def.GetType(), "PresentationUnitDefinition")?.GetValue(def);
                var unitName = unitRef?.GetType().GetProperty("XmlSerializableElementName")?.GetValue(unitRef) as string;
                if (string.IsNullOrEmpty(unitName)) return;
                Entry link = null;
                foreach (var e in entries) if (e.unit == unitName && e.scale > 0f && Math.Abs(e.scale - 1f) > 0.001f && e.scaleMode == "data") { link = e; break; }
                if (link == null) return;   // no scale, or Transform mode (handled per-pawn in ApplyPawnScale)
                // NO once-per-def gate: the addon's Load runs MORE THAN ONCE per session and each vanilla
                // ReloadFragments rebuilds FragmentEntries from the definition — clobbering our scaled entries
                // (field-proven: gear reverted to vanilla size while the body kept the scaled skeleton). Re-apply
                // on every Load; slots already pointing at our clones are skipped, so steady state is cheap.
                bool firstRun = fragDefsDone.Add(defName);
                float s = link.scale;

                var sk0 = UniversalInject.GetMember(addon, "Skeleton") as UnityEngine.Object;
                var mc0 = UniversalInject.GetMember(addon, "MeshCollection") as UnityEngine.Object;
                if (sk0 == null || !sk0) { Plugin.Log.LogWarning($"[Formation] '{defName}': no skeleton on the addon — cannot scale."); return; }
                var renderer = UniversalInject.GetMember(animMgr, "FxComponentRenderer");
                var mcm = UniversalInject.GetMember(animMgr, "FxComponentMeshContentManager");
                var layerObj = UniversalInject.GetMember(animMgr, "FXMeshLayerIndex");
                int layerIdx = layerObj is int li ? li : Convert.ToInt32(layerObj ?? 0);
                if (renderer == null || mcm == null) { Plugin.Log.LogWarning($"[Formation] '{defName}': animation managers not ready — not scaled this session."); return; }
                var regM = animMgr.GetType().GetMethod("RegisterMeshCollection", BindingFlags.Public | BindingFlags.Instance);

                // 1) the scaled skeleton (bind translations + hosted body meshes ×s) and, if distinct, the mesh collection.
                // Later Loads see OUR clone already on the addon — never re-scale a clone (double-shrink).
                UnityEngine.Object sk1;
                if (sk0.name.Contains("_HAFs")) sk1 = sk0;
                else { sk1 = GetScaledSkeleton(sk0, s, defName); if (sk1 == null) return; }
                UnityEngine.Object mc1 = (mc0 == null || !mc0 || ReferenceEquals(mc0, sk0) || ReferenceEquals(mc0, sk1)) ? sk1
                    : mc0.name.Contains("_HAFs") ? mc0
                    : GetScaledCollection(mc0, s, defName);

                // 2) register the clones (the Skeleton branch assigns a SkeletonId and uploads the scaled bind slabs)
                void RegisterOnce(UnityEngine.Object c)
                {
                    if (c == null) return;
                    var rk = c.GetInstanceID().ToString();
                    if (!clonesRegisteredThisSession.Add(rk)) return;
                    try { regM?.Invoke(animMgr, new object[] { c }); }
                    catch (Exception rex) { Plugin.Log.LogWarning("[Formation] scaled-clone register: " + (rex.InnerException ?? rex).Message); }
                    try { AccessTools.Method(c.GetType(), "LoadIFN")?.Invoke(c, new object[] { mcm, layerIdx, -1 }); }
                    catch (Exception lex) { Plugin.Log.LogWarning("[Formation] scaled-clone LoadIFN: " + (lex.InnerException ?? lex).Message); }
                }
                RegisterOnce(sk1);
                if (!ReferenceEquals(mc1, sk1)) RegisterOnce(mc1);

                // 3) swap the addon (the custom-model repoint idiom)
                void SetM(object o, string nm, object val)
                {
                    var p = AccessTools.Property(o.GetType(), nm);
                    if (p != null && p.CanWrite) { try { p.SetValue(o, val); return; } catch { } }
                    AccessTools.Field(o.GetType(), nm)?.SetValue(o, val);
                }
                SetM(addon, "Skeleton", sk1);
                SetM(addon, "MeshCollection", mc1);

                // 4) rebuild every fragment entry against the scaled assets
                var fragsArr = UniversalInject.GetMember(addon, "FragmentEntries") as Array;
                if (fragsArr == null || fragsArr.Length == 0)
                { Plugin.Log.LogInfo($"[Formation] '{defName}': skeleton scaled x{s} (no fragments)."); return; }
                var fragType = fragsArr.GetType().GetElementType();
                var fMc = AccessTools.Field(fragType, "meshCollection");
                var fMn = AccessTools.Field(fragType, "meshName");
                var fBn = AccessTools.Field(fragType, "boneName");
                var fSlot = AccessTools.Field(fragType, "SlotIndex");
                var fEnc = AccessTools.Field(fragType, "EncodedMeshAndVisualParticleCount");
                var pFol = AccessTools.Property(fragType, "FxOutputLayer");
                var ctor5 = fragType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(c => c.GetParameters().Length == 5);
                var loadM = AccessTools.Method(fragType, "Load");
                if (fMc == null || fMn == null || ctor5 == null || loadM == null)
                { Plugin.Log.LogWarning("[Formation] FragmentEntry layout changed (game update?) — fragments not rebuilt."); return; }

                int replaced = 0;
                var newEnc = new uint[fragsArr.Length];
                for (int i = 0; i < fragsArr.Length; i++)
                {
                    var it = fragsArr.GetValue(i);
                    if ((uint)fEnc.GetValue(it) == 0) continue;                       // dead slot: leave as-is
                    var mn = fMn.GetValue(it) as string;
                    if (string.IsNullOrEmpty(mn)) continue;
                    var collOrig = fMc.GetValue(it) as UnityEngine.Object;
                    if (collOrig != null && collOrig && collOrig.name.Contains("_HAFs"))
                    { newEnc[i] = (uint)fEnc.GetValue(it); continue; }                // already ours (a later Load kept it) — keep its enc for the descriptor pass
                    UnityEngine.Object coll1 = null;
                    if (collOrig != null && collOrig)
                    {
                        coll1 = ReferenceEquals(collOrig, sk0) ? sk1
                              : (mc0 != null && ReferenceEquals(collOrig, mc0)) ? mc1
                              : GetScaledCollection(collOrig, s, defName);
                        if (coll1 == null) { Plugin.Log.LogWarning($"[Formation] '{defName}' fragment '{mn}': collection not scalable — left vanilla."); continue; }
                        RegisterOnce(coll1);
                    }
                    var item = ctor5.Invoke(new object[] { fSlot.GetValue(it), coll1, mn, pFol?.GetValue(it), fBn.GetValue(it) });
                    try { loadM.Invoke(item, new object[] { sk1, renderer, mcm, layerIdx }); }
                    catch (Exception lex) { Plugin.Log.LogWarning($"[Formation] '{defName}' fragment '{mn}' Load: " + (lex.InnerException ?? lex).Message); continue; }
                    uint enc = (uint)fEnc.GetValue(item);
                    if (enc == 0)
                    {
                        uint cidx = 0;
                        try { if (coll1 != null) cidx = (uint)AccessTools.Method(coll1.GetType(), "GetFxMeshIndex", new[] { typeof(string) }).Invoke(coll1, new object[] { mn }); } catch { }
                        Plugin.Log.LogWarning($"[Formation] '{defName}' fragment '{mn}': scaled encode returned 0 (cloneMeshIndex={cidx}) — kept vanilla.");
                        continue;
                    }
                    fragsArr.SetValue(item, i);
                    newEnc[i] = enc;
                    replaced++;
                }

                // 5) descriptor: pre-registration the snapshot picks the swapped assets up; post-registration patch surgically
                int defId = -1;
                try { defId = Convert.ToInt32(UniversalInject.GetMember(addon, "PawnDefinitionId")); } catch { }
                if (defId < 0)
                {
                    if (firstRun || replaced > 0)
                        Plugin.Log.LogInfo($"[Formation] '{defName}': SCALED x{s} in data (skeleton + {replaced} fragment(s)) pre-registration — the snapshot carries it.");
                    return;
                }
                var pmType = AccessTools.TypeByName("Amplitude.Mercury.Animation.PawnManager");
                var pm = pmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                         ?? AccessTools.Field(pmType, "Instance")?.GetValue(null);
                var descs = AccessTools.Field(pmType, "gpuPawnDescriptorEntries")?.GetValue(pm) as Array;
                var gfrags = AccessTools.Field(pmType, "gpuPawnDescriptorFragmentEntries")?.GetValue(pm) as Array;
                var cntF = AccessTools.Field(pmType, "persistentFragmentEntryCount");
                var dirtyF = AccessTools.Field(pmType, "descriptorBufferDirty");
                if (pm == null || descs == null || gfrags == null || cntF == null || defId >= descs.Length)
                { Plugin.Log.LogWarning($"[Formation] '{defName}': descriptor arrays unreadable — scaled fragments may stay invisible."); return; }
                var dEntry = descs.GetValue(defId);
                var dT = dEntry.GetType();
                uint start = (uint)dT.GetField("StartFragment").GetValue(dEntry);
                uint count = (uint)dT.GetField("FragmentCount").GetValue(dEntry);
                if (count == 0)
                {
                    // descriptor slot allotted but not yet filled (the game populates it on a LATER pass, from the
                    // addon's FragmentEntries — which now hold OUR scaled entries; the next Load postfix also re-runs us)
                    if (firstRun) Plugin.Log.LogInfo($"[Formation] '{defName}': descriptor not yet populated — scaled entries are in place for the game's own fill; re-checked on the next Load.");
                    return;
                }
                if (count != fragsArr.Length)
                { Plugin.Log.LogWarning($"[Formation] '{defName}': descriptor count {count} != FragmentEntries {fragsArr.Length} — patch skipped (order unknown)."); return; }
                var feT = gfrags.GetType().GetElementType();
                var encF2 = feT.GetField("EncodedMeshAndVisualParticleCountFxMeshIndex");
                // idempotence: if the live block already carries our encodes, don't grow the tail again this Load
                bool differs = false;
                for (int k = 0; k < count && !differs; k++)
                    if (newEnc[k] != 0 && (uint)encF2.GetValue(gfrags.GetValue((int)start + k)) != newEnc[k]) differs = true;
                if (!differs) return;
                int tail = Convert.ToInt32(cntF.GetValue(pm));
                int need = tail + (int)count;
                if (gfrags.Length < need)
                {
                    var grown = Array.CreateInstance(gfrags.GetType().GetElementType(), need + 100);
                    Array.Copy(gfrags, grown, gfrags.Length);
                    AccessTools.Field(pmType, "gpuPawnDescriptorFragmentEntries").SetValue(pm, grown); gfrags = grown;
                }
                for (int k = 0; k < count; k++)
                {
                    var rec = gfrags.GetValue((int)start + k);   // verbatim copy preserves SkinnedMeshIndex/bone/layer
                    if (newEnc[k] != 0) encF2.SetValue(rec, newEnc[k]);
                    gfrags.SetValue(rec, tail + k);
                }
                dT.GetField("StartFragment").SetValue(dEntry, (uint)tail);
                descs.SetValue(dEntry, defId);
                cntF.SetValue(pm, tail + (int)count);
                dirtyF?.SetValue(pm, true);
                if (firstRun || replaced > 0)
                    Plugin.Log.LogInfo($"[Formation] '{defName}': SCALED x{s} in data (skeleton '{sk1.name}' + {replaced}/{count} fragment(s) this pass); descriptor[{defId}] repointed {start}+{count} -> {tail}+{count}.");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Formation] MaybeScaleFragments: " + ex); }
        }

        // Scaled Skeleton clone: bone bind/local translations ×s (rotations untouched), bbox ×s, hosted meshes ×s.
        static UnityEngine.Object GetScaledSkeleton(UnityEngine.Object sk0, float s, string tag)
        {
            try
            {
                string key = sk0.GetInstanceID() + "|" + s.ToString("0.###");
                if (scaledCollections.TryGetValue(key, out var have) && have != null) return have;
                var sk1 = UnityEngine.Object.Instantiate(sk0);
                sk1.name = sk0.name + "_HAFs" + s.ToString("0.###");
                sk1.hideFlags = HideFlags.HideAndDontSave;
                var bones = AccessTools.Field(sk1.GetType(), "BoneInfos")?.GetValue(sk1) as Array;
                if (bones == null || bones.Length == 0)
                { Plugin.Log.LogWarning($"[Formation] '{tag}': skeleton clone has no BoneInfos — not scaled."); return null; }
                for (int j = 0; j < bones.Length; j++)
                {
                    var bi = bones.GetValue(j);   // boxed struct
                    ScaleTrsTranslation(bi, "BindPose", s);
                    ScaleTrsTranslation(bi, "Local", s);
                    bones.SetValue(bi, j);
                }
                var bminF = AccessTools.Field(sk1.GetType(), "BBoxMin");
                var bmaxF = AccessTools.Field(sk1.GetType(), "BBoxMax");
                if (bminF?.GetValue(sk1) is Vector3 bmin) bminF.SetValue(sk1, bmin * s);
                if (bmaxF?.GetValue(sk1) is Vector3 bmax) bmaxF.SetValue(sk1, bmax * s);
                int scaledMeshes = ScaleAllContents(sk1, s, tag);
                Plugin.Log.LogInfo($"[Formation] '{tag}': skeleton '{sk1.name}' — {bones.Length} bone binds ×{s}, {scaledMeshes} hosted mesh(es) scaled.");
                scaledCollections[key] = sk1;
                return sk1;
            }
            catch (Exception ex) { Plugin.Log.LogError("[Formation] GetScaledSkeleton: " + ex); return null; }
        }

        static void ScaleTrsTranslation(object boneInfoBoxed, string trsField, float s)
        {
            var f = AccessTools.Field(boneInfoBoxed.GetType(), trsField);
            if (f == null) return;
            var trs = f.GetValue(boneInfoBoxed);   // boxed TRS
            var tF = AccessTools.Field(trs.GetType(), "Translation");
            if (tF?.GetValue(trs) is Vector3 t) { tF.SetValue(trs, t * s); f.SetValue(boneInfoBoxed, trs); }
        }

        // Scaled plain MeshCollection clone (EQ fragment collections): every hosted mesh ×s.
        static UnityEngine.Object GetScaledCollection(UnityEngine.Object mc0, float s, string tag)
        {
            try
            {
                string key = mc0.GetInstanceID() + "|" + s.ToString("0.###");
                if (scaledCollections.TryGetValue(key, out var have) && have != null) return have;
                var clone = UnityEngine.Object.Instantiate(mc0);
                clone.name = mc0.name + "_HAFs" + s.ToString("0.###");
                clone.hideFlags = HideFlags.HideAndDontSave;
                int n = ScaleAllContents(clone, s, tag);
                if (n == 0) { Plugin.Log.LogWarning($"[Formation] '{tag}': '{clone.name}' had no scalable mesh contents."); return null; }
                scaledCollections[key] = clone;
                return clone;
            }
            catch (Exception ex) { Plugin.Log.LogError("[Formation] GetScaledCollection: " + ex); return null; }
        }

        // Scale every hosted mesh's pre-encoded vertex POSITIONS ×s in a collection clone. Positions are the first
        // 3 floats of each vertex record (stride = bytes/(vertexCount·4) floats; normals/tangents are directions —
        // invariant under uniform scale; bone weights untouched). CRC zeroed (guard bypass), fresh guid per mesh.
        static int ScaleAllContents(UnityEngine.Object coll, float s, string tag)
        {
            var sis = UniversalInject.GetMember(coll, "skinnedMeshInfos") as Array;
            if (sis == null) return 0;
            int done = 0;
            for (int j = 0; j < sis.Length; j++)
            {
                var si = sis.GetValue(j);
                var fmcF = AccessTools.Field(si.GetType(), "FxMeshContent");
                var fmc = fmcF?.GetValue(si);   // boxed struct copy
                if (fmc == null) continue;
                var fmcT = fmc.GetType();
                var vbF = AccessTools.Field(fmcT, "verticesBytes");
                var vcF = AccessTools.Field(fmcT, "vertexCount");
                var bytes = vbF?.GetValue(fmc) as byte[];
                int vc = 0; try { vc = Convert.ToInt32(vcF?.GetValue(fmc) ?? 0); } catch { }
                if (bytes == null || bytes.Length == 0 || vc <= 0 || bytes.Length % (vc * 4) != 0) continue;
                int strideBytes = bytes.Length / vc;
                var scaled = new byte[bytes.Length];
                Buffer.BlockCopy(bytes, 0, scaled, 0, bytes.Length);
                for (int v = 0; v < vc; v++)
                {
                    int b0 = v * strideBytes;
                    for (int c = 0; c < 3; c++)
                    {
                        float f = BitConverter.ToSingle(scaled, b0 + c * 4) * s;
                        var fb = BitConverter.GetBytes(f);
                        scaled[b0 + c * 4] = fb[0]; scaled[b0 + c * 4 + 1] = fb[1];
                        scaled[b0 + c * 4 + 2] = fb[2]; scaled[b0 + c * 4 + 3] = fb[3];
                    }
                }
                vbF.SetValue(fmc, scaled);
                AccessTools.Field(fmcT, "verticesBytesCrc")?.SetValue(fmc, 0u);   // guard bypass: 0 skips validation by design
                var bminF = AccessTools.Field(fmcT, "bboxMin");
                var bmaxF = AccessTools.Field(fmcT, "bboxMax");
                if (bminF?.GetValue(fmc) is Vector3 bmin) bminF.SetValue(fmc, bmin * s);
                if (bmaxF?.GetValue(fmc) is Vector3 bmax) bmaxF.SetValue(fmc, bmax * s);
                var guidObj = UniversalInject.GetMember(fmc, "Guid");
                var fresh = UniversalInject.MakeGuid(
                    (guidObj?.GetHashCode() ?? j) ^ 0x48414653,
                    ((UniversalInject.GetMember(si, "MeshName")?.ToString() ?? j.ToString()).GetHashCode()),
                    (int)(s * 10000f),
                    unchecked((int)0x0F0F0F0F));
                AccessTools.Field(fmcT, "Guid")?.SetValue(fmc, fresh);
                fmcF.SetValue(si, fmc);
                if (si.GetType().IsValueType) sis.SetValue(si, j);
                done++;
            }
            return done;
        }

        // TRANSFORM scale mode (v1, user-elected per link): pawn root localScale at InstantiatePawn. Simple and
        // decent on bodies + spacing; KNOWN LIMITS on humans (rigid gear double-scales/mis-anchors, limbs distort
        // when scaling UP) — the window says so. "data" mode routes through MaybeScaleFragments instead.
        static readonly HashSet<string> scaleLogged = new HashSet<string>(StringComparer.Ordinal);
        internal static void ApplyPawnScale(object pawn, object unit)
        {
            try
            {
                if (Plugin.FormationOverrideOn == null || !Plugin.FormationOverrideOn.Value || entries.Count == 0) return;
                var pc = pawn as Component;
                if (pc == null || unit == null) return;
                var unitDef = AccessTools.Field(unit.GetType(), "PresentationUnitDefinition")?.GetValue(unit)
                              ?? AccessTools.Property(unit.GetType(), "PresentationUnitDefinition")?.GetValue(unit);
                var unitName = (unitDef as UnityEngine.Object)?.name;
                if (string.IsNullOrEmpty(unitName)) return;
                foreach (var e in entries)
                {
                    if (e.unit != unitName || e.scale <= 0f || Math.Abs(e.scale - 1f) < 0.001f || e.scaleMode == "data") continue;
                    pc.transform.localScale = Vector3.one * e.scale;
                    if (scaleLogged.Add(unitName))
                        Plugin.Log.LogInfo($"[Formation] '{unitName}' pawns scaled x{e.scale} (Transform mode: root localScale).");
                    return;
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Formation] pawn scale: " + ex); }
        }

        static object DbFind(IEnumerable db, string name)
        {
            foreach (var el in db)
                if ((el as UnityEngine.Object)?.name == name) return el;
            return null;
        }

        // ---- Formation3D prefab dummy-pool extension (called by Hk_FormationPrefabExtend, pre-pool-creation) ----

        internal static void ExtendFormationPrefab()
        {
            try
            {
                if (Plugin.FormationOverrideOn == null || !Plugin.FormationOverrideOn.Value) return;
                EnsureConfig();
                int need = 0;
                foreach (var e in entries) if (e.dummies.Count > need) need = e.dummies.Count;
                if (need == 0) return;

                var st = AccessTools.TypeByName("PresentationEntityFactoryControllerSettings");
                var inst = st != null ? AccessTools.Property(st, "Instance")?.GetValue(null) : null;
                var prefab = inst != null ? AccessTools.Field(st, "Formation3DPrefab")?.GetValue(inst) as Component : null;
                if (prefab == null) { Plugin.Log.LogWarning("[Formation] Formation3DPrefab not reachable — big formations may exceed the dummy pool."); return; }

                int have = ExtendDummies(prefab, need, out int grew);
                if (grew > 0)
                    Plugin.Log.LogInfo($"[Formation] Formation3DPrefab dummy pool extended {have - grew} -> {have} (before Formation3DPool creation).");
                else
                    Plugin.Log.LogInfo($"[Formation] Formation3DPrefab dummy pool: {have} (biggest custom formation needs {need}) — no extension needed.");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Formation] prefab extension: " + ex); }
        }

        // Grow a Formation3D's Dummies array (prefab OR live pooled instance) to `need` by cloning its last dummy
        // child. Returns the resulting length; `grew` = how many were added (0 = already big enough / can't extend).
        // Instantiate remaps intra-clone refs, but the component's Transform/GameObject fields are stamped explicitly.
        internal static int ExtendDummies(Component formation3d, int need, out int grew)
        {
            grew = 0;
            var dummiesF = AccessTools.Field(formation3d.GetType(), "Dummies");
            var arr = dummiesF?.GetValue(formation3d) as Array;
            int have = arr?.Length ?? 0;
            if (have == 0) { Plugin.Log.LogWarning("[Formation] Formation3D has no Dummies array — cannot extend."); return have; }
            if (have >= need) return have;
            var elemT = arr.GetType().GetElementType();
            var template = arr.GetValue(have - 1) as Component;
            if (template == null) { Plugin.Log.LogWarning("[Formation] Formation3D's last dummy is null — cannot extend."); return have; }
            var newArr = Array.CreateInstance(elemT, need);
            Array.Copy(arr, newArr, have);
            var fT = AccessTools.Field(elemT, "Transform");
            var fG = AccessTools.Field(elemT, "GameObject");
            for (int i = have; i < need; i++)
            {
                var clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
                clone.name = $"Dummy [HAF {i}]";
                var comp = clone.GetComponent(elemT);
                fT?.SetValue(comp, clone.transform);
                fG?.SetValue(comp, clone);
                newArr.SetValue(comp, i);
            }
            dummiesF.SetValue(formation3d, newArr);
            grew = need - have;
            return need;
        }

        // Belt-and-braces for the prefab surgery: right before the game lays a definition onto a POOLED Formation3D
        // instance, top the instance itself up if the definition needs more dummies than the instance carries (covers
        // pool clones that predate the prefab extension, and any Unity oddity around parenting clones into an asset).
        // DIAGNOSTIC while the axis is unverified: also log WHICH definition lands on WHICH unit — once per
        // definition name for vanilla, every time for formations named in our registry (they're the ones under test).
        static readonly HashSet<string> initSeen = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> oursArmies = new HashSet<string>(StringComparer.Ordinal);   // armies that ever wore a registry formation: log their re-inits too (a later re-init with a vanilla def would otherwise hide behind the once-per-name gate)
        internal static void EnsureInstanceCapacity(object formation3d, object parent, object definition)
        {
            try
            {
                if (Plugin.FormationOverrideOn == null || !Plugin.FormationOverrideOn.Value) return;
                var f3d = formation3d as Component;
                if (f3d == null || definition == null) return;
                var defName = (definition as UnityEngine.Object)?.name ?? "<null>";
                var defDummies = AccessTools.Field(definition.GetType(), "Dummies")?.GetValue(definition) as Array;
                int need = defDummies?.Length ?? 0;

                bool ours = false;
                foreach (var e in entries) if (e.formation == defName) { ours = true; break; }
                var parentName = (parent as Transform)?.name ?? (parent as Component)?.name ?? "<?>";
                if (ours) oursArmies.Add(parentName);
                if (ours || oursArmies.Contains(parentName) || initSeen.Add(defName))
                {
                    var instArr = AccessTools.Field(f3d.GetType(), "Dummies")?.GetValue(f3d) as Array;
                    Plugin.Log.LogInfo($"[Formation] init: '{defName}' ({need} dummies) -> '{parentName}' (instance capacity {instArr?.Length ?? 0})" + (ours ? "   << REGISTRY FORMATION" : ""));
                }

                if (need <= 0) return;
                int have = ExtendDummies(f3d, need, out int grew);
                if (grew > 0)
                    Plugin.Log.LogInfo($"[Formation] live Formation3D instance topped up {have - grew} -> {have} dummies for '{defName}'.");

                // THE >9 FIX: replace any dummy that isn't parented under THIS instance. When the prefab is grown past the
                // vanilla 9/10, the pooled clones carry a 12-entry Dummies array whose extra entries still REFERENCE the
                // PREFAB's dummies (a runtime-added child isn't remapped on pool-clone the way native children are). Those
                // prefab dummies live at world origin, so the game's `Dummies[i].Transform.localPosition = def.Position`
                // moves a prefab dummy and the instance's pawn is stranded at (~0,0,0) — the "3 warriors lost in the east"
                // (their dummyLocal is right, but the unit's world offset is never applied because the dummy isn't a child
                // of the unit's formation). Fix: clone a genuine instance-child dummy as a fresh child and repoint the slot.
                // Runs as a prefix, BEFORE the positioning loop. No-op when every slot is already a real instance child.
                var arr2 = AccessTools.Field(f3d.GetType(), "Dummies")?.GetValue(f3d) as Array;
                if (arr2 != null && arr2.Length > 0)
                {
                    var elemT = arr2.GetType().GetElementType();
                    var fT = AccessTools.Field(elemT, "Transform");
                    var fG = AccessTools.Field(elemT, "GameObject");
                    var formTf = f3d.transform;
                    Component tmpl = null;                               // a real instance-child dummy to clone from
                    for (int i = 0; i < arr2.Length; i++)
                    {
                        var c = arr2.GetValue(i) as Component;
                        if (c != null && c.transform.IsChildOf(formTf)) { tmpl = c; break; }
                    }
                    int fixedCount = 0;
                    if (tmpl != null)
                        for (int i = 0; i < arr2.Length; i++)
                        {
                            var comp = arr2.GetValue(i) as Component;
                            if (comp != null && comp.transform.IsChildOf(formTf)) continue;   // genuine instance child — leave it
                            var clone = UnityEngine.Object.Instantiate(tmpl.gameObject, tmpl.transform.parent);   // fresh child under the instance
                            clone.name = $"Dummy [HAF-inst {i}]";
                            var cc = clone.GetComponent(elemT) as Component;
                            fT?.SetValue(cc, cc.transform);
                            fG?.SetValue(cc, cc.gameObject);
                            arr2.SetValue(cc, i);                        // repoint the slot at the instance-owned dummy
                            fixedCount++;
                        }
                    if (fixedCount > 0)
                        Plugin.Log.LogInfo($"[Formation] replaced {fixedCount} prefab-bound dummy slot(s) with fresh instance children for '{defName}' (fixes pawns stranded at world origin).");
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Formation] instance capacity: " + ex); }
        }
    }

    // Prefix on PresentationGameObjectPoolController.DoStart — the exact site that news GameObjectPool<Formation3D>
    // from the prefab (per presentation session). Runs before the coroutine body, so the prefab grows before the
    // first clone. REMEMBER: this class must be in Plugin.cs's explicit hook list or it is silently skipped.
    [HarmonyPatch]
    internal static class Hk_FormationPrefabExtend
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("PresentationGameObjectPoolController");
            return t != null ? AccessTools.Method(t, "DoStart") : null;
        }
        static void Prefix() => FormationOverride.ExtendFormationPrefab();
    }

    // Prefix on FormationHelper.InitializeFormation3DForDefinition(formation, parent, definition) — the game laying a
    // formation definition onto a pooled Formation3D instance. Tops the INSTANCE up when the definition outgrows it
    // (see EnsureInstanceCapacity). Must be in Plugin.cs's explicit hook list.
    [HarmonyPatch]
    internal static class Hk_FormationInstanceExtend
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Data.World.FormationHelper")   // verified home (decompile)
                    ?? AccessTools.TypeByName("FormationHelper");
            return t != null ? AccessTools.Method(t, "InitializeFormation3DForDefinition") : null;
        }
        static void Prefix(object __0, object __1, object __2) => FormationOverride.EnsureInstanceCapacity(__0, __1, __2);
    }

    // TEMP diagnostic while the axis lands: postfix on PresentationUnit.InstantiatePawns — the site that decides
    // pawn count = ceil(DummyCount × healthRatio). Logs the actual spawn math for any unit whose formation is bigger
    // than vanilla (DummyCount > 9 = a registry formation), so "9 out of 12" separates into health-driven (working
    // as designed) vs a genuine cap. Must be in Plugin.cs's explicit hook list.
    // Postfix on the single static PresentationPawn.InstantiatePawn(prefab, container, pawnDef, unit, dummy, audio) —
    // applies the registry link's per-model Scale to every pawn the game creates for that unit type. Must be in
    // Plugin.cs's explicit hook list.
    [HarmonyPatch]
    internal static class Hk_FormationPawnScale
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationPawn")
                    ?? AccessTools.TypeByName("PresentationPawn");
            return t != null ? AccessTools.Method(t, "InstantiatePawn") : null;
        }
        static void Postfix(object __result, object __3) => FormationOverride.ApplyPawnScale(__result, __3);
    }

    [HarmonyPatch]
    internal static class Hk_FormationSpawnDiag
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationUnit")
                    ?? AccessTools.TypeByName("PresentationUnit");
            return t != null ? AccessTools.Method(t, "InstantiatePawns") : null;
        }
        static void Postfix(object __instance) => FormationOverride.SpawnDiag(__instance);
    }
}
