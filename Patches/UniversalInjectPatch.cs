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
    // Generic, registry-driven model injector — the runtime half of the Universal Baker. Reads enc_models.json from
    // BepInEx/config (written by the editor), registers every baked skeleton, and on each unit's AddOn.Load repoints
    // the matching pawn definition onto its skeleton using the proven self-discovery (read the host's body mesh name,
    // rename ours to match, resolve, skin via <bodyMesh>_OutputLayer). One patch handles any number of models.
    // One pawn's published movement state (STATE-DRIVEN models, Phase 2): written by the main-thread poll
    // (ProcessAnimStates), read by the per-frame pose hook via nearest-position match (the deploy poll's approximation).
    internal struct StateSample { public UnityEngine.Vector3 pos; public bool moving; public float stoppedAt; public float moveStartedAt; public bool combat; }

    internal class ModelEntry
    {
        public string resourceName = "", pawnDescription = "";
        public string coreDesc = "";     // pawnDescription minus the trailing _NN instance suffix, computed ONCE at registry publish. The per-frame movement polls + the sim-thread FindEntryForUnitDefinition matched units by re-running Regex.Replace(pawnDescription,"_[0-9]+$","") per entry per unit — pure garbage since it's a load-time constant. Read-only after publish (safe from any thread).
        public int sa, sb, sc, sd, ta, tb, tc, td;   // skeleton + atlas Amplitude guid components
        public object skeleton;
        public object hostOutputLayer;
        public UnityEngine.Texture2D tex;
        public string layerHint = "";
        public object isolatedLayer;     // our private clone of the host output layer (texture isolation)
        public string hideMeshes = "";   // comma-separated donor-FRAGMENT name substrings to hide (works for fragment-based extras; a donor's animated skinned sub-parts, e.g. a helicopter rotor, are encoded at pawn-spawn and cannot be hidden this late — pick a rotor-free donor instead)
        public UnityEngine.Vector3 position;  // ANIMATED models: applied as a runtime world offset in the pose hook (z = height/up). Static models bake position into the mesh at Bake time instead, so this is only read for animated entries.
        public float scale = 1f;              // ANIMATED models: runtime multiplier on the pawn's ObjectSpace.Scale (default 1 = unchanged). Lets us fix an animated model baked at the wrong scale WITHOUT a re-bake (e.g. the howitzer's 100x FBX unit-conversion oversize -> set 0.01). Config-only field; absent = 1.
        public float desaturate = 0f;         // TEXTURE-ONLY GREY variant: 0 = off. >0 = DON'T repoint the mesh; isolate this unit's output layer and paint a DESATURATED copy of its OWN atlas (1 = full grey) while the civ-colour tint is neutralised. Makes a Common copy read as a bland grey version of an emblematic unit; the original is untouched (they share the layer, so the isolation clone is essential). No bake / no custom model needed.
        public float brightness = 1f;         // UNIVERSAL skin brightness GAMMA (1 = unchanged, >1 lighter, <1 darker). Applied FIRST (before desaturate/tint) to whatever skin the unit gets. Multiplicative in the dark range, so a near-black atlas actually lightens — the additive tint tops out (+30 lifts 18 to only 48; gamma 1.5 lifts it ~2.4x). Managed by the Unit Retexture window.
        public string textureFile = "";       // TEXTURE-ONLY RETEXTURE: a PNG filename in BepInEx/config/enc_skins/. When set, the plugin loads that PNG and paints it onto the unit's ISOLATED output layer (same isolation as desaturate — original untouched, vanilla mesh kept). Hot-loaded at runtime, no bake/rebuild. Takes precedence over desaturate. Painted on a dump of the unit's own atlas (round-trips via PNG). Managed by the Unit Retexture editor window.
        public float tintR = 0f;              // UNIVERSAL skin colour offset, red channel (-255..+255, 0 = none). Added AFTER desaturate to whatever skin this unit ends up with — the loaded textureFile PNG, OR a copy of its own atlas. Equal negative R/G/B = darken; equal positive = brighten; one channel tints.
        public float tintG = 0f;              // ... green channel (-255..+255).
        public float tintB = 0f;              // ... blue channel (-255..+255).
        public int ca, cb, cc, cd;       // ANIMATED models: our baked ClipCollection Amplitude guid (its own clip, e.g. a drone's spinning-prop 'hover'). 0,0,0,0 = static model (no pose override).
        public object clipColl;          // loaded ClipCollection asset
        public int animId = -1;          // resolved animation id of our clip (after it's registered in AnimationManager.Apply)
        // STATE-DRIVEN (Phase 2, 2026-07-19): idle = the primary clip above; MOVE plays while the unit travels;
        // optional AFTER plays once on stopping. Each role is its own baked ClipCollection sharing the one skeleton.
        public bool animStateDriven;
        public int mca, mcb, mcc, mcd;   // MOVEMENT ClipCollection Amplitude guid
        public int aca, acb, acc, acd;   // AFTER-MOVEMENT ClipCollection Amplitude guid (0,0,0,0 = none)
        public int ata, atb, atc, atd;   // ATTACK ClipCollection Amplitude guid (0,0,0,0 = none) — played once when the pawn ranged-attacks
        public int cba, cbb, cbc, cbd;   // COMBAT-IDLE ClipCollection Amplitude guid (0,0,0,0 = none) — replaces IDLE while the army is locked in a battle
        public int pva, pvb, pvc, pvd;   // PRE-MOVEMENT ClipCollection Amplitude guid (0,0,0,0 = none) — played ONCE when the unit STARTS moving (e.g. a howitzer folding), then the Movement loop
        public int iea, ieb, iec, ied;   // IDLE-OVERRIDE ClipCollection Amplitude guid (0,0,0,0 = none) — a STANCE baked as a ROLE (real deltas vs the full primary clip's reference pose); the stance-as-PRIMARY trap encodes ~identity and renders as REST (the howitzer's "forgot to deploy")
        public int ala, alb, alc, ald;   // IDLE-ALT ClipCollection Amplitude guid (0,0,0,0 = none) — an OCCASIONAL flavor one-shot while plain-idle (the tiger's howl), played on the jittered idleAltInterval cadence; one pawn per unit type per firing
        public int a2a, a2b, a2c, a2d;   // IDLE-ALT 2 ClipCollection Amplitude guid (0,0,0,0 = none) — optional second flavor clip (eat/groom); each firing picks randomly between the two
        public object moveClipColl, afterClipColl, attackClipColl, combatClipColl, preMoveClipColl, idleClipColl, idleAltClipColl, idleAlt2ClipColl;
        public int moveAnimId = -1, afterAnimId = -1, attackAnimId = -1, combatAnimId = -1, preMoveAnimId = -1, idleAnimId = -1, idleAltAnimId = -1, idleAlt2AnimId = -1;
        public float moveDur = 1f, afterDur = 1f, attackDur = 1f, combatDur = 1f, preMoveDur = 1f, idleDur = 1f, idleAltDur = 1f, idleAlt2Dur = 1f;
        public float idleAltInterval = 0f;   // avg SECONDS between idle-alt one-shots (jittered 0.6-1.4x, like the idle growl); <=0 disables even when clips are baked
        public float animPhaseSpread = 0.5f; // DEFAULT 0.5 (2026-07-31): spread this model's pawns over half the clip so a multi-pawn unit stops moving as ONE BODY — twelve canoes rocking as a rigid raft, eight monsters swinging their heads in unison. 1 = the whole clip, 0 = lockstep (the old behaviour). Applies to LOOPING poses only; one-shots stay tied to their trigger. This default also governs registries written before the field existed, so every animated model gains the desync without an edit.
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
        public int attackRepeats = 1;    // how many times the ATTACK clip replays per trigger (the fire window = repeats x clip duration; the GPU sampler's Repeat(Time,1) wraps each pass). For short recoil-pop source clips (shootAR2s = 0.17s) that should read as sustained fire. Runtime-only knob — no re-bake.
        // HAND PROP (weapon axis, 2026-07-19): a rigid Prop-Lab mesh glued to a bone of OUR skeleton — the soldier's
        // gun. The donor (an APC) has no weapon slots, so the plugin CONSTRUCTS the FragmentEntry itself at repoint
        // time instead of riding the vanilla slot path. All four are runtime-only registry strings.
        public string handPropName = "";  // the Prop Lab resource name (assets <name>_Collection / mesh <name>_DistrictMesh)
        public string handPropGuid = "";  // the <name>_Collection Amplitude guid "a,b,c,d" (Prop Lab prints + clipboards it)
        public string handPropMat = "";   // borrowed material guid "a,b,c,d"; "" = the shared EQ_DLC04_Weapons material
        public string handPropBone = "";  // bone-name SUBSTRING on OUR skeleton (bones are renamed b###_<orig>); "" = "R_Hand"
        public string handPropAngles = "";// draw-time rotation "x,y,z" (deg) stamped onto the FxMesh asset BEFORE encoding; "" stamps ZERO (neutralizes the engine's -90X class default — baked angle values don't survive the bundle). Hand-edited escape hatch: change + relaunch, no bake/rebuild.
        public bool disabled;             // DEBUG toggle: skip this override entirely so the ORIGINAL vanilla unit renders (compare against the custom model, observe the donor's own animation). Runtime-only — Save (no bake) + relaunch. The entry is dropped at load, so it doesn't even claim its pawn.
        public bool silenceDonorVfx;      // suppress the donor's MecanimEvent VFX (muzzle flashes, animator-driven puffs) for THIS unit. The donor's flash anchors are DONOR bone names (ParentNameToLaunchVFXPosition) that don't exist on our replaced skeleton, so inherited flashes render misplaced — this drops them at the StartVFXEvent chokepoint (the audio-silence pattern). Runtime-only.
        public bool vfxSilencedLogged;    // session flag: log the first suppressed event once per entry
        public bool clearAimLayer;        // clear the game's procedural BoneRotation layer for THIS model (artillery: the donor streams aim/wheel junk that twists the rig). Replaces the old blanket fire/deploy rule for STATE-DRIVEN artillery — characters need the layer (facing), a migrated howitzer needs it cleared. Runtime-only.
        public string turretBone = "";    // TURRETIZE (2026-07-24): bone-name SUBSTRING (renamed b###_<orig>) of a turret to aim at the target. The game already streams its aim/heading angle into a BoneRotation slot on an INVALID bone index — we retarget that slot's SkeletonBoneIndex to THIS bone so the engine's own aim yaws our turret. "" = no turret aim. Runtime-only (no re-bake).
        public int turretBoneIdx = -2;    // cached bone index for turretBone (-2 = not resolved yet, -1 = not found). Resolved once from e.skeleton.BoneInfos.
        public int turretAxis = -1;       // aim-axis override for the turret bone: -1 = keep the game's streamed axis (1 = "up" in ITS frame, which on a bone pointing along its own length reads as PITCH); 0/1/2 = force the bone's local X/Y/Z. A vehicle TURRET needs its YAW axis; a mechanized HOWITZER/ARTILLERY barrel needs its PITCH axis — hence per-model.
        public string muzzleBone = "";    // MUZZLE-RELOCATE (2026-07-24): bone-name SUBSTRING (renamed b###_<orig>) that the weapon muzzle-flash should fire FROM. The donor's fire clip names ITS weapon socket (e.g. an AA gun's "Canon") in the FireProjectile mecanim event; that name is absent on our renamed rig, so AlterationFireProjectile falls back to the pawn's ROOT + the donor's socket-local offset -> the flash lands off-side. We hook PresentationSubPawn.GetBoneTRS and, when the requested bone isn't on our skeleton, redirect the lookup to THIS bone (e.g. the turret/gun) so the flash anchors on our unit. "" = leave the vanilla behavior. Runtime-only (no re-bake).
        public string muzzleBoneName;     // cached FULL bone name resolved from muzzleBone (null = not resolved yet, "" = not found on our skeleton).
        public string muzzleOffset = "";  // RUNTIME dial: "x,y,z" WORLD-units added to the pinned fire origin (flash + tracer start). The empirical fix for a rig whose gun-bone head sits at the base (the Ehrhardt): raise the origin without re-baking. "" = none.
        public UnityEngine.Vector3 muzzleOffsetV; public bool muzzleOffsetParsed;   // parsed once per session
        public bool muzzlePinLogged;      // session flag: log the first StartVFXEvent pin once per entry
        public object handPropLayer;      // session-scoped: our PRIVATE clone of the borrowed weapon output layer, painted with the prop's own atlas (<prop>_Atlas)
        public UnityEngine.Texture2D propAtlasTex;   // session-scoped: the prop atlas — repainted EVERY TICK like the unit retexture (the game resets the material; a one-shot paint flip-flopped between sessions)
        public readonly Dictionary<long, UnityEngine.Vector3> stateLastPos = new Dictionary<long, UnityEngine.Vector3>();  // MAIN thread poll: unit GUID -> last render pos
        public readonly Dictionary<long, bool> stateMoving = new Dictionary<long, bool>();                                 // unit GUID -> was moving last poll (detects the moving->stopped flip)
        public readonly Dictionary<long, float> stateStoppedAt = new Dictionary<long, float>();                            // unit GUID -> Time.time the unit stopped moving
        public readonly Dictionary<long, float> stateMoveStartedAt = new Dictionary<long, float>();                       // unit GUID -> Time.time the unit STARTED moving (the PRE-MOVEMENT one-shot window, e.g. the howitzer folding)
        public readonly List<StateSample> stateSamples = new List<StateSample>();   // published for the pose hook (lock on it); pos = pawn render position
        public float animDuration = 1f;  // clip duration (s); PawnEntryPose.Time is NORMALIZED (Mathf.Repeat(Time,1) = one loop), so Time = seconds/duration plays it at real speed with every frame
        public int skeletonId = -1;      // runtime AnimationManager skeleton index of our registered skeleton (to match PawnManager.PawnEntry.SkeletonId)
        public int descId = -1;          // runtime PawnDescriptorId of our unit (learned from the correctly-skinned pawn), to spot the wrong-skeleton twin the game spawns for the same unit
        public bool fragsLogged;         // one-shot: dump the donor's fragment mesh names once, so the modder can find hide targets
        public bool repointed;
        public bool respawnAfterLoad;    // FIX for the save-load first-instance rotor race: when true, the plugin re-runs the game's own PresentationUnit.UpdatePawns (ReleasePawns+InstantiatePawns) on this model's units ~3s after load, so the first instance's borrowed donor rotor is rebuilt correctly. Set ONLY for models that borrow a donor's animated sub-part (e.g. the helicopter's rotor); harmless-but-pointless flicker otherwise, so default off.
        public bool freezeDonorAnim;     // FREEZE the donor's idle/move animation: a STATIC borrowed mesh inherits the donor's pose bob (e.g. a drone donor's hover wiggle looks wrong on a large airship). When true, the pose hook pins every pawn pose's Time to 0 each frame so the donor animation can't advance — the mesh holds rigid while the pawn still glides tile-to-tile. Static models only (animated models drive their own clip).
        public bool fireOnAttack;        // ANIMATED: play the clip ONCE when the unit attacks (ArtilleryStrikeStarted), resting at frame 0 otherwise — instead of the default continuous loop (a drone's spinning prop). Set for a howitzer's barrel-elevation-on-fire. See docs/Firing-On-Attack.md.
        // PER-INSTANCE fire, so only the howitzer that actually bombarded animates (not every howitzer of the type):
        public readonly System.Collections.Concurrent.ConcurrentQueue<long> fireGuidQueue = new System.Collections.Concurrent.ConcurrentQueue<long>();  // SIM thread enqueues the firing unit's SimulationEntityGUID; Plugin.Update (main thread) drains it (no Unity access on the sim thread).
        public readonly List<FireInstance> activeFires = new List<FireInstance>();  // MAIN/render thread only (locked): each firing pawn's render position + start time; the pose hook plays the clip on the pawn nearest an active fire.
        // DEPLOY-ON-STOP (a HELD state, not a one-shot): the clip rests at the DEPLOYED pose by default and snaps to the
        // UNDEPLOYED pose while the unit is moving. Pure function of "is this pawn's unit moving right now" — no state machine,
        // AI/concurrency-safe. Plugin.Update polls PresentationUnit.IsAnyPawnMoving and records the moving pawns' positions.
        public bool deployOnStop;             // hold the deployed pose when idle, undeploy (frame 0) while moving
        public float deployPoseTime = 1f;     // normalized clip time of the DEPLOYED pose (1 = a real deploy clip's end; 0.5 = the barrel-fire clip's raised plateau, used to prove the plumbing without a deploy clip)
        public float deploySpeed = 1f;        // multiplier on the gradual-deploy ramp speed (1 = the clip's authored speed; 2 = twice as fast). Only affects the forward deploy-on-stop; folding on move is always instant.
        public float recoilSpeed = 1f;        // multiplier on the recoil-on-fire (kickback) playback speed (1 = the tail's authored speed; 3 = the kick plays 3x faster). Only affects deployOnStop+fireOnAttack models.
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
        public bool engineSound;
        public bool silenceDonorAudio;         // SUPPRESS all of the borrowed donor's Wwise sound on this unit's pawns (idle growl + combat maul/scratch that ride in on the reused animator/description). Reusable: any unit that inherits an unwanted donor sound can set it. Silences ONLY Wwise (AudioEmitter.PostEvent) — our own custom WAVs (Unity AudioSource) still play, so it composes with soundIdleFile/soundFile.
        public string engineStartEvent = "";  // Wwise event NAME posted on move-START (e.g. Play_UNIT_Vehicles_StealthCorvette_Start). Set => posted BY NAME (works for the FIRST unit, no live capture); empty => fall back to the auto-captured handle.
        public string engineStopEvent = "";   // ... move-STOP (..._Stop). Extract names via the F8 "Dump Sound Catalog"; assign per unit in the registry.
        public string soundFile = "";          // CUSTOM audio, LOOP while moving: a WAV filename in BepInEx/config/enc_sounds/. Unity AudioSource, 3D. For units the game has NO sound for (drones, zeppelins) or a bespoke engine.
        public string soundStartFile = "";     // CUSTOM one-shot on move-START (spool-up): a WAV in enc_sounds/.
        public string soundStopFile = "";      // CUSTOM one-shot on move-STOP (spool-down): a WAV in enc_sounds/.
        public float soundVolume = 1f;         // travel-loop volume
        public float soundStartVolume = 1f;    // move-start one-shot volume
        public float soundStopVolume = 1f;     // move-stop one-shot volume
        public string soundIdleFile = "";      // CUSTOM one-shot growl played OCCASIONALLY WHILE IDLE (not moving): a WAV in enc_sounds/. Replaces a donor's periodic idle vocalization (pair with silenceDonorAudio). Fired on a randomized timer per pawn.
        public float soundIdleVolume = 1f;     // idle-growl one-shot volume
        public float soundIdleInterval = 11f;  // AVERAGE seconds between idle growls (jittered 0.6..1.4x per pawn so a pack doesn't chorus). <=0 disables.
        public float soundIdleGroupRadius = 10f; // GROUP de-dup: growls suppressed within this radius of another recent growl, so a clustered unit (many pawns) snarls with ONE voice per interval instead of all at once. <=0 = per-pawn (no de-dup).
        public string soundAttackFile = "";    // CUSTOM one-shot played ON ATTACK (each swing/shot) — a WAV in enc_sounds/. A DISTINCT, more violent sound than the idle growl; fired from OnPawnAttack with a per-pawn min-gap so rapid multi-swing fights don't machine-gun it.
        public float soundAttackVolume = 1f;   // attack one-shot volume
        public float soundAttackOffset = 0f;   // seconds INTO the attack WAV where playback starts (skip a silent/windup lead-in so the impact lands on the swing); 0 = from the top
        public string soundDeathFile = "";     // CUSTOM one-shot on a pawn's DEATH (PresentationPawn.TriggerDeath) — the rattle/scream that closes the unit's audio arc. Per-entry min-gap so a wiped stack doesn't chorus five at once.
        public float soundDeathVolume = 1f;    // death one-shot volume
        public float soundDeathOffset = 0f;    // seconds into the death WAV (same semantics as the attack offset)
        public string soundBattleFile = "";    // CUSTOM one-shot WAR CRY when a battle STARTS with this unit in it (SimulationEvent_BattleStarted; sim thread -> queued, played camera-anchored on the main thread). One cry per entry per battle.
        public float soundBattleVolume = 1f;   // war-cry volume
        public float soundBattleOffset = 0f;   // seconds into the war-cry WAV
        public UnityEngine.AudioClip customClip, customStartClip, customStopClip, customIdleClip, customAttackClip, customDeathClip, customBattleClip;    // loaded once from the files
        public float deathSoundNextAt, battleCryNextAt;   // per-entry min-gap clocks (a wiped stack / double battle shouldn't chorus)
        public readonly Dictionary<long, float> attackSoundNextAt = new Dictionary<long, float>();   // attacking-pawn id -> earliest Time.time it may play the attack sound again (min-gap)
        public bool customClipTried;                                                 // don't retry a failed load every poll
        public readonly Dictionary<int, float> idleNextAt = new Dictionary<int, float>();   // sub-pawn instance id -> Time.time of its next idle growl (jittered)
        public readonly List<KeyValuePair<UnityEngine.Vector3, float>> idleRecent = new List<KeyValuePair<UnityEngine.Vector3, float>>();  // recent growls (pos, Time.time) for group de-dup — pruned each poll
        public string assetDir = "";     // owning pack's asset root (set at registry load, never parsed from JSON): WAVs/PNGs resolve from <assetDir>/sounds|skins first, then the legacy shared enc_sounds/enc_skins
        public readonly Dictionary<int, UnityEngine.AudioSource> customSources = new Dictionary<int, UnityEngine.AudioSource>();  // sub-pawn instance id -> our looping AudioSource (played while moving)
        public readonly Dictionary<int, float> loopHoldUntil = new Dictionary<int, float>();   // instance id -> Time.time to hold the travel loop off until (so the spool-up one-shot isn't masked)
        public readonly Dictionary<int, UnityEngine.Vector3> engineLastPos = new Dictionary<int, UnityEngine.Vector3>();  // sub-pawn instance id -> last render pos
        public readonly Dictionary<int, bool> engineMoving = new Dictionary<int, bool>();                                  // sub-pawn instance id -> was moving last poll
    }

    // One in-flight one-shot: the world position of a pawn that just fired + when it started. The pose hook matches a
    // pawn to the nearest active fire by ObjectSpace position (both are Unity render coords), so only the firer animates.
    internal struct FireInstance { public UnityEngine.Vector3 pos; public float startTime; public long pawnId; }
    // A pawn's render position + the (ramped) normalized pose time its unit should currently hold, for the gradual deploy.
    internal struct DeploySample { public UnityEngine.Vector3 pos; public float poseTime; }

    internal static partial class UniversalInject
    {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static List<ModelEntry> entries;
        static bool loaded, registered, repointActiveLogged, stLogged, greyWaitLogged;
        static int loadAttempts;   // failed-load counter: latch `loaded` only after a success or a few tries, so a TRANSIENT read/parse error (AV scan, sharing violation at startup) retries instead of disabling injection for the whole session
        static UnityEngine.Texture2D _flatN, _white, _black, _grey;   // neutral overlay maps (kill the host's detail/camo)

        // A discovered mod PACK: one registry file's wrapper metadata + its models. HAF (Humankind Asset Framework)
        // multi-mod support merges many packs into `entries`, so ENC is just one mod among many — any modder ships their
        // own pack (config + assets) to join. The wrapper keys (modId/schemaVersion/dependsOn/loadAfter/overrides) sit
        // BESIDE the existing "models" array, so a legacy bare { "models": [...] } file still parses — with default metadata.
        class Pack
        {
            public string modId = "", file = "";
            public string assetDir = "";              // per-pack ASSET ROOT (2026-07-19): file-based assets (WAVs in sounds/, PNGs in skins/) resolve here FIRST, then fall back to the legacy shared enc_sounds/enc_skins — so a third-party pack ships self-contained instead of feeling like an ENC extension. "" (the base pack) = legacy folders only.
            public int schemaVersion;
            public List<string> dependsOn = new List<string>();
            public List<string> loadAfter = new List<string>();
            public List<PackOverride> overrides = new List<PackOverride>();   // ENFORCED since 2026-07-19: an explicit, declared replacement of another pack's entry
            public List<ModelEntry> models = new List<ModelEntry>();
        }
        // A declared cross-pack replacement: "this pack intentionally replaces <modId>'s entry on <pawnDescription>".
        // Declared = consensual under the HAF conflict philosophy (an UNdeclared clash is still first-loaded-wins, loud).
        class PackOverride { public string modId = "", pawn = ""; }

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
                // DISCOVERY: the ENC base registry (enc_models.json) + every *.json a third-party mod drops in haf_packs/.
                // Each file is a PACK; a joining modder ships their own pack instead of editing ours. The base loads FIRST,
                // so ENC's own models are protected from an accidental clash (first-loaded wins — see the merge below).
                var basePath = Path.Combine(Paths.ConfigPath, "enc_models.json");
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
                if (files.Count == 0) { Plugin.Log.LogInfo("[Uni] no registry at " + basePath + " and no haf_packs/*.json"); loaded = true; return; }

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
                                Plugin.Log.LogInfo($"[Uni] OVERRIDE: pack '{pk.modId}' replaces '{held}' on pawn '{e.pawnDescription}' (declared).");
                                continue;
                            }
                            conflicts.Add($"pawn={e.pawnDescription} kept={held} dropped={pk.modId}({e.resourceName})");
                            Plugin.Log.LogWarning($"[Uni] CONFLICT: pack '{pk.modId}' targets pawn '{e.pawnDescription}' already claimed by '{held}' — keeping '{held}' (first-loaded wins; declare it in `overrides` to replace).");
                            continue;
                        }
                        if (e.disabled) { Plugin.Log.LogInfo($"[Uni] '{e.resourceName}' -> '{e.pawnDescription}': DISABLED in registry — skipping override (original unit rendered)."); continue; }
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
                    Plugin.Log.LogInfo($"[Resize] {unitScaleRules.Count} unit-scale rule(s): " + string.Join(", ", unitScaleRules.Select(r => $"'{r.match}'x{r.scale:0.###}" + (r.era > 0 ? $"@era{r.era}" : ""))));

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
                    Plugin.Log.LogInfo("[Resize] era grid: " + string.Join(" | ", eraGridRows.OrderBy(k => k.Key)
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
                    Plugin.Log.LogInfo("[Resize] formation-by-size: " + string.Join(", ",
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
                // shared folders); the base pack keeps the legacy enc_sounds/enc_skins.
                assetDir = isBase ? "" : isDirPack ? Path.GetDirectoryName(file)
                                                   : Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file)),
            };
            try
            {
                var root = JObject.Parse(text);
                if (root["modId"] != null) pk.modId = (string)root["modId"];
                if (root["schemaVersion"] != null) pk.schemaVersion = (int)root["schemaVersion"];
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
        static List<string> RegexStrArray(string text, string field)
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
        static List<Pack> ResolvePacks(List<Pack> packs, List<string> notes)
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
        static List<ModelEntry> ParseModels(string text)
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
                            var s = m["skel"]; var t = m["atlas"]; var c = m["clip"]; var p = m["position"];
                            var cmv = m["clipMove"]; var cfa = m["clipAfter"]; var cat = m["clipAttack"]; var ccb = m["clipCombat"]; var cpv = m["clipPreMove"]; var cid = m["clipIdle"]; var cAlt = m["clipIdleAlt"]; var ca2 = m["clipIdleAlt2"];
                            entries.Add(new ModelEntry
                            {
                                resourceName = (string)m["resourceName"] ?? "", pawnDescription = (string)m["pawnDescription"] ?? "", hideMeshes = (string)m["hideMeshes"] ?? "",
                                sa = A(s, 0), sb = A(s, 1), sc = A(s, 2), sd = A(s, 3),
                                ta = A(t, 0), tb = A(t, 1), tc = A(t, 2), td = A(t, 3),
                                ca = A(c, 0), cb = A(c, 1), cc = A(c, 2), cd = A(c, 3),
                                animStateDriven = (bool?)m["animStateDriven"] ?? false,
                                mca = A(cmv, 0), mcb = A(cmv, 1), mcc = A(cmv, 2), mcd = A(cmv, 3),
                                aca = A(cfa, 0), acb = A(cfa, 1), acc = A(cfa, 2), acd = A(cfa, 3),
                                ata = A(cat, 0), atb = A(cat, 1), atc = A(cat, 2), atd = A(cat, 3),
                                cba = A(ccb, 0), cbb = A(ccb, 1), cbc = A(ccb, 2), cbd = A(ccb, 3),
                                pva = A(cpv, 0), pvb = A(cpv, 1), pvc = A(cpv, 2), pvd = A(cpv, 3),
                                iea = A(cid, 0), ieb = A(cid, 1), iec = A(cid, 2), ied = A(cid, 3),
                                ala = A(cAlt, 0), alb = A(cAlt, 1), alc = A(cAlt, 2), ald = A(cAlt, 3),
                                a2a = A(ca2, 0), a2b = A(ca2, 1), a2c = A(ca2, 2), a2d = A(ca2, 3),
                                idleAltInterval = m["idleAltInterval"] != null ? (float)m["idleAltInterval"] : 0f,
                                animPhaseSpread = m["animPhaseSpread"] != null ? (float)m["animPhaseSpread"] : 0.5f,
                                position = new UnityEngine.Vector3(Fp(p, "x"), Fp(p, "y"), Fp(p, "z")),
                                scale = m["scale"] != null ? (float)m["scale"] : 1f,
                                brightness = m["brightness"] != null ? (float)m["brightness"] : 1f,
                                desaturate = m["desaturate"] != null ? (float)m["desaturate"] : 0f,
                                tintR = m["tintR"] != null ? (float)m["tintR"] : 0f,
                                tintG = m["tintG"] != null ? (float)m["tintG"] : 0f,
                                tintB = m["tintB"] != null ? (float)m["tintB"] : 0f,
                                textureFile = (string)m["textureFile"] ?? "",
                                handPropName = (string)m["handPropName"] ?? "",
                                handPropGuid = (string)m["handPropGuid"] ?? "",
                                handPropMat = (string)m["handPropMat"] ?? "",
                                handPropBone = (string)m["handPropBone"] ?? "",
                                handPropAngles = (string)m["handPropAngles"] ?? "",
                                respawnAfterLoad = (bool?)m["respawnAfterLoad"] ?? false,
                                freezeDonorAnim = (bool?)m["freezeDonorAnim"] ?? false,
                                disabled = (bool?)m["disabled"] ?? false,
                                clearAimLayer = (bool?)m["clearAimLayer"] ?? false,
                                silenceDonorVfx = (bool?)m["silenceDonorVfx"] ?? false,
                                turretBone = (string)m["turretBone"] ?? "",
                                turretAxis = (int?)m["turretAxis"] ?? -1,
                                muzzleBone = (string)m["muzzleBone"] ?? "",
                                muzzleOffset = (string)m["muzzleOffset"] ?? "",
                                fireOnAttack = (bool?)m["fireOnAttack"] ?? false,
                                deployOnStop = (bool?)m["deployOnStop"] ?? false,
                                engineSound = (bool?)m["engineSound"] ?? false,
                                silenceDonorAudio = (bool?)m["silenceDonorAudio"] ?? false,
                                engineStartEvent = (string)m["engineStartEvent"] ?? "",
                                engineStopEvent = (string)m["engineStopEvent"] ?? "",
                                soundFile = (string)m["soundFile"] ?? "",
                                soundStartFile = (string)m["soundStartFile"] ?? "",
                                soundStopFile = (string)m["soundStopFile"] ?? "",
                                soundVolume = m["soundVolume"] != null ? (float)m["soundVolume"] : 1f,
                                soundStartVolume = m["soundStartVolume"] != null ? (float)m["soundStartVolume"] : 1f,
                                soundStopVolume = m["soundStopVolume"] != null ? (float)m["soundStopVolume"] : 1f,
                                soundIdleFile = (string)m["soundIdleFile"] ?? "",
                                soundIdleVolume = m["soundIdleVolume"] != null ? (float)m["soundIdleVolume"] : 1f,
                                soundIdleInterval = m["soundIdleInterval"] != null ? (float)m["soundIdleInterval"] : 11f,
                                soundIdleGroupRadius = m["soundIdleGroupRadius"] != null ? (float)m["soundIdleGroupRadius"] : 10f,
                                soundAttackFile = (string)m["soundAttackFile"] ?? "",
                                soundAttackVolume = m["soundAttackVolume"] != null ? (float)m["soundAttackVolume"] : 1f,
                                soundAttackOffset = m["soundAttackOffset"] != null ? (float)m["soundAttackOffset"] : 0f,
                                soundDeathFile = (string)m["soundDeathFile"] ?? "",
                                soundDeathVolume = m["soundDeathVolume"] != null ? (float)m["soundDeathVolume"] : 1f,
                                soundDeathOffset = m["soundDeathOffset"] != null ? (float)m["soundDeathOffset"] : 0f,
                                soundBattleFile = (string)m["soundBattleFile"] ?? "",
                                soundBattleVolume = m["soundBattleVolume"] != null ? (float)m["soundBattleVolume"] : 1f,
                                soundBattleOffset = m["soundBattleOffset"] != null ? (float)m["soundBattleOffset"] : 0f,
                                deployPoseTime = m["deployPoseTime"] != null ? (float)m["deployPoseTime"] : 1f,
                                attackRepeats = m["attackRepeats"] != null ? (int)m["attackRepeats"] : 1,
                                deploySpeed = m["deploySpeed"] != null ? (float)m["deploySpeed"] : 1f,
                                recoilSpeed = m["recoilSpeed"] != null ? (float)m["recoilSpeed"] : 1f,
                            });
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
                var sda = Regex.Matches(text, "\"silenceDonorAudio\"\\s*:\\s*(true|false)"); // parity: suppress the borrowed donor's Wwise sound (idle + combat)
                var svx = Regex.Matches(text, "\"silenceDonorVfx\"\\s*:\\s*(true|false)");   // parity: suppress the donor's MecanimEvent VFX (misplaced muzzle flashes)
                var esa = Regex.Matches(text, "\"engineStartEvent\"\\s*:\\s*\"([^\"]*)\"");  // parity: Wwise event name posted on move-start
                var eso = Regex.Matches(text, "\"engineStopEvent\"\\s*:\\s*\"([^\"]*)\"");    // parity: Wwise event name posted on move-stop
                var sf = Regex.Matches(text, "\"soundFile\"\\s*:\\s*\"([^\"]*)\"");           // parity: custom WAV loop in enc_sounds/
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
                var txf = Regex.Matches(text, "\"textureFile\"\\s*:\\s*\"([^\"]*)\"");      // texture-only retexture: PNG filename in enc_skins/
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
                        ca = i < cl.Count ? G(cl[i], 1) : 0, cb = i < cl.Count ? G(cl[i], 2) : 0, cc = i < cl.Count ? G(cl[i], 3) : 0, cd = i < cl.Count ? G(cl[i], 4) : 0,
                        animStateDriven = i < asd.Count && asd[i].Groups[1].Value == "true",
                        mca = i < cmvR.Count ? G(cmvR[i], 1) : 0, mcb = i < cmvR.Count ? G(cmvR[i], 2) : 0, mcc = i < cmvR.Count ? G(cmvR[i], 3) : 0, mcd = i < cmvR.Count ? G(cmvR[i], 4) : 0,
                        aca = i < cfaR.Count ? G(cfaR[i], 1) : 0, acb = i < cfaR.Count ? G(cfaR[i], 2) : 0, acc = i < cfaR.Count ? G(cfaR[i], 3) : 0, acd = i < cfaR.Count ? G(cfaR[i], 4) : 0,
                        ata = i < catR.Count ? G(catR[i], 1) : 0, atb = i < catR.Count ? G(catR[i], 2) : 0, atc = i < catR.Count ? G(catR[i], 3) : 0, atd = i < catR.Count ? G(catR[i], 4) : 0,
                        cba = i < ccbR.Count ? G(ccbR[i], 1) : 0, cbb = i < ccbR.Count ? G(ccbR[i], 2) : 0, cbc = i < ccbR.Count ? G(ccbR[i], 3) : 0, cbd = i < ccbR.Count ? G(ccbR[i], 4) : 0,
                        pva = i < cpvR.Count ? G(cpvR[i], 1) : 0, pvb = i < cpvR.Count ? G(cpvR[i], 2) : 0, pvc = i < cpvR.Count ? G(cpvR[i], 3) : 0, pvd = i < cpvR.Count ? G(cpvR[i], 4) : 0,
                        iea = i < cidR.Count ? G(cidR[i], 1) : 0, ieb = i < cidR.Count ? G(cidR[i], 2) : 0, iec = i < cidR.Count ? G(cidR[i], 3) : 0, ied = i < cidR.Count ? G(cidR[i], 4) : 0,
                        ala = i < calR.Count ? G(calR[i], 1) : 0, alb = i < calR.Count ? G(calR[i], 2) : 0, alc = i < calR.Count ? G(calR[i], 3) : 0, ald = i < calR.Count ? G(calR[i], 4) : 0,
                        a2a = i < ca2R.Count ? G(ca2R[i], 1) : 0, a2b = i < ca2R.Count ? G(ca2R[i], 2) : 0, a2c = i < ca2R.Count ? G(ca2R[i], 3) : 0, a2d = i < ca2R.Count ? G(ca2R[i], 4) : 0,
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
                        silenceDonorAudio = i < sda.Count && sda[i].Groups[1].Value == "true",
                        silenceDonorVfx = i < svx.Count && svx[i].Groups[1].Value == "true",
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
        static ModelEntry LongestMatch(List<ModelEntry> list, string name, Func<ModelEntry, string> key)
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

        internal static ModelEntry FindEntryForUnitDefinition(string unitDefText)
        {
            // Snapshot the reference: this runs on the SIM thread (combat hook) while the main thread may republish
            // `entries` (LoadRegistry retry). The published list is never mutated after publish, so iterating a
            // snapshot is safe; iterating the field directly was not (review 2026-07-19).
            // coreDesc = pawnDescription minus the _NN suffix; the >4 floor keeps a too-short key from matching everything.
            return LongestMatch(entries, unitDefText, x => x.coreDesc.Length > 4 ? x.coreDesc : "");
        }

        // SESSION RE-ARM (review 2026-07-19): AnimationLoad fires per game-session and the manager's collection list is
        // rebuilt — the props axis re-arms on every fire (proven in-game), but the core model axis latched `registered`
        // once per PROCESS: load a second game in the same app run and EnsureRegistered no-ops, leaving our skeletons
        // unregistered and every learned id (skeletonId/animId/descId) stale against the new session's assignments.
        // Called from the AnimationLoad postfix, right before EnsureRegistered re-registers into the fresh manager.
        internal static void RearmModelRegistration()
        {
            registered = false;
            anyAnimated = null; anyMuzzle = null; anyFreeze = null; anyRescuable = null;                    // recomputed on the next pawn-add
            unitScaleByDesc.Clear(); unitScaleNameByDesc.Clear(); vanillaScaledLogged.Clear(); descApplied.Clear(); cachedEra = -1;   // descriptor ids + era are session-scoped (meshApplied deliberately KEPT: the Fx vertex buffers persist)
            _listenerChecked = false;                                // the AudioListener rode a session-scoped camera
            var list = entries;
            if (list != null)
                foreach (var e in list)
                {
                    e.skeletonId = -1; e.animId = -1; e.descId = -1; e.repointed = false;   // session-scoped ids re-learn
                    e.moveAnimId = -1; e.afterAnimId = -1; e.attackAnimId = -1; e.combatAnimId = -1; e.preMoveAnimId = -1; e.idleAnimId = -1; e.idleAltAnimId = -1; e.idleAlt2AnimId = -1;   // state-role ids re-resolve
                    e.idleAltNextAt = 0f; e.idleAltStart = -1f; e.idleAltChosenId = -1;   // idle-alt cadence is session-scoped (Time.time resets)
                    e.stateLastPos.Clear(); e.stateMoving.Clear(); e.stateStoppedAt.Clear(); e.stateMoveStartedAt.Clear();
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
                    if (e.tex != null) { try { UnityEngine.Object.Destroy(e.tex); } catch { } e.tex = null; }
                    // Per-instance state keyed by session-scoped ids (unit GUIDs / sub-pawn instance ids): a new game
                    // can REUSE those ids, so stale entries would feed the first poll wrong moving/deploy decisions,
                    // and the maps otherwise only ever grow. The AudioSources rode session-1 pawn objects (destroyed
                    // with them) — dropping the references is enough.
                    e.deployProgress.Clear(); e.deployLastPos.Clear();
                    e.customSources.Clear(); e.loopHoldUntil.Clear(); e.engineLastPos.Clear(); e.engineMoving.Clear();
                    e.idleNextAt.Clear(); e.attackSoundNextAt.Clear();   // were UNBOUNDED across reloads (never cleared) — session-scoped sub-pawn ids / attacker hashcodes
                }
            deployMoveState = null;                                  // diagnostic map, unit GUIDs are session-scoped
            respawnBase.Clear(); respawnCount.Clear();               // keyed by session-1 unit objects
            _silencedEmitterIds.Clear();                             // static: grew per silenced AudioEmitter ever seen, never reset — session-scoped instance ids
            // DISTRICT axis session state (same bug class): the FxManager and each entry's leaves/private clone were
            // captured from session-1 presentation objects — reusing them in a second game points at torn-down GPU
            // state. Null everything; DistrictApplyEntries re-derives per district instance as the new session loads.
            distFxManager = null;
            foreach (var d in distModels)
            { d.plbc = null; d.privateLeaf = null; d.leaves.Clear(); d.collected = false; d.wait = 0; d.matchLogged = false; d.pointedLogged = false; }
        }

    }
}
