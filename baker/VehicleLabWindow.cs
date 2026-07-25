// VehicleLabWindow.cs — "Vehicle Lab" (Tools ▸ HAF ▸ Vehicle Lab, 2026-07-25): VEHICLEIZE a raw STATIC vehicle
// model into the rigged, Spin-animated GLB the animated bake path consumes — the hand-made Ehrhardt recipe
// (Animated-Models.md) as a dialog:
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
    enum Role { Body, Wheel, Turret, Ignore, Default }
    [Serializable] class Part { public string name; public int verts; public Vector3 center, size; public Role role; }

    // Everything an assignment session builds is [SerializeField]: a DOMAIN RELOAD (any recompile) must never eat
    // the marked roles again — the field incident that motivated recipes in the first place.
    [SerializeField] string srcFile = "";
    [SerializeField] string outGlb = "";   // explicit output path — never silently derived at write time (overwrite guard in Vehicleize)
    [SerializeField] List<Part> parts = new List<Part>();
    [SerializeField] int frames = 15; [SerializeField] float degrees = -360f; [SerializeField] int axisChoice = 0;   // 0 = Auto (per wheel), 1..3 = X/Y/Z
    [SerializeField] int minVerts = 50;   // parts below this are COLLAPSED into Body (a triangle-soup FBX probes into thousands of shards)
    Vector2 partsScroll;

    // ── Recipes: the whole vehicleize configuration as a JSON file — reload it after a restart, keep one per model,
    // ship it next to the model so a collaborator reproduces the exact rig. ──
    [Serializable] class Recipe { public string srcFile, outGlb; public int frames, axisChoice, minVerts; public float degrees; public List<Part> parts; }
    const string RecipesDir = "Assets/FactorySource/VehicleLab/Recipes";
    static readonly string[] AxisOptions = { "Auto (thinnest extent = axle, per wheel)", "X", "Y", "Z" };
    string status = "";
    string lastOutGlb = "";

    // turntable preview state
    GameObject inst; PreviewRenderUtility pru; AnimationClip spinClip;
    Bounds bounds; bool boundsValid; float spinT; double lastTick;
    float fullRadius;   // whole-model radius — far-plane margin must NOT shrink to a focused part's bounds
    Vector2 orbit = new Vector2(140f, -18f); float zoom = 1.5f;
    // part focus/highlight: clicking a row zooms onto that part and tints it — the "which shard is the wheel?" x-ray
    string selectedPart = "";
    Renderer highlightedRenderer; Material[] highlightedOriginals;
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
        if (inst != null) { spinT += (float)(now - lastTick); Repaint(); }
        lastTick = now;
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Vehicleize a STATIC vehicle: probe its parts, mark the wheels (and turret), and generate the rigged " +
            "Spin GLB the animated bake consumes — no Blender knowledge needed. The preview below plays the result. " +
            "Then bake it in the Factory/Animation Lab (settings printed on success).", MessageType.Info);

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

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(srcFile) || !File.Exists(srcFile)))
                if (GUILayout.Button(new GUIContent("Probe parts", "Headless Blender lists the model's mesh parts (a single combined mesh is split into loose parts). Roles are auto-guessed from names."), GUILayout.Height(24)))
                    Probe();
            using (new EditorGUI.DisabledScope(parts.Count == 0))
                if (GUILayout.Button(new GUIContent("Save recipe", "Save the whole configuration (source, output, roles, knobs) as JSON — reload it any time with Load."), GUILayout.Width(90), GUILayout.Height(24)))
                    SaveRecipe();
            if (GUILayout.Button(new GUIContent("Load recipe…", "Restore a saved configuration."), GUILayout.Width(100), GUILayout.Height(24)))
                LoadRecipe();
        }

        if (parts.Count > 0)
        {
            // Tiny-fragment collapse: a triangle-soup FBX probes into THOUSANDS of 3-4-vert shards — they all belong
            // to Body anyway (anything not marked wheel/turret skins to Root). Only substantial parts are listed.
            minVerts = EditorGUILayout.IntSlider(new GUIContent("Hide parts under (verts)",
                "Parts smaller than this are collapsed into Body automatically (they skin to Root). Raise it if the list is still noisy; lower it if a small wheel is missing."), minVerts, 1, 2000);
            var shown = parts.Where(x => x.verts >= minVerts).ToList();
            int hidden = parts.Count - shown.Count;
            int unreviewed = shown.Count(x => x.role == Role.Default);
            EditorGUILayout.LabelField($"Parts ({shown.Count} shown{(hidden > 0 ? $", {hidden} tiny fragments auto-collapsed" : "")}{(unreviewed > 0 ? $", {unreviewed} unreviewed" : ", all reviewed")}) — mark the wheels & turret:", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("  Keys:  ↑/↓ = previous/next part (zooms + highlights it below)   ·   W/T/B/I/D = mark selected as Wheel/Turret/Body/Ignore/Default(unreviewed)", EditorStyles.miniLabel);
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
                else if (idx >= 0 && (ev.keyCode == KeyCode.W || ev.keyCode == KeyCode.T || ev.keyCode == KeyCode.B || ev.keyCode == KeyCode.I || ev.keyCode == KeyCode.D))
                {
                    shown[idx].role = ev.keyCode == KeyCode.W ? Role.Wheel
                                    : ev.keyCode == KeyCode.T ? Role.Turret
                                    : ev.keyCode == KeyCode.I ? Role.Ignore
                                    : ev.keyCode == KeyCode.D ? Role.Default : Role.Body;
                    ev.Use(); Repaint();
                }
            }
            partsScroll = EditorGUILayout.BeginScrollView(partsScroll, GUILayout.ExpandHeight(true), GUILayout.MinHeight(280));   // roomy by default (user request: double the original 120)
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
            axisChoice = EditorGUILayout.Popup(new GUIContent("Axle axis", "Auto infers each wheel's axle as its thinnest bbox extent — right for normal wheels; override only if a wheel spins the wrong way around."), axisChoice, AxisOptions);
            frames = EditorGUILayout.IntSlider(new GUIContent("Spin frames", "Length of the generated Spin action. Apparent speed is tuned later with slice steps (Spin[1..N/2]) — this just needs to be a smooth loop."), frames, 5, 60);
            degrees = EditorGUILayout.Slider(new GUIContent("Spin degrees", "Wheel rotation over the clip. -360 = one full forward turn (negate if wheels roll backward in the preview)."), degrees, -720f, 720f);

            int wheels = parts.Count(x => x.role == Role.Wheel);
            using (new EditorGUI.DisabledScope(wheels == 0 || string.IsNullOrEmpty(outGlb)))
                if (GUILayout.Button(new GUIContent($"Vehicleize  →  {(string.IsNullOrEmpty(outGlb) ? "(set the Output GLB)" : Path.GetFileName(outGlb))}",
                        wheels == 0 ? "Mark at least one part as Wheel." : "Runs Blender: rig + Spin action + GLB export + preview."), GUILayout.Height(28)))
                    Vehicleize();
        }

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

        // turntable preview (the real imported preview FBX playing its Spin clip)
        if (inst != null)
        {
            var rect = GUILayoutUtility.GetRect(200, 260, GUILayout.ExpandWidth(true));
            HandlePreviewInput(rect);
            if (Event.current.type == EventType.Repaint) RenderPreview(rect);
        }
    }

    void Probe()
    {
        // Re-probing MERGES, never wipes: roles already assigned (by hand or a loaded recipe) are re-applied by
        // part name. This is also how a minimal recipe expands for review — Load recipe, then Probe to surface
        // every unmarked part around the kept markings.
        var kept = new Dictionary<string, Role>();
        foreach (var p0 in parts) if (p0.role != Role.Default) kept[p0.name] = p0.role;   // explicit Body verdicts survive too
        parts.Clear(); DestroyPreview();
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
            if (t.Length != 5 || t[0] != "PART") continue;
            var c = t[3].Split(','); var s = t[4].Split(',');
            if (c.Length != 3 || s.Length != 3) continue;
            var p = new Part
            {
                name = t[1],
                verts = int.TryParse(t[2], out var v) ? v : 0,
                center = new Vector3(F(c[0]), F(c[1]), F(c[2])),
                size = new Vector3(F(s[0]), F(s[1]), F(s[2])),
            };
            var low = p.name.ToLowerInvariant();
            p.role = kept.TryGetValue(p.name, out var kr) ? kr
                   : low.Contains("wheel") || low.Contains("tyre") || low.Contains("tire") ? Role.Wheel
                   : low.Contains("turret") ? Role.Turret : Role.Default;
            parts.Add(p);
        }
        if (File.Exists(prevFull))
        {
            AssetDatabase.ImportAsset(prevRel, ImportAssetOptions.ForceUpdate);
            BuildPreview(prevRel);   // no Spin clip yet — a static turntable for part inspection
        }
        status = parts.Count == 0
            ? "Probe found no mesh parts — is this a mesh model? (See the Console for Blender output.)"
            : $"Probed {parts.Count} part(s); {parts.Count(x => x.role == Role.Wheel)} wheel(s), {parts.Count(x => x.role == Role.Turret)} turret(s)" +
              (kept.Count > 0 ? $" ({parts.Count(x => kept.ContainsKey(x.name) && x.role == kept[x.name])} of {kept.Count} earlier markings kept)" : " (auto-guessed)") +
              ". Click a row to see WHICH part it is (zoom + yellow highlight), assign roles, then Vehicleize.";
    }

    void SaveRecipe()
    {
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        Directory.CreateDirectory(Path.Combine(projRoot, RecipesDir));
        string def = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(srcFile) ? "vehicle" : srcFile);
        string p = EditorUtility.SaveFilePanel("Save vehicleize recipe", Path.Combine(projRoot, RecipesDir), def, "json");
        if (string.IsNullOrEmpty(p)) return;
        var r = new Recipe { srcFile = srcFile, outGlb = outGlb, frames = frames, axisChoice = axisChoice, minVerts = minVerts, degrees = degrees, parts = parts };
        File.WriteAllText(p, JsonUtility.ToJson(r, true));
        AssetDatabase.Refresh();
        status = "Recipe saved: " + p;
    }

    void LoadRecipe()
    {
        string projRoot = Directory.GetParent(Application.dataPath).FullName;
        string dir = Path.Combine(projRoot, RecipesDir);
        string p = EditorUtility.OpenFilePanel("Load vehicleize recipe", Directory.Exists(dir) ? dir : projRoot, "json");
        if (string.IsNullOrEmpty(p) || !File.Exists(p)) return;
        try
        {
            var r = JsonUtility.FromJson<Recipe>(File.ReadAllText(p));
            if (r == null || r.parts == null) { status = "Not a vehicleize recipe: " + p; return; }
            DestroyPreview();
            srcFile = r.srcFile; outGlb = r.outGlb; frames = r.frames; axisChoice = r.axisChoice; minVerts = r.minVerts; degrees = r.degrees;
            parts = r.parts;
            status = $"Recipe loaded ({parts.Count} parts, {parts.Count(x => x.role == Role.Wheel)} wheels). " +
                     "Vehicleize directly — or press Probe to list ALL parts for review (your marked roles are kept, plus the preview returns for click-to-highlight).";
        }
        catch (Exception e) { status = "Recipe load failed: " + e.Message; }
    }

    // Zoom the preview onto one part and tint it — restores the previous part's materials first. "" = full view.
    void SelectPart(string name)
    {
        if (highlightedRenderer != null && highlightedOriginals != null)
        { try { highlightedRenderer.sharedMaterials = highlightedOriginals; } catch { } }
        highlightedRenderer = null; highlightedOriginals = null;
        selectedPart = name;
        boundsValid = false;   // re-derive (full model or the part) on next render
        if (inst == null || string.IsNullOrEmpty(name)) return;
        var r = inst.GetComponentsInChildren<Renderer>()
                    .FirstOrDefault(x => x != null && (x.gameObject.name == name || x.gameObject.name.StartsWith(name)));
        if (r == null) return;
        if (highlightMat == null)
        {
            var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            highlightMat = new Material(sh) { color = new Color(1f, 0.85f, 0.1f) };
            highlightMat.hideFlags = HideFlags.HideAndDontSave;
        }
        highlightedRenderer = r;
        highlightedOriginals = r.sharedMaterials;
        r.sharedMaterials = Enumerable.Repeat(highlightMat, r.sharedMaterials.Length).ToArray();
        bounds = r.bounds; bounds.Expand(bounds.size.magnitude * 0.6f + 0.1f); boundsValid = true;   // frame the part with context
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
        string wheels = string.Join(";", parts.Where(p => p.role == Role.Wheel).Select(p => p.name));
        string turrets = string.Join(";", parts.Where(p => p.role == Role.Turret).Select(p => p.name));
        string axis = axisChoice == 0 ? "AUTO" : AxisOptions[axisChoice];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!RunBlender($"rig \"{srcFile}\" \"{lastOutGlb}\" \"{prevFull}\" \"{wheels}\" \"{turrets}\" {axis} {frames} {degrees.ToString("0.#", inv)}", out string stdout)) return;
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
        status = $"DONE → {lastOutGlb}\n{done}\n\nNext: Factory ▸ Browse this GLB, Size as usual; Animation Lab ▸ State-driven, " +
                 $"Idle/reference = Spin[0..0], Movement = Spin[1..{frames}] (add /2 etc. to taste), Convert raw rig ON, " +
                 "Fix 100× OFF, Auto-ground ON. Bake.";
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
            var p = new System.Diagnostics.Process();
            p.StartInfo.FileName = UniversalBaker.FindBlender();
            p.StartInfo.Arguments = $"--background --python \"{script}\" -- {args}";
            p.StartInfo.UseShellExecute = false; p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true; p.StartInfo.RedirectStandardError = true;
            p.Start();
            if (!UniversalBaker.RunBounded(p, 300000, out stdout, out string stderr)) { status = "Blender timed out (5 min)."; return false; }
            if (stdout.Contains("VEHICLE ERROR"))
            { status = stdout.Split('\n').FirstOrDefault(l => l.Contains("VEHICLE ERROR")) ?? "Blender step failed."; Debug.LogError("[VehicleLab]\n" + stdout + "\n--- stderr ---\n" + stderr); return false; }
            Debug.Log("[VehicleLab]\n" + string.Join("\n", stdout.Split('\n').Where(l => l.StartsWith("PART|") || l.StartsWith("VEHICLE"))));
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
        spinClip = AssetDatabase.LoadAllAssetsAtPath(prevRel).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
        if (pru == null) pru = new PreviewRenderUtility();
        inst = Instantiate(prefab);
        pru.AddSingleGO(inst);
        boundsValid = false; spinT = 0f;
    }
    void DestroyPreview()
    {
        if (inst != null) { DestroyImmediate(inst); inst = null; }
        spinClip = null; boundsValid = false;
        selectedPart = ""; highlightedRenderer = null; highlightedOriginals = null;
    }
    void HandlePreviewInput(Rect rect)
    {
        var e = Event.current;
        if (!rect.Contains(e.mousePosition)) return;
        // zoom-out ceiling 50 (was 5): with a TINY part focused, distance scales off its bounds — seeing the part
        // in the context of the whole vehicle needs an order of magnitude more headroom.
        if (e.type == EventType.ScrollWheel) { zoom = Mathf.Clamp(zoom * Mathf.Pow(1.12f, e.delta.y > 0 ? 1f : -1f), 0.2f, 50f); e.Use(); }
        else if (e.type == EventType.MouseDrag && e.button == 0) { orbit += new Vector2(e.delta.x, -e.delta.y) * 0.7f; orbit.y = Mathf.Clamp(orbit.y, -89f, 89f); e.Use(); }
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
        cam.transform.position = bounds.center + rot * (Vector3.back * dist);
        cam.transform.rotation = Quaternion.LookRotation(bounds.center - cam.transform.position);
        // far-plane margin uses the WHOLE model's radius: with a tiny part focused, `radius` is that part's — a
        // part-scaled margin put the far plane just behind the shard and visibly carved the vehicle away on zoom-out.
        cam.nearClipPlane = 0.01f; cam.farClipPlane = dist + Mathf.Max(radius, fullRadius) * 4f; cam.fieldOfView = 30f;
        pru.lights[0].intensity = 1.3f;
        pru.lights[0].transform.rotation = Quaternion.Euler(45f, 45f, 0f);
        if (pru.lights.Length > 1) pru.lights[1].intensity = 0.6f;
        pru.ambientColor = new Color(0.3f, 0.3f, 0.3f);
        cam.Render();
        GUI.DrawTexture(rect, pru.EndPreview(), ScaleMode.StretchToFill, false);
    }
}
