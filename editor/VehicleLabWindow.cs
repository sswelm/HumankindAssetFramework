// VehicleLabWindow.cs — "Vehicle Lab" (Tools ▸ HAF ▸ Vehicle Lab, 2026-07-25): turn a raw STATIC model into the
// rigged, animated GLB the animated bake path consumes — the hand-made Ehrhardt recipe (Animated-Models.md) as a
// dialog. Despite the name it is no longer wheels-only: the button is "Generate rig", and a FLOATING unit (the
// dug-out canoe) rigs with no wheels at all — parts marked Ignore are stripped and Wave rock authors the sway.
//   1. Browse a raw model (glb/gltf/fbx/obj/blend) and PROBE it (headless Blender lists the mesh parts).
//   2. Assign roles per part — Wheel / Turret / Body — auto-guessed from names (wheel|tyre|tire / turret); the
//      axle axis per wheel is inferred geometrically in the tool (a wheel is THIN along its axle), overridable.
//   3. Vehicleize: Blender builds Root + per-wheel bones (+Turret), rigid-skins every part, authors the LINEAR
//      "Spin" action (frame 0 = rest — Spin[0..0] is the motionless Idle), exports <name>_Spin.glb + a preview FBX.
//   4. The TURNTABLE PREVIEW plays the Spin clip on the real imported FBX (Unity can't import glb) — wheels visibly
//      spinning before you ever open the Factory. Then: Factory ▸ Browse the generated GLB, Lab: Idle Spin[0..0],
//      Movement Spin[1..N], Convert raw rig ON, Fix 100× OFF, Auto-ground ON (the settings are printed on success).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class VehicleLabWindow : EditorWindow
{
    [MenuItem("Tools/HAF/Vehicle Lab")]
    static void Open()
    {
        var w = GetWindow<VehicleLabWindow>("Vehicle Lab");
        w.minSize = new Vector2(680, 560);   // wide enough for the info text; tall enough for list + knobs + preview
    }

    // Default = NOT YET REVIEWED (the probe's initial state); Body is an explicit "looked at it, it's hull" verdict.
    // Both skin to Root in the rig — the split only exists so review progress is visible. Default is APPENDED so
    // the ints in saved recipes stay valid (Body=0, Wheel=1, Turret=2, Ignore=3).
    // Edgecase = "not sure yet, don't let me forget": rig-wise identical to Default (skins to Root), stays in
    // the only-unreviewed filter, exists purely as a parking flag during a review sweep. Appended (order matters
    // for saved-recipe ints).
    // Track = a tread loop: rigs STATIC like Body but gets its OWN bone (Track_NN_L/R) and therefore its own
    // mesh through the per-bone join — never welded into the hull, so future tread-motion tricks (flipbook /
    // texture scroll) can target it. Appended (saved-recipe ints must keep meaning).
    // Rotor / TailRotor appended LAST so saved-recipe role ints stay valid (Body=0 … Gun=7). Both rig like a Wheel
    // (proximity-cluster → one bone; axle = the cluster's thinnest extent) — which is geometrically correct for BOTH a
    // main rotor (flat top disc → vertical mast axle) and a tail rotor (vertical tail disc → lateral axle). They differ
    // from Wheel only downstream: a rotorcraft bakes CONTINUOUS (always spins) with Auto-ground OFF (flyer).
    // Trail (2026-08-22, the M114 deploy): a split-trail ARM that swings OPEN when the gun deploys — one bone hinged at
    // the end nearest the body, rotating about the vertical, mirrored per side. ("Leg" is deliberately NOT used —
    // it is reserved for a walking mech limb.) Dropdown-only for now: no shortcut key.
    enum Role { Body, Wheel, Turret, Ignore, Default, Edgecase, Caterpillar, Gun, Rotor, TailRotor, Trail, Muzzle, Cradle }
    [Serializable] class Part { public string name; public int verts; public Vector3 center, size; public Role role;
        public int vis = -1;   // probe's escape-ray verdict: 1 = external (visible from outside), 0 = interior (never visible — strippable), -1 = unclassified (pre-visibility probe)
        public string bone = ""; }   // rigged sources: the bone this shard is weighted to (probe 2026-08-20) — lets a BONE row highlight its shards

    // Everything an assignment session builds is [SerializeField]: a DOMAIN RELOAD (any recompile) must never eat
    // the marked roles again — the field incident that motivated recipes in the first place.
    [SerializeField] string srcFile = "";
    [SerializeField] string outGlb = "";   // explicit output path — never silently derived at write time (overwrite guard in Vehicleize)
    [SerializeField] List<Part> parts = new List<Part>();
    // ── SKM fast path: a rip that ships FULLY skinned (probe prints RIGBONE rows) can reuse its ARTIST skeleton —
    // mark which BONES spin instead of marking shards; Vehicleize then authors Spin on the source rig (pivots,
    // weights and weapon bones untouched). boneParts mirrors `parts` row-for-row, holding bones.
    [SerializeField] List<Part> boneParts = new List<Part>();
    [SerializeField] bool useSourceRig;
    // THE FAST-PATH PREDICATE, DEFINED ONCE (2026-08-22). The UI and Generate each had their own copy, and the
    // Deploy/gun/recoil block then read the RAW `parts` list while Generate read `boneParts` — so on a rigged
    // source (the Ehrhardt, any SKM_ rip) marking a bone as Trail or Gun left the whole section reading
    // "no trails marked" with every dial disabled, while Generate happily consumed those bone roles and shipped
    // the defaults (35°, pivot 0.5, recoil 0) with no way to change them. One predicate, one list, no drift.
    bool FastPath => useSourceRig && boneParts.Count > 0;
    List<Part> ActiveParts => FastPath ? boneParts : parts;
    [SerializeField] int frames = 15; [SerializeField] float degrees = -360f; [SerializeField] int axisChoice = 0;   // 0 = Auto (per wheel), 1..3 = X/Y/Z
    // TRAILS (2026-08-22): how far each split-trail arm swings open when the gun deploys, and over how many
    // frames. Mirrored per side, hinged at the arm's body end — the rigger authors it as a separate "Deploy" action.
    [SerializeField] float trailSpreadDeg = 35f; [SerializeField] int trailFrames = 12;
    // GUN PIVOT: where the Gun bone sits along the assembly — the runtime elevation rotates about it, so this IS
    // the trunnion. 0.5 = bbox centre (unchanged default); an artillery piece wants ~0.4 (measured on the M114).
    [SerializeField] float gunPivot = 0.5f;
    // GUN DEPLOY ELEVATION: degrees the gun raises ACROSS the Deploy clip, on the same frames as the trail spread.
    // A towed gun travels clamped level and elevates once the trails are planted — so the raise belongs in the
    // same clip, and every use the state machine makes of Deploy (unfold / fold / hold) carries it along free.
    [SerializeField] float gunDeployElev = 0f;
    // RECOIL: how far the tube kicks back as a FRACTION OF ITS OWN LENGTH, and over how many frames. 0 = off, and
    // off means no Barrel bone is created at all — a gun that never recoils costs nothing and regenerates unchanged.
    [SerializeField] float recoilDist = 0f; [SerializeField] int recoilFrames = 16;
    // Frames of held pose before the kick — the engine can start the attack clip while the gun is still slewing,
    // and the front of the clip is the one part of that timing we control outright.
    [SerializeField] int recoilLead = 0;
    [SerializeField] int tailAxisChoice = 0;   // a tail rotor spins on a different axis than the main rotor — its own Auto/X/Y/Z override
    [SerializeField] float tailYawAdj = 0f;    // manual trim on the tail axle: swing about vertical, degrees
    [SerializeField] float tailPitchAdj = 0f;  // manual trim on the tail axle: tilt up/down, degrees
    [SerializeField] string loadedRecipe = "";   // the recipe shown in the "Edit existing" combobox ("" = ＜new model＞); tracked by NAME so the frame-rebuilt file list can't desync it
    [SerializeField] int treadAdvCells = 3;   // tread advance per loop in cells
    [SerializeField] float treadCellsPerLink = 4f; // tread detail: cells per molded link = the BONES dial (4 = smoothest; 0.25 = one bone per four links)
    static readonly float[] TreadDetailValues = { 4f, 2f, 1f, 0.5f, 0.25f };
    static readonly string[] TreadDetailLabels = {
        "4 — quarter link (smoothest, most bones)", "2 — half link", "1 — one bone per link",
        "0.5 — one bone per TWO links", "0.25 — one bone per FOUR links (coarsest)" };
    [SerializeField] bool tracksStatic = false; // isolation switch: rig tread loops rigid to the hull (no link bones, no conveyor)
    [SerializeField] bool spinEnabled = true;   // MASTER spin switch (2026-08-19, user request: disabling spin on a wheeled vehicle meant unmarking every wheel — the wave-checkbox lesson again). Off = generate with 0 spin degrees + static tracks; bones/markings all kept.
    // WAVE ROCK (2026-07-31): slow idle sway for FLOATING units, authored on a Hull bone under Root. 0 = off.
    [SerializeField] float rockDegrees = 0f;
    [SerializeField] int rockFrames = 120;
    [SerializeField] int rockAxisChoice = 0;   // 0 = Auto (longer horizontal extent = the hull's length), 1 = X, 2 = Y
    static readonly string[] RockAxisOptions = { "Auto (longest horizontal extent = hull length)", "X (hull runs along X)", "Y (hull runs along Y)" };
    [SerializeField] float rockHeading = 0f;      // heading OFFSET from that axis, degrees about vertical
    [SerializeField] float rockPitchDeg = 2.4f;   // pitch amplitude in DEGREES (absolute, not a ratio)
    [SerializeField] bool waveEnabled = false;    // wave-rock MASTER toggle (user request): off = wheeled/tracked (default), on = floating unit. When off, 0° is sent regardless of the amplitude sliders (which keep their values).
    [SerializeField] int rockRollCycles = 1;      // full roll swings per clip (integer = seamless loop)
    [SerializeField] int rockPitchCycles = 1;     // full pitch swings per clip
        [SerializeField] float rockPitchPhase = 90f;  // degrees; at equal speed this is what keeps the motion 2D (ellipse, not one diagonal)
    const int RockFps = 24;                       // Blender's scene fps — the clip's real-time length
    // The two motion sections fold independently (Sound Studio pattern): a model is almost always EITHER a wheeled
    // vehicle OR a floating one, so ~10 permanently-irrelevant rows were on screen at all times.
    [SerializeField] bool foldSpin = true, foldWave = false, foldOrient = false, foldTrails = false;
    // Straighten a source that imports crooked / on its side. Baked into the vertex data BEFORE the rig is built,
    // so wheel axles, tread side detection and the rock's auto hull-length axis all read the corrected pose.
    [SerializeField] Vector3 modelRot = Vector3.zero;
    [SerializeField] bool showWaterline = true;   // level reference grid in the preview
    [SerializeField] bool previewPaused;           // freeze the turntable spin (to judge level / inspect a pose)
    [SerializeField] bool showLevelLine = true;    // a horizontal reference cross at rotor height — align the rotor bar to it
    [SerializeField] int minVerts = 50;   // parts below this are COLLAPSED into Body (a triangle-soup FBX probes into thousands of shards)
    [SerializeField] float minPartSize = 0f;  // hide parts whose LARGEST bbox dimension is below this — drop minVerts + raise this to surface big-but-low-poly parts (flat discs, plates)
    [SerializeField] float minHeight = -999f; // hide parts whose CENTER height is below this (clamped to the model's span, so the default means "off") — slide up to isolate turret-level parts
    [SerializeField] float maxHeight = 999f;  // the reverse: hide parts whose CENTER height is ABOVE this — slide down to strip the superstructure and isolate wheel/chassis level
    [SerializeField] float minWidth = -999f;  // LEFT/RIGHT slice (user request 2026-08-01): hide parts whose center is LEFT of this on the WIDTH axis (center.y — the axis the two wheels mirror across); bracket with maxWidth to isolate ONE side's wheel. Default span = off.
    [SerializeField] float maxWidth = 999f;   // the reverse: hide parts whose center is RIGHT of this (center.y).
    static float MaxDim(Part p) => Mathf.Max(p.size.x, Mathf.Max(p.size.y, p.size.z));
    bool VisiblePart(Part x) => x.verts >= minVerts && MaxDim(x) >= minPartSize && x.center.z >= minHeight && x.center.z <= maxHeight && x.center.y >= minWidth && x.center.y <= maxWidth;
    [SerializeField] int partFilter;      // list filter: 0 = all; see FilterOptions (Unreviewed = Default + Edgecase)
    static readonly string[] FilterOptions = { "None (all parts)", "Undecided (Default + Edgecase)", "Default", "Wheel", "Turret", "Body", "Ignore", "Edgecase", "Caterpillar", "Gun", "Rotor", "Tail rotor", "Trail", "Muzzle", "Cradle" };
    bool MatchesFilter(Role r) => partFilter == 1 ? (r == Role.Default || r == Role.Edgecase)
                                : partFilter == 2 ? r == Role.Default
                                : partFilter == 3 ? r == Role.Wheel
                                : partFilter == 4 ? r == Role.Turret
                                : partFilter == 5 ? r == Role.Body
                                : partFilter == 6 ? r == Role.Ignore
                                : partFilter == 7 ? r == Role.Edgecase
                                : partFilter == 8 ? r == Role.Caterpillar
                                : partFilter == 9 ? r == Role.Gun
                                : partFilter == 10 ? r == Role.Rotor
                                : partFilter == 11 ? r == Role.TailRotor
                                : partFilter == 12 ? r == Role.Trail
                                : partFilter == 13 ? r == Role.Muzzle
                                : partFilter == 14 ? r == Role.Cradle : true;
    // Roles that SPIN (get a bone + the Spin action): wheels and both rotor kinds. Used for the Generate-enable gate,
    // the spin-section summary, Verify, and the "inside the wheel" test — so a rotorcraft with no Wheel parts still rigs.
    static bool IsSpinner(Role r) => r == Role.Wheel || r == Role.Rotor || r == Role.TailRotor;
    // Visibility switch (probe's escape-ray verdict): All / External / Interior. Interior = never visible from any
    // outside direction — the strip candidates (mark them Ignore to reclaim triangle budget for visible surfaces).
    [SerializeField] int visFilter;
    static readonly string[] VisFilterOptions = { "All parts", "External only", "Interior only" };
    bool MatchesVis(Part p) => visFilter == 0 || (visFilter == 1 ? p.vis != 0 : p.vis == 0);   // unclassified (-1, old probe) counts as external
    Vector2 partsScroll;
    Vector2 windowScroll;                        // the whole dialog scrolls — the knob sections outgrew a normal window
    [SerializeField] int previewHeight = 400;    // fixed (not greedy): a scroll view needs a bounded child

    // ── Recipes: the whole vehicleize configuration as a JSON file — reload it after a restart, keep one per model,
    // ship it next to the model so a collaborator reproduces the exact rig. ──
    [Serializable] class Recipe
    {
        public string srcFile, outGlb; public int frames, axisChoice, minVerts; public float degrees;
        public List<Part> parts; public List<Part> boneParts; public bool useSourceRig;
        public int treadAdvCells = 3; public float treadCellsPerLink = 4f;
        // 2026-08-01: these ALSO drive the bake command (line ~800), but the DTO omitted them — so a saved recipe
        // baked a DIFFERENT rig on reload (as-imported orientation, no wave rock, live tracks) and, worse, whatever
        // rock/orientation was on screen LEAKED into the next model's bake because Load never reset them. Now stored.
        // Defaults match the live field defaults so a PRE-2026-08-01 recipe (missing these keys) loads NEUTRALLY =
        // wheeled / as-imported / no rock (waveEnabled false gates the rock params regardless).
        public float trailSpreadDeg = 35f; public int trailFrames = 12;   // trails: spread angle + clip length
        public float gunPivot = 0.5f;      // where the Gun bone (= the elevation trunnion) sits along the assembly
        public float gunDeployElev = 0f;   // degrees the gun raises across the Deploy clip
        public float recoilDist = 0f; public int recoilFrames = 16;   // recoil: fraction of tube length + clip length
        public int recoilLead = 0;         // held frames before the kick
        public bool tracksStatic = false;
        public bool spinEnabled = true;    // default true: recipes that predate the field keep their spin (absent-field = old behavior)
        public Vector3 modelRot = Vector3.zero;
        public bool waveEnabled = false;
        public float rockDegrees = 0f;
        public int rockFrames = 120;
        public int rockAxisChoice = 0;
        public float rockHeading = 0f;
        public float rockPitchDeg = 2.4f;
        public int rockRollCycles = 1;
        public int rockPitchCycles = 1;
        public float rockPitchPhase = 90f;
    }
    const string RecipesDir = "Assets/FactorySource/VehicleLab/Recipes";
    static readonly string[] AxisOptions = { "Auto (thinnest extent = axle, per wheel)", "X", "Y", "Z" };
    string status = "";
    string lastOutGlb = "";

    // turntable preview state
    GameObject inst; PreviewRenderUtility pru; AnimationClip spinClip;
    // CLIP PICKER (2026-08-22): the rig can now author more than one action (Spin + Deploy), so the turntable must
    // let you choose which to judge — the same need the Animation Lab preview had. Rebuilt with every preview.
    List<AnimationClip> previewClips; [SerializeField] int previewClipIdx;
    Bounds bounds; bool boundsValid; float spinT; double lastTick;
    float fullRadius;   // whole-model radius — far-plane margin must NOT shrink to a focused part's bounds
    Vector2 orbit = new Vector2(140f, -18f); float zoom = 1.5f;
    [SerializeField] Vector2 previewPan;   // camera-plane pan (middle/right-drag), in dist units — ported from the Factory preview
    // part focus/highlight: clicking a row zooms onto that part and tints it — the "which shard is the wheel?" x-ray
    string selectedPart = "";
    List<Renderer> highlightedRenderers; List<Material[]> highlightedOriginals;   // a bone row tints MANY shards
    // CHECKER SKIN for the turntable (2026-08-22): rotation is invisible on an untextured, rotationally symmetric
    // part. Painted onto the live instance's renderers; the yellow part-highlight still wins on top of it.
    [SerializeField] bool previewChecker = true;
    static Texture2D checkerTex; static Material checkerMat;
    Dictionary<Renderer, Material[]> checkerOriginals;
    static Material highlightMat;

    void OnEnable() { EditorApplication.update += Tick; lastTick = EditorApplication.timeSinceStartup; }
    void OnDisable()
    {
        EditorApplication.update -= Tick;
        if (inst != null) DestroyImmediate(inst);
        if (pru != null) { try { pru.Cleanup(); } catch { } pru = null; }
    }
    void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        if (inst != null) { if (!previewPaused) spinT += (float)(now - lastTick); Repaint(); }
        lastTick = now;
    }

    void OnGUI()
    {
        // WINDOW SCROLL: with Orientation + Spin + Wave rock the knobs outgrew a normal window height. Both the
        // parts list and the preview below are given FIXED heights for this — a greedy (ExpandHeight) child inside
        // a scroll view resolves against infinite space and the scrollbar never appears.
        windowScroll = EditorGUILayout.BeginScrollView(windowScroll);
        EditorGUILayout.HelpBox(
            "Rig a STATIC model for the animated bake: probe its parts, mark what moves (wheels, turret) or what to strip, and generate the rigged " +
            "Spin GLB the animated bake consumes — no Blender knowledge needed. The preview below plays the result. " +
            "Then bake it in the Factory/Animation Lab (settings printed on success).", MessageType.Info);

        // --- Edit existing (FIRST row, Animation-Lab-style; folds in the old New-model button + Recipes dropdown +
        // Load-recipe file dialog). ＜new model＞ = start fresh; any other entry loads that saved recipe (which fills in
        // Raw model / Output GLB below). Tracked by NAME (loadedRecipe) so the per-frame-rebuilt file list can't desync it. ---
        using (new EditorGUILayout.HorizontalScope())
        {
            string rdirFull = Path.Combine(Directory.GetParent(Application.dataPath).FullName, RecipesDir);
            string[] rfiles = Directory.Exists(rdirFull) ? Directory.GetFiles(rdirFull, "*.json").OrderByDescending(File.GetLastWriteTime).ThenBy(x => x).ToArray() : new string[0];
            var names = new string[rfiles.Length + 1];
            names[0] = "＜new model＞";
            // Display labels carry the file's last-modified stamp (user ask 2026-08-20: a bare name list can't tell
            // you which recipe you worked on yesterday); `names` stays the bare name for matching/dialogs.
            var labels = new string[rfiles.Length + 1];
            labels[0] = names[0];
            for (int i = 0; i < rfiles.Length; i++)
            {
                names[i + 1] = Path.GetFileNameWithoutExtension(rfiles[i]);
                labels[i + 1] = File.GetLastWriteTime(rfiles[i]).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "   —  " + names[i + 1];   // stamp first, newest at the top
            }
            int cur = 0;
            for (int i = 0; i < rfiles.Length; i++) if (names[i + 1] == loadedRecipe) { cur = i + 1; break; }

            int sel = EditorGUILayout.Popup(new GUIContent("Edit existing",
                "Load a saved recipe, or ＜new model＞ to start fresh. Recipes live in " + RecipesDir + "; Save recipe… adds to this list."), cur, labels);
            if (sel != cur)
            {
                bool dirty = parts.Count > 0 || boneParts.Count > 0;
                int marked = ActiveParts.Count(x => x.role != Role.Default);
                bool ok = !dirty || EditorUtility.DisplayDialog("Vehicle Lab",
                    (sel == 0 ? "Start a new model — discard the current session?" : $"Load recipe '{names[sel]}' — discard the current session?") + "\n\n" +
                    (marked > 0 ? marked + " marked part(s) will be lost unless you saved a recipe.\n\n" : "") +
                    "The generated GLB on disk is not touched.", sel == 0 ? "Start new" : "Load", "Cancel");
                if (ok) { if (sel == 0) NewModel(); else LoadRecipeFromPath(rfiles[sel - 1]); }
                GUI.FocusControl(null);
            }
            using (new EditorGUI.DisabledScope(cur <= 0))
            {
                if (GUILayout.Button(new GUIContent("↻ Reload", "Re-load the selected recipe from disk, discarding unsaved form changes."), GUILayout.Width(72)))
                    { LoadRecipeFromPath(rfiles[cur - 1]); GUI.FocusControl(null); }
                if (GUILayout.Button(new GUIContent("Remove", "Delete the selected recipe FILE from disk. The generated GLB and the current session are not touched."), GUILayout.Width(72)))
                    if (EditorUtility.DisplayDialog("Remove recipe", $"Delete recipe '{names[cur]}' from disk?\n\n(The generated GLB and the current session are not touched.)", "Delete", "Cancel"))
                    { try { File.Delete(rfiles[cur - 1]); AssetDatabase.Refresh(); } catch (Exception e) { status = "Delete failed: " + e.Message; } loadedRecipe = ""; GUI.FocusControl(null); }
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            srcFile = EditorGUILayout.TextField(new GUIContent("Raw model", "The static source model (glb/gltf/fbx/obj/blend)."), srcFile);
            if (GUILayout.Button("Browse…", GUILayout.Width(70)))
            {
                var p = EditorUtility.OpenFilePanel("Pick the static vehicle model", Path.GetDirectoryName(string.IsNullOrEmpty(srcFile) ? "D:/3DModels" : srcFile), "glb,gltf,fbx,obj,blend");
                if (!string.IsNullOrEmpty(p))
                {
                    srcFile = p; parts.Clear(); status = "";
                    outGlb = Path.Combine(Path.GetDirectoryName(p), Path.GetFileNameWithoutExtension(p) + "_Spin.glb").Replace('\\', '/');   // suggestion only — fully editable below
                }
            }
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            outGlb = EditorGUILayout.TextField(new GUIContent("Output GLB", "Where the rigged Spin GLB is written. Defaults to <source>_Spin.glb next to the source, but fully yours — an existing file asks before being overwritten (protect hand-made rigs!)."), outGlb);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                var p = EditorUtility.SaveFilePanel("Output GLB", Path.GetDirectoryName(string.IsNullOrEmpty(outGlb) ? srcFile : outGlb),
                    Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(outGlb) ? srcFile + "_Spin" : outGlb), "glb");
                if (!string.IsNullOrEmpty(p)) outGlb = p.Replace('\\', '/');
            }
        }

        // --- Actions: probe the source, save the current marking as a recipe (appears above), verify the classification ---
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(srcFile) || !File.Exists(srcFile)))
                if (GUILayout.Button(new GUIContent("Probe parts", "Headless Blender lists the model's mesh parts (a single combined mesh is split into loose parts). Roles are auto-guessed from names."), GUILayout.Height(24)))
                    Probe();
            using (new EditorGUI.DisabledScope(parts.Count == 0 && boneParts.Count == 0))
                if (GUILayout.Button(new GUIContent("Save recipe…", "Save the whole configuration (source, output, roles, knobs) as JSON — it then appears in the Edit-existing dropdown above."), GUILayout.Width(110), GUILayout.Height(24)))
                    SaveRecipe();
            using (new EditorGUI.DisabledScope(parts.Count == 0 && boneParts.Count == 0))
                if (GUILayout.Button(new GUIContent("Verify", "Sanity-check the classification: shows the wheel bones the rig step would build (clustering preview) and flags stray clusters, axle disagreement, unpaired wheels, turret outliers and undecided leftovers."), GUILayout.Width(70), GUILayout.Height(24)))
                    VerifySelection();
        }

        if (parts.Count > 0 || boneParts.Count > 0)
        {
            if (boneParts.Count > 0)
                useSourceRig = EditorGUILayout.ToggleLeft(new GUIContent($"Use source skeleton (fast path) — the model ships fully rigged ({boneParts.Count} bones)",
                    "The probe found an artist skeleton with full vertex weights. ON: mark which BONES spin and the rig step reuses that skeleton unchanged (artist axle pivots, weapon/socket bones kept). OFF: the static shard-marking flow."), useSourceRig);
            var list = ActiveParts;
            // Tiny-fragment collapse: a triangle-soup FBX probes into THOUSANDS of 3-4-vert shards — they all belong
            // to Body anyway (anything not marked wheel/turret skins to Root). Only substantial parts are listed.
            minVerts = EditorGUILayout.IntSlider(new GUIContent("Hide parts under (verts)",
                "Parts smaller than this are collapsed into Body automatically (they skin to Root). Raise it if the list is still noisy; lower it if a small wheel is missing."), minVerts, 1, 2000);
            minPartSize = EditorGUILayout.Slider(new GUIContent("Hide parts under (size)",
                "Parts whose largest bbox dimension is below this are hidden (they stay on the hull, like the verts filter). Drop the verts slider and raise this to find LARGE parts with only a few vertices — flat discs and plates."), minPartSize, 0f, 2f);
            // Height filter: slider range auto-fits the model's actual vertical span (probe center heights, Z-up).
            // PAD the ends a hair BEYOND the outermost part (user finding 2026-08-01): the default clamps to the exact
            // min/max, but the slider rounds slightly inside, clipping the edge part ("1 hidden by the sliders" at rest).
            // With the pad, "fully open" sits just past the parts, so nothing hides until you actually drag inward.
            float zLo = list.Min(x => x.center.z), zHi = list.Max(x => x.center.z), zPad = Mathf.Max(0.02f, (zHi - zLo) * 0.02f);
            minHeight = EditorGUILayout.Slider(new GUIContent("Hide parts below (height)",
                "Parts whose center height is below this are hidden. Slide up past the hull deck to isolate turret-level parts."), Mathf.Clamp(minHeight, zLo - zPad, zHi + zPad), zLo - zPad, zHi + zPad);
            maxHeight = EditorGUILayout.Slider(new GUIContent("Hide parts above (height)",
                "Parts whose center height is above this are hidden. Slide down to strip the superstructure and isolate wheel/chassis-level parts."), Mathf.Clamp(maxHeight, zLo - zPad, zHi + zPad), zLo - zPad, zHi + zPad);
            // Left/right (width) filter (user request 2026-08-01): the horizontal companion to the height bracket —
            // slice along the WIDTH axis (center.y, where the two wheels mirror) to isolate ONE side's wheel. Same
            // end-padding so a fresh model hides nothing until you drag.
            float yLo = list.Min(x => x.center.y), yHi = list.Max(x => x.center.y), yPad = Mathf.Max(0.02f, (yHi - yLo) * 0.02f);
            minWidth = EditorGUILayout.Slider(new GUIContent("Hide parts left of (side)",
                "Parts whose center is LEFT of this on the width axis are hidden — bracket with the next slider to keep just one side's wheel. (Straighten the model in Orientation first so the two wheels split along this axis.)"), Mathf.Clamp(minWidth, yLo - yPad, yHi + yPad), yLo - yPad, yHi + yPad);
            maxWidth = EditorGUILayout.Slider(new GUIContent("Hide parts right of (side)",
                "Parts whose center is RIGHT of this on the width axis are hidden. Slide the two together onto one wheel to isolate it, then mark it Wheel."), Mathf.Clamp(maxWidth, yLo - yPad, yHi + yPad), yLo - yPad, yHi + yPad);
            partFilter = EditorGUILayout.Popup(new GUIContent("Show only",
                "Filter the list to one classification. Marking a part out of the current filter removes it from the list and auto-advances to the next."), partFilter, FilterOptions);
            int interiorN = list.Count(x => x.vis == 0);
            visFilter = EditorGUILayout.Popup(new GUIContent("Visibility",
                "The probe's escape-ray verdict per part: External = some surface point can see out; Interior = provably " +
                "never visible from outside (cockpit gear, engine guts) — mark those Ignore to reclaim triangle budget. " +
                "Probed before this feature existed? Re-Probe to classify." + (interiorN > 0 ? $"  ({interiorN} interior found)" : "")),
                visFilter, VisFilterOptions);
            var shown = list.Where(x => VisiblePart(x) && MatchesFilter(x.role) && MatchesVis(x)).ToList();
            int hidden = list.Count(x => !VisiblePart(x));
            int unreviewed = list.Count(x => VisiblePart(x) && x.role == Role.Default);
            int edgecases = list.Count(x => VisiblePart(x) && x.role == Role.Edgecase);
            EditorGUILayout.LabelField($"{(useSourceRig && boneParts.Count > 0 ? "Source BONES" : "Parts")} ({shown.Count} shown{(hidden > 0 ? $", {hidden} hidden by the sliders" : "")}{(unreviewed > 0 ? $", {unreviewed} undecided" : ", all decided")}{(edgecases > 0 ? $", {edgecases} edge-case" : "")}) — mark {(useSourceRig && boneParts.Count > 0 ? "the bones that SPIN (Wheel)" : "the wheels & turret")}:", EditorStyles.boldLabel);
            if (useSourceRig && boneParts.Count > 0)   // 2026-08-20: a user hunted for the turret's shards here — in this mode they are ONE row
                EditorGUILayout.LabelField("Each row is one BONE of the shipped skeleton; all the shards skinned to it count as that row (the turret's parts = the Turret bone). Untick the fast path to list and mark individual parts.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("  Keys:  ↑/↓ = previous/next part   ·   W/T/B = Wheel/Turret/Body   ·   R = Rotor (main, spins about the mast)   ·   L = taiL rotor (spins about the lateral axis)   ·   G = Gun (rides the Turret; muzzle/socket anchor)   ·   C = Caterpillar (tread loop)   ·   I = Ignore (DELETED)   ·   D = Default   ·   E = Edgecase", EditorStyles.miniLabel);
            // Keyboard review loop: ↑/↓ step the selection (zoom+highlight follows), W/T/B/I mark the selected
            // part's role — the whole list can be reviewed without mousing between rows and dropdowns.
            var ev = Event.current;
            if (ev.type == EventType.KeyDown && shown.Count > 0 && !EditorGUIUtility.editingTextField)
            {
                int idx = shown.FindIndex(x => x.name == selectedPart);
                if (ev.keyCode == KeyCode.UpArrow || ev.keyCode == KeyCode.DownArrow)
                {
                    idx = ev.keyCode == KeyCode.DownArrow ? Mathf.Min(idx + 1, shown.Count - 1) : Mathf.Max(idx - 1, 0);
                    SelectPart(shown[idx].name);
                    partsScroll.y = Mathf.Max(0f, idx * 20f - 120f);   // keep the selected row in view (~20px rows)
                    GUIUtility.keyboardControl = 0;                    // a focused slider/popup must not swallow the arrows
                    ev.Use(); Repaint();
                }
                else if (idx >= 0 && (ev.keyCode == KeyCode.W || ev.keyCode == KeyCode.T || ev.keyCode == KeyCode.B || ev.keyCode == KeyCode.I || ev.keyCode == KeyCode.D || ev.keyCode == KeyCode.E || ev.keyCode == KeyCode.C || ev.keyCode == KeyCode.G || ev.keyCode == KeyCode.R || ev.keyCode == KeyCode.L))
                {
                    shown[idx].role = ev.keyCode == KeyCode.W ? Role.Wheel
                                    : ev.keyCode == KeyCode.T ? Role.Turret
                                    : ev.keyCode == KeyCode.I ? Role.Ignore
                                    : ev.keyCode == KeyCode.D ? Role.Default
                                    : ev.keyCode == KeyCode.E ? Role.Edgecase
                                    : ev.keyCode == KeyCode.C ? Role.Caterpillar
                                    : ev.keyCode == KeyCode.G ? Role.Gun
                                    : ev.keyCode == KeyCode.R ? Role.Rotor
                                    : ev.keyCode == KeyCode.L ? Role.TailRotor : Role.Body;
                    // If the new role falls outside the active filter, the part leaves the list — advance to the
                    // next one so the sweep continues instead of the selection dying with the removed row.
                    if (partFilter != 0 && !MatchesFilter(shown[idx].role))
                    {
                        SelectPart(idx + 1 < shown.Count ? shown[idx + 1].name : idx > 0 ? shown[idx - 1].name : "");
                        partsScroll.y = Mathf.Max(0f, idx * 20f - 120f);
                    }
                    ev.Use(); Repaint();
                }
            }
            partsScroll = EditorGUILayout.BeginScrollView(partsScroll, GUILayout.Height(280));   // fixed: a greedy child inside the window scroll would never let it scroll
            foreach (var p in shown)
                using (new EditorGUILayout.HorizontalScope())
                {
                    p.role = (Role)EditorGUILayout.EnumPopup(p.role, GUILayout.Width(70));
                    // the row label is a BUTTON: click = zoom the preview onto this part and tint it yellow
                    bool isSel = selectedPart == p.name;
                    var st = isSel ? EditorStyles.whiteMiniLabel : EditorStyles.miniLabel;
                    if (GUILayout.Button($"{(isSel ? "◉ " : "")}{p.name}   ({p.verts} verts, size {p.size.x:0.00}×{p.size.y:0.00}×{p.size.z:0.00})", st))
                        SelectPart(isSel ? "" : p.name);   // click again = back to full view
                }
            EditorGUILayout.EndScrollView();
            if (inst == null)
                EditorGUILayout.LabelField("  (probe preview unavailable — part focus needs the probe's preview FBX; re-Probe after recompiling)", EditorStyles.miniLabel);
            else
                EditorGUILayout.LabelField("  Click a row to zoom + highlight it in the preview below; click again for the full view.", EditorStyles.miniLabel);
            // ORIENTATION first: it changes what every measurement below sees, so it reads as step one.
            if (Section(ref foldOrient, "Orientation — straighten the model",
                    modelRot == Vector3.zero ? "as imported" : $"{modelRot.x:0}° / {modelRot.y:0}° / {modelRot.z:0}°"))
            {
                EditorGUILayout.LabelField("  Rotates the model BEFORE rigging, baked into the mesh — so the axle, tread and hull-length inferences all read the straightened pose. (The Factory's own Rotation turns the finished bake instead.)", EditorStyles.miniLabel);
                modelRot.x = EditorGUILayout.Slider(new GUIContent("Roll X (deg)", "Rotate about X. Use to stand up a model that imports lying on its side."), modelRot.x, -180f, 180f);
                modelRot.y = EditorGUILayout.Slider(new GUIContent("Pitch Y (deg)", "Rotate about Y. Use to level a model that imports nose-up or nose-down."), modelRot.y, -180f, 180f);
                modelRot.z = EditorGUILayout.Slider(new GUIContent("Yaw Z (deg)", "Rotate about the vertical. Use to point the model along the axis the rig expects (vehicles here run along X)."), modelRot.z, -180f, 180f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(EditorGUIUtility.labelWidth);
                    if (GUILayout.Button("Reset to as-imported", GUILayout.Width(160))) modelRot = Vector3.zero;
                    foreach (var q in new[] { 90f, -90f, 180f })
                        if (GUILayout.Button($"Z {q:+0;-0}°", GUILayout.Width(58))) modelRot.z = Mathf.Repeat(modelRot.z + q + 180f, 360f) - 180f;
                }
            }

            // TWO MOTION SECTIONS, collapsible (2026-07-31): a model is almost always EITHER a wheeled vehicle OR a
            // floating one, so showing both knob sets at once was ~10 permanently-irrelevant rows. Each header
            // summarises its state while collapsed, so nothing is hidden — only folded away.
            int wheelCount = list.Count(x => IsSpinner(x.role));
            if (Section(ref foldSpin, "Spin — wheels & tracks",
                    wheelCount == 0 ? "no wheels/rotors marked — inert"
                    : !spinEnabled ? "DISABLED (rig keeps bones, nothing turns)"
                    : $"{wheelCount} spinning part(s) · {degrees:0}° / {frames} frames"))
            {
                // MASTER SWITCH (2026-08-19, the wave-checkbox lesson applied to spin: disabling spin on a wheeled
                // vehicle used to mean unmarking every wheel). Off = Generate passes 0 spin degrees and forces
                // static tracks — bones and markings all survive for re-enabling. Dials stay visible, disabled.
                spinEnabled = EditorGUILayout.ToggleLeft(new GUIContent("  Enable spin animation",
                    "Off: the rig is generated with zero wheel/rotor rotation and static tracks — every bone and marking is kept, nothing turns. On: normal spin. Markings and dial values survive toggling."), spinEnabled);
                // SPIN GATING (2026-08-19, user: "really confusing that this is also present [on a boat] — we
                // should be able to disable it"): with NO wheel/rotor/turret marked, spin is inert by definition,
                // so the dials gray out instead of inviting tuning that does nothing. The ONE honest exception is
                // named below: Spin frames still FLOORS the generated clip length (vehicle_rig: frame_end =
                // max(spin frames, rock frames)), so a wave-only model with a rock cycle SHORTER than Spin frames
                // would still be affected — the note says so instead of hiding it.
                bool spinInert = wheelCount == 0;
                if (spinInert)
                    EditorGUILayout.HelpBox("No wheel/rotor/turret parts are marked — spin does nothing on this model. " +
                        "(Only 'Spin frames' can still matter: it floors the generated clip length, e.g. for a wave-rock cycle shorter than it.)", MessageType.None);
                using (new EditorGUI.DisabledScope(spinInert || !spinEnabled))
                {
                axisChoice = EditorGUILayout.Popup(new GUIContent("Axle axis", "Auto infers each wheel's axle as its thinnest bbox extent — right for normal wheels; override only if a wheel spins the wrong way around."), axisChoice, AxisOptions);
                if (list.Any(x => x.role == Role.TailRotor))
                {
                    tailAxisChoice = EditorGUILayout.Popup(new GUIContent("Tail-rotor axle", "The tail fan spins about a DIFFERENT axis than the main rotor (lateral, not vertical). Auto reads it from the disc; if the fan spins wrong, pick X / Y / Z by eye in the preview — this affects ONLY the tail rotor, so the main rotor stays put."), tailAxisChoice, AxisOptions);
                    tailYawAdj = EditorGUILayout.Slider(new GUIContent("Tail axle yaw trim", "Swing the tail axle left/right about vertical, degrees — on top of the Auto/forced axle. Dial by eye until the fan spins flat in its ring."), tailYawAdj, -90f, 90f);
                    tailPitchAdj = EditorGUILayout.Slider(new GUIContent("Tail axle pitch trim", "Tilt the tail axle up/down, degrees — on top of the Auto/forced axle. Dial by eye until the fan spins flat in its ring."), tailPitchAdj, -90f, 90f);
                }
                frames = EditorGUILayout.IntSlider(new GUIContent("Spin frames", "Length of the generated Spin action. Apparent speed is tuned later with slice steps (Spin[1..N/2]) — this just needs to be a smooth loop."), frames, 5, 60);
                degrees = EditorGUILayout.Slider(new GUIContent("Spin degrees", "Wheel rotation over the clip (one full turn = 360). Which SIGN rolls forward depends on the model's nose direction — check the preview and negate if the wheels roll backward. For a +X-facing model (like the Ehrhardt), +360 is forward."), degrees, -720f, 720f);
                if (list.Any(x => x.role == Role.Caterpillar))
                {
                    // The isolation switch FIRST, gating the dials it makes moot (user design 2026-08-19: "the
                    // checkbox should be moved before tread speed and should enable the tread speed controls").
                    tracksStatic = EditorGUILayout.Toggle(new GUIContent("Static tracks (no movement)", "ISOLATION SWITCH: rig the tread loops rigid to the hull — no link bones, no conveyor animation. Wheels still spin; the track geometry stays but doesn't run. For debugging (or a cheap LOD-style rig). Untick to enable the tread dials below."), tracksStatic);
                    using (new EditorGUI.DisabledScope(tracksStatic))
                    {
                        treadAdvCells = EditorGUILayout.IntSlider(new GUIContent("Tread speed (cells/loop)", "Belt advance per Spin loop, in cells (cell size set by Tread detail below). At detail 4: 4 cells = one full link — the belt matches the big wrap wheels exactly; 3 = slightly slower. Restarts stay invisible at any value (the pattern maps onto the cleat sub-grid)."), treadAdvCells, 1, 8);
                        int di = System.Array.IndexOf(TreadDetailValues, treadCellsPerLink); if (di < 0) di = 0;
                        di = EditorGUILayout.Popup(new GUIContent("Tread detail (cells/link)", "THE BONES DIAL: how many rigid tread pieces per molded track link. Above 1 splits links (smoother wheel wraps, more bones); below 1 MERGES links (one bone carries 2 or 4 links — the escape hatch for finely-molded tracks like the Bradley's 0.186 pitch, where even one-per-link is 75 bones a side). Tread speed is in these cells; the pattern still maps at every restart."), di, TreadDetailLabels);
                        treadCellsPerLink = TreadDetailValues[di];
                    }
                    // road-wheel/roller and rear-idler speeds are AUTOMATIC: rims match the belt's advance
                    // (belt-continuity), each snapped to its own spoke-symmetry grid for pop-free loop restarts
                    // (both proven manually via dials first, then automated at the user's request)
                }
                }   // end spin-inert DisabledScope
            }

            // TRAILS — a split-trail gun's DEPLOY. The arms marked Trail get a bone hinged at their body end and a
            // separate "Deploy" action that swings them open, mirrored per side; `Spin` keeps the wheels rolling
            // with the arms at their folded rest. Assign in the Lab as: Idle/reference `Deploy`, Idle stance
            // `Deploy[N..N]`, Movement `Spin`, After-move `Deploy`, Pre-move `Deploy[N..0]`.
            if (Section(ref foldTrails, "Deploy — a split-trail gun coming into action",
                    ActiveParts.Count(p => p.role == Role.Trail) == 0 ? "no trails marked"
                        : $"{ActiveParts.Count(p => p.role == Role.Trail)} trail(s) · {trailSpreadDeg:0.#}° over {trailFrames} frames"
                          + (gunDeployElev != 0f ? $" · gun +{gunDeployElev:0.#}°" : "")))
            {
                using (new EditorGUI.DisabledScope(ActiveParts.Count(p => p.role == Role.Trail) == 0))
                {
                    trailSpreadDeg = EditorGUILayout.Slider(new GUIContent("Spread (deg)",
                        "How far each trail swings OUT from its towing position when the gun deploys — mirrored per " +
                        "side, hinged at the arm's body end and rotated about the vertical. The M114's trails open " +
                        "to roughly 35–45°. 0 = no deploy motion."), trailSpreadDeg, 0f, 90f);
                    trailFrames = EditorGUILayout.IntSlider(new GUIContent("Deploy frames",
                        "Length of the 'Deploy' clip. The runtime plays every clip at its authored length (24 fps), " +
                        "so ~12 frames ≈ half a second — pace it here, not at runtime."), trailFrames, 2, 60);
                }
                // GUN PIVOT lives here rather than with the trails because it is the same kind of knob: where a
                // moving part actually turns. The runtime elevation (Animation Lab ▸ "Gun elevation — max") rotates
                // the Gun bone about ITS OWN ORIGIN, so this IS the trunnion.
                using (new EditorGUI.DisabledScope(ActiveParts.Count(p => p.role == Role.Gun || p.role == Role.Muzzle || p.role == Role.Cradle) == 0))
                {
                    gunPivot = EditorGUILayout.Slider(new GUIContent("Gun pivot (breech→muzzle)",
                        "Where the Gun bone sits along the gun assembly — and therefore where the barrel ELEVATES " +
                        "from. 0 = the breech end, 1 = the muzzle, 0.5 = the assembly's centre (the historical " +
                        "placement, kept as the default so existing rigs regenerate unchanged). A real gun pivots at " +
                        "its trunnions: ~0.4 on the M114. Too far forward and the breech swings down through the " +
                        "carriage when the gun elevates."), gunPivot, 0f, 1f);
                    gunDeployElev = EditorGUILayout.Slider(new GUIContent("Gun raise on deploy (deg)",
                        "Degrees the gun elevates ACROSS the Deploy clip — same frames as the trail spread, because " +
                        "a towed gun travels clamped level over its closed trails and only comes up once they are " +
                        "planted. Every use the state machine makes of Deploy carries it: unfold raises, the " +
                        "reversed clip lowers it back onto the travel lock before the unit rolls, the held last " +
                        "frame keeps it up. Composes with the Animation Lab's runtime 'Gun elevation — max', which " +
                        "writes a separate channel — dial that one against this raised base, not against level. " +
                        "0 = leave the gun level. Needs trails: the Deploy clip is what carries it."),
                        gunDeployElev, 0f, 45f);
                    if (gunDeployElev != 0f && ActiveParts.Count(p => p.role == Role.Trail) == 0)
                        EditorGUILayout.HelpBox("No trails marked — there is no Deploy clip to carry the raise, so " +
                            "this is doing nothing. Mark the trail arms (T) first.", MessageType.Warning);
                    // MUZZLE marking is optional, so say what it is FOR rather than leaving the role a mystery in
                    // the dropdown — and say plainly that it is not a bone, which is the thing to get wrong.
                    // The three gun roles all weld to the ONE Gun bone, so the dropdown alone cannot explain why they
                    // are separate. Say what each one is FOR — the differences only appear in the span and, later,
                    // in recoil.
                    int nM = ActiveParts.Count(p => p.role == Role.Muzzle), nC = ActiveParts.Count(p => p.role == Role.Cradle);
                    EditorGUILayout.HelpBox(
                        "Gun · Cradle · Muzzle all weld to the one Gun bone — they elevate together about the " +
                        "trunnions. They differ in what else they mean:\n" +
                        "• Gun — the tube itself. Defines the breech→muzzle span the pivot above slides along, and " +
                        "it is the part that will KICK BACK when recoil is authored.\n" +
                        "• Cradle — the frame that holds the tube (trunnions, recoil cylinders, the trough it slides " +
                        "in). Kept OUT of the span, because a cradle stops well short of the muzzle and would " +
                        "otherwise shrink it. It is what STAYS while the barrel recoils.\n" +
                        "• Muzzle — a separately-modelled brake or flash hider. Pins the tip exactly instead of " +
                        "guessing at the gun bbox's far extreme, and the run reports the measured fire origin for " +
                        "the Animation Lab's Muzzle offset. Skip it if the brake is modelled INTO the barrel mesh."
                        + (nM + nC > 0 ? $"\n\nMarked: {nC} cradle, {nM} muzzle." : ""), MessageType.None);

                    // RECOIL — the one motion that needs Gun and Cradle on SEPARATE bones, so it lives with them.
                    recoilDist = EditorGUILayout.Slider(new GUIContent("Recoil (fraction of tube)",
                        "How far the tube kicks back when the gun fires, as a fraction of its OWN length — so the " +
                        "dial means the same thing on any model at any scale. 0 = off, and off means no Barrel bone " +
                        "is created at all, so a gun that never recoils costs nothing.\n\n" +
                        "AN ELEVATED GUN WANTS LESS. Sliding back down a raised bore drives the breech DOWN as well " +
                        "as back, so the stroke a level gun can afford will bury the breech in the ground at 45°. " +
                        "Real howitzers solve this with variable recoil — a shorter stroke the higher they elevate. " +
                        "The ~0.3 quoted for an M114 is its LOW-elevation stroke; on the 45° M114 rig, 0.15 is " +
                        "about the ceiling. The run measures the actual clearance and warns you.\n\n" +
                        "This is the only motion that needs the tube and the cradle on SEPARATE bones — mark them " +
                        "Gun and Cradle above, or the whole assembly slides together."), recoilDist, 0f, 0.6f);
                    using (new EditorGUI.DisabledScope(recoilDist <= 0f))
                        recoilLead = EditorGUILayout.IntSlider(new GUIContent("Recoil lead-in (frames)",
                            "Frames the gun holds STILL at the start of the Recoil clip, before the kick. The engine " +
                            "starts the attack clip on its own strike clock — an estimate that can fire while the gun " +
                            "is still slewing onto the target — and the front of this clip is the one part of that " +
                            "timing under our control. Raise it until the kick visibly lands AFTER the turn. " +
                            "24 fps, so 24 = one second of hold. 0 = kick immediately."), recoilLead, 0, 96);
                    using (new EditorGUI.DisabledScope(recoilDist <= 0f))
                        recoilFrames = EditorGUILayout.IntSlider(new GUIContent("Recoil frames",
                            "Length of the 'Recoil' clip at 24 fps. The kick takes the first ~15% and the ride " +
                            "forward gets the rest — that asymmetry is what reads as a shot, so it is derived " +
                            "rather than left to be set wrong. A gun kicks back in a blink and the recuperator eases it home " +
                            "over about a second, so ~16-36 frames is the realistic range. (The proven M114 attack " +
                            "clip is 157 frames, but that is its whole fire cycle — slam, slide home, reload, aiming " +
                            "raise — not the kick.)"), recoilFrames, 3, 160);
                    if (recoilDist > 0f)
                    {
                        int nG = ActiveParts.Count(p => p.role == Role.Gun);
                        if (nG == 0)
                            EditorGUILayout.HelpBox("Recoil needs Gun parts — nothing is marked Gun, so there is no " +
                                "tube to slide.", MessageType.Warning);
                        else if (nC == 0)
                            EditorGUILayout.HelpBox("No Cradle marked: the whole gun assembly will slide back " +
                                "together, mount and all. Mark the frame that holds the tube as Cradle so it stays.",
                                MessageType.Warning);
                        EditorGUILayout.HelpBox("Recoil is a TRANSLATION. Tick Animation Lab ▸ Keep bone " +
                            "translations, or the bake discards it and the gun will not move at all — the clip bake " +
                            "is rotation-only by default. Assign 'Recoil' to the Attack clip.", MessageType.Info);
                    }
                }
                using (new EditorGUI.DisabledScope(ActiveParts.Count(p => p.role == Role.Trail) == 0))
                {
                    EditorGUILayout.HelpBox("Assign after baking: Idle/reference = Deploy · Idle stance = Deploy[" +
                        trailFrames + ".." + trailFrames + "] · Movement = Spin · After-move = Deploy · Pre-move = Deploy[" +
                        trailFrames + "..0]", MessageType.None);
                }
            }

            // WAVE ROCK — a FLOATING unit's idle sway. Independent of wheels: a boat marks nothing but Ignore
            // (to strip parts) and rocks. Rotation-only on a Hull bone, so no Keep-translations needed downstream.
            if (Section(ref foldWave, "Wave rock — floating units",
                    !waveEnabled ? "off"
                    : rockDegrees > 0f || rockPitchDeg > 0f ? $"roll {rockDegrees:0.#}°×{rockRollCycles} · pitch {rockPitchDeg:0.#}°×{rockPitchCycles} · {rockFrames / (float)RockFps:0.0}s" : "on (0°)"))
            {
                // ONE CHECKBOX to disable the whole section (2026-08-01, user request): a wheeled/tracked vehicle needs
                // no sway, and hunting for two amplitude sliders to zero is clumsy. Off by default — floating units tick
                // it on. When off, nothing is authored regardless of the slider values (they stay for when you re-enable).
                waveEnabled = EditorGUILayout.ToggleLeft(new GUIContent("  Enable wave rock",
                    "Idle sway for FLOATING units (boats). Leave OFF for wheeled/tracked vehicles — the wheels' Spin is all they need."), waveEnabled);
                using (new EditorGUI.DisabledScope(!waveEnabled))
                {
                    // TWO INDEPENDENT WAVES, stated plainly (2026-07-31): each swing owns its amplitude in DEGREES and
                    // its own whole cycle count. Ratios and multipliers coupled them and made the outcome unpredictable.
                    EditorGUILayout.LabelField("  Two independent sine waves on the hull. Each: how far (degrees) and how many full swings per clip.", EditorStyles.miniLabel);
                    rockFrames = EditorGUILayout.IntSlider(new GUIContent($"Clip length ({rockFrames / (float)RockFps:0.0}s)",
                        "How long the whole looping clip is, in frames (24 = 1 second). The cycle counts below are per " +
                        "THIS clip, so a longer clip at the same cycle count means slower motion."), rockFrames, 20, 600);

                    EditorGUILayout.LabelField("  Roll — side to side, about the hull's length", EditorStyles.miniBoldLabel);
                    rockDegrees = EditorGUILayout.Slider(new GUIContent("   Roll amount (deg)",
                        "How far the vessel heels each way. 3-8 suits a small boat."), rockDegrees, 0f, 30f);
                    rockRollCycles = EditorGUILayout.IntSlider(new GUIContent("   Roll swings per clip",
                        "How many full roll cycles fit in the clip. Whole numbers only, so the loop never pops."), rockRollCycles, 1, 8);

                    EditorGUILayout.LabelField("  Pitch — bow up and down, across the hull", EditorStyles.miniBoldLabel);
                    rockPitchDeg = EditorGUILayout.Slider(new GUIContent("   Pitch amount (deg)",
                        "How far the bow rises and falls. 0 = a pure beam roll with no pitching at all."), rockPitchDeg, 0f, 30f);
                    rockPitchCycles = EditorGUILayout.IntSlider(new GUIContent("   Pitch swings per clip",
                        "How many full pitch cycles fit in the clip. Set it EQUAL to the roll count for both axes at the " +
                        "same speed; higher makes the bow bob faster than the vessel heels."), rockPitchCycles, 1, 8);
                    rockPitchPhase = EditorGUILayout.Slider(new GUIContent("   Pitch offset (deg)",
                        "How far the pitch wave is shifted against the roll. At EQUAL swing counts this decides the shape: " +
                        "0 keeps them in lockstep so the hull tilts along one fixed diagonal (reads as a single axis), " +
                        "90 traces an ellipse — the hull circling as it bobs — and 180 mirrors that."), rockPitchPhase, 0f, 360f);

                    EditorGUILayout.LabelField("  Axis", EditorStyles.miniBoldLabel);
                    rockAxisChoice = EditorGUILayout.Popup(new GUIContent("   Hull length axis",
                        "Which axis the hull RUNS ALONG — it rolls about this one and pitches about the other horizontal " +
                        "axis. Auto picks the longer horizontal extent. Override if roll and pitch appear swapped."), rockAxisChoice, RockAxisOptions);
                    rockHeading = EditorGUILayout.Slider(new GUIContent("   Axis heading (deg)",
                        "Swings both axes around the vertical together. For a hull that isn't axis-aligned, or to take " +
                        "the swell on the quarter."), rockHeading, -90f, 90f);
                }
            }
            EditorGUILayout.Space(4);

            int wheels = list.Count(x => IsSpinner(x.role));
            bool canRig = wheels > 0 || (waveEnabled && (rockDegrees > 0f || rockPitchDeg > 0f));
            using (new EditorGUI.DisabledScope(!canRig || string.IsNullOrEmpty(outGlb)))
                if (GUILayout.Button(new GUIContent($"Generate rig{(useSourceRig && boneParts.Count > 0 ? " (fast path)" : "")}  →  {(string.IsNullOrEmpty(outGlb) ? "(set the Output GLB)" : Path.GetFileName(outGlb))}",
                        !canRig ? "Mark at least one entry as Wheel / Rotor / Tail rotor — or set a Wave rock amplitude (a floating unit needs no wheels)." : "Runs Blender: rig + Spin action + GLB export + preview."), GUILayout.Height(28)))
                    Vehicleize();
        }

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

        // turntable preview (the real imported preview FBX playing its Spin clip)
        if (inst != null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Preview   (drag = orbit · middle/right-drag = pan · scroll = zoom · click a part row to focus)", EditorStyles.miniBoldLabel);
                // WHICH CLIP (2026-08-22): the rig authors `Spin` (wheels) and, for a split-trail gun, `Deploy`
                // (the arms swinging open). Judge either here rather than waiting for a bake.
                if (previewClips != null && previewClips.Count > 1)
                {
                    // 180, not 90: the names arrive prefixed with the rig's own name ("VehicleRig|Deploy"), so at the
                    // old width every entry truncated to "VehicleRig|D…" — the half that identifies the clip was the
                    // half being cut. Wide enough for the longest name this rigger authors, prefix included.
                    int pick = EditorGUILayout.Popup(previewClipIdx, previewClips.Select(c => c.name).ToArray(), GUILayout.Width(180));
                    if (pick != previewClipIdx)
                    {
                        previewClipIdx = pick; spinClip = previewClips[pick]; spinT = 0f; previewPaused = false; Repaint();
                    }
                }
                previewPaused = GUILayout.Toggle(previewPaused, new GUIContent(previewPaused ? "▶ Play" : "❚❚ Pause",
                    "Freeze the spin so you can judge whether the rotor is level (or inspect a static pose)."),
                    EditorStyles.miniButton, GUILayout.Width(70));
                // FRAME STEP: nudge the sampled time by exactly one clip frame (and pause, so the pose holds). The
                // close-inspection tool for judging an axle: step through a few frames and watch where each blade goes.
                if (GUILayout.Button(new GUIContent("◀", "Step one animation frame BACK (pauses playback)."), EditorStyles.miniButtonLeft, GUILayout.Width(26)) && spinClip != null)
                {
                    previewPaused = true;
                    float fr = spinClip.frameRate > 1f ? spinClip.frameRate : 30f;
                    spinT -= 1f / fr;
                    if (spinT < 0f) spinT += Mathf.Max(spinClip.length, 1f / fr);   // wrap so % never sees a negative
                    Repaint();
                }
                if (GUILayout.Button(new GUIContent("▶", "Step one animation frame FORWARD (pauses playback)."), EditorStyles.miniButtonRight, GUILayout.Width(26)) && spinClip != null)
                {
                    previewPaused = true;
                    float fr = spinClip.frameRate > 1f ? spinClip.frameRate : 30f;
                    spinT += 1f / fr;
                    Repaint();
                }
                showLevelLine = GUILayout.Toggle(showLevelLine, new GUIContent("Level line",
                    "A world-horizontal cross at rotor height. The rotor is level when its blade bar runs PARALLEL to this line; if it slopes, adjust Orientation ▸ Roll X / Pitch Y until it matches."),
                    EditorStyles.miniButton, GUILayout.Width(80));
                showWaterline = GUILayout.Toggle(showWaterline, new GUIContent("Waterline grid",
                    "A level grid at the model's lowest point — the reference to straighten against. The brighter " +
                    "centre line runs along +X (the axis the rig treats as forward), so heading is readable too."),
                    EditorStyles.miniButton, GUILayout.Width(100));
                // CHECKER (2026-08-22, user: "I can't see the wheels spin"): the preview renders the raw model with
                // no material, and a featureless grey disc gives the eye NOTHING to track — a spinning wheel and a
                // still one look identical. A high-contrast checker fixes that, and beats the real tyre texture,
                // which is itself nearly rotationally symmetric. Same blind spot the Animation Lab preview had.
                bool wantChecker = GUILayout.Toggle(previewChecker, new GUIContent("Checker",
                    "Paint the preview with a high-contrast checker so ROTATION is visible — a bare grey wheel looks " +
                    "identical spinning or still. Off = the model's own (usually untextured) look."),
                    EditorStyles.miniButton, GUILayout.Width(70));
                if (wantChecker != previewChecker) { previewChecker = wantChecker; ApplyChecker(previewChecker); Repaint(); }
            }
            // min 400 tall and greedy: the inspection view claims all leftover window height (was fixed 260,
            // leaving dead grey space below in a tall window).
            var rect = GUILayoutUtility.GetRect(200f, 4000f, previewHeight, previewHeight, GUILayout.ExpandWidth(true));
            HandlePreviewInput(rect);
            if (Event.current.type == EventType.Repaint) RenderPreview(rect);
            previewHeight = EditorGUILayout.IntSlider(new GUIContent("Preview height", "Taller preview, or shorter to keep the knobs on screen. The window scrolls either way."), previewHeight, 220, 900);
        }
        EditorGUILayout.EndScrollView();
    }

    void Probe()
    {
        // Re-probing MERGES, never wipes: roles already assigned (by hand or a loaded recipe) are re-applied by
        // part name. This is also how a minimal recipe expands for review — Load recipe, then Probe to surface
        // every unmarked part around the kept markings.
        var kept = new Dictionary<string, Role>();
        foreach (var p0 in parts) if (p0.role != Role.Default) kept[p0.name] = p0.role;   // explicit Body verdicts survive too
        var keptBones = new Dictionary<string, Role>();
        foreach (var b0 in boneParts) if (b0.role != Role.Default) keptBones[b0.name] = b0.role;
        bool hadBones = boneParts.Count > 0;
        parts.Clear(); boneParts.Clear(); DestroyPreview();
        // probe also exports a preview FBX of the SPLIT model, so part rows can zoom/highlight in the turntable
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        string prevDir = "Assets/FactorySource/VehicleLab";
        Directory.CreateDirectory(Path.Combine(projRoot, prevDir));
        string prevRel = prevDir + "/" + Path.GetFileNameWithoutExtension(srcFile) + "_probe.fbx";
        string prevFull = Path.Combine(projRoot, prevRel).Replace('\\', '/');
        if (!RunBlender($"probe \"{srcFile}\" \"{prevFull}\"", out string stdout)) return;
        // Lenient float parse: degenerate shards can emit "nan" (python lowercase — .NET rejects it) — such a value
        // becomes 0 instead of killing the whole probe on one bad line out of thousands.
        float F(string s2) => float.TryParse(s2, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim().Split('|');
            // PART rows carry an optional 6th field: the escape-ray visibility verdict (1 external / 0 interior).
            bool okLen = t.Length == 5 || ((t.Length == 6 || t.Length == 7) && t[0] == "PART");   // 7th = dominant bone (2026-08-20)
            if (!okLen || (t[0] != "PART" && t[0] != "RIGBONE")) continue;
            var c = t[3].Split(','); var s = t[4].Split(',');
            if (c.Length != 3 || s.Length != 3) continue;
            var p = new Part
            {
                name = t[1],
                verts = int.TryParse(t[2], out var v) ? v : 0,
                center = new Vector3(F(c[0]), F(c[1]), F(c[2])),
                size = new Vector3(F(s[0]), F(s[1]), F(s[2])),
                vis = t.Length >= 6 && int.TryParse(t[5], out var vv) ? vv : -1,
                bone = t.Length == 7 ? t[6].Trim() : "",
            };
            var low = p.name.ToLowerInvariant();
            var keptMap = t[0] == "RIGBONE" ? keptBones : kept;
            p.role = keptMap.TryGetValue(p.name, out var kr) ? kr
                   : low.Contains("tail") && (low.Contains("rotor") || low.Contains("prop")) ? Role.TailRotor  // "tail rotor" before the generic rotor guess
                   : low.Contains("fantail") || low.Contains("fenestron") ? Role.TailRotor
                   : low.Contains("rotor") || low.Contains("helix") || low.Contains("blade") || low.Contains("propeller") ? Role.Rotor
                   : low.Contains("wheel") || low.Contains("tyre") || low.Contains("tire") ? Role.Wheel
                   : low.Contains("turret") ? Role.Turret : Role.Default;
            (t[0] == "RIGBONE" ? boneParts : parts).Add(p);
        }
        if (boneParts.Count > 0 && !hadBones) useSourceRig = true;   // first detection: default to the fast path
        if (File.Exists(prevFull))
        {
            AssetDatabase.ImportAsset(prevRel, ImportAssetOptions.ForceUpdate);
            BuildPreview(prevRel);   // no Spin clip yet — a static turntable for part inspection
        }
        status = parts.Count == 0
            ? "Probe found no mesh parts — is this a mesh model? (See the Console for Blender output.)"
            : (boneParts.Count > 0
                ? $"SOURCE IS RIGGED: {boneParts.Count} skinned bones found ({boneParts.Count(x => x.role == Role.Wheel)} auto-guessed as wheels) — fast path ON: mark the bones that spin and Generate rig. " +
                  "Toggle it off for the static shard flow. "
                : "") +
              $"Probed {parts.Count} part(s); {parts.Count(x => x.role == Role.Wheel)} wheel(s), {parts.Count(x => x.role == Role.Turret)} turret(s)" +
              (kept.Count > 0 ? $" ({parts.Count(x => kept.ContainsKey(x.name) && x.role == kept[x.name])} of {kept.Count} earlier markings kept)" : " (auto-guessed)") +
              ". Click a row to see WHICH part it is (zoom + yellow highlight), assign roles, then Generate rig.";
    }

    // Sanity report on the current classification — mirrors the rig script's wheel clustering so the numbers
    // shown here are exactly the bones Vehicleize will build.
    void VerifySelection()
    {
        var report = new List<(string text, string part)>();
        bool warn = false;
        var vlist = ActiveParts;   // fast path verifies the BONE marking (each wheel bone = its own cluster)
        var wheels = vlist.Where(p => IsSpinner(p.role)).ToList();   // Wheel + Rotor + Tail rotor all get a spin bone
        var turrets = vlist.Where(p => p.role == Role.Turret).ToList();

        if (wheels.Count == 0) { report.Add(("✗ No parts marked Wheel / Rotor / Tail rotor — nothing will spin.", null)); warn = true; }
        else
        {
            // Wheels cluster by PROXIMITY (tire+rim+spokes near one hub). Rotors do NOT: each role fuses to ONE hub at
            // its centroid — a rotor disc's blades are far apart but spin as one, exactly what the rig script builds.
            var clusters = new List<(Part anchor, List<Part> members)>();   // wheel hubs only (proximity)
            foreach (var p in wheels.Where(x => x.role == Role.Wheel).OrderByDescending(MaxDim))
            {
                var home = clusters.FirstOrDefault(cl => (p.center - cl.anchor.center).magnitude <= 0.75f * MaxDim(cl.anchor));
                if (home.anchor == null) clusters.Add((p, new List<Part> { p }));
                else home.members.Add(p);
            }
            var rotorGroups = new List<(string label, List<Part> members)>();
            var mains = wheels.Where(x => x.role == Role.Rotor).ToList();
            var tails = wheels.Where(x => x.role == Role.TailRotor).ToList();
            if (mains.Count > 0) rotorGroups.Add(("main rotor", mains));
            if (tails.Count > 0) rotorGroups.Add(("tail rotor", tails));

            report.Add(($"• {wheels.Count} spinning part(s) → {clusters.Count + rotorGroups.Count} spin bone(s):", null));
            int AxleIdx(Part a) => a.size.x <= a.size.y && a.size.x <= a.size.z ? 0 : a.size.y <= a.size.z ? 1 : 2;
            if (clusters.Count > 0)
            {
                float biggest = clusters.Max(c => MaxDim(c.anchor));
                foreach (var c in clusters.Take(12))
                {
                    bool stray = MaxDim(c.anchor) < 0.5f * biggest;
                    if (stray) warn = true;
                    report.Add(($"    wheel ⌀{MaxDim(c.anchor):0.00} at ({c.anchor.center.x:0.00}, {c.anchor.center.y:0.00}, {c.anchor.center.z:0.00}) — {c.members.Count} part(s)" +
                                (stray ? "  ⚠ small anchor — stray shard far from every wheel? (becomes its own bone)" : ""), c.anchor.name));
                }
                if (clusters.Count > 12) report.Add(($"    … and {clusters.Count - 12} more", null));
                // axle-agreement + left/right-mirror are CAR geometry (paired wheels, one shared axle) — WHEELS ONLY.
                if (clusters.Select(c => AxleIdx(c.anchor)).Distinct().Count() > 1)
                { report.Add(("  ⚠ wheel anchors disagree on the axle axis — a stray cluster, or set the Axle axis override.", null)); warn = true; }
                foreach (var c in clusters)
                    if (Mathf.Abs(c.anchor.center.y) > 0.15f &&
                        !clusters.Any(o => o.anchor != c.anchor && Mathf.Abs(o.anchor.center.x - c.anchor.center.x) < 0.2f
                                                                && Mathf.Abs(o.anchor.center.y + c.anchor.center.y) < 0.2f))
                    { report.Add(($"  ⚠ wheel at ({c.anchor.center.x:0.00}, {c.anchor.center.y:0.00}) has no mirrored partner — missed the other side?", c.anchor.name)); warn = true; }
            }
            foreach (var g in rotorGroups)
            {
                var ctr = g.members.Aggregate(Vector3.zero, (a, p) => a + p.center) / g.members.Count;
                int ax = AxleIdx(g.members.OrderByDescending(MaxDim).First());
                report.Add(($"    {g.label} → 1 hub · {g.members.Count} blade part(s) at ({ctr.x:0.00}, {ctr.y:0.00}, {ctr.z:0.00}) · axle {"XYZ"[ax]}", null));
            }
            // center-in-sphere is a coarse test — run it on WHEEL clusters only. A rotor's radius engulfs the fuselage,
            // so testing "inside a rotor" would flag half the body (the earlier false 41-part warning).
            var insideParts = vlist.Where(p => !IsSpinner(p.role) &&
                    clusters.Any(c => MaxDim(p) < 0.9f * MaxDim(c.anchor) &&
                                      (p.center - c.anchor.center).magnitude <= 0.5f * MaxDim(c.anchor)))
                .OrderByDescending(MaxDim).ToList();
            if (insideParts.Count > 0)
            {
                report.Add(($"• {insideParts.Count} unmarked part(s) sit inside wheel volumes — fine if deliberate (static hub rings), else check them (largest first):", null));
                foreach (var p in insideParts.Take(150))
                    report.Add(($"      {p.name}  [{p.role}]  ⌀{MaxDim(p):0.00} at ({p.center.x:0.00}, {p.center.y:0.00}, {p.center.z:0.00})", p.name));
                if (insideParts.Count > 150) report.Add(($"      … and {insideParts.Count - 150} more (tiny)", null));
            }
        }

        // INTERIOR parts (probe's escape-ray verdict) still baked in: provably invisible from outside, so any that
        // aren't Ignored are pure wasted vertex budget. Listed clickable, biggest waste first. Silent when the probe
        // predates the visibility feature (vis == -1 everywhere) or on the fast path (bones carry no verdict).
        var interiorKept = vlist.Where(p => p.vis == 0 && p.role != Role.Ignore).OrderByDescending(p => p.verts).ToList();
        if (interiorKept.Count > 0)
        {
            warn = true;
            report.Add(($"⚠ {interiorKept.Count} interior part(s) not Ignored — provably invisible, {interiorKept.Sum(p => p.verts)} verts still baked in:", null));
            foreach (var p in interiorKept.Take(60))
                report.Add(($"      {p.name}  [{p.role}]  {p.verts} verts", p.name));
            if (interiorKept.Count > 60) report.Add(($"      … and {interiorKept.Count - 60} more", null));
        }

        if (turrets.Count > 0)
        {
            var cen = turrets.Aggregate(Vector3.zero, (a, p) => a + p.center) / turrets.Count;
            report.Add(($"• {turrets.Count} turret part(s) on one Turret bone at ({cen.x:0.00}, {cen.y:0.00}, {cen.z:0.00}).", null));
            var far = turrets.Where(p => (p.center - cen).magnitude > 1.5f).ToList();
            if (far.Count > 0)
            {
                warn = true;
                report.Add(($"  ⚠ {far.Count} turret part(s) far from the turret centroid — accidental marks?", null));
                foreach (var p in far.Take(8))
                    report.Add(($"      {p.name} at ({p.center.x:0.00}, {p.center.y:0.00}, {p.center.z:0.00})", p.name));
                if (far.Count > 8) report.Add(($"      … and {far.Count - 8} more", null));
            }
        }

        int undecided = vlist.Count(p => p.role == Role.Default);
        int edge = vlist.Count(p => p.role == Role.Edgecase);
        if (undecided > 0) { report.Add(($"⚠ {undecided} part(s) still undecided (Default).", null)); warn = true; }
        if (edge > 0) report.Add(($"• {edge} Edgecase part(s) — rig static (like Body), safe.", null));
        if (!warn) report.Add(("Looks sane — ready to generate the rig.", null));

        Debug.Log("[VehicleLab] Verify:\n" + string.Join("\n", report.Select(r => r.text)));
        VerifyReportWindow.Open(this, report, warn);
        status = (warn ? "Verify: warnings (report window / Console). " : "Verify: looks sane. ") + report[0].text;
    }

    // "Show" in the report jumps the Lab to the part — preview highlight AND the list row (selected + scrolled
    // into view), so it can be reclassified on the spot. If the current sliders/filter hide the part, they are
    // relaxed just enough for its row to exist. Non-modal by design: the report stays readable while working.
    internal void FocusPart(string name)
    {
        var p = ActiveParts.FirstOrDefault(x => x.name == name);
        if (p != null)
        {
            if (p.verts < minVerts) minVerts = Mathf.Max(1, p.verts);
            if (MaxDim(p) < minPartSize) minPartSize = 0f;
            if (p.center.z < minHeight) minHeight = p.center.z;
            if (p.center.z > maxHeight) maxHeight = p.center.z;
            if (p.center.y < minWidth) minWidth = p.center.y;
            if (p.center.y > maxWidth) maxWidth = p.center.y;
            if (!MatchesFilter(p.role)) partFilter = 0;
            int idx = ActiveParts.Where(x => VisiblePart(x) && MatchesFilter(x.role) && MatchesVis(x)).ToList().FindIndex(x => x.name == name);
            if (idx >= 0) partsScroll.y = Mathf.Max(0f, idx * 20f - 120f);
        }
        SelectPart(name);
        Repaint();
    }

    class VerifyReportWindow : EditorWindow
    {
        VehicleLabWindow lab; List<(string text, string part)> rows; Vector2 scroll;
        string activePart;   // the row whose Show was pressed last — highlighted, so the reading position is never lost
        public static void Open(VehicleLabWindow lab, List<(string text, string part)> rows, bool warn)
        {
            var w = GetWindow<VerifyReportWindow>(utility: true, title: warn ? "Verify — warnings" : "Verify — looks sane", focus: true);
            w.lab = lab; w.rows = rows; w.activePart = null;
            w.minSize = new Vector2(520, 220);
        }
        void OnGUI()
        {
            if (rows == null) { Close(); return; }   // stale after a domain reload — just re-run Verify
            scroll = EditorGUILayout.BeginScrollView(scroll);
            var wrap = new GUIStyle(EditorStyles.label) { wordWrap = true };
            var wrapActive = new GUIStyle(wrap) { fontStyle = FontStyle.Bold };
            wrapActive.normal.textColor = new Color(1f, 0.85f, 0.1f);   // matches the preview highlight tint
            foreach (var r in rows)
            {
                bool active = r.part != null && r.part == activePart;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField((active ? "▶ " : "") + r.text, active ? wrapActive : wrap);
                    if (r.part != null && lab != null && GUILayout.Button("Show", GUILayout.Width(48)))
                    { activePart = r.part; lab.FocusPart(r.part); }
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }

    // Reset EVERY piece of session state to its declared default. Deliberately exhaustive: a half-reset is the
    // bug this button exists to kill (stale bone rows silently kept the SKM fast path on for an unrigged model).
    void NewModel()
    {
        srcFile = ""; outGlb = ""; lastOutGlb = ""; loadedRecipe = "";
        parts.Clear(); boneParts.Clear(); useSourceRig = false;
        frames = 15; degrees = -360f; axisChoice = 0;
        treadAdvCells = 3; treadCellsPerLink = 4f; tracksStatic = false;
        rockDegrees = 0f; rockFrames = 120; rockAxisChoice = 0; rockHeading = 0f; rockPitchDeg = 2.4f; rockRollCycles = 1; rockPitchCycles = 1; rockPitchPhase = 90f; waveEnabled = false; foldSpin = true; foldWave = false; foldOrient = false; modelRot = Vector3.zero;
        minVerts = 50; minPartSize = 0f; minHeight = -999f; maxHeight = 999f; minWidth = -999f; maxWidth = 999f;
        partFilter = 0; selectedPart = ""; partsScroll = Vector2.zero; previewPan = Vector2.zero;
        DestroyPreview();
        status = "New model: pick a Raw model, then Probe parts. (Wheels optional — a floating unit just needs a Wave rock amplitude.)";
        GUI.FocusControl(null);
        Repaint();
    }

    // WATERLINE / HORIZON GRID: a level reference at the model's base so "is it straight?" is answerable by eye —
    // straightening has nothing to judge against otherwise. Submitted as a LINE MESH through the preview camera
    // (pru.DrawMesh before cam.Render()), NOT as immediate GL after it: raw GL there inherits the GUI matrices and
    // the current render target, so the first attempt drew nothing visible. The centre line runs along +X — the
    // axis the rig treats as forward — and is brighter, so heading reads as well as level.
    static Material lineMat;
    Mesh waterMesh; Vector4 waterKey = Vector4.zero;
    void SubmitWaterline(float radius)
    {
        if (!showWaterline || pru == null) return;
        if (lineMat == null)
        {
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return;
            lineMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMat.SetInt("_ZWrite", 0);
        }
        float half = Mathf.Max(radius * 1.8f, 0.5f);
        float y = bounds.center.y - bounds.extents.y;   // the model's lowest point = where it meets the water
        var c = bounds.center;
        var key = new Vector4(half, y, c.x, c.z);
        if (waterMesh == null || key != waterKey)
        {
            waterKey = key;
            if (waterMesh == null) waterMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            waterMesh.Clear();
            const int Lines = 6;                       // per side of centre
            float step = half / Lines;
            var verts = new List<Vector3>(); var cols = new List<Color>(); var idx = new List<int>();
            var faint = new Color(0.45f, 0.62f, 0.75f, 0.30f);
            for (int i = -Lines; i <= Lines; i++)
            {
                float o = i * step;
                bool axis = i == 0;
                AddLine(verts, cols, idx, new Vector3(c.x - half, y, c.z + o), new Vector3(c.x + half, y, c.z + o),
                        axis ? new Color(0.55f, 0.85f, 1f, 0.95f) : faint);
                AddLine(verts, cols, idx, new Vector3(c.x + o, y, c.z - half), new Vector3(c.x + o, y, c.z + half),
                        axis ? new Color(0.55f, 0.85f, 1f, 0.5f) : faint);
            }
            waterMesh.SetVertices(verts);
            waterMesh.SetColors(cols);
            waterMesh.SetIndices(idx.ToArray(), MeshTopology.Lines, 0);
        }
        pru.DrawMesh(waterMesh, Matrix4x4.identity, lineMat, 0);
    }
    // A bright WORLD-HORIZONTAL cross at the model's top (rotor height): the rotor is level when its blade bar runs
    // parallel to this line. Unlike the waterline grid (at the bottom), it sits right at the rotor for a direct compare.
    Mesh levelMesh; Vector4 levelKey = Vector4.zero;
    void SubmitLevelLine(float radius)
    {
        if (!showLevelLine || pru == null) return;
        if (lineMat == null)
        {
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return;
            lineMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMat.SetInt("_ZWrite", 0);
        }
        float half = Mathf.Max(radius * 1.9f, 0.5f);
        float y = bounds.center.y + bounds.extents.y * 0.92f;   // near the top = rotor height
        var c = bounds.center;
        var key = new Vector4(half, y, c.x, c.z);
        if (levelMesh == null || key != levelKey)
        {
            levelKey = key;
            if (levelMesh == null) levelMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            levelMesh.Clear();
            var verts = new List<Vector3>(); var cols = new List<Color>(); var idx = new List<int>();
            var col = new Color(1f, 0.82f, 0.15f, 0.95f);   // bright amber, dead horizontal
            AddLine(verts, cols, idx, new Vector3(c.x - half, y, c.z), new Vector3(c.x + half, y, c.z), col);
            AddLine(verts, cols, idx, new Vector3(c.x, y, c.z - half), new Vector3(c.x, y, c.z + half), col);
            levelMesh.SetVertices(verts); levelMesh.SetColors(cols);
            levelMesh.SetIndices(idx.ToArray(), MeshTopology.Lines, 0);
        }
        pru.DrawMesh(levelMesh, Matrix4x4.identity, lineMat, 0);
    }
    static void AddLine(List<Vector3> v, List<Color> c, List<int> i, Vector3 a, Vector3 b, Color col)
    {
        i.Add(v.Count); i.Add(v.Count + 1);
        v.Add(a); v.Add(b); c.Add(col); c.Add(col);
    }

    // A collapsible section header — the Sound Studio's, so both windows read the same. Uses Foldout (not
    // BeginFoldoutHeaderGroup) so there is no strict End pairing to balance, with a RIGHT-ALIGNED grey summary
    // shown only WHILE FOLDED: a collapsed window still says at a glance which sections carry a configuration.
    static GUIStyle sectionSummaryStyle;
    static bool Section(ref bool state, string title, string summary = null)
    {
        EditorGUILayout.Space(4);
        var rect = GUILayoutUtility.GetRect(1, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
        state = EditorGUI.Foldout(rect, state, title, true, EditorStyles.foldoutHeader);
        if (!state && !string.IsNullOrEmpty(summary))
        {
            if (sectionSummaryStyle == null)
                sectionSummaryStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight, normal = { textColor = new Color(0.55f, 0.75f, 0.55f) } };
            var r2 = rect; r2.xMin = rect.xMin + rect.width * 0.35f;
            GUI.Label(r2, summary, sectionSummaryStyle);
        }
        return state;
    }

    void SaveRecipe()
    {
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        Directory.CreateDirectory(Path.Combine(projRoot, RecipesDir));
        // Default to the CURRENT recipe's name, not the source model's (2026-08-19 user find: saved as prod3,
        // the next Save suggested prod2 again — the srcFile-derived default silently reverted the name).
        string def = !string.IsNullOrEmpty(loadedRecipe) ? loadedRecipe
                   : Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(srcFile) ? "vehicle" : srcFile);
        string p = EditorUtility.SaveFilePanel("Save vehicleize recipe", Path.Combine(projRoot, RecipesDir), def, "json");
        if (string.IsNullOrEmpty(p)) return;
        var r = new Recipe
        {
            srcFile = srcFile, outGlb = outGlb, frames = frames, axisChoice = axisChoice, minVerts = minVerts, degrees = degrees,
            parts = parts, boneParts = boneParts, useSourceRig = useSourceRig, treadAdvCells = treadAdvCells, treadCellsPerLink = treadCellsPerLink,
            // orientation + tread isolation + wave rock — the rest of what the bake command consumes
            tracksStatic = tracksStatic, spinEnabled = spinEnabled, modelRot = modelRot, waveEnabled = waveEnabled,
            trailSpreadDeg = trailSpreadDeg, trailFrames = trailFrames, gunPivot = gunPivot, gunDeployElev = gunDeployElev, recoilDist = recoilDist, recoilFrames = recoilFrames, recoilLead = recoilLead,
            rockDegrees = rockDegrees, rockFrames = rockFrames, rockAxisChoice = rockAxisChoice, rockHeading = rockHeading,
            rockPitchDeg = rockPitchDeg, rockRollCycles = rockRollCycles, rockPitchCycles = rockPitchCycles, rockPitchPhase = rockPitchPhase,
        };
        File.WriteAllText(p, JsonUtility.ToJson(r, true));
        AssetDatabase.Refresh();
        loadedRecipe = Path.GetFileNameWithoutExtension(p);   // reflect the just-saved recipe in the combobox
        status = "Recipe saved: " + p;
    }

    void LoadRecipeFromPath(string p)
    {
        if (string.IsNullOrEmpty(p) || !File.Exists(p)) return;
        try
        {
            string json = File.ReadAllText(p);
            var r = JsonUtility.FromJson<Recipe>(json);
            if (r == null || r.parts == null) { status = "Not a vehicleize recipe: " + p; return; }
            // HONESTY NOTE (2026-08-20): a recipe saved before a feature existed carries no key for it, and
            // JsonUtility silently fills the C# default — which is correct loading, but INVISIBLE (the canoe's
            // wave config "loss" took GLB forensics to diagnose). JsonUtility can't tell absent from default,
            // so key-presence in the raw text is the honest signal; one representative key per feature era.
            var predates = new List<string>();
            void Chk(string key, string label) { if (!json.Contains("\"" + key + "\"")) predates.Add(label); }
            Chk("boneParts", "source-rig fast path");
            Chk("treadAdvCells", "tread dials");
            Chk("tracksStatic", "orientation + static tracks");
            Chk("waveEnabled", "wave rock");
            Chk("spinEnabled", "spin switch");
            DestroyPreview();
            srcFile = r.srcFile; outGlb = r.outGlb; frames = r.frames; axisChoice = r.axisChoice; minVerts = r.minVerts; degrees = r.degrees;
            treadAdvCells = r.treadAdvCells > 0 ? r.treadAdvCells : 3;   // pre-knob recipes default to road-wheel sync
            treadCellsPerLink = r.treadCellsPerLink > 0f ? r.treadCellsPerLink : 4f;
            // orientation + tread isolation + wave rock: fully RESTORE them (so a wheeled recipe overwrites a boat's
            // rock and vice-versa — no leak between models). off/zero is the safe neutral for a pre-2026-08-01 recipe;
            // the counted fields guard against a missing-key 0 the way treadAdvCells does.
            modelRot = r.modelRot; tracksStatic = r.tracksStatic; spinEnabled = r.spinEnabled;
            trailSpreadDeg = r.trailSpreadDeg; trailFrames = r.trailFrames; gunPivot = r.gunPivot; gunDeployElev = r.gunDeployElev; recoilDist = r.recoilDist; recoilFrames = r.recoilFrames; recoilLead = r.recoilLead;
            waveEnabled = r.waveEnabled; rockDegrees = r.rockDegrees; rockAxisChoice = r.rockAxisChoice; rockHeading = r.rockHeading;
            rockPitchDeg = r.rockPitchDeg; rockPitchPhase = r.rockPitchPhase;
            rockFrames = r.rockFrames > 0 ? r.rockFrames : 120;
            rockRollCycles = r.rockRollCycles > 0 ? r.rockRollCycles : 1;
            rockPitchCycles = r.rockPitchCycles > 0 ? r.rockPitchCycles : 1;
            parts = r.parts;
            boneParts = r.boneParts ?? new List<Part>();   // pre-fast-path recipes have no bone list
            useSourceRig = r.useSourceRig && boneParts.Count > 0;
            loadedRecipe = Path.GetFileNameWithoutExtension(p);   // reflect the loaded recipe in the combobox
            status = $"Recipe loaded ({parts.Count} parts{(boneParts.Count > 0 ? $", {boneParts.Count} source bones, fast path {(useSourceRig ? "ON" : "off")}" : "")}, {ActiveParts.Count(x => x.role == Role.Wheel)} wheels). " +
                     "generate the rig directly — or press Probe to list ALL parts for review (your marked roles are kept, plus the preview returns for click-to-highlight)." +
                     (predates.Count > 0 ? $"\nNOTE — recipe predates: {string.Join(", ", predates)}. Those loaded as safe defaults; check the dials and Save to modernize it." : "");
        }
        catch (Exception e) { status = "Recipe load failed: " + e.Message; }
    }

    // Zoom the preview onto one row's geometry and tint it — restores the previous selection's materials first.
    // "" = full view. Two kinds of row: a mesh SHARD (matched by renderer name, exact before prefix — ".100" must
    // not grab ".1000") or, on the source-skeleton fast path, a BONE. No renderer is named after a bone, so a bone
    // row highlights every shard whose vertex weights point dominantly at it (2026-08-20: the Ehrhardt's bone rows
    // "stopped" highlighting — they never could; only shard rows did. Now both do).
    void SelectPart(string name)
    {
        if (highlightedRenderers != null)
            for (int i = 0; i < highlightedRenderers.Count; i++)
                try { if (highlightedRenderers[i] != null) highlightedRenderers[i].sharedMaterials = highlightedOriginals[i]; } catch { }
        highlightedRenderers = null; highlightedOriginals = null;
        selectedPart = name;
        boundsValid = false;   // re-derive (full model or the part) on next render
        previewPan = Vector2.zero;   // a part focus should CENTER the part — a leftover pan would frame empty space
        if (inst == null || string.IsNullOrEmpty(name)) return;
        var all = inst.GetComponentsInChildren<Renderer>();
        var hits = new List<Renderer>();
        var byName = all.FirstOrDefault(x => x != null && x.gameObject.name == name)
                  ?? all.FirstOrDefault(x => x != null && x.gameObject.name.StartsWith(name));
        if (byName != null) hits.Add(byName);
        else if (ShardsByBone(all).TryGetValue(name, out var shards)) hits.AddRange(shards);
        if (hits.Count == 0) return;
        if (highlightMat == null)
        {
            var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            highlightMat = new Material(sh) { color = new Color(1f, 0.85f, 0.1f) };
            highlightMat.hideFlags = HideFlags.HideAndDontSave;
        }
        highlightedRenderers = hits;
        highlightedOriginals = hits.Select(r => r.sharedMaterials).ToList();
        Bounds b = hits[0].bounds;
        foreach (var r in hits)
        {
            r.sharedMaterials = Enumerable.Repeat(highlightMat, r.sharedMaterials.Length).ToArray();
            b.Encapsulate(r.bounds);
        }
        bounds = b; bounds.Expand(bounds.size.magnitude * 0.6f + 0.1f); boundsValid = true;   // frame the selection with context
    }

    // bone name → the skinned shards whose vertex weights point dominantly at that bone. Built once per preview
    // instance (3,350 shards × boneWeights is a one-off cost) and cleared with the preview.
    Dictionary<string, List<Renderer>> boneShards;
    Dictionary<string, List<Renderer>> ShardsByBone(Renderer[] all)
    {
        if (boneShards != null) return boneShards;
        boneShards = new Dictionary<string, List<Renderer>>();
        // Preferred source (2026-08-20): the probe reports each shard's dominant bone in its PART line, so the preview
        // no longer needs skin weights (the skinned preview export was the 84 s probe hog). Renderers are matched by
        // shard name; the weight tally below remains as the fallback for skinned previews (the rig-mode FBX).
        var byName = new Dictionary<string, Renderer>();
        foreach (var r in all) if (r != null && !byName.ContainsKey(r.gameObject.name)) byName[r.gameObject.name] = r;
        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p.bone) || !byName.TryGetValue(p.name, out var rr)) continue;
            if (!boneShards.TryGetValue(p.bone, out var l)) boneShards[p.bone] = l = new List<Renderer>();
            l.Add(rr);
        }
        if (boneShards.Count > 0) return boneShards;
        foreach (var r in all)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr == null || smr.sharedMesh == null || smr.bones == null || smr.bones.Length == 0) continue;
            var bw = smr.sharedMesh.boneWeights;
            if (bw == null || bw.Length == 0) continue;
            var tally = new Dictionary<int, float>();
            foreach (var w in bw)
            {
                if (w.weight0 > 0f) tally[w.boneIndex0] = (tally.TryGetValue(w.boneIndex0, out var a) ? a : 0f) + w.weight0;
                if (w.weight1 > 0f) tally[w.boneIndex1] = (tally.TryGetValue(w.boneIndex1, out var c) ? c : 0f) + w.weight1;
            }
            if (tally.Count == 0) continue;
            int best = tally.OrderByDescending(kv => kv.Value).First().Key;
            if (best < 0 || best >= smr.bones.Length || smr.bones[best] == null) continue;
            string bn = smr.bones[best].name;
            if (!boneShards.TryGetValue(bn, out var list)) boneShards[bn] = list = new List<Renderer>();
            list.Add(r);
        }
        return boneShards;
    }

    void Vehicleize()
    {
        // OVERWRITE GUARD: the output path is explicit and user-owned — an existing file (e.g. a HAND-MADE rig like
        // the original Ehrhardt_Spin.glb) is never clobbered without an explicit yes.
        if (File.Exists(outGlb) && !EditorUtility.DisplayDialog("Overwrite existing file?",
                $"'{outGlb}' already exists.\n\nOverwrite it? (If this is a hand-made rig, pick a different output path instead.)",
                "Overwrite", "Cancel"))
        { status = "Cancelled — pick a different Output GLB path."; return; }
        DestroyPreview();
        string baseName = Path.GetFileNameWithoutExtension(outGlb);
        lastOutGlb = outGlb.Replace('\\', '/');
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        string prevDir = "Assets/FactorySource/VehicleLab";
        Directory.CreateDirectory(Path.Combine(projRoot, prevDir));
        string prevRel = prevDir + "/" + baseName + "_preview.fbx";
        string prevFull = Path.Combine(projRoot, prevRel).Replace('\\', '/');
        // Part lists travel via FILE (@path): a thorough marking session is hundreds of shard names — far past
        // the ~32k Windows command-line limit that an inline ;-joined string would hit.
        string wheelsFile = Path.Combine(projRoot, prevDir, baseName + "_wheels.txt").Replace('\\', '/');
        string turretsFile = Path.Combine(projRoot, prevDir, baseName + "_turrets.txt").Replace('\\', '/');
        bool fast = FastPath;          // fast path: spin the marked SOURCE BONES, reuse the artist skeleton (one definition, see ActiveParts)
        var src = ActiveParts;         // the SAME list the UI counts roles from - they cannot disagree
        string ignoreFile = Path.Combine(projRoot, prevDir, baseName + "_ignore.txt").Replace('\\', '/');
        // Rotor + TailRotor travel in their OWN lists (not the wheels file): the rig script fuses each group into ONE
        // hub bone at its centroid so a wide rotor disc spins as one, instead of the wheel path's per-proximity bones
        // that shred the blades into separate pinwheels. Their "rotorcraft-ness" (continuous spin + flyer) is conveyed
        // to the bake step via the success message below.
        bool hasRotor = src.Any(p => p.role == Role.Rotor || p.role == Role.TailRotor);
        File.WriteAllLines(wheelsFile, src.Where(p => p.role == Role.Wheel).Select(p => p.name).ToArray());
        string rotorsFile = Path.Combine(projRoot, prevDir, baseName + "_rotors.txt").Replace('\\', '/');
        string tailrotorsFile = Path.Combine(projRoot, prevDir, baseName + "_tailrotors.txt").Replace('\\', '/');
        File.WriteAllLines(rotorsFile, src.Where(p => p.role == Role.Rotor).Select(p => p.name).ToArray());
        File.WriteAllLines(tailrotorsFile, src.Where(p => p.role == Role.TailRotor).Select(p => p.name).ToArray());
        File.WriteAllLines(turretsFile, src.Where(p => p.role == Role.Turret).Select(p => p.name).ToArray());
        // Ignore = DELETED from the output (static path; unused Sketchfab option meshes). Fast path: bones can't be "deleted" — unused.
        File.WriteAllLines(ignoreFile, src.Where(p => p.role == Role.Ignore).Select(p => p.name).ToArray());
        string tracksFile = Path.Combine(projRoot, prevDir, baseName + "_tracks.txt").Replace('\\', '/');
        File.WriteAllLines(tracksFile, src.Where(p => p.role == Role.Caterpillar).Select(p => p.name).ToArray());
        string gunsFile = Path.Combine(projRoot, prevDir, baseName + "_guns.txt").Replace('\\', '/');
        File.WriteAllLines(gunsFile, src.Where(p => p.role == Role.Gun).Select(p => p.name).ToArray());
        // TRAILS (2026-08-22, the M114 deploy): split-trail arms that swing OPEN when the gun deploys. Each gets a bone
        // hinged at its body end; the rigger authors a separate "Deploy" action that spreads them, so the state
        // machine can use Deploy[N..N] as the deployed stance, Deploy as the unfold and Deploy[N..0] as the fold —
        // while `Spin` keeps the wheels rolling with the legs folded (their rest).
        string trailsFile = Path.Combine(projRoot, prevDir, baseName + "_trails.txt").Replace('\\', '/');
        File.WriteAllLines(trailsFile, src.Where(p => p.role == Role.Trail).Select(p => p.name).ToArray());
        // MUZZLE (2026-08-22): a separately-modelled muzzle brake / flash hider. It gets NO bone of its own — the
        // brake is bolted to the tube, so it must elevate and recoil with it — the rigger just welds it to the Gun
        // bone. What marking it buys is a PRECISE muzzle tip: the breech→muzzle span that Gun pivot measures against
        // stops guessing at the gun bbox's far extreme, and the rigger can report the exact fire origin.
        string muzzlesFile = Path.Combine(projRoot, prevDir, baseName + "_muzzles.txt").Replace('\\', '/');
        File.WriteAllLines(muzzlesFile, src.Where(p => p.role == Role.Muzzle).Select(p => p.name).ToArray());
        // CRADLE (2026-08-22): the frame that HOLDS the tube — trunnions, recoil cylinders, the trough the barrel
        // slides in. It welds to the Gun bone (cradle and tube elevate together about the trunnions) but is kept out
        // of the breech→muzzle span, since a cradle stops well short of the muzzle and would otherwise shrink it.
        // Once recoil exists it is the part that STAYS while the barrel kicks back — the reason it is its own role.
        string cradlesFile = Path.Combine(projRoot, prevDir, baseName + "_cradles.txt").Replace('\\', '/');
        File.WriteAllLines(cradlesFile, src.Where(p => p.role == Role.Cradle).Select(p => p.name).ToArray());
        string axis = axisChoice == 0 ? "AUTO" : AxisOptions[axisChoice];
        string tailAxis = tailAxisChoice == 0 ? "AUTO" : AxisOptions[tailAxisChoice];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!RunBlender($"{(fast ? "rigfast" : "rig")} \"{srcFile}\" \"{lastOutGlb}\" \"{prevFull}\" \"@{wheelsFile}\" \"@{turretsFile}\" {axis} {frames} {(spinEnabled ? degrees : 0f).ToString("0.#", inv)} \"@{ignoreFile}\" \"@{tracksFile}\" \"@{gunsFile}\" {treadAdvCells} 1 1 {treadCellsPerLink.ToString("0.##", inv)} {(tracksStatic || !spinEnabled ? "1" : "0")} {(waveEnabled ? rockDegrees : 0f).ToString("0.##", inv)} {rockFrames} {(rockAxisChoice == 1 ? "X" : rockAxisChoice == 2 ? "Y" : "AUTO")} {rockHeading.ToString("0.##", inv)} {(waveEnabled ? rockPitchDeg : 0f).ToString("0.##", inv)} {rockPitchCycles} \"{modelRot.x.ToString("0.##", inv)},{modelRot.y.ToString("0.##", inv)},{modelRot.z.ToString("0.##", inv)}\" {rockPitchPhase.ToString("0.##", inv)} {rockRollCycles} \"@{rotorsFile}\" \"@{tailrotorsFile}\" {tailAxis} {tailYawAdj.ToString("0.##", inv)} {tailPitchAdj.ToString("0.##", inv)} \"@{trailsFile}\" {trailSpreadDeg.ToString("0.##", inv)} {trailFrames} {gunPivot.ToString("0.###", inv)} {gunDeployElev.ToString("0.##", inv)} \"@{muzzlesFile}\" \"@{cradlesFile}\" {recoilDist.ToString("0.###", inv)} {recoilFrames} {recoilLead}", out string stdout)) return;
        // SUCCESS = THE SCRIPT'S OWN FINAL MARKER (the documented Blender trap: it exits 0 even when the python
        // script crashes mid-way — without this gate a half-run printed a fake "DONE" with no file on disk).
        string done = stdout.Split('\n').FirstOrDefault(l => l.Contains("VEHICLE RIG DONE"));
        if (done == null || !File.Exists(lastOutGlb))
        {
            status = "Vehicleize FAILED — Blender crashed mid-run (no completion marker" + (File.Exists(lastOutGlb) ? "" : ", no output file") + "). Full Blender output in the Console.";
            Debug.LogError("[VehicleLab] rig run did not complete. Full output:\n" + stdout);
            return;
        }
        AssetDatabase.ImportAsset(prevRel, ImportAssetOptions.ForceUpdate);
        var imp = AssetImporter.GetAtPath(prevRel) as ModelImporter;
        if (imp != null && (imp.animationType != ModelImporterAnimationType.Generic || !imp.importAnimation))
        { imp.animationType = ModelImporterAnimationType.Generic; imp.importAnimation = true; imp.SaveAndReimport(); }
        BuildPreview(prevRel);
        // surface the BONE TOTAL prominently (user request): the budget lines are the difference between a
        // clean unit and the 256-wall / twitch-ceiling diseases — they were buried in the Console until now
        string bones = stdout.Split('\n').FirstOrDefault(l => l.Contains("VEHICLE armature:"))?.Trim() ?? "";
        string hybrid = string.Join("\n", stdout.Split('\n').Where(l => l.Contains("HYBRID v2") || l.Contains("BONE BUDGET CLAMP")).Select(l => l.Trim()));
        string bakeRecipe = hasRotor
            ? "Animation Lab ▸ State-driven OFF (a rotor spins CONTINUOUSLY), Idle/reference = Spin (full), Convert raw rig ON, " +
              "Fix 100× OFF, Auto-ground OFF (flyer), Keep bone translations ✓. Bake."
            : "Animation Lab ▸ State-driven, Idle/reference = Spin[0..0], Movement = Spin (full), Convert raw rig ON, " +
              "Fix 100× OFF, Auto-ground ON, Keep bone translations ✓. Bake.";
        // The MUZZLE lines are the measured fire origin — the value the Animation Lab's Muzzle offset dial is
        // otherwise found by iterate-and-relaunch. Worth surfacing, not leaving in the Console.
        // WARNINGS ride along too (2026-08-22): the rigger's own safety findings — a recoil stroke that buries the
        // breech, and a split-trail pair that came out un-mirrored — were printed only to the Console, i.e. exactly
        // where a "the rig looks wrong" question does NOT get answered. They are the lines most worth reading.
        string muzzle = string.Join("\n", stdout.Split('\n')
            .Where(l => l.Contains("VEHICLE MUZZLE") || l.Contains("VEHICLE CRADLE")
                     || l.Contains("*** WARNING") || l.Contains("DEPLOY gun: SKIPPED"))
            .Select(l => l.Trim()));
        status = $"DONE → {lastOutGlb}\n{bones}\n{hybrid}\n{done}\n{muzzle}\n\nNext: Factory ▸ Browse this GLB, Size as usual; " + bakeRecipe;
        EditorGUIUtility.systemCopyBuffer = lastOutGlb;   // ready to paste into the Factory's Browse field
    }

    bool RunBlender(string args, out string stdout)
    {
        stdout = "";
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        string script = Path.Combine(projRoot, "Tools", "vehicle_rig.py");
        if (!File.Exists(script)) { status = "Tools/vehicle_rig.py missing."; return false; }
        try
        {
            EditorUtility.DisplayProgressBar("Vehicle Lab", "Running Blender…", 0.4f);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var p = new System.Diagnostics.Process();
            p.StartInfo.FileName = UniversalBaker.FindBlender();
            p.StartInfo.Arguments = $"--background --python \"{script}\" -- {args}";
            p.StartInfo.UseShellExecute = false; p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true; p.StartInfo.RedirectStandardError = true;
            p.Start();
            if (!UniversalBaker.RunBounded(p, 300000, out stdout, out string stderr)) { status = "Blender timed out (5 min)."; return false; }
            if (stdout.Contains("VEHICLE ERROR"))
            { status = stdout.Split('\n').FirstOrDefault(l => l.Contains("VEHICLE ERROR")) ?? "Blender step failed."; Debug.LogError("[VehicleLab]\n" + stdout + "\n--- stderr ---\n" + stderr); return false; }
            sw.Stop();
            // console headline = the OUTCOME (collapsed console shows the first line; the old raw dump buried the
            // DONE line and spammed thousands of PART rows)
            var lines = stdout.Split('\n').Where(l => l.StartsWith("VEHICLE")).Select(l => l.TrimEnd()).ToList();
            int partCount = stdout.Split('\n').Count(l => l.StartsWith("PART|"));
            string headline = lines.LastOrDefault(l => l.Contains("RIG DONE"))
                            ?? (partCount > 0 ? $"probe: {partCount} part(s) found" : lines.LastOrDefault() ?? "run complete");
            Debug.Log($"[VehicleLab] {headline}   ({sw.Elapsed.TotalSeconds:0.0}s)\n" + string.Join("\n", lines));
            return true;
        }
        catch (Exception e) { status = "Blender run failed: " + e.Message; return false; }
        finally { EditorUtility.ClearProgressBar(); }
    }

    // ---- turntable preview: the imported preview FBX rendered as a REAL instance (AddSingleGO — Law 4: never
    // hand-rolled mesh draws), its Spin clip sampled on a loop so the wheels visibly turn. Drag orbits, wheel zooms.
    void BuildPreview(string prevRel)
    {
        DestroyPreview();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prevRel);
        if (prefab == null) return;
        previewClips = AssetDatabase.LoadAllAssetsAtPath(prevRel).OfType<AnimationClip>()
                                    .Where(c => c != null && !c.name.StartsWith("__preview")).OrderBy(c => c.name).ToList();
        if (previewClipIdx >= previewClips.Count) previewClipIdx = 0;
        spinClip = previewClips.Count > 0 ? previewClips[previewClipIdx] : null;
        if (pru == null) pru = new PreviewRenderUtility();
        inst = Instantiate(prefab);
        pru.AddSingleGO(inst);
        checkerOriginals = null;
        if (previewChecker) ApplyChecker(true);   // fresh instance: repaint it
        boundsValid = false; spinT = 0f;
        // REFRAME on every fresh preview (2026-07-27 user request): a leftover pan/zoom from inspecting the
        // previous rig can frame empty space ("I lost the item from view" after re-Vehicleize). Orbit angle is
        // kept — it's a viewing preference, not a framing offset.
        previewPan = Vector2.zero; zoom = 1.5f;
    }
    // Paint (or unpaint) the live preview instance with the checker skin. Originals are remembered per renderer so
    // toggling off restores exactly what the import gave us. Clears any part highlight first: the highlight stores
    // "the materials that were there" to restore later, and swapping underneath it would strand stale entries.
    void ApplyChecker(bool on)
    {
        if (inst == null) return;
        if (highlightedRenderers != null) SelectPart(null);
        if (on)
        {
            if (checkerTex == null)
            {
                const int N = 64, CELL = 8;   // 8-px squares: reads clearly at any turntable zoom
                checkerTex = new Texture2D(N, N) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                        checkerTex.SetPixel(x, y, ((x / CELL + y / CELL) & 1) == 0
                            ? new Color(0.82f, 0.82f, 0.80f) : new Color(0.20f, 0.22f, 0.26f));
                checkerTex.Apply();
            }
            if (checkerMat == null)
            {
                checkerMat = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave, mainTexture = checkerTex };
                checkerMat.SetFloat("_Glossiness", 0.05f);
                checkerMat.mainTextureScale = new Vector2(4f, 4f);   // a few squares per part, whatever the UV layout
            }
            checkerOriginals = new Dictionary<Renderer, Material[]>();
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                checkerOriginals[r] = r.sharedMaterials;
                r.sharedMaterials = Enumerable.Repeat(checkerMat, Mathf.Max(1, r.sharedMaterials.Length)).ToArray();
            }
        }
        else if (checkerOriginals != null)
        {
            foreach (var kv in checkerOriginals)
                try { if (kv.Key != null) kv.Key.sharedMaterials = kv.Value; } catch { }
            checkerOriginals = null;
        }
    }

    void DestroyPreview()
    {
        if (inst != null) { DestroyImmediate(inst); inst = null; }
        boneShards = null; highlightedRenderers = null; highlightedOriginals = null;   // per-instance caches die with it
        spinClip = null; previewClips = null; boundsValid = false;
        selectedPart = ""; highlightedRenderers = null; highlightedOriginals = null;
    }
    void HandlePreviewInput(Rect rect)
    {
        var e = Event.current;
        if (!rect.Contains(e.mousePosition)) return;
        // zoom-out ceiling 50 (was 5): with a TINY part focused, distance scales off its bounds — seeing the part
        // in the context of the whole vehicle needs an order of magnitude more headroom.
        if (e.type == EventType.ScrollWheel) { zoom = Mathf.Clamp(zoom * Mathf.Pow(1.12f, e.delta.y > 0 ? 1f : -1f), 0.2f, 50f); e.Use(); }
        else if (e.type == EventType.MouseDrag && e.button == 0) { orbit += new Vector2(e.delta.x, -e.delta.y) * 0.7f; orbit.y = Mathf.Clamp(orbit.y, -89f, 89f); e.Use(); }
        else if (e.type == EventType.MouseDrag && (e.button == 1 || e.button == 2))
        {
            // middle/right-drag pans in the camera plane (ported from the Factory preview, 2026-07-27 user
            // request — inspecting corner details like the spoke wheels); scaled by radius at render time
            previewPan += new Vector2(-e.delta.x, e.delta.y) * 0.0035f;
            e.Use();
        }
    }
    void RenderPreview(Rect rect)
    {
        if (inst == null || pru == null) return;
        if (spinClip != null && spinClip.length > 0.01f)
            spinClip.SampleAnimation(inst, spinT % spinClip.length);
        if (!boundsValid)
        {
            bool first = true;
            foreach (var r in inst.GetComponentsInChildren<Renderer>())
            { if (r == null) continue; if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds); }
            boundsValid = !first;
            if (boundsValid) fullRadius = bounds.extents.magnitude;
        }
        if (!boundsValid) return;
        pru.BeginPreview(rect, GUIStyle.none);
        var cam = pru.camera;
        float radius = Mathf.Max(bounds.extents.magnitude, 0.1f);
        float dist = radius * 2f * zoom;
        var rot = Quaternion.Euler(-orbit.y, orbit.x, 0f);
        // pan shifts the look target along the camera's right/up axes (× dist so it tracks the cursor at any zoom)
        var lookAt = bounds.center + rot * new Vector3(previewPan.x, previewPan.y, 0f) * dist;
        cam.transform.position = lookAt + rot * (Vector3.back * dist);
        cam.transform.rotation = Quaternion.LookRotation(lookAt - cam.transform.position);
        // far-plane margin uses the WHOLE model's radius: with a tiny part focused, `radius` is that part's — a
        // part-scaled margin put the far plane just behind the shard and visibly carved the vehicle away on zoom-out.
        cam.nearClipPlane = 0.01f; cam.farClipPlane = dist + Mathf.Max(radius, fullRadius) * 4f; cam.fieldOfView = 30f;
        pru.lights[0].intensity = 1.3f;
        pru.lights[0].transform.rotation = Quaternion.Euler(45f, 45f, 0f);
        if (pru.lights.Length > 1) pru.lights[1].intensity = 0.6f;
        pru.ambientColor = new Color(0.3f, 0.3f, 0.3f);
        SubmitWaterline(radius);   // queued for THIS render — DrawMesh must precede cam.Render()
        SubmitLevelLine(radius);   // the horizontal reference at rotor height
        cam.Render();
        GUI.DrawTexture(rect, pru.EndPreview(), ScaleMode.StretchToFill, false);
    }
}
