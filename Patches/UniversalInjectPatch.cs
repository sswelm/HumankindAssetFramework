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
    // Generic, registry-driven model injector — the runtime half of the Universal Baker. Reads haf_models.json from
    // BepInEx/config (written by the editor), registers every baked skeleton, and on each unit's AddOn.Load repoints
    // the matching pawn definition onto its skeleton using the proven self-discovery (read the host's body mesh name,
    // rename ours to match, resolve, skin via <bodyMesh>_OutputLayer). One patch handles any number of models.
    // One pawn's published movement state (STATE-DRIVEN models, Phase 2): written by the main-thread poll
    // (ProcessAnimStates), read by the per-frame pose hook via nearest-position match (the deploy poll's approximation).
    internal struct StateSample { public UnityEngine.Vector3 pos; public bool moving; public float stoppedAt; public float moveStartedAt; public bool combat; public float combatChangedAt; }

    internal class ModelEntry : Haf.Schema.HafModelSchema   // the ~64 shared behavioral/sound/prop fields live in Haf.Schema now (one definition, inherited); only GUID (sa/sb/..), runtime-state, and non-shared fields stay below
    {
        public string coreDesc = "";     // pawnDescription minus the trailing _NN instance suffix, computed ONCE at registry publish. The per-frame movement polls + the sim-thread FindEntryForUnitDefinition matched units by re-running Regex.Replace(pawnDescription,"_[0-9]+$","") per entry per unit — pure garbage since it's a load-time constant. Read-only after publish (safe from any thread).
        public int sa, sb, sc, sd, ta, tb, tc, td;   // skeleton + atlas Amplitude guid components
        public object skeleton;
        public object hostOutputLayer;
        public UnityEngine.Texture2D tex;
        public bool texOwned;            // true only when `tex` is a texture WE created (LoadSkinPng / BuildAdjustedAtlas) and may Destroy on re-arm. FALSE when `tex` is the raw bundle atlas from LoadAtlas — Destroying that unloads the shared asset so AssetDatabase.LoadAsset then returns NULL (the organ-gun-goes-red-on-reload bug).
        public string layerHint = "";
        public object isolatedLayer;     // our private clone of the host output layer (texture isolation)
        // ---- CLIP ROLES: ONE TABLE (Cut A, 2026-08-21; see ClipRoles.cs). The PRIMARY role is the model's own clip (a
        // drone's spinning-prop 'hover'; not authored = static model, no pose override). The eight STATE-DRIVEN roles
        // (Phase 2, 2026-07-19) each hold their own baked ClipCollection sharing the one skeleton: MOVE plays while the
        // unit travels, AFTER once on stopping, ATTACK once per ranged attack, COMBAT replaces idle while the army is
        // locked in battle, PRE-MOVE once when the unit starts moving (the howitzer folding), IDLE-OVERRIDE is a stance
        // baked as a role (the stance-as-PRIMARY trap encodes ~identity and renders as REST — the howitzer's "forgot to
        // deploy"), IDLE-ALT / IDLE-ALT-2 are occasional flavor one-shots (the tiger's howl / groom) on the jittered
        // idleAltInterval cadence. Guids come from the pack's clip* arrays (both parse paths fill the table); the
        // collection, animId and duration are resolved at registration. Every "all roles" site loops ClipRoles.All.
        public readonly ClipBinding[] Roles = ClipRoles.NewTable();
        public ClipBinding Role(ClipRole r) => Roles[(int)r];
        // Named accessors — sugar INTO the table for the per-role call sites (the pose hook reads `e.attackAnimId`, not
        // `e.Roles[3].animId`). They cannot drift from the table because they ARE the table.
        public object clipColl     { get => Roles[0].coll; set => Roles[0].coll = value; }
        public int    animId       { get => Roles[0].animId; set => Roles[0].animId = value; }
        public float  animDuration { get => Roles[0].dur; set => Roles[0].dur = value; }   // clip duration (s); PawnEntryPose.Time is NORMALIZED (Mathf.Repeat(Time,1) = one loop), so Time = seconds/duration plays it at real speed with every frame
        public object moveClipColl     { get => Roles[1].coll; set => Roles[1].coll = value; }
        public object afterClipColl    { get => Roles[2].coll; set => Roles[2].coll = value; }
        public object attackClipColl   { get => Roles[3].coll; set => Roles[3].coll = value; }
        public object combatClipColl   { get => Roles[4].coll; set => Roles[4].coll = value; }
        public object preMoveClipColl  { get => Roles[5].coll; set => Roles[5].coll = value; }
        public object idleClipColl     { get => Roles[6].coll; set => Roles[6].coll = value; }
        public object idleAltClipColl  { get => Roles[7].coll; set => Roles[7].coll = value; }
        public object idleAlt2ClipColl { get => Roles[8].coll; set => Roles[8].coll = value; }
        public int moveAnimId     { get => Roles[1].animId; set => Roles[1].animId = value; }
        public int afterAnimId    { get => Roles[2].animId; set => Roles[2].animId = value; }
        public int attackAnimId   { get => Roles[3].animId; set => Roles[3].animId = value; }
        public int combatAnimId   { get => Roles[4].animId; set => Roles[4].animId = value; }
        public int preMoveAnimId  { get => Roles[5].animId; set => Roles[5].animId = value; }
        public int idleAnimId     { get => Roles[6].animId; set => Roles[6].animId = value; }
        public int idleAltAnimId  { get => Roles[7].animId; set => Roles[7].animId = value; }
        public int idleAlt2AnimId { get => Roles[8].animId; set => Roles[8].animId = value; }
        public float moveDur     { get => Roles[1].dur; set => Roles[1].dur = value; }
        public float afterDur    { get => Roles[2].dur; set => Roles[2].dur = value; }
        public float attackDur   { get => Roles[3].dur; set => Roles[3].dur = value; }
        public float combatDur   { get => Roles[4].dur; set => Roles[4].dur = value; }
        public float preMoveDur  { get => Roles[5].dur; set => Roles[5].dur = value; }
        public float idleDur     { get => Roles[6].dur; set => Roles[6].dur = value; }
        public float idleAltDur  { get => Roles[7].dur; set => Roles[7].dur = value; }
        public float idleAlt2Dur { get => Roles[8].dur; set => Roles[8].dur = value; }
        // The state machine (StatePose / ProcessAnimStates) must run whenever ANY state role resolved — NOT just move.
        // Gating on moveAnimId alone meant a move-less state-driven model (idle+attack, no move clip) armed fires that
        // never animated — StatePose was never entered (critical-review #8). A loop over the table cannot repeat that.
        public bool AnyStateRole { get { for (int i = 1; i < Roles.Length; i++) if (Roles[i].animId >= 0) return true; return false; } }
        // Per-pawn phase, TRACKED BY POSITION. The pawn entry carries no stable identity (only poses, bone
        // rotations and ObjectSpace), and its array slot is NOT stable: changing camera zoom swaps LODs, the
        // engine re-adds every pawn, and slot-derived phases jump — the animation visibly snapped on every zoom.
        // Position is intrinsic to the pawn, so a nearest-match tracker survives the rebuild AND follows the pawn
        // as it sails. Match radius is deliberately small (well under formation spacing, far over per-frame travel).
        internal class PawnPhase { public UnityEngine.Vector3 pos; public float phase; public float seen; }
        public readonly List<PawnPhase> phaseTracks = new List<PawnPhase>();
        public float phaseLogAt = 0f;        // throttle for the [Phase] census
        public float idleAltNextAt, idleAltStart = -1f, idleAltChosenDur = 1f;   // session cadence state (per entry = one voice per unit type)
        public int idleAltChosenId = -1;
        public UnityEngine.Vector3 idleAltPos;   // which pawn is performing this firing (nearest-match, same 4u radius class)
        // HAND PROP (weapon axis, 2026-07-19): a rigid Prop-Lab mesh glued to a bone of OUR skeleton — the soldier's
        // gun. The donor (an APC) has no weapon slots, so the plugin CONSTRUCTS the FragmentEntry itself at repoint
        // time instead of riding the vanilla slot path. All four are runtime-only registry strings.
        public string rotorSpinBones = "";  // reclaim rotor bones the donor clip hijacks: "BoneName@axis;BoneName@axis" (axis 0/1/2, per-model like turretAxis). Each named bone gets a BoneRotation slot with a constantly-advancing angle — the aim-layer override outranks the clip's channel, so the rotor spins flat about the chosen axis while the donor clip drives the body.
        public float rotorSpinSpeed = 720f; // rotor spin rate, degrees/second (720 = 120 RPM)
        public int[] rotorIdx; public int[] rotorAxis;   // resolved bone indices + axes (cached once)
        public int[] rotorTrimIdx = new int[0], rotorTrimAxis = new int[0]; public float[] rotorTrimDeg = new float[0]; public string rotorTrimSig;   // rotor-trim dial lines resolved to THIS rig's bone slots, re-resolved only when the dial file changes (perf 2026-08-21)
        public bool vfxSilencedLogged;    // session flag: log the first suppressed event once per entry
        public int turretBoneIdx = -2;    // cached bone index for turretBone (-2 = not resolved yet, -1 = not found). Resolved once from e.skeleton.BoneInfos.
        public int gunElevBoneIdx = -2;   // cached bone index (-2 = unresolved, -1 = not found)
        public string muzzleBoneName;     // cached FULL bone name resolved from muzzleBone (null = not resolved yet, "" = not found on our skeleton).
        public UnityEngine.Vector3 muzzleOffsetV; public bool muzzleOffsetParsed;   // parsed once per session
        public bool muzzlePinLogged;      // session flag: log the first StartVFXEvent pin once per entry
        public object handPropLayer;      // session-scoped: our PRIVATE clone of the borrowed weapon output layer, painted with the prop's own atlas (<prop>_Atlas)
        public UnityEngine.Texture2D propAtlasTex;   // session-scoped: the prop atlas — repainted EVERY TICK like the unit retexture (the game resets the material; a one-shot paint flip-flopped between sessions)
        public readonly Dictionary<long, UnityEngine.Vector3> stateLastPos = new Dictionary<long, UnityEngine.Vector3>();  // MAIN thread poll: unit GUID -> last render pos
        public readonly Dictionary<long, bool> stateMoving = new Dictionary<long, bool>();                                 // unit GUID -> was moving last poll (detects the moving->stopped flip)
        public readonly Dictionary<long, float> stateStoppedAt = new Dictionary<long, float>();                            // unit GUID -> Time.time the unit stopped moving
        public readonly Dictionary<long, float> stateMoveStartedAt = new Dictionary<long, float>();                       // unit GUID -> Time.time the unit STARTED moving (the PRE-MOVEMENT one-shot window, e.g. the howitzer folding)
        public readonly Dictionary<long, bool> stateCombat = new Dictionary<long, bool>();                               // unit GUID -> was battle-locked last poll (detects the combat flip)
        public readonly Dictionary<long, float> stateCombatChangedAt = new Dictionary<long, float>();                    // unit GUID -> Time.time combat last FLIPPED (the combatZ ease ramp start)
        public readonly List<StateSample> stateSamples = new List<StateSample>();   // published for the pose hook (lock on it); pos = pawn render position
        public int skeletonId = -1;      // runtime AnimationManager skeleton index of our registered skeleton (to match PawnManager.PawnEntry.SkeletonId)
        public int descId = -1;          // runtime PawnDescriptorId of our unit (learned from the correctly-skinned pawn), to spot the wrong-skeleton twin the game spawns for the same unit
        public bool fragsLogged;         // one-shot: dump the donor's fragment mesh names once, so the modder can find hide targets
        public bool repointed;
        // PER-INSTANCE fire, so only the howitzer that actually bombarded animates (not every howitzer of the type):
        public readonly System.Collections.Concurrent.ConcurrentQueue<long> fireGuidQueue = new System.Collections.Concurrent.ConcurrentQueue<long>();  // SIM thread enqueues the firing unit's SimulationEntityGUID; Plugin.Update (main thread) drains it (no Unity access on the sim thread).
        public readonly List<FireInstance> activeFires = new List<FireInstance>();  // MAIN/render thread only (locked): each firing pawn's render position + start time; the pose hook plays the clip on the pawn nearest an active fire.
        // DEPLOY-ON-STOP (a HELD state, not a one-shot): the clip rests at the DEPLOYED pose by default and snaps to the
        // UNDEPLOYED pose while the unit is moving. Pure function of "is this pawn's unit moving right now" — no state machine,
        // AI/concurrency-safe. Plugin.Update polls PresentationUnit.IsAnyPawnMoving and records the moving pawns' positions.
        // GRADUAL deploy: instead of snapping, ramp each unit's pose time toward its target (0 while moving, deployPoseTime
        // when stopped) at the clip's authored speed, so the legs visibly spread/fold. Progress is per-unit (stateful) so it
        // survives across polls and units entering/leaving view; the pose hook reads the ramped value matched by position.
        public readonly Dictionary<long, float> deployProgress = new Dictionary<long, float>();  // MAIN thread: unit GUID -> current normalized pose time (ramps toward target)
        public readonly Dictionary<long, UnityEngine.Vector3> deployLastPos = new Dictionary<long, UnityEngine.Vector3>();  // MAIN thread: unit GUID -> last render position; movement = the position actually changed (instant fold, settle-immune)
        public readonly List<DeploySample> deploySamples = new List<DeploySample>();             // MAIN thread only (locked): each pawn's render position + its unit's current (ramped) pose time; the pose hook holds that pose on the nearest pawn.
        public float deployLastPoll;          // Time.time of the last deploy poll, for a framerate-independent ramp step
        // ENGINE AUDIO: our injected units never FIRE the per-ship move sound (Play_UNIT_Vehicles_<Type>_Start/_Stop) — it
        // rides the service path tied to the vanilla unit's move state, which our re-loaded units don't trigger. When set,
        // the plugin detects each instance starting/stopping (render-position delta, like deployOnStop) and posts the
        // captured Start/Stop AudioEventHandle onto that pawn's AudioEmitter, restoring the missing engine sound.
        public int lastPawnFrame = -1;   // duplicate-pawn hide (hideSubPawns): Time.frameCount of the last pawn add for this entry
        public readonly List<UnityEngine.Vector3> pawnKeptPos = new List<UnityEngine.Vector3>();   // hideSubPawns: positions of the pawns KEPT this frame — one per distinct UNIT (a unit's stacked squadron duplicates share a position; different units are tiles apart). Keeping per-position, not a per-type count, lets two units of the same model coexist.
        public float rendererCensusNextAt;   // next Unity-renderer census time for this entry (the ghost-rotor hunt)
        public int profCat = -1;             // runtime: the unit's TYPE category (human/land/turret/air/ship) off its capability profile at addon load — drives the category default rates.
        public UnityEngine.Vector3 tiltLastPos; public float tiltCur; public float tiltLastTime;   // move-tilt runtime state
        public UnityEngine.AudioClip customClip, customStartClip, customStopClip, customIdleClip, customAttackClip, customDeathClip, customBattleClip;    // loaded once from the files
        public float deathSoundNextAt, battleCryNextAt;   // per-entry min-gap clocks (a wiped stack / double battle shouldn't chorus)
        public readonly Dictionary<long, float> attackSoundNextAt = new Dictionary<long, float>();   // attacking-pawn id -> earliest Time.time it may play the attack sound again (min-gap)
        public bool customClipTried;                                                 // don't retry a failed load every poll
        public readonly Dictionary<int, float> idleNextAt = new Dictionary<int, float>();   // sub-pawn instance id -> Time.time of its next idle growl (jittered)
        public readonly List<KeyValuePair<UnityEngine.Vector3, float>> idleRecent = new List<KeyValuePair<UnityEngine.Vector3, float>>();  // recent growls (pos, Time.time) for group de-dup — pruned each poll
        public string assetDir = "";     // owning pack's asset root (set at registry load, never parsed from JSON): WAVs/PNGs resolve from <assetDir>/sounds|skins first, then the legacy shared haf_sounds/haf_skins
        public readonly Dictionary<int, UnityEngine.AudioSource> customSources = new Dictionary<int, UnityEngine.AudioSource>();  // sub-pawn instance id -> our looping AudioSource (played while moving)
        public readonly Dictionary<int, float> loopHoldUntil = new Dictionary<int, float>();   // instance id -> Time.time to hold the travel loop off until (so the spool-up one-shot isn't masked)
        public readonly Dictionary<int, UnityEngine.Vector3> engineLastPos = new Dictionary<int, UnityEngine.Vector3>();  // sub-pawn instance id -> last render pos
        public readonly Dictionary<int, bool> engineMoving = new Dictionary<int, bool>();                                  // sub-pawn instance id -> was moving last poll
        public readonly Dictionary<int, ulong> engineEmitterGuids = new Dictionary<int, ulong>();                          // sub-pawn instance id -> its Wwise game-object id, cached WHILE ALIVE so a unit that despawns mid-move (e.g. into a battle) can have its looping Wwise _Start Stopped even after the emitter GameObject is destroyed
        public readonly Dictionary<int, float> engineLoudSince = new Dictionary<int, float>();                            // sub-pawn instance id -> Time.time we last SAW it alive this poll (watchdog heartbeat); if a loop is still flagged active long after this goes stale, the unit vanished and we force-stop it
        public readonly Dictionary<int, uint> enginePlayingIds = new Dictionary<int, uint>();                             // sub-pawn instance id -> the Wwise PLAYING id returned when we posted its _Start loop; StopPlayingID cuts THAT voice regardless of whether the emitter game-object still exists (the reliable despawn stop; StopAll(guid) no-ops once the object is unregistered)
    }

    // One in-flight one-shot: the world position of a pawn that just fired + when it started. The pose hook matches a
    // pawn to the nearest active fire by ObjectSpace position (both are Unity render coords), so only the firer animates.
    // waitAlign (battle-turn spike): the fire is armed but its clip clock is HELD (startTime pinned to now each
    // frame) until the pawn's turn-ease yaw reaches the game's target — the recoil then fires exactly when the
    // barrel faces the enemy. armTime bounds the hold (4 s failsafe). Only set by the artillery arm when turn
    // ease is active; default(false) everywhere else = exact old behavior.
    internal struct FireInstance { public UnityEngine.Vector3 pos; public float startTime; public long pawnId; public bool waitAlign; public float armTime; }
    // A pawn's render position + the (ramped) normalized pose time its unit should currently hold, for the gradual deploy.
    internal struct DeploySample { public UnityEngine.Vector3 pos; public float poseTime; }

    internal static partial class UniversalInject
    {
        internal const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static List<ModelEntry> entries;
        static bool loaded, registered, repointActiveLogged, stLogged, greyWaitLogged;
        static int loadAttempts;   // failed-load counter: latch `loaded` only after a success or a few tries, so a TRANSIENT read/parse error (AV scan, sharing violation at startup) retries instead of disabling injection for the whole session
        static volatile bool reloadRearmPending;   // set by the per-session seams (Sandbox.Load / PawnManager.Load, possibly off the main thread); consumed on the main-thread Update tick so RearmModelRegistration's Unity Destroys run safely
        static volatile bool districtResetSync;    // Sandbox.Load requested its own district reset (via districtResetPending) for the pending rearm — the deferred consume then skips a redundant district reset (a New Game leaves this false so the consume does it)
        static volatile bool districtResetPending; // Sandbox.Load (possibly off the main thread) asks for the district reset; CONSUMED on the main thread — by the next Update tick or by the first district presentation hook of the rebuild, whichever comes first (review 2026-08-21: the reset used to run inline on the sim thread, Clear()ing ~13 collections the main thread reads per frame)
        static UnityEngine.Texture2D _flatN, _white, _black, _grey;   // neutral overlay maps (kill the host's detail/camo)

        // A discovered mod PACK: one registry file's wrapper metadata + its models. HAF (Humankind Asset Framework)
        // multi-mod support merges many packs into `entries`, so ENC is just one mod among many — any modder ships their
        // own pack (config + assets) to join. The wrapper keys (modId/schemaVersion/dependsOn/loadAfter/overrides) sit
        // BESIDE the existing "models" array, so a legacy bare { "models": [...] } file still parses — with default metadata.
        internal class Pack
        {
            public string modId = "", file = "";
            public string assetDir = "";              // per-pack ASSET ROOT (2026-07-19): file-based assets (WAVs in sounds/, PNGs in skins/) resolve here FIRST, then fall back to the legacy shared haf_sounds/haf_skins — so a third-party pack ships self-contained instead of feeling like an ENC extension. "" (the base pack) = legacy folders only.
            public int schemaVersion;
            public string moduleName = "";           // HK LOAD ORDER (2026-08-16): the Humankind runtime module this pack extends. Packs load in the SAME order the game loads their modules. Defaults to the pack's own folder (subdir pack) / filename (flat) — == the module Name by convention, INDEPENDENT of modId (ENC's modId is "enc" but its folder/module is "ENCReload"); an explicit "module" key overrides.
            public string moduleGuid = "";           // optional explicit HK module GUID; wins over moduleName when it matches (stable across a module rename/retitle).
            public List<string> dependsOn = new List<string>();
            public List<string> loadAfter = new List<string>();
            public List<PackOverride> overrides = new List<PackOverride>();   // ENFORCED since 2026-07-19: an explicit, declared replacement of another pack's entry
            public List<ModelEntry> models = new List<ModelEntry>();
        }
        // A declared cross-pack replacement: "this pack intentionally replaces <modId>'s entry on <pawnDescription>".
        // Declared = consensual under the HAF conflict philosophy (an UNdeclared clash is still first-loaded-wins, loud).
        internal class PackOverride { public string modId = "", pawn = ""; }

        static void LoadRegistry()
        {
            if (loaded) return;
            // Build into a LOCAL list and publish `entries` once, fully populated (reference assignment is atomic).
            // The old code published the empty list first and Add()ed into it — the sim-thread combat hook
            // (FindEntryForUnitDefinition) could then foreach over it mid-Add and throw (review 2026-07-19).
            entries = new List<ModelEntry>();   // readers see empty while we build
            var built = new List<ModelEntry>();
            try
            {
                // DISCOVERY: every *.json / <mod>/pack.json a modder drops in haf_packs/ (+ a legacy haf_models.json base
                // file if present). Each file is a PACK — the content-extension of a Humankind runtime MODULE; a joining
                // modder ships their own pack instead of editing ours. Packs are then ORDERED to match the game's own
                // module load order (see the HK-ORDER step below), so first-loaded-wins conflicts resolve exactly as the
                // player's mod order dictates — the framework borrows HK's ordering instead of inventing one.
                var basePath = Path.Combine(Paths.ConfigPath, "haf_models.json");
                var files = new List<string>();
                if (File.Exists(basePath)) files.Add(basePath);
                var packDir = Path.Combine(Paths.ConfigPath, "haf_packs");
                if (Directory.Exists(packDir))
                {
                    var found = new List<string>(Directory.GetFiles(packDir, "*.json"));
                    // Subdirectory packs (2026-07-19): haf_packs/<mymod>/pack.json — the pack's own folder is its
                    // asset root (sounds/, skins/ inside it), so a pack ships as ONE self-contained directory.
                    foreach (var dir in Directory.GetDirectories(packDir))
                    {
                        var pj = Path.Combine(dir, "pack.json");
                        if (File.Exists(pj)) found.Add(pj);
                    }
                    files.AddRange(found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                }
                if (files.Count == 0) { Plugin.Diag("[Uni] no registry at " + basePath + " and no haf_packs/*.json"); loaded = true; return; }

                var packs = new List<Pack>();
                foreach (var file in files)
                {
                    // One unreadable pack must not sink the others — skip it loudly and keep going.
                    try { packs.Add(ParsePack(file, file == basePath)); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[Uni] pack '{Path.GetFileName(file)}' failed to parse ({ex.Message}); skipped"); }
                }
                if (packs.Count == 0) throw new Exception("no packs could be read");   // transient (lock/AV) — retry, don't latch

                // PACK RESOLUTION (enforced 2026-07-19): duplicate modIds rejected, dependsOn validated, load order
                // topologically sorted over dependsOn + loadAfter (stable — no declared constraints = the old
                // base-first + filename order, byte-identical), cycles fall back loudly. See ResolvePacks.
                var resolution = new List<string>();

                // HK-ORDER (2026-08-16): a HAF pack is the content-extension of a Humankind runtime module, so packs load
                // in the SAME order the game loaded their modules (the player's mod order). Match each pack to its module
                // by explicit moduleGuid, else explicit/auto moduleName (== the pack's folder/file name by convention);
                // matched packs sort by the module's load-order INDEX, unmatched packs keep alphabetical order after them.
                // OrderBy is a STABLE sort, so the alphabetical seed order is preserved within an equal key. If the game's
                // module list can't be read (called too early, game update), packs stay alphabetical — the prior behavior.
                var rawMods = GetRuntimeModulesRaw();
                if (rawMods != null && rawMods.Length > 0)
                {
                    var nameIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var guidIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < rawMods.Length; i++)
                    {
                        var parts = (rawMods[i] ?? "").Split('\\');   // "Name\GUID\Version\CRC32\UID\GameVersion"
                        if (parts.Length > 0 && parts[0].Length > 0 && !nameIdx.ContainsKey(parts[0])) nameIdx[parts[0]] = i;
                        if (parts.Length > 1 && parts[1].Length > 0 && !guidIdx.ContainsKey(parts[1])) guidIdx[parts[1]] = i;
                    }
                    Func<Pack, int> orderOf = p =>
                    {
                        if (!string.IsNullOrEmpty(p.moduleGuid) && guidIdx.TryGetValue(p.moduleGuid, out var gi)) return gi;
                        if (!string.IsNullOrEmpty(p.moduleName) && nameIdx.TryGetValue(p.moduleName, out var ni)) return ni;
                        return int.MaxValue;   // unmatched -> alphabetical tail after all module-matched packs
                    };
                    packs = packs.OrderBy(orderOf).ToList();
                    resolution.Add("HK module order: " + string.Join(" → ", packs.Select(p =>
                        { int o = orderOf(p); return p.modId + (o == int.MaxValue ? " (no matching module — alphabetical)" : " #" + o + "→" + p.moduleName); })));
                }
                else resolution.Add("Humankind module order unavailable — packs kept alphabetical");

                packs = ResolvePacks(packs, resolution);
                if (packs.Count == 0) throw new Exception("no packs survived resolution (see haf_load_report.txt)");

                // MERGE with explicit conflict detection. A model's identity is its pawnDescription (the physical pawn slot
                // — two skins can't ride one pawn). Policy: a DECLARED override (the pack's `overrides` array names the
                // owning modId + pawn) REPLACES the earlier entry — declared = consensual, logged as an override, not a
                // conflict. An UNdeclared clash stays FIRST-loaded wins, logged LOUD (no silent overrides — the HAF
                // conflict philosophy).
                var ownerMod = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var ownerIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var conflicts = new List<string>();
                var applied = new List<string>();
                foreach (var pk in packs)
                    foreach (var e in pk.models)
                    {
                        e.assetDir = pk.assetDir;   // file-based assets (WAV/PNG) resolve pack-relative first
                        if (!string.IsNullOrEmpty(e.pawnDescription) && ownerMod.TryGetValue(e.pawnDescription, out var held))
                        {
                            bool declared = false;
                            foreach (var ov in pk.overrides)
                                if (string.Equals(ov.modId, held, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(ov.pawn, e.pawnDescription, StringComparison.OrdinalIgnoreCase))
                                { declared = true; break; }
                            if (declared)
                            {
                                built[ownerIdx[e.pawnDescription]] = e;   // replace in place — ownerIdx stays valid
                                ownerMod[e.pawnDescription] = pk.modId;
                                applied.Add($"pawn={e.pawnDescription} '{pk.modId}' replaces '{held}' (declared override)");
                                Plugin.Diag($"[Uni] OVERRIDE: pack '{pk.modId}' replaces '{held}' on pawn '{e.pawnDescription}' (declared).");
                                continue;
                            }
                            conflicts.Add($"pawn={e.pawnDescription} kept={held} dropped={pk.modId}({e.resourceName})");
                            Plugin.Log.LogWarning($"[Uni] CONFLICT: pack '{pk.modId}' targets pawn '{e.pawnDescription}' already claimed by '{held}' — keeping '{held}' (first-loaded wins; declare it in `overrides` to replace).");
                            continue;
                        }
                        if (e.disabled) { Plugin.Diag($"[Uni] '{e.resourceName}' -> '{e.pawnDescription}': DISABLED in registry — skipping override (original unit rendered)."); continue; }
                        if (!string.IsNullOrEmpty(e.pawnDescription)) { ownerMod[e.pawnDescription] = pk.modId; ownerIdx[e.pawnDescription] = built.Count; }
                        built.Add(e);
                    }

                WriteLoadReport(packs, built.Count, conflicts, applied, resolution);
                Plugin.Log.LogInfo($"[Uni] loaded {packs.Count} pack(s), {built.Count} model(s), {conflicts.Count} conflict(s), {applied.Count} override(s) [" + string.Join(", ", packs.Select(p => p.modId + "×" + p.models.Count)) + "]");
                // RESIZE LAB rules (2026-07-28, user-designed): every pack may carry a "unitScales" array of
                // {match, scale} — a runtime multiplier for ANY pawn whose PRESENTATION DEFINITION name contains
                // `match` (vanilla units included; no bake, no assets). All matching rules MULTIPLY (a per-unit
                // true-size correction rides on a broader rule). Resolved to descriptor ids in RepointMatch,
                // applied at pawn spawn in OnPawnAdded. v2 (planned): trueSize / current-era reference anchoring.
                unitScaleRules.Clear();
                foreach (var file in files)
                {
                    try
                    {
                        var text2 = File.ReadAllText(file);
                        var arr = Regex.Match(text2, "\"unitScales\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                        if (!arr.Success) continue;
                        foreach (Match rm in Regex.Matches(arr.Groups[1].Value, "\\{[^{}]*\\}", RegexOptions.Singleline))
                        {
                            var mm = Regex.Match(rm.Value, "\"match\"\\s*:\\s*\"([^\"]*)\"");
                            var ms = Regex.Match(rm.Value, "\"scale\"\\s*:\\s*(-?[\\d.eE+]+)");
                            if (!mm.Success || !ms.Success) continue;
                            var key = mm.Groups[1].Value.Trim();
                            if (key.Length == 0) continue;
                            var mr = Regex.Match(rm.Value, "\"era\"\\s*:\\s*(-?\\d+)");   // optional: the unit's own era (0/absent = read it off the name)
                            int ruleEra = mr.Success && int.TryParse(mr.Groups[1].Value, out int re) && re > 0 ? re : 0;
                            if (float.TryParse(ms.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sv) && sv > 0f)
                                unitScaleRules.Add(new ScaleRule { match = key, scale = sv, era = ruleEra });
                        }
                    }
                    catch (Exception ex) { Plugin.Log.LogWarning("[Resize] unitScales parse in '" + Path.GetFileName(file) + "': " + ex.Message); }
                }
                if (unitScaleRules.Count > 0)
                    Plugin.Diag($"[Resize] {unitScaleRules.Count} unit-scale rule(s): " + string.Join(", ", unitScaleRules.Select(r => $"'{r.match}'x{r.scale:0.###}" + (r.era > 0 ? $"@era{r.era}" : ""))));

                // GLOBAL ERA LAB (2026-07-29, user-designed): each pack may carry an "eraGrid" — one row per UNIT
                // era holding that unit's rescale modifier for every CURRENT era, i.e. modifier[unitEra][nowEra].
                // A grid, not a curve, because how much a unit should shrink depends on both how old it is and how
                // far the world has moved: in the Contemporary age an Ancient trireme and an Industrial battleship
                // must age differently. Scope is deliberately narrow (user rule): it multiplies units that ALREADY
                // have a scale rule and never resizes anything else. Missing cells fall back to unitEra/nowEra.
                eraGridRows.Clear();
                foreach (var file in files)
                {
                    try
                    {
                        var text3 = File.ReadAllText(file);
                        var arr = Regex.Match(text3, "\"eraGrid\"\\s*:\\s*\\[(.*)\\]", RegexOptions.Singleline);
                        if (!arr.Success) continue;
                        foreach (Match rm in Regex.Matches(arr.Groups[1].Value, "\\{[^{}]*\"scales\"\\s*:\\s*\\[[^\\]]*\\][^{}]*\\}", RegexOptions.Singleline))
                        {
                            var me = Regex.Match(rm.Value, "\"unitEra\"\\s*:\\s*(\\d+)");
                            var sa = Regex.Match(rm.Value, "\"scales\"\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.Singleline);
                            if (!me.Success || !sa.Success || !int.TryParse(me.Groups[1].Value, out int uEra)) continue;
                            var cells = sa.Groups[1].Value.Split(',')
                                .Select(t => float.TryParse(t.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cv) ? cv : 1f)
                                .ToArray();
                            if (cells.Length > 0) eraGridRows[uEra] = cells;   // later packs win, same as the rest of the merge
                        }
                    }
                    catch (Exception ex) { Plugin.Log.LogWarning("[Resize] eraGrid parse in '" + Path.GetFileName(file) + "': " + ex.Message); }
                }
                if (eraGridRows.Count > 0)
                    Plugin.Diag("[Resize] era grid: " + string.Join(" | ", eraGridRows.OrderBy(k => k.Key)
                        .Select(k => $"unit era {k.Key} -> [" + string.Join(",", k.Value.Select(v => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).ToArray()) + "]")));

                // FORMATION BY SIZE (Global Era Lab second table, runtime 2026-07-30): {threshold, formation} rows;
                // when a ruled unit's EFFECTIVE scale (rule x era anchor) drops to <= a threshold, its unit
                // definition is repointed at that formation (first row with threshold >= scale wins; sorted
                // ascending here so the walk can take the first hit) and live units re-form. Above every
                // threshold = the unit's own original formation.
                formationBySize.Clear();
                foreach (var file in files)
                {
                    try
                    {
                        var text4 = File.ReadAllText(file);
                        var arr4 = Regex.Match(text4, "\"formationThresholds\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                        if (!arr4.Success) continue;
                        var rows = new List<KeyValuePair<float, string>>();
                        foreach (Match rm in Regex.Matches(arr4.Groups[1].Value, "\\{[^{}]*\\}", RegexOptions.Singleline))
                        {
                            var th = Regex.Match(rm.Value, "\"threshold\"\\s*:\\s*([0-9.eE+-]+)");
                            var fm = Regex.Match(rm.Value, "\"formation\"\\s*:\\s*\"([^\"]+)\"");
                            if (th.Success && fm.Success
                                && float.TryParse(th.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tv))
                                rows.Add(new KeyValuePair<float, string>(tv, fm.Groups[1].Value));
                        }
                        if (rows.Count > 0) { formationBySize.Clear(); formationBySize.AddRange(rows); }   // later packs win
                    }
                    catch (Exception ex) { Plugin.Log.LogWarning("[Resize] formationThresholds parse in '" + Path.GetFileName(file) + "': " + ex.Message); }
                }
                formationBySize.Sort((a, b) => a.Key.CompareTo(b.Key));
                if (formationBySize.Count > 0)
                    Plugin.Diag("[Resize] formation-by-size: " + string.Join(", ",
                        formationBySize.Select(t => $"<= x{t.Key.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} -> {t.Value}")));
                foreach (var e in built) e.coreDesc = CoreDesc(e.pawnDescription);   // cache the _NN-stripped match key ONCE (read every frame by the movement polls); done before publish so readers never see it unset
                entries = built;   // publish fully built — never mutated after this point
                // Invalidate the pawn-hook early-out caches: if pawns spawned during a transient-failure retry window
                // they latched anyAnimated/anyFreeze against the EMPTY list — without this, a recovered retry would
                // repoint meshes but leave every animated/freeze behavior dead for the session (review round 2).
                anyAnimated = null; anyMuzzle = null; anyFreeze = null; anyRescuable = null;
                loaded = true;
            }
            catch (Exception e)
            {
                // Do NOT latch `loaded` on a failure — a transient hiccup (AV scan, sharing violation while the game is
                // still flushing a file) would otherwise disable ALL injection for the session. Retry on the next few
                // pawn loads; give up (latch) only after 3 tries so a genuinely broken file doesn't re-parse forever.
                entries = new List<ModelEntry>();
                if (++loadAttempts >= 3) { loaded = true; Plugin.Log.LogError("[Uni] registry load failed 3x, giving up for this session: " + e); }
                else Plugin.Log.LogWarning($"[Uni] registry load failed (attempt {loadAttempts}/3), will retry on next pawn: " + e.Message);
            }
        }

        // Read Humankind's ordered ACTIVE runtime-module list — one encoded string per module IN LOAD ORDER
        // ("Name\GUID\Version\CRC32\UID\GameVersion"). Path: Amplitude.Framework.Services.GetService(IRuntimeService)
        // -> Amplitude.Mercury.Runtime.IRuntimeService.GetRuntimeModules(). Fully reflected + guarded: returns null if
        // the API can't be reached (game update, or called before the runtime is up) so the caller falls back to
        // alphabetical pack order. The game sorts modules Standalone→Conversion→Extension, stable within the player's
        // selection, so index 0 is vanilla and higher indices are later-loaded mods.
        static string[] GetRuntimeModulesRaw()
        {
            try
            {
                // Types resolved via GameBinding accessors — the ONE place their names live, and the startup binding
                // report validates them (haf_bindings_report.txt). This is the reflection-drift-net migration pattern:
                // no raw Type.GetType at the call site, so a game rename surfaces in the report instead of silently here.
                var servicesT = GameBinding.FrameworkServices;
                var iRuntime  = GameBinding.RuntimeService;
                if (servicesT == null || iRuntime == null) return null;
                var svc = servicesT.GetMethod("GetService", new[] { typeof(Type) })?.Invoke(null, new object[] { iRuntime });
                if (svc == null) return null;
                return iRuntime.GetMethod("GetRuntimeModules")?.Invoke(svc, null) as string[];
            }
            catch (Exception ex) { Plugin.Diag("[Uni] runtime-module order unavailable (" + ex.Message + ") — packs stay alphabetical."); return null; }
        }

        // Parse one pack file into its wrapper metadata + models. The wrapper is OPTIONAL: a legacy bare { "models": [...] }
        // yields default metadata (modId = "enc" for the base file, else the filename). Throws only if the file can't be read.
        static Pack ParsePack(string file, bool isBase)
        {
            var text = File.ReadAllText(file);
            bool isDirPack = !isBase && string.Equals(Path.GetFileName(file), "pack.json", StringComparison.OrdinalIgnoreCase);
            var pk = new Pack
            {
                file = file,
                // default modId: the DIRECTORY name for a subdirectory pack (every one is named pack.json — the
                // filename would collide as "pack"), else the filename; an explicit "modId" key overrides below.
                modId = isBase ? "enc" : isDirPack ? Path.GetFileName(Path.GetDirectoryName(file)) : Path.GetFileNameWithoutExtension(file),
                // asset root: the pack's own directory for a subdirectory pack; for a flat haf_packs/<name>.json, a
                // sibling folder of the same name (may not exist — resolution then falls through to the legacy
                // shared folders); the base pack keeps the legacy haf_sounds/haf_skins.
                assetDir = isBase ? "" : isDirPack ? Path.GetDirectoryName(file)
                                                   : Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file)),
                // module match key (auto): the pack's own folder (subdir pack) or filename (flat) — the HK module Name
                // by convention. Computed INDEPENDENTLY of modId so ENC (modId "enc", folder "ENCReload") still maps.
                moduleName = isBase ? "" : isDirPack ? Path.GetFileName(Path.GetDirectoryName(file))
                                                     : Path.GetFileNameWithoutExtension(file),
            };
            try
            {
                var root = JObject.Parse(text);
                if (root["modId"] != null) pk.modId = (string)root["modId"];
                if (root["schemaVersion"] != null) pk.schemaVersion = (int)root["schemaVersion"];
                if (root["module"] != null) pk.moduleName = (string)root["module"];        // explicit override of the folder-name auto-match
                if (root["moduleGuid"] != null) pk.moduleGuid = (string)root["moduleGuid"];
                pk.dependsOn = StrList(root["dependsOn"]);
                pk.loadAfter = StrList(root["loadAfter"]);
                if (root["overrides"] is JArray ovs)
                    foreach (var ov in ovs)
                    {
                        string om = (string)ov["modId"] ?? "", op = (string)ov["pawnDescription"] ?? "";
                        if (om.Length > 0 && op.Length > 0) pk.overrides.Add(new PackOverride { modId = om, pawn = op });
                    }
            }
            catch (Exception ex)
            {
                // The wrapper JSON didn't parse — almost always the SAME hand-edit typo that also drops the MODELS parse
                // (below) to its regex fallback. Without recovering the header here, a declared cross-pack `overrides`
                // silently vanished and the merge downgraded it to a first-loaded-wins conflict. Recover by regex + WARN
                // loudly so the modder knows to fix the JSON (models still load, so nothing else signals the header loss).
                Plugin.Log.LogWarning($"[Uni] pack '{Path.GetFileName(file)}' header didn't JSON-parse ({ex.Message}) — recovering modId/dependsOn/loadAfter/overrides by regex. Fix the JSON syntax to be safe.");
                pk.overrides.Clear();   // in case the try partially populated before throwing
                var mid = Regex.Match(text, "\"modId\"\\s*:\\s*\"([^\"]*)\"");
                if (mid.Success && mid.Groups[1].Value.Length > 0) pk.modId = mid.Groups[1].Value;
                var sv = Regex.Match(text, "\"schemaVersion\"\\s*:\\s*(\\d+)");
                if (sv.Success && int.TryParse(sv.Groups[1].Value, out var svi)) pk.schemaVersion = svi;
                var mm = Regex.Match(text, "\"module\"\\s*:\\s*\"([^\"]*)\"");
                if (mm.Success && mm.Groups[1].Value.Length > 0) pk.moduleName = mm.Groups[1].Value;
                var mg = Regex.Match(text, "\"moduleGuid\"\\s*:\\s*\"([^\"]*)\"");
                if (mg.Success && mg.Groups[1].Value.Length > 0) pk.moduleGuid = mg.Groups[1].Value;
                pk.dependsOn = RegexStrArray(text, "dependsOn");
                pk.loadAfter = RegexStrArray(text, "loadAfter");
                var ovBlock = Regex.Match(text, "\"overrides\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                if (ovBlock.Success)
                    foreach (Match ovm in Regex.Matches(ovBlock.Groups[1].Value, "\\{[^}]*\\}"))
                    {
                        var om = Regex.Match(ovm.Value, "\"modId\"\\s*:\\s*\"([^\"]*)\"");
                        var op = Regex.Match(ovm.Value, "\"pawnDescription\"\\s*:\\s*\"([^\"]*)\"");
                        if (om.Success && op.Success && om.Groups[1].Value.Length > 0 && op.Groups[1].Value.Length > 0)
                            pk.overrides.Add(new PackOverride { modId = om.Groups[1].Value, pawn = op.Groups[1].Value });
                    }
            }
            pk.models = ParseModels(text);
            return pk;
        }

        static List<string> StrList(JToken t) =>
            (t as JArray)?.Select(x => (string)x).Where(s => !string.IsNullOrEmpty(s)).ToList() ?? new List<string>();

        // REGEX recovery of a wrapper string-array ("dependsOn"/"loadAfter") when JObject.Parse failed — same resilience
        // the models parse already has. These keys are wrapper-only (never in a model entry), so matching the whole file is safe.
        internal static List<string> RegexStrArray(string text, string field)
        {
            var list = new List<string>();
            var m = Regex.Match(text, "\"" + field + "\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (m.Success)
                foreach (Match s in Regex.Matches(m.Groups[1].Value, "\"([^\"]*)\""))
                    if (s.Groups[1].Value.Length > 0) list.Add(s.Groups[1].Value);
            return list;
        }

        // Write a human-readable load report next to the registry — packs discovered, model counts, reserved metadata, and
        // any conflicts. This is what makes a multi-pack setup DEBUGGABLE (the early slice of HAF runtime diagnostics) and is
        // the first thing a joining modder checks to confirm their pack was seen.
        // PACK RESOLUTION (2026-07-19, was reserved-and-logged): enforce what the wrapper declares, in this order —
        // duplicate modIds rejected (first file keeps the id), dependsOn validated (a missing dependency SKIPS the
        // pack; iterated to a fixpoint since a skip can strand a dependent), then a STABLE topological sort over
        // dependsOn + loadAfter edges. Stability matters: ready packs are picked in SEED order (base first, then
        // filename order), so with no declared constraints the result is byte-identical to the pre-resolution order —
        // today's single-pack setup is provably unaffected. loadAfter naming an absent modId is soft (ignored);
        // a dependency cycle appends its members in seed order with a loud warning. `notes` feeds the load report.
        internal static List<Pack> ResolvePacks(List<Pack> packs, List<string> notes)
        {
            // -- duplicate modIds --
            var byId = new Dictionary<string, Pack>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<Pack>();
            foreach (var pk in packs)
            {
                if (byId.TryGetValue(pk.modId, out var first))
                {
                    notes.Add($"SKIPPED '{Path.GetFileName(pk.file)}': duplicate modId '{pk.modId}' (kept '{Path.GetFileName(first.file)}')");
                    Plugin.Log.LogWarning($"[Uni] pack '{Path.GetFileName(pk.file)}' skipped: duplicate modId '{pk.modId}' — already claimed by '{Path.GetFileName(first.file)}'.");
                    continue;
                }
                byId[pk.modId] = pk; kept.Add(pk);
            }
            // -- dependsOn (hard requirement, fixpoint) --
            bool removedAny = true;
            while (removedAny)
            {
                removedAny = false;
                for (int i = kept.Count - 1; i >= 0; i--)
                    foreach (var dep in kept[i].dependsOn)
                        if (!kept.Any(x => string.Equals(x.modId, dep, StringComparison.OrdinalIgnoreCase)))
                        {
                            notes.Add($"SKIPPED '{kept[i].modId}': dependsOn '{dep}' is not loaded");
                            Plugin.Log.LogWarning($"[Uni] pack '{kept[i].modId}' skipped: dependsOn '{dep}' is not loaded.");
                            kept.RemoveAt(i); removedAny = true; break;
                        }
            }
            // -- stable topological order (Kahn, seed-order picks) --
            var ordered = new List<Pack>();
            var pending = new List<Pack>(kept);
            while (pending.Count > 0)
            {
                Pack pick = null;
                foreach (var pk in pending)
                {
                    bool ready = true;
                    foreach (var pre in pk.dependsOn.Concat(pk.loadAfter))
                        if (pending.Any(x => !ReferenceEquals(x, pk) && string.Equals(x.modId, pre, StringComparison.OrdinalIgnoreCase)))
                        { ready = false; break; }
                    if (ready) { pick = pk; break; }
                }
                if (pick == null)   // every pending pack waits on another pending pack = a cycle
                {
                    notes.Add("CYCLE in dependsOn/loadAfter among: " + string.Join(", ", pending.Select(p => p.modId)) + " — file order used for these");
                    Plugin.Log.LogWarning("[Uni] pack ordering CYCLE among: " + string.Join(", ", pending.Select(p => p.modId)) + " — falling back to file order.");
                    ordered.AddRange(pending);
                    break;
                }
                ordered.Add(pick); pending.Remove(pick);
            }
            for (int i = 0; i < ordered.Count; i++)
                if (!ReferenceEquals(ordered[i], kept[i])) { notes.Add("load order (after sort): " + string.Join(" → ", ordered.Select(p => p.modId))); break; }
            return ordered;
        }

        static void WriteLoadReport(List<Pack> packs, int total, List<string> conflicts, List<string> applied, List<string> resolution)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("HAF load report  (regenerated every load)");
                sb.AppendLine($"packs={packs.Count}  models={total}  conflicts={conflicts.Count}  overrides applied={applied.Count}");
                sb.AppendLine();
                foreach (var p in packs)
                {
                    sb.AppendLine($"[{p.modId}]  schemaVersion={p.schemaVersion}  models={p.models.Count}  file={Path.GetFileName(p.file)}");
                    if (p.dependsOn.Count > 0) sb.AppendLine("    dependsOn: " + string.Join(", ", p.dependsOn));
                    if (p.loadAfter.Count > 0) sb.AppendLine("    loadAfter: " + string.Join(", ", p.loadAfter));
                    if (p.overrides.Count > 0) sb.AppendLine("    overrides declared: " + string.Join(", ", p.overrides.Select(o => o.modId + ":" + o.pawn)));
                }
                if (resolution.Count > 0) { sb.AppendLine(); sb.AppendLine("RESOLUTION:"); foreach (var r in resolution) sb.AppendLine("  " + r); }
                if (applied.Count > 0) { sb.AppendLine(); sb.AppendLine("OVERRIDES APPLIED (declared replacements):"); foreach (var a in applied) sb.AppendLine("  " + a); }
                if (conflicts.Count > 0) { sb.AppendLine(); sb.AppendLine("CONFLICTS (undeclared — first-loaded kept; declare in `overrides` to replace):"); foreach (var c in conflicts) sb.AppendLine("  " + c); }
                File.WriteAllText(Path.Combine(Paths.ConfigPath, "haf_load_report.txt"), sb.ToString());
            }
            catch { }
        }

        // Parse the "models" array from one registry file's text. PRIMARY: Newtonsoft (object-per-model, robust to a
        // missing/reordered field). FALLBACK: field-by-field regex (index-aligned). Semantics identical to the original
        // single-file loader; lifted into a helper so every pack shares it. The local `entries` intentionally shadows the
        // field to keep the large per-field Add blocks below verbatim.
        // CONFIG-KEY WHITELIST for the generic parse (2026-08-17, verified-review finding): ToObject<ModelEntry>
        // binds ANY name-matching public field — including runtime-state (`repointed`/`descId`/`animId`/`assetDir`/
        // the per-session dictionaries), which a hostile or typo'd pack key could poison before the session re-arm
        // ever runs; worse, a key colliding with a READONLY collection (`phaseTracks`) makes ToObject THROW,
        // silently demoting the whole pack to the fragile index-aligned regex fallback. So every model object is
        // stripped to declared config BEFORE the generic map. Fail-safe by default: a NEW shared field is
        // whitelisted by reflection automatically, and a NEW runtime-state field is protected without anyone
        // remembering an attribute. Plugin-only CONFIG keys (rare — rotorSpin*) must be added here; a new one
        // that's forgotten fails LOUD (the feature's key is stripped, Diag names it). Bake-time-only editor keys
        // (targetTris, convertRig, …) are stripped too — the plugin never read them; Diag-logged, not warned,
        // because every real pack carries ~50 of them by design.
        static readonly HashSet<string> registryConfigKeys = BuildRegistryConfigKeys();
        static HashSet<string> BuildRegistryConfigKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in typeof(Haf.Schema.HafModelSchema).GetFields(BindingFlags.Public | BindingFlags.Instance))
                keys.Add(f.Name);
            keys.UnionWith(new[] {
                "rotorSpinBones", "rotorSpinSpeed",   // plugin-only config (no editor field; parity-allowlisted runtime-only keys)
                "skel", "atlas", "clip", "clipMove", "clipAfter", "clipAttack", "clipCombat",
                "clipPreMove", "clipIdle", "clipIdleAlt", "clipIdleAlt2",   // GUID arrays — hand-pinned after ToObject, so they must survive the strip
            });
            return keys;
        }

        internal static List<ModelEntry> ParseModels(string text)
        {
            var entries = new List<ModelEntry>();
                // PRIMARY: Newtonsoft (the game's own copy) parses each model as an OBJECT, so fields stay with their
                // model — robust to a missing/reordered field on any single model, unlike the index-aligned regex below.
                // UnityEngine.JsonUtility silently returns empty in the game's Mono runtime (works only in the editor),
                // so it's not usable here; Newtonsoft works in-process. Regex is kept as a last-resort fallback.
                try
                {
                    var models = JObject.Parse(text)["models"] as JArray;
                    if (models != null && models.Count > 0)
                    {
                        int A(JToken arr, int k) => (arr is JArray a && k < a.Count) ? (int)a[k] : 0;
                        float Fp(JToken o, string k) => o?[k] != null ? (float)o[k] : 0f;
                        foreach (var m in models)
                        {
                            // position is a UnityEngine.Vector3: Newtonsoft chokes deserializing it (its `normalized`
                            // property self-references Vector3), so pull the key OUT of the object before the generic map
                            // and re-pin it by hand below — otherwise ToObject throws and the whole model drops to the
                            // fragile index-aligned regex fallback. Read it first, then remove.
                            var p = m["position"]; (m as JObject)?.Remove("position");
                            // Strip every non-config key (see registryConfigKeys above) so the generic map below can
                            // only ever touch declared config — runtime-state fields are unreachable from pack JSON.
                            if (m is JObject mo)
                            {
                                List<string> stripped = null;
                                foreach (var prop in mo.Properties().ToList())
                                    if (!registryConfigKeys.Contains(prop.Name))
                                    { (stripped ?? (stripped = new List<string>())).Add(prop.Name); prop.Remove(); }
                                if (stripped != null)
                                    Plugin.Diag("[Uni] stripped " + stripped.Count + " non-config key(s) pre-parse: " + string.Join(", ", stripped));
                            }
                            // The name-matching config (every string/bool/float/int field, inherited from the shared
                            // HafModelSchema + ModelEntry's own whitelisted keys) deserializes generically — one mapping,
                            // no hand-list to drift against the editor. Absent keys fall to each field's initializer
                            // (the shared defaults); runtime-state fields CANNOT bind — the strip above removed them.
                            var e = m.ToObject<ModelEntry>();
                            // The GUID arrays also DON'T map by name (one JSON array skel[] -> four ints sa/sb/sc/sd, etc.),
                            // so they're extracted explicitly here.
                            var s = m["skel"]; var t = m["atlas"]; var c = m["clip"];
                            var cmv = m["clipMove"]; var cfa = m["clipAfter"]; var cat = m["clipAttack"]; var ccb = m["clipCombat"]; var cpv = m["clipPreMove"]; var cid = m["clipIdle"]; var cAlt = m["clipIdleAlt"]; var ca2 = m["clipIdleAlt2"];
                            e.sa = A(s, 0); e.sb = A(s, 1); e.sc = A(s, 2); e.sd = A(s, 3);
                            e.ta = A(t, 0); e.tb = A(t, 1); e.tc = A(t, 2); e.td = A(t, 3);
                            // clip-role guid quads → the ROLE TABLE. One line per role, each reading its whole array:
                            // no per-component hand-copy (the `alc` class of typo) is possible here.
                            void R(ClipRole r, JToken arr) => e.Role(r).Set(A(arr, 0), A(arr, 1), A(arr, 2), A(arr, 3));
                            R(ClipRole.Primary, c); R(ClipRole.Move, cmv); R(ClipRole.After, cfa); R(ClipRole.Attack, cat); R(ClipRole.Combat, ccb);
                            R(ClipRole.PreMove, cpv); R(ClipRole.IdleOverride, cid); R(ClipRole.IdleAlt, cAlt); R(ClipRole.IdleAlt2, ca2);
                            e.position = new UnityEngine.Vector3(Fp(p, "x"), Fp(p, "y"), Fp(p, "z"));
                            entries.Add(e);
                        }
                        Plugin.Log.LogInfo($"[Uni] parsed {entries.Count} model(s) via Newtonsoft [" + string.Join(", ", entries.Select(e => e.resourceName + "->" + e.pawnDescription)) + "]");
                        return entries;
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("[Uni] Newtonsoft parse failed (" + ex.Message + "); using regex fallback"); entries.Clear(); }

                // FALLBACK: field-by-field regex. Each model has exactly one of each field in document order, so the i-th
                // match of each belongs to model i (fragile only if a MIDDLE model omits a field — the object parse above avoids that).
                const string i4 = @"\[\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\]";
                var rn = Regex.Matches(text, "\"resourceName\"\\s*:\\s*\"([^\"]*)\"");
                var pd = Regex.Matches(text, "\"pawnDescription\"\\s*:\\s*\"([^\"]*)\"");
                var hm = Regex.Matches(text, "\"hideMeshes\"\\s*:\\s*\"([^\"]*)\"");
                var sk = Regex.Matches(text, "\"skel\"\\s*:\\s*" + i4);
                var at = Regex.Matches(text, "\"atlas\"\\s*:\\s*" + i4);
                var cl = Regex.Matches(text, "\"clip\"\\s*:\\s*" + i4);   // ClipCollection guid (animated models); absent on static models
                var cmvR = Regex.Matches(text, "\"clipMove\"\\s*:\\s*" + i4);    // parity: STATE-DRIVEN movement ClipCollection guid
                var cfaR = Regex.Matches(text, "\"clipAfter\"\\s*:\\s*" + i4);   // parity: STATE-DRIVEN after-movement ClipCollection guid
                var catR = Regex.Matches(text, "\"clipAttack\"\\s*:\\s*" + i4);  // parity: STATE-DRIVEN attack ClipCollection guid
                var ccbR = Regex.Matches(text, "\"clipCombat\"\\s*:\\s*" + i4);  // parity: STATE-DRIVEN combat-idle ClipCollection guid
                var cpvR = Regex.Matches(text, "\"clipPreMove\"\\s*:\\s*" + i4); // parity: STATE-DRIVEN pre-movement ClipCollection guid
                var cidR = Regex.Matches(text, "\"clipIdle\"\\s*:\\s*" + i4);   // parity: STATE-DRIVEN idle-override ClipCollection guid
                var calR = Regex.Matches(text, "\"clipIdleAlt\"\\s*:\\s*" + i4);   // parity: idle-alt flavor one-shot ClipCollection guid
                var ca2R = Regex.Matches(text, "\"clipIdleAlt2\"\\s*:\\s*" + i4);  // parity: second idle-alt flavor ClipCollection guid
                var iai = Regex.Matches(text, "\"idleAltInterval\"\\s*:\\s*(-?[\\d.eE+]+)");   // parity: avg seconds between idle-alt one-shots
                var aps = Regex.Matches(text, "\"animPhaseSpread\"\\s*:\\s*(-?[\\d.eE+]+)");  // parity: per-instance animation phase spread
                var asd = Regex.Matches(text, "\"animStateDriven\"\\s*:\\s*(true|false)");   // parity: state-driven mode flag
                // position object {x,y,z} — JsonUtility writes Vector3 in x,y,z order. Applied as a runtime world offset for animated models.
                var po = Regex.Matches(text, "\"position\"\\s*:\\s*\\{\\s*\"x\"\\s*:\\s*(-?[\\d.eE+]+)\\s*,\\s*\"y\"\\s*:\\s*(-?[\\d.eE+]+)\\s*,\\s*\"z\"\\s*:\\s*(-?[\\d.eE+]+)");
                var ra = Regex.Matches(text, "\"respawnAfterLoad\"\\s*:\\s*(true|false)");   // parity with the Newtonsoft path (line ~77) — else the first-instance rotor fix silently defaults off here
                var fz = Regex.Matches(text, "\"freezeDonorAnim\"\\s*:\\s*(true|false)");   // parity with the Newtonsoft path — else the donor-animation freeze silently defaults off here
                var cal = Regex.Matches(text, "\"clearAimLayer\"\\s*:\\s*(true|false)");    // parity: per-model aim-layer clear (state-driven artillery)
                var dsb = Regex.Matches(text, "\"disabled\"\\s*:\\s*(true|false)");         // parity: per-model DEBUG disable (show original unit)
                var foa = Regex.Matches(text, "\"fireOnAttack\"\\s*:\\s*(true|false)");     // parity: play the clip once on attack vs loop
                var dos = Regex.Matches(text, "\"deployOnStop\"\\s*:\\s*(true|false)");     // parity: hold deployed when idle, undeploy while moving
                var eng = Regex.Matches(text, "\"engineSound\"\\s*:\\s*(true|false)");      // parity: fire the per-ship engine move sound on our units
                var hsp = Regex.Matches(text, "\"hideSubPawns\"\\s*:\\s*(true|false)");    // parity: strip the donor's secondary sub-pawns (the "GPU rotor")
                var mtl = Regex.Matches(text, "\"moveTilt\"\\s*:\\s*(-?[0-9.]+)");         // parity: nose-down pitch while moving (helicopter attitude)
                var trt = Regex.Matches(text, "\"turnRate\"\\s*:\\s*(-?[0-9.]+)");         // parity: eased facing, deg/s
                var tbk = Regex.Matches(text, "\"turnBank\"\\s*:\\s*(-?[0-9.]+)");         // parity: bank into the turn, degrees
                var gem = Regex.Matches(text, "\"gunElevMax\"\\s*:\\s*(-?[0-9.]+)");       // parity: distance-proportional barrel elevation, max degrees
                var gea = Regex.Matches(text, "\"gunElevAxis\"\\s*:\\s*(-?[0-9]+)");       // parity: elevation local axis index
                var hgd = Regex.Matches(text, "\"hugDrop\"\\s*:\\s*(-?[0-9.]+)");          // parity: terrain hug drop, units
                var hgl = Regex.Matches(text, "\"hugLookahead\"\\s*:\\s*(-?[0-9.]+)");     // parity: terrain hug probe lead, units
                var cbz = Regex.Matches(text, "\"combatZ\"\\s*:\\s*(-?[0-9.]+)");          // parity: combat height offset, units (− submerges)
                var sda = Regex.Matches(text, "\"silenceDonorAudio\"\\s*:\\s*(true|false)"); // parity: suppress the borrowed donor's Wwise sound (idle + combat)
                var svx = Regex.Matches(text, "\"silenceDonorVfx\"\\s*:\\s*(true|false)");   // parity: suppress the donor's MecanimEvent VFX (misplaced muzzle flashes)
                var udc = Regex.Matches(text, "\"useDonorClip\"\\s*:\\s*(true|false)");   // parity: donor clip drives the unit
                var rsb = Regex.Matches(text, "\"rotorSpinBones\"\\s*:\\s*\"([^\"]*)\"");  // parity: reclaimed rotor bones
                var rss = Regex.Matches(text, "\"rotorSpinSpeed\"\\s*:\\s*(-?[0-9.]+)");   // parity: rotor spin deg/s
                var esa = Regex.Matches(text, "\"engineStartEvent\"\\s*:\\s*\"([^\"]*)\"");  // parity: Wwise event name posted on move-start
                var eso = Regex.Matches(text, "\"engineStopEvent\"\\s*:\\s*\"([^\"]*)\"");    // parity: Wwise event name posted on move-stop
                var sf = Regex.Matches(text, "\"soundFile\"\\s*:\\s*\"([^\"]*)\"");           // parity: custom WAV loop in haf_sounds/
                var sfa = Regex.Matches(text, "\"soundStartFile\"\\s*:\\s*\"([^\"]*)\"");     // parity: custom WAV one-shot on move-start
                var sfo = Regex.Matches(text, "\"soundStopFile\"\\s*:\\s*\"([^\"]*)\"");      // parity: custom WAV one-shot on move-stop
                var svl = Regex.Matches(text, "\"soundVolume\"\\s*:\\s*(-?[\\d.eE+]+)");       // parity: travel-loop volume
                var svs = Regex.Matches(text, "\"soundStartVolume\"\\s*:\\s*(-?[\\d.eE+]+)");  // parity: start one-shot volume
                var svp = Regex.Matches(text, "\"soundStopVolume\"\\s*:\\s*(-?[\\d.eE+]+)");   // parity: stop one-shot volume
                var sif = Regex.Matches(text, "\"soundIdleFile\"\\s*:\\s*\"([^\"]*)\"");      // parity: custom WAV growl played occasionally while idle
                var siv = Regex.Matches(text, "\"soundIdleVolume\"\\s*:\\s*(-?[\\d.eE+]+)");   // parity: idle-growl one-shot volume
                var sii = Regex.Matches(text, "\"soundIdleInterval\"\\s*:\\s*(-?[\\d.eE+]+)"); // parity: avg seconds between idle growls
                var sig = Regex.Matches(text, "\"soundIdleGroupRadius\"\\s*:\\s*(-?[\\d.eE+]+)"); // parity: group de-dup radius for idle growls
                var saf = Regex.Matches(text, "\"soundAttackFile\"\\s*:\\s*\"([^\"]*)\"");        // parity: custom WAV one-shot on attack
                var sav = Regex.Matches(text, "\"soundAttackVolume\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: attack one-shot volume
                var sao = Regex.Matches(text, "\"soundAttackOffset\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: attack one-shot start offset (s into the WAV)
                var sdf = Regex.Matches(text, "\"soundDeathFile\"\\s*:\\s*\"([^\"]*)\"");         // parity: custom WAV one-shot on death
                var sdv = Regex.Matches(text, "\"soundDeathVolume\"\\s*:\\s*(-?[\\d.eE+]+)");     // parity: death one-shot volume
                var sdo = Regex.Matches(text, "\"soundDeathOffset\"\\s*:\\s*(-?[\\d.eE+]+)");     // parity: death one-shot start offset
                var sbf = Regex.Matches(text, "\"soundBattleFile\"\\s*:\\s*\"([^\"]*)\"");        // parity: custom WAV war cry on battle start
                var sbv = Regex.Matches(text, "\"soundBattleVolume\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: war-cry volume
                var sbo = Regex.Matches(text, "\"soundBattleOffset\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: war-cry start offset
                var dpt = Regex.Matches(text, "\"deployPoseTime\"\\s*:\\s*(-?[\\d.eE+]+)"); // parity: normalized clip time of the deployed pose (default 1)
                var arp = Regex.Matches(text, "\"attackRepeats\"\\s*:\\s*(-?[\\d.eE+]+)");  // parity: STATE-DRIVEN attack clip replay count per trigger (default 1)
                var dsp = Regex.Matches(text, "\"deploySpeed\"\\s*:\\s*(-?[\\d.eE+]+)");    // parity: gradual-deploy ramp speed multiplier (default 1)
                var rsp = Regex.Matches(text, "\"recoilSpeed\"\\s*:\\s*(-?[\\d.eE+]+)");   // parity: recoil-on-fire playback speed multiplier (default 1)
                var sc = Regex.Matches(text, "\"scale\"\\s*:\\s*(-?[\\d.eE+]+)");           // runtime ObjectSpace scale multiplier (default 1)
                var bri = Regex.Matches(text, "\"brightness\"\\s*:\\s*(-?[\\d.eE+]+)");     // universal skin brightness gamma (default 1 = unchanged)
                var des = Regex.Matches(text, "\"desaturate\"\\s*:\\s*(-?[\\d.eE+]+)");     // texture-only grey strength (default 0 = off)
                var tR = Regex.Matches(text, "\"tintR\"\\s*:\\s*(-?[\\d.eE+]+)");           // universal skin colour offset R (-255..255)
                var tG = Regex.Matches(text, "\"tintG\"\\s*:\\s*(-?[\\d.eE+]+)");           // ... G
                var tB = Regex.Matches(text, "\"tintB\"\\s*:\\s*(-?[\\d.eE+]+)");           // ... B
                var txf = Regex.Matches(text, "\"textureFile\"\\s*:\\s*\"([^\"]*)\"");      // texture-only retexture: PNG filename in haf_skins/
                var hpn = Regex.Matches(text, "\"handPropName\"\\s*:\\s*\"([^\"]*)\"");     // parity: hand-prop resource name
                var hpg = Regex.Matches(text, "\"handPropGuid\"\\s*:\\s*\"([^\"]*)\"");     // parity: hand-prop collection guid csv
                var hpm = Regex.Matches(text, "\"handPropMat\"\\s*:\\s*\"([^\"]*)\"");      // parity: hand-prop borrowed material guid csv
                var hpb = Regex.Matches(text, "\"handPropBone\"\\s*:\\s*\"([^\"]*)\"");     // parity: hand-prop bone-name substring
                var tbn = Regex.Matches(text, "\"turretBone\"\\s*:\\s*\"([^\"]*)\"");       // parity: turret aim bone-name substring
                var tax = Regex.Matches(text, "\"turretAxis\"\\s*:\\s*(-?\\d+)");           // parity: turret aim-axis override
                var mzb = Regex.Matches(text, "\"muzzleBone\"\\s*:\\s*\"([^\"]*)\"");       // parity: muzzle-flash bone-name substring
                var mzo = Regex.Matches(text, "\"muzzleOffset\"\\s*:\\s*\"([^\"]*)\"");     // parity: world-units offset dial on the pinned fire origin
                var hpa = Regex.Matches(text, "\"handPropAngles\"\\s*:\\s*\"([^\"]*)\"");   // parity: hand-prop draw-time import angles csv
                int G(Match m, int g) => int.TryParse(m.Groups[g].Value, out var r) ? r : 0;
                float F(Match m, int g) => float.TryParse(m.Groups[g].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0f;
                // clip-role GUID quads (one JSON array each) land in the ROLE TABLE after construction — the same nine keys the
                // Newtonsoft path hand-extracts (parity: check_schema_parity.sh greps the Regex.Matches keys above).
                void Quad(ModelEntry me, ClipRole r, MatchCollection mc, int idx) { if (idx < mc.Count) me.Role(r).Set(G(mc[idx], 1), G(mc[idx], 2), G(mc[idx], 3), G(mc[idx], 4)); }
                int n = Math.Min(pd.Count, Math.Min(sk.Count, at.Count));
                for (int i = 0; i < n; i++)
                {
                    entries.Add(new ModelEntry
                    {
                        resourceName = i < rn.Count ? rn[i].Groups[1].Value : ("model" + i),
                        pawnDescription = pd[i].Groups[1].Value,
                        hideMeshes = i < hm.Count ? hm[i].Groups[1].Value : "",   // hideMeshes appears once per model in doc order, same as the others
                        sa = G(sk[i], 1), sb = G(sk[i], 2), sc = G(sk[i], 3), sd = G(sk[i], 4),
                        ta = G(at[i], 1), tb = G(at[i], 2), tc = G(at[i], 3), td = G(at[i], 4),
                        animStateDriven = i < asd.Count && asd[i].Groups[1].Value == "true",
                        idleAltInterval = i < iai.Count && float.TryParse(iai[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _iai) ? _iai : 0f,
                        animPhaseSpread = i < aps.Count && float.TryParse(aps[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _aps) ? _aps : 0.5f,
                        position = i < po.Count ? new UnityEngine.Vector3(F(po[i], 1), F(po[i], 2), F(po[i], 3)) : UnityEngine.Vector3.zero,
                        respawnAfterLoad = i < ra.Count && ra[i].Groups[1].Value == "true",
                        freezeDonorAnim = i < fz.Count && fz[i].Groups[1].Value == "true",
                        disabled = i < dsb.Count && dsb[i].Groups[1].Value == "true",
                        clearAimLayer = i < cal.Count && cal[i].Groups[1].Value == "true",
                        fireOnAttack = i < foa.Count && foa[i].Groups[1].Value == "true",
                        deployOnStop = i < dos.Count && dos[i].Groups[1].Value == "true",
                        engineSound = i < eng.Count && eng[i].Groups[1].Value == "true",
                        hideSubPawns = i < hsp.Count && hsp[i].Groups[1].Value == "true",
                        moveTilt = i < mtl.Count && float.TryParse(mtl[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mtv) ? mtv : 0f,
                        turnRate = i < trt.Count && float.TryParse(trt[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var trv) ? trv : 0f,
                        turnBank = i < tbk.Count && float.TryParse(tbk[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tbv) ? tbv : 0f,
                        gunElevMax = i < gem.Count && float.TryParse(gem[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gev) ? gev : 0f,
                        gunElevAxis = i < gea.Count && int.TryParse(gea[i].Groups[1].Value, out var gav) ? gav : 0,
                        hugDrop = i < hgd.Count && float.TryParse(hgd[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hdv) ? hdv : 0f,
                        hugLookahead = i < hgl.Count && float.TryParse(hgl[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hlv) ? hlv : 1.5f,
                        combatZ = i < cbz.Count && float.TryParse(cbz[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cbv) ? cbv : 0f,
                        silenceDonorAudio = i < sda.Count && sda[i].Groups[1].Value == "true",
                        silenceDonorVfx = i < svx.Count && svx[i].Groups[1].Value == "true",
                        useDonorClip = i < udc.Count && udc[i].Groups[1].Value == "true",
                        rotorSpinBones = i < rsb.Count ? rsb[i].Groups[1].Value : "",
                        rotorSpinSpeed = i < rss.Count && float.TryParse(rss[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rsv) ? rsv : 720f,
                        engineStartEvent = i < esa.Count ? esa[i].Groups[1].Value : "",
                        engineStopEvent = i < eso.Count ? eso[i].Groups[1].Value : "",
                        soundFile = i < sf.Count ? sf[i].Groups[1].Value : "",
                        soundStartFile = i < sfa.Count ? sfa[i].Groups[1].Value : "",
                        soundStopFile = i < sfo.Count ? sfo[i].Groups[1].Value : "",
                        soundVolume = i < svl.Count && float.TryParse(svl[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _svl) ? _svl : 1f,
                        soundStartVolume = i < svs.Count && float.TryParse(svs[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _svs) ? _svs : 1f,
                        soundStopVolume = i < svp.Count && float.TryParse(svp[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _svp) ? _svp : 1f,
                        soundIdleFile = i < sif.Count ? sif[i].Groups[1].Value : "",
                        soundIdleVolume = i < siv.Count && float.TryParse(siv[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _siv) ? _siv : 1f,
                        soundIdleInterval = i < sii.Count && float.TryParse(sii[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sii) ? _sii : 11f,
                        soundIdleGroupRadius = i < sig.Count && float.TryParse(sig[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sig) ? _sig : 10f,
                        soundAttackFile = i < saf.Count ? saf[i].Groups[1].Value : "",
                        soundAttackVolume = i < sav.Count && float.TryParse(sav[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sav) ? _sav : 1f,
                        soundAttackOffset = i < sao.Count && float.TryParse(sao[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sao) ? _sao : 0f,
                        soundDeathFile = i < sdf.Count ? sdf[i].Groups[1].Value : "",
                        soundDeathVolume = i < sdv.Count && float.TryParse(sdv[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sdv) ? _sdv : 1f,
                        soundDeathOffset = i < sdo.Count && float.TryParse(sdo[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sdo) ? _sdo : 0f,
                        soundBattleFile = i < sbf.Count ? sbf[i].Groups[1].Value : "",
                        soundBattleVolume = i < sbv.Count && float.TryParse(sbv[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sbv) ? _sbv : 1f,
                        soundBattleOffset = i < sbo.Count && float.TryParse(sbo[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sbo) ? _sbo : 0f,
                        deployPoseTime = i < dpt.Count && float.TryParse(dpt[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _dpt) ? _dpt : 1f,
                        attackRepeats = i < arp.Count && int.TryParse(arp[i].Groups[1].Value, out var _arp) ? _arp : 1,
                        deploySpeed = i < dsp.Count && float.TryParse(dsp[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _dsp) ? _dsp : 1f,
                        recoilSpeed = i < rsp.Count && float.TryParse(rsp[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _rsp) ? _rsp : 1f,
                        scale = i < sc.Count && float.TryParse(sc[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _sc) ? _sc : 1f,
                        brightness = i < bri.Count && float.TryParse(bri[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _bri) ? _bri : 1f,
                        desaturate = i < des.Count && float.TryParse(des[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _des) ? _des : 0f,
                        tintR = i < tR.Count && float.TryParse(tR[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _tr) ? _tr : 0f,
                        tintG = i < tG.Count && float.TryParse(tG[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _tg) ? _tg : 0f,
                        tintB = i < tB.Count && float.TryParse(tB[i].Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _tb) ? _tb : 0f,
                        textureFile = i < txf.Count ? txf[i].Groups[1].Value : "",
                        handPropName = i < hpn.Count ? hpn[i].Groups[1].Value : "",
                        handPropGuid = i < hpg.Count ? hpg[i].Groups[1].Value : "",
                        handPropMat = i < hpm.Count ? hpm[i].Groups[1].Value : "",
                        handPropBone = i < hpb.Count ? hpb[i].Groups[1].Value : "",
                        turretBone = i < tbn.Count ? tbn[i].Groups[1].Value : "",
                        turretAxis = i < tax.Count && int.TryParse(tax[i].Groups[1].Value, out var _tax) ? _tax : -1,
                        muzzleBone = i < mzb.Count ? mzb[i].Groups[1].Value : "",
                        muzzleOffset = i < mzo.Count ? mzo[i].Groups[1].Value : "",
                        handPropAngles = i < hpa.Count ? hpa[i].Groups[1].Value : "",
                    });
                    var ne = entries[entries.Count - 1];
                    Quad(ne, ClipRole.Primary, cl, i);        Quad(ne, ClipRole.Move, cmvR, i);     Quad(ne, ClipRole.After, cfaR, i);
                    Quad(ne, ClipRole.Attack, catR, i);       Quad(ne, ClipRole.Combat, ccbR, i);   Quad(ne, ClipRole.PreMove, cpvR, i);
                    Quad(ne, ClipRole.IdleOverride, cidR, i); Quad(ne, ClipRole.IdleAlt, calR, i);  Quad(ne, ClipRole.IdleAlt2, ca2R, i);
                }
                Plugin.Log.LogInfo($"[Uni] read {text.Length} chars; parsed {entries.Count} model(s) via regex [" + string.Join(", ", entries.Select(e => e.resourceName + "->" + e.pawnDescription)) + "]");
                return entries;
        }

        // FIRING-ON-ATTACK: match a firing unit's UnitDefinition text (e.g. "LandUnit_Era6_Common_TowedGunHowitzers …")
        // to one of our injected models by the shared core of its pawnDescription
        // ("Era6_Common_TowedGunHowitzers_01" -> core "Era6_Common_TowedGunHowitzers"). Returns the entry, or null.
        // Match a game NAME to the MOST SPECIFIC entry — the LONGEST `key` that is a substring of `name`, NOT the first
        // in registry order. So a variant entry (pawnDescription "..._Elite") wins over the base it extends, instead of
        // whichever sorts first silently claiming the specialised unit and binding the wrong model. Warns ONCE per name
        // when >1 entry matches, so a nested/ambiguous pawnDescription surfaces in the log instead of mis-binding in
        // silence. Log guard is locked (called from the main pawn hook AND the sim-thread combat hook). Result is
        // IDENTICAL to the old FirstOrDefault when only one entry matches (the common case). Non-capturing key lambdas
        // are cached by the compiler, so no per-call allocation.
        static readonly HashSet<string> _ambigLogged = new HashSet<string>();
        internal static ModelEntry LongestMatch(List<ModelEntry> list, string name, Func<ModelEntry, string> key)
        {
            if (list == null || string.IsNullOrEmpty(name)) return null;
            ModelEntry best = null; int bestLen = -1, count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var k = key(list[i]);
                if (string.IsNullOrEmpty(k) || name.IndexOf(k, StringComparison.OrdinalIgnoreCase) < 0) continue;
                count++;
                if (k.Length > bestLen) { bestLen = k.Length; best = list[i]; }
            }
            if (count > 1 && best != null)
                lock (_ambigLogged)
                    if (_ambigLogged.Add(name))
                        Plugin.Log.LogWarning($"[Uni] '{name}' matched {count} registry entries by substring — using the most specific ('{key(best)}'). Make nested pawnDescriptions distinct to avoid mis-binding.");
            return best;
        }

        // THE ONE unit→entry matcher. Every per-unit path (repoint, combat/sound, and the movement/deploy/state polls)
        // resolves a unit to its entry through longest-match here, so they never disagree about which entry drives a unit.
        //  1) FULL pawnDescription first (same key the repoint path uses, Inject.cs LongestMatch on pawnDescription): this
        //     distinguishes entries that differ ONLY in the trailing _NN (e.g. Foo_01 vs Foo_02 = two distinct models) —
        //     coreDesc strips _NN, so on its own it collapses them and the winner was registry-order-arbitrary.
        //  2) coreDesc fallback (pawnDescription minus _NN; >4 floor keeps a too-short key from matching everything): a
        //     single entry still covers a unit whose runtime definition name lacks the suffix — the historical behavior.
        // Strictly more precise than the old coreDesc-only match with a safe fallback, so it never regresses a working bind.
        internal static ModelEntry FindEntryForUnitDefinition(string unitDefText)
        {
            // Snapshot the reference: this runs on the SIM thread (combat hook) while the main thread may republish
            // `entries` (LoadRegistry retry). The published list is never mutated after publish, so iterating a
            // snapshot is safe; iterating the field directly was not (review 2026-07-19).
            var snap = entries;
            return LongestMatch(snap, unitDefText, x => x.pawnDescription)
                ?? LongestMatch(snap, unitDefText, x => x.coreDesc.Length > 4 ? x.coreDesc : "");
        }

        // SESSION RE-ARM (review 2026-07-19): AnimationLoad fires per game-session and the manager's collection list is
        // rebuilt — the props axis re-arms on every fire (proven in-game), but the core model axis latched `registered`
        // once per PROCESS: load a second game in the same app run and EnsureRegistered no-ops, leaving our skeletons
        // unregistered and every learned id (skeletonId/animId/descId) stale against the new session's assignments.
        // Called from the AnimationLoad postfix, right before EnsureRegistered re-registers into the fresh manager.
        // A new game session (new game OR save-load) rebuilds the AnimationManager, but AnimationLoad — where the
        // eager re-arm hangs — fires only ONCE PER PROCESS (proven 2026-08-16 by [SessionProbe]). So the re-arm is
        // also requested from the two seams that DO fire per session: Sandbox.Load (save-load only) and
        // PawnManager.Load (EVERY session, incl. a New Game — the seam that closes the new-game gap). Thread-safe
        // flag only; the heavy work (RearmModelRegistration destroys session-1 Unity clones) runs on the main-thread
        // Update tick in ConsumePendingReloadRearm. Multiple triggers per load coalesce into one consume.
        internal static void RequestReloadRearm() => reloadRearmPending = true;

        // Sandbox.Load (save-load): request the district reset. It MUST land before the district presentation hooks
        // bind during the world rebuild, or they bind onto corpse leaves (the Oracle incident) — so the hooks consume
        // the flag themselves on entry (Hooks.cs), and Update consumes it otherwise. It used to run INLINE here,
        // justified as "pure reference-nulling (thread-safe)"; it is not — ResetDistrictSessionState Clear()s
        // trackedDistricts / loadedSelectorByKey / scopedStates / the wonder-template caches, all read (and written)
        // by the main thread's per-frame polls, and this hook may be off the main thread. Clearing a Dictionary
        // under a concurrent TryGetValue is a corruption race, once per save-load. Then request the model re-arm,
        // flagging that districts are handled so the deferred consume won't churn a second reset. A NEW GAME does
        // NOT reach this (no save deserialize) — there the deferred consume resets districts itself.
        internal static void RequestSaveLoadRearm()
        {
            districtResetPending = true;
            districtResetSync = true;
            reloadRearmPending = true;
        }

        // Main thread ONLY (Plugin.Update, and the entry of every district presentation hook). Runs the district reset
        // Sandbox.Load asked for. Idempotent and cheap when nothing is pending — the hooks call it per district build.
        internal static void ConsumePendingDistrictReset()
        {
            if (!districtResetPending) return;
            districtResetPending = false;
            try { DistrictInject.ResetDistrictSessionState(); }
            catch (Exception ex) { Plugin.Log.LogError("[District] deferred session reset: " + ex); }
        }

        // Main thread (Plugin.Update). Runs the deferred full re-arm requested by any per-session seam above.
        internal static void ConsumePendingReloadRearm()
        {
            ConsumePendingDistrictReset();   // before the early return: the district reset must not wait on the model re-arm
            if (!reloadRearmPending) return;
            reloadRearmPending = false;
            bool distDone = districtResetSync; districtResetSync = false;   // Sandbox.Load already reset districts (save-load)? then skip the redundant reset; a New Game needs it done here.
            try { RearmModelRegistration(resetDistricts: !distDone); }
            catch (Exception ex) { Plugin.Log.LogError("[Uni] deferred load re-arm: " + ex); }
        }

        internal static void RearmModelRegistration(bool resetDistricts = true)
        {
            MarkSubPawnsDirtyAndReverify();   // session-1 sub-pawn components are corpses; the shared scan must refresh (and re-verify the walk once)
            _unitEntryCache.Clear();   // unit->entry cache (ProcessAnimStates) keys session-1 PresentationUnits
            registered = false;
            anyAnimated = null; anyMuzzle = null; anyFreeze = null; anyRescuable = null;                    // recomputed on the next pawn-add
            unitScaleByDesc.Clear(); unitScaleNameByDesc.Clear(); vanillaScaledLogged.Clear(); descApplied.Clear(); cachedEra = -1;   // descriptor ids + era are session-scoped (meshApplied deliberately KEPT: the Fx vertex buffers persist)
            vanillaTurnByDesc.Clear(); vanillaEaseLogged.Clear(); addonDefIds.Clear(); descCensusLogged.Clear();   // vanilla turn-ease links re-resolve to fresh descriptor ids next session
            vanillaCatByDesc.Clear(); descTurret.Clear(); descHover.Clear(); classSamples.Clear();   // category + hover/turret classifications are descriptor-id keyed -> session-scoped too
            _listenerChecked = false;                                // the AudioListener rode a session-scoped camera
            // DISTRICT runtime state is session-scoped too (the Oracle incident) — reset ONCE, at the end of this method
            // (the canonical call below). Until 2026-08-21 it was also called here, so every re-arm reset districts twice.
            var list = entries;
            if (list != null)
                foreach (var e in list)
                {
                    e.skeletonId = -1; e.animId = -1; e.descId = -1; e.repointed = false;   // session-scoped ids re-learn
                    foreach (var b in e.Roles) b.animId = -1;                                   // every clip role's id re-resolves (the table, not a hand-list)
                    e.idleAltNextAt = 0f; e.idleAltStart = -1f; e.idleAltChosenId = -1;   // idle-alt cadence is session-scoped (Time.time resets)
                    e.stateLastPos.Clear(); e.stateMoving.Clear(); e.stateStoppedAt.Clear(); e.stateMoveStartedAt.Clear();
                    e.stateCombat.Clear(); e.stateCombatChangedAt.Clear();
                    lock (e.stateSamples) e.stateSamples.Clear();
                    lock (e.activeFires) e.activeFires.Clear();                              // session-1 fire windows (positions + Time.time) are meaningless in the new session
                    // Retexture/isolation state is session-scoped too: the isolated layer is a clone of a SESSION-1
                    // output layer and the adjusted atlas was dumped from it — handing either to the new session's
                    // content manager re-injects dead objects. Cheap to re-derive; destroy the texture we created.
                    // DESTROY the Instantiate'd layer CLONES we made (isolatedLayer/handPropLayer): they're runtime clones
                    // the game doesn't track, so nulling alone leaked a native layer object per retexture/prop entry per
                    // reload. The session-1 objects they were cloned from + injected into are torn down by now, so nothing
                    // live references them (the `&& iso` fake-null guard also covers the case teardown already got them).
                    // hostOutputLayer may alias isolatedLayer (same clone, destroyed once); propAtlasTex is a game-owned
                    // bundle asset — only null those two.
                    if (e.isolatedLayer is UnityEngine.Object iso && iso) UnityEngine.Object.Destroy(iso);
                    if (e.handPropLayer is UnityEngine.Object hpl && hpl) UnityEngine.Object.Destroy(hpl);
                    e.isolatedLayer = null; e.hostOutputLayer = null; e.handPropLayer = null; e.propAtlasTex = null;
                    // ONLY destroy a texture WE created (PNG skin / adjusted atlas). A raw bundle atlas (LoadAtlas)
                    // is a shared game asset — Destroying it makes AssetDatabase.LoadAsset return NULL on the next
                    // save-reload, so the model loses its skin and falls back to the donor look (organ gun turned red).
                    if (e.tex != null) { if (e.texOwned) { try { UnityEngine.Object.Destroy(e.tex); } catch { } } e.tex = null; e.texOwned = false; }
                    // Per-instance state keyed by session-scoped ids (unit GUIDs / sub-pawn instance ids): a new game
                    // can REUSE those ids, so stale entries would feed the first poll wrong moving/deploy decisions,
                    // and the maps otherwise only ever grow. The AudioSources rode session-1 pawn objects (destroyed
                    // with them) — dropping the references is enough.
                    e.deployProgress.Clear(); e.deployLastPos.Clear();
                    e.customSources.Clear(); e.loopHoldUntil.Clear(); e.engineLastPos.Clear(); e.engineMoving.Clear(); e.engineEmitterGuids.Clear(); e.engineLoudSince.Clear(); e.enginePlayingIds.Clear();
                    e.idleNextAt.Clear(); e.attackSoundNextAt.Clear();   // were UNBOUNDED across reloads (never cleared) — session-scoped sub-pawn ids / attacker hashcodes
                }
            deployMoveState = null;                                  // diagnostic map, unit GUIDs are session-scoped
            respawnBase.Clear(); respawnCount.Clear();               // keyed by session-1 unit objects
            knownManagers.Clear();                                   // dead session's pawn managers (the stray-slot sweep re-learns live ones)
            _silencedEmitterIds.Clear();                             // static: grew per silenced AudioEmitter ever seen, never reset — session-scoped instance ids
            // DISTRICT axis session state (same bug class): the FxManager and each entry's tiles/private clone were
            // captured from session-1 presentation objects — reusing them in a second game points at torn-down GPU
            // state. ONE canonical reset (a hand-rolled copy here had already drifted: it missed the texture bindings
            // and cached bind slots) — DistrictApplyEntries re-derives per district instance as the new session loads.
            if (resetDistricts) DistrictInject.ResetDistrictSessionState();
        }

    }
}
