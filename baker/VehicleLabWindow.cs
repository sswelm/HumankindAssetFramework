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

    enum Role { Body, Wheel, Turret, Ignore }
    class Part { public string name; public int verts; public Vector3 center, size; public Role role; }

    [SerializeField] string srcFile = "";
    List<Part> parts = new List<Part>();
    int frames = 15; float degrees = -360f; int axisChoice = 0;   // 0 = Auto (per wheel), 1..3 = X/Y/Z
    int minVerts = 50;            // parts below this are COLLAPSED into Body (a triangle-soup FBX probes into thousands of shards)
    Vector2 partsScroll;
    static readonly string[] AxisOptions = { "Auto (thinnest extent = axle, per wheel)", "X", "Y", "Z" };
    string status = "";
    string lastOutGlb = "";

    // turntable preview state
    GameObject inst; PreviewRenderUtility pru; AnimationClip spinClip;
    Bounds bounds; bool boundsValid; float spinT; double lastTick;
    Vector2 orbit = new Vector2(140f, -18f); float zoom = 1.5f;

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
                if (!string.IsNullOrEmpty(p)) { srcFile = p; parts.Clear(); status = ""; }
            }
        }

        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(srcFile) || !File.Exists(srcFile)))
            if (GUILayout.Button(new GUIContent("Probe parts", "Headless Blender lists the model's mesh parts (a single combined mesh is split into loose parts). Roles are auto-guessed from names."), GUILayout.Height(24)))
                Probe();

        if (parts.Count > 0)
        {
            // Tiny-fragment collapse: a triangle-soup FBX probes into THOUSANDS of 3-4-vert shards — they all belong
            // to Body anyway (anything not marked wheel/turret skins to Root). Only substantial parts are listed.
            minVerts = EditorGUILayout.IntSlider(new GUIContent("Hide parts under (verts)",
                "Parts smaller than this are collapsed into Body automatically (they skin to Root). Raise it if the list is still noisy; lower it if a small wheel is missing."), minVerts, 1, 2000);
            var shown = parts.Where(x => x.verts >= minVerts).ToList();
            int hidden = parts.Count - shown.Count;
            EditorGUILayout.LabelField($"Parts ({shown.Count} shown{(hidden > 0 ? $", {hidden} tiny fragments auto-collapsed into Body" : "")}) — mark the wheels & turret:", EditorStyles.boldLabel);
            partsScroll = EditorGUILayout.BeginScrollView(partsScroll, GUILayout.ExpandHeight(true), GUILayout.MinHeight(280));   // roomy by default (user request: double the original 120)
            foreach (var p in shown)
                using (new EditorGUILayout.HorizontalScope())
                {
                    p.role = (Role)EditorGUILayout.EnumPopup(p.role, GUILayout.Width(70));
                    EditorGUILayout.LabelField($"{p.name}   ({p.verts} verts, size {p.size.x:0.00}×{p.size.y:0.00}×{p.size.z:0.00})", EditorStyles.miniLabel);
                }
            EditorGUILayout.EndScrollView();
            axisChoice = EditorGUILayout.Popup(new GUIContent("Axle axis", "Auto infers each wheel's axle as its thinnest bbox extent — right for normal wheels; override only if a wheel spins the wrong way around."), axisChoice, AxisOptions);
            frames = EditorGUILayout.IntSlider(new GUIContent("Spin frames", "Length of the generated Spin action. Apparent speed is tuned later with slice steps (Spin[1..N/2]) — this just needs to be a smooth loop."), frames, 5, 60);
            degrees = EditorGUILayout.Slider(new GUIContent("Spin degrees", "Wheel rotation over the clip. -360 = one full forward turn (negate if wheels roll backward in the preview)."), degrees, -720f, 720f);

            int wheels = parts.Count(x => x.role == Role.Wheel);
            using (new EditorGUI.DisabledScope(wheels == 0))
                if (GUILayout.Button(new GUIContent($"Vehicleize  →  {Path.GetFileNameWithoutExtension(srcFile)}_Spin.glb", wheels == 0 ? "Mark at least one part as Wheel." : "Runs Blender: rig + Spin action + GLB export + preview."), GUILayout.Height(28)))
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
        parts.Clear(); DestroyPreview();
        if (!RunBlender($"probe \"{srcFile}\"", out string stdout)) return;
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
            p.role = low.Contains("wheel") || low.Contains("tyre") || low.Contains("tire") ? Role.Wheel
                   : low.Contains("turret") ? Role.Turret : Role.Body;
            parts.Add(p);
        }
        status = parts.Count == 0
            ? "Probe found no mesh parts — is this a mesh model? (See the Console for Blender output.)"
            : $"Probed {parts.Count} part(s); {parts.Count(x => x.role == Role.Wheel)} auto-marked as wheels. Adjust roles and Vehicleize.";
    }

    void Vehicleize()
    {
        DestroyPreview();
        string dir = Path.GetDirectoryName(srcFile);
        string baseName = Path.GetFileNameWithoutExtension(srcFile);
        lastOutGlb = Path.Combine(dir, baseName + "_Spin.glb").Replace('\\', '/');
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
        string done = stdout.Split('\n').FirstOrDefault(l => l.Contains("VEHICLE RIG DONE"));
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
            if (!UniversalBaker.RunBounded(p, 180000, out stdout, out string _)) { status = "Blender timed out (3 min)."; return false; }
            if (stdout.Contains("VEHICLE ERROR"))
            { status = stdout.Split('\n').FirstOrDefault(l => l.Contains("VEHICLE ERROR")) ?? "Blender step failed."; Debug.LogError("[VehicleLab]\n" + stdout); return false; }
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
    }
    void HandlePreviewInput(Rect rect)
    {
        var e = Event.current;
        if (!rect.Contains(e.mousePosition)) return;
        if (e.type == EventType.ScrollWheel) { zoom = Mathf.Clamp(zoom * Mathf.Pow(1.12f, e.delta.y > 0 ? 1f : -1f), 0.2f, 5f); e.Use(); }
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
        }
        if (!boundsValid) return;
        pru.BeginPreview(rect, GUIStyle.none);
        var cam = pru.camera;
        float radius = Mathf.Max(bounds.extents.magnitude, 0.1f);
        float dist = radius * 2f * zoom;
        var rot = Quaternion.Euler(-orbit.y, orbit.x, 0f);
        cam.transform.position = bounds.center + rot * (Vector3.back * dist);
        cam.transform.rotation = Quaternion.LookRotation(bounds.center - cam.transform.position);
        cam.nearClipPlane = 0.01f; cam.farClipPlane = dist + radius * 4f; cam.fieldOfView = 30f;
        pru.lights[0].intensity = 1.3f;
        pru.lights[0].transform.rotation = Quaternion.Euler(45f, 45f, 0f);
        if (pru.lights.Length > 1) pru.lights[1].intensity = 0.6f;
        pru.ambientColor = new Color(0.3f, 0.3f, 0.3f);
        cam.Render();
        GUI.DrawTexture(rect, pru.EndPreview(), ScaleMode.StretchToFill, false);
    }
}
