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
                var spType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationSubPawn");
                if (spType == null) { Plugin.Log.LogError("[Audio] PresentationSubPawn type not found (game update?)"); return; }
                var holderType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationUnitHolder");
                var all = UnityEngine.Object.FindObjectsOfType(spType);
                Plugin.Log.LogInfo($"[Audio] --- audio probe: {all.Length} sub-pawns in scene (filter='{filter}') ---");
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
                    Plugin.Log.LogInfo($"[Audio] '{goName}' emitter={(emitter != null ? "YES" : "NULL")} id={eid} reg={reg} {act} " +
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
                        Plugin.Log.LogInfo($"[Audio] holder[{hi}] {h.GetType().Name} emitter={(hemit != null ? "YES" : "NULL")} reg={hreg} rumble={(playV != null ? "SET" : "empty")}");
                        if (++hi >= 8) break;
                    }
                    Plugin.Log.LogInfo($"[Audio] total holders in scene: {holders.Length}");
                }
                Plugin.Log.LogInfo($"[Audio] --- probe done: {shown} shown of {all.Length} (emitter reg=REG/unreg, idle/free events, holder rumble) ---");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] DumpAudioState: " + ex); }
        }

        // ---- AUDIO: post a harvested engine/rumble event onto our units' emitters, so we can HEAR something ----
        // Foundation for the from-scratch audio feature. Static config is byte-identical between the audible vanilla
        // unit and our silent copies, so instead of un-breaking the vanilla move-trigger we DRIVE the sound ourselves:
        // harvest one live, registered move-rumble AudioEventHandle (every holder carries one), and PostEvent it straight
        // onto each matched sub-pawn's AudioEmitter (which is present + registered). If audible, we own unit audio and
        // can wire play-on-move / stop-on-idle next. NOTE: rumble is a LOOP — each click stacks another until we add Stop.
        // Live audio trace (Hk_AudioTrace patches Wwise PostEvent; gated here so it's free until toggled on in F8).
        public static bool AudioTraceOn;
        public static string AudioTraceFilter = "";
        public static object StashedEngineHandle;   // live 'Play_UNIT_Vehicles_ModernBoat_Idle' AudioEventHandle, auto-captured by Hk_AudioTrace
        public static object StashedLoudHandle;      // the per-ship engine MOVE-START handle (Play_UNIT_Vehicles_<Type>_Start), auto-captured
        public static object StashedStopHandle;      // the matching MOVE-STOP handle (..._Stop), auto-captured
        public static string StashedLoudName = "";
        public static readonly System.Collections.Generic.HashSet<string> SeenEvents = new System.Collections.Generic.HashSet<string>();
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

                var spType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationSubPawn");
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
        static List<KeyValuePair<UnityEngine.Object, ModelEntry>> _ourSubpawns;   // cached OUR-units' sub-pawns (refreshed ~every 2s)
        static float _spCacheAt;
        static List<ModelEntry> _audioOn;      // cached audio-enabled subset — the fields it filters on are set once at registry load
        static List<ModelEntry> _audioOnSrc;   // the entries list the cache was built from (rebuilt when the registry republishes)

        // ---- SILENCE DONOR AUDIO ----
        // AudioEmitter InstanceIDs whose Wwise posts we drop. Hk_SilenceAudio (prefix on AudioEmitter.PostEvent) reads this
        // every post and returns false — no lock: both writer (this poll) and reader (the post) run on the presentation
        // thread. Stale ids from destroyed emitters are harmless (they just never match a live emitter again).
        internal static readonly HashSet<int> _silencedEmitterIds = new HashSet<int>();
        static readonly HashSet<int> _engineLiveIds = new HashSet<int>();   // reused each ~2s subpawn refresh — live sub-pawn ids, to prune the per-pawn engine dicts of dead ones
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
                if (!_akStopAllTried)
                {
                    _akStopAllTried = true;
                    var ak = AccessTools.TypeByName("Amplitude.Wwise.Interop.AkSoundEngine") ?? AccessTools.TypeByName("AkSoundEngine");
                    if (ak != null)
                        foreach (var m in ak.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                            if (m.Name == "StopAll" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(ulong)) { _akStopAll = m; break; }
                    if (_akStopAll == null) Plugin.Log.LogWarning("[Audio] AkSoundEngine.StopAll(ulong) not found — an already-playing donor idle loop may linger until re-init");
                }
                if (_akStopAll == null) return;
                var g = GetMember(emitter, "AudioEntityGUID"); if (g == null) return;   // AudioEntityGUID struct (boxed)
                var raw = GetMember(g, "guid"); if (raw == null) return;                 // private readonly ulong backing field
                _akStopAll.Invoke(null, new object[] { Convert.ToUInt64(raw) });
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Audio] StopAllOnEmitter: " + ex.Message); }
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
                    if ((x.engineSound || x.silenceDonorAudio || !string.IsNullOrEmpty(x.soundFile) || !string.IsNullOrEmpty(x.soundStartFile) || !string.IsNullOrEmpty(x.soundStopFile) || !string.IsNullOrEmpty(x.soundIdleFile) || !string.IsNullOrEmpty(x.soundAttackFile)) && !string.IsNullOrEmpty(x.pawnDescription))
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

                var spType = AccessTools.TypeByName("Amplitude.Mercury.Presentation.PresentationSubPawn");
                if (spType == null) return;
                // FindObjectsOfType is a FULL scene scan — running it every poll causes periodic frame spikes (bad 1% lows).
                // Do it (and the name-match) only every ~2s, caching just OUR units' sub-pawns; each poll then touches only
                // those few. New units appear within ~2s; destroyed ones read as Unity fake-null and are skipped.
                float now = UnityEngine.Time.time;
                if (_ourSubpawns == null || now - _spCacheAt > 2f)
                {
                    _ourSubpawns = new List<KeyValuePair<UnityEngine.Object, ModelEntry>>();
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(spType))
                    {
                        if (!(o is UnityEngine.Component c) || c == null) continue;
                        var m = LongestMatch(on, c.gameObject.name, x => x.pawnDescription);   // most-specific match (shared with the inject + combat matchers)
                        if (m != null) _ourSubpawns.Add(new KeyValuePair<UnityEngine.Object, ModelEntry>(o, m));
                    }
                    _spCacheAt = now;
                    // Prune the per-pawn engine dicts of ids whose sub-pawn is gone (died / LOD-rebuilt) — they only grew.
                    _engineLiveIds.Clear();
                    foreach (var pr in _ourSubpawns) if (pr.Key != null) _engineLiveIds.Add(pr.Key.GetInstanceID());
                    foreach (var e in on)
                    {
                        PruneById(e.engineLastPos, _engineLiveIds); PruneById(e.engineMoving, _engineLiveIds);
                        PruneById(e.customSources, _engineLiveIds); PruneById(e.loopHoldUntil, _engineLiveIds); PruneById(e.idleNextAt, _engineLiveIds);
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
                        Plugin.Log.LogInfo($"[Audio] '{e.resourceName}' donor audio silenced (emitter {emo.GetInstanceID()})");
                    }
                    var pos = (GetMember(sp, "Transform") as UnityEngine.Transform)?.position ?? comp.transform.position;
                    bool moving = e.engineLastPos.TryGetValue(id, out var last) && (pos - last).sqrMagnitude > 0.06f * 0.06f;
                    e.engineLastPos[id] = pos;
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
                    if (e.engineSound && emitter != null && moving != wasMoving)
                    {
                        string evName = moving ? e.engineStartEvent : e.engineStopEvent;
                        if (!string.IsNullOrEmpty(evName)) PostEventByName(emitter, evName);   // BY NAME — first-unit-safe
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
                    var p = Path.Combine(Paths.ConfigPath, "enc_sounds", "drone.wav");
                    if (File.Exists(p)) clip = LoadWav(p, "test");
                }
                if (clip == null) { Plugin.Log.LogError("[Sound] test: no WAV loaded and no enc_sounds/drone.wav to fall back on"); return; }
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
                Plugin.Log.LogInfo($"[Sound] test: playing '{clip.name}' 2D @full volume. AudioListener: present={haveListener}, volume was {ov} (forced 1), pause was {op} (forced false).");
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
                Plugin.Log.LogInfo("[Sound] no Unity AudioListener found — added one to '" + go.name + "'");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Sound] EnsureAudioListener: " + e.Message); }
        }

        // Load a custom WAV: the owning pack's <assetDir>/sounds/<file> first (per-pack assets, 2026-07-19), then the
        // legacy shared enc_sounds/<file>. Null if unset/missing.
        static UnityEngine.AudioClip LoadCustom(string file, string tag, string assetDir = "")
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (!string.IsNullOrEmpty(assetDir))
            {
                var pp = Path.Combine(assetDir, "sounds", file);
                if (File.Exists(pp)) return LoadWav(pp, tag);
            }
            var p = Path.Combine(Paths.ConfigPath, "enc_sounds", file);
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
                Plugin.Log.LogInfo($"[Sound] loaded WAV '{tag}' ({channels}ch {rate}Hz {bits}bit, {(n / (float)channels / rate):0.0}s)");
                return clip;
            }
            catch (Exception e) { Plugin.Log.LogError("[Sound] LoadWav '" + path + "': " + e); return null; }
        }

        // Post a Wwise event BY NAME onto a unit emitter (AkSoundEngine.PostEvent(name, gameObjectID)). Needs no captured
        // handle, so a named engine sound plays for the FIRST unit at load. The emitter's Wwise game-object id is its
        // AudioEntityGUID (a ulong).
        static System.Reflection.MethodInfo _postByName;
        static void PostEventByName(object emitter, string eventName)
        {
            try
            {
                var g = GetMember(emitter, "AudioEntityGUID");
                if (!(GetMember(g, "guid") is ulong gid)) return;
                if (_postByName == null)
                {
                    var ak = AccessTools.TypeByName("Amplitude.Wwise.Interop.AkSoundEngine");
                    _postByName = ak?.GetMethods().FirstOrDefault(m => m.Name == "PostEvent"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(string)
                        && m.GetParameters()[1].ParameterType == typeof(ulong));
                }
                _postByName?.Invoke(null, new object[] { eventName, gid });
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Audio] postByName '" + eventName + "': " + ex.Message); }
        }

        // Runtime SOUND EXTRACTOR: write the full Wwise event-name catalog (every loaded AudioEventHandle) to a config
        // file, so the modder can browse the names and assign the right engineStartEvent/engineStopEvent per unit.
        public static void DumpSoundCatalog()
        {
            try
            {
                var t = AccessTools.TypeByName("Amplitude.Wwise.AudioEventHandle") ?? AccessTools.TypeByName("AudioEventHandle");
                if (t == null) { Plugin.Log.LogError("[Audio] AudioEventHandle type not found"); return; }
                var names = UnityEngine.Resources.FindObjectsOfTypeAll(t).OfType<UnityEngine.Object>()
                    .Select(o => o.name).Where(n => !string.IsNullOrEmpty(n))
                    .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var path = Path.Combine(Paths.ConfigPath, "enc_sound_catalog.txt");
                File.WriteAllLines(path, names);
                Plugin.Log.LogInfo($"[Audio] sound catalog: {names.Count} AudioEventHandle names -> {path}");
            }
            catch (Exception ex) { Plugin.Log.LogError("[Audio] DumpSoundCatalog: " + ex); }
        }

    }
}
