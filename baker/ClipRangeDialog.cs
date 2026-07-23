// ClipRangeDialog.cs — the CLIP RANGE PICKER (2026-07-19, user-designed). Opened from any clip field's ▶ button:
// shows a playable/scrubbable 3D preview of the model's clips (via an "inspection FBX" — a pure Blender format
// conversion carrying ALL clips, no rig surgery) with Start/End frame fields and a Speed /N step (every Nth source
// frame = N× faster; pacing is bake-only — Law 3 — so this is where walk speed is authored, and Play previews at
// that speed); Confirm writes `clip[start..end/N]` (or the plain clip name for the full range at /1) back into the field. This is how a modder finds segment boundaries
// inside a long multi-motion clip (e.g. the M114's deploy 0..180 + recoil 180..250) without opening Blender.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class ClipRangeDialog : EditorWindow
{
    const float FPS = 24f;   // Blender-standard export rate; exact on every model so far (deploy 250f=10.417s, Idle1 341f=14.208s)

    string modelFile, resourceName, fbxDir, srcKey;
    Action<string> onConfirm;
    AnimationClip[] clips = new AnimationClip[0];
    string[] clipNames = new string[0];    // EXACT action names (the "HAFCLIP|" take prefix stripped) — what Confirm writes
    string[] clipPaths = new string[0];    // which inspection FBX carries each clip (one action per file)
    string[] clipLabels = new string[0];
    int clipIdx;
    string instPath;                       // the FBX the current instance came from
    float frame; int startF, endF; int stepN = 1;
    bool playing; double lastTick;

    // The instance is added to the PreviewRenderUtility's OWN SCENE (AddSingleGO) and rendered by its camera like
    // any live object — Unity's real skinning path, real transforms, real materials. NO hand-rolled BakeMesh/DrawMesh:
    // two attempts at reproducing the renderer's scale handling by hand each corrupted the view (un-mirrored legs,
    // then giant parts) while the underlying animation data was provably correct. Nothing sits between data and eyes.
    GameObject inst;
    PreviewRenderUtility pru;
    Vector2 orbit = new Vector2(150f, -15f);
    float zoom = 1.4f;
    Bounds bounds; bool boundsValid;

    public static void Open(string modelFile, string resourceName, string currentSpec, Action<string> onConfirm)
    {
        var w = GetWindow<ClipRangeDialog>(true, "Clip range picker", true);
        w.minSize = new Vector2(560, 560);
        w.modelFile = modelFile ?? "";
        w.resourceName = (resourceName ?? "").Trim();
        w.onConfirm = onConfirm;
        w.Prepare(currentSpec ?? "");
    }

    void Prepare(string currentSpec)
    {
        // parse a pre-existing "name[a..b]" / "name[a..b/N]" spec so the dialog reopens where the field points
        string wantClip = currentSpec; int wantS = -1, wantE = -1; stepN = 1;
        var m = System.Text.RegularExpressions.Regex.Match(currentSpec, @"^(.*)\[(\d+)\.\.(\d+)(?:/(\d+))?\]$");
        if (m.Success)
        {
            wantClip = m.Groups[1].Value; wantS = int.Parse(m.Groups[2].Value); wantE = int.Parse(m.Groups[3].Value);
            if (m.Groups[4].Success) stepN = Mathf.Max(1, int.Parse(m.Groups[4].Value));
        }

        if (string.IsNullOrEmpty(resourceName) || string.IsNullOrEmpty(modelFile) || !System.IO.File.Exists(modelFile))
        { ShowNotification(new GUIContent("Needs a loaded entry with an existing model file.")); return; }
        fbxDir = "Assets/FactorySource/" + resourceName + "/inspect";
        string proj = System.IO.Directory.GetParent(Application.dataPath).FullName;
        string dirFull = System.IO.Path.Combine(proj, fbxDir);
        var existing = System.IO.Directory.Exists(dirFull) ? System.IO.Directory.GetFiles(dirFull, "*.fbx") : new string[0];
        string srcTag = System.IO.Path.Combine(dirFull, "source.txt");   // which model file + converter version these
        // FBXs came from — a timestamp check alone silently reuses the previous model's clips when the entry is
        // repointed at an OLDER file, and a converter fix would be silently bypassed (the frozen raw-preview bug).
        string toolPath = System.IO.Path.Combine(proj, "Tools", "inspect_fbx.py");
        srcKey = modelFile + "|" + (System.IO.File.Exists(toolPath) ? System.IO.File.GetLastWriteTimeUtc(toolPath).Ticks.ToString() : "0");
        bool stale = existing.Length == 0
                     || !System.IO.File.Exists(System.IO.Path.Combine(dirFull, "manifest.txt"))   // pre-manifest converter output (mirrored anim / unreliable take names) — force re-convert
                     || !System.IO.File.Exists(srcTag) || System.IO.File.ReadAllText(srcTag).Trim() != srcKey
                     || System.IO.File.GetLastWriteTimeUtc(modelFile) > existing.Max(f => System.IO.File.GetLastWriteTimeUtc(f));
        if (stale && !BuildInspectFbx(proj, dirFull)) return;

        // exact action names come from the manifest (FBX take names are unreliable: slot quirks yielded "Scene",
        // and safe filenames can't round-trip characters like the '|' in Sketchfab action names)
        var nameByFile = new Dictionary<string, string>();
        string manifestPath = System.IO.Path.Combine(dirFull, "manifest.txt");
        if (System.IO.File.Exists(manifestPath))
            foreach (var line in System.IO.File.ReadAllLines(manifestPath))
            { var t = line.Split('\t'); if (t.Length == 2) nameByFile[t[0]] = t[1]; }
        var clipL = new List<AnimationClip>(); var nameL = new List<string>(); var pathL = new List<string>();
        foreach (var full in System.IO.Directory.GetFiles(dirFull, "*.fbx").OrderBy(f => f))
        {
            string fileName = System.IO.Path.GetFileName(full);
            string rel = fbxDir + "/" + fileName;
            var imp = AssetImporter.GetAtPath(rel) as ModelImporter;
            if (imp != null && (imp.animationType != ModelImporterAnimationType.Generic || !imp.importAnimation))
            { imp.animationType = ModelImporterAnimationType.Generic; imp.importAnimation = true; imp.SaveAndReimport(); }
            foreach (var c in AssetDatabase.LoadAllAssetsAtPath(rel).OfType<AnimationClip>())
            {
                if (c.name.StartsWith("__preview")) continue;
                string nm = nameByFile.TryGetValue(fileName, out var exact) ? exact
                          : System.IO.Path.GetFileNameWithoutExtension(fileName);
                clipL.Add(c); nameL.Add(nm); pathL.Add(rel);
                break;   // one action per inspection FBX — ignore any extra takes
            }
        }
        clips = clipL.ToArray(); clipNames = nameL.ToArray(); clipPaths = pathL.ToArray();
        clipLabels = clips.Select((c, i) => $"{clipNames[i]}   (frames 0..{Mathf.RoundToInt(c.length * FPS)}, {c.length:0.0}s)").ToArray();
        if (clips.Length == 0) { ShowNotification(new GUIContent("No animation clips in the inspection FBXs.")); return; }
        clipIdx = Mathf.Max(0, Array.IndexOf(clipNames, wantClip));
        int total = TotalFrames;
        startF = wantS >= 0 ? Mathf.Clamp(wantS, 0, total) : 0;
        endF = wantE >= 0 ? Mathf.Clamp(wantE, 0, total) : total;
        frame = startF;
        DestroyInstance();
        Repaint();
    }

    bool BuildInspectFbx(string proj, string dirFull)
    {
        try
        {
            EditorUtility.DisplayProgressBar("Clip range picker", "Converting the model's clips to inspection FBXs (Blender)…", 0.4f);
            if (System.IO.Directory.Exists(dirFull))                                    // clear stale per-clip files (removed clips)
                foreach (var f in System.IO.Directory.GetFiles(dirFull, "*.fbx")) System.IO.File.Delete(f);
            var p = new System.Diagnostics.Process();
            p.StartInfo.FileName = UniversalBaker.FindBlender();
            p.StartInfo.Arguments = $"--background --python \"{System.IO.Path.Combine(proj, "Tools", "inspect_fbx.py")}\" -- \"{modelFile}\" \"{dirFull}\"";
            p.StartInfo.UseShellExecute = false; p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true; p.StartInfo.RedirectStandardError = true;
            p.Start();
            // concurrent drain (shared with the baker): sequential ReadToEnd(stdout)+ReadToEnd(stderr) deadlocks when
            // Blender fills the stderr pipe buffer while we block on stdout — Unity hangs until the process is killed.
            if (!UniversalBaker.RunBounded(p, 180000, out string so, out string _))
            { Debug.LogError("[ClipRange] Blender inspect conversion timed out."); return false; }
            if (!System.IO.Directory.Exists(dirFull) || System.IO.Directory.GetFiles(dirFull, "*.fbx").Length == 0)
            { Debug.LogError("[ClipRange] no inspection FBXs produced:\n" + so); return false; }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dirFull, "source.txt"), srcKey);
            AssetDatabase.Refresh();
            return true;
        }
        catch (Exception e) { Debug.LogError("[ClipRange] " + e.Message); return false; }
        finally { EditorUtility.ClearProgressBar(); }
    }

    int TotalFrames => clips.Length == 0 ? 0 : Mathf.RoundToInt(clips[Mathf.Clamp(clipIdx, 0, clips.Length - 1)].length * FPS);

    void EnsureInstance()
    {
        string want = clips.Length > 0 ? clipPaths[Mathf.Clamp(clipIdx, 0, clipPaths.Length - 1)] : null;
        if (inst != null && instPath == want) return;
        DestroyInstance();
        if (want == null) return;
        instPath = want;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(want);
        if (prefab == null) return;
        if (pru == null) pru = new PreviewRenderUtility();
        inst = Instantiate(prefab);
        pru.AddSingleGO(inst);   // lives in the preview scene; its own renderers draw it — no manual mesh baking
        boundsValid = false;
    }

    void DestroyInstance()
    {
        if (inst != null) { DestroyImmediate(inst); inst = null; }
        boundsValid = false;
    }

    void OnEnable() { EditorApplication.update += Tick; lastTick = EditorApplication.timeSinceStartup; }
    void OnDisable()
    {
        EditorApplication.update -= Tick;
        DestroyInstance();
        if (pru != null) { try { pru.Cleanup(); } catch { } pru = null; }
    }

    void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        if (playing && clips.Length > 0)
        {
            // Speed step: /N keeps every Nth source frame at bake, so in-game the slice plays N× faster — the
            // preview advances N× over the SOURCE frames, which is the same pace the baked clip will have.
            frame += (float)((now - lastTick) * FPS * Mathf.Max(1, stepN));
            // Play LOOPS WITHIN the selected Start..End range (not the whole clip) so it previews exactly the slice
            // being captured. Reversed ranges (start>end) just loop the same span forward; a held stance (start==end)
            // parks on that frame. Falls back to the full clip only when no range is set.
            int total = Mathf.Max(1, TotalFrames);
            int lo = Mathf.Clamp(Mathf.Min(startF, endF), 0, total);
            int hi = Mathf.Clamp(Mathf.Max(startF, endF), 0, total);
            if (hi <= lo) { frame = lo; }                       // held stance — sit on the frame
            else { if (frame > hi) frame = lo + (frame - hi) % (hi - lo); if (frame < lo) frame = lo; }
            Repaint();
        }
        lastTick = now;
    }

    void SamplePose()
    {
        if (clips.Length == 0) return;
        EnsureInstance();
        if (inst == null) return;
        var clip = clips[Mathf.Clamp(clipIdx, 0, clips.Length - 1)];
        clip.SampleAnimation(inst, Mathf.Clamp(frame, 0, TotalFrames) / FPS);
        if (!boundsValid)
        {
            bool first = true;
            foreach (var r in inst.GetComponentsInChildren<Renderer>())
            {
                if (r == null) continue;
                if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds);
            }
            boundsValid = !first;
        }
    }

    void OnGUI()
    {
        if (clips.Length == 0)
        {
            EditorGUILayout.HelpBox("No clips loaded — open this dialog from a clip field's ▶ button on an entry with a model file.", MessageType.Info);
            return;
        }
        int total = TotalFrames;

        int newIdx = EditorGUILayout.Popup("Clip", clipIdx, clipLabels);
        if (newIdx != clipIdx) { clipIdx = newIdx; total = TotalFrames; frame = 0; startF = 0; endF = total; boundsValid = false; }

        // keyboard single-frame stepping (←/→, with Shift = ±10) — the analyst's scrub
        var ev = Event.current;
        if (ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.LeftArrow || ev.keyCode == KeyCode.RightArrow))
        {
            playing = false;
            int stride = ev.shift ? 10 : 1;
            frame = Mathf.Clamp(Mathf.Round(frame) + (ev.keyCode == KeyCode.RightArrow ? stride : -stride), 0, total);
            ev.Use(); Repaint();
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            startF = EditorGUILayout.IntField("Start", startF, GUILayout.Width(220));
            if (GUILayout.Button(new GUIContent("◄ set current", "Copy the preview's current frame into this field"), GUILayout.Width(95))) startF = Mathf.RoundToInt(frame);
            if (GUILayout.Button(new GUIContent("go ►", "Jump the preview TO this frame (the reverse of 'set current')"), GUILayout.Width(45)))
            { playing = false; frame = Mathf.Clamp(startF, 0, total); GUI.FocusControl(null); Repaint(); }
            GUILayout.Space(12);
            endF = EditorGUILayout.IntField("End", endF, GUILayout.Width(220));
            if (GUILayout.Button(new GUIContent("◄ set current", "Copy the preview's current frame into this field"), GUILayout.Width(95))) endF = Mathf.RoundToInt(frame);
            if (GUILayout.Button(new GUIContent("go ►", "Jump the preview TO this frame (the reverse of 'set current')"), GUILayout.Width(45)))
            { playing = false; frame = Mathf.Clamp(endF, 0, total); GUI.FocusControl(null); Repaint(); }
            GUILayout.Space(12);
            GUILayout.Label(new GUIContent("Speed /", "Frame-skip step baked into the slice: /2 keeps every 2nd source frame, so the slice plays 2× faster in-game (pacing is bake-only — there is no runtime speed knob). ► Play previews at this speed."), GUILayout.Width(50));
            stepN = Mathf.Max(1, EditorGUILayout.IntField(stepN, GUILayout.Width(28)));
            GUILayout.Label(stepN > 1 ? $"= {stepN}× faster" : "(1 = authored pace)", GUILayout.Width(95));
        }

        // transport row BELOW Start/End (user call): the controls sit adjacent to the preview they drive
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(playing ? "❚❚ Pause" : "► Play", GUILayout.Width(80)))
            {
                playing = !playing;
                // start playback at the range's beginning if the playhead is outside the Start..End slice
                if (playing) { int lo = Mathf.Min(startF, endF), hi = Mathf.Max(startF, endF); if (frame < lo || frame > hi) frame = lo; }
            }
            if (GUILayout.Button(new GUIContent("|◄", "One frame back (also ← key; Shift+← = 10 back)"), GUILayout.Width(30)))
            { playing = false; frame = Mathf.Max(0, Mathf.Round(frame) - 1); GUI.FocusControl(null); Repaint(); }
            if (GUILayout.Button(new GUIContent("►|", "One frame forward (also → key; Shift+→ = 10 forward)"), GUILayout.Width(30)))
            { playing = false; frame = Mathf.Min(total, Mathf.Round(frame) + 1); GUI.FocusControl(null); Repaint(); }
            EditorGUI.BeginChangeCheck();
            frame = GUILayout.HorizontalSlider(frame, 0, total);
            if (EditorGUI.EndChangeCheck()) playing = false;   // scrubbing pauses
            GUILayout.Label($"frame {Mathf.RoundToInt(frame)} / {total}", GUILayout.Width(110));
        }
        startF = Mathf.Clamp(startF, 0, total);
        endF = Mathf.Clamp(endF, 0, total);
        EditorGUILayout.HelpBox(
            "Play or scrub to find where a motion begins/ends, capture the numbers with 'set current'. " +
            "Start > End plays the slice REVERSED (a fold from an unfold); Start = End is a held stance. " +
            "Speed /N bakes every Nth frame = N× faster (Play previews it). Confirm writes the slice into the clip field.", MessageType.None);

        // preview (own camera: drag orbits, scroll zooms — the wheel is consumed here)
        var rect = GUILayoutUtility.GetRect(200, 330, GUILayout.ExpandWidth(true));
        HandlePreviewInput(rect);
        if (Event.current.type == EventType.Repaint) RenderPreview(rect);

        using (new EditorGUILayout.HorizontalScope())
        {
            string clipName = clipNames[clipIdx];
            string spec = (startF == 0 && endF == total && stepN <= 1) ? clipName
                        : $"{clipName}[{startF}..{endF}{(stepN > 1 ? "/" + stepN : "")}]";
            if (GUILayout.Button("Confirm:   " + spec, GUILayout.Height(30)))
            { onConfirm?.Invoke(spec); Close(); }
            if (GUILayout.Button("Cancel", GUILayout.Height(30), GUILayout.Width(90))) Close();
        }
    }

    void HandlePreviewInput(Rect rect)
    {
        var e = Event.current;
        if (!rect.Contains(e.mousePosition)) return;
        if (e.type == EventType.ScrollWheel)
        { zoom = Mathf.Clamp(zoom * Mathf.Pow(1.12f, e.delta.y > 0 ? 1f : -1f), 0.2f, 5f); e.Use(); Repaint(); }
        else if (e.type == EventType.MouseDrag && e.button == 0)
        { orbit += new Vector2(e.delta.x, -e.delta.y) * 0.7f; orbit.y = Mathf.Clamp(orbit.y, -89f, 89f); e.Use(); Repaint(); }
    }

    void RenderPreview(Rect rect)
    {
        SamplePose();
        if (!boundsValid || pru == null) return;
        pru.BeginPreview(rect, GUIStyle.none);
        var cam = pru.camera;
        float radius = Mathf.Max(bounds.extents.magnitude, 0.1f);
        float dist = radius * 2.0f * zoom;
        var rot = Quaternion.Euler(-orbit.y, orbit.x, 0f);
        cam.transform.position = bounds.center + rot * (Vector3.back * dist);
        cam.transform.rotation = Quaternion.LookRotation(bounds.center - cam.transform.position);
        cam.nearClipPlane = 0.01f; cam.farClipPlane = dist + radius * 4f; cam.fieldOfView = 30f;
        pru.lights[0].intensity = 1.3f;
        pru.lights[0].transform.rotation = Quaternion.Euler(45f, 45f, 0f);
        if (pru.lights.Length > 1) pru.lights[1].intensity = 0.6f;
        pru.ambientColor = new Color(0.3f, 0.3f, 0.3f);
        cam.Render();   // the instance lives in the preview scene (AddSingleGO) — its own renderers draw it
        GUI.DrawTexture(rect, pru.EndPreview(), ScaleMode.StretchToFill, false);
    }
}
