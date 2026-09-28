// ModelRegistry.cs (ENC editor) — the ENC Model Factory's config store. Writes a JSON file the in-game plugin reads
// to bind each baked model onto its pawn definition at runtime. This EDITOR side uses UnityEngine.JsonUtility to WRITE
// the file (fine in the editor's Mono). IMPORTANT: the game-runtime plugin does NOT read it back with JsonUtility —
// JsonUtility silently returns an EMPTY object in the game's Mono runtime, so the plugin parses with Newtonsoft (its own
// JSON dependency; see UniversalInjectPatch.LoadRegistry). Do not "simplify" the plugin back to JsonUtility — it will
// no-op and inject nothing. Written into the game's BepInEx/config.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// BAKE-TIME atlas mode. Auto = pack multi-material when the model has >1 material, else single texture. Single/Multi force it.
public enum MaterialMode { Auto = 0, Single = 1, Multi = 2 }

// One baked model. Field names MUST match the plugin's reader (JsonUtility binds by field name; extra fields ignored).
[Serializable]
public class ModelDef : Haf.Schema.HafModelSchema
{
    public string modelFile = "";
    public Vector3 rotation;            // rotation offset (deg)
    public float size = 5f;             // world length of the model's longest axis
    // WHERE THIS UNIT'S WATER REALLY IS (2026-09-27, user: "in many cases the water level in game does not
    // correspond correctly with the preview"). `waterLevel` is one number for the pack — the height of the game's
    // water surface above a baked model's origin, calibrated once on a cruiser. It does not hold for every unit: HAF
    // repoints a mesh into a DONOR unit, and the game floats that donor where it floats it, so a ship dialled to sit
    // right in game reads as riding high or low against the standard plane in the preview. This is that unit's
    // correction to the plane, in world units, added to the pack's water level. PREVIEW ONLY: it moves the water the
    // preview draws and the keel/top readout measured against it, never the model. What sinks the model is still
    // `position.z`. Zero means the pack standard holds for this unit, which is the usual case.
    public float waterOffset = 0f;      // PREVIEW ONLY: this unit's correction to the pack's water level (see above)
    public int normalsMode = 1;         // 0 KeepModel, 1 Recalculate, 2 Faceted
    public float smoothingAngle = 20f;
    public int convertGrid = 0;         // GLB->OBJ: 0 = faithful (preserve UV seams), >0 = decimate
    public bool reuseExtracted = false; // reuse existing OBJ/albedo on re-bake (skip re-import), preserving hand-edited textures
    public bool doubleSided = false;    // add a reversed back face to every triangle — fixes single-sided/CAD meshes (e.g. a hovercraft skirt) that render invisible in-game because the engine culls backfaces
    public bool windingFix = false;     // rewind every face OUTWARD (documented CAD winding fix) so single-sided/CAD meshes render single-sided without doubling geometry — preferred over doubleSided for convex hulls (hovercraft, ships)
    public bool multiMesh = false;      // OPT-IN (2026-09-13, the Bremen; ANIMATED bakes too since 2026-09-20): a bake over the 16,320-quad per-fragment ceiling splits into _ModelMesh_B.. chunks the plugin draws as extra fragments. Off = the classic warn-and-clip behavior — a conscious choice: more fragments cost more draw work, and each chunk duplicates its seam vertices in the shared pawn vertex buffer (how much depends on the cut: the frigate measured +26% split static, +1% split animated)
    public bool heightUV = false;       // override UVs with U=length, V=normalized height, so a vertical-gradient albedo maps by height (e.g. black skirt low, grey hull high) regardless of the source/CAD UVs
    public float albedoBrightness = 1f; // BAKE-TIME: multiply the baked atlas RGB (1 = unchanged). >1 lifts a dark skin — the injection path ships FLAT albedo (donor PBR neutralized), so shiny/dark models read muddy in-game. Runtime plugin ignores it (baked into the atlas).
    public float albedoSaturation = 1f; // BAKE-TIME: scale colour vividness around per-pixel luminance (1 = unchanged, 0 = greyscale, >1 = punchier). Fixes a washed-out/desaturated albedo. Baked into the atlas; plugin ignores it.
    public bool keepBlack = false;      // BAKE-TIME (multi-material): default false neutralizes near-black atlas regions to grey (hides UV dead-zones / packing gaps). Tick for a model with an INTENTIONALLY black material (glossy canopy, dark cockpit) so it stays black. Default false = existing behaviour, so old registries are unaffected.
    public MaterialMode materialMode = MaterialMode.Auto;   // BAKE-TIME: Auto = pack an atlas when the model has >1 material (needed for OPEN kit: a towed gun's wheels/legs/barrel), else one texture. Single = force one texture (closed models: tanks/planes). Multi = force the multi-material atlas even if auto-detect misfires. The ANIMATED path previously only did Single — Multi/Auto now bring per-material atlas packing to animated models too.
    public int atlasMaxDim = 512;      // BAKE-TIME: longest side of the baked atlas in px (256/512/1024/2048). Smaller = smaller shipped _Atlas.asset (DXT1). Units are ~80px at map zoom so 512-1024 is ample. 0/absent -> 512 (baker guards it), so old registries are safe.
    public int targetTris = 24000;      // >0 = quadric-decimate the source to ~this many triangles (via Blender) before baking. NO hard per-model cap exists in the engine (verified live: maxMeshTriangleCount ships unlimited) — the budget is the SHARED pawn-layer pool (~1M verts across all loaded model types; see HAF docs/Vertex-Budget.md + the F8 Mesh Budget readout). Default 24000 = a good roster citizen (halves to 12000 under double-sided); go higher for hero units. It's a CEILING (models already under it pass through untouched, never upscaled). 0 = off.
    public string stripParts = "";      // BAKE-TIME (Blender): comma-separated object-name substrings to DELETE from YOUR model before baking — e.g. its own rotor so the donor's spinning rotor shows through, or a crew figure / weapon pod. Mirror of hideMeshes but on the source model, not the donor. Ignored by the runtime plugin. Empty = keep everything.
    public int[] skel = new int[4];     // baked skeleton Amplitude guid {a,b,c,d}
    public int[] atlas = new int[4];    // baked atlas Amplitude guid {a,b,c,d}
    public int[] clip = new int[4];     // ANIMATED only: baked ClipCollection Amplitude guid {a,b,c,d}; static models leave it {0,0,0,0}
    public bool animated = false;       // true = baked from the model's OWN armature + clip (animated path), not the procedural vehicle rig
    public string animClip = "";        // ANIMATED only: name of the clip to bake when the model has several (e.g. "hover"); empty = the assigned/first action
    public string animateBones = "";    // ANIMATED only: comma-separated bone-name prefixes to keep animation on (e.g. "prop,rotor"); empty = keep the whole clip
    public string staticParts = "";     // ANIMATED only (BAKE-TIME, conversion path; canoe finding 2026-07-30): comma-separated MESH- or MATERIAL-name substrings to keep WEIGHTLESS (ride the root bone statically) instead of bone-binding them. For rigid decor hung off a skeleton whose ANIMATED pose frame disagrees with the static node layout (the dug-out canoe's sail: rib bones sit 100+ units from rest at every frame, so binding dragged the sail off the mast; weightless parts stay at their authored position, like the mast itself). Empty = bind everything (mech behaviour, unchanged).
    public bool localNodeAnim = false;  // ANIMATED only (BAKE-TIME, conversion path; canoe finding 2026-07-30): transplant OBJECT-level node animation into LOCAL-DELTA bones — one bone per animated node AT ITS STATIC placement, keyed with only that node's own wiggle (static_basis^-1 @ animated_basis). For models whose motion lives on nodes, not bones (hull rock / log bob / paddle strokes / sail sway), and whose full hierarchy SCATTERS when Blender replays it (the canoe: animated pose frame ≠ static layout, so both the plain clear and deploy_convert's world-space bake fail — one freezes, the other disassembles). Off = node animation is cleared (existing models bake byte-identically).
    public bool animUnitFix = false;    // ANIMATED only (BAKE-TIME, not runtime): tick if the model bakes ~100x too big & floats (a metre->cm FBX unit scale). The baker then measures the FBX at true scale (useFileScale off) + bakes with the unit scale on, so Size = in-game units. Per-model: some rig exports need it, others break with it (the drone bakes correct OFF, the howitzer needs it ON).
    public bool convertRig = false;     // ANIMATED only (BAKE-TIME, not runtime): route the bake through the RAW-RIG CONVERSION (rest-normalize + visual rebake, no-op root collapse, topological bone rename, rotation/scale fold, clean-unit export). Needed for auto-rigged/location-keyed rigs (the Combine soldier); leave OFF for purpose-made rigs (drone, howitzer) — off = the byte-identical legacy pipeline. This flag — not the Rotation field — is the pipeline switch (it used to be 'rotation != 0', which made Rotation a landmine on legacy models).
    public bool autoGroundWheels = false; // RETIRED 2026-09-20 (kept so older pack.json files still round-trip, and it still keys the slim-FBX cache): every animated bake now centres the box on the origin and drops the lowest point to it — the same placement the static path bakes in. Was: sit a rigged VEHICLE on the terrain automatically — the bake drops the model's LOWEST point (tyre contact) to the skeleton origin (lift by −minZ, the same keel→z=0 the static path does), so you never dial the Position offset by hand. SELF-CORRECTING (raw file lifts fully, an already-grounded one lifts ~0 → never double-lifts). Opt-in: only for a vehicle whose lowest point is its ground contact (a flyer/hover model would be pinned down).
    public bool keepTranslations = false; // ANIMATED only (BAKE-TIME, conversion path; 2026-07-25 caterpillar unlock): KEEP genuinely translation-animated bone location curves through the bake — the engine plays RotationTranslation clips (vanilla tank tread shuttle bones prove it); the historical rotation-only strip stays the default for the native-scale trap. Opt-in per model; existing models re-bake byte-identically while OFF.
    public bool deployConvert = false;  // ANIMATED only (BAKE-TIME): run Tools/deploy_convert.py on the model file FIRST — turns a RIGID-MOVING-PARTS source (Sketchfab howitzer/crane/landing-gear: node transforms, no skinning) into a bone-per-part skinned rig the animated bake can consume. The model file should be the RAW original; the baker converts into FactorySource/<res>/deploy_converted.glb (cached on args+source) and bakes THAT. Every knob below is part of the entry — nothing hand-run, fully reproducible. The converted file also carries ready-made role clips (deployed/folded/unfold/fold/recoil).
    public int deployStart = 0;         // deployConvert: source frame where the DEPLOY motion starts (usually 0).
    public int deployEnd = 0;           // deployConvert: source frame where the deploy motion ENDS (fully deployed). REQUIRED (>0) when deployConvert — scrub the raw file in the ▶ picker to find it.
    public string deployStrip = "";     // deployConvert: comma-separated name substrings to DELETE from the source (crew figures, loose shells/props — soft-skinned rigs break the rigid bake). Empty = the tool's M114-proven defaults. Setting this REPLACES the defaults (the canoe's "camera" override needs that); to ADD parts without losing the defaults use deployStripExtra.
    public string deployStripExtra = "";// deployConvert: parts to remove ON TOP of the defaults (built via the Lab's "Also remove" part-picker) — always appended to the effective kill-list, so you never re-type the default crew/prop names. The M114's control hand-wheels live here.
    public string deployReadyFrame = "";// deployConvert: source frame of the FULLY-ELEVATED barrel ("" = don't retarget the barrel). The source often pauses at an aim angle before rising much later; this re-keys barrel/cannon bones to rise over the deploy's back half instead.
    public string deployLegScale = "";  // deployConvert: leg-spread re-key ("" = KEEP THE ORIGINAL LEG ANIMATION VERBATIM — no re-authoring). A number re-keys bones named *leg* as a clean travel→spread interpolation scaled by it (1 = full source spread, 0.5 = half as wide). This is the once-hidden 'voodoo' — now a visible recipe knob.
    public string deployBarrelScale = "";// deployConvert: barrel elevation scale ("" = 1). >1 exaggerates past the source's firing max (axis-angle scaled, safe to extrapolate).
    public string deployRecoil = "";    // deployConvert: "start..end" recoil sub-range IN THE SOURCE clip ("" = no recoil tail). Its kickback is remapped onto the deployed pose as a tail after deployEnd (played by the attack role / fire-on-attack).
    public string deployRecoilStep = "";// deployConvert: recoil source-frame sampling step ("" = 2).
    public string deployRecoilMag = ""; // deployConvert: recoil slide-distance scale ("" = 1 = the source distance; 2 ≈ half the tube).
    public string deployArcR = "";      // deployConvert: FK-arc pivot distance ("" = 400). Larger = straighter recoil slide but more jitter-prone.
    public string deploySlamDeg = "";   // deployConvert: the kick's SLAM PITCH IN DEGREES ("" = fall back to Arc R / 400). States intent directly — the converter derives the arc radius so the rendered in-game peak equals this (Law 5: the arc renders as a tube pitch). ~5 = the subtle legacy dip, 8-12 = clearly visible, 20+ = dramatic.
    public string deploySlamSettle = ""; // deployConvert: the SLAM's recovery duration as a multiple of its rise ("" = 1 = a symmetric snap; 3 = a heavy gun easing back). The rise always follows the source's own slam timing.
    public string deployWheelBones = "";  // deployConvert: comma-separated WHEEL bone/part names (substrings OK) to roll in the 'folded' travel stance ("" = no wheel spin). The converter keys a LINEAR roll about each wheel's axle into the 'folded' role clip — set the Movement clip to folded[1..N]. Bake-time; see docs/Animated-Models.md.
    public string deployWheelAxis = "";   // deployConvert: the wheels' axle axis, X/Y/Z world, or "" = AUTO (each wheel's thinnest skinned extent).
    public string deployWheelFrames = ""; // deployConvert: frames in the folded loop ("" = 15) — Movement clip = folded[1..N].
    public string deployWheelDegrees = "";// deployConvert: degrees the wheels turn over the loop ("" = -360; flip the sign if they roll backwards).
    public string deployRecoilReturn = ""; // deployConvert: the PALINDROME RETURN slowdown ("" = 4). The recoil range should be the SLAM ONLY (the source's post-slam frames are usually reload choreography); the return is the same kick played BACKWARD at this multiple of its duration (4 = quarter-speed glide back into battery). 0 = no return (kick holds; the idle hold snaps the tube forward).
    public string animClipMove = "";    // STATE-DRIVEN only: source clip name for the MOVEMENT state (e.g. "Skel|a_RunN"). Required when animStateDriven; baked into its own ClipCollection (clipMove) sharing the one skeleton.
    public string animClipAfter = "";   // STATE-DRIVEN only: source clip name for the optional AFTER-MOVEMENT one-shot (played once on stopping, then Idle). Empty = stop straight into Idle.
    public string animClipAttack = "";  // STATE-DRIVEN only: source clip name for the optional ATTACK one-shot (played once when the unit ranged-attacks; overrides every other state for its duration). Empty = no attack clip.
    public string animClipCombat = "";  // STATE-DRIVEN only: source clip name for the optional COMBAT-IDLE stance (replaces Idle while the army is locked in a battle; a single-frame stance clip is fine). Empty = normal Idle in battle.
    public string animClipPreMove = ""; // STATE-DRIVEN only: source clip name for the optional PRE-MOVEMENT one-shot (played once when the unit STARTS moving — e.g. a howitzer folding — then the Movement loop). Empty = straight into Movement.
    public string animClipIdleAlt = ""; // STATE-DRIVEN only: optional IDLE-ALT flavor one-shot clip (the tiger's howl) — occasional, on the idleAltInterval cadence, only while plain-idle. "" = none.
    public string animClipIdleAlt2 = "";// STATE-DRIVEN only: optional SECOND idle-alt flavor clip (eat/groom); each firing picks randomly between the two. "" = none.
    public string animClipIdle = "";    // STATE-DRIVEN only: optional IDLE-OVERRIDE clip. When set, the primary Clip (animClip) is only the REFERENCE clip (defines the skeleton's reference pose — use the FULL source motion) and idle plays THIS role instead. REQUIRED for stance idles (a howitzer's deployed hold, e.g. "deploy[179..180]"): a stance baked as the PRIMARY encodes ~identity against its own reference and renders as REST in-game (the "forgot to deploy" trap). Empty = idle plays animClip (characters: a real idle loop like Idle1 is its own valid reference).
    public bool bakeLocked = false;     // BAKE LOCK (2026-07-27, the m114 guard): while true, BOTH windows' Bake buttons are disabled for this entry. Protects an in-game-VERIFIED bake from accidental regeneration when the shared tooling has moved on underneath it (the engine-contract rework changed deploy_convert so much that the m114's next conversion is choreography-DIVERGENT until its migration pass — see docs/Animation-Pitfalls.md "The engine contract"). Unlocking is a deliberate act: untick, bake, RE-VERIFY IN-GAME.
    public string socketBones = "";     // BAKE-TIME (DONOR SOCKETS, 2026-07-24): "DonorName=OurBoneSubstr[@x,y,z];..." — bake EXACT-NAMED zero-weight socket bones (the names the donor's fire events ask for, e.g. "Canon_Up_left=MW_T") so flash/smoke/projectile origin resolve NATIVELY on our rig and follow the parent bone. Socketed models rename with 'A###_' (not 'b###_') so Amplitude's alphabetical sort stays topological. Obsoletes muzzleBone for re-baked models. Re-BAKE to apply.
    public int[] clipMove = new int[4]; // STATE-DRIVEN only: baked MOVEMENT ClipCollection Amplitude guid {a,b,c,d}
    public int[] clipAfter = new int[4];// STATE-DRIVEN only: baked AFTER-MOVEMENT ClipCollection Amplitude guid {a,b,c,d}; {0,0,0,0} = no after clip
    public int[] clipAttack = new int[4];// STATE-DRIVEN only: baked ATTACK ClipCollection Amplitude guid {a,b,c,d}; {0,0,0,0} = no attack clip
    public int[] clipCombat = new int[4];// STATE-DRIVEN only: baked COMBAT-IDLE ClipCollection Amplitude guid {a,b,c,d}; {0,0,0,0} = no combat stance
    public int[] clipPreMove = new int[4];// STATE-DRIVEN only: baked PRE-MOVEMENT ClipCollection Amplitude guid {a,b,c,d}; {0,0,0,0} = no pre-movement clip
    public int[] clipIdle = new int[4]; // STATE-DRIVEN only: baked IDLE-OVERRIDE ClipCollection Amplitude guid {a,b,c,d}; {0,0,0,0} = idle plays the primary clip
    public int[] clipIdleAlt = new int[4];  // STATE-DRIVEN only: baked IDLE-ALT ClipCollection guid — an OCCASIONAL flavor one-shot while plain-idle (the tiger's howl), on the idleAltInterval cadence; {0,0,0,0} = none
    public int[] clipIdleAlt2 = new int[4]; // STATE-DRIVEN only: baked SECOND idle-alt ClipCollection guid (eat/groom); each firing picks randomly between the two; {0,0,0,0} = none
}

// An explicit override of another pack's asset: "this pack intentionally replaces <modId>'s skin on <pawnDescription>."
// RESERVED for HAF multi-mod: the runtime parses + reports it today; ordering/override RESOLUTION is a later increment.
[Serializable]
public class OverrideRef { public string modId = ""; public string pawnDescription = ""; }

// The registry FILE (one HAF pack). The wrapper keys sit BESIDE the existing `models` array — additive, so an older
// bare { "models": [...] } file still loads (JsonUtility fills wrapper defaults). ENC writes itself as the base pack
// (modId "enc", no deps), and the same shape is what a joining modder copies as a template for their own pack.
[Serializable]
class RegistryFile
{
    public int schemaVersion = 1;                                   // HAF schema version this file targets (bump additively)
    public string modId = HafPackageContext.DefaultModId;          // unique pack id — this project's own (an existing pack.json's value wins on load)
    public string module = "";                                     // HK runtime MODULE this pack extends — pack load order follows the game's mod order (docs/Multi-Mod). "" = auto (the pack folder name == module Name by convention); set only to override. Read by UniversalInjectPatch.ParsePack.
    public string moduleGuid = "";                                 // optional explicit HK module GUID (wins over `module`/folder; stable across a retitle). "" = unused.
    public List<string> dependsOn = new List<string>();            // RESERVED: modIds this pack requires (parsed + reported, not yet enforced)
    public List<string> loadAfter = new List<string>();            // RESERVED: modIds this pack must load after (deterministic ordering, not yet enforced)
    public List<OverrideRef> overrides = new List<OverrideRef>();  // RESERVED: explicit cross-pack replacements (no implicit overrides)
    public List<ModelDef> models = new List<ModelDef>();           // the Factory-generated model entries (unchanged)
    public List<UnitScaleRule> unitScales = new List<UnitScaleRule>();   // Resize Lab: runtime scale rules for ANY unit (vanilla included) — no bake, no assets
    public List<EraScaleRow> eraGrid = new List<EraScaleRow>();          // Global Era Lab: unit-era × current-era modifier grid
    public bool eraGridEnabled = true;   // Global Era Lab "apply era ageing" toggle — false keeps the grid authored but inert
                                         // (every cell acts as 1.0 at runtime). Initializer true = a pack saved before this
                                         // field existed stays enabled (JsonUtility runs initializers, absent key keeps them).
    public List<FormationThreshold> formationThresholds = new List<FormationThreshold>();   // Global Era Lab: swap formation as a unit shrinks
    public float waterLevel = 0.16f;   // HAF WATER STANDARD (2026-08-18): the game's water surface height above a naval
                                       // model's origin (mean ~0.05 + wave allowance ~0.11, calibrated in-game: cruiser
                                       // paint line, submarine deck-awash). Every vessel's position Z is calibrated
                                       // against it. Part of the pack CONFIGURATION (versioned, dual-written, backed up);
                                       // no editor UI modifies it — change it HERE, then recalibrate every vessel's Z.
}

// GLOBAL ERA LAB, second table (2026-07-29, user-designed): as an aged unit gets SMALLER, swap its formation — a
// lone Ancient trireme at x0.8 reads as a lost dinghy beside a carrier, while three or five small hulls read as a
// squadron. Rows are {threshold, formation}, ordered by INCREASING threshold, and the FIRST row whose threshold is
// >= the unit's effective scale wins:
//
//   0.3 -> Formation_5   |   0.6 -> Formation_3   |   1.0 -> Formation_1
//   a unit at x0.25 fields 5 hulls, at x0.5 fields 3, at x0.9 stays single.
//
// A unit whose scale is above every threshold keeps its own formation (no row matches = no change). The formation
// name is anything the game or an ENC formation entry defines — the same namespace the Formation Override window
// works in, so a custom 3-hull naval formation authored there can be selected here.
[Serializable]
public class FormationThreshold
{
    public float threshold = 1f;     // effective scale at or below which this formation applies
    public string formation = "";    // formation name (vanilla, or one injected by an ENC formation entry)
    public string note = "";         // free label for the Lab only
}

// GLOBAL ERA LAB row (2026-07-29, user-designed): ONE row per unit era, holding that unit's rescale modifier for
// EVERY current global era — together the rows form a grid, `modifier[unitEra][currentEra]`.
//
// Why a grid and not one value per era: how much a unit should shrink depends on BOTH how old it is and how far
// the world has moved. An Ancient hull in the Contemporary age may want 0.15 while a Medieval one wants 0.4 — a
// single per-era curve can't express that, a grid can. The diagonal (unit era == current era) is a unit rendering
// at exactly its authored size, which is why it defaults to 1.
//
// The grid is 6x6: eras 1..6 (Ancient, Classical, Medieval, Early Modern, Industrial, Contemporary). The engine's
// index has Neolithic at 0 (verified in-game: the index equals the era number players see), but nothing is authored
// for the Neolithic, so era 0 is folded into era 1 on lookup rather than given a row.
//
// SCOPE (user rule): these modifiers only ever multiply a unit that ALREADY has a Resize Lab rule. Nothing else is
// resized. A missing row/cell is 1.0 — the runtime invents no curve, so an unauthored grid changes nothing.
[Serializable]
public class EraScaleRow
{
    public int unitEra = 0;                           // the era the unit belongs to (row)
    public List<float> scales = new List<float>();    // rescale modifier per CURRENT era (column), index = era index
    public string note = "";                          // free label for the Lab only
}

// RESIZE LAB rule (2026-07-28, user-designed): a runtime scale applied to every pawn whose PRESENTATION
// DEFINITION name CONTAINS `match` (case-insensitive). All matching rules MULTIPLY — the two-stage model:
// a unit's TRUE-SIZE correction ("ManOWar" -> 0.7) times its ERA modifier ("Era4_" -> 0.9). Runtime-only:
// the plugin applies it at pawn spawn (ObjectSpace.Scale); relaunch to see changes, nothing is baked.
[Serializable]
public class UnitScaleRule
{
    public string match = "";     // substring of the pawn definition name (e.g. "Era4_Common_ManOWar_01")
    public float scale = 1f;      // the unit's size IN ITS OWN ERA; 1 = no change
    public int era = 0;           // the unit's own era, which the Global Era Lab grid ages it from. 0 = auto-detect
                                  // from the definition name ("Era4_Common_ManOWar_01" -> 4); set it explicitly for
                                  // a unit whose name carries no era token (a custom/modded definition).
    public float trueSize = 0f;   // reserved (0 = unused): the unit's REAL-WORLD size in metres, for a future
                                  // reference-size version of era anchoring (enter dimensions instead of factors).
    public string note = "";      // free label for the Lab list only
}

public static class ModelRegistry
{
    // Last-resort fallback if Steam auto-detection finds nothing (e.g. non-default install Steam can't report).
    const string FallbackConfigDir = @"C:\Program Files (x86)\Steam\steamapps\common\Humankind\BepInEx\config";

    // Where the plugin reads the registry from: an explicit user override wins; otherwise auto-detect the Humankind
    // install via Steam's library config; otherwise the fallback. The manual override is the escape hatch — the
    // Factory window exposes it so an adopter with a weird layout can point it by hand.
    public static string ConfigDir
    {
        get
        {
            var over = ConfigDirOverride;
            if (!string.IsNullOrWhiteSpace(over)) return over;
            return AutoDetectConfigDir() ?? FallbackConfigDir;
        }
    }
    // Neutral key, with the historical "ENC.bepinexConfig" still read so an existing setup keeps working.
    public static string ConfigDirOverride
    {
        get
        {
            var v = EditorPrefs.GetString("HAF.BepInExConfig", "");
            return !string.IsNullOrEmpty(v) ? v : EditorPrefs.GetString("ENC.bepinexConfig", "");
        }
        set => EditorPrefs.SetString("HAF.BepInExConfig", value ?? "");
    }
    // ENC is now a self-contained HAF SUBDIR PACK (2026-07-24): it ships as ONE directory (pack.json + sounds/ + skins/)
    // so its registry AND file-assets are publishable, instead of loose in the shared BepInEx/config. PackLiveDir = what
    // the running game reads (deployed under haf_packs/); PackRepoDir = the git-tracked source of truth in this project.
    // The editor dual-writes both, exactly as it used to dual-write the live registry + the project backup.
    // The pack is THIS PROJECT'S (HafPackageContext.PackName), never a hardcoded one. ConfigDir is the GAME's
    // config — machine-global — so a hardcoded "ENCReload" here made the tools in any other project on the same
    // machine read (and try to re-bake) ENC's deployed pack. A guest project now gets its own, initially empty.
    public static string PackLiveDir => Path.Combine(ConfigDir, "haf_packs", HafPackageContext.PackName);
    public static string PackRepoDir => Path.Combine(Application.dataPath, "Pack", HafPackageContext.PackName);
    public static string RegistryPath => Path.Combine(PackLiveDir, "pack.json");

    // ---- zero-config game-path discovery (mirrors the Blender/glbconv self-location) ----

    // First Humankind install found across all Steam libraries -> its BepInEx/config. Returns null if not found.
    public static string AutoDetectConfigDir()
    {
        try
        {
            foreach (var hk in HumankindInstallDirs())
                return Path.Combine(hk, "BepInEx", "config");   // config may not exist until BepInEx runs once
        }
        catch { }
        return null;
    }

    static IEnumerable<string> HumankindInstallDirs()
    {
        foreach (var lib in SteamLibraries())
        {
            string hk = Path.Combine(lib, "steamapps", "common", "Humankind");
            if (Directory.Exists(hk)) yield return hk;
        }
    }

    static IEnumerable<string> SteamLibraries()
    {
        string steam = SteamPath();
        if (string.IsNullOrEmpty(steam)) yield break;
        yield return steam;   // the base Steam dir is itself a library
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    static string SteamPath()
    {
        // Probe the standard Steam install locations. Games on OTHER drives are still found — libraryfolders.vdf
        // (inside this base install) lists every library. Steam installed to a truly custom folder is the one case
        // this misses; that's what the Factory window's manual Override is for. (Deliberately no registry lookup:
        // Microsoft.Win32.Registry isn't referenced under Unity's default .NET Standard API level.)
        foreach (var c in new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        })
            if (!string.IsNullOrEmpty(c) && Directory.Exists(c)) return c;
        return null;
    }

    // Set when the last Load() found a registry file it couldn't parse. Save() refuses to run while it's
    // set, so a corrupt / half-edited registry is never silently overwritten with a fresh (empty) list —
    // which would wipe every baked model's settings.
    static bool lastLoadCorrupt;
    // …and set when the last Load() could not READ the source at all - another program had it open, or was replacing
    // it (review of PR #100, P1). NOT corruption: nothing is wrong with the file and nothing needs recovering, so it
    // never raises the recovery banner, whose "Restore last commit" is a `git checkout` that discards uncommitted work.
    // But the list that Load() returned is empty for want of a read, so Save() refuses while it is set, exactly as for
    // corruption - an Upsert onto that list would write one model over the whole pack. Cleared by the next Load().
    static bool lastLoadLocked;

    // Set true once THIS session's Load() has observed the on-disk registry, so the session-static
    // UnitScales/EraGrid/FormationThresholds below reflect reality. A domain reload (recompile) resets it to false
    // AND empties those statics — so a Save() reached before a fresh Load() must NOT write the empty statics (that
    // silently wiped all Resize-Lab / Era-Lab data). While false, Save() preserves those arrays from the on-disk file.
    static bool loaded;

    // …and WHICH version of the era settings a window copied out of them is the WINDOW's to keep (StaticsVersion,
    // outside review of PR #100, fourth round: a session-wide token was advanced by any window's Load() while the Era
    // Lab still showed the grid it had copied earlier). EraSettingsVersion is the print of the session's era settings
    // at this instant - read right after a Load(), the version of exactly what a window copies out of them next; ""
    // when this session never loaded them.
    [Serializable] class EraSnapshot { public List<EraScaleRow> eraGrid; public bool eraGridEnabled; public List<FormationThreshold> formationThresholds; }
    static string EraPrint(List<EraScaleRow> grid, bool gridEnabled, List<FormationThreshold> thresholds) =>
        Fingerprint(JsonUtility.ToJson(new EraSnapshot {
            eraGrid = grid ?? new List<EraScaleRow>(), eraGridEnabled = gridEnabled,
            formationThresholds = thresholds ?? new List<FormationThreshold>() }));
    static string EraPrint(RegistryFile f) => EraPrint(f.eraGrid, f.eraGridEnabled, f.formationThresholds);
    public static string EraSettingsVersion => loaded ? EraPrint(EraGrid, EraGridEnabled, FormationThresholds) : "";

    // RESIZE LAB rules — the registry file's `unitScales` array, captured at every Load and written back on
    // every Save (same session-static pattern as the pack header fields). The Lab edits this list directly.
    public static List<UnitScaleRule> UnitScales = new List<UnitScaleRule>();
    public static float WaterLevel = 0.16f;   // see RegistryFile.waterLevel — refreshed on every Load(); read-only in the editor

    // GLOBAL ERA LAB grid — the registry file's `eraGrid` array, same capture-on-Load / write-on-Save pattern.
    public static List<EraScaleRow> EraGrid = new List<EraScaleRow>();
    public static bool EraGridEnabled = true;   // registry `eraGridEnabled` — same pattern; false = grid kept but ignored at runtime

    // GLOBAL ERA LAB formation thresholds — same capture-on-Load / write-on-Save pattern.
    public static List<FormationThreshold> FormationThresholds = new List<FormationThreshold>();

    // PACK WRAPPER METADATA, captured at Load like the arrays above (2026-08-23). No window edits these — Save()
    // already preserves them from the on-disk file — but "Validate pack" now needs to READ them: the shared rule core
    // gained wrapper rules (modId / schemaVersion / dependsOn / loadAfter / overrides), and until now the only place
    // a wrapper mistake surfaced was the player's haf_load_report.txt, which is the one place the author never looks.
    public static string PackModId = "";
    public static int PackSchemaVersion;
    public static List<string> PackDependsOn = new List<string>();
    public static List<string> PackLoadAfter = new List<string>();
    public static List<OverrideRef> PackOverrides = new List<OverrideRef>();

    static void CaptureWrapper(RegistryFile d)
    {
        PackModId = d?.modId ?? "";
        PackSchemaVersion = d?.schemaVersion ?? 0;
        PackDependsOn = d?.dependsOn ?? new List<string>();
        PackLoadAfter = d?.loadAfter ?? new List<string>();
        PackOverrides = d?.overrides ?? new List<OverrideRef>();
    }

    // The git-tracked SOURCE OF TRUTH: the pack's pack.json in the repo (Assets/Pack/ENCReload). Written on every Save,
    // it survives a game reinstall / Steam "verify files" wiping BepInEx/config, gives version history in git, and Load()
    // auto-restores the live pack from it if the game copy ever goes missing. (Was Assets/Databases/haf_models.backup.json
    // when ENC was the loose base pack — now the whole pack ships as one directory.)
    public static string ProjectBackupPath => Path.Combine(PackRepoDir, "pack.json");
    // THE COLLAPSE (2026-08-19, backlog #3 closed): pack.json is ONE file now. The git-tracked PROJECT file is
    // THE registry — the editor reads and writes only it. The DEPLOYED copy under BepInEx/config is a BUILD
    // ARTIFACT, regenerated on every Save (and recreated on Load if missing), exactly like the deployed DLLs:
    // derived, never edited. Hand-edits to the deployed file are detected and warned about on Load, and
    // overwritten by the next Save. (Historically the deployed copy was authoritative and the project file the
    // dual-written shadow — the split surprised every external tool and cost a day of coherence drills.)
    public static string SourcePath => ProjectBackupPath;   // the honest name for what it now is

    const string PrefCollapsed = "HAF.Registry.SingleSource";   // one-time per-machine migration marker
    static bool artifactDriftWarned;   // warn once per domain load, not per Load() call (RefreshList polls)
    static bool artifactReadWarned;    // the deployed copy could not even be READ to compare (an exclusive holder) — said once, not per poll

    // One-time migration: until the marker is set, the DEPLOYED copy is still the historical authority — adopt
    // it into the project file if they differ (covers a machine whose last session predates the collapse).
    static void MigrateToSingleSourceOnce()
    {
        if (EditorPrefs.GetBool(PrefCollapsed, false)) return;
        try
        {
            if (File.Exists(RegistryPath))
            {
                string dep = File.ReadAllText(RegistryPath);
                if (!File.Exists(SourcePath) || File.ReadAllText(SourcePath) != dep)
                {
                    Directory.CreateDirectory(PackRepoDir);
                    File.WriteAllText(SourcePath, dep);
                    Debug.Log("[Factory] registry collapse migration: adopted the deployed pack.json into the project source (the deployed copy was authoritative until 2026-08-19; from now on it is a build artifact).");
                }
            }
            EditorPrefs.SetBool(PrefCollapsed, true);
        }
        catch (Exception e) { Debug.LogWarning("[Factory] registry collapse migration failed (will retry next load): " + e.Message); }
    }

    // WHAT THE EDITOR ITSELF LAST WROTE (review of #100). The empty-source guard in Save() infers "emptied outside the
    // editor" from a source with no models beside a deployed copy with some — but the editor empties the source itself
    // (Remove() of the last model), and if the deploy of THAT write failed (the game held the file) the two copies
    // disagree for an innocent reason, and every later save refused. The source's fingerprint, recorded on every write,
    // tells the editor's own empty source from a hand-emptied one; the PENDING fingerprint marks a deploy that failed,
    // so the next Load() can finish it. Per machine (EditorPrefs), like the deployed copy it describes.
    static string PrefLastWrite => "HAF.Registry.LastWrite|" + SourcePath;
    static string PrefPendingDeploy => "HAF.Registry.PendingDeploy|" + SourcePath;
    static bool pendingRetryWarned;   // a locked artifact fails the retry on every Load() poll — say so once
    static double lastPendingAttempt = -1; static int pendingFailures;   // RegistryRules.PendingRetryDue: 2, 4, 8, 16 s, then every 30 s

    static string Fingerprint(string json)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(RegistryRules.FingerprintText(json))));   // CRLF/LF-blind: git rewriting the source is not a hand-emptying
    }

    // Atomic write of the deployed ARTIFACT; throws on failure (the callers word the consequence).
    static void WriteArtifact(string json)
    {
        Directory.CreateDirectory(PackLiveDir);
        var tmp = RegistryPath + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            if (File.Exists(RegistryPath)) File.Replace(tmp, RegistryPath, null);
            else File.Move(tmp, RegistryPath);
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    // Keep the deployed ARTIFACT in step: recreate it when missing (fresh/reinstalled game), FINISH a deploy that a
    // Save() couldn't complete, and warn ONCE when it was hand-edited (the next Save overwrites it — the old habit
    // points at the wrong file now).
    static void SyncArtifact(string sourceJson)
    {
        try
        {
            if (!File.Exists(RegistryPath))
            {
                Directory.CreateDirectory(PackLiveDir);
                File.WriteAllText(RegistryPath, sourceJson);
                EditorPrefs.DeleteKey(PrefPendingDeploy);
                Debug.Log($"[Factory] deployed registry artifact recreated from the project source → {RegistryPath}");
                return;
            }
            string pending = EditorPrefs.GetString(PrefPendingDeploy, "");
            if (pending != "")
            {
                // NOT ON EVERY POLL (review of PR #100, P4): Load() runs whenever a window refreshes, and a game holding
                // the file failed the retry every time - a full read of the deployed copy and a full temp write into the
                // game's config folder, per repaint. Until the next attempt is due, the pending deploy explains the
                // difference and there is nothing to check.
                double now = EditorApplication.timeSinceStartup;
                if (!RegistryRules.PendingRetryDue(now, lastPendingAttempt, pendingFailures)) return;
                lastPendingAttempt = now;
                string deployedNow;
                try { deployedNow = File.ReadAllText(RegistryPath); }
                catch (Exception re)
                {
                    // AN EXCLUSIVE HOLDER denies the READ as well as the replace (measured: a plain reader, share Read or
                    // ReadWrite, blocks only the replace; share None - a scanner, a sync tool - blocks both). That is a failed
                    // attempt like any other: count it, back off, say it once. Outside this catch it reached the outer
                    // handler instead, which logged every attempt and never grew the delay past 2 s (review of PR #100).
                    pendingFailures++;
                    if (!pendingRetryWarned) { pendingRetryWarned = true; Debug.LogWarning($"[Factory] the deployed copy is still out of date and can't even be read right now ({re.Message}) — another program holds it exclusively; retrying, every 2 s at first, backing off to every 30 s."); }
                    return;
                }
                if (deployedNow == sourceJson) { EditorPrefs.DeleteKey(PrefPendingDeploy); pendingFailures = 0; lastPendingAttempt = -1; }   // it caught up
                else if (pending == Fingerprint(sourceJson))
                {
                    // The source is still exactly what the failed Save wrote: this difference is OUR unfinished deploy,
                    // not a hand-edit — finish it.
                    try
                    {
                        WriteArtifact(sourceJson);
                        EditorPrefs.DeleteKey(PrefPendingDeploy);
                        pendingRetryWarned = false; pendingFailures = 0; lastPendingAttempt = -1;
                        Debug.Log($"[Factory] finished a deploy an earlier save couldn't complete → {RegistryPath}");
                    }
                    catch (Exception re)
                    {
                        pendingFailures++;
                        if (!pendingRetryWarned) { pendingRetryWarned = true; Debug.LogWarning($"[Factory] the deployed copy is still out of date (the last save couldn't refresh it: {re.Message}); retrying — every 2 s at first, backing off to every 30 s — until it can."); }
                    }
                    return;
                }
                else { EditorPrefs.DeleteKey(PrefPendingDeploy); pendingFailures = 0; lastPendingAttempt = -1; }   // the source changed since (git, a hand-edit): that deploy is moot
            }
            string deployedText = null;
            if (!artifactDriftWarned)
            {
                // the same exclusive holder, with no deploy pending: a read that fails is said once, not on every poll
                try { deployedText = File.ReadAllText(RegistryPath); artifactReadWarned = false; }
                catch (Exception re)
                {
                    if (!artifactReadWarned) { artifactReadWarned = true; Debug.LogWarning($"[Factory] the deployed copy '{RegistryPath}' can't be read to compare it with the source ({re.Message}) — another program holds it exclusively. Checked again on later loads; said once."); }
                    return;
                }
            }
            if (!artifactDriftWarned && deployedText != sourceJson)
            {
                artifactDriftWarned = true;
                Debug.LogWarning("[Factory] the DEPLOYED pack.json differs from the project source. Since the 2026-08-19 collapse the deployed copy is a BUILD ARTIFACT — a hand-edit there is ignored by the editor and overwritten on the next Save. Edit the source instead: " + SourcePath);
            }
        }
        catch (Exception e) { Debug.LogWarning("[Factory] deployed-artifact sync: " + e.Message); }
    }

    // Keep the registry in a STABLE alphabetical order (by resourceName, case-insensitive) everywhere it's read or
    // written, so the Factory dropdown AND both config files (the live haf_models.json + the git-tracked backup) list
    // models the same way every time. Without this the order was insertion/re-serialization order, so a re-bake could
    // shuffle entries — an annoying dropdown that keeps changing, and a giant meaningless backup.json diff each commit.
    // Ordering is display/serialization only; the runtime plugin matches by pawnDescription, so order never affects it.
    static List<ModelDef> SortByName(List<ModelDef> list)
    {
        list?.Sort((a, b) => string.Compare(a?.resourceName, b?.resourceName, StringComparison.OrdinalIgnoreCase));
        return list ?? new List<ModelDef>();
    }

    // LEGACY-CONTRACT MIGRATION (2026-07-18): the conversion pipeline used to be triggered by 'rotation != 0' on an
    // animated entry (the soldier shipped with the 360,0,0 identity trick). That made Rotation a hidden pipeline
    // switch — editing it on a legacy model silently rerouted the bake. The explicit convertRig flag replaced it.
    // ONE-SHOT: it only runs when the FILE predates the flag (no "convertRig" key anywhere in the JSON — every
    // post-refactor Save writes the key on every entry, so its absence is a reliable pre-refactor marker). Gating on
    // the file, not the entry, is what makes a user's deliberate choice stick: an always-on migration would force
    // convertRig back ON every Load for any animated entry with a rotation set, making the unticked state
    // impossible to keep (review 2026-07-19).
    static List<ModelDef> Migrate(List<ModelDef> list, string rawJson)
    {
        if (rawJson != null && rawJson.Contains("\"convertRig\"")) return list;   // post-refactor file — user intent is explicit, keep it
        foreach (var m in list)
            if (m != null && m.animated && !m.convertRig && m.rotation != Vector3.zero)
            {
                m.convertRig = true;
                Debug.Log($"[Factory] {m.resourceName}: migrated to the explicit 'Convert raw rig' flag (was implied by rotation {m.rotation}). The rotation value is unchanged.");
            }
        return list;
    }

    public static List<ModelDef> Load()
    {
        try
        {
            lastLoadLocked = false;   // every Load() decides it afresh (see the field)
            // `loaded` is set BELOW, only where this Load actually refreshed the statics from a file (review of PR #100).
            // Set up here, as it was, a read that FAILED marked the post-reload statics authoritative while they were
            // still empty — harmless while every save waited on the failure flags, and a wipe of the pack's era grid and
            // scale rules once SaveStatics stopped waiting on the lock flag.
            MigrateToSingleSourceOnce();
            if (!File.Exists(SourcePath))
            {
                // Don't declare the registry dead on ONE glance: an external editor's save-by-rename (temp write →
                // delete → rename) leaves a milliseconds-wide window where the file doesn't exist. Re-check briefly.
                System.Threading.Thread.Sleep(250);
                if (!File.Exists(SourcePath))
                {
                    lastLoadCorrupt = false; corruptLogged = false;
                    // Project source gone (a fresh clone that predates the pack, or a hand-deletion) but a deployed
                    // artifact exists: ADOPT it — it's the only surviving copy of the data.
                    if (File.Exists(RegistryPath))
                    {
                        try
                        {
                            var dep = File.ReadAllText(RegistryPath);
                            // ANY readable pack, not only one with models (review of #100): an empty pack's modId,
                            // dependencies, scale rules and era settings live only here too, and a save onto defaults
                            // would erase them. The shape rule keeps a broken `{}` from passing for a pack.
                            var d = ParseRegistry(dep, RegistryPath, out var depWhy);
                            if (d == null) Debug.LogWarning($"[Factory] the project registry source is missing and the deployed artifact '{RegistryPath}' is unreadable ({depWhy}) — nothing to adopt; saves refuse until one of them is readable.");
                            else
                            {
                                Directory.CreateDirectory(PackRepoDir);
                                File.WriteAllText(SourcePath, dep);
                                Debug.Log($"[Factory] project registry source was missing — adopted the deployed artifact ({d.models.Count} model(s), {RegistryPath}).");
                                UnitScales = d.unitScales ?? new List<UnitScaleRule>();
                                WaterLevel = d.waterLevel;
                                CaptureWrapper(d);
                                EraGrid = d.eraGrid ?? new List<EraScaleRow>();
                                EraGridEnabled = d.eraGridEnabled;
                                FormationThresholds = d.formationThresholds ?? new List<FormationThreshold>();
                                loaded = true;   // the statics now reflect the adopted pack
                                return Migrate(SortByName(d.models), dep);
                            }
                        }
                        catch (Exception be) { Debug.LogWarning($"[Factory] the deployed artifact '{RegistryPath}' is unreadable ({be.Message}) — treating as absent."); }
                    }
                    else loaded = true;   // no source and no deployed copy: a first-ever pack, and the statics in memory are its truth
                    return new List<ModelDef>();
                }
            }
            string json;
            try { json = File.ReadAllText(SourcePath); }
            catch (Exception re) when (RegistryRules.ClassifyReadFailure(re) != RegistryRules.ReadFailure.NotARead)
            {
                // could not READ it: see lastLoadLocked. Not the corrupt path below — and it SUPERSEDES an earlier
                // corrupt verdict (outside review of PR #100, second round): that verdict was about bytes this Load could
                // not see, the file may have been repaired since, and leaving lastLoadCorrupt up kept "Restore last
                // commit" — a git checkout — one click from discarding that uncommitted repair. Clearing it frees no save:
                // Load-fed saves refuse on lastLoadLocked, and SaveStatics judges the bytes with its own read.
                // corruptLogged stays: a file still broken when it can be read again is not logged and copied twice.
                lastLoadLocked = true; lastLoadCorrupt = false;
                LastLockDetail = re.Message; LastLockAdvice = RegistryRules.ReadFailureAdvice(RegistryRules.ClassifyReadFailure(re));
                return new List<ModelDef>();   // a Load-fed save refuses while lastLoadLocked, so this empty list can never be written
            }
            var data = ParseSource(json, out var why);
            if (data == null) throw new SourceUnreadable(why);   // same verdict Save() reaches — not "a pack with no models"
            lastLoadCorrupt = false; corruptLogged = false;
            UnitScales = data?.unitScales ?? new List<UnitScaleRule>();
            WaterLevel = data != null ? data.waterLevel : 0.16f;
            CaptureWrapper(data);
            EraGrid = data?.eraGrid ?? new List<EraScaleRow>();
            EraGridEnabled = data == null || data.eraGridEnabled;
            FormationThresholds = data?.formationThresholds ?? new List<FormationThreshold>();
            loaded = true;   // the statics now reflect the file
            SyncArtifact(json);   // deployed copy recreated if missing; hand-edit there warned about once
            return Migrate(SortByName(data?.models ?? new List<ModelDef>()), json);
        }
        catch (Exception e)
        {
            // The source exists but won't parse (a hand-edit typo, a half-written file). Preserve it and flag
            // it so Save() won't clobber it and lose everything. PINPOINT the fault (2026-08-19, user request):
            // JsonUtility's exceptions carry no location — re-parse with Newtonsoft purely for diagnosis, whose
            // JsonReaderException names the exact line and column of the missing comma/bracket.
            lastLoadCorrupt = true;
            LastCorruptDetail = e is SourceUnreadable ? e.Message : Pinpoint(SourcePath) ?? e.Message;
            // LOG ONCE per corruption (drill finding 2026-08-19: every window polls Load(), so this line spammed
            // the Console dozens of times for one broken file — the red banner is the persistent surface, the log
            // is the event record). Reset when the corruption clears (successful load or recovery).
            if (!corruptLogged)
            {
                corruptLogged = true;
                string keep = SourcePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";   // timestamped: a second corruption never overwrites the evidence of the first
                try { File.Copy(SourcePath, keep, true); } catch { }
                Debug.LogError($"[Factory] registry source '{SourcePath}' is unreadable — {LastCorruptDetail}. " +
                               $"Preserved as '{Path.GetFileName(keep)}'. The Model Factory shows one-click recovery " +
                               "(restore the last deploy, or the last git commit). Baking won't save until recovered, to avoid wiping your models.");
            }
            return new List<ModelDef>();
        }
    }

    // ---- CORRUPT-SOURCE RECOVERY (2026-08-19, user design: "not only a try/catch but recovery functionality") ----
    public static bool LastLoadCorrupt => lastLoadCorrupt;
    public static bool LastLoadLocked => lastLoadLocked;   // the Factory's plain warning — no recovery buttons (see the field)
    public static string LastLockDetail = "";
    public static string LastLockAdvice = "";   // RegistryRules.ReadFailureAdvice — a lock and access denied are not equally temporary
    public static string LastCorruptDetail = "";
    static bool corruptLogged;   // one Console error per corruption, not per Load() poll (drill finding)

    // Newtonsoft re-parse purely for DIAGNOSIS: its reader exception names the line/column JsonUtility hides.
    static string Pinpoint(string path)
    {
        try { Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)); return "JsonUtility rejected it but Newtonsoft parses it (structure beyond JsonUtility's subset?)"; }
        catch (Newtonsoft.Json.JsonReaderException jre) { return $"line {jre.LineNumber}, position {jre.LinePosition}: {jre.Message}"; }
        catch (Exception ex) { return ex.Message; }
    }

    // THE SOURCE'S VERDICT, shared by Load() and Save() so they never disagree about the same bytes. Null + why =
    // unreadable. JsonUtility can't tell `{}` from `"models": []` (RegistryFile.models defaults to an empty list), so
    // an empty result must show the key in the raw text: every file the editor writes carries it, and a source
    // without one is a broken edit, not an empty pack (review of PR #100: `{}` wiped every model via SaveStatics).
    // Newtonsoft only runs on an empty result, so the polled Load() of a real pack pays nothing for it.
    static RegistryFile ParseSource(string json, out string why) => ParseRegistry(json, SourcePath, out why);

    static RegistryFile ParseRegistry(string json, string path, out string why)
    {
        why = null;
        RegistryFile f;
        try { f = JsonUtility.FromJson<RegistryFile>(json); }
        catch (Exception e) { why = Pinpoint(path) ?? e.Message; return null; }
        if (f == null) { why = "the file is empty or not a JSON object"; return null; }
        if (f.models == null || f.models.Count == 0)
        {
            if (!RegistryRules.HasModelsArray(json)) { why = "it has no \"models\" array (every pack.json carries one, even an empty pack)"; return null; }
            f.models = f.models ?? new List<ModelDef>();
        }
        return f;
    }

    // A verdict ParseSource already worded — Load()'s catch must not replace it with Pinpoint's "Newtonsoft parses it".
    class SourceUnreadable : Exception { public SourceUnreadable(string why) : base(why) { } }

    // Both recovery paths share the same safety contract: the candidate is VALIDATED (must parse and hold >=1
    // model) BEFORE it overwrites the source; the corrupt file is already preserved timestamped; success clears
    // the Save lock. Returns a status line for the banner/Console.
    static string RecoverSourceFrom(string candidateJson, string label)
    {
        try
        {
            var r = JsonUtility.FromJson<RegistryFile>(candidateJson);
            if (r?.models == null || r.models.Count == 0) return $"⚠ recovery from {label} REFUSED: candidate holds no models (nothing was overwritten).";
            Directory.CreateDirectory(PackRepoDir);
            var tmp = SourcePath + ".tmp";
            File.WriteAllText(tmp, candidateJson);
            if (File.Exists(SourcePath)) File.Replace(tmp, SourcePath, null); else File.Move(tmp, SourcePath);
            lastLoadCorrupt = false; LastCorruptDetail = ""; corruptLogged = false;
            AssetDatabase.Refresh();
            return $"Recovered {r.models.Count} model(s) from {label}. The corrupt copy is preserved beside the source for hand-merging.";
        }
        catch (Exception e) { return $"⚠ recovery from {label} FAILED: {e.Message} (source untouched)."; }
    }

    // The LAST GOOD DEPLOY — usually the freshest valid copy (every Save refreshed it), and needs no git.
    public static string RecoverFromArtifact()
    {
        if (!File.Exists(RegistryPath)) return "⚠ no deployed artifact exists to recover from.";
        try { return RecoverSourceFrom(File.ReadAllText(RegistryPath), "the deployed artifact (last good deploy)"); }
        catch (Exception e) { return "⚠ could not read the deployed artifact: " + e.Message; }
    }

    // The last COMMITTED version via git (the source is git-tracked — that was the point of the collapse).
    public static string RecoverFromGit()
    {
        try
        {
            string projRoot = Directory.GetParent(Application.dataPath).FullName;
            string rel = "Assets/Pack/" + HafPackageContext.PackName + "/pack.json";
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"-C \"{projRoot}\" checkout -- \"{rel}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                if (p.ExitCode != 0) return "⚠ git recovery FAILED: " + (string.IsNullOrWhiteSpace(err) ? ("exit " + p.ExitCode) : err.Trim());
            }
            // git rewrote the file on disk — validate it exactly like any other candidate before declaring victory
            return RecoverSourceFrom(File.ReadAllText(SourcePath), "git (last committed version)");
        }
        catch (Exception e) { return "⚠ git recovery FAILED: " + e.Message + " (is git installed?)"; }
    }

    // Returns true if the registry was written. False = nothing was saved (corrupt-guard tripped, or the atomic write
    // hit a transient lock) — the caller should surface that instead of assuming success.
    public static bool Save(List<ModelDef> models) => Save(models, null);

    // What SaveStatics writes: the Era Lab's own settings, and the version of them that window holds.
    sealed class EraEdit { public List<EraScaleRow> grid; public bool enabled; public List<FormationThreshold> thresholds; public StaticsVersion held; }

    // era != null = SaveStatics: write the models exactly as THIS read of the source holds them (keepDiskModels). One
    // read serves both the merge base and the kept models, so a file that turns unreadable between two reads can't
    // slip through.
    static bool Save(List<ModelDef> models, EraEdit era)
    {
        bool keepDiskModels = era != null;
        if (lastLoadCorrupt)
        {
            Debug.LogError("[Factory] not saving: the existing registry was unreadable (see the .corrupt.json backup). " +
                           "Fix or delete it and press Refresh first — refusing to overwrite it and lose your models.");
            return false;
        }
        if (lastLoadLocked && !keepDiskModels)
        {
            // A save fed by Load() writes the list that Load returned — empty, for want of a read. SaveStatics is not fed
            // by Load: it takes its models from its OWN read just below, which judges the bytes as they are now, so a
            // lock flag left by an earlier poll must not hold it up (review of PR #100).
            Debug.LogWarning($"[Factory] not saving: the last load couldn't read the registry source ({LastLockDetail}), so the list this save " +
                             "would write came from a read that failed. Nothing was changed — refresh (or wait for the next refresh), then save again.");
            return false;
        }
        if (keepDiskModels && !loaded)
        {
            // …and the statics SaveStatics exists to write must be ones a Load put there. Without one this session (a
            // domain reload, then a load that failed) they are the reload's empty lists; writing them would wipe the pack's
            // era grid and scale rules, and keeping the disk's instead would report success for edits never written.
            Debug.LogWarning("[Factory] not saving: this Lab's settings were never loaded from the registry this session (the load couldn't read it), " +
                             "so there is nothing trustworthy to write. Refresh once the registry can be read, then save again.");
            return false;
        }
        // MERGE onto the current on-disk file instead of rebuilding from defaults: preserve the pack HEADER
        // (schemaVersion/modId/dependsOn/loadAfter/overrides — no window edits these, so they must survive every Save),
        // and preserve the scale/era/threshold arrays whenever this session hasn't Load()ed them (the session statics
        // are empty right after a domain reload; writing them then would silently wipe Resize/Era-Lab data). With NO
        // source the deployed copy is the only surviving one and becomes the base (see below); with neither, RegistryFile
        // defaults, so a first-ever Save still writes a valid pack.
        RegistryFile file = null;
        string sourceText = null;
        bool sourceExists = File.Exists(SourcePath);
        if (!sourceExists) { System.Threading.Thread.Sleep(250); sourceExists = File.Exists(SourcePath); }   // an external editor's save-by-rename, as in Load()
        if (sourceExists)   // merge base = the SOURCE (the collapse: deployed is derived)
        {
            // A source that EXISTS but won't parse (conflict markers, a half-finished hand edit) refuses the Save. The
            // lastLoadCorrupt guard only remembers the previous Load(); a SaveStatics caller never Load()s, and it used
            // to read an unreadable source as "no models" and write models: [] to both copies.
            // …but a source that could not be READ at all is not corrupt (review of PR #100, P1): another program has
            // it open, or is replacing it. This save refuses and changes nothing; it does NOT raise the recovery banner,
            // whose "Restore last commit" would git-checkout away every uncommitted edit of a file with nothing wrong in it.
            try { sourceText = File.ReadAllText(SourcePath); }
            catch (Exception e)
            {
                lastLoadLocked = true; LastLockDetail = e.Message; LastLockAdvice = RegistryRules.ReadFailureAdvice(RegistryRules.ClassifyReadFailure(e));   // the Factory's plain warning
                Debug.LogWarning($"[Factory] not saving: the registry source '{SourcePath}' can't be read right now ({e.Message}). {LastLockAdvice} Nothing was changed.");
                return false;
            }
            file = ParseSource(sourceText, out string why);
            if (file == null)
            {
                lastLoadCorrupt = true; LastCorruptDetail = why;   // the Factory's recovery banner; the next Load() preserves the evidence
                lastLoadLocked = false;   // it was just read, so whatever lock an earlier poll saw is over
                Debug.LogError($"[Factory] not saving: the registry source '{SourcePath}' is unreadable right now — {why}. " +
                               "Refusing to overwrite it; fix it (or use the Model Factory's recovery) and save again.");
                return false;
            }
        }
        else if (File.Exists(RegistryPath))
        {
            // NO SOURCE, but a deployed copy: it is the only surviving copy — of the models AND of the pack's modId,
            // dependencies, scale rules and era settings, even when it holds no models (review of #100: a default base
            // erased an empty pack's configuration). Build on it, exactly as Load() adopts it; unreadable refuses.
            string why;
            try { file = ParseRegistry(File.ReadAllText(RegistryPath), RegistryPath, out why); }
            catch (Exception e) { file = null; why = e.Message; }
            if (file == null)
            {
                Debug.LogError($"[Factory] not saving: the registry source '{SourcePath}' is missing and the deployed copy " +
                               $"'{RegistryPath}' can't be read ({why}) — it may be the only copy of this pack. Close whatever holds it (the game?) and save again.");
                return false;
            }
            Debug.Log($"[Factory] the registry source was missing — this save builds on the deployed copy ({file.models.Count} model(s), its pack settings kept).");
        }
        if (file == null) file = new RegistryFile();
        bool writtenByEditor = sourceText != null && Fingerprint(sourceText) == EditorPrefs.GetString(PrefLastWrite, "");
        int deployed = 0; string deployedWhy = null;
        if (sourceExists && file.models.Count == 0 && !writtenByEditor) deployed = DeployedModelCount(out deployedWhy);   // read the deployed copy only when it can decide anything
        var verdict = RegistryRules.JudgeEmptySource(sourceExists, file.models.Count, writtenByEditor, deployed);
        if (verdict != RegistryRules.EmptySourceVerdict.Allow)
        {
            // THE SOURCE HOLDS NO MODELS, and the editor didn't write it that way — for EVERY save, not just statics
            // (review of #100: a Factory bake Upsert()s onto the empty list it loaded and wrote a one-model registry over
            // 37). Every editor Save writes both copies, so a deployed copy that still has models means the source was
            // emptied outside the editor (models cut out mid-edit) — no save may make that wipe permanent. A deployed copy
            // that can't be READ may still hold them, so that refuses too ("unreadable" once counted as "empty"). An empty
            // source the editor wrote ITSELF (Remove() of the last model) passes by its fingerprint, even when its deploy
            // failed and the deployed copy is stale (review of #100: that once blocked every later save).
            Debug.LogError($"[Factory] not saving: the registry source '{SourcePath}' holds no models, and " +
                           (verdict == RegistryRules.EmptySourceVerdict.RefuseDeployedHasModels
                               ? $"the deployed copy still has {deployed}. Restore them (the Model Factory's recovery, or git) and save again. "
                               : $"the deployed copy '{RegistryPath}' can't be checked ({deployedWhy}), so it may still hold them. Close whatever holds it (the game?) and save again. ") +
                           "If the pack really is empty now, delete the deployed copy (the next Load recreates it from the source).");
            return false;
        }
        if (keepDiskModels && (era.held == null || !era.held.MaySaveOver(EraPrint(file))))
        {
            // THE FILE'S ERA SETTINGS ARE NOT THE ONES THIS ERA LAB COPIED (review of PR #100, third and fourth rounds):
            // another window, git or a hand edit changed them since the Lab loaded - possibly while the Lab's refresh
            // was caught by a lock, or while another window's Load() refreshed the session but not this Lab's grid.
            // Writing now would put the older settings over the newer.
            Debug.LogWarning("[Factory] not saving: the era settings in the registry changed since this Era Lab loaded them (another window, git, or a hand edit). " +
                             "Writing now would put the older settings over the newer ones. Reload the Lab, redo the change, and save again.");
            return false;
        }
        if (keepDiskModels) models = file.models;
        SortByName(models);   // write BOTH the live registry and the backup alphabetically, so the order is stable across bakes
        file.models = models;
        if (keepDiskModels)
        {
            // THE ERA LAB'S OWN SETTINGS, and nothing else from the session (review of PR #100, fourth round): the unit
            // scales stay exactly as this read found them - the session's may be older than the file (a refresh lost to
            // a lock), and the Era Lab never edits them.
            file.eraGrid = era.grid ?? new List<EraScaleRow>();
            file.eraGridEnabled = era.enabled;
            file.formationThresholds = era.thresholds ?? new List<FormationThreshold>();
        }
        else if (loaded)   // the statics reflect the on-disk state (+ any Lab edits this session) — authoritative
        {
            file.unitScales = UnitScales ?? new List<UnitScaleRule>();
            file.eraGrid = EraGrid ?? new List<EraScaleRow>();
            file.eraGridEnabled = EraGridEnabled;
            file.formationThresholds = FormationThresholds ?? new List<FormationThreshold>();
        }
        // else: keep file.unitScales/eraGrid/formationThresholds exactly as read from disk — never overwrite with the empty statics
        var json = JsonUtility.ToJson(file, true);
        // 1) Atomic write to THE SOURCE (the collapse: the git-tracked project file is the one registry): fill a
        //    temp file, then swap it in, so an interrupted or locked write can never leave a truncated registry.
        try
        {
            Directory.CreateDirectory(PackRepoDir);
            // …and only over the text this save read and judged (CheckedReplace, review of PR #100, fifth round): another
            // editor replacing the file between that read and this write used to be overwritten unseen. The replace
            // reports what it displaced; anything but `sourceText` puts the other version back and refuses.
            var outcome = CheckedReplace.Write(SourcePath, sourceText, json, out string replaceNote);
            if (replaceNote != null) Debug.LogWarning($"[Factory] registry source: {replaceNote}.");
            if (outcome == CheckedReplace.Outcome.Conflict)
            {
                Debug.LogWarning($"[Factory] not saving: the registry source '{SourcePath}' changed while this save was writing it (another editor, git, a sync tool). " +
                                 "That version is back in place and nothing of this save was written. Reload, then save again.");
                return false;
            }
            EditorPrefs.SetString(PrefLastWrite, Fingerprint(json));   // the editor's own write — see PrefLastWrite
            if (keepDiskModels)
            {
                // Only NOW does the session take the Era Lab's settings: a refused save leaves the session untouched, so a
                // later save from another window can't write a grid this guard refused. The window's version follows its
                // own write - printed from the written bytes as the next read will parse them.
                var written = JsonUtility.FromJson<RegistryFile>(json);
                UnitScales = written.unitScales; EraGrid = written.eraGrid; EraGridEnabled = written.eraGridEnabled; FormationThresholds = written.formationThresholds;
                era.held.Saved(EraPrint(written));
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[Factory] registry write FAILED — the model baked but its entry was NOT saved to " +
                           $"'{SourcePath}' ({e.Message}). Close whatever's locking it (AV, search indexer) and re-bake; " +
                           "the previous source is intact (git history has every committed version).");
            return false;
        }
        // 2) Refresh the DEPLOYED ARTIFACT (what the running game reads) — atomically too. A failure here does not
        //    fail the Save (the source of truth is safe) but it is LOUD: the game keeps loading the stale artifact
        //    until the next successful Save/Load regenerates it — the PENDING fingerprint is what lets a Load() finish it.
        try
        {
            WriteArtifact(json);
            EditorPrefs.DeleteKey(PrefPendingDeploy);
            pendingFailures = 0; lastPendingAttempt = -1;
        }
        catch (Exception e)
        {
            EditorPrefs.SetString(PrefPendingDeploy, Fingerprint(json));
            pendingRetryWarned = false; pendingFailures = 0; lastPendingAttempt = EditorApplication.timeSinceStartup;   // the first retry is due in 2 s
            Debug.LogWarning($"[Factory] deployed-artifact refresh FAILED ({e.Message}) — the registry SOURCE saved fine, " +
                             $"but the GAME will keep loading the stale copy at '{RegistryPath}' until a Save/Load succeeds " +
                             "(is the game running and holding the file?).");
        }
        AssetDatabase.Refresh();
        return true;
    }

    // Save the Era Lab's settings only - the era grid, its switch and the formation thresholds - preserving everything
    // else on disk exactly as read, the models included (re-read fresh so a concurrent model edit/bake in another window
    // isn't reverted by a stale snapshot). `held` is the version of the settings the calling window copied
    // (EraSettingsVersion right after its Load()); the save refuses unless the file still carries it, and advances it
    // on success. The session statics change only when the save succeeds. An unreadable source refuses (returns false).
    public static bool SaveStatics(List<EraScaleRow> eraGrid, bool eraGridEnabled, List<FormationThreshold> thresholds, StaticsVersion held) =>
        Save(null, new EraEdit { grid = eraGrid, enabled = eraGridEnabled, thresholds = thresholds, held = held });

    // Models in the deployed copy: 0 only when it is ABSENT or provably empty; -1 when it exists but can't be read or
    // fails the same shape rule as the source (locked by the game, half-written, `{}`) — no evidence of an empty pack.
    static int DeployedModelCount(out string why)
    {
        why = null;
        if (!File.Exists(RegistryPath)) return 0;
        try { var d = ParseRegistry(File.ReadAllText(RegistryPath), RegistryPath, out why); return d == null ? -1 : d.models.Count; }
        catch (Exception e) { why = e.Message; return -1; }
    }

    public static bool Upsert(ModelDef def)
    {
        // NORMALIZE at the one choke point every write path passes (2026-09-12): attackRepeats floors at 1 —
        // the schema default and the validator's rule. The old "Make static" clear wrote 0, and an entry round-
        // tripped back to animated then warned "attackRepeats: 0 — must be >= 1" on every bake with no way to
        // heal unless the Attack section's slider happened to be drawn (it only draws with an Attack clip set).
        // Flooring here heals every scarred entry on its next Save, whichever window saves it.
        if (def.attackRepeats < 1) def.attackRepeats = 1;
        // Same family (2026-09-13, the SteamTransports import): the old make-static clear wrote these to 0 too —
        // the runtime and the Lab sliders treat <=0 as "1 = authored speed", so floor them to the schema default.
        if (def.deploySpeed <= 0f) def.deploySpeed = 1f;
        if (def.recoilSpeed <= 0f) def.recoilSpeed = 1f;
        var list = Load();
        // CASE-INSENSITIVE, to match the filesystem the key really lives on (2026-08-22). The replace used ordinal
        // `==` while the Factory's collision guard compares OrdinalIgnoreCase, and that gap had a hole in it: renaming
        // "Tank" to "tank" read as "writing over itself" to the guard, then failed to remove the old row here — two
        // registry entries whose baked assets (<name>_Skeleton.asset, …) are the SAME files on Windows, each bake
        // silently overwriting the other. No shipped pack has case-duplicate names, so nothing is merged by this.
        list.RemoveAll(m => string.Equals(m.resourceName, def.resourceName, StringComparison.OrdinalIgnoreCase));
        list.Add(def);
        return Save(list);
    }

    // Remove a model from the registry by resource name. Returns true if something was removed. The baked skeleton/atlas
    // assets are left in the project (harmless); this just stops the plugin injecting that model.
    public static bool Remove(string resourceName)
    {
        var list = Load();
        int before = list.Count;
        list.RemoveAll(m => m.resourceName == resourceName);
        if (list.Count == before) return false;
        return Save(list);
    }

    public static int[] ParseGuid(string csv)
    {
        var p = (csv ?? "").Split(',');
        int g(int i) => p.Length > i && int.TryParse(p[i], out var r) ? r : 0;
        return new[] { g(0), g(1), g(2), g(3) };
    }
}
