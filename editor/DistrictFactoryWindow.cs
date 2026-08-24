// DistrictFactoryWindow.cs (ENC editor) — the DISTRICT Factory dialog (Tools ▸ ENC ▸ District Factory). The district
// counterpart of ModelFactoryWindow: pick a district + a model file, set the bake knobs, press Bake — it runs the same
// static bake core (UniversalBaker.Build; pawnDescription empty, districts don't use one), wraps the result as a
// bone-free FxMesh (DistrictBaker.BakeFxMesh — the district shader is STATIC, a rigged mesh draws nothing), and writes
// the haf_districts.json entry the plugin's district repoint reads. No dummy pawn, no donor, no skeleton wiring.
//
// Runtime prerequisites (docs/District-Visuals.md): the district definition needs a RENDERABLE ConstructibleVisualAffinity
// and CLEARED Additional Visual Levels (data edit in this project), and the plugin's [District] DistrictRepoint = true.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class DistrictFactoryWindow : EditorWindow
{
    [MenuItem("Tools/HAF/District Factory")]
    static void Open() => GetWindow<DistrictFactoryWindow>("District Factory");

    List<DistrictDef> all = new List<DistrictDef>();
    string[] existing = { "<New>" };
    int selected;
    DistrictDef cur = new DistrictDef();
    string status = "";
    Vector2 scroll;

    // EMBEDDED PREVIEW — renders the entry's baked mesh exactly as the game draws it (the FxMesh path: the bone-free
    // _DistrictMesh rotated by the draw-time FxMesh rotation), standing on a district-tile ground square (~10 across) so
    // the Size knob reads at a glance. Facing comes LIVE from the form field, so the model can be turned by eye
    // in here; the Rotation offset is baked into the vertices and only shows after a re-Bake. Same PreviewRenderUtility
    // owner-camera pattern as the Animation Lab (built-in previews have no zoom and the scroll view steals the wheel).
    PreviewRenderUtility pru;                       // non-serializable; lazily created, cleaned in OnDisable
    Material pvFallbackMat, pvTileMat;              // created on demand, HideAndDontSave, destroyed in OnDisable
    Mesh pvTileMesh;
    Mesh pvArrowMesh;                               // flat compass line + N glyph: map NORTH (how the tile presents in-game); Facing 0° points along it
    Mesh pvMesh; Material[] pvMats;                 // the baked district mesh + its atlas preview material
    string pvLoadedFor;                             // resourceName the cache was built for (null = load on next paint)
    Rect groundPickRect, hexPickRect;   // "Pick" button rects, captured on Repaint (GetLastRect is unreliable on click)
    [SerializeField] Vector2 pvOrbit = new Vector2(35f, -30f);
    [SerializeField] float pvZoom = 1f;
    [SerializeField] Vector2 pvPan;

    void OnEnable() => RefreshList();

    // The natural flow is bake -> build the mod -> glance back at this window: re-run the health checks whenever the
    // window regains focus, so the STALE BUNDLE verdict updates itself instead of showing the pre-build state.
    void OnFocus() { groundTexCache.Clear(); if (selected > 0 && cur != null && !string.IsNullOrWhiteSpace(cur.district)) { RunHealthChecks(); Repaint(); } }   // drop cached ground textures so a fresh plugin dump is picked up

    void OnDisable()
    {
        if (pru != null) { pru.Cleanup(); pru = null; }
        if (pvFallbackMat != null) DestroyImmediate(pvFallbackMat);
        if (pvTileMat != null) DestroyImmediate(pvTileMat);
        if (pvTileMesh != null) DestroyImmediate(pvTileMesh);
        if (pvArrowMesh != null) DestroyImmediate(pvArrowMesh);
    }

    void RefreshList()
    {
        all = DistrictRegistry.Load();
        var notice = DistrictRegistry.TakeNotice(); if (!string.IsNullOrEmpty(notice)) status = notice;   // self-healing is shown, not Console-only
        existing = new[] { "<New>" }.Concat(all.Select(d => d.district)).ToArray();
    }

    void OnSelect()
    {
        cur = selected > 0 && selected <= all.Count
            ? JsonUtility.FromJson<DistrictDef>(JsonUtility.ToJson(all[selected - 1]))   // edit a COPY so Cancel/Reset doesn't mutate the list
            : new DistrictDef();
        status = "";
        LoadPreviewAssets(force: true);   // preview follows the selection — never show the previous entry's model
        RunHealthChecks();
    }

    // ---- HEALTH CHECKS (the review's editor-side validation) --------------------------------------------------------
    // The two failure modes that actually cost launches this week — registry-vs-asset GUID drift ("waiting for leaves
    // to load..." forever) and a stale mod bundle (mesh/atlas pair mismatch = scrambled texture) — plus the July
    // data-prerequisite trap (non-empty Additional Visual Levels = guaranteed empty tile). All detectable right here,
    // at authoring time. Computed on selection / after Bake (never per-OnGUI — these hit the AssetDatabase and disk).
    readonly List<(MessageType sev, string msg)> health = new List<(MessageType, string)>();
    static string CommunityDir => HafPaths.CommunityDir;   // resolved per machine (see HafPaths); null = not known

    void RunHealthChecks()
    {
        health.Clear();
        if (selected <= 0 || string.IsNullOrWhiteSpace(cur.district)) return;   // a fresh entry has nothing to validate
        string res = (cur.resourceName ?? "").Trim();
        try
        {
            // 1) registry-vs-asset GUID drift: the shipped GUIDs must match the assets currently on disk
            System.DateTime newestBaked = System.DateTime.MinValue;
            void CheckGuid(string suffix, string regGuid, string what, bool critical)
            {
                if (string.IsNullOrEmpty(regGuid)) return;   // entry doesn't ship this asset — nothing to check
                string path = "Assets/Resources/" + res + suffix + ".asset";
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset == null)
                { health.Add((MessageType.Error, $"{what}: registry has a GUID but '{path}' is MISSING — the game will wait forever for it. Re-bake.")); return; }
                var diskGuid = DistrictBaker.AmplitudeGuid(asset);
                if (!string.IsNullOrEmpty(diskGuid) && diskGuid != regGuid)
                    health.Add((critical ? MessageType.Error : MessageType.Warning,
                        $"{what}: registry GUID != the asset on disk ({regGuid} vs {diskGuid}) — the game loads a different {what.ToLowerInvariant()} than this project holds. Re-bake to re-sync."));
                var full = System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath).FullName, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(full) && System.IO.File.GetLastWriteTime(full) > newestBaked) newestBaked = System.IO.File.GetLastWriteTime(full);
            }
            CheckGuid("_FxMesh", cur.fxMeshGuid, "FxMesh", critical: true);
            CheckGuid("_Atlas", cur.atlasGuid, "Albedo atlas", critical: false);
            CheckGuid("_NormalAtlas", cur.normalAtlasGuid, "Normal atlas", critical: false);
            CheckGuid("_RoughAtlas", cur.roughAtlasGuid, "Roughness atlas", critical: false);

            // 2) stale deployment: baked assets newer than the newest built mod bundle = the game ships an older pair
            //    (re-bakes reshuffle atlas packing — the mesh/atlas halves MUST come from the same bake; learned twice)
            if (newestBaked > System.DateTime.MinValue && System.IO.Directory.Exists(CommunityDir))
            {
                System.DateTime newestBundle = System.DateTime.MinValue;
                foreach (var dir in System.IO.Directory.GetDirectories(CommunityDir, "ENCReload.*"))
                    foreach (var f in System.IO.Directory.GetFiles(dir, "*.assetbundle"))
                        if (System.IO.File.GetLastWriteTime(f) > newestBundle) newestBundle = System.IO.File.GetLastWriteTime(f);
                if (newestBundle == System.DateTime.MinValue)
                    health.Add((MessageType.Warning, "No built ENCReload assetbundle found in the game's Community folder — build the mod before launching."));
                else if (newestBaked > newestBundle)
                    health.Add((MessageType.Warning, $"STALE BUNDLE: baked assets ({newestBaked:HH:mm:ss}) are newer than the built mod ({newestBundle:HH:mm:ss}) — REBUILD the mod before launching, or the game ships the old mesh/atlas pair (scrambled texture)."));
            }
            // SAY SO WHEN THE CHECK CANNOT RUN. Until 2026-08-23 the Community folder was a hardcoded const, so off
            // the one machine it named this whole block was skipped by Directory.Exists and the panel stayed silent —
            // indistinguishable from "checked, all fine". An unrun check must never look like a passed one.
            else if (newestBaked > System.DateTime.MinValue && string.IsNullOrEmpty(CommunityDir))
                health.Add((MessageType.Warning, "STALE BUNDLE check NOT RUNNING: Humankind's Community folder was not found. " +
                    "Open Tools ▸ HAF ▸ Ship Status and press 'Locate…' to point HAF at it once."));

            // 3) data prerequisites on the district definition (the July trap): non-empty Additional Visual Levels
            //    resolves to material 0,0,0,0 = a guaranteed empty tile; a missing affinity renders nothing to swap.
            var def = FindDistrictDefinition(cur.district);
            if (def == null)
            {
                // A base-game / DLC constructible (the 'Extension_' namespace — districts, quarters, wonders) is
                // DEFINED IN THE GAME, not this project, so the project search can't see it. That's the normal case
                // for the reactor/silo/etc. targets — the plugin binds the mesh to it BY NAME at runtime, so stay
                // SILENT (a note here only confuses). Only a name that ISN'T in that namespace and ISN'T a project
                // asset is a probable typo worth a warning.
                if (cur.district == null || !cur.district.StartsWith("Extension_", StringComparison.OrdinalIgnoreCase))
                    health.Add((MessageType.Warning, $"No district definition named '{cur.district}' found — check the spelling. (Base-game targets are named 'Extension_…' and bind at runtime; a project-local definition should resolve here.)"));
            }
            else
            {
                var so = new SerializedObject(def);
                var lv = so.FindProperty("AdditionalVisualLevels");
                if (lv != null && lv.isArray && lv.arraySize > 0)
                    health.Add((MessageType.Error, $"'{cur.district}' has {lv.arraySize} Additional Visual Level(s) — the visual lookup keys on the full combo and an unregistered combo is an EMPTY TILE (the July trap). Clear the list on the definition."));
                var aff = so.FindProperty("ConstructibleVisualAffinity")?.FindPropertyRelative("serializableElementName");
                if (aff != null && string.IsNullOrEmpty(aff.stringValue))
                    health.Add((MessageType.Warning, $"'{cur.district}' has NO ConstructibleVisualAffinity — the tile resolves no material family and there is nothing to swap. Set a renderable affinity (or the native wonder affinity + WonderNativeRows for wonders)."));
            }
        }
        catch (Exception ex) { health.Add((MessageType.Warning, "Health checks failed to run: " + ex.Message)); }
    }

    // Broader than the Pick dropdown's search on purpose: entries can target WONDER-class constructibles too
    // (ArtificialWonderDefinition lives in its own Constructible*Definition asset, and its class name does NOT end
    // in "DistrictDefinition" — the Oracle health check false-negatived on the narrow search). The UIMapper
    // sub-assets share the definition's name, so the type-name filter stays essential.
    static UnityEngine.Object FindDistrictDefinition(string name)
    {
        foreach (var guid in AssetDatabase.FindAssets("Constructible"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith("Definition.asset")) continue;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o == null || o.name != name) continue;
                var tn = o.GetType().Name;
                if (tn.EndsWith("DistrictDefinition") || tn.EndsWith("ArtificialWonderDefinition") || tn.EndsWith("HolySiteDefinition")) return o;
            }
        }
        return null;
    }

    void OnGUI()
    {
        // CORRUPT-SOURCE RECOVERY banner (the Factory's, via the shared SingleSourceRegistry engine — 2026-08-20): the
        // fault is PINPOINTED (line/column) and recovery is ONE CLICK, each path validated before it writes and the broken
        // file already preserved timestamped. Save/Bake stay locked until recovered.
        if (DistrictRegistry.LastLoadCorrupt)
        {
            EditorGUILayout.HelpBox("DISTRICT REGISTRY SOURCE IS CORRUPT — " + DistrictRegistry.LastCorruptDetail + "\n" +
                "The broken file is preserved beside the source; Save/Bake are locked so nothing can be wiped. Recover:", MessageType.Error);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Restore last deploy", "Copy the deployed file (refreshed on every Save — usually the freshest valid copy) back over the source. Validated before writing; the corrupt file stays preserved."), GUILayout.Width(140)))
                { status = DistrictRegistry.RecoverFromArtifact(); RefreshList(); Debug.Log("[District] " + status); GUIUtility.ExitGUI(); }
                if (GUILayout.Button(new GUIContent("Restore last commit", "git checkout the source — the last committed version. Validated before accepting; the corrupt file stays preserved."), GUILayout.Width(140)))
                { status = DistrictRegistry.RecoverFromGit(); RefreshList(); Debug.Log("[District] " + status); GUIUtility.ExitGUI(); }
                if (GUILayout.Button(new GUIContent("Open broken file", "Reveal the source in Explorer to fix the reported line by hand — then reopen or refresh the window."), GUILayout.Width(120)))
                { EditorUtility.RevealInFinder(DistrictRegistry.SourcePath); }
            }
        }
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            int sel = EditorGUILayout.Popup("District model", selected, existing);
            if (GUILayout.Button("Refresh", GUILayout.Width(70))) RefreshList();
            using (new EditorGUI.DisabledScope(selected <= 0))
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    // key on the SELECTED entry, not the (possibly edited) text field — same E2 pitfall as the unit window
                    var name = selected > 0 && selected < existing.Length ? existing[selected] : null;
                    if (!string.IsNullOrEmpty(name) &&
                        EditorUtility.DisplayDialog("Remove district model",
                            $"Remove '{name}' from the district registry? The plugin will stop swapping its mesh on next launch. " +
                            "(The baked FxMesh assets stay in the project.)", "Remove", "Cancel"))
                    {
                        bool removed = DistrictRegistry.Remove(name);
                        selected = 0; cur = new DistrictDef(); RefreshList(); GUI.FocusControl(null);
                        status = removed ? $"Removed '{name}' from the district registry." : $"'{name}' was not in the registry — nothing removed.";
                    }
                }
            if (sel != selected) { selected = sel; OnSelect(); GUI.FocusControl(null); }
        }
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            cur.district = EditorGUILayout.TextField(new GUIContent("District",
                "The district's ConstructibleDefinitionName — e.g. Extension_Base_BreederReactor. The plugin matches the " +
                "on-map district by this name. Remember the DATA side: the definition needs a renderable " +
                "ConstructibleVisualAffinity and CLEARED Additional Visual Levels, or nothing renders at all."), cur.district);
            var districts = GatherDistrictNames();
            using (new EditorGUI.DisabledScope(districts.Length == 0))
                if (GUILayout.Button(new GUIContent("Pick", districts.Length == 0 ? "No district definitions found in the project databases — type the name" : null), GUILayout.Width(70)))
                {
                    var r = GUILayoutUtility.GetLastRect();
                    new StringDropdown(new AdvancedDropdownState(), districts, districts, "Districts", n =>
                    {
                        cur.district = n;
                        if (string.IsNullOrWhiteSpace(cur.resourceName)) cur.resourceName = DeriveResourceName(n);
                        Repaint();
                    }).Show(r);
                }
        }
        cur.resourceName = EditorGUILayout.TextField(new GUIContent("Resource name",
            "Unique id — names the baked assets (<name>_ModelMesh / _DistrictMesh / _FxMesh). Letters, digits, '_' or '-' only."), cur.resourceName);
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.modelFile = EditorGUILayout.TextField(new GUIContent("Model file",
                "GLB / glTF / OBJ / FBX / .blend. Leave EMPTY on an existing entry to re-bake with new settings."), cur.modelFile);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                var p = EditorUtility.OpenFilePanel("Select 3D model", "", "glb,gltf,obj,fbx,blend");
                if (!string.IsNullOrEmpty(p))
                {
                    cur.modelFile = p;
                    if (string.IsNullOrWhiteSpace(cur.resourceName))
                        cur.resourceName = System.Text.RegularExpressions.Regex.Replace(
                            System.IO.Path.GetFileNameWithoutExtension(p), @"[^A-Za-z0-9_\-]", "");
                }
            }
        }
        if ((cur.modelFile ?? "").ToLowerInvariant().EndsWith(".blend") && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox(".blend import needs Blender installed (auto-detected). Install it, or set EditorPrefs 'ENC.blenderPath' to blender.exe.", MessageType.Warning);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Bake", EditorStyles.miniBoldLabel);
        cur.size = EditorGUILayout.FloatField(new GUIContent("Size",
            "World length of the model's longest axis. A tile hex is ~7 across its flats (~8 corner to corner — the " +
            "preview hex is true size) — ~5-6 fills the tile, ~2.5 tile-furniture."), cur.size);
        cur.rotation = EditorGUILayout.Vector3Field(new GUIContent("Rotation offset (deg)",
            "The STAND-IT-UP control, baked into the mesh — on top of the automatic longest-axis align, which can TIP a " +
            "near-cubic model onto its side around ANY axis (the plant needed Z=-90). The preview below shows the result " +
            "after each Bake — dial it there, no relaunch needed. To merely TURN a standing building, use Facing instead."), cur.rotation);
        cur.facing = EditorGUILayout.Slider(new GUIContent("Facing on tile (deg)",
            "Turn the building on its tile — always about the vertical, can't tip it. Previewed LIVE; written into the " +
            "FxMesh at Bake (auto-level re-grounds for the result)."), cur.facing, 0f, 360f);
        cur.posOffset = EditorGUILayout.Vector3Field(new GUIContent("Position offset",
            "Nudge the building on its tile, in world units (a tile hex is ~7 across): X/Z slide it over the tile, Y " +
            "lifts it off the ground. Applied AFTER the auto-level at Bake (so leveling can't cancel it); previewed " +
            "LIVE. The same knob the Model Factory has for units."), cur.posOffset);
        cur.clipHexPct = EditorGUILayout.Slider(new GUIContent("Clip to tile hex (%)",
            "CUT the model to the in-game cell at Bake, so an oversized site plan ends at the hex border like a vanilla " +
            "district instead of overhanging its neighbors. 100 = the exact cell (the preview hex); slightly less pulls " +
            "the cut inside the border, slightly more leaves a rim. 0 = off. Tip: with the clip on, Size 8-9 lets the " +
            "site plan FILL the whole cell corner to corner. Cut faces are open (no cap) — fine from the game camera, " +
            "check the preview after Bake."), cur.clipHexPct, 0f, 120f);
        cur.foundationDepth = EditorGUILayout.Slider(new GUIContent("Foundation depth",
            "Extrude the building's footprint straight DOWN into the earth by this many world units at Bake — a solid " +
            "concrete plinth. On a cliff or uneven tile the building otherwise overhangs into empty air; the plinth " +
            "plants it on a base that runs down past the drop. 0 = off. Try ~8-12 for a coastal cliff. Textured with a " +
            "concrete strip added to the atlas (the plinth shows in the preview below the model)."), cur.foundationDepth, 0f, 30f);
        // cur.importAngles stays in the registry for entries authored before Facing (their FxMesh rotation composes it),
        // but it's no longer a UI control: Rotation offset stands the model up (previewed per bake), Facing turns it
        // (previewed live) — two rotation fields with overlapping jobs only bred "which one do I use?".
        if (cur.sourceTris > 0)
            EditorGUILayout.LabelField(" ", $"source model: {cur.sourceTris:N0} tris (before reduction, from the last bake)", EditorStyles.miniLabel);
        cur.targetTris = EditorGUILayout.IntField(new GUIContent("Target triangles",
            "Quadric-decimate ceiling before baking (0 = off; models under it pass through untouched). District meshes share " +
            "one ~3M-vert GPU buffer that runs nearly FULL in a late-game city — keep this modest, or set the plugin's " +
            "[District] DistrictBufferHeadroom (e.g. 2000000) to enlarge the buffer."), cur.targetTris);
        cur.normalsMode = EditorGUILayout.Popup(new GUIContent("Normals",
            "KeepModel = the artist's; Recalculate = hard edges via smoothing angle (angular models want a LOW angle); Faceted = fully flat."),
            cur.normalsMode, new[] { "Keep model", "Recalculate", "Faceted" });
        using (new EditorGUI.DisabledScope(cur.normalsMode != 1))
            cur.smoothingAngle = EditorGUILayout.Slider("Smoothing angle", cur.smoothingAngle, 0f, 180f);
        cur.convertGrid = EditorGUILayout.IntField(new GUIContent("Weld & simplify (0 = keep exact)",
            "GLB→OBJ conversion: 0 = keep exact (preserves UV seams — textured models), >0 = weld/simplify nearby vertices (heavy untextured meshes only)."), cur.convertGrid);
        int atlasIdx = Array.IndexOf(new[] { 256, 512, 1024, 2048, 4096 }, cur.atlasMaxDim <= 0 ? 1024 : cur.atlasMaxDim);
        atlasIdx = EditorGUILayout.Popup(new GUIContent("Atlas size",
            "Longest side of the packed texture atlas. Multi-material models divide this between ALL their sheets " +
            "(the temple packs ten 1024² textures — at 512 each got ~160², visibly blurry). Districts render close-up: " +
            "1024 minimum for multi-material, 2048 for hero pieces like wonders."), atlasIdx < 0 ? 2 : atlasIdx,
            new[] { "256", "512", "1024", "2048", "4096" });
        cur.atlasMaxDim = new[] { 256, 512, 1024, 2048, 4096 }[atlasIdx];
        cur.stripParts = EditorGUILayout.TextField(new GUIContent("Strip parts",
            "Comma-separated object-name substrings to DELETE from the source model before baking (via Blender). Empty = keep everything."), cur.stripParts ?? "");
        cur.reuseExtracted = EditorGUILayout.Toggle(new GUIContent("Reuse extracted files",
            "Skip re-importing the model file and reuse the OBJ/albedo already extracted — tick after hand-editing the texture so your fix survives a re-bake."), cur.reuseExtracted);

        // ---- PARTS (the pizza): extra models composed onto the tile at bake ----
        EditorGUILayout.Space();
        if (cur.parts == null) cur.parts = new List<DistrictPart>();
        EditorGUILayout.LabelField($"Parts — extra models on the tile ({cur.parts.Count})", EditorStyles.miniBoldLabel);
        int removePart = -1;
        for (int i = 0; i < cur.parts.Count; i++)
        {
            var p = cur.parts[i];
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    p.modelFile = EditorGUILayout.TextField(new GUIContent($"Part {i + 1} model",
                        "This part's model file. Bakes with the entry's Normals / Target triangles / Atlas settings."), p.modelFile);
                    if (GUILayout.Button("Browse", GUILayout.Width(70)))
                    {
                        var pf = EditorUtility.OpenFilePanel("Select part model", "", "glb,gltf,obj,fbx,blend");
                        if (!string.IsNullOrEmpty(pf)) p.modelFile = pf;
                    }
                    if (GUILayout.Button("X", GUILayout.Width(22))) removePart = i;
                }
                p.size = EditorGUILayout.FloatField(new GUIContent("Size", "World length of this part's longest axis (the tile is ~7 across)."), p.size);
                p.targetTris = EditorGUILayout.IntField(new GUIContent("Target triangles",
                    "THIS part's decimation ceiling. Parts render small on the tile — keep this LOW (a grove multiplies it), so the whole " +
                    "composed mesh stays under the ~65,535-vertex per-district-mesh limit. 0 = use the entry's Target."), p.targetTris);
                p.rotation = EditorGUILayout.Vector3Field(new GUIContent("Rotation offset (deg)", "Stand THIS part up (baked into its import, like the entry's Rotation offset)."), p.rotation);
                p.facing = EditorGUILayout.Slider(new GUIContent("Facing (deg)", "Turn this part on the tile, about the vertical."), p.facing, 0f, 360f);
                p.leafScale = EditorGUILayout.Slider(new GUIContent("Leaf size ×",
                    "GEOMETRY: scales every small disconnected triangle island (each leaf card) around its own centroid — the leaves get " +
                    "physically bigger. The trunk (one big connected island) is untouched. 1 = as authored; try 1.5-2.5."), p.leafScale <= 0f ? 1f : p.leafScale, 1f, 3f);
                p.alphaBoost = EditorGUILayout.Slider(new GUIContent("Leaf fullness",
                    "Cutout-foliage fullness: boosts the part's texture alpha AND dilates the opaque leaf sprites (each whole step above 1 " +
                    "grows every leaf by ~1 texel — needed for binary-alpha foliage like the beech, where a plain alpha boost is a no-op). " +
                    "1 = as authored; 2-4 = fuller crown."), p.alphaBoost <= 0f ? 1f : p.alphaBoost, 1f, 4f);
                p.posOffset = EditorGUILayout.Vector3Field(new GUIContent("Position offset", "Place this part: X/Z slide across the tile, Y lifts it off the base's floor. The part auto-grounds to the base's floor first."), p.posOffset);
                // COPIES — the same part placed again (a grove): one bake, one atlas slot, geometry per copy
                if (p.copies == null) p.copies = new List<Vector3>();
                int removeCopy = -1;
                for (int c = 0; c < p.copies.Count; c++)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        p.copies[c] = EditorGUILayout.Vector3Field(new GUIContent($"Copy {c + 1} offset",
                            "Extra placement of this part: X/Z slide across the tile, Y lifts. Auto-rotated by the golden angle so copies don't look cloned."), p.copies[c]);
                        if (GUILayout.Button("X", GUILayout.Width(22))) removeCopy = c;
                    }
                if (removeCopy >= 0) p.copies.RemoveAt(removeCopy);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Add copy", "Place this part AGAIN at another offset (one bake, geometry appended per copy — triangles multiply)."), GUILayout.Width(90)))
                        p.copies.Add(p.posOffset + new Vector3(1.5f, 0f, -1.5f));
                    if (p.copies.Count > 0)
                        EditorGUILayout.LabelField($"{1 + p.copies.Count}x this part on the tile — triangles multiply per copy", EditorStyles.miniLabel);
                }
            }
        }
        if (removePart >= 0) cur.parts.RemoveAt(removePart);
        if (GUILayout.Button("Add part", GUILayout.Width(90))) cur.parts.Add(new DistrictPart());
        if (cur.parts.Count > 0)
            EditorGUILayout.HelpBox("Parts are BAKED-IN: each part bakes with its own knobs, grounds to the base's floor, and merges into ONE mesh + super albedo/normal/rough atlases (the runtime is unchanged). Alpha-cutout foliage is supported (verified in-game). Placement shows in the preview after Bake.", MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Runtime", EditorStyles.miniBoldLabel);
        // GROUND — the terrain paint under the district (the "sauce"): a maintained field instead of bare terrain.
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.groundMaterial = EditorGUILayout.TextField(new GUIContent("Ground (terrain paint)",
                "The GroundMaterialDefinition painted under this district — a maintained grass/paved field instead of bare terrain " +
                "(a wonder's affinity has none). Empty = the game's default. Pick a name at right, or type one."), cur.groundMaterial ?? "");
            // GetLastRect is only reliable on Repaint; capture the button rect then and use it on the click, or the
            // dropdown opens detached in the corner (GetLastRect returns a stale/zero rect during the MouseDown event).
            if (GUILayout.Button("Pick", GUILayout.Width(70)))
                new StringDropdown(new AdvancedDropdownState(), GroundMaterialNames, GroundMaterialNames, "Ground materials",
                    n => { cur.groundMaterial = n == "(none)" ? "" : n; Repaint(); }).Show(groundPickRect);
            if (Event.current.type == EventType.Repaint) groundPickRect = GUILayoutUtility.GetLastRect();
        }
        // Isolate is always ON for authored districts (private per-instance leaf: scoped + textured). The old global
        // shared-leaf swap (isolate=false) had no texture injection and changed every district of the culture — a
        // footgun with no real use, so its toggle is retired. New entries default true; the plugin still honors a
        // legacy entry that has it false.
        cur.isolate = true;
        // HEXAGON SCULPTING — the raised platform + strategic-zoom footprint (a custom wonder has none = flat).
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.hexSculpt = EditorGUILayout.TextField(new GUIContent("Footprint (hex sculpting)",
                "The HexagonSculptingDefinition that carves this district's raised terrain platform — also its top-down " +
                "footprint at strategic zoom / in battle. A custom wonder has none (sits flat). EmblematicAndCityCenter* " +
                "are the district platforms. Empty = flat. Pick a name at right, or type one."), cur.hexSculpt ?? "");
            if (GUILayout.Button("Pick", GUILayout.Width(70)))
                new StringDropdown(new AdvancedDropdownState(), HexSculptNames, HexSculptNames, "Hex sculpting",
                    n => { cur.hexSculpt = n == "(none)" ? "" : n; Repaint(); }).Show(hexPickRect);
            if (Event.current.type == EventType.Repaint) hexPickRect = GUILayoutUtility.GetLastRect();
        }
        // Strategic footprint (decals) — grafted onto the reactor at runtime by the plugin; the BUILDING stays ours.
        // Distinct from "Footprint (hex sculpting)" above, which is the raised terrain platform. Only the decal footprint
        // still lazy-builds ~1s on first zoom-out (engine limitation, docs/District-Dedicated-Visual.md).
        // MUTUALLY EXCLUSIVE with the Mesh footprint below: when the mesh IS the footprint, any decal footprint is dropped
        // (Hide inherited decal), so this control is greyed out and reads "(superseded by Mesh footprint)".
        using (new EditorGUI.DisabledScope(cur.footprintMesh))
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel(new GUIContent("Strategic footprint (decals)",
                "The top-down decal footprint at strategic zoom. Picked from another district; grafted at runtime so the " +
                "building stays your reactor. '(baked-in)' keeps whatever the selector was baked with. Some donors' nested " +
                "decals may not transfer cleanly — check in-game. Ignored when Mesh footprint is on (the mesh is the footprint)."));
            if (GUILayout.Button(cur.footprintMesh ? "(superseded by Mesh footprint)" : FootprintDonorLabel(cur.footprintDonor ?? "")))
                new StringDropdown(new AdvancedDropdownState(), FootprintDonorNames, FootprintDonorNames, "Strategic footprint",
                    n => { cur.footprintDonor = FootprintDonorGuid(n); Repaint(); }).Show(fpPickRect);
            if (Event.current.type == EventType.Repaint) fpPickRect = GUILayoutUtility.GetLastRect();
        }
        // MESH strategic footprint — the district's OWN 3D building stays visible when zoomed out and BECOMES the footprint,
        // instead of a flat decal. See docs/District-Dedicated-Visual.md "MESH footprint". OFF = the plugin's global config
        // (DistrictFootprintMesh…) stays in charge; turning it ON here makes this entry authoritative.
        bool prevFpMesh = cur.footprintMesh;
        cur.footprintMesh = EditorGUILayout.ToggleLeft(new GUIContent("Mesh footprint (3D building as the footprint)",
            "Keep this district's own 3D building mesh rendering at strategic zoom, so the footprint IS the real model — no flat/sketchy " +
            "decal. OFF leaves the plugin's global DistrictFootprintMesh config in charge; ON makes this entry's settings authoritative."),
            cur.footprintMesh);
        if (cur.footprintMesh && !prevFpMesh)   // ticking it ON pre-fills the full shipped treatment — else the entry becomes
        {                                        // authoritative with B&W/flatten OFF and the district regresses to 3D colour.
            cur.footprintMeshBW = true; cur.footprintMeshFlat = true; cur.footprintMeshHideDecal = true;
            if (cur.footprintMeshFlatHeight < 0.03f) cur.footprintMeshFlatHeight = 0.17f;
        }
        if (cur.footprintMesh)
            using (new EditorGUI.IndentLevelScope())
            {
                cur.footprintMeshBW = EditorGUILayout.ToggleLeft(new GUIContent("Black & white when zoomed out",
                    "Render the mesh footprint greyscale on the strategic map; full colour up close."), cur.footprintMeshBW);
                cur.footprintMeshFlat = EditorGUILayout.ToggleLeft(new GUIContent("Flatten to a sheet when zoomed out",
                    "Squash the mesh flat on the strategic map so it reads as a footprint sheet, not a 3D model poking up; full height up close."), cur.footprintMeshFlat);
                using (new EditorGUI.DisabledScope(!cur.footprintMeshFlat))
                    cur.footprintMeshFlatHeight = EditorGUILayout.Slider(new GUIContent("   Flatten height",
                        "size.y multiplier when flat: ~0.02 is paper-flat but its edges drown where the tile's terrain rises over them; " +
                        "~0.17 reads flat yet clears the ground; 1 = full 3D. Live-tunable in-game via the F8 window."),
                        cur.footprintMeshFlatHeight, 0.02f, 1f);
                cur.footprintMeshHideDecal = EditorGUILayout.ToggleLeft(new GUIContent("Hide the inherited decal footprint",
                    "Drop the template's baked footprint decal (e.g. the MissileSilo outline) that would otherwise show beneath the mesh."), cur.footprintMeshHideDecal);
            }
        // (The scoped-selector GUID is baked automatically by Bake and saved on the entry — no UI row needed; the Bake
        //  status line reports whether the district landed on the scoped or legacy path.)

        EditorGUILayout.Space();
        char badChar = '\0';
        foreach (char c in cur.resourceName ?? "")
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) { badChar = c; break; }
        bool nameValid = badChar == '\0';
        bool isNew = selected <= 0;
        bool canBake = !string.IsNullOrWhiteSpace(cur.district)
                    && !string.IsNullOrWhiteSpace(cur.resourceName)
                    && nameValid
                    && (!isNew || !string.IsNullOrWhiteSpace(cur.modelFile));
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!canBake))
                if (GUILayout.Button("Bake", GUILayout.Height(34))) DoBake();
            // Persist the RUNTIME knobs (Strategic footprint, Ground, Hex sculpting, isolate, atlas GUIDs...) to
            // haf_districts.json WITHOUT re-baking — so changing a footprint doesn't re-run the model bake or mint a new
            // selector GUID. Only valid for an entry with a district set (Upsert keys on it).
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(cur.district)))
                if (GUILayout.Button(new GUIContent("Save settings", "Write this entry's runtime knobs to haf_districts.json without baking. Relaunch to apply."), GUILayout.Height(34), GUILayout.Width(110)))
                    SaveSettingsNoBake();
            if (GUILayout.Button("Reset", GUILayout.Height(34), GUILayout.Width(72))) { cur = new DistrictDef(); selected = 0; status = ""; GUI.FocusControl(null); }
        }
        if (!canBake)
            EditorGUILayout.HelpBox(
                !nameValid && !string.IsNullOrWhiteSpace(cur.resourceName)
                    ? $"Resource name can't contain '{(badChar == ' ' ? "space" : badChar.ToString())}'. Use letters, digits, '_' or '-' only."
                : isNew ? "New district model: set District, Resource name and a Model file to bake."
                        : "Set District and Resource name to bake.", MessageType.Warning);

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);

        // health panel — the editor-side validation (GUID drift / stale bundle / data prerequisites)
        // Info-level entries are neutral notes (e.g. a base-game target that can't be validated here), NOT problems —
        // count only Warning/Error toward the "issue(s)" tally so a healthy entry doesn't read as broken.
        int issues = 0; foreach (var (sev, _) in health) if (sev != MessageType.Info) issues++;
        if (health.Count > 0)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(issues > 0 ? $"Health — {issues} issue(s)" : "Health — consistent ✓ (notes below)", EditorStyles.miniBoldLabel);
                if (GUILayout.Button(new GUIContent("Re-check", "Re-run the GUID / stale-bundle / data-prerequisite checks"), GUILayout.Width(70))) RunHealthChecks();
            }
            foreach (var (sev, msg) in health) EditorGUILayout.HelpBox(msg, sev);
        }
        else if (selected > 0)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Health — registry, assets, bundle and definition all consistent ✓", EditorStyles.miniLabel);
                if (GUILayout.Button("Re-check", GUILayout.Width(70))) RunHealthChecks();
            }
        }

        DrawPreviewPane();

        EditorGUILayout.HelpBox(
            "Bake imports the model, bakes a bone-free district FxMesh, and writes the haf_districts.json entry the plugin reads.\n" +
            "• The preview below predicts the in-game look. Tune Rotation offset until it stands (re-Bake to see), Facing to turn it (live).\n" +
            "• DATA prerequisite (once per district): set a renderable ConstructibleVisualAffinity + CLEAR Additional Visual Levels on the definition.\n" +
            "• Plugin prerequisite: [District] DistrictRepoint = true (+ DistrictBufferHeadroom for big meshes in late-game cities).\n" +
            "• Then REBUILD the mod (ships the FxMesh) and relaunch.\n" +
            "Registry source (edit this, git-tracked): " + DistrictRegistry.SourcePath + "\nDeployed artifact (what the game reads, regenerated on Save): " + DistrictRegistry.RegistryPath, MessageType.None);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Open config folder", GUILayout.Width(150)))
                EditorUtility.RevealInFinder(System.IO.File.Exists(DistrictRegistry.RegistryPath)
                    ? DistrictRegistry.RegistryPath : ModelRegistry.ConfigDir);
            GUILayout.Label("↑ haf_districts.json + the plugin .cfg", EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();
    }

    // Persist the current entry's runtime knobs to haf_districts.json without re-baking (Upsert keeps the existing
    // baked GUIDs untouched). Used to change a footprint/ground/hex choice on an already-baked district.
    void SaveSettingsNoBake()
    {
        cur.district = (cur.district ?? "").Trim();
        bool ok = DistrictRegistry.Upsert(cur);
        RefreshList();
        selected = Array.IndexOf(existing, cur.district); if (selected < 0) selected = 0;
        status = ok
            ? $"Saved runtime settings for '{cur.district}' (no re-bake). Relaunch the game to apply."
            : "Registry SAVE FAILED — see Console (is haf_districts.json locked / open elsewhere?).";
        if (!ok) Debug.LogError("[District] " + status);
        GUI.FocusControl(null);
    }

    void DoBake()
    {
        // trim on cur ITSELF so what's baked and what's registered stay identical (unit-window review finding E1)
        cur.district = (cur.district ?? "").Trim();
        cur.resourceName = (cur.resourceName ?? "").Trim();
        cur.modelFile = (cur.modelFile ?? "").Trim();
        cur.stripParts = (cur.stripParts ?? "").Trim();

        // 1) the same static bake core as the unit Factory — pawnDescription stays empty (registry-only field, unused by Build)
        var cfg = new BakeConfig
        {
            resourceName = cur.resourceName, modelFile = cur.modelFile, pawnDescription = "",
            rotationEuler = cur.rotation, positionOffset = Vector3.zero, size = cur.size,
            normals = (NormalsMode)cur.normalsMode, smoothingAngle = cur.smoothingAngle, convertGrid = cur.convertGrid,
            targetTris = cur.targetTris, stripParts = cur.stripParts, reuseExtracted = cur.reuseExtracted,
            materialMode = MaterialMode.Auto, atlasMaxDim = cur.atlasMaxDim <= 0 ? 1024 : cur.atlasMaxDim, albedoBrightness = 1f, albedoSaturation = 1f,
        };
        var r = UniversalBaker.Build(cfg);
        if (!r.ok) { status = "Bake FAILED: " + r.error; return; }
        if (UniversalBaker.LastPrepSourceTris > 0) cur.sourceTris = UniversalBaker.LastPrepSourceTris;

        // 2) wrap the baked mesh as the bone-free district FxMesh
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Resources/" + cur.resourceName + "_ModelMesh.asset");
        if (mesh == null) { status = $"Bake succeeded but '{cur.resourceName}_ModelMesh.asset' wasn't found — can't build the FxMesh."; return; }

        // 2b) PIZZA compose: bake each part with its own knobs, then merge base + parts into ONE mesh + ONE super-atlas.
        //     (Purely bake-time — the runtime still receives a single FxMesh + atlas pair, so nothing downstream changes.)
        bool composedLeveled = false;
        string composeReceipt = null;
        if (cur.parts != null && cur.parts.Count > 0)
        {
            Texture2D LoadTex(string suffix, string res2) => AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/" + res2 + suffix + ".asset");
            var partData = new List<DistrictBaker.ComposeSource>();
            for (int i = 0; i < cur.parts.Count; i++)
            {
                var p = cur.parts[i];
                if (string.IsNullOrWhiteSpace(p.modelFile)) { status = $"Part {i + 1} has no Model file — set it or remove the part."; return; }
                var pcfg = new BakeConfig
                {
                    resourceName = cur.resourceName + "_p" + (i + 1), modelFile = p.modelFile.Trim(), pawnDescription = "",
                    rotationEuler = p.rotation, positionOffset = Vector3.zero, size = p.size,
                    normals = (NormalsMode)cur.normalsMode, smoothingAngle = cur.smoothingAngle, convertGrid = cur.convertGrid,
                    targetTris = p.targetTris > 0 ? p.targetTris : cur.targetTris, stripParts = "", reuseExtracted = false,
                    materialMode = MaterialMode.Auto, atlasMaxDim = cur.atlasMaxDim <= 0 ? 1024 : cur.atlasMaxDim, albedoBrightness = 1f, albedoSaturation = 1f,
                };
                var pr = UniversalBaker.Build(pcfg);
                if (!pr.ok) { status = $"Part {i + 1} ('{System.IO.Path.GetFileName(p.modelFile)}') bake FAILED: " + pr.error; return; }
                var pm = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Resources/" + pcfg.resourceName + "_ModelMesh.asset");
                if (pm == null) { status = $"Part {i + 1}: baked mesh '{pcfg.resourceName}_ModelMesh.asset' not found."; return; }
                partData.Add(new DistrictBaker.ComposeSource
                {
                    mesh = pm, albedo = LoadTex("_Atlas", pcfg.resourceName),
                    normal = LoadTex("_NormalAtlas", pcfg.resourceName), rough = LoadTex("_RoughAtlas", pcfg.resourceName),
                    facing = p.facing, posOffset = p.posOffset, alphaBoost = p.alphaBoost, leafScale = p.leafScale,
                    copies = p.copies,
                });
            }
            var baseSrc = new DistrictBaker.ComposeSource
            {
                mesh = mesh, albedo = LoadTex("_Atlas", cur.resourceName),
                normal = LoadTex("_NormalAtlas", cur.resourceName), rough = LoadTex("_RoughAtlas", cur.resourceName),
                facing = 0f, posOffset = Vector3.zero,
            };
            // compose does its own BASE-anchored leveling (incl. the entry's Position offset) — BakeFxMesh must not
            // re-level the union, or a side-heavy grove re-centers the whole pizza (the shifted-temple bake)
            composedLeveled = true;
            mesh = DistrictBaker.ComposeDistrict(baseSrc, partData, Quaternion.Euler(ComposedImportAngles()),
                cur.atlasMaxDim <= 0 ? 1024 : cur.atlasMaxDim, cur.posOffset, out var superAtlas, out var superNormal, out var superRough);
            // the super atlases REPLACE the entry's atlas assets (safe: compose blit-copied every input first)
            void SaveTex(Texture2D tex, string suffix, TextureFormat fmt)
            {
                tex.name = cur.resourceName + suffix;
                EditorUtility.CompressTexture(tex, fmt, TextureCompressionQuality.Normal);
                tex.Apply(false, false);
                string p2 = "Assets/Resources/" + cur.resourceName + suffix + ".asset";
                AssetDatabase.DeleteAsset(p2);
                AssetDatabase.CreateAsset(tex, p2);
            }
            // alpha-aware compression for the super albedo (cutout foliage needs DXT5; scan BEFORE compressing)
            int superAtlasW = superAtlas.width, superAtlasH = superAtlas.height;
            bool superHasAlpha = false;
            var spx = superAtlas.GetPixels32();
            for (int i = 0; i < spx.Length; i += 97) if (spx[i].a < 250) { superHasAlpha = true; break; }
            string atlasPath = "Assets/Resources/" + cur.resourceName + "_Atlas.asset";
            SaveTex(superAtlas, "_Atlas", superHasAlpha ? TextureFormat.DXT5 : TextureFormat.DXT1);
            SaveTex(superNormal, "_NormalAtlas", TextureFormat.DXT5);
            SaveTex(superRough, "_RoughAtlas", TextureFormat.DXT1);
            // the preview material must sample the super-atlas, not the base's old sheet
            var matAsset = AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/" + cur.resourceName + "_Mat.mat");
            if (matAsset != null)
            {
                matAsset.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);
                // alpha-card foliage (leaf textures that are mostly transparent): switch the PREVIEW material to
                // cutout when the super-atlas carries real transparency, so cards show leaves instead of solid
                // triangles. VERIFIED in-game 2026-08-08: the district shader honors cutout (the beech tree).
                if (superHasAlpha)
                {
                    matAsset.SetFloat("_Mode", 1f);                       // Standard shader: Cutout
                    matAsset.EnableKeyword("_ALPHATEST_ON");
                    matAsset.SetFloat("_Cutoff", 0.5f);
                    matAsset.renderQueue = 2450;
                    Debug.Log($"[District] super-atlas carries transparency — preview material set to alpha-cutout (in-game behavior of the district shader with alpha is UNVERIFIED).");
                }
                EditorUtility.SetDirty(matAsset);
            }
            AssetDatabase.SaveAssets();
            int totalCopies = 0; foreach (var p in cur.parts) totalCopies += p.copies != null ? p.copies.Count : 0;
            composeReceipt = $"composed: {cur.parts.Count} part(s) + {totalCopies} cop(ies) · super-atlas {superAtlasW}x{superAtlasH} {(superHasAlpha ? "DXT5 (alpha kept)" : "DXT1 (opaque)")} · base-anchored center";
            Debug.Log($"[District] {composeReceipt}");
        }

        // FOUNDATION: append a concrete swatch to the atlas (grows it + remaps the mesh UVs), then extrude the
        // building's footprint straight down into that swatch, so on a cliff/uneven tile it plants on a plinth
        // instead of overhanging into air. Must run BEFORE BakeFxMesh — it edits the mesh UVs the bake reads.
        Vector2 foundationUV = default;
        if (cur.foundationDepth > 0f)
        {
            foundationUV = DistrictBaker.AppendConcreteStrip(cur.resourceName, mesh);
            // the strip rewrote _Atlas.asset (delete+create) — the preview material's texture pointer is now stale;
            // re-point it at the grown atlas so the preview stays textured
            var fm = AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/" + cur.resourceName + "_Mat.mat");
            var fa = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/" + cur.resourceName + "_Atlas.asset");
            if (fm != null && fa != null) { fm.mainTexture = fa; EditorUtility.SetDirty(fm); AssetDatabase.SaveAssets(); }
        }
        string guid = DistrictBaker.BakeFxMesh(mesh, cur.resourceName, ComposedImportAngles(), out _,
            levelOnGround: !composedLeveled, postLevelOffset: composedLeveled ? Vector3.zero : cur.posOffset, clipHexPct: cur.clipHexPct,
            foundationDepth: cur.foundationDepth, foundationUV: foundationUV);
        if (string.IsNullOrEmpty(guid)) { status = "District FxMesh bake FAILED (see Console)."; return; }
        cur.fxMeshGuid = guid;
        cur.posOffsetBaked = cur.posOffset;   // the preview shows future posOffset edits as a live delta against this

        // the baked albedo atlas GUID — the plugin paints it into the district atlas page (texture injection)
        var atlasTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/" + cur.resourceName + "_Atlas.asset");
        cur.atlasGuid = atlasTex != null ? DistrictBaker.AmplitudeGuid(atlasTex) ?? "" : "";
        if (string.IsNullOrEmpty(cur.atlasGuid))
            Debug.LogWarning($"[District] no baked atlas for '{cur.resourceName}' — the model will render untextured in-game (vanilla district shading).");
        // surface-map atlases (normal/roughness packed with the same rects — empty when the model shipped none)
        var nrmTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/" + cur.resourceName + "_NormalAtlas.asset");
        cur.normalAtlasGuid = nrmTex != null ? DistrictBaker.AmplitudeGuid(nrmTex) ?? "" : "";
        var rghTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/" + cur.resourceName + "_RoughAtlas.asset");
        cur.roughAtlasGuid = rghTex != null ? DistrictBaker.AmplitudeGuid(rghTex) ?? "" : "";
        // (composed entries: the guids above now point at the SUPER normal/rough atlases — packed with the super
        // albedo's exact rects, base maps blitted in, neutral fill for map-less parts — so the marble keeps its
        // relief. The v1 albedo-only shortcut turned the whole temple donor-map blue; falsified same evening.)

        // 2c) SCOPED selector — bake the data-authored CityMapSelector so this district renders via the scoped path (the
        //     reactor's route: mesh footprint, per-district texture/B&W/flatten), not the legacy isolate/repoint path.
        //     Best-effort: the model already baked, so a selector failure (e.g. an emitter-only footprint template) just
        //     leaves the district on the legacy path with a warning instead of aborting the whole bake.
        cur.selectorGuid = "";   // clear BEFORE the (re-)bake so a FAILURE genuinely falls to the legacy path (as the comment
                                 // above promises). Without this, a re-bake keeps a STALE selectorGuid from a previous bake:
                                 // the entry Upserts as "✓" but routes through the scoped path with an old selector against the
                                 // freshly-minted fxMesh (delete+create) — a broken/empty district that reports success.
        if (DistrictBaker.BakeScopedSelector(cur.resourceName, out var selGuid, out var selErr))
            cur.selectorGuid = selGuid;
        else
            Debug.LogWarning($"[District] '{cur.resourceName}': scoped selector NOT baked ({selErr}) — the district stays on the legacy path. Pick a single-building Footprint template (Tools/HAF/District/Footprint template...) and re-bake to migrate it.");

        LoadPreviewAssets(force: true);   // fresh assets exist even if the registry save below fails

        // 3) registry entry
        bool saved = DistrictRegistry.Upsert(cur);
        RefreshList();
        selected = Array.IndexOf(existing, cur.district); if (selected < 0) selected = 0;
        if (!saved)
        {
            status = $"Baked '{cur.resourceName}', but the REGISTRY SAVE FAILED (see Console). Close whatever's locking haf_districts.json and re-bake.";
            Debug.LogError("[District] " + status);
            return;
        }
        status = $"Baked district model '{cur.resourceName}' -> '{cur.district}'\nFxMesh {guid}  (verts={mesh.vertexCount}, tris={TriCount(mesh)}{(cur.sourceTris > 0 ? $", source model {cur.sourceTris:N0} tris" : "")})\n" +
                 (composeReceipt != null ? composeReceipt + "\n" : "") +
                 (string.IsNullOrWhiteSpace(cur.selectorGuid) ? "scoped selector: NOT baked (legacy path) — see Console\n" : $"scoped selector {cur.selectorGuid} (scoped path)\n") +
                 "Check the FxMesh Inspector preview for orientation, then rebuild the mod + relaunch.";
        Debug.Log("[District] " + status);
        RunHealthChecks();   // fresh bake: the stale-bundle warning should light up until the mod is rebuilt
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/Resources/" + cur.resourceName + "_FxMesh.asset");
    }

    // ---- embedded preview ----

    void LoadPreviewAssets(bool force = false)
    {
        string res = (cur.resourceName ?? "").Trim();
        if (!force && res == pvLoadedFor) return;
        pvLoadedFor = res; pvMesh = null; pvMats = null; pvPan = Vector2.zero;
        if (string.IsNullOrEmpty(res)) return;
        // prefer the SHIPPED district mesh (bone-free, rotation offset baked in); fall back to the unit-style bake output
        pvMesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Resources/" + res + "_DistrictMesh.asset")
              ?? AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Resources/" + res + "_ModelMesh.asset");
        if (pvMesh == null) return;
        // the static bake writes the atlased Standard material to Resources/<res>_Mat.mat — use it so the preview is
        // textured; the FactorySource PreviewMat only exists for unit-path bakes of the same resource name
        var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/" + res + "_Mat.mat")
               ?? AssetDatabase.LoadAssetAtPath<Material>("Assets/FactorySource/" + res + "/" + res + "_PreviewMat.mat");
        var mats = new Material[Mathf.Max(1, pvMesh.subMeshCount)];
        for (int i = 0; i < mats.Length; i++) mats[i] = mat;   // null slots fall back at draw time (proper fake-null check there)
        pvMats = mats;
    }

    void DrawPreviewPane()
    {
        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Preview — predicts the in-game orientation", EditorStyles.miniBoldLabel);
            if (GUILayout.Button(new GUIContent("Center", "Re-center the view on the model (resets pan + zoom; keeps the orbit angle)"), GUILayout.Width(60)))
            { pvPan = Vector2.zero; pvZoom = 1f; Repaint(); }
            if (GUILayout.Button("Refresh", GUILayout.Width(70))) LoadPreviewAssets(force: true);
        }
        LoadPreviewAssets();
        if (pvMesh == null)
        {
            EditorGUILayout.HelpBox(string.IsNullOrWhiteSpace(cur.resourceName)
                ? "Set a Resource name (or pick an existing entry) to preview its baked mesh."
                : $"No baked mesh for '{cur.resourceName.Trim()}' yet — press Bake and the preview appears here.", MessageType.Info);
            return;
        }
        // grow with the window: ~45% of its height so a tall dock gets a big viewport, never under 300px
        var rect = GUILayoutUtility.GetRect(10f, Mathf.Max(300f, position.height * 0.45f), GUILayout.ExpandWidth(true));
        DrawPreview(rect);
        EditorGUILayout.LabelField($"{pvMesh.vertexCount} verts · {TriCount(pvMesh)} tris · hex = one district tile at TRUE in-game size, at surface level · N line = map North (Facing 0° points along it) · LMB orbit, wheel zoom, MMB/RMB pan", EditorStyles.miniLabel);
        EditorGUILayout.LabelField("Facing + Position offset preview LIVE (Bake makes them real). Rotation offset is baked into the mesh — re-Bake to see it.", EditorStyles.miniLabel);
    }

    void DrawPreview(Rect rect)
    {
        var e = Event.current;
        if (rect.Contains(e.mousePosition))
        {
            if (e.type == EventType.ScrollWheel)
            {
                // consume the wheel HERE so the window's scroll view never sees it — this is the zoom
                pvZoom = Mathf.Clamp(pvZoom * Mathf.Pow(1.12f, e.delta.y > 0 ? 1f : -1f), 0.1f, 5f);
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0)
            {
                pvOrbit += new Vector2(e.delta.x, -e.delta.y) * 0.7f;
                pvOrbit.y = Mathf.Clamp(pvOrbit.y, -89f, 89f);
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && (e.button == 1 || e.button == 2))
            {
                pvPan += new Vector2(-e.delta.x, e.delta.y) * 0.0035f;   // pan in the camera plane, scaled by view distance at render time
                e.Use(); Repaint();
            }
        }
        if (e.type != EventType.Repaint) return;
        if (pvMesh == null) { pvLoadedFor = null; return; }   // asset deleted under us (a re-bake) — reload on the next paint
        if (pru == null) pru = new PreviewRenderUtility();
        if (pvFallbackMat == null) pvFallbackMat = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave };
        if (pvTileMat == null)
        {
            pvTileMat = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave };
            pvTileMat.SetFloat("_Glossiness", 0f);
        }
        // show the chosen ground material in the preview: the ACTUAL terrain texture (dumped by the plugin to
        // haf_ground_tex/<name>.png) when available, else the material's true tint (haf_ground_colors.json), else a
        // by-family colour. Texture wins — set white base so it isn't tinted, and tile it a few times across the hex.
        var groundTex = GroundTexture(cur.groundMaterial);
        pvTileMat.mainTexture = groundTex;
        pvTileMat.color = groundTex != null ? Color.white : GroundTint(cur.groundMaterial);
        if (groundTex != null) pvTileMat.mainTextureScale = new Vector2(3f, 3f);
        pru.BeginPreview(rect, GUIStyle.none);
        // try/finally so a throw in DrawMesh/Render can never skip EndPreview (the "BeginPreview not closed" cascade)
        Texture tex = null;
        try
        {
            // the game's draw-time rotation, LIVE from the form fields — dial the model upright + turned right here.
            // Position offset previews as a DELTA vs what the current bake already carries (baked into the vertices).
            var mtx = Matrix4x4.Translate(cur.posOffset - cur.posOffsetBaked)
                    * Matrix4x4.Rotate(Quaternion.Euler(0f, cur.facing, 0f) * Quaternion.Euler(cur.importAngles));
            var b = TransformBounds(mtx, pvMesh.bounds);
            // the FOUNDATION plinth extrudes below the surface (y<0); don't let it drag the framing down — the camera
            // frames on the ABOVE-GROUND building (clamp the bottom to the surface) so adding a plinth doesn't shift
            // the view center. The plinth still draws; it's just not counted when centering.
            if (b.min.y < 0f) { var mn = b.min; var mx = b.max; mn.y = 0f; b.SetMinMax(mn, mx); }
            // the tile square is the TRUE in-game surface: the plane through the origin. It must NOT follow the model —
            // anchoring it under the mesh's lowest point hid a half-sunk bake (the nuclear plant surfaced only its domes
            // in-game while the preview looked grounded). A model below this plane previews sunk because it IS sunk.
            // The preview reference frame is oriented to match how the game draws the district cell: the hexgrid is turned
            // +30° (a hex is 60°-symmetric, so this is its visible correction) and the compass/North indicator +150° so
            // map North points where it actually does in-game. Both are clockwise-from-above (Unity +Y). Preview-only.
            var tileMtx = Matrix4x4.Translate(new Vector3(0f, -0.02f, 0f)) * Matrix4x4.Rotate(Quaternion.Euler(0f, 30f, 0f));
            var frame = b; frame.Encapsulate(TransformBounds(tileMtx, TileMesh().bounds));

            var cam = pru.camera;
            float radius = Mathf.Max(frame.extents.magnitude, 0.1f);
            float dist = radius * 2.0f * pvZoom;
            var rot = Quaternion.Euler(-pvOrbit.y, pvOrbit.x, 0f);
            var lookAt = frame.center + rot * new Vector3(pvPan.x, pvPan.y, 0f) * dist;
            cam.transform.position = lookAt + rot * (Vector3.back * dist);
            cam.transform.rotation = Quaternion.LookRotation(lookAt - cam.transform.position);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = dist + radius * 4f;
            cam.fieldOfView = 30f;
            pru.lights[0].intensity = 1.3f;
            pru.lights[0].transform.rotation = Quaternion.Euler(45f, 45f, 0f);
            if (pru.lights.Length > 1) pru.lights[1].intensity = 0.6f;
            pru.ambientColor = new Color(0.3f, 0.3f, 0.3f);

            pru.DrawMesh(TileMesh(), tileMtx, pvTileMat, 0);
            if (pvArrowMesh == null) pvArrowMesh = ModelFactoryWindow.BuildCompass("DistrictCompass");
            pru.DrawMesh(pvArrowMesh, Matrix4x4.Translate(new Vector3(0f, -0.01f, 0f)) * Matrix4x4.Rotate(Quaternion.Euler(0f, 150f, 0f)), pvFallbackMat, 0);
            for (int s = 0; s < pvMesh.subMeshCount; s++)
            {
                var m = pvMats != null && pvMats.Length > 0 ? pvMats[Mathf.Min(s, pvMats.Length - 1)] : null;
                if (m == null) m = pvFallbackMat;   // Unity fake-null too (the material asset dies on a re-bake)
                pru.DrawMesh(pvMesh, mtx, m, s);
            }
            cam.Render();
        }
        finally { tex = pru.EndPreview(); }
        if (tex != null) GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
    }

    // Triangle count without allocating (Mesh.triangles copies the whole index array per access — per-repaint GC churn)
    static int TriCount(Mesh m)
    {
        long n = 0;
        for (int s = 0; s < m.subMeshCount; s++) n += m.GetIndexCount(s);
        return (int)(n / 3);
    }

    Mesh TileMesh()
    {
        if (pvTileMesh == null) pvTileMesh = ModelFactoryWindow.BuildTileHex("DistrictTileHex", 0f);   // corner faces +Z (district cell orientation)
        return pvTileMesh;
    }

    // Preview colour for a GroundMaterialDefinition. Prefers the TRUE per-material tint the plugin dumped to
    // haf_ground_colors.json (from each material's GroundMaterialAuthoringData.Color — run the game once with the
    // districts loaded to generate it); falls back to a by-family colour when the dump isn't present yet.
    static Dictionary<string, Color> groundColors;
    static bool groundColorsTried;
    static void LoadGroundColors()
    {
        groundColorsTried = true; groundColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = System.IO.Path.Combine(ModelRegistry.ConfigDir, "haf_ground_colors.json");
            if (!System.IO.File.Exists(path)) return;
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, "\"([^\"]+)\"\\s*:\\s*\\[\\s*([0-9.]+)\\s*,\\s*([0-9.]+)\\s*,\\s*([0-9.]+)");
                if (m.Success)
                    groundColors[m.Groups[1].Value] = new Color(
                        float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch { }
    }
    // The ACTUAL terrain texture the plugin dumped for this ground material (haf_ground_tex/<name>.png), loaded
    // once and cached. Null when the dump hasn't run yet or the material shipped no texture.
    static readonly Dictionary<string, Texture2D> groundTexCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    static Texture2D GroundTexture(string ground)
    {
        if (string.IsNullOrEmpty(ground)) return null;
        if (groundTexCache.TryGetValue(ground, out var t) && t != null) return t;   // only cache SUCCESSFUL loads — retry if the PNG appears later (the plugin dumps it on a game run)
        Texture2D tex = null;
        try
        {
            var path = System.IO.Path.Combine(ModelRegistry.ConfigDir, "haf_ground_tex", ground + ".png");
            if (System.IO.File.Exists(path))
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "ground_" + ground, hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Repeat };
                tex.LoadImage(System.IO.File.ReadAllBytes(path));
            }
        }
        catch { }
        if (tex != null) groundTexCache[ground] = tex;
        return tex;
    }
    static Color GroundTint(string ground)
    {
        if (string.IsNullOrEmpty(ground)) return new Color(0.33f, 0.40f, 0.29f);   // the old neutral tile
        if (!groundColorsTried) LoadGroundColors();
        if (groundColors != null && groundColors.TryGetValue(ground, out var exact)) return exact;   // the material's TRUE tint
        if (ground.StartsWith("Prairie", StringComparison.OrdinalIgnoreCase)) return new Color(0.30f, 0.46f, 0.22f);      // grass green
        if (ground.StartsWith("Constructible", StringComparison.OrdinalIgnoreCase)) return new Color(0.52f, 0.49f, 0.42f); // paved/dirt
        if (ground.StartsWith("Sterile", StringComparison.OrdinalIgnoreCase)) return new Color(0.62f, 0.56f, 0.40f);       // dry sparse
        return new Color(0.33f, 0.40f, 0.29f);
    }

    static Bounds TransformBounds(Matrix4x4 m, Bounds b)
    {
        var c = m.MultiplyPoint3x4(b.center);
        var e = b.extents;
        var ne = new Vector3(
            Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
            Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
            Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
        return new Bounds(c, ne * 2f);
    }

    // The full draw-time rotation written into the FxMesh: Facing (a yaw about the drawn-space vertical) applied AFTER
    // the import angles — so import angles stand the model up and Facing turns the standing model, never tips it.
    Vector3 ComposedImportAngles() =>
        (Quaternion.Euler(0f, cur.facing, 0f) * Quaternion.Euler(cur.importAngles)).eulerAngles;

    // Extension_Base_BreederReactor -> "BreederReactor". Suggested resource name.
    // The game's GroundMaterialDefinition vocabulary (from the plugin's [Ground] dump — stable game data). Prairie_* =
    // lush grass fields; Constructible_* = the paved/maintained ground districts use; Sterile_* = sparse/dry.
    static readonly string[] GroundMaterialNames =
    {
        "(none)",
        "Prairie_Grassland", "Prairie_Temperate", "Prairie_Mediterranean", "Prairie_Savanna", "Prairie_Tropical",
        "Prairie_Taiga", "Prairie_Tundra", "Prairie_Arctic", "Prairie_Badlands", "Prairie_Desert",
        "Constructible_Temperate_01", "Constructible_Temperate_02", "Constructible_Temperate_03",
        "Constructible_Hot_01", "Constructible_Hot_02", "Constructible_Hot_03",
        "Constructible_Dry_01", "Constructible_Dry_02", "Constructible_Dry_03",
        "Constructible_Cold_01", "Constructible_Cold_02", "Constructible_Cold_03",
        "Sterile_Desert", "Sterile_Grassland", "Sterile_Mediterranean", "Sterile_Savanna",
        "Sterile_Taiga", "Sterile_Temperate", "Sterile_Tropical", "Sterile_Tundra",
    };

    // HexagonSculptingDefinition vocabulary (from the plugin's [HexSculpt] dump). EmblematicAndCityCenter* = the
    // district/building raised-platform footprints; POI_* = natural/resource point-of-interest shapes.
    // Strategic-footprint donor districts (name -> selector GUID "a,b,c,d"). The plugin grafts the chosen donor's top-down
    // decal footprint onto our reactor at runtime; the building stays ours. "(baked-in ...)" = keep the selector's own.
    static readonly (string name, string guid)[] FootprintDonors =
    {
        ("(baked-in — keep selector's own)", ""),
        ("Industry (factories)",   "149945011,1306056350,1706429623,-368887441"),
        ("MissileSilo",            "-1158439761,1096327552,-1625448046,-477384506"),
        ("NuclearTest (brick plant)","-1883953677,1215187674,-1533191005,-2060159479"),
        ("Science",                "217712326,1333435725,1112758,601061218"),
        ("Food",                   "-1632914879,1264979107,-860135776,898048793"),
        ("Military",               "1990225763,1262659521,1233462935,-36990848"),
        ("Money",                  "-1482860262,1190406099,-1169012827,610018136"),
        ("Order",                  "307882725,1278992011,560327327,373902021"),
        ("Harbour",                "1135955116,1293714479,321596598,-1162436722"),
        ("SpaceLaunch (rocket pad)","-711220426,1124345735,-1819569492,72056027"),
        ("SatelliteLaunch",        "1708676664,1196002645,-305692763,1766765912"),
        ("HolySite",               "-1674189652,1156219232,1325226633,-1273229686"),
    };
    static string[] FootprintDonorNames => System.Array.ConvertAll(FootprintDonors, d => d.name);
    static string FootprintDonorGuid(string name) { foreach (var d in FootprintDonors) if (d.name == name) return d.guid; return ""; }
    static string FootprintDonorLabel(string guid) { foreach (var d in FootprintDonors) if (d.guid == guid) return d.name; return string.IsNullOrEmpty(guid) ? FootprintDonors[0].name : "custom (" + guid + ")"; }
    static Rect fpPickRect;

    static readonly string[] HexSculptNames = BuildHexNames();
    static string[] BuildHexNames()
    {
        var l = new List<string> { "(none)" };
        for (int i = 1; i <= 33; i++) l.Add("EmblematicAndCityCenter" + i.ToString("00"));
        foreach (var s in new[] { "03", "08", "09", "15", "21", "27", "33" }) l.Add("EmblematicAndCityCenters_Set02_" + s);
        for (int i = 1; i <= 18; i++) l.Add("POI_NaturalModifier" + i.ToString("00"));
        for (int i = 1; i <= 8; i++) l.Add("POI_ResourceStrategic" + i.ToString("00"));
        return l.ToArray();
    }

    static string DeriveResourceName(string districtName)
    {
        if (string.IsNullOrEmpty(districtName)) return "";
        var parts = districtName.Split('_');
        return parts.Length > 0 ? parts[parts.Length - 1] : districtName;
    }

    // Every district-flavoured ConstructibleDefinition name found in the project databases (vanilla SDK + ENC). District
    // definitions live as sub-assets of the Constructible*ExtensionDefinition database assets; their concrete types all
    // end in "DistrictDefinition" (ExtensionDistrictDefinition, ArtificialDepositDistrictDefinition, Wondrous…).
    static string[] districtCache;
    static string[] GatherDistrictNames()
    {
        if (districtCache != null) return districtCache;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in AssetDatabase.FindAssets("ConstructibleCommonExtensionDefinition"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".asset")) continue;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && o.GetType().Name.EndsWith("DistrictDefinition") && !string.IsNullOrEmpty(o.name))
                    names.Add(o.name);
        }
        districtCache = names.ToArray();
        return districtCache;
    }
}
