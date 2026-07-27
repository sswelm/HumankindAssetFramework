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
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ENCAccessProof
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
        static readonly HashSet<object> reformed = new HashSet<object>();  // units already re-formed this session (once each)

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
                    };
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

                    if (e.unit.Length == 0 || e.formation.Length == 0)
                    { Plugin.Log.LogWarning("[Formation] registry entry skipped (unit or formation name empty)."); continue; }
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

        // Small reflection reader (field OR property) — FormationOverride keeps its own so it needn't reach into UniversalInject.
        static object Mem(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name); if (p != null) { try { return p.GetValue(o); } catch { } }
            var f = t.GetField(name);    if (f != null) { try { return f.GetValue(o); } catch { } }
            return null;
        }

        // Walk the live armies (same path as UniversalInject's post-load respawn) and, for every unit whose
        // PresentationUnitDefinition matches a repointed entry, re-run the game's own UpdatePawns ONCE so it rebuilds its
        // pawn grid at the new dummy count (the plugin's Formation3D-prefab growth + formation overwrite are live by now).
        // Throttled; idempotent per unit (tracked in `reformed`); skips units already at/above the entry's count (they
        // spawned after the override — re-forming them would be a pointless visible pop).
        static void MaybeReinstantiate()
        {
            if (++reformScanFrame % 5 != 0) return;                 // ~12x/s is ample; the frame counter still advances
            var presType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.Presentation");
            var factory = presType == null ? null : AccessTools.Field(presType, "PresentationEntityFactoryController")?.GetValue(null);
            var armies = factory == null ? null : Mem(factory, "PresentationArmyEntities") as Array;
            if (armies == null) return;

            var present = new HashSet<object>();
            foreach (var army in armies)
            {
                if (army == null) continue;
                var unit = Mem(army, "PresentationUnit");
                if (unit == null) continue;
                var pdef = Mem(unit, "PresentationUnitDefinition");
                string pdn = Mem(pdef, "name")?.ToString() ?? Mem(pdef, "Name")?.ToString() ?? "";
                if (pdn.Length == 0) continue;
                var e = entries.FirstOrDefault(x => x.done && x.dummies.Count > 0
                          && string.Equals(x.unit, pdn, StringComparison.OrdinalIgnoreCase));
                if (e == null) continue;                             // not one of our repointed units
                present.Add(unit);
                if (reformed.Contains(unit)) continue;               // already handled this session
                bool loaded = true; try { loaded = Convert.ToBoolean(Mem(unit, "IsLoaded")); } catch { }
                if (!loaded) continue;                                // nothing rendered yet — wait
                int pawns = (Mem(unit, "Pawns") as ICollection)?.Count ?? -1;
                if (pawns >= e.dummies.Count) { reformed.Add(unit); continue; }   // already full — spawned after the override
                reformed.Add(unit);                                  // mark BEFORE the call so a throwing unit isn't retried forever
                bool naval = false; try { naval = Convert.ToBoolean(Mem(unit, "IsNaval")); } catch { }
                AccessTools.Method(unit.GetType(), "UpdatePawns", new[] { typeof(bool) })?.Invoke(unit, new object[] { naval });
                Plugin.Log.LogInfo($"[Formation] re-instantiated '{pdn}' ({pawns} -> up to {e.dummies.Count} pawns) — it had spawned before the override applied.");
            }
            reformed.RemoveWhere(u => !present.Contains(u));         // drop gone units so a genuinely new instance is handled again
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
                // every unit referencing it (usable as a global override; the log is loud so it's never a surprise).
                var fdT = existing.GetType();
                FillFormationFields(existing, fdT, e);
                Plugin.Log.LogWarning($"[Formation] '{e.formation}' already existed in the database — its data was OVERWRITTEN in place " +
                                      $"from the registry ({e.dummies.Count} dummies). If that name is a vanilla formation, every unit using it is affected.");
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
            for (int i = 0; i < e.dummies.Count; i++)
            {
                object d = Activator.CreateInstance(dummyType);   // boxed struct
                fPos.SetValue(d, e.dummies[i].pos);
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
        static void SetFreshElementReference(object owner, string fieldName, string element)
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
