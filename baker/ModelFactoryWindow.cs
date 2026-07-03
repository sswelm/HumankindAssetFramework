// ModelFactoryWindow.cs (ENC editor) — Tools > Universal Model Factory.
// Create a NEW 3D resource or pick an existing one, choose a target pawn description + a model file (.glb/.obj/.fbx),
// and configure EVERYTHING we learned makes a model work: rotation, position (z = waterline), size, normals mode,
// smoothing angle, conversion grid. Press Bake -> skeleton + atlas + a JSON registry entry the in-game plugin reads.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class ModelFactoryWindow : EditorWindow
{
    ModelDef cur = new ModelDef();
    int selected;                       // 0 = <New>, else index into `existing`
    string[] existing = { "<New>" };
    string status = "";
    Vector2 scroll;
    bool showSettings;

    // Cheap animation probe (no Blender), cached per model-file path. State: 0 = unknown (allow), 1 = animation
    // detected (allow + hint), 2 = definitely none (disable the Animated toggle). Keeps the checkbox from being ticked
    // on a static model. Runs once when the path changes, not every OnGUI frame.
    string animProbeFile = "";   // sentinel != any real path so the first real path always probes
    int animProbeState;

    [MenuItem("Tools/Universal Model Factory")]
    static void Open()
    {
        var w = GetWindow<ModelFactoryWindow>(false, "Universal Model Factory");
        w.minSize = new Vector2(500, 470);
        w.RefreshList();
    }

    void OnEnable() { RefreshList(); }

    void RefreshList()
    {
        var names = ModelRegistry.Load().Select(e => e.resourceName).ToList();
        names.Insert(0, "<New>");
        existing = names.ToArray();
        if (selected >= existing.Length) selected = 0;
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        // Widen the label column so the longer labels ("Position offset (Z = waterline)", "Double-sided (single-sided/CAD)",
        // "Animated (own rig + clip)") aren't clipped. Scales with window width so fields still get room when widened.
        EditorGUIUtility.labelWidth = Mathf.Clamp(position.width * 0.42f, 210f, 320f);
        GUILayout.Space(10f);
        EditorGUILayout.LabelField("Universal Model Factory", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        DrawSettings();
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            int sel = EditorGUILayout.Popup("3D resource", selected, existing);
            if (GUILayout.Button("Refresh", GUILayout.Width(70))) RefreshList();
            // Remove the selected registry entry (disabled on <New>). Prompts, then drops it from enc_models.json.
            using (new EditorGUI.DisabledScope(selected <= 0))
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    var name = cur.resourceName;
                    if (!string.IsNullOrEmpty(name) &&
                        EditorUtility.DisplayDialog("Remove model",
                            $"Remove '{name}' from the registry? The plugin will stop injecting it on next launch. " +
                            "(The baked skeleton/atlas assets stay in the project.)", "Remove", "Cancel"))
                    {
                        ModelRegistry.Remove(name);
                        selected = 0; cur = new ModelDef(); RefreshList(); GUI.FocusControl(null);
                        status = $"Removed '{name}' from the registry.";
                    }
                }
            if (sel != selected) { selected = sel; OnSelectResource(); GUI.FocusControl(null); }
        }
        EditorGUILayout.Space();

        cur.resourceName = EditorGUILayout.TextField("Resource name", cur.resourceName);
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.pawnDescription = EditorGUILayout.TextField("Pawn description", cur.pawnDescription);
            if (GUILayout.Button("Pick", GUILayout.Width(70)))
            {
                var r = GUILayoutUtility.GetLastRect();
                new PawnDropdown(new AdvancedDropdownState(), GatherPawnNames(), n =>
                {
                    cur.pawnDescription = n;
                    if (string.IsNullOrWhiteSpace(cur.resourceName)) cur.resourceName = DeriveResourceName(n); // suggest a name for a NEW resource
                    Repaint();
                }).Show(r);
            }
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.modelFile = EditorGUILayout.TextField("Model file", cur.modelFile);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                var p = EditorUtility.OpenFilePanel("Select 3D model", "", "glb,gltf,obj,fbx,blend");
                if (!string.IsNullOrEmpty(p)) cur.modelFile = p;
            }
        }
        if ((cur.modelFile ?? "").ToLowerInvariant().EndsWith(".blend") && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox(".blend import needs Blender installed (auto-detected). Install it, or set EditorPrefs 'ENC.blenderPath' to blender.exe.", MessageType.Warning);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Animation", EditorStyles.miniBoldLabel);
        EnsureAnimProbe(cur.modelFile);
        bool noAnim = animProbeState == 2;
        if (noAnim) cur.animated = false;   // a static model can't drive the animated path
        using (new EditorGUI.DisabledScope(noAnim))
            cur.animated = EditorGUILayout.Toggle(new GUIContent("Animated (own rig + clip)",
            "Bake from the model's OWN armature + animation clip so it plays its own motion in-game (e.g. a drone's " +
            "propellers spin) — instead of the static single-bone vehicle rig. The model MUST be rigged with a skeletal " +
            "animation (glb/fbx/blend). Blender slims it (join + decimate, keep the armature + first clip), Unity bakes an " +
            "Amplitude Skeleton + ClipCollection, and the plugin drives the pawn's pose onto it. Needs Blender (auto-detected)."), cur.animated);
        using (new EditorGUI.DisabledScope(!cur.animated))
            cur.animClip = EditorGUILayout.TextField(new GUIContent("Clip name",
                "Which animation to bake when the model has several clips — e.g. 'hover' (the drone also ships " +
                "'exploded_view' and 'step_by_step', which you do NOT want). Case-sensitive, must match the clip's name in " +
                "the source model. Leave EMPTY to use the model's assigned/first clip."), cur.animClip ?? "");
        using (new EditorGUI.DisabledScope(!cur.animated))
            cur.animateBones = EditorGUILayout.TextField(new GUIContent("Animate only bones",
                "Optional. Comma-separated bone-name PREFIXES to keep animation on — e.g. 'prop,rotor' keeps only the " +
                "spinning parts and strips camera / body-bob curves that make the model wobble ('unbalanced flywheel'). " +
                "Leave EMPTY to keep the whole clip (for a fully-animated model). The clip's frame range is always " +
                "auto-clamped to kill the ~1s per-loop stall from padded tail frames."), cur.animateBones ?? "");
        // Blender is a HARD dependency for the animated path (rig-slim + clip bake); glbconv can't emit a rigged FBX.
        // Warn as soon as an animated model is detected — not only after ticking — so a Blender-less adopter knows upfront.
        // (Detection itself needs no Blender, so the checkbox stays usable; only Bake will fail until Blender is present.)
        if ((cur.animated || animProbeState == 1) && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox("The Animated path needs Blender (to slim the rig + bake the clip) — it wasn't found. " +
                "Install Blender (auto-detected under Program Files) or set EditorPrefs 'ENC.blenderPath' to blender.exe. " +
                "Detection above works without Blender, but Bake will fail until it's installed.", MessageType.Warning);
        if (cur.animated)
            EditorGUILayout.HelpBox("Animated mode uses Size + Reduce-to-tris; the static Mesh/shading options below " +
                "(normals, winding, double-sided, height UVs, convert grid) don't apply.", MessageType.None);
        else if (noAnim)
            EditorGUILayout.HelpBox("No animation found in this model — the Animated option is disabled. Pick a rigged " +
                "glb / gltf / fbx (with a skeletal clip) to enable it, or use the static bake.", MessageType.None);
        else if (animProbeState == 1)
            EditorGUILayout.HelpBox("Animation detected in this model — tick 'Animated' to bake its own rig + clip.", MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Transform", EditorStyles.miniBoldLabel);
        cur.rotation = EditorGUILayout.Vector3Field("Rotation offset (XYZ)", cur.rotation);
        cur.position = EditorGUILayout.Vector3Field("Position offset (Z = waterline)", cur.position);
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.size = EditorGUILayout.FloatField(new GUIContent("Size (units)", "Length of the model's longest axis, in world units"), cur.size, GUILayout.Width(220));
            GUILayout.FlexibleSpace();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Mesh / shading", EditorStyles.miniBoldLabel);
        cur.normalsMode = (int)(NormalsMode)EditorGUILayout.EnumPopup("Normals", (NormalsMode)cur.normalsMode);
        using (new EditorGUI.DisabledScope(cur.normalsMode != (int)NormalsMode.Recalculate))
            cur.smoothingAngle = EditorGUILayout.Slider("Smoothing angle", cur.smoothingAngle, 0f, 180f);
        cur.heightUV = EditorGUILayout.Toggle(new GUIContent("Height-based UVs",
            "Override UVs with V = normalized height, so a vertical-gradient albedo maps by height regardless of the " +
            "model's own UVs — e.g. a black skirt low + grey hull high (put a bottom-black / top-grey PNG named " +
            "'*albedo*.png' in the resource folder). For untextured CAD models that need a simple gradient skin."), cur.heightUV);
        cur.windingFix = EditorGUILayout.Toggle(new GUIContent("Winding fix (CAD/convex)",
            "Rewind faces outward so single-sided / CAD 'sketch' meshes render single-sided instead of culling to invisible " +
            "(e.g. a hovercraft skirt). Lighter than double-sided (no extra geometry). Assumes a roughly convex hull — " +
            "true for vehicles/ships. Preferred for CAD hulls; use Double-sided for genuinely non-convex thin shells."), cur.windingFix);
        cur.doubleSided = EditorGUILayout.Toggle(new GUIContent("Double-sided (single-sided/CAD)",
            "Add a back face to every surface so single-sided or CAD 'sketch' meshes don't render invisible in-game (the " +
            "engine culls backfaces). Enable for models with missing / see-through parts — e.g. a hovercraft skirt. " +
            "Doubles the triangle count."), cur.doubleSided);
        cur.targetTris = EditorGUILayout.IntField(new GUIContent("Reduce to ~tris (0 = off)",
            "Quadric-decimate a heavy model to about this many triangles (via Blender) before baking, to fit the engine's " +
            "SHARED buffer (one budget across ALL injected models + the game's own fx meshes). Default 24000 (halves to " +
            "12000 under double-sided — the confirmed best LCAC bake, just under the ~25k per-model ceiling). It's a " +
            "CEILING, not a quota: a model already under it passes through untouched (never " +
            "upscaled). Toggling Double-sided automatically HALVES the effective target (it doubles the baked geometry), so " +
            "you set this once and just flip Double-sided on/off. Preserves thin parts (per-object). 0 = no reduction. " +
            "Needs Blender (auto-detected)."), cur.targetTris);
        if (cur.targetTris > 0 && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox("Reduce-to-tris uses Blender (quadric decimation) — Blender wasn't found, so Bake will " +
                "fail. Either set this to 0, use 'Convert grid' below (Blender-free GLB decimation), or install Blender / " +
                "set its path in Settings above.", MessageType.Warning);
        cur.hideMeshes = EditorGUILayout.TextField(new GUIContent("Hide donor meshes",
            "RUNTIME, not baked. Comma-separated name substrings of the DONOR unit's extra parts to hide on this unit — " +
            "e.g. 'Rotor' to remove the attack-helicopter rotor from a drone. Leave EMPTY to keep them (a custom " +
            "helicopter can borrow the donor's spinning rotor by leaving this blank). Find the exact names in the " +
            "BepInEx log after a launch: \"[Uni] <name> donor fragment[i] mesh='...'\". Takes effect on reload — no re-bake. " +
            "NOTE: only hides FRAGMENT-based extras; a donor's animated skinned sub-parts (helicopter rotor, spinning " +
            "wheels) are encoded at pawn-spawn and can't be hidden — pick a donor without them."), cur.hideMeshes ?? "");
        cur.convertGrid = EditorGUILayout.IntField(new GUIContent("Convert grid",
            "GLB / glTF / .blend only — controls how the source mesh is converted to OBJ.\n\n" +
            "0 = faithful: keep every vertex and UV exactly (preserves texture seams). Use this for " +
            "textured models — any decimation averages UVs across seams and scrambles the skin.\n\n" +
            ">0 = decimate to a vertex-cluster grid of this resolution along the longest axis " +
            "(higher = more detail/vertices). Use only for heavy UNtextured meshes that need simplifying.\n\n" +
            "Ignored for OBJ/FBX (already meshes)."), cur.convertGrid);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Texture / import", EditorStyles.miniBoldLabel);
        cur.reuseExtracted = EditorGUILayout.Toggle(new GUIContent("Reuse extracted files", "Skip re-importing the model file and reuse the OBJ/albedo already in the resource folder. Tick this after hand-editing the extracted texture (e.g. in paint.net) so your fix survives the bake."), cur.reuseExtracted);

        EditorGUILayout.Space();
        // A brand-new resource (<New>) has nothing to re-bake, so it also needs a model file; an existing one may leave
        // Model file empty to re-bake with new settings. Missing either target field greys Bake out with a reason.
        bool isNew = selected <= 0;
        bool canBake = !string.IsNullOrWhiteSpace(cur.resourceName)
                    && !string.IsNullOrWhiteSpace(cur.pawnDescription)
                    && (!isNew || !string.IsNullOrWhiteSpace(cur.modelFile));
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!canBake))
                if (GUILayout.Button("Bake", GUILayout.Height(34))) DoBake();
            if (GUILayout.Button("Reset", GUILayout.Height(34), GUILayout.Width(72))) { cur = new ModelDef(); selected = 0; status = ""; GUI.FocusControl(null); }
        }
        if (!canBake)
            EditorGUILayout.HelpBox(
                isNew ? "New resource: set Resource name, Pawn description and a Model file to bake."
                      : "Set Resource name and Pawn description to bake.", MessageType.Warning);

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
        EditorGUILayout.HelpBox(
            "Bake imports the model, bakes a skeleton + atlas, and writes the JSON registry the in-game plugin reads.\n" +
            "• Formats: GLB / glTF / OBJ / FBX, and .blend (auto-converted via installed Blender).\n" +
            "• Model file empty = re-bake the existing resource with new settings (fast iteration).\n" +
            "• Normals: KeepModel = the artist's; Recalculate = hard edges via smoothing angle; Faceted = fully flat.\n" +
            "• Convert grid: 0 keeps UV seams (textured models); >0 decimates (heavy untextured meshes).\n" +
            "Registry: " + ModelRegistry.RegistryPath, MessageType.None);
        EditorGUILayout.EndScrollView();
    }

    // Game-path settings: auto-detected Humankind BepInEx/config, with a manual override for odd install layouts.
    void DrawSettings()
    {
        string resolved = ModelRegistry.ConfigDir;
        bool exists = System.IO.Directory.Exists(resolved);
        bool blenderOk = UniversalBaker.BlenderAvailable();
        // Header shows a marker even when collapsed, so a missing game path OR missing Blender is always visible.
        string mark = (!exists ? "  ⚠ game path" : "") + (!blenderOk ? (exists ? "  ⚠ Blender not detected" : " & Blender") : "");
        showSettings = EditorGUILayout.Foldout(showSettings, "Settings — game & Blender path" + mark, true);
        if (!showSettings) return;
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            string auto = ModelRegistry.AutoDetectConfigDir();
            EditorGUILayout.LabelField("Auto-detected", string.IsNullOrEmpty(auto) ? "(Humankind not found via Steam)" : auto, EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                string ov = EditorGUILayout.TextField(new GUIContent("Override", "Leave empty to use auto-detect. Point at <Humankind>/BepInEx/config if detection misses your install."), ModelRegistry.ConfigDirOverride);
                if (ov != ModelRegistry.ConfigDirOverride) ModelRegistry.ConfigDirOverride = ov;
                if (GUILayout.Button("Browse", GUILayout.Width(70)))
                {
                    string p = EditorUtility.OpenFolderPanel("Select Humankind BepInEx/config", resolved, "");
                    if (!string.IsNullOrEmpty(p)) { ModelRegistry.ConfigDirOverride = p; GUI.FocusControl(null); }
                }
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(ModelRegistry.ConfigDirOverride)))
                    if (GUILayout.Button("Clear", GUILayout.Width(56))) { ModelRegistry.ConfigDirOverride = ""; GUI.FocusControl(null); }
            }
            EditorGUILayout.HelpBox("Registry target:\n" + ModelRegistry.RegistryPath +
                (exists ? "" : "\n(folder doesn't exist yet — created on Bake; check the path if this looks wrong)"),
                exists ? MessageType.None : MessageType.Warning);
        }

        // --- Blender: needed for animated import, .blend import, and Reduce-to-tris. Show status + an in-UI override. ---
        EditorGUILayout.Space();
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            string found = UniversalBaker.FindBlender();
            EditorGUILayout.LabelField("Blender", blenderOk ? found : "⚠ not detected", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                string bov = EditorPrefs.GetString("ENC.blenderPath", "");
                string nb = EditorGUILayout.TextField(new GUIContent("Override (blender.exe)", "Leave empty to auto-detect the newest install under Program Files. Set this if Blender is elsewhere or only on PATH."), bov);
                if (nb != bov) EditorPrefs.SetString("ENC.blenderPath", nb ?? "");
                if (GUILayout.Button("Browse", GUILayout.Width(70)))
                {
                    string p = EditorUtility.OpenFilePanel("Select blender.exe", "", "exe");
                    if (!string.IsNullOrEmpty(p)) { EditorPrefs.SetString("ENC.blenderPath", p); GUI.FocusControl(null); }
                }
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(EditorPrefs.GetString("ENC.blenderPath", ""))))
                    if (GUILayout.Button("Clear", GUILayout.Width(56))) { EditorPrefs.SetString("ENC.blenderPath", ""); GUI.FocusControl(null); }
            }
            EditorGUILayout.HelpBox(blenderOk
                ? "Used for animated import, .blend import, and Reduce-to-tris. GLB/OBJ/FBX static bakes work without it."
                : "Not found — animated import, .blend import, and Reduce-to-tris will fail. Install Blender or point the override at blender.exe. Static GLB/OBJ/FBX bakes still work (glbconv), and 'Convert grid' decimates without Blender.",
                blenderOk ? MessageType.None : MessageType.Warning);
        }
    }

    void OnSelectResource()
    {
        if (selected <= 0) { cur = new ModelDef(); status = ""; return; }
        var e = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == existing[selected]);
        if (e == null) return;
        cur = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(e));   // clone so edits don't mutate the stored copy
        status = "Loaded '" + e.resourceName + "'. Edit + Bake; leave Model file empty to re-bake with new settings.";
    }

    // Re-probe only when the model-file path changes (OnGUI runs every frame; file I/O must not).
    void EnsureAnimProbe(string file)
    {
        file = file ?? "";
        if (file == animProbeFile) return;
        animProbeFile = file;
        animProbeState = ProbeAnimation(file);
    }

    // Does the model file contain a skeletal animation? 0 = unknown (can't tell cheaply → allow), 1 = yes, 2 = no.
    // Deliberately conservative: only returns 2 ("none") when we're confident (OBJ, or a glTF with no animations), so we
    // never wrongly BLOCK a rigged model; ambiguous formats (.blend) and a token-less FBX stay "unknown" (allowed).
    static int ProbeAnimation(string file)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file)) return 0;
            switch (System.IO.Path.GetExtension(file).ToLowerInvariant())
            {
                case ".obj": return 2;                                   // OBJ can't carry animation
                case ".glb": return ProbeGlb(file);
                case ".gltf": return HasGltfAnim(System.IO.File.ReadAllText(file)) ? 1 : 2;
                case ".fbx": return ProbeFbx(file) ? 1 : 0;              // token found = yes; absent = unknown (don't block)
                default: return 0;                                       // .blend etc — needs Blender to know
            }
        }
        catch { return 0; }
    }

    // Read a binary glTF's JSON chunk and check for a non-empty "animations" array.
    static int ProbeGlb(string file)
    {
        using (var fs = System.IO.File.OpenRead(file))
        using (var br = new System.IO.BinaryReader(fs))
        {
            if (fs.Length < 20 || br.ReadUInt32() != 0x46546C67u) return 0;   // "glTF" magic
            br.ReadUInt32(); br.ReadUInt32();                                  // version, total length
            uint clen = br.ReadUInt32(); uint ctype = br.ReadUInt32();         // first chunk = JSON
            if (ctype != 0x4E4F534Au) return 0;                               // "JSON"
            var bytes = br.ReadBytes((int)System.Math.Min(clen, 16u * 1024 * 1024));
            return HasGltfAnim(System.Text.Encoding.UTF8.GetString(bytes)) ? 1 : 2;
        }
    }

    // "animations":[ … ] present AND non-empty (next non-space char after '[' isn't ']').
    static bool HasGltfAnim(string json)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"animations\"\\s*:\\s*\\[");
        if (!m.Success) return false;
        int i = m.Index + m.Length;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        return i < json.Length && json[i] != ']';
    }

    // FBX (binary or ASCII) names its animation via AnimStack / AnimCurveNode object records. Scan up to a cap.
    static bool ProbeFbx(string file)
    {
        try
        {
            var data = System.IO.File.ReadAllBytes(file);
            int n = System.Math.Min(data.Length, 48 * 1024 * 1024);
            string hay = System.Text.Encoding.ASCII.GetString(data, 0, n);
            return hay.IndexOf("AnimStack", System.StringComparison.Ordinal) >= 0
                || hay.IndexOf("AnimCurveNode", System.StringComparison.Ordinal) >= 0;
        }
        catch { return false; }
    }

    static string[] pawnCache;
    static string[] GatherPawnNames()
    {
        if (pawnCache != null) return pawnCache;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in AssetDatabase.FindAssets("PresentationPawnDefinition"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".asset")) continue;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && o.GetType().Name == "PresentationPawnDefinition" && !string.IsNullOrEmpty(o.name))
                    names.Add(o.name);
        }
        pawnCache = names.ToArray();
        return pawnCache;
    }

    // Era6_Common_StealthCruisers_01 -> "StealthCruisers" (drop a trailing numeric token). Suggested resource name.
    static string DeriveResourceName(string pawnName)
    {
        if (string.IsNullOrEmpty(pawnName)) return "";
        var parts = pawnName.Split('_');
        int end = parts.Length - 1;
        if (end > 0 && int.TryParse(parts[end], out _)) end--;
        return end >= 0 ? parts[end] : pawnName;
    }

    void DoBake()
    {
        var cfg = new BakeConfig
        {
            resourceName = cur.resourceName.Trim(), modelFile = (cur.modelFile ?? "").Trim(), pawnDescription = cur.pawnDescription.Trim(),
            rotationEuler = cur.rotation, positionOffset = cur.position, size = cur.size,
            normals = (NormalsMode)cur.normalsMode, smoothingAngle = cur.smoothingAngle, convertGrid = cur.convertGrid,
            reuseExtracted = cur.reuseExtracted, doubleSided = cur.doubleSided, windingFix = cur.windingFix, heightUV = cur.heightUV, targetTris = cur.targetTris,
            animated = cur.animated, animClip = (cur.animClip ?? "").Trim(), animateBones = (cur.animateBones ?? "").Trim()
        };
        var r = cfg.animated ? UniversalBaker.BuildAnimated(cfg) : UniversalBaker.Build(cfg);
        if (!r.ok) { status = "Bake FAILED: " + r.error; return; }
        cur.skel = ModelRegistry.ParseGuid(r.skeletonGuid);
        cur.atlas = ModelRegistry.ParseGuid(r.atlasGuid);
        cur.clip = cfg.animated ? ModelRegistry.ParseGuid(r.clipGuid) : new int[4];   // static models carry {0,0,0,0}
        ModelRegistry.Upsert(cur);
        RefreshList();
        selected = System.Array.IndexOf(existing, cur.resourceName); if (selected < 0) selected = 0;
        status = cfg.animated
            ? $"Baked ANIMATED '{cur.resourceName}' -> '{cur.pawnDescription}'\nskeleton {r.skeletonGuid}\nclip {r.clipGuid}\nNow rebuild the mod + relaunch."
            : $"Baked '{cur.resourceName}' -> '{cur.pawnDescription}'  (raw bbox {r.bbox})\nskeleton {r.skeletonGuid}\nNow rebuild the mod + relaunch.";
        Debug.Log("[Factory] " + status);
    }
}

// Searchable popup of every PresentationPawnDefinition name (built-in search box). Picking sets the pawn description.
class PawnDropdown : AdvancedDropdown
{
    readonly string[] names; readonly Action<string> onPick; readonly Dictionary<int, string> map = new Dictionary<int, string>();
    public PawnDropdown(AdvancedDropdownState s, string[] names, Action<string> onPick) : base(s)
    { this.names = names; this.onPick = onPick; minimumSize = new Vector2(300, 420); }
    protected override AdvancedDropdownItem BuildRoot()
    {
        var root = new AdvancedDropdownItem("Pawn definitions (" + names.Length + ")");
        foreach (var n in names) { var it = new AdvancedDropdownItem(n); root.AddChild(it); map[it.id] = n; }
        return root;
    }
    protected override void ItemSelected(AdvancedDropdownItem item) { if (map.TryGetValue(item.id, out var n)) onPick(n); }
}
