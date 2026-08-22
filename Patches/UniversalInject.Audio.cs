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
        // ---- AUDIO PROBE (step 1 diagnostic) ----
        // Walk every live PresentationSubPawn and log its audio wiring, so we can see WHY custom/retextured units are
        // silent. The decompile says movement/engine sound is posted to an AudioEmitter component on the sub-pawn
        // (NOT the material/mesh) from PresentationPawnDescription.IdleAudioEvent at spawn. This dumps, per sub-pawn:
        // does the AudioEmitter exist? is it registered (EntityName/Name)? is an IdleAudioEvent actually set? — so we can
        // compare a working unit (emblematic corvette: idleEvent SET) against a silent one (our copy: expected empty).
        // Bound to the F8 window; reuses the atlas-dump name filter (e.g. "Corvette" shows both corvettes side by side).
        public static void DumpAudioState(string filter = null)
        {
            try
            {
                var spType = GameBinding.PresentationSubPawn;
                if (spType == null) { Plugin.Log.LogError("[Audio] PresentationSubPawn type not found (game update?)"); return; }
                var holderType = GameBinding.PresentationUnitHolder;
                var all = UnityEngine.Object.FindObjectsOfType(spType);
                Plugin.Diag($"[Audio] --- audio probe: {all.Length} sub-pawns in scene (filter='{filter}') ---");
                int shown = 0;
                foreach (var sp in all)
                {
                    var go = (sp as UnityEngine.Component) != null ? ((UnityEngine.Component)sp).gameObject : null;
                    string goName = go != null ? go.name : "?";
                    var desc = GetMember(sp, "PresentationPawnDescription");
                    string descName = desc is UnityEngine.Object od && od != null ? od.name : "(null desc)";
                    var emitter = GetMember(sp, "AudioEmitter");
                    string entityName = emitter != null ? GetMember(emitter, "EntityName") as string : null;
                    string regName = emitter != null ? GetMember(emitter, "Name") as string : null;
                    string descAudioName = desc != null ? GetMember(desc, "AudioEntityName") as string : null;

                    string hay = goName + " | " + descName + " | " + entityName;
                    if (!string.IsNullOrEmpty(filter) && hay.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // Is an IdleAudioEvent set? IdleAudioEvent is an AudioEventHandleReference struct; .Value resolves the
                    // handle (null when the guid is zero / bank not loaded). This is the engine/idle loop source.
                    string idle = "n/a";
                    if (desc != null)
                    {
                        var idleRef = GetMember(desc, "IdleAudioEvent");
                        if (idleRef == null) idle = "no-field";
                        else { object v = null; try { v = GetMember(idleRef, "Value"); } catch { } idle = v != null ? "SET" : "empty"; }
                    }

                    // Layer C: movement free-event SFX readiness (walk/jog/run hashes populated from the animation).
                    int freeCount = -1;
                    var fh = GetMember(sp, "FreeEventHashes");
                    if (fh != null && GetMember(fh, "Count") is int fc) freeCount = fc;

                    // Is this sub-pawn's emitter actually REGISTERED with Wwise? A present component != registered; an
                    // unregistered emitter silently no-ops every PostEvent. AudioEntityGUID.IsValid == registered.
                    string reg = "n/a";
                    if (emitter != null) { var g = GetMember(emitter, "AudioEntityGUID"); if (g != null && GetMember(g, "IsValid") is bool bv) reg = bv ? "REG" : "unreg"; }

                    // Emitter 3D position vs the unit's actual position — a big gap = the emitter tracks a stale transform
                    // (our re-load), so its sound plays away from the ship and is inaudible even when the right event posts.
                    string pos = "?";
                    if (emitter != null)
                    {
                        var ep = GetMember(emitter, "Position");
                        var tr = GetMember(sp, "Transform") as UnityEngine.Transform;
                        var tp = tr != null ? tr.position : UnityEngine.Vector3.zero;
                        if (ep is UnityEngine.Vector3 epv) pos = $"emit({epv.x:0.0},{epv.z:0.0}) unit({tp.x:0.0},{tp.z:0.0}) d={(epv - tp).magnitude:0.0}";
                    }

                    int eid = emitter is UnityEngine.Object eo2 && eo2 != null ? eo2.GetInstanceID() : 0;
                    // The game posts the engine to our emitters at the right position, yet silent — so check whether the
                    // emitter component actually RUNS (its Update pushes position/speed to Wwise; disabled/inactive => the
                    // sound stays parked at the origin, far from the listener = inaudible).
                    string act = "?";
                    if (emitter is UnityEngine.Behaviour beh)
                        act = $"en={beh.enabled} actHier={(beh.gameObject != null && beh.gameObject.activeInHierarchy)} actSelf={(beh.gameObject != null && beh.gameObject.activeSelf)}";
                    Plugin.Diag($"[Audio] '{goName}' emitter={(emitter != null ? "YES" : "NULL")} id={eid} reg={reg} {act} " +
                                       $"idleEvent={idle} freeEvents={freeCount} pos=[{pos}]");
                    shown++;
                }
                // Layer B: the unit-holder move RUMBLE (generic, posted on move to the HOLDER's own emitter). Holders
                // aren't transform-parents of sub-pawns, so sample them directly. Rumble config is generic, so a few
                // samples tell us if Layer B is set up at all and whether holder emitters register.
                if (holderType != null)
                {
                    var holders = UnityEngine.Object.FindObjectsOfType(holderType);
                    int hi = 0;
                    foreach (var h in holders)
                    {
                        var hemit = GetMember(h, "audioEmitter");
                        string hreg = "n/a";
                        if (hemit != null) { var g = GetMember(hemit, "AudioEntityGUID"); if (g != null && GetMember(g, "IsValid") is bool hb) hreg = hb ? "REG" : "unreg"; }
                        object playV = null; var play = GetMember(h, "playRumbleAudioEvent");
                        if (play != null) { try { playV = GetMember(play, "Value"); } catch { } }
                        Plugin.Diag($"[Audio] holder[{hi}] {h.GetType().Name} emitter={(hemit != null ? "YES" : "NULL")} reg={hreg} rumble={(playV != null ? "SET" : "empty")}");
                        if (++hi >= 8) break;
                    }
                    Plugin.Diag($"[Audio] total holders in scene: {holders.Length}");
                }
                Plugin.Diag($"[Audio] --- probe done: {shown} shown of {all.Length} (emitter reg=REG/unreg, idle/free events, holder rumble) ---");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] DumpAudioState: " + ex); }
        }

        // ---- AUDIO: post a harvested engine/rumble event onto our units' emitters, so we can HEAR something ----
        // Foundation for the from-scratch audio feature. Static config is byte-identical between the audible vanilla
        // unit and our silent copies, so instead of un-breaking the vanilla move-trigger we DRIVE the sound ourselves:
        // harvest one live, registered move-rumble AudioEventHandle (every holder carries one), and PostEvent it straight
        // onto each matched sub-pawn's AudioEmitter (which is present + registered). If audible, we own unit audio and
        // can wire play-on-move / stop-on-idle next. NOTE: rumble is a LOOP — each click stacks another until we add Stop.
        // SILENCE-BY-EVENT-NAME (Hk_SilenceEvents Prefix on AudioManager.PostEvent). Config is a comma-separated list of
        // event-name SUBSTRINGS to drop; a match suppresses the post. Runs on the game's hot audio path, so: empty config
        // fast-returns immediately, and the substring list is re-parsed ONLY when the config string actually changes.
        static string _silenceRaw = "\0";   // sentinel != any real config value (incl. "") so the first call parses
        [ProcessLived("config-derived cache, rebuilt whenever the raw config string changes")] static string[] _silenceSubs = new string[0];
        static string[] SplitSubs(string raw)
        {
            var parts = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>();
            foreach (var p in parts) { var s = p.Trim(); if (s.Length > 0) kept.Add(s); }
            return kept.ToArray();
        }
        internal static bool ShouldSilenceEvent(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // (1) the hand-edit escape hatch: Audio/SilenceAudioEvents config string (re-parsed only when it changes).
            var raw = Plugin.SilenceAudioEvents != null ? Plugin.SilenceAudioEvents.Value : "";
            if (!string.Equals(raw, _silenceRaw, StringComparison.Ordinal)) { _silenceRaw = raw; _silenceSubs = SplitSubs(raw); }
            for (int i = 0; i < _silenceSubs.Length; i++)
                if (name.IndexOf(_silenceSubs[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // (2) the registry: haf_sounds.json, authored by the Game Sound Lab (loaded once at first use).
            EnsureSoundOverrides();
            for (int i = 0; i < _soundOverrideSubs.Length; i++)
                if (name.IndexOf(_soundOverrideSubs[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Game Sound Lab registry: BepInEx/config/haf_sounds.json = { "overrides": [ { "silence": "<event-substring>",
        // "replaceWith": "" } ] }. Loaded once (relaunch to apply Lab edits, same as the district/formation registries).
        // replaceWith is reserved for the future silence-then-substitute step; only `silence` is consumed today.
        static bool _soundOvLoaded;
        [ProcessLived("config-derived cache, rebuilt whenever the raw config string changes")] static string[] _soundOverrideSubs = new string[0];
        static void EnsureSoundOverrides()
        {
            if (_soundOvLoaded) return;
            _soundOvLoaded = true;
            try
            {
                var path = Path.Combine(Paths.ConfigPath, "haf_sounds.json");
                if (!File.Exists(path)) return;
                var arr = JObject.Parse(File.ReadAllText(path))["overrides"] as JArray;
                if (arr == null) return;
                var kept = new List<string>();
                foreach (var o in arr) { var s = (string)o["silence"]; if (!string.IsNullOrEmpty(s)) kept.Add(s.Trim()); }
                _soundOverrideSubs = kept.ToArray();
                if (_soundOverrideSubs.Length > 0)
                    Plugin.Log.LogInfo($"[Audio] sound overrides: {_soundOverrideSubs.Length} silence rule(s) from haf_sounds.json");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] haf_sounds.json parse: " + ex); }
        }

        // Live audio trace (Hk_AudioTrace patches Wwise PostEvent; gated here so it's free until toggled on in F8).
        public static bool AudioTraceOn;
        public static string AudioTraceFilter = "";
        public static object StashedEngineHandle;   // live 'Play_UNIT_Vehicles_ModernBoat_Idle' AudioEventHandle, auto-captured by Hk_AudioTrace
        public static object StashedLoudHandle;      // the per-ship engine MOVE-START handle (Play_UNIT_Vehicles_<Type>_Start), auto-captured
        public static object StashedStopHandle;      // the matching MOVE-STOP handle (..._Stop), auto-captured
        public static string StashedLoudName = "";
        [ProcessLived("diagnostic once-per-event log dedup")] public static readonly System.Collections.Generic.HashSet<string> SeenEvents = new System.Collections.Generic.HashSet<string>();
        public static string EmitterName(object emitter) =>
            (GetMember(emitter, "EntityName") as string) ?? (GetMember(emitter, "Name") as string) ?? emitter?.GetType().Name ?? "?";

        static System.Reflection.MethodInfo _postEvent;
        public static void PlayAudioTest(string filter = null)
        {
            try
            {
                // Post the REAL vehicle engine-idle event (captured live by Hk_AudioTrace) onto each matched sub-pawn's
                // emitter. This is what the trace proved the game itself posts to the audible boats.
                var handle = StashedLoudHandle ?? StashedEngineHandle;
                string which = StashedLoudHandle != null ? StashedLoudName : ((StashedEngineHandle as UnityEngine.Object)?.name ?? "engine");
                if (handle == null) { Plugin.Log.LogError("[Audio] nothing captured — turn Audio Trace ON and give a unit a MOVE ORDER (captures a recognizable sound), then retry."); return; }

                var spType = GameBinding.PresentationSubPawn;
                int posted = 0;
                foreach (var sp in UnityEngine.Object.FindObjectsOfType(spType))
                {
                    var desc = GetMember(sp, "PresentationPawnDescription");
                    string nm = ((sp as UnityEngine.Component) != null ? ((UnityEngine.Component)sp).gameObject.name : "") +
                                " " + (desc is UnityEngine.Object od && od != null ? od.name : "");
                    if (!string.IsNullOrEmpty(filter) && nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var emitter = GetMember(sp, "AudioEmitter");
                    if (emitter == null) continue;
                    if (_postEvent == null)
                        _postEvent = emitter.GetType().GetMethods().FirstOrDefault(m => m.Name == "PostEvent"
                            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "AudioEventHandle");
                    if (_postEvent == null) { Plugin.Log.LogError("[Audio] PostEvent(AudioEventHandle) not found on emitter"); return; }
                    try { _postEvent.Invoke(emitter, new[] { handle }); posted++; }
                    catch (Exception ie) { Plugin.Log.LogWarning("[Audio] engine post: " + (ie.InnerException ?? ie).Message); }
                }
                Plugin.Log.LogInfo($"[Audio] posted '{which}' to {posted} matched sub-pawn emitter(s) (filter='{filter}'). LISTEN.");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] PlayAudioTest: " + ex); }
        }

        // ---- ENGINE AUDIO driver: fire the per-ship move Start/Stop sound on our units, which never trigger it themselves.
        // Polls each of our engineSound units' sub-pawns; on a movement TRANSITION (render-position delta, like deployOnStop)
        // it PostEvents the captured Start (begin) / Stop (end) AudioEventHandle onto that pawn's emitter. The Start/Stop
        // handles are auto-captured from any vehicle move by Hk_AudioTrace, so they're ready once any boat has moved once.
        static int engineFrame;
        static bool _listenerChecked;
        [ProcessLived("derived subset of the shared sub-pawn scan, rebuilt per rescan")] static List<KeyValuePair<UnityEngine.Object, ModelEntry>> _ourSubpawns;   // the audio-relevant subset of the SHARED sub-pawn scan (rebuilt once per rescan)
        static int _engineScanVersion = -1;                                         // last OurSubPawns version this poll rebuilt from
        [ProcessLived("cache keyed on the entries list identity, rebuilt when the registry republishes")] static List<ModelEntry> _audioOn;      // cached audio-enabled subset — the fields it filters on are set once at registry load
        [ProcessLived("cache keyed on the entries list identity, rebuilt when the registry republishes")] static List<ModelEntry> _audioOnSrc;   // the entries list the cache was built from (rebuilt when the registry republishes)

        // ---- SILENCE DONOR AUDIO ----
        // AudioEmitter InstanceIDs whose Wwise posts we drop. Hk_SilenceAudio (prefix on AudioEmitter.PostEvent) reads this
        // every post and returns false — no lock: both writer (this poll) and reader (the post) run on the presentation
        // thread. Stale ids from destroyed emitters are harmless (they just never match a live emitter again).
        [SessionScoped] internal static readonly HashSet<int> _silencedEmitterIds = new HashSet<int>();
        [ProcessLived("per-refresh scratch")] static readonly HashSet<int> _engineLiveIds = new HashSet<int>();   // reused each ~2s subpawn refresh — live sub-pawn ids, to prune the per-pawn engine dicts of dead ones
        // Remove per-pawn dict entries whose sub-pawn is gone (combat death / zoom-LOD rebuild). These id-keyed maps
        // (engineLastPos/engineMoving/customSources/loopHoldUntil/idleNextAt) otherwise ONLY grew — a slow managed-heap
        // leak proportional to pawns spawned. Called only on the ~2s subpawn refresh (not per frame); allocates a
        // removal list only when something actually died.
        static void PruneById<TV>(Dictionary<int, TV> dict, HashSet<int> live)
        {
            if (dict.Count == 0) return;
            List<int> gone = null;
            foreach (var k in dict.Keys) if (!live.Contains(k)) (gone ?? (gone = new List<int>())).Add(k);
            if (gone != null) for (int i = 0; i < gone.Count; i++) dict.Remove(gone[i]);
        }
        // Like PruneById, but for the travel-loop AudioSources: STOP + Destroy each source before forgetting it. A pawn
        // that despawns mid-move — e.g. into a BATTLE, which replaces its PresentationUnit so the old id leaves the live
        // set — would otherwise leave a looping AudioSource playing forever (the "sound that never stopped" after a
        // battle). The normal move→stop transition Pauses it; the despawn path skipped any stop, so do it here.
        static void PruneSources(Dictionary<int, UnityEngine.AudioSource> dict, HashSet<int> live)
        {
            if (dict.Count == 0) return;
            List<int> gone = null;
            foreach (var k in dict.Keys) if (!live.Contains(k)) (gone ?? (gone = new List<int>())).Add(k);
            if (gone == null) return;
            for (int i = 0; i < gone.Count; i++)
            {
                if (dict.TryGetValue(gone[i], out var src) && src != null) { src.Stop(); UnityEngine.Object.Destroy(src); }
                dict.Remove(gone[i]);
            }
        }
        [ProcessLived("per-refresh scratch")] static readonly List<int> _goneEngines = new List<int>();
        // Kill a stuck engine loop by BOTH routes: the captured playing-id (reliable even after the emitter object is gone)
        // and StopAll on the cached game-object id (fallback, and cuts any other voice still on it). Returns a short tag
        // for the diagnostic log so we can see which handles we actually had.
        static string StopEngineLoop(ModelEntry e, int id)
        {
            bool havePid = e.enginePlayingIds.TryGetValue(id, out var pid) && pid != 0;
            bool haveGuid = e.engineEmitterGuids.TryGetValue(id, out var gid) && gid != 0;
            if (havePid) StopPlayingId(pid);
            if (haveGuid) StopByGuid(gid);
            return $"pid={(havePid ? pid.ToString() : "-")} guid={(haveGuid ? gid.ToString() : "-")}";
        }
        // The Wwise engine loop (Play_UNIT_Vehicles_*_Start) is stopped only on a move→stop TRANSITION while the pawn is
        // still polled. A unit that despawns mid-move — notably into a BATTLE, which swaps its PresentationUnit so the id
        // leaves the live set — never gets its _Stop, so the loop runs forever: the "cart sound" that keeps echoing after
        // a battle (verified: OrganGun / SiegeHowitzersCar etc. have engineSound=true). On prune, for any id that was
        // moving, cut its loop by the Wwise game-object id we cached WHILE IT WAS ALIVE (the emitter GameObject may already
        // be destroyed — reading its guid now would fail — so we can't depend on the live emitter reference), then forget it.
        // MUST run before the generic engineMoving/engineLastPos prune, or those dicts are already emptied of the dead ids
        // this scans for (that ordering silently disabled this net once already — the battle echo came straight back).
        static void StopAndPruneEngines(ModelEntry e, HashSet<int> live)
        {
            if (e.engineMoving.Count == 0) return;
            _goneEngines.Clear();
            foreach (var k in e.engineMoving.Keys) if (!live.Contains(k)) _goneEngines.Add(k);
            for (int i = 0; i < _goneEngines.Count; i++)
            {
                int id = _goneEngines[i];
                if (e.engineSound && e.engineMoving[id]) StopEngineLoop(e, id);   // vanished mid-move with a looping _Start — cut it
                e.engineMoving.Remove(id); e.engineEmitterGuids.Remove(id); e.engineLastPos.Remove(id); e.engineLoudSince.Remove(id); e.enginePlayingIds.Remove(id);
            }
        }
        // WATCHDOG — the hard backstop. Despawn detection kept missing the battle case (the loop survived every "detect the
        // vanish" fix), so instead of trusting detection we time-box every loop: while a unit is polled we stamp
        // engineLoudSince[id] = now each poll (a heartbeat). If a loop is still flagged moving but its heartbeat is older
        // than EngineLoopMaxSilence, the unit stopped being polled (battle despawn / kill / LOD drop) yet never got its
        // _Stop — so force-stop it by the cached game-object id. Runs every poll over the small engineMoving dict (NOT the
        // found sub-pawns), so it fires even when the unit is gone from the scene entirely. A genuinely long move refreshes
        // the heartbeat ~10x/s, so it never trips mid-travel.
        const float EngineLoopMaxSilence = 2.5f;   // seconds a loop may run without a heartbeat before we force-stop it
        [ProcessLived("per-tick scratch")] static readonly List<int> _watchdogStop = new List<int>();
        static void WatchdogEngineLoops(ModelEntry e, float now)
        {
            if (!e.engineSound || e.engineMoving.Count == 0) return;
            _watchdogStop.Clear();
            foreach (var kv in e.engineMoving)
                if (kv.Value && (!e.engineLoudSince.TryGetValue(kv.Key, out var seen) || now - seen > EngineLoopMaxSilence))
                    _watchdogStop.Add(kv.Key);
            for (int i = 0; i < _watchdogStop.Count; i++)
            {
                int id = _watchdogStop[i];
                Plugin.Log.LogInfo($"[Audio] engine-loop watchdog: force-stopped '{e.resourceName}' pawn {id} ({StopEngineLoop(e, id)}) — stuck loop after despawn");
                e.engineMoving[id] = false;   // stopped — don't re-fire every poll; a real reappearance re-posts _Start
            }
        }
        static System.Reflection.MethodInfo _akStopAll; static bool _akStopAllTried;
        // Cut everything currently playing on ONE emitter's Wwise game-object — the idle loop that already started at spawn,
        // before this poll first registered the emitter. Future posts are handled by the suppress-prefix, so this is a
        // one-shot per emitter. AkSoundEngine.StopAll(ulong gameObjectId); the id is the emitter's AudioEntityGUID (implicitly
        // a ulong — we read the struct's backing 'guid' field). Best-effort: if the type/method can't be resolved the
        // prefix still stops all FUTURE growls, only the current loop instance would linger.
        static void StopAllOnEmitter(object emitter)
        {
            try
            {
                var g = GetMember(emitter, "AudioEntityGUID"); if (g == null) return;   // AudioEntityGUID struct (boxed)
                var raw = GetMember(g, "guid"); if (raw == null) return;                 // private readonly ulong backing field
                StopByGuid(Convert.ToUInt64(raw));
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Audio] StopAllOnEmitter: " + ex.Message); }
        }
        // Cut everything playing on a Wwise game-object by its id (AkSoundEngine.StopAll(ulong)). Split out of
        // StopAllOnEmitter so StopAndPruneEngines can stop a despawned unit's loop from a guid it cached while the unit was
        // alive — no live emitter reference needed. Best-effort: if the method can't be resolved, future posts are still
        // caught by the suppress-prefix; only an already-playing loop would linger.
        static void StopByGuid(ulong gid)
        {
            try
            {
                if (!_akStopAllTried)
                {
                    _akStopAllTried = true;
                    var ak = GameBinding.AkSoundEngine;
                    if (ak != null)
                        foreach (var m in ak.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                            if (m.Name == "StopAll" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(ulong)) { _akStopAll = m; break; }
                    if (_akStopAll == null) Plugin.Log.LogWarning("[Audio] AkSoundEngine.StopAll(ulong) not found — an already-playing loop may linger until re-init");
                }
                if (_akStopAll == null || gid == 0) return;
                _akStopAll.Invoke(null, new object[] { gid });
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Audio] StopByGuid: " + ex.Message); }
        }

        public static void ProcessEngineAudio()
        {
            var list = entries;
            if (list == null || !Plugin.UniversalInjectOn.Value) return;
            // Throttle FIRST, and cache the filtered subset: the Where().ToList() used to run — and allocate — on
            // EVERY Plugin.Update call, 60x/s for the whole session, before the frame throttle (perf pass 2026-07-19).
            if (++engineFrame % 6 != 0) return;   // ~10x/s (render-position delta = movement)
            if (!ReferenceEquals(_audioOnSrc, list))
            {
                var built = new List<ModelEntry>();
                foreach (var x in list)
                    if ((x.engineSound || x.silenceDonorAudio || !string.IsNullOrEmpty(x.soundFile) || !string.IsNullOrEmpty(x.soundStartFile) || !string.IsNullOrEmpty(x.soundStopFile) || !string.IsNullOrEmpty(x.soundIdleFile) || !string.IsNullOrEmpty(x.soundAttackFile) || !string.IsNullOrEmpty(x.soundDeathFile) || !string.IsNullOrEmpty(x.soundBattleFile)) && !string.IsNullOrEmpty(x.pawnDescription))   // MUST include death/battle — the loader below (and OnPawnDeath/ProcessBattleCries) consume them; omitting them here starved a death-only/battle-cry-only entry (its clip never loaded, and ProcessBattleCries re-enqueued forever)
                        built.Add(x);
                _audioOn = built; _audioOnSrc = list;
            }
            var on = _audioOn;
            if (on.Count == 0) return;
            try
            {
                // Lazy-load custom WAV clips + make sure a Unity AudioListener exists (a Wwise game may have none, which
                // would make our AudioSources silent).
                bool anyCustom = false;
                foreach (var e in on)
                {
                    if (string.IsNullOrEmpty(e.soundFile) && string.IsNullOrEmpty(e.soundStartFile) && string.IsNullOrEmpty(e.soundStopFile) && string.IsNullOrEmpty(e.soundIdleFile) && string.IsNullOrEmpty(e.soundAttackFile) && string.IsNullOrEmpty(e.soundDeathFile) && string.IsNullOrEmpty(e.soundBattleFile)) continue;
                    anyCustom = true;
                    if (!e.customClipTried)
                    {
                        e.customClipTried = true;
                        e.customClip = LoadCustom(e.soundFile, e.resourceName, e.assetDir);
                        e.customStartClip = LoadCustom(e.soundStartFile, e.resourceName + "_start", e.assetDir);
                        e.customStopClip = LoadCustom(e.soundStopFile, e.resourceName + "_stop", e.assetDir);
                        e.customIdleClip = LoadCustom(e.soundIdleFile, e.resourceName + "_idle", e.assetDir);
                        e.customAttackClip = LoadCustom(e.soundAttackFile, e.resourceName + "_attack", e.assetDir);
                        e.customDeathClip = LoadCustom(e.soundDeathFile, e.resourceName + "_death", e.assetDir);
                        e.customBattleClip = LoadCustom(e.soundBattleFile, e.resourceName + "_battle", e.assetDir);
                    }
                }
                if (anyCustom) EnsureAudioListener();

                // The scene scan is SHARED (UniversalInject.SubPawnScan.cs — one FindObjectsOfType per ≤5 s or on a dirty
                // mark, for every consumer); this poll keeps only the audio-relevant entries' sub-pawns and re-runs its
                // per-id pruning exactly once per rescan (perf pass 2026-08-21: this scan + ProcessSubPawnVisuals' copy
                // were ~1.7 ms/frame averaged, as two separate 60-100 ms stalls).
                float now = UnityEngine.Time.time;
                var scan = OurSubPawns(list, now, out int scanVersion);
                if (scanVersion != _engineScanVersion)
                {
                    _engineScanVersion = scanVersion;
                    _ourSubpawns = new List<KeyValuePair<UnityEngine.Object, ModelEntry>>(scan.Count);
                    foreach (var pr in scan) if (on.Contains(pr.Value)) _ourSubpawns.Add(pr);
                    // Prune the per-pawn engine dicts of ids whose sub-pawn is gone (died / LOD-rebuilt) — they only grew.
                    _engineLiveIds.Clear();
                    foreach (var pr in _ourSubpawns) if (pr.Key != null) _engineLiveIds.Add(pr.Key.GetInstanceID());
                    foreach (var e in on)
                    {
                        // StopAndPruneEngines FIRST — it stops the move-loop of any unit that vanished mid-move and then
                        // removes that id from engineMoving/engineLastPos/engineEmitterGuids. Pruning engineMoving here
                        // before it (the old order) emptied the dict of the very dead ids it scans for, so it stopped
                        // nothing and the battle echo leaked. It now owns engineMoving + engineLastPos.
                        StopAndPruneEngines(e, _engineLiveIds);
                        PruneSources(e.customSources, _engineLiveIds); PruneById(e.loopHoldUntil, _engineLiveIds); PruneById(e.idleNextAt, _engineLiveIds);
                    }
                }
                foreach (var pair in _ourSubpawns)
                {
                    var sp = pair.Key; var e = pair.Value;
                    if (sp == null) continue;   // destroyed since the last refresh
                    var comp = sp as UnityEngine.Component;
                    int id = sp.GetInstanceID();

                    // SILENCE DONOR AUDIO: register this pawn's emitter so Hk_SilenceAudio drops its future Wwise posts
                    // (idle growl re-posts + combat maul/scratch), and StopAll ONCE to cut the idle loop already running
                    // since spawn. Our custom WAVs (Unity AudioSource) don't post through the emitter, so they still play.
                    if (e.silenceDonorAudio && GetMember(sp, "AudioEmitter") is UnityEngine.Object emo && emo != null && _silencedEmitterIds.Add(emo.GetInstanceID()))
                    {
                        StopAllOnEmitter(emo);
                        Plugin.Diag($"[Audio] '{e.resourceName}' donor audio silenced (emitter {emo.GetInstanceID()})");
                    }
                    var pos = (GetMember(sp, "Transform") as UnityEngine.Transform)?.position ?? comp.transform.position;
                    bool moving = e.engineLastPos.TryGetValue(id, out var last) && (pos - last).sqrMagnitude > 0.06f * 0.06f;
                    e.engineLastPos[id] = pos;
                    if (e.engineSound) e.engineLoudSince[id] = now;   // watchdog heartbeat: we saw this unit alive this poll
                    bool wasMoving = e.engineMoving.TryGetValue(id, out var wm) && wm;

                    // custom one-shots on a move-start / move-stop TRANSITION (spool-up / spool-down), 3D at the unit.
                    if (moving != wasMoving)
                    {
                        var oneShot = moving ? e.customStartClip : e.customStopClip;
                        if (oneShot != null)
                        {
                            UnityEngine.AudioSource.PlayClipAtPoint(oneShot, pos, moving ? e.soundStartVolume : e.soundStopVolume);
                            if (moving) e.loopHoldUntil[id] = now + oneShot.length;   // hold the loop off so it doesn't mask the spool-up
                        }
                    }

                    // (A0) idle growl: an occasional one-shot WHILE IDLE (not moving), on a per-pawn jittered timer —
                    // replaces a silenced donor's periodic idle vocalization. Moving cancels the cadence (reschedules on
                    // stop) so it never fires mid-walk; jitter (0.6..1.4x) keeps a pack from growling in unison. The first
                    // growl is one interval out, so a fresh spawn/stop doesn't bark instantly.
                    if (e.customIdleClip != null && e.soundIdleInterval > 0f)
                    {
                        if (moving) e.idleNextAt.Remove(id);
                        else if (!e.idleNextAt.TryGetValue(id, out var idleNext))
                            e.idleNextAt[id] = now + e.soundIdleInterval * UnityEngine.Random.Range(0.6f, 1.4f);
                        else if (now >= idleNext)
                        {
                            // GROUP de-dup: a unit is many pawns, so without this all 5 of a formation snarl at once (a
                            // "cut-up" wall). Suppress this growl if another for THIS entry played within groupRadius in the
                            // last interval — a nearby packmate is already the voice; this pawn just waits its next turn. The
                            // voice rotates naturally as timers come due. radius<=0 disables (per-pawn, the old behaviour).
                            bool nearbyRecent = false;
                            if (e.soundIdleGroupRadius > 0f)
                            {
                                float cutoff = now - e.soundIdleInterval;
                                e.idleRecent.RemoveAll(r => r.Value < cutoff);
                                float r2 = e.soundIdleGroupRadius * e.soundIdleGroupRadius;
                                for (int k = 0; k < e.idleRecent.Count; k++)
                                    if ((e.idleRecent[k].Key - pos).sqrMagnitude <= r2) { nearbyRecent = true; break; }
                            }
                            if (!nearbyRecent)
                            {
                                UnityEngine.AudioSource.PlayClipAtPoint(e.customIdleClip, pos, e.soundIdleVolume);
                                if (e.soundIdleGroupRadius > 0f) e.idleRecent.Add(new KeyValuePair<UnityEngine.Vector3, float>(pos, now));
                            }
                            e.idleNextAt[id] = now + e.soundIdleInterval * UnityEngine.Random.Range(0.6f, 1.4f);
                        }
                    }

                    // (A) custom WAV: a Unity AudioSource looped WHILE MOVING, paused when stopped (and held off during any spool-up)
                    if (e.customClip != null)
                    {
                        if (!e.customSources.TryGetValue(id, out var src) || src == null)
                        {
                            src = comp.gameObject.AddComponent<UnityEngine.AudioSource>();
                            src.clip = e.customClip; src.loop = true; src.playOnAwake = false; src.spatialBlend = 1f;
                            src.minDistance = 6f; src.maxDistance = 220f; src.rolloffMode = UnityEngine.AudioRolloffMode.Linear;
                            e.customSources[id] = src;
                        }
                        src.volume = e.soundVolume;   // live-configurable travel-loop volume
                        bool held = e.loopHoldUntil.TryGetValue(id, out var until) && now < until;
                        if (moving && !held && !src.isPlaying) src.Play();
                        else if (!moving && src.isPlaying) src.Pause();
                    }

                    // (B) Wwise engine event: post Start/Stop on a movement TRANSITION
                    var emitter = GetMember(sp, "AudioEmitter");
                    // Cache the Wwise game-object id WHILE THE UNIT IS ALIVE, so a despawn-mid-move (battle) can still stop
                    // its loop after the emitter GameObject is gone (StopAndPruneEngines / the battle-echo fix).
                    if (e.engineSound && emitter != null)
                    {
                        var eg = GetMember(emitter, "AudioEntityGUID");
                        if (eg != null && GetMember(eg, "guid") is ulong egid && egid != 0) e.engineEmitterGuids[id] = egid;
                    }
                    if (e.engineSound && emitter != null && moving != wasMoving)
                    {
                        string evName = moving ? e.engineStartEvent : e.engineStopEvent;
                        if (!string.IsNullOrEmpty(evName))
                        {
                            uint pid = PostEventByName(emitter, evName);   // BY NAME — first-unit-safe
                            if (moving) { if (pid != 0) e.enginePlayingIds[id] = pid; }   // remember the loop so we can stop it after despawn
                            else e.enginePlayingIds.Remove(id);                            // _Stop posted the normal way — nothing stuck to track
                        }
                        else
                        {
                            if (_postEvent == null)
                                _postEvent = emitter.GetType().GetMethods().FirstOrDefault(m => m.Name == "PostEvent"
                                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "AudioEventHandle");
                            var handle = moving ? StashedLoudHandle : StashedStopHandle;
                            if (_postEvent != null && handle != null) try { _postEvent.Invoke(emitter, new[] { handle }); } catch { }
                        }
                    }
                    e.engineMoving[id] = moving;
                }
                // Watchdog runs AFTER the per-pawn loop so this poll's heartbeats are already recorded: present units read
                // fresh, only vanished ones read stale. This is the guaranteed backstop for the battle echo.
                for (int ei = 0; ei < on.Count; ei++) WatchdogEngineLoops(on[ei], now);
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] ProcessEngineAudio: " + ex); }
        }

        // On-demand test: play a loaded custom WAV as a 2D (non-positional) full-volume sound, and report the Unity
        // AudioListener state. Isolates "can Unity play ANY sound in this Wwise game?" from the 3D/movement path. If this
        // is silent while AudioListener.volume reads 0, the game has muted Unity's audio bus (Wwise-only) and we can't use
        // AudioSource for custom files without forcing that back.
        static UnityEngine.AudioSource _testSrc;
        public static void PlaySoundTest()
        {
            try
            {
                var clip = entries?.FirstOrDefault(e => e.customClip != null)?.customClip;
                if (clip == null)
                {
                    var p = Path.Combine(Paths.ConfigPath, "haf_sounds", "drone.wav");
                    if (File.Exists(p)) clip = LoadWav(p, "test");
                }
                if (clip == null) { Plugin.Log.LogError("[Sound] test: no WAV loaded and no haf_sounds/drone.wav to fall back on"); return; }
                EnsureAudioListener();
                float ov = UnityEngine.AudioListener.volume; bool op = UnityEngine.AudioListener.pause;
                UnityEngine.AudioListener.volume = 1f; UnityEngine.AudioListener.pause = false;   // force, in case the game muted Unity's bus
                bool haveListener = UnityEngine.Object.FindObjectOfType<UnityEngine.AudioListener>() != null;
                if (_testSrc == null)
                {
                    var go = new UnityEngine.GameObject("ENC_SoundTest");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _testSrc = go.AddComponent<UnityEngine.AudioSource>();
                    _testSrc.spatialBlend = 0f; _testSrc.volume = 1f; _testSrc.loop = false;
                }
                _testSrc.clip = clip; _testSrc.Play();
                Plugin.Diag($"[Sound] test: playing '{clip.name}' 2D @full volume. AudioListener: present={haveListener}, volume was {ov} (forced 1), pause was {op} (forced false).");
            }
            catch (Exception e) { Plugin.Log.LogError("[Sound] PlaySoundTest: " + e); }
        }

        // A Unity AudioListener is required for our AudioSources to be audible; a Wwise-driven game may ship without one.
        static void EnsureAudioListener()
        {
            if (_listenerChecked) return; _listenerChecked = true;
            try
            {
                if (UnityEngine.Object.FindObjectOfType<UnityEngine.AudioListener>() != null) return;
                var cam = UnityEngine.Camera.main;
                var go = cam != null ? cam.gameObject : new UnityEngine.GameObject("ENC_AudioListener");
                go.AddComponent<UnityEngine.AudioListener>();
                Plugin.Diag("[Sound] no Unity AudioListener found — added one to '" + go.name + "'");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Sound] EnsureAudioListener: " + e.Message); }
        }

        // Load a custom WAV: the owning pack's <assetDir>/sounds/<file> first (per-pack assets, 2026-07-19), then the
        // legacy shared haf_sounds/<file>. Null if unset/missing.
        static UnityEngine.AudioClip LoadCustom(string file, string tag, string assetDir = "")
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (!string.IsNullOrEmpty(assetDir))
            {
                var pp = Path.Combine(assetDir, "sounds", file);
                if (File.Exists(pp)) return LoadWav(pp, tag);
            }
            var p = Path.Combine(Paths.ConfigPath, "haf_sounds", file);
            if (!File.Exists(p)) { Plugin.Log.LogWarning($"[Sound] file not found: {p}" + (string.IsNullOrEmpty(assetDir) ? "" : $" (also tried {Path.Combine(assetDir, "sounds")})")); return null; }
            return LoadWav(p, tag);
        }

        // Load a PCM/float WAV file into an AudioClip (no Wwise, no coroutine). Handles 8/16/24/32-bit PCM + 32-bit float.
        static UnityEngine.AudioClip LoadWav(string path, string tag)
        {
            try
            {
                var b = File.ReadAllBytes(path);
                if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') { Plugin.Log.LogWarning("[Sound] not a WAV (need PCM WAV, not mp3/ogg): " + path); return null; }
                int channels = 1, rate = 44100, bits = 16, fmt = 1, dataOff = -1, dataLen = 0, p = 12;
                while (p + 8 <= b.Length)
                {
                    string cid = System.Text.Encoding.ASCII.GetString(b, p, 4);
                    int csz = BitConverter.ToInt32(b, p + 4);
                    // A corrupt/truncated file can hold a negative chunk size -> p stops advancing -> the game hangs
                    // hard in this loop on the main thread. Bail on any size that can't advance the cursor.
                    if (csz < 0) { Plugin.Log.LogWarning("[Sound] malformed WAV chunk (negative size) in " + path + " — stopping parse."); break; }
                    if (cid == "fmt ") { fmt = BitConverter.ToInt16(b, p + 8); channels = BitConverter.ToInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12); bits = BitConverter.ToInt16(b, p + 22); }
                    else if (cid == "data") { dataOff = p + 8; dataLen = Math.Min(csz, b.Length - (p + 8)); break; }
                    p += 8 + csz + (csz & 1);
                }
                if (dataOff < 0 || channels < 1) { Plugin.Log.LogWarning("[Sound] no data/fmt chunk: " + path); return null; }
                int bps = bits / 8, n = dataLen / bps;
                var f = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int o = dataOff + i * bps;
                    if (fmt == 3 && bits == 32) f[i] = BitConverter.ToSingle(b, o);
                    else if (bits == 16) f[i] = BitConverter.ToInt16(b, o) / 32768f;
                    else if (bits == 32) f[i] = BitConverter.ToInt32(b, o) / 2147483648f;
                    else if (bits == 24) { int v = b[o] | (b[o + 1] << 8) | ((sbyte)b[o + 2] << 16); f[i] = v / 8388608f; }
                    else if (bits == 8) f[i] = (b[o] - 128) / 128f;
                }
                var clip = UnityEngine.AudioClip.Create(tag, n / channels, channels, rate, false);
                clip.SetData(f, 0);
                Plugin.Diag($"[Sound] loaded WAV '{tag}' ({channels}ch {rate}Hz {bits}bit, {(n / (float)channels / rate):0.0}s)");
                return clip;
            }
            catch (Exception e) { Plugin.Log.LogError("[Sound] LoadWav '" + path + "': " + e); return null; }
        }

        // Post a Wwise event BY NAME onto a unit emitter (AkSoundEngine.PostEvent(name, gameObjectID)). Needs no captured
        // handle, so a named engine sound plays for the FIRST unit at load. The emitter's Wwise game-object id is its
        // AudioEntityGUID (a ulong).
        static System.Reflection.MethodInfo _postByName;
        // F8 AUDITION: post a Wwise event by name so a modder can HEAR a catalog sound before silencing/replacing it.
        // Wwise needs a registered game object to post on, so we borrow a live sub-pawn's emitter (AudioEntityGUID.IsValid).
        // The sound plays at that unit's position — fine to audition; a truly 2D ambience event plays 2D regardless.
        public static void PlayEventByName(string eventName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(eventName)) { Plugin.Log.LogWarning("[Audio] Play Event: type an event name first (from Dump Sound Catalog)."); return; }
                eventName = eventName.Trim();
                // Find the AudioEventHandle OBJECT by name (same source DumpSoundCatalog enumerates). We post the handle via
                // the emitter's own PostEvent(AudioEventHandle) — the PROVEN path PlayAudioTest uses — NOT AkSoundEngine's
                // by-name static (which posted silently). The by-name path was the bug.
                var ht = GameBinding.AudioEventHandle;
                object handle = ht == null ? null : UnityEngine.Resources.FindObjectsOfTypeAll(ht).OfType<UnityEngine.Object>()
                    .FirstOrDefault(o => string.Equals(o.name, eventName, StringComparison.OrdinalIgnoreCase));
                if (handle == null) { Plugin.Log.LogError($"[Audio] Play Event: no loaded AudioEventHandle named '{eventName}' (check spelling; some banks only load in certain game states)."); return; }

                // Post on ALL AudioEmitters in the scene — UNITS *and* cities/districts/etc. A unit sound needs a unit
                // emitter; a city-ambience event (Play_HG_ENV_City_*) needs the CITY's emitter — walking only sub-pawns
                // misses it. No camera math (Camera.main is null in-game). If the F8 name filter is set, narrow by
                // emitter name. The listener sits in-scene, so at least one post lands audibly.
                var emType = GameBinding.AudioEmitter;
                if (emType == null) { Plugin.Log.LogError("[Audio] Play Event: AudioEmitter type not found — are you in a loaded game?"); return; }
                int posted = 0;
                foreach (var emObj in UnityEngine.Object.FindObjectsOfType(emType))
                {
                    if (!(emObj is UnityEngine.Object em) || em == null) continue;
                    if (!string.IsNullOrEmpty(AudioTraceFilter) && em.name.IndexOf(AudioTraceFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var g = GetMember(em, "AudioEntityGUID");
                    if (!(g != null && GetMember(g, "IsValid") is bool bv && bv)) continue;   // registered only
                    if (_postEvent == null)
                        _postEvent = em.GetType().GetMethods().FirstOrDefault(m => m.Name == "PostEvent"
                            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "AudioEventHandle");
                    if (_postEvent == null) { Plugin.Log.LogError("[Audio] Play Event: emitter.PostEvent(AudioEventHandle) not found."); return; }
                    try { _postEvent.Invoke(em, new[] { handle }); posted++; } catch (Exception ie) { Plugin.Log.LogWarning("[Audio] Play Event post: " + (ie.InnerException ?? ie).Message); }
                }
                if (posted == 0) { Plugin.Log.LogError("[Audio] Play Event: no registered emitter to post on — load a game (or clear the name filter)."); return; }
                Plugin.Log.LogInfo($"[Audio] Play Event: posted '{eventName}' on {posted} emitter(s) (units + cities). LISTEN.");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] PlayEventByName: " + ex); }
        }

        // Stop the audition: a "_Start" event begins a LOOP that runs until its "_Stop", so posting one leaves it playing.
        // StopAll on every emitter cuts whatever we started (blunt — also stops the game's own sounds on those units, which
        // it re-triggers naturally). Use it to end a looping audition.
        public static void StopEventAudition()
        {
            try
            {
                var emType = GameBinding.AudioEmitter;
                if (emType == null) return;
                int stopped = 0;
                foreach (var emObj in UnityEngine.Object.FindObjectsOfType(emType))
                {
                    if (!(emObj is UnityEngine.Object em) || em == null) continue;
                    StopAllOnEmitter(em); stopped++;
                }
                Plugin.Log.LogInfo($"[Audio] Play Event: STOPPED audio on {stopped} emitter(s).");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] StopEventAudition: " + ex); }
        }

        // Returns the Wwise PLAYING id (AkPlayingID, uint) so a looping _Start can be stopped later by that id even after
        // its emitter game-object is gone. 0 if the post failed or the binding returns void.
        static uint PostEventByName(object emitter, string eventName)
        {
            try
            {
                var g = GetMember(emitter, "AudioEntityGUID");
                if (!(GetMember(g, "guid") is ulong gid)) return 0;
                if (_postByName == null)
                {
                    var ak = GameBinding.AkSoundEngine;
                    _postByName = ak?.GetMethods().FirstOrDefault(m => m.Name == "PostEvent"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(string)
                        && m.GetParameters()[1].ParameterType == typeof(ulong));
                }
                var r = _postByName?.Invoke(null, new object[] { eventName, gid });
                return r is uint pid ? pid : 0;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Audio] postByName '" + eventName + "': " + ex.Message); return 0; }
        }

        static System.Reflection.MethodInfo _akStopPid; static bool _akStopPidTried;
        // Stop ONE playing voice by its Wwise playing-id (AkSoundEngine.StopPlayingID(uint, [int transMs], [curve])). Unlike
        // StopAll(gameObjectId), this targets the voice itself, so it works even after the emitter object is destroyed /
        // unregistered — the reliable way to kill a looping _Start whose unit despawned into a battle. Best-effort.
        static void StopPlayingId(uint playingId)
        {
            try
            {
                if (!_akStopPidTried)
                {
                    _akStopPidTried = true;
                    var ak = GameBinding.AkSoundEngine;
                    if (ak != null)
                        foreach (var m in ak.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                            if (m.Name == "StopPlayingID" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(uint)) { _akStopPid = m; break; }
                    if (_akStopPid == null) Plugin.Log.LogWarning("[Audio] AkSoundEngine.StopPlayingID(uint) not found — despawn loop-stop falls back to StopAll(guid)");
                }
                if (_akStopPid == null || playingId == 0) return;
                var ps = _akStopPid.GetParameters();
                var args = new object[ps.Length];
                args[0] = playingId;
                for (int i = 1; i < ps.Length; i++)   // fill optional transition/curve params with their defaults
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                _akStopPid.Invoke(null, args);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Audio] StopPlayingId: " + ex.Message); }
        }

        // Runtime SOUND EXTRACTOR: write the full Wwise event-name catalog (every loaded AudioEventHandle) to a config
        // file, so the modder can browse the names and assign the right engineStartEvent/engineStopEvent per unit.
        public static void DumpSoundCatalog()
        {
            try
            {
                var t = GameBinding.AudioEventHandle;
                if (t == null) { Plugin.Log.LogError("[Audio] AudioEventHandle type not found"); return; }
                var names = UnityEngine.Resources.FindObjectsOfTypeAll(t).OfType<UnityEngine.Object>()
                    .Select(o => o.name).Where(n => !string.IsNullOrEmpty(n))
                    .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var path = Path.Combine(Paths.ConfigPath, "haf_sound_catalog.txt");
                File.WriteAllLines(path, names);
                Plugin.Log.LogInfo($"[Audio] sound catalog: {names.Count} AudioEventHandle names -> {path}");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] DumpSoundCatalog: " + ex); }
        }

    }
}
