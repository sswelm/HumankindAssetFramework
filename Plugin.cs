using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HumankindAssetFramework
{
    // First BepInEx mod: prove we can (1) load, (2) hook the game, (3) read the live registry
    // AND the configured target mod's own assets — with a config file + an in-game feedback window.
    [BepInPlugin(GUID, "Humankind Asset Framework", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        // Neutral framework identity (2026-07-19; pre-adoption, so no compat shim needed — the old
        // community.humankind.encaccessproof cfg was copied to the new name once, by hand, on this machine).
        public const string GUID = "community.humankind.haf";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool>   VerboseLog;      // gate the chatty per-model/per-pawn bring-up logs; OFF = a quiet load (summaries + warnings/errors only)

        // Gated diagnostic log: per-model/per-pawn bring-up detail that's a useful trace when investigating but noise in a
        // normal run. OFF by default -> quiet. Summaries ("loaded N models"), warnings and errors stay on Log directly.
        internal static void Diag(string msg) { if (VerboseLog != null && VerboseLog.Value) Log?.LogInfo(msg); }

        internal static ConfigEntry<string> TargetMod;       // which mod's assets to access
        internal static ConfigEntry<string> AssetNameFilter; // substring that identifies that mod's assets
        internal static ConfigEntry<KeyCode> ToggleKey;      // open/close the feedback window (Shift+ToggleKey = dump GPU mesh-buffer usage)
        internal static ConfigEntry<bool>   UniversalInjectOn; // registry-driven universal injector (Model Factory)
        internal static ConfigEntry<bool>   StateProbePose0Move; // TEMP diagnostic: play a state-driven model's MOVE clip on Pose0, weight 1, always (isolates move-clip vs Pose1-slot failures)
        internal static ConfigEntry<string> DumpPawnRig;      // CATERPILLAR investigation: pawn-name substring (e.g. "MediumTanks"); when that VANILLA addon loads, dump its skeleton bone tables + clip fields once (how do vanilla tank treads roll?). "" = off.
        internal static ConfigEntry<int>    RespawnDelayFrames; // frames to wait after a borrowed-rotor unit renders before re-spawning it (first-instance rotor fix)
        internal static ConfigEntry<bool>   PersistUnitFacing;  // persist each army's on-screen facing to a HAF side-file on save and restore it on load (the standard save has no facing field)
        // --- EXPERIMENTAL: district visual repoint (the second injection axis; see docs/District-Visuals.md) ---
        internal static ConfigEntry<bool>   DistrictRepointOn;   // master enable for the district-visual repoint hook
        internal static ConfigEntry<string> DistrictName;        // which district's on-map visual to replace (ConstructibleDefinitionName), e.g. Villages_StoneQuarry
        internal static ConfigEntry<string> DistrictAffinity;    // ZERO-BAKE proof: swap the district's visualAffinity to another vanilla one (renders an existing building; no custom asset needed)
        internal static ConfigEntry<string> DistrictEvolverGuid; // CUSTOM MODEL: an FxEvolverMaterial GUID (our baked quarry) as 4 ints "a,b,c,d"; SetChannel points the district's mesh channel at it
        internal static ConfigEntry<string> DistrictFxMeshGuid;  // MESH-SWAP: our baked FxMesh GUID; keep the district's own working material, swap only its mesh to ours (best render odds)
        internal static ConfigEntry<int>    DistrictBufferHeadroom; // extra vertices to add to the big (Visual) GPU mesh buffer at init, so custom district meshes fit even in a full late-game city. 0 = off (leave the buffer as the game sizes it).
        internal static ConfigEntry<bool>   DistrictIsolate;         // scope the mesh-swap to only the target district's own tile (private per-instance leaf) instead of the shared-global swap
        internal static ConfigEntry<bool>   DistrictDebug;           // investigation diagnostics ([District] saw / [DistrictMat] / [DistrictSub] dumps) — off in normal play, they reflect on every district update
        // --- EXPERIMENTAL: generic GPU mesh-buffer overrides (units, districts, any content layer) ---
        internal static ConfigEntry<string> BufferOverrides;     // per-layer overrides "<nameSubstr>:verts=+N,idx=+N,meshes=+N,maxtris=N;..." applied at layer creation
        internal static ConfigEntry<int>    SkeletonBoneBudget;  // shared per-frame animated-bone pool size (vanilla 65,535; high-bone customs overflow it -> spike plague)
        internal static ConfigEntry<string> SilenceAudioEvents;  // comma-separated Wwise event-name SUBSTRINGS to drop at AudioManager.PostEvent (test/POC for era-audio) — "" = silence nothing
        // --- EXPERIMENTAL: pawn prop/attachment axis (custom weapons & gear; see the sling experiment) ---
        internal static ConfigEntry<bool>   PropRegisterOn;      // register our baked MeshCollections with the AnimationManager (the fragment render gate)
        internal static ConfigEntry<string> PropCollectionGuids; // semicolon-separated "a,b,c,d" GUIDs of MeshCollection/Skeleton assets to register
        internal static ConfigEntry<string> PropCollectionNames; // semicolon-separated asset NAMES (same order as the GUIDs) — fallback loader when the Amplitude catalog misses the GUID

        internal static ConfigEntry<string> ProjectileOverrides;  // EXPERIMENTAL projectile axis: "<pawnDefGuid>=<projectileGuid>;..." — point a unit's fired projectile at our baked ProjectileAsset (the kamikaze drone)

        internal static ConfigEntry<bool>   FormationOverrideOn;  // FORMATION axis: enc_formations.json (Formation Override window) — inject custom formations + repoint units (pawn count per unit)
        internal static ConfigEntry<bool>   FormationReinstantiateOn; // FORMATION axis: after apply, re-instantiate already-spawned units of a repointed type so they reach the new pawn count (fixes the load-race undercount)

        private bool show;
        private Rect winRect = new Rect(60, 60, 480, 420);
        private Vector2 scroll;
        private string atlasFilter = "";   // Dump Atlases: only layers whose name contains this (blank = all)
        private string previewEvent = "";  // F8 audition: Wwise event name to post via Play Event

        private void Awake()
        {
            Log = Logger;

            // --- the config file (auto-written to BepInEx/config/community.humankind.encaccessproof.cfg) ---
            TargetMod       = Config.Bind("General", "TargetMod", "ENCReload",
                                  "Name of the mod whose assets this plugin should access.");
            AssetNameFilter = Config.Bind("General", "AssetNameFilter", "Zeppelin",
                                  "Substring used to find that mod's assets in the loaded databases (proof of access).");
            ToggleKey       = Config.Bind("General", "ToggleWindowKey", KeyCode.F8,
                                  "Key to toggle the in-game feedback window. Hold SHIFT + this key to instead dump the live " +
                                  "GPU mesh-content buffer usage (verts/indices/meshes per layer vs the 100k/250k/256 ceiling) to the log.");
            VerboseLog      = Config.Bind("General", "VerboseLog", false,
                                  "Log the chatty per-model / per-pawn BRING-UP detail (skeleton repoints, atlas/skin/prop injection, " +
                                  "pose-hook dumps, per-unit state, etc.). OFF (default) keeps a normal load QUIET — only the summary lines " +
                                  "('loaded N models/districts/sounds'), warnings and errors. Turn ON when investigating a specific model.");
            UniversalInjectOn = Config.Bind("Factory", "UniversalInject", true,
                                  "Registry-driven universal model injector (the Model Factory). Reads the model registry JSON " +
                                  "from this config folder and repoints each listed pawn definition onto its baked skeleton.");
            StateProbePose0Move = Config.Bind("Factory", "StateProbePose0Move", false,
                                  "TEMP diagnostic for state-driven models: play the MOVEMENT clip on Pose0, weight 1, ALWAYS " +
                                  "(ignores the state machine). If the model runs in place standing still, the move clip is fine " +
                                  "and the Pose1 slot is the problem; if it's invisible, the move clip's GPU bake is bad.");
            DumpPawnRig = Config.Bind("Factory", "DumpPawnRig", "",
                                  "CATERPILLAR investigation: pawn-name substring (e.g. MediumTanks). When a matching VANILLA " +
                                  "pawn addon loads, dump its skeleton bone tables, mesh info and clip-related fields to the log " +
                                  "ONCE — the data that decides how vanilla tank treads roll (track bones vs shader scroll). Empty = off.");
            RespawnDelayFrames = Config.Bind("Factory", "RespawnDelayFrames", 1,
                                  "Frames to wait after a borrowed-rotor unit (a model with respawnAfterLoad set) renders before " +
                                  "the plugin re-spawns it to clear the first-instance low-rotor bug. 1 = near-instant (default). " +
                                  "Increase (e.g. 30 = ~0.5s at 60fps) only if a slower machine briefly shows the low rotor before it corrects.");

            PersistUnitFacing = Config.Bind("Factory", "PersistUnitFacing", true,
                                  "Persist each army's on-screen facing (FormationAngle) to a HAF side-file " +
                                  "(BepInEx/config/haf_state/facing/<save>.facing) on save, and restore it on load. The game's own " +
                                  "save has NO facing field — units otherwise reset heading on reload. Keyed by the army's serialized " +
                                  "GUID; never touches the standard save. true = on (default).");

            // --- EXPERIMENTAL district-visual repoint (docs/District-Visuals.md). Off by default; scoped to ONE district by
            //     name so the shared visual affinity other districts borrow is never touched. Two independent modes below. ---
            DistrictRepointOn   = Config.Bind("District", "DistrictRepoint", false,
                                  "EXPERIMENTAL: enable replacing a single district's on-map visual (the second injection axis). " +
                                  "Scoped to the DistrictName below only — other districts sharing the same visual affinity are unaffected.");
            DistrictName        = Config.Bind("District", "DistrictName", "Villages_StoneQuarry",
                                  "The ConstructibleDefinitionName of the district whose on-map building to replace (e.g. ENC's Villages_StoneQuarry).");
            DistrictAffinity    = Config.Bind("District", "DistrictAffinityOverride", "",
                                  "ZERO-BAKE proof mode: set to another vanilla visual-affinity name (e.g. DistrictVisualAffinity_Base_Industry) to make the " +
                                  "district render that existing building instead — no custom asset needed. Proves the hook + scoping in-game. Blank = off.");
            DistrictEvolverGuid = Config.Bind("District", "DistrictEvolverGuid", "",
                                  "CUSTOM-MODEL mode: an FxEvolverMaterial asset GUID (our baked quarry material) as four ints \"a,b,c,d\". " +
                                  "The hook calls the game's public SetChannel(layer, guid) so the district draws our custom static mesh. Blank = off. " +
                                  "Takes precedence over DistrictAffinityOverride when both are set.");
            DistrictFxMeshGuid  = Config.Bind("District", "DistrictFxMeshGuid", "",
                                  "MESH-SWAP mode (best render odds): our baked FxMesh GUID as four ints \"a,b,c,d\". Instead of loading a whole " +
                                  "foreign material, the hook keeps the district's OWN working material and swaps just its mesh to ours — so our model " +
                                  "renders in the context that already works. Only needs an FxMesh (District step 1), no cloned material. " +
                                  "Takes precedence over the other two modes. Blank = off.");
            DistrictIsolate     = Config.Bind("District", "DistrictIsolate", false,
                                  "SCOPE the mesh-swap to ONLY the DistrictName tile(s), instead of mutating the shared building leaves globally. " +
                                  "Builds ONE private (Instantiated) leaf material pointing at our FxMesh and points just this district's own " +
                                  "channel + particle at it — so other cities' buildings are untouched. Needs DistrictFxMeshGuid set. EXPERIMENTAL.");
            DistrictDebug       = Config.Bind("District", "DistrictDebug", false,
                                  "Verbose district-investigation diagnostics: log every district name seen ([District] saw), each district's " +
                                  "resolved material GUID ([DistrictMat]) and the target's sub-material table ([DistrictSub]). These reflect on " +
                                  "every district update — leave OFF in normal play; turn on only when mapping a new district's material chain.");
            DistrictBufferHeadroom = Config.Bind("District", "DistrictBufferHeadroom", 0,
                                  "Extra VERTICES to add to the game's big 'Visual' GPU mesh buffer (the shared building buffer, ~3,000,000 by default) " +
                                  "at startup, so custom district meshes fit even when a built-up late-game city has nearly filled it. 0 = off. " +
                                  "e.g. 1000000 = +~48MB VRAM. Applied once at buffer creation; takes effect on the next launch.");

            SilenceAudioEvents  = Config.Bind("Audio", "SilenceAudioEvents", "",
                                  "Comma-separated Wwise event-name SUBSTRINGS to SILENCE — any sound whose event name contains one is dropped at the " +
                                  "service sink (AudioManager.PostEvent), the same chokepoint the F8 Audio Trace watches. Case-insensitive. \"\" = silence " +
                                  "nothing (default, no-op). e.g. \"Vehicles_Mortar_Move\" mutes the organ gun's move sound; add a city-ambience event once " +
                                  "the Audio Trace names it. Re-read on every post, so edits take effect without a relaunch (F5-reload the config).");

            // DEFAULT RATIONALE: 262,144 = 4x vanilla (65,535). This is a deliberately generous SAFETY margin, not an
            // empirically-fitted worst case — at 242 bones/instance it is ~1,080 tread-heavy instances of headroom, far
            // more than any observed map has needed, chosen so the spike plague cannot recur rather than to hit a tight
            // bound. It is cheap to over-provision: the pool holds bone entries, so 4x costs only a few MB of VRAM, and
            // it is applied UNCONDITIONALLY (every plugin user pays it, even a pure-reskin setup with no high-bone custom
            // on screen). Both trade-offs are intentional given how small the cost is; shrink it only if VRAM is tight,
            // and re-verify on a dense late-game map (the plague only shows when the pool actually overflows).
            SkeletonBoneBudget  = Config.Bind("Buffers", "SkeletonBoneBudget", 262144,
                                  "Size of the game's shared per-frame ANIMATED-BONE pool (PawnManager's animatedSkeletonEntry buffers; " +
                                  "vanilla = 65,535 entries shared by EVERY pawn on screen). High-bone custom skeletons (tank-destroyer " +
                                  "treads = 242 bones/instance, mech = 240) overflow it on dense late-game maps — overflowing pawns read " +
                                  "other pawns' matrices and stretch into spikes / twitch (INCLUDING vanilla units). Applied at pawn-system " +
                                  "creation. Default 262,144 = 4x vanilla: a generous safety margin (~1,080 tread-instances of headroom), " +
                                  "not a tight bound — costs only a few MB of VRAM, applied to all users. 0 = leave the vanilla size.");

            // --- EXPERIMENTAL: generic GPU mesh-buffer overrides. Every mesh family (pawns, districts, effects) uploads
            //     into per-layer GPU buffers created with serialized sizes AND a per-mesh triangle cap that silently
            //     TRUNCATES any mesh above it (holes in the model, no log). This lifts any of them, per layer. ---
            BufferOverrides     = Config.Bind("Buffers", "BufferOverrides", "",
                                  "Per-layer GPU mesh-buffer overrides, applied once at layer creation. Format: " +
                                  "\"<layerNameSubstring>:verts=+N,idx=+N,meshes=+N,maxtris=N\" — semicolon-separated for several layers. " +
                                  "verts/idx/meshes ADD to the layer's buffer sizes (vertex buffer / index buffer / mesh table); " +
                                  "maxtris SETS the per-mesh triangle cap absolutely (0 = unlimited — quads beyond the cap are otherwise " +
                                  "silently dropped, leaving holes in a detailed model). Layer names: Shift+F8 mesh-budget dump. " +
                                  "e.g. \"MeshWithSkeleton:verts=+200000,idx=+500000,maxtris=0\". Blank = off.");

            // --- EXPERIMENTAL pawn PROP/attachment axis (custom weapons & gear on pawn attachment slots). A
            //     PresentationPawnFragmentMesh (the EQ_* asset a pawn's Attachements slot references) hard-gates on its
            //     ModelPrefab's MeshCollection being REGISTERED with the AnimationManager; this registers ours. ---
            PropRegisterOn      = Config.Bind("Props", "PropRegister", false,
                                  "EXPERIMENTAL: register our baked MeshCollection assets with the game's AnimationManager so a custom " +
                                  "PresentationPawnFragmentMesh (a pawn attachment: weapon/gear, e.g. a sling) can reference our mesh. " +
                                  "Without this the fragment logs 'was not registered to AnimationManager' and draws nothing.");
            PropCollectionGuids = Config.Bind("Props", "PropCollectionGuids", "",
                                  "Semicolon-separated Amplitude GUIDs (each four ints \"a,b,c,d\") of MeshCollection/Skeleton assets from our " +
                                  "mod bundle to register at load. Blank = none.");
            PropCollectionNames = Config.Bind("Props", "PropCollectionNames", "",
                                  "Semicolon-separated asset NAMES matching PropCollectionGuids in order (e.g. Sling_Collection). Used as a " +
                                  "fallback loader: Amplitude's asset catalog misses mod-bundle MeshCollections by GUID, so the plugin pulls " +
                                  "the asset by name from the game's already-loaded Unity bundles instead.");

            // --- EXPERIMENTAL projectile axis (docs/Projectiles.md): a unit's PresentationPawnDefinition.Projectile (a
            //     ProjectileAssetReference, read at attack time to spawn the flying FX) is re-pointed at our baked
            //     ProjectileAsset — whose trail is a cloned mesh-drawer rendering our FxMesh (the kamikaze drone). ---
            ProjectileOverrides = Config.Bind("Projectiles", "ProjectileOverrides", "",
                                  "EXPERIMENTAL: point a unit's fired projectile at our baked ProjectileAsset. Format: " +
                                  "\"<pawnDefGuid>=<projectileGuid>;...\" — each side four ints \"a,b,c,d\". For the PresentationPawnDefinition " +
                                  "with that GUID, set its Projectile to the ProjectileAsset with that GUID (both from Projectile Lab; the pawn " +
                                  "def GUID is the Guid line of the unit in the SDK Asset Picker). Applied at AnimationLoad. Blank = off.");

            // --- FORMATION override (fifth data axis; ZERO baked assets — see Patches/FormationOverridePatch.cs).
            //     Registry-driven and inert without enc_formations.json, so it defaults ON like UniversalInject. ---
            FormationOverrideOn = Config.Bind("Formations", "FormationOverride", true,
                                  "Registry-driven FORMATION override (the Formation Override editor window): reads enc_formations.json, " +
                                  "rebuilds each custom PresentationFormationDefinition at runtime (dummy positions + the six per-orientation " +
                                  "grids), adds it to the live formation database at load, and repoints each linked unit's formation " +
                                  "reference — changing how MANY pawn models the unit displays (pawn count = ceil(health% x dummy count)). " +
                                  "Also grows the Formation3D dummy pool when a custom formation is bigger than the vanilla prefab allows. " +
                                  "No baked assets, fully reversible: remove the registry entry and the game is vanilla on next launch. " +
                                  "Inert when the registry file is absent or empty.");
            FormationReinstantiateOn = Config.Bind("Formations", "FormationReinstantiate", true,
                                  "After the formation override applies, re-run the game's own UpdatePawns on any already-spawned unit of a " +
                                  "repointed type so it rebuilds its pawn grid at the new dummy count. Fixes units (e.g. the player's starting " +
                                  "units) that spawned DURING load, before the override + Formation3D-prefab growth landed, so they were stuck at " +
                                  "the vanilla count. Costs a one-time visible pawn 're-form' pop on those units. Turn off to keep the count units " +
                                  "had when they first rendered.");

            // Patch each hook independently so a single missing Amplitude target (a game update renaming one type) only
            // disables THAT hook -- instead of a null TargetMethod throwing out of PatchAll and failing the whole plugin.
            var harmony = new Harmony(GUID);
            int patched = 0;
            var hooks = new[] {
                typeof(UniRegisterHook), typeof(UniRepointHook), typeof(UniPawnPoseHook),
                typeof(Hk_ArtilleryStrike),   // firing-on-attack: bombard -> play the model's clip once (docs/Firing-On-Attack.md)
                typeof(Hk_PawnRangedFight),   // state-driven ATTACK: every pawn ranged shot arms the attack-clip window (per-pawn, main thread)
                typeof(Hk_PawnMeleeFight),    // state-driven MELEE ATTACK: close-combat pawns (the Abomination animal) arm the attack-clip window (2026-07-22)
                typeof(Hk_SilenceAudio),      // silenceDonorAudio: drop the borrowed donor's Wwise posts (idle growl + combat maul) on opted-in pawns (2026-07-23)
                typeof(Hk_EarlyAttackSound),  // early attack roar: UnitActionFaceEnemy.StartUnitAction -> TryEarlyAttackSound, the earliest "our unit commits to the strike" seam (2026-07-21)
                typeof(Hk_PawnDeath),         // death cue: one-shot as a pawn of ours starts dying (2026-07-23)
                typeof(Hk_SilenceVfx),        // donor VFX suppression: drop misplaced donor muzzle flashes, sounds untouched (2026-07-24)
                typeof(Hk_FireProjStash),     // muzzle offset stash: bracket AlterationFireProjectile.StartEvent so the bone redirect can pre-compensate the donor's barrel offset (2026-07-24)
                typeof(Hk_BattleStarted),     // battle-start war cry: sim-thread match -> main-thread camera-anchored one-shot (2026-07-23)
                typeof(Hk_AudioTrace),        // diagnostic: live-trace Wwise PostEvent (gated behind the F8 Audio Trace toggle)
                typeof(Hk_SilenceEvents),     // silence-by-event-name: drop any Wwise post whose name matches Audio/SilenceAudioEvents (POC for era-audio; no-op when empty)
                typeof(Hk_DistrictRepoint),   // EXPERIMENTAL: replace one district's on-map visual (docs/District-Visuals.md)
                typeof(Hk_DistrictBufferHeadroom), // EXPERIMENTAL: enlarge the shared 'Visual' mesh buffer so custom district meshes fit (opt-in)
                typeof(Hk_AnimatedBonePoolHeadroom), // enlarge the shared per-frame animated-bone pool (65,535 vanilla) — the spike-plague fix
                typeof(Hk_PropRegister),           // EXPERIMENTAL: register our prop MeshCollections at AnimationLoad, before pawn resolution (opt-in)
                typeof(Hk_ProjectileOverride),     // EXPERIMENTAL: re-point a unit's Projectile at our baked ProjectileAsset (kamikaze drone) at AnimationLoad (opt-in)
                typeof(Hk_MuzzleRelocate),         // muzzleBone: redirect the muzzle-flash bone lookup (donor weapon socket missing on our renamed rig) to OUR bone (2026-07-24)
                typeof(Hk_FormationPrefabExtend),  // FORMATION axis: grow Formation3DPrefab's dummy pool before the pool clones it, so >9-pawn custom formations fit (2026-07-27)
                typeof(Hk_FormationInstanceExtend),// FORMATION axis: top up a live pooled Formation3D when its definition outgrows it (belt-and-braces for the prefab surgery) (2026-07-27)
                typeof(Hk_FormationSpawnDiag),     // FORMATION axis TEMP diagnostic: log dummies/pawns/health at InstantiatePawns for >9-dummy formations (2026-07-27)
                typeof(Hk_FormationPawnScale),     // FORMATION axis: per-model Scale from the registry link (pawn root localScale -> GPU TRS) (2026-07-28)
                typeof(Hk_SandboxSave), typeof(Hk_SandboxLoad),  // FACING PERSIST: capture each army's FormationAngle on save, restore on load (the standard save has no facing) (2026-08-01)
                typeof(Hk_BattleTurnRate),  // BATTLE TURN spike: cap unanimated choreography turns at rate deg/s instead of the vanilla fixed 0.5 s (2026-08-05)
                typeof(Hk_BattleHoldFire),  // BATTLE TURN spike: hold PawnActionRangedStartAttack until the shooter's turn completes (2026-08-05)
                typeof(Hk_BattleTurnProbe), // BATTLE TURN spike DIAGNOSTIC: log RotationFSM turn starts (which path does an attack's turn take?) (2026-08-05)
                typeof(Hk_BattleTurnStep),  // BATTLE TURN spike DIAGNOSTIC: log StepTurning route (animated turn-anim vs unanimated 0.5 s lerp) per unit (2026-08-05)
                typeof(Hk_BattleAttackDelay), // BATTLE TURN spike: extend AttackFSM delayDuration by the remaining turn-ease time — the shell waits for the barrel (2026-08-05)
                typeof(Hk_BattleAttackGate),  // BATTLE TURN spike: DYNAMIC gate on the attack FSM's delay step — waits for alignment whenever the facing snap lands (fixes the Start-time race) (2026-08-05)
            };
            int skipped = 0;
            foreach (var t in hooks)
            {
                try
                {
                    // Patch() returns the methods it ACTUALLY patched. A hook whose TargetMethod returns null (the type/method
                    // wasn't found — Amplitude API drift) patches nothing WITHOUT throwing, so counting "didn't throw" would
                    // report it as patched. Count real patches instead, and warn on a hook that resolved no target.
                    var applied = harmony.CreateClassProcessor(t).Patch();
                    if (applied != null && applied.Count > 0) patched++;
                    else { skipped++; Log.LogWarning($"[Uni] hook '{t.Name}' patched nothing — its TargetMethod found no method (Amplitude API changed, or the hook self-disabled)."); }
                }
                catch (System.Exception ex) { skipped++; Log.LogError($"[Uni] hook '{t.Name}' failed to apply (Amplitude API changed?): {ex.Message}"); }
            }
            Log.LogInfo($"Model Factory plugin loaded ({patched}/{hooks.Length} hooks patched{(skipped > 0 ? $", {skipped} skipped — see warnings above" : "")}). Press {ToggleKey.Value} in-game for the " +
                        $"diagnostic window. UniversalInject={UniversalInjectOn.Value}");
            GameBinding.ValidateAndLog(GameBinding.Catalog);   // compatibility report: warn loudly if a bound game type/member went missing (game update)
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey.Value))
            {
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    UniversalInject.DumpMeshBudget();   // Shift+F8 = dump GPU mesh-buffer usage (F9 collided with Humankind quick-load)
                else
                    show = !show;                       // F8 = toggle the feedback window
            }
            if (UniversalInjectOn.Value)
            {
                UniversalInject.TickTexture();          // keep registry-driven model atlases applied
                UniversalInject.MaybeRespawnPostLoad(); // one-shot post-load re-spawn to clear the first-instance rotor race
                UniversalInject.ProcessFireQueues();    // per-instance fire-on-attack: arm only the pawn that actually bombarded
                UniversalInject.ProcessDeployState();   // deploy-on-stop: record which of our pawns' units are currently moving
                UniversalInject.ProcessAnimStates();    // state-driven (Phase 2): publish per-unit moving/stopped for the idle/move/after clips
                UniversalInject.ProcessEngineAudio();   // engine sound: fire the per-ship Start/Stop move sound on our units
                UniversalInject.ProcessSubPawnVisuals();   // one-shot pawn-prefab hierarchy dump (the ghost-rotor hunt); no-op once dumped
                UniversalInject.ProcessBattleCries();   // battle-start war cries queued by the sim-thread hook
                UniversalInject.PollRotorTrim();        // live rotor-trim dial (enc_rotortrim.txt): constant BR-slot tilt on donor-clip rotor bones
                UniversalInject.PollTurnEase();         // live turn-ease dial (enc_turnease.txt): eased facing + bank on donor-clip units (spike)
                UniversalInject.PollTerrainHug();       // live terrain-hug dial (enc_hugterrain.txt): fly low over open ground, climb for districts (spike)
                UniversalInject.TickDistrictMeshSwap(); // EXPERIMENTAL district: per-frame swap our FxMesh into the live selector's leaf drawers
            }
            BattleTurn.Poll();                          // live battle-turn dial (enc_battleturn.txt): turn rate + hold-fire for ALL units — independent of model injection, so outside the UniversalInject gate (spike)
            if (PersistUnitFacing.Value)
                FacingPersist.Tick();                   // capture each army's facing + restore it after a load (stationary units only). OWN gate — facing is independent of model injection, so turning UniversalInject off must NOT silence it (it has its own save/load hooks + config).
            if (PropRegisterOn.Value)
                UniversalInject.TickPropRegister();     // EXPERIMENTAL props: register our MeshCollections once the AnimationManager exists
            if (FormationOverrideOn.Value)
                FormationOverride.Tick();               // FORMATION axis: retry inject+repoint if the databases weren't up at AnimationLoad
        }

        private void OnGUI()
        {
            if (!show) return;
            winRect = GUILayout.Window(GUID.GetHashCode(), winRect, DrawWindow, "Humankind Asset Framework");
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label($"Target mod: {TargetMod.Value}     Filter: \"{AssetNameFilter.Value}\"");
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Re-scan now")) Prober.RunScan();
                if (GUILayout.Button("Scan Models")) Prober.ScanModels();
                if (GUILayout.Button("Test Write")) Prober.TestWrite();
                if (GUILayout.Button("Clear")) Prober.Report.Clear();
            }
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Dump UnitDef")) Prober.DumpUnitDef();
                if (GUILayout.Button("Dump Formation")) Prober.DumpFormation();
                if (GUILayout.Button("Dump Atlases")) UniversalInject.DumpOutputLayerAtlases(atlasFilter);
                if (GUILayout.Button("Dump Audio")) UniversalInject.DumpAudioState(atlasFilter);
                if (GUILayout.Button("Dump District")) { Prober.Report.Clear(); foreach (var l in UniversalInject.DumpDistrictState()) Prober.Report.Add(l); }
                if (GUILayout.Button("Mesh Budget")) { Prober.Report.Clear(); foreach (var l in UniversalInject.MeshBudgetLines()) { Prober.Report.Add(l); Plugin.Log.LogInfo("[Budget] " + l); } }
                if (GUILayout.Button("Smoke Test")) UniversalInject.RunSmokeTest();   // runtime integration check: bindings + registry + injection health -> [SmokeTest] PASS/FAIL
            }
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("Dump name filter — atlas/audio (blank = all):", GUILayout.Width(220));
                atlasFilter = GUILayout.TextField(atlasFilter);   // e.g. "Corvette" -> dumps only that unit's layer, not all 600+
            }
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Play Audio (test) on filtered units")) UniversalInject.PlayAudioTest(atlasFilter);
                if (GUILayout.Button(UniversalInject.AudioTraceOn ? "Audio Trace: ON" : "Audio Trace: OFF"))
                { UniversalInject.AudioTraceOn = !UniversalInject.AudioTraceOn; UniversalInject.AudioTraceFilter = atlasFilter; }
                if (GUILayout.Button("Dump Sound Catalog")) UniversalInject.DumpSoundCatalog();
                if (GUILayout.Button("Play Sound Test (WAV)")) UniversalInject.PlaySoundTest();
            }
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("Play Event:", GUILayout.Width(70));   // audition a Wwise event by name (from Dump Sound Catalog) on a live emitter
                previewEvent = GUILayout.TextField(previewEvent);
                if (GUILayout.Button("Play Event", GUILayout.Width(90))) UniversalInject.PlayEventByName(previewEvent);
                if (GUILayout.Button("Stop", GUILayout.Width(50))) UniversalInject.StopEventAudition();   // cut a looping "_Start" audition
            }
            GUILayout.Space(4);
            // Unit resize / era ageing (live): the global era drives the Global Era Lab grid, so seeing both the era
            // and each ruled unit's composed size here is what makes an authoring pass verifiable without the log.
            GUILayout.Label("Unit resize — Resize Lab rules x Global Era Lab grid:");
            foreach (var l in UniversalInject.ResizeStatusLines()) GUILayout.Label(l);
            GUILayout.Space(4);
            GUILayout.Label("GPU mesh buffer (live) — Shift+F8 also logs it:");
            foreach (var l in UniversalInject.MeshBudgetLines()) GUILayout.Label(l);
            GUILayout.Space(4);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(320));
            if (Prober.Report.Count == 0)
                GUILayout.Label("No scan yet — load a game (auto-scans on load), or press Re-scan.");
            foreach (var line in Prober.Report)
                GUILayout.Label(line);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }
    }
}
