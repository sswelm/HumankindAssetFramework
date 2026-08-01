// FacingPersistPatch.cs — persist each army's on-screen FACING across a save/load (2026-08-01).
//
// WHY: the game's own save does NOT store unit facing — the simulation `Unit`/`Army` have no orientation field
// (verified in the decompile); facing lives only on the presentation (`PresentationUnit.FormationAngle`, an int
// world heading) and is rebuilt from movement/actions on load, so a reloaded unit resets its heading. We keep a
// HAF-owned side-file (never touching the game save) and restore it.
//
// HOW (three parts, all reflection — the game types aren't referenced):
//   • MAIN THREAD (Plugin.Update -> FacingPersist.Tick): walk Presentation.PresentationEntityFactoryController.
//     PresentationArmyEntities, snapshot {SimulationEntityGUID -> FormationAngle} for every loaded army into `live`.
//   • SAVE (Sandbox.Save postfix — possibly off-main-thread): write the last `live` snapshot to
//     BepInEx/config/haf_state/facing/<saveName>.facing (keyed by StorageContainerInfo.Name so each save matches).
//   • LOAD (Sandbox.Load postfix): arm the matching file; the main-thread tick then re-applies each army's heading
//     via PresentationUnit.FlipPawnsGrid(angle, Teleport) once the pawn is loaded + settled (past the respawn race).
//
// Key = SimulationEntityGUID (ulong): serialized by the sim, so it survives the load (unlike a tile position).
// Feature-gated by Plugin.PersistUnitFacing. Fail-soft throughout — a facing restore must never break a load.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace HumankindAssetFramework
{
    // ---- SAVE hook: Sandbox.Save(StorageContainerInfo, SerializationFormat, GameSaveDescriptor) — the single choke
    // point for manual/quick/auto saves. __0 = the StorageContainerInfo (its .Name is the save's file identity). ----
    [HarmonyPatch]
    internal static class Hk_SandboxSave
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Sandbox.Sandbox");
            return t?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Save" && m.GetParameters().Length >= 1
                    && m.GetParameters()[0].ParameterType.Name == "StorageContainerInfo");
        }
        static void Postfix(object __0) { try { FacingPersist.OnSave(__0); } catch (Exception ex) { Plugin.Log.LogError("[Facing] save hook: " + ex); } }
    }

    // ---- LOAD hook: Sandbox.Load(StorageContainerInfo). Fires when the SIM is deserialized (presentation not built
    // yet), so we only ARM here and let the main-thread tick apply once pawns exist. ----
    [HarmonyPatch]
    internal static class Hk_SandboxLoad
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Amplitude.Mercury.Sandbox.Sandbox");
            return t?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Load" && m.GetParameters().Length >= 1
                    && m.GetParameters()[0].ParameterType.Name == "StorageContainerInfo");
        }
        static void Postfix(object __0) { try { FacingPersist.OnLoad(__0); } catch (Exception ex) { Plugin.Log.LogError("[Facing] load hook: " + ex); } }
    }

    internal static class FacingPersist
    {
        const int ApplyWindow  = 300;  // keep restoring for ~5s to catch late-loading armies AND re-correct a respawn reset, then stop

        static readonly object gate = new object();
        static readonly Dictionary<ulong, int> live = new Dictionary<ulong, int>();   // guid -> angle, main-thread snapshot (save reads it)

        // apply state (touched on the main thread except pendingFile, set by the load hook thread)
        static string pendingFile;
        static readonly Dictionary<ulong, int> pendingMap = new Dictionary<ulong, int>();
        static readonly HashSet<ulong> applied = new HashSet<ulong>();   // per-army: logged-once guard (drift re-applies still fire)
        static bool mapLoaded;
        static int applyStart = -1;
        static int frame;

        static string Dir => Path.Combine(Paths.ConfigPath, "haf_state", "facing");
        static string Sanitize(string s) => string.Join("_", (string.IsNullOrEmpty(s) ? "save" : s).Split(Path.GetInvalidFileNameChars()));
        static string FileFor(string save) => Path.Combine(Dir, Sanitize(save) + ".facing");

        // ---------------- SAVE (may be off the main thread): dump the last main-thread snapshot ----------------
        internal static void OnSave(object storageContainerInfo)
        {
            if (!Plugin.PersistUnitFacing.Value) return;
            string name = UniversalInject.GetMember(storageContainerInfo, "Name")?.ToString();
            if (string.IsNullOrEmpty(name)) return;
            Dictionary<ulong, int> snap;
            lock (gate) snap = new Dictionary<ulong, int>(live);
            if (snap.Count == 0) return;
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                foreach (var kv in snap)
                    sb.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append(',').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(FileFor(name), sb.ToString());
                Plugin.Log.LogInfo($"[Facing] saved {snap.Count} army facings -> '{name}.facing'");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Facing] write: " + ex); }
        }

        // ---------------- LOAD (may be off the main thread): arm the matching file for the tick to apply ----------
        internal static void OnLoad(object storageContainerInfo)
        {
            if (!Plugin.PersistUnitFacing.Value) return;
            string name = UniversalInject.GetMember(storageContainerInfo, "Name")?.ToString();
            if (string.IsNullOrEmpty(name)) return;
            lock (gate)
            {
                pendingFile = FileFor(name);
                pendingMap.Clear(); applied.Clear();
                mapLoaded = false; applyStart = -1;
            }
            Plugin.Log.LogInfo($"[Facing] load '{name}' — will restore facing if a side-file exists");
        }

        // ---------------- MAIN THREAD (Plugin.Update) ----------------
        internal static void Tick()
        {
            if (!Plugin.PersistUnitFacing.Value) return;
            frame++;
            bool pend; lock (gate) pend = pendingFile != null;
            // During a restore run EVERY frame so we turn the unit the instant its pawn exists (no neutral flash);
            // in steady state throttle to ~4x/s (capture freshness only).
            if (!pend && frame % 15 != 0) return;
            try { Walk(); } catch (Exception ex) { Plugin.Log.LogError("[Facing] tick: " + ex); }
        }

        static void Walk()
        {
            string pf; lock (gate) pf = pendingFile;
            bool applying = pf != null;

            // Load the armed file once (first tick after a load).
            if (applying && !mapLoaded)
            {
                mapLoaded = true; applyStart = frame;
                lock (gate) pendingMap.Clear();
                if (File.Exists(pf))
                {
                    foreach (var line in File.ReadAllLines(pf))
                    {
                        var p = line.Split(',');
                        if (p.Length == 2 && ulong.TryParse(p[0].Trim(), out var g) && int.TryParse(p[1].Trim(), out var a))
                            lock (gate) pendingMap[g] = a;
                    }
                    Plugin.Log.LogInfo($"[Facing] restoring {pendingMap.Count} army facings");
                }
                if (pendingMap.Count == 0) { lock (gate) pendingFile = null; applying = false; }
            }

            var presType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.Presentation");
            var factory = presType == null ? null : CachedField(presType, "PresentationEntityFactoryController")?.GetValue(null);
            var armies = factory == null ? null : UniversalInject.GetMember(factory, "PresentationArmyEntities") as Array;
            if (armies == null) return;

            var snap = new Dictionary<ulong, int>();
            foreach (var army in armies)
            {
                if (army == null) continue;
                var unit = UniversalInject.GetMember(army, "PresentationUnit");
                if (unit == null) continue;
                bool loaded = true; try { loaded = Convert.ToBoolean(UniversalInject.GetMember(unit, "IsLoaded")); } catch { }
                if (!loaded) continue;
                ulong guid; try { guid = Convert.ToUInt64(UniversalInject.GetMember(UniversalInject.GetMember(army, "ArmyInfo"), "SimulationEntityGUID")); } catch { continue; }
                if (guid == 0) continue;
                int angle; try { angle = Convert.ToInt32(UniversalInject.GetMember(unit, "FormationAngle")); } catch { continue; }
                snap[guid] = angle;

                // Restore? Apply the instant a pawn exists — no settle — and keep re-applying whenever the heading has
                // DRIFTED from the target (a fresh load renders neutral; a respawnAfterLoad rebuild resets it). Skipping
                // when already-facing means stable units cost nothing and never jitter.
                if (applying && pendingMap.TryGetValue(guid, out int want))
                {
                    int d = ((angle - want) % 360 + 360) % 360;          // circular difference; ~0 or ~360 == already there
                    if (d > 2 && d < 358)
                    {
                        if (ApplyFacing(unit, want) && applied.Add(guid)) Plugin.Log.LogInfo($"[Facing] restored army {guid} -> {want}°");
                    }
                    else applied.Add(guid);
                }
            }

            lock (gate) { live.Clear(); foreach (var kv in snap) live[kv.Key] = kv.Value; }

            if (applying && applyStart >= 0 && frame - applyStart > ApplyWindow)
            {
                lock (gate) { pendingFile = null; pendingMap.Clear(); }
                Plugin.Log.LogInfo($"[Facing] restore window closed ({applied.Count} armies restored)");
            }
        }

        // FlipPawnsGrid(int absoluteAngle, FormationMoveBehaviour moveType, ...optional) — the line-1837 convenience
        // overload: sets FormationAngle, calls OnFormationOrientationChanged, and (Teleport) rotates every pawn
        // instantly. Resolved once by (int, enum) first two params; optional tail filled from the params' own defaults
        // (useDelay forced false for an instant restore, updateAnimState true).
        static MethodInfo flip; static object teleport;
        static bool ApplyFacing(object unit, int angle)
        {
            try
            {
                if (flip == null)
                {
                    flip = unit.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .Where(m => m.Name == "FlipPawnsGrid").OrderBy(m => m.GetParameters().Length)
                        .FirstOrDefault(m => { var ps = m.GetParameters(); return ps.Length >= 2 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType.IsEnum; });
                    if (flip != null) teleport = Enum.Parse(flip.GetParameters()[1].ParameterType, "Teleport");
                }
                if (flip == null || teleport == null) return false;
                var ps = flip.GetParameters();
                var args = new object[ps.Length];
                args[0] = angle; args[1] = teleport;
                for (int i = 2; i < ps.Length; i++)
                    args[i] = ps[i].Name == "useDelay" ? (object)false
                            : ps[i].Name == "updateAnimState" ? (object)true
                            : ps[i].HasDefaultValue ? ps[i].DefaultValue
                            : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                flip.Invoke(unit, args);
                return true;
            }
            catch (Exception ex) { Plugin.Log.LogError("[Facing] apply: " + ex); return false; }
        }

        static readonly Dictionary<(Type, string), FieldInfo> fc = new Dictionary<(Type, string), FieldInfo>();
        static FieldInfo CachedField(Type t, string n) { var k = (t, n); if (!fc.TryGetValue(k, out var f)) fc[k] = f = AccessTools.Field(t, n); return f; }
    }
}
