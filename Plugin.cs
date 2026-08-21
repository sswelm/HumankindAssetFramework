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

        // ---- LOGGING HYGIENE (2026-08-19 logging audit) ----
        // LOG-ONCE, systematized: the codebase had grown 16 hand-rolled `static bool xLogged` guards, and the
        // pattern's failure mode is forgetting one (the corrupt-source error spammed dozens of Console lines the
        // day it shipped). One keyed gate replaces them: Once(key) is true exactly once per process per key.
        static readonly System.Collections.Generic.HashSet<string> onceKeys = new System.Collections.Generic.HashSet<string>();
        internal static bool Once(string key) { lock (onceKeys) return onceKeys.Add(key); }
        internal static void DiagOnce(string key, string msg) { if (Once(key)) Diag(msg); }
        internal static void LogOnceInfo(string key, string msg) { if (Once(key)) Log?.LogInfo(msg); }
        internal static void LogOnceWarning(string key, string msg) { if (Once(key)) Log?.LogWarning(msg); }
        // INVARIANT FORMATTING, by policy: C# string interpolation formats with the CURRENT culture at the call
        // site, so a wrapper cannot retro-fix `$"…{someFloat}…"` (the combatZ line printed "-0,13" on a Dutch
        // locale). POLICY: any log line or file that interpolates a float/double/Vector uses Inv($"…") — it
        // formats the whole interpolation invariantly. Files HAF machine-reads are already invariant (verified
        // in the audit: config parses all pass InvariantCulture; the report writers emit ints or pre-formatted
        // strings). Console lines migrate opportunistically as they're touched.
        internal static string Inv(System.FormattableString f) => System.FormattableString.Invariant(f);

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
        internal static ConfigEntry<int>    DistrictMeshDensityBoost; // multiplier on the private layer's PrimitivePerParticleCount — raises the per-mesh 255-sub-particle render ceiling so high-poly composed districts (grove pizzas) draw fully. 0/1 = vanilla.
        internal static ConfigEntry<string> DistrictGroundMaterial; // force a GroundMaterialDefinition (grass field) under custom districts — the terrain paint a wonder's affinity lacks. Blank = off. DistrictDebug logs the valid names.
        internal static ConfigEntry<string> DistrictHexSculpt; // force a HexagonSculptingDefinition (raised platform + strategic footprint) under custom wonders. Blank = off. Per-entry field overrides.
        internal static ConfigEntry<bool>   DistrictIsolate;         // scope the mesh-swap to only the target district's own tile (private per-instance leaf) instead of the shared-global swap
        internal static ConfigEntry<bool>   DistrictDebug;           // investigation diagnostics ([District] saw / [DistrictMat] / [DistrictSub] dumps) — off in normal play, they reflect on every district update
        internal static ConfigEntry<string> WonderNativeRows;        // SPIKE wip-wonder-affinity: fill empty cells in the ArtificialWonder repo database, "WonderName=a,b,c,d;..." (FxEvolverMaterial guid)
        internal static ConfigEntry<string> DistrictMainRows;        // SPIKE dedicated-visual (hybrid register): fill */District/Main.Level1+2 cells for an affinity -> our baked CityMapSelector, "AffinityName=a,b,c,d;..."
        internal static ConfigEntry<string> DistrictSelectorTile;    // SCOPED dedicated-visual: put our baked selector on ONLY the named district's tile (keeps shared affinity + fallback), "ConstructibleDefinitionName=a,b,c,d;..."
        internal static ConfigEntry<string> DistrictFootprint;       // RUNTIME footprint choice: graft a chosen donor selector's DECALS onto the scoped district (building stays ours), "ConstructibleDefinitionName=a,b,c,d;..." (donor = any */District/Main selector GUID)
        internal static ConfigEntry<string> DistrictFootprintDrop;   // comma-separated decal NAME substrings to DROP from the grafted footprint (the rock/rubble "surface texture" layers); blank = keep ALL donor decals
        internal static ConfigEntry<string> DistrictFootprintMask;   // EXPERIMENTAL: path to a PNG silhouette mask -> a UNIQUE strategic footprint matching the district's own layout (injected into a private clone of the SchematicView mask atlas)
        internal static ConfigEntry<string> DistrictFootprintMaskSize; // tuning: world size (units) of the injected silhouette footprint decal; default 3.0
        internal static ConfigEntry<string> DistrictFootprintMaskRotation; // tuning: rotate the footprint decal N degrees clockwise; default 0
        internal static ConfigEntry<string> DistrictFootprintMaskCut;      // "true" = cut the footprint to the PNG's shape (may render faint via SchematicView); default false = solid block (skip mask)
        internal static ConfigEntry<string> DistrictFootprintMesh;         // "true" = keep the district's own 3D BUILDING MESH rendering at strategic zoom (RenderFeatureSelector.SelectionFlags0=0 -> AlwaysEnabled) so the footprint IS the real geometry, not a flat decal
        internal static ConfigEntry<string> DistrictFootprintMeshBW;       // "true" = when the persistent mesh footprint is on the STRATEGIC map (zoomed out), bind a GREYSCALE copy of its albedo so the reactor reads black-and-white; full colour up close. Needs DistrictFootprintMesh=true
        internal static ConfigEntry<string> DistrictFootprintMeshFlat;     // "true" = SQUASH the persistent mesh flat (size.y -> ~0) while on the strategic map, so the footprint reads as a FLAT reactor-shaped sheet instead of a 3D model; full height up close. Needs DistrictFootprintMesh=true
        internal static ConfigEntry<string> DistrictFootprintMeshHideDecal; // "true" (default) = when the mesh footprint is on, DROP the template's baked footprint DECAL item(s) (the inherited donor outline that shows beneath the mesh). Needs DistrictFootprintMesh=true
        internal static ConfigEntry<string> DistrictFootprintMeshFlatHeight; // flatten HEIGHT (size.y multiplier) on the strategic map: ~0.02 = paper-flat (edges can drown in rising terrain), up toward 1 = full 3D. Tune in the F8 window until it reads flat yet clears the ground
        // --- EXPERIMENTAL: generic GPU mesh-buffer overrides (units, districts, any content layer) ---
        internal static ConfigEntry<string> BufferOverrides;     // per-layer overrides "<nameSubstr>:verts=+N,idx=+N,meshes=+N,maxtris=N;..." applied at layer creation
        internal static ConfigEntry<int>    SkeletonBoneBudget;  // shared per-frame animated-bone pool size (vanilla 65,535; high-bone customs overflow it -> spike plague)
        internal static ConfigEntry<string> SilenceAudioEvents;  // comma-separated Wwise event-name SUBSTRINGS to drop at AudioManager.PostEvent (test/POC for era-audio) — "" = silence nothing
        // --- EXPERIMENTAL: pawn prop/attachment axis (custom weapons & gear; see the sling experiment) ---
        internal static ConfigEntry<bool>   PropRegisterOn;      // register our baked MeshCollections with the AnimationManager (the fragment render gate)
        internal static ConfigEntry<string> PropCollectionGuids; // semicolon-separated "a,b,c,d" GUIDs of MeshCollection/Skeleton assets to register
        internal static ConfigEntry<string> PropCollectionNames; // semicolon-separated asset NAMES (same order as the GUIDs) — fallback loader when the Amplitude catalog misses the GUID

        internal static ConfigEntry<string> ProjectileOverrides;  // EXPERIMENTAL projectile axis: "<pawnDefGuid>=<projectileGuid>;..." — point a unit's fired projectile at our baked ProjectileAsset (the kamikaze drone)

        internal static ConfigEntry<bool>   FormationOverrideOn;  // FORMATION axis: haf_formations.json (Formation Override window) — inject custom formations + repoint units (pawn count per unit)
        internal static ConfigEntry<bool>   FormationReinstantiateOn; // FORMATION axis: after apply, re-instantiate already-spawned units of a repointed type so they reach the new pawn count (fixes the load-race undercount)

        private bool show;
        private Rect winRect = new Rect(60, 60, 520, 420);
        // True while the cursor is over the (open) F8 window, in GUI coordinates — read by Hk_MouseCoverExtend so the
        // game treats our window like one of its own (map input suppressed under it). Written every OnGUI event.
        internal static bool WindowHovered;
        private Vector2 scroll;
        private string atlasFilter = "";   // Dump Atlases: only layers whose name contains this (blank = all)
        private string previewEvent = "";  // F8 audition: Wwise event name to post via Play Event

        private void Awake()
        {
            Log = Logger;

            // --- the config file (auto-written to BepInEx/config/community.humankind.haf.cfg) ---
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
            WonderNativeRows    = Config.Bind("District", "WonderNativeRows", "",
                                  "SPIKE (wip-wonder-affinity): fill a custom Artificial Wonder's EMPTY cell in the game's 'ArtificialWonder' " +
                                  "visual database so the NATIVE wonder affinity renders a completed model. Format: 'WonderName=a,b,c,d;...' " +
                                  "where the guid is an FxEvolverMaterial (vanilla wonder material for a zero-bake proof, or our own baked one).");
            DistrictMainRows    = Config.Bind("District", "DistrictMainRows", "",
                                  "SPIKE (dedicated-visual hybrid): register a DATA-AUTHORED district selector by filling the */District/Main." +
                                  "Level1+Level2 cells for an affinity with our baked CityMapSelector's GUID. Format: 'AffinityName=a,b,c,d;...' " +
                                  "(e.g. DistrictVisualAffinity_Base_Industry=...). The game then resolves + LODs our selector natively.");
            DistrictFootprint = Config.Bind("District", "DistrictFootprint", "",
                                  "RUNTIME footprint choice for a scoped district (DistrictSelectorTile). Grafts the DECALS of a chosen donor selector " +
                                  "onto the reactor's tile — the building stays ours, only the strategic footprint changes. Format: " +
                                  "'ConstructibleDefinitionName=a,b,c,d;...' where the guid is any */District/Main selector (e.g. Base_Industry = " +
                                  "149945011,1306056350,1706429623,-368887441; MissileSilo = -1158439761,1096327552,-1625448046,-477384506). Blank = " +
                                  "keep the footprint baked into the selector. Change it + relaunch to switch footprints, no re-bake. (Note: the strategic " +
                                  "footprint still lazy-builds ~1s on first zoom-out — engine limitation, independent of which footprint.)");
            DistrictFootprintDrop = Config.Bind("District", "DistrictFootprintDrop", "Gravel,CityBricks,Battlement,Destroyed,Dammaged,Damaged",
                                  "SURFACE-TEXTURE filter: comma-separated decal NAME substrings to DROP from the grafted footprint. The default set " +
                                  "removes the gravel + battlement-rubble 'rocks' layers that render at close 3D zoom and TWITCH at the strategic<->3D " +
                                  "zoom boundary (from donors like MissileSilo), leaving only the clean SchematicView footprint. Set BLANK to keep ALL " +
                                  "donor decals (rock texture included), or list your own substrings (case-insensitive). Matches by decal material name.");
            DistrictFootprintMask = Config.Bind("District", "DistrictFootprintMask", "",
                                  "EXPERIMENTAL — UNIQUE footprint: path to a PNG mask (white-on-transparent top-down silhouette of the district's own " +
                                  "layout, e.g. from the model). When set, we build a private 1-entry mask atlas from it, clone the SchematicView output " +
                                  "layer to point its mask atlas at ours, and re-point one of the scoped district's SchematicView decals at it — so the " +
                                  "strategic footprint shows the district's ACTUAL shape instead of a generic donor outline. Blank = off (generic footprint).");
            DistrictFootprintMaskSize = Config.Bind("District", "DistrictFootprintMaskSize", "3.0",
                                  "Tuning for DistrictFootprintMask: the world size (in tile units) of the injected silhouette footprint decal. " +
                                  "Raise if the silhouette is too small on the strategic map, lower if it overflows the hex. Default 3.0.");
            DistrictFootprintMaskRotation = Config.Bind("District", "DistrictFootprintMaskRotation", "0",
                                  "Tuning for DistrictFootprintMask: rotate the footprint decal N degrees CLOCKWISE about the vertical axis. " +
                                  "Negative = counter-clockwise. Default 0.");
            DistrictFootprintMaskCut = Config.Bind("District", "DistrictFootprintMaskCut", "false",
                                  "false (default) = draw a SOLID BLOCK (the decal's full quad — bold, instant). 'true' = CUT the " +
                                  "footprint to the PNG mask's shape (e.g. a circle) — but the SchematicView shader tends to render a " +
                                  "cut shape faintly/sketchily. Toggle to compare.");
            DistrictFootprintMesh = Config.Bind("District", "DistrictFootprintMesh", "false",
                                  "EXPERIMENTAL — MESH footprint: 'true' keeps the district's own 3D building mesh visible when you zoom out to the " +
                                  "strategic map (instead of it fading to a flat decal). Works by zeroing each building element's RenderFeatureSelector " +
                                  "(AlwaysEnabled), so the same geometry renders in every zoom band. The footprint is then the ACTUAL reactor buildings, " +
                                  "solid and shaped — no sketchy decal. Default false.");
            DistrictFootprintMeshBW = Config.Bind("District", "DistrictFootprintMeshBW", "false",
                                  "EXPERIMENTAL — B&W footprint: 'true' makes the persistent MESH footprint render BLACK-AND-WHITE while you're on the " +
                                  "strategic (zoomed-out) map, and full colour up close. Works by binding a greyscale copy of the reactor's albedo whenever " +
                                  "the engine reports the close-up zoom band has faded out (RenderFeatureProvider.ComputeRenderState). Needs " +
                                  "DistrictFootprintMesh=true (there's nothing to grey if the mesh isn't kept at strategic zoom). Default false.");
            DistrictFootprintMeshFlat = Config.Bind("District", "DistrictFootprintMeshFlat", "false",
                                  "EXPERIMENTAL — FLAT footprint: 'true' squashes the persistent mesh footprint FLAT (vertical size -> ~0) while you're on the " +
                                  "strategic (zoomed-out) map, so it reads as a flat reactor-shaped sheet on the ground instead of a 3D model poking up; full " +
                                  "height up close. Same schematic-band signal as the B&W option drives the swap (re-emits the element on the crossover). Needs " +
                                  "DistrictFootprintMesh=true. Default false.");
            DistrictFootprintMeshHideDecal = Config.Bind("District", "DistrictFootprintMeshHideDecal", "true",
                                  "When the MESH footprint is on, the district's own mesh IS the strategic footprint — so the flat DECAL footprint baked " +
                                  "into its selector (inherited from the donor/template it was built from, e.g. a MissileSilo outline) is redundant and shows " +
                                  "THROUGH/beneath the mesh. 'true' (default) drops those footprint decal item(s) so only the mesh reads. Set 'false' to keep " +
                                  "the decal. Needs DistrictFootprintMesh=true.");
            DistrictFootprintMeshFlatHeight = Config.Bind("District", "DistrictFootprintMeshFlatHeight", "0.17",
                                  "Tuning for DistrictFootprintMeshFlat: the flatten HEIGHT = the size.y multiplier applied on the strategic map. ~0.02 is " +
                                  "paper-flat, but the sheet is then coplanar with the ground so its edges drown where the tile's terrain rises over them; up " +
                                  "toward 1.0 is full 3D. The sweet spot reads flat yet still pokes clear of the terrain. Tune it LIVE in the F8 window " +
                                  "(vertical placement is terrain-owned, so this — not a lift — is the lever). Default 0.08.");
            DistrictSelectorTile = Config.Bind("District", "DistrictSelectorTile", "",
                                  "SCOPED dedicated-visual: put a DATA-AUTHORED district selector on ONLY the named district's own tile(s) " +
                                  "(matched by ConstructibleDefinitionName), leaving the shared visual affinity — and every other district using " +
                                  "it — untouched. Unlike DistrictMainRows (which fills the shared affinity cell → hits ALL districts of that " +
                                  "affinity), this overrides just the one tile at runtime and keeps the non-plugin fallback intact. The building " +
                                  "element's output layer is bound automatically. Format: 'ConstructibleDefinitionName=a,b,c,d;...' " +
                                  "(e.g. Extension_Base_BreederReactor=...).");
            DistrictBufferHeadroom = Config.Bind("District", "DistrictBufferHeadroom", 0,
                                  "Extra VERTICES to add to the game's big 'Visual' GPU mesh buffer (the shared building buffer, ~3,000,000 by default) " +
                                  "at startup, so custom district meshes fit even when a built-up late-game city has nearly filled it. 0 = off. " +
                                  "e.g. 1000000 = +~48MB VRAM. Applied once at buffer creation; takes effect on the next launch.");
            DistrictGroundMaterial = Config.Bind("District", "DistrictGroundMaterial", "",
                                  "Force a GROUND MATERIAL (the terrain paint — grass, pavement) under EVERY custom district in the registry, " +
                                  "so a custom wonder gets a maintained field instead of bare terrain. The value is a GroundMaterialDefinition " +
                                  "NAME; set DistrictDebug=true and check the log for '[Ground] GroundMaterialDefinition names (...)' to see the " +
                                  "valid options (pick a grass one). Blank = off (vanilla terrain). Uses the game's own terrain paint, blended.");
            DistrictHexSculpt = Config.Bind("District", "DistrictHexSculpt", "",
                                  "Force a HexagonSculptingDefinition under custom districts — the raised terrain PLATFORM a district carves, " +
                                  "which is also its top-down FOOTPRINT at strategic zoom / in battle. A custom wonder's affinity has none, so it " +
                                  "sits flat with no footprint. The value is a HexagonSculptingDefinition NAME; set DistrictDebug=true and check the " +
                                  "log for '[HexSculpt] HexagonSculptingDefinition names (...)' to see the options. Blank = off. Per-entry field overrides.");
            DistrictMeshDensityBoost = Config.Bind("District", "DistrictMeshDensityBoost", 8,
                                  "Multiplier on a custom district's private-layer PrimitivePerParticleCount, raising the PER-MESH primitive ceiling. " +
                                  "A district mesh renders as sub-particles whose count is hard-clamped at 255 (an 8-bit field): a high-poly composed " +
                                  "model (e.g. a temple + a grove of trees) exceeds it and the excess is silently not drawn. The mesh is fully stored; " +
                                  "only the render clamp bites, so multiplying PPC repacks it under the ceiling with the same GPU work. Default 8 (~8x " +
                                  "headroom). 0/1 = vanilla. Applied per district on our private layer clone only.");

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
            //     Registry-driven and inert without haf_formations.json, so it defaults ON like UniversalInject. ---
            FormationOverrideOn = Config.Bind("Formations", "FormationOverride", true,
                                  "Registry-driven FORMATION override (the Formation Override editor window): reads haf_formations.json, " +
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
                typeof(Hk_DistrictGroundMaterial), // EXPERIMENTAL: force a ground material (grass field) under a custom district
                typeof(Hk_GroundApplyProbe),       // DIAGNOSTIC: log the ground index each district resolves (find the Industry "deadzone")
                typeof(Hk_DistrictHexSculpt),      // EXPERIMENTAL: force hexagon sculpting (raised platform + strategic footprint) under a custom wonder
                typeof(Hk_AnimatedBonePoolHeadroom), // enlarge the shared per-frame animated-bone pool (65,535 vanilla) — the spike-plague fix
                typeof(Hk_PropRegister),           // EXPERIMENTAL: register our prop MeshCollections at AnimationLoad, before pawn resolution (opt-in)
                typeof(Hk_ProjectileOverride),     // EXPERIMENTAL: re-point a unit's Projectile at our baked ProjectileAsset (kamikaze drone) at AnimationLoad (opt-in)
                typeof(Hk_MuzzleRelocate),         // muzzleBone: redirect the muzzle-flash bone lookup (donor weapon socket missing on our renamed rig) to OUR bone (2026-07-24)
                typeof(Hk_FormationPrefabExtend),  // FORMATION axis: grow Formation3DPrefab's dummy pool before the pool clones it, so >9-pawn custom formations fit (2026-07-27)
                typeof(Hk_FormationInstanceExtend),// FORMATION axis: top up a live pooled Formation3D when its definition outgrows it (belt-and-braces for the prefab surgery) (2026-07-27)
                typeof(Hk_FormationSpawnDiag),     // FORMATION axis TEMP diagnostic: log dummies/pawns/health at InstantiatePawns for >9-dummy formations (2026-07-27)
                typeof(Hk_FormationPawnScale),     // FORMATION axis: per-model Scale from the registry link (pawn root localScale -> GPU TRS) (2026-07-28)
                typeof(Hk_SandboxSave), typeof(Hk_SandboxLoad),  // FACING PERSIST: capture each army's FormationAngle on save, restore on load (the standard save has no facing) (2026-08-01)
                typeof(Hk_ArtilleryAimPrep),  // BATTLE TURN: arm the strike's aim overrides + ONE shared release clock before flip/teleport/schedules — keeps anim, sound, smoke, shell in lockstep (2026-08-05)
                typeof(Hk_ArtilleryLaunchPose), // BATTLE TURN: re-capture + re-aim the shell's spawn pose at FIRE time (vanilla captures it pre-pivot; the transform never turns with the eased model) (2026-08-05)
                typeof(Hk_ArtilleryHold),     // BATTLE TURN (verified): MAP BOMBARD — add the striker's remaining turn-ease time to the artillery launch/hit schedules (2026-08-05, docs/Turn-Ease.md)
                typeof(Hk_BombardAnimHold),   // BATTLE TURN (verified): defer the bombard's TeleportToSimpleAttack so the donor muzzle flash + shot SOUND also wait for the turn (2026-08-05)
                typeof(Hk_BattleHullAim),     // BATTLE TURN: arm the aim override for OUR turretless land/ship models when a battle volley is choreographed — vanilla never hull-aims vehicles (2026-08-06)
                typeof(Hk_BattleHoldFire),    // BATTLE TURN experimental (hold=1): hold a BATTLE ranged attack until the turn completes (rotation FSM OR the hull-aim alignment) (2026-08-05)
                typeof(Hk_BattleAttackGate),  // BATTLE TURN experimental (hold=1): dynamic gate on the attack FSM's delay step for battle volleys (2026-08-05)
                typeof(Hk_BattleTurnProbe),   // BATTLE TURN forensics (diag=1): log RotationFSM turn starts (2026-08-05)
                typeof(Hk_BattleTurnStep),    // BATTLE TURN forensics (diag=1): log StepTurning route (animated vs unanimated) + start/end angles (2026-08-05)
                typeof(Hk_MouseCoverExtend),  // F8 window click-through fix: hovering our window counts as the game's own mouse-covered (map stops panning under it) (2026-08-17)
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
            UniversalInject.ConsumePendingReloadRearm();  // main-thread re-arm after an in-session save-reload (Sandbox.Load requested it off-thread); covers BOTH the model + district axes, so it runs regardless of the injection gate below
            DistrictInject.DrainDistrictDestroys();       // main-thread free of the previous session's district runtime clones queued by ResetDistrictSessionState (leak fix); cheap no-op when the queue is empty
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
                UniversalInject.PollRotorTrim();        // live rotor-trim dial (haf_rotortrim.txt): constant BR-slot tilt on donor-clip rotor bones
                UniversalInject.PollTurnEase();         // live turn-ease dial (haf_turnease.txt): eased facing + bank on donor-clip units (spike)
                UniversalInject.PollTerrainHug();       // live terrain-hug dial (haf_hugterrain.txt): fly low over open ground, climb for districts (spike)
                UniversalInject.PollClassScan();        // category turn ease: sample live units for the Hover ability + azimuth turrets (~3s; only while category rates are active)
                DistrictInject.TickDistrictMeshSwap(); // EXPERIMENTAL district: per-frame swap our FxMesh into the live selector's leaf drawers
                DistrictInject.PollRepoDump();         // SPIKE wip-wonder-affinity: one-shot AssetReferenceRepository dump (DistrictDebug-gated)
                DistrictInject.PollWonderRows();       // SPIKE wip-wonder-affinity: fill configured wonder cells in the ArtificialWonder visual DB
                DistrictInject.PollDistrictMainRows(); // SPIKE dedicated-visual (hybrid): register our baked selector in */District/Main for an affinity
                DistrictInject.PollDistrictSelectorTile(); // SCOPED dedicated-visual: put our selector on ONLY the named district's tile (keeps shared affinity + fallback)
                DistrictInject.ProbeAxisGrowth();      // SPIKE dedicated-visual: one-shot — can matrix.Add grow a criteria axis with a NEW value? (DistrictDebug-gated)
                DistrictInject.PollHexSculptDial();     // live dial (haf_hexsculpt.txt): re-carve every sculpted district's platform without a relaunch
            }
            BattleTurn.Poll();                          // live battle-turn dial (haf_battleturn.txt): turn rate + hold-fire for ALL units — independent of model injection, so outside the UniversalInject gate (spike)
            Hk_BombardAnimHold.Tick();                  // replay deferred bombard attack poses once their turn-hold elapses (muzzle flash + shot sound timing)
            if (PersistUnitFacing.Value)
                FacingPersist.Tick();                   // capture each army's facing + restore it after a load (stationary units only). OWN gate — facing is independent of model injection, so turning UniversalInject off must NOT silence it (it has its own save/load hooks + config).
            if (PropRegisterOn.Value)
                UniversalInject.TickPropRegister();     // EXPERIMENTAL props: register our MeshCollections once the AnimationManager exists
            if (FormationOverrideOn.Value)
                FormationOverride.Tick();               // FORMATION axis: retry inject+repoint if the databases weren't up at AnimationLoad
        }

        private void OnGUI()
        {
            WindowHovered = show && winRect.Contains(Event.current.mousePosition);
            if (!show) return;
            // Fixed width: GUILayout.Window otherwise re-measures width from content every repaint, so the label
            // word-wrap "breathed" while dragging — the verdict text visibly reflowing read as instability.
            winRect = GUILayout.Window(GUID.GetHashCode(), winRect, DrawWindow, "Humankind Asset Framework", GUILayout.Width(520));
        }

        private void DrawWindow(int id)
        {
            // FRAGILITY BANNER: HAF binds to the game by reflection, so a game update can silently break a feature. The
            // startup GameBinding report resolves the critical types/members; if any are missing, shout it HERE (top of the
            // window a player actually opens) — not just in the log — with exactly what broke.
            if (GameBinding.HealthMissing > 0)
            {
                var prev = GUI.color; GUI.color = new Color(1f, 0.55f, 0.55f);
                GUILayout.Label($"⚠ GAME BINDING: {GameBinding.HealthSummary}");
                foreach (var d in GameBinding.HealthDetail) GUILayout.Label($"    • {d}");
                GUI.color = prev;
                GUILayout.Space(4);
            }
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Dump Atlases")) UniversalInject.DumpOutputLayerAtlases(atlasFilter);   // Unit Retexture workflow: dump a unit's atlas to paint
                if (GUILayout.Button("Smoke Test")) UniversalInject.RunSmokeTest();   // adopter check: bindings + registry + injection health -> [SmokeTest] PASS/FAIL
            }
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("Dump name filter — atlas/audio (blank = all):", GUILayout.Width(220));
                atlasFilter = GUILayout.TextField(atlasFilter);   // e.g. "Corvette" -> dumps only that unit's layer, not all 600+
            }
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button(UniversalInject.AudioTraceOn ? "Audio Trace: ON" : "Audio Trace: OFF"))
                { UniversalInject.AudioTraceOn = !UniversalInject.AudioTraceOn; UniversalInject.AudioTraceFilter = atlasFilter; }
                if (GUILayout.Button("Dump Sound Catalog")) UniversalInject.DumpSoundCatalog();
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
            // Strategic footprint — flatten-height LIVE OVERRIDE across all scoped districts (per-district values are
            // authored in the District Factory; this is a session-wide quick-tune. Reset returns to per-district values).
            GUILayout.Label("Strategic footprint — flatten height (live override, all scoped districts; 0.02 = flat, 1 = full 3D):");
            using (new GUILayout.HorizontalScope())
            {
                bool overriding = DistrictInject.FlatHeightOverriding();
                float h = DistrictInject.FlatHeightOverrideValue();
                GUILayout.Label(overriding ? $"override = {h:0.00}" : "per-district", GUILayout.Width(110));
                if (GUILayout.Button("-0.05", GUILayout.Width(55))) DistrictInject.NudgeFlatHeight(-0.05f);
                if (GUILayout.Button("-0.01", GUILayout.Width(55))) DistrictInject.NudgeFlatHeight(-0.01f);
                if (GUILayout.Button("+0.01", GUILayout.Width(55))) DistrictInject.NudgeFlatHeight(+0.01f);
                if (GUILayout.Button("+0.05", GUILayout.Width(55))) DistrictInject.NudgeFlatHeight(+0.05f);
                float nv = GUILayout.HorizontalSlider(h, 0.02f, 1f, GUILayout.Width(160));
                if (Mathf.Abs(nv - h) > 0.001f) DistrictInject.SetFlatHeight(nv);
                if (overriding && GUILayout.Button("Reset", GUILayout.Width(55))) DistrictInject.ClearFlatHeightOverride();
            }
            GUILayout.Space(4);
            GUILayout.Label("GPU mesh buffer (live) — Shift+F8 also logs it:");
            foreach (var l in UniversalInject.MeshBudgetLines()) GUILayout.Label(l);
            GUILayout.Space(4);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(320));
            if (Prober.Report.Count == 0)
                GUILayout.Label("Press \"Smoke Test\" to check HAF loaded + injected your mod (result shows here).");
            foreach (var line in Prober.Report)
                GUILayout.Label(line);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }
    }
}
