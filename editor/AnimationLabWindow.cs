// AnimationLabWindow.cs (ENC editor) — Tools > ENC > Animation Lab.
// The dedicated dialog for a model's ANIMATION: which clip plays and how (fire-on-attack, deploy-on-stop, recoil —
// and, next, the state-driven idle/movement/after-movement set). Mutually exclusive with the Model Factory
// and working together with it: the Factory owns the MODEL (identity, file, transform, size, geometry/shading) and
// shows a read-only animation summary with a jump button here; this Lab owns the animation settings and shows the
// model identity read-only. Both bake through the same pipeline (ConfigFor -> UniversalBaker.BuildAnimated ->
// ModelRegistry.Upsert), so it does not matter where Bake is pressed — the settings are just EDITED in one place each.
// Docks as a tab next to the Factory so the pair presents as one tabbed dialog.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class AnimationLabWindow : EditorWindow
{
    // [SerializeField] so the form survives a DOMAIN RELOAD (recompile / play-mode toggle), matching the other Labs.
    [SerializeField] ModelDef cur = new ModelDef { animated = true };
    [SerializeField] int selected;                 // 0 = <New>, else index into `existing`
    // Foldout state for the gun-elevation timing block. [SerializeField] so a domain reload (any recompile)
    // does not spring it back open — the same reason every other bit of window state here is serialized.
    [SerializeField] bool elevTimingOpen;
    [SerializeField] Vector2 scroll;
    string[] existing = { "<New>" };
    string status = "";
    List<string> animClips = new List<string>();   // clip names read from the model (Clip picker)
    List<KeyValuePair<string, int>> animBonePrefixes = new List<KeyValuePair<string, int>>();  // bone-name prefix -> count (Bones picker)
    List<string> animBoneNames = new List<string>();   // FULL bone names (per-bone precision — the Donor-sockets dialog)
    string clipProbeFile = "\0";                    // sentinel != any real path so the first real path always inspects
    // FIT PREVIEW (model + hand prop combined) — custom PreviewRenderUtility renderer: Unity's built-in prefab
    // preview has no zoom and the window's scroll view steals the wheel; owning the camera gives real orbit + zoom.
    PreviewRenderUtility fitPRU;                                    // non-serializable; lazily created, cleaned in OnDisable
    List<(Mesh mesh, Material[] mats, Matrix4x4 mtx)> fitDraws;     // flattened draw list from the combined prefab
    Bounds fitBounds;
    [SerializeField] string previewPath = "";       // the combined prefab shown (survives domain reloads)
    [SerializeField] Vector2 fitOrbit = new Vector2(150f, -15f);    // yaw/pitch (deg)
    [SerializeField] float fitZoom = 1.4f;                          // camera distance factor (scroll wheel)
    [SerializeField] Vector2 fitPan;                                // camera-plane pan (middle/right-drag), in dist units
    [SerializeField] Vector3 fitAngles;             // LIVE prop rotation: applied to the previewed prop instantly (no bake) and saved to the registry (the plugin stamps the same value in-game)
    static Material fitFallbackMat;
    // GROUND REFERENCE (same square as the District/Model Factory panes): a tile-sized square at the ORIGIN plane.
    // Drawn only for the FBX rest-pose route (fitGrounded) — the rig is rest-normalized with the ground at the origin,
    // so the square is the in-game ground truth there; the static-prefab fallback is display-flipped and would lie.
    [SerializeField] bool fitGrounded;
    [SerializeField] bool fitRefMan;   // "Ref man" size reference (2026-08-19) — shared builder/constant with the Factory
    [SerializeField] Vector2 fitRefManPos = new Vector2(1.5f, 0f);   // his spot (X sideways, Z fore/aft) — user-dialed
    Mesh fitRefManMesh;
    Material fitGroundMat;
    Mesh fitGroundMesh;
    Mesh fitArrowMesh;   // flat FORWARD (+Z) arrow on the square — the direction to dial the model toward
    // PLAY THE CLIP (2026-08-22, the towed howitzer's rolling wheels): the fit preview above draws the rig's BIND
    // POSE (DrawMesh of sharedMesh — no skinning), so a clip can never move in it; and the raw-model ▶ picker shows
    // the source UNTEXTURED, where a dark wheel against a dark hull reads as motionless (user report). Playing a clip
    // needs a REAL instance in the preview scene (AddSingleGO) whose SkinnedMeshRenderers skin as the clip is
    // sampled — the Vehicle Lab turntable's proven route, ported here. The clip comes from the BAKED per-role FBX
    // (anim/ idle, anim_move/, anim_after/, anim_premove/, anim_attack/) and wears the model's baked atlas, so what
    // plays here is what the game will play.
    GameObject fitAnimInst;          // live instance inside fitPRU's scene (non-serializable: rebuilt on demand)
    AnimationClip fitAnimClip;
    Bounds fitAnimBounds; bool fitAnimBoundsValid;
    [SerializeField] string fitAnimRole = "";        // "" = rest pose (the static draw list); else the role FBX subfolder
    [SerializeField] bool fitAnimPlaying = true;
    [SerializeField] float fitAnimSpeed = 1f;
    float fitAnimT; double fitAnimTick;
    List<(string label, string dir)> fitRoles; string fitRolesFor;   // role clips that actually exist, cached per resource
    static readonly (string label, string dir)[] FitAnimRoleDirs = {
        ("Idle / main clip", "anim"), ("Movement", "anim_move"), ("After-move", "anim_after"),
        ("Pre-move (fold)", "anim_premove"), ("Attack", "anim_attack"),
    };

    [MenuItem("Tools/HAF/Animation Lab")]
    static void Open()
    {
        // Dock as a TAB next to the Model Factory (desiredDockNextTo) so the two authoring tools present as
        // one tabbed dialog. Falls back to a floating window when the Factory isn't open.
        var w = GetWindow<AnimationLabWindow>("Animation Lab", false, typeof(ModelFactoryWindow));
        w.minSize = new Vector2(480, 420);
        w.RefreshList();
    }

    [SerializeField] bool formDiffersFromRegistry;   // set on domain reload when the surviving form ≠ the saved entry

    void OnEnable()
    {
        RefreshList();
        clipProbeFile = "\0";   // a domain reload wipes the cached clip/bone lists; reset the probe sentinel so the NEXT OnGUI re-reads the model ONCE (populates the Clip/Turret/Bone Pick without a manual Reload). ONE probe, no per-frame thrash.
        // DOMAIN-RELOAD RECONCILIATION (v2 — the v1 auto-resync silently DISCARDED unsaved form edits on every
        // compile, destroying user work mid-authoring; the opposite policy silently kept a stale form that clobbered
        // registry edits at the next Save. Neither silence is acceptable.) The form's serialized state SURVIVES the
        // reload untouched; if it differs from the saved registry entry, a warning banner appears with an explicit
        // choice: ↻ Reload (take the registry) or Save (keep the form). The user decides — never the reload.
        formDiffersFromRegistry = false;
        if (selected > 0 && !string.IsNullOrEmpty((cur.resourceName ?? "").Trim()))
        {
            var reg = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == cur.resourceName.Trim());
            // Compare apples-to-apples: OnGUI materializes cur's empty recoil knobs to their defaults, so a raw reg
            // (empty strings on entries never re-saved since these fields existed) must be materialized too — else
            // the banner fires spuriously after every compile with no user edit, and users learn to ignore it.
            if (reg != null) MaterializeRecoilDefaults(reg);
            if (reg != null && JsonUtility.ToJson(reg) != JsonUtility.ToJson(cur))
                formDiffersFromRegistry = true;
        }
        // Domain-reload restore of the fit preview — WITH the atlas-UV substitute (2026-08-19 second drill
        // finding: the substitution landed in the rebuild path only, and THIS path is the one that runs right
        // after a compile — the one-forgotten-call-site pattern again). The owning resource is the FactorySource
        // subfolder in the persisted path; a model with no _PreviewMesh (single-material) just passes null.
        if (!string.IsNullOrEmpty(previewPath))
        {
            var resDir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(previewPath));
            LoadFitPreview(previewPath, UniversalBaker.LoadPreviewSubstitutes(resDir));   // the SHARED loader — multi-SMR set
        }
    }
    void OnDisable() { DestroyFitPreview(); }

    // The recoil knobs' explicit defaults, in ONE place (user rule: no empty fields). Applied to the form so
    // what's shown is what bakes/saves, AND to a loaded entry before the form-vs-registry diff so a not-yet-
    // re-saved entry's empty strings don't read as a spurious edit.
    static void MaterializeRecoilDefaults(ModelDef d)
    {
        if (string.IsNullOrWhiteSpace(d.deployRecoilStep)) d.deployRecoilStep = "0";
        if (string.IsNullOrWhiteSpace(d.deployRecoilReturn)) d.deployRecoilReturn = "4";
        if (string.IsNullOrWhiteSpace(d.deploySlamDeg)) d.deploySlamDeg = "0";
        if (string.IsNullOrWhiteSpace(d.deploySlamSettle)) d.deploySlamSettle = "1";
    }

    // ---- PLAY THE CLIP: the role clips this model actually has (cached; a Bake or an entry switch re-probes) ----
    List<(string label, string dir)> FitRoles()
    {
        string res = (cur?.resourceName ?? "").Trim();
        if (fitRoles != null && fitRolesFor == res) return fitRoles;
        fitRolesFor = res; fitRoles = new List<(string, string)>();
        if (res.Length > 0)
            foreach (var (label, dir) in FitAnimRoleDirs)
                if (AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/FactorySource/{res}/{dir}/{res}_anim.fbx") != null)
                    fitRoles.Add((label, dir));
        return fitRoles;
    }

    // Instantiate the role's baked FBX into the preview scene and hold its clip. dir null/"" = back to the rest pose.
    void BuildAnimPreview(string dir)
    {
        DestroyAnimPreview();
        fitAnimRole = dir ?? "";
        if (fitAnimRole.Length == 0) { Repaint(); return; }
        string res = (cur?.resourceName ?? "").Trim();
        string path = $"Assets/FactorySource/{res}/{fitAnimRole}/{res}_anim.fbx";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
        {   // the role isn't baked (yet) — say so once and fall back instead of retrying every repaint
            status = $"Play clip: nothing baked at {path} — Bake this entry first.";
            fitAnimRole = ""; Repaint(); return;
        }
        fitAnimClip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                                   .FirstOrDefault(c => c != null && !c.name.StartsWith("__preview"));
        if (fitPRU == null) fitPRU = new PreviewRenderUtility();
        fitAnimInst = Instantiate(prefab);
        fitAnimInst.hideFlags = HideFlags.HideAndDontSave;
        // Wear the SAME skin as the rest-pose preview: the model's baked atlas material, plus the atlas-UV
        // substitute meshes of a multi-material bake (matched by vertex count, like LoadFitPreview). A substitute
        // is only accepted when it carries the SAME skinning data — a UV clone without bind poses would render
        // the model as an unskinned heap, and a wrong-UV wheel still shows its rotation, which is the point here.
        var bodyMat = AssetDatabase.LoadAssetAtPath<Material>($"Assets/FactorySource/{res}/{res}_PreviewMat.mat");
        var pool = UniversalBaker.LoadPreviewSubstitutes(res)?.Where(m => m != null).ToList();
        int subUsed = 0, subSkipped = 0;
        foreach (var r in fitAnimInst.GetComponentsInChildren<Renderer>(true))
        {
            if (bodyMat != null)
            {
                var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < mats.Length; i++) mats[i] = bodyMat;
                r.sharedMaterials = mats;
            }
            if (pool == null || !(r is SkinnedMeshRenderer smr) || smr.sharedMesh == null) continue;
            int hit = pool.FindIndex(s => s.vertexCount == smr.sharedMesh.vertexCount);
            if (hit < 0) continue;
            var sub = pool[hit]; pool.RemoveAt(hit);
            if (sub.bindposes.Length == smr.sharedMesh.bindposes.Length && sub.boneWeights.Length == sub.vertexCount)
            { smr.sharedMesh = sub; subUsed++; }
            else subSkipped++;
        }
        fitPRU.AddSingleGO(fitAnimInst);
        fitAnimT = 0f; fitAnimTick = EditorApplication.timeSinceStartup; fitAnimBoundsValid = false;
        status = fitAnimClip != null
            ? $"Playing '{fitAnimClip.name}' ({fitAnimClip.length:0.00}s) from {fitAnimRole}/ — the BAKED clip, as the game plays it."
              + (subSkipped > 0 ? $"  ({subSkipped} atlas-UV clone(s) skipped — unskinned; texture may be off, motion is exact.)" : "")
            : $"{fitAnimRole}/ has no animation clip — showing its rest pose.";
        Repaint();
    }

    void DestroyAnimPreview()
    {
        if (fitAnimInst != null) { DestroyImmediate(fitAnimInst); fitAnimInst = null; }
        fitAnimClip = null; fitAnimBoundsValid = false;
    }

    void DestroyFitPreview()
    {
        DestroyAnimPreview();   // before Cleanup: the instance lives in the PRU's scene
        fitDraws = null;
        if (fitGroundMat != null) { DestroyImmediate(fitGroundMat); fitGroundMat = null; }
        if (fitGroundMesh != null) { DestroyImmediate(fitGroundMesh); fitGroundMesh = null; }
        if (fitArrowMesh != null) { DestroyImmediate(fitArrowMesh); fitArrowMesh = null; }
        if (fitPRU != null) { try { fitPRU.Cleanup(); } catch { } fitPRU = null; }
    }

    // Any BAKE (this Lab, the Factory, the Prop Lab) deletes+recreates assets the combined prefab references —
    // the stale fit preview then renders the soldier MAGENTA (dead material refs). So every bake retires the
    // combined view (back to the normal per-window previews); pressing 'Refresh fit preview' rebuilds it from
    // the fresh assets. previewPath is kept so Refresh knows what to rebuild.
    internal static void InvalidateFitPreviews()
    {
        foreach (var w in Resources.FindObjectsOfTypeAll<AnimationLabWindow>())
        { w.fitDraws = null; w.DestroyAnimPreview(); w.fitRoles = null; w.Repaint(); }   // the FBX is being reimported — its instance and clip die with it
    }

    // ...and after a bake COMPLETES, rebuild them from the fresh assets (a preview built post-bake can't be stale).
    // Only windows that were showing one (previewPath set) and still have a hand prop configured come back.
    internal static void RebuildFitPreviews()
    {
        foreach (var w in Resources.FindObjectsOfTypeAll<AnimationLabWindow>())
            if (!string.IsNullOrEmpty(w.previewPath))
            {
                w.BuildFitPreview();
                if (!string.IsNullOrEmpty(w.fitAnimRole)) w.BuildAnimPreview(w.fitAnimRole);   // and resume the clip that was playing, now from the fresh bake
            }
    }

    // Flatten the combined prefab into (mesh, materials, matrix) draws — the asset hierarchy's transforms are valid
    // (the FBX rest pose IS the bind pose after rest-normalization, so drawing sharedMesh at the renderer transform
    // shows the correct stance; the prop's matrix includes the bone chain + the live rotation).
    void LoadFitPreview(string prefabPath, List<Mesh> uvSubstitutes = null)
    {
        previewPath = prefabPath ?? "";
        fitDraws = null;
        fitPan = Vector2.zero;   // fresh preview reframes centered (same rule as the Vehicle Lab) — orbit/zoom kept
        var go = string.IsNullOrEmpty(previewPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(previewPath);
        if (go == null) return;
        fitDraws = new List<(Mesh, Material[], Matrix4x4)>();
        bool first = true;
        // ATLAS-UV SUBSTITUTION (2026-08-19 — the Factory's fix, ported; MULTI-SMR same day: one persisted clone
        // PER skinned renderer, matched-and-consumed per renderer by GEOMETRY IDENTITY — vertex count, never name;
        // CreateAsset renames persisted meshes, which made a name match silently never fire — Factory drill lesson).
        var pool = uvSubstitutes != null ? new List<Mesh>(uvSubstitutes.Where(s => s != null)) : null;
        int subTotal = pool?.Count ?? 0, subUsed = 0;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
            if (m == null) continue;
            if (pool != null && r is SkinnedMeshRenderer)
            {
                int hit = pool.FindIndex(s => s.vertexCount == m.vertexCount);
                if (hit >= 0) { m = pool[hit]; pool.RemoveAt(hit); subUsed++; }
            }
            var mtx = r.transform.localToWorldMatrix;
            fitDraws.Add((m, r.sharedMaterials, mtx));
            var wb = TransformBounds(mtx, m.bounds);
            if (first) { fitBounds = wb; first = false; } else fitBounds.Encapsulate(wb);
        }
        if (fitDraws.Count == 0) fitDraws = null;
        // Loud diagnostic (the Factory drill lesson: a silent no-match is how this fix hid its failure twice)
        if (subTotal > 0)
            Debug.Log("[AnimLab] fit-preview UV substitution: " +
                      (subUsed == subTotal ? $"APPLIED {subUsed}/{subTotal} (texture-correct)"
                                           : $"APPLIED {subUsed}/{subTotal} — {subTotal - subUsed} clone(s) UNMATCHED (FBX re-slimmed since the last bake? Re-bake to refresh the _PreviewMesh set)"));
        // (A donor-clip "footprint centering" briefly lived here — REMOVED with the double-application discovery;
        // the runtime Position offset is now drawn LIVE in DrawFitPreview instead, which is the honest prediction.)
        Repaint();
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

    void DrawFitPreview(Rect rect)
    {
        var e = Event.current;
        if (rect.Contains(e.mousePosition))
        {
            if (e.type == EventType.ScrollWheel)
            {
                // consume the wheel HERE so the window's scroll view never sees it — this is the zoom
                fitZoom = Mathf.Clamp(fitZoom * Mathf.Pow(1.12f, e.delta.y > 0 ? 1f : -1f), 0.1f, 5f);
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0)
            {
                fitOrbit += new Vector2(e.delta.x, -e.delta.y) * 0.7f;
                fitOrbit.y = Mathf.Clamp(fitOrbit.y, -89f, 89f);
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && (e.button == 1 || e.button == 2))
            {
                // middle/right-drag pan in the camera plane (2026-07-27 user request — same control as the
                // Factory and Vehicle Lab previews); scaled by view distance at render time
                fitPan += new Vector2(-e.delta.x, e.delta.y) * 0.0035f;
                e.Use(); Repaint();
            }
        }
        if (e.type != EventType.Repaint || fitDraws == null) return;
        if (fitPRU == null) fitPRU = new PreviewRenderUtility();
        if (fitFallbackMat == null) fitFallbackMat = new Material(Shader.Find("Standard"));
        // PLAY THE CLIP: advance and sample BEFORE the camera framing below (it frames the posed instance).
        if (fitAnimInst != null)
        {
            if (fitAnimClip != null && fitAnimClip.length > 0.01f)
            {
                double now = EditorApplication.timeSinceStartup;
                float dt = Mathf.Clamp((float)(now - fitAnimTick), 0f, 0.1f);   // clamp: a stalled editor must not jump the clip
                fitAnimTick = now;
                if (fitAnimPlaying) fitAnimT = Mathf.Repeat(fitAnimT + dt * fitAnimSpeed, fitAnimClip.length);
                fitAnimClip.SampleAnimation(fitAnimInst, fitAnimT);
            }
            if (!fitAnimBoundsValid)
            {   // framed ONCE from the posed instance — re-framing per frame would breathe with the animation
                bool first = true;
                foreach (var r in fitAnimInst.GetComponentsInChildren<Renderer>())
                {
                    if (r == null) continue;
                    if (first) { fitAnimBounds = r.bounds; first = false; } else fitAnimBounds.Encapsulate(r.bounds);
                }
                fitAnimBoundsValid = !first;
            }
        }
        fitPRU.BeginPreview(rect, GUIStyle.none);
        // try/finally so a throw in DrawMesh/Render can never skip EndPreview — an unclosed PRU errors and renders
        // garbage on EVERY later frame (the "BeginPreview not closed" cascade), which made this preview untrustworthy.
        Texture fitTex = null;
        try
        {
            var cam = fitPRU.camera;
            // frame the ground square too when it's drawn — a square outside the frustum reads as "no square"
            var frame = fitAnimInst != null && fitAnimBoundsValid ? fitAnimBounds : fitBounds;
            if (fitGrounded) frame.Encapsulate(new Bounds(new Vector3(0f, -0.02f, 0f),
                new Vector3(2f * ModelFactoryWindow.TileCornerRadius, 0.04f, 2f * ModelFactoryWindow.TileInradius)));
            float radius = Mathf.Max(frame.extents.magnitude, 0.1f);
            float dist = radius * 2.0f * fitZoom;
            var rot = Quaternion.Euler(-fitOrbit.y, fitOrbit.x, 0f);
            var lookAt = frame.center + rot * new Vector3(fitPan.x, fitPan.y, 0f) * dist;   // pan shifts the look target in the camera plane
            cam.transform.position = lookAt + rot * (Vector3.back * dist);
            cam.transform.rotation = Quaternion.LookRotation(lookAt - cam.transform.position);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = dist + radius * 4f;
            cam.fieldOfView = 30f;
            fitPRU.lights[0].intensity = 1.3f;
            fitPRU.lights[0].transform.rotation = Quaternion.Euler(45f, 45f, 0f);
            if (fitPRU.lights.Length > 1) fitPRU.lights[1].intensity = 0.6f;
            fitPRU.ambientColor = new Color(0.3f, 0.3f, 0.3f);
            if (fitGrounded)
            {
                if (fitGroundMesh == null) fitGroundMesh = ModelFactoryWindow.BuildTileHex("AnimLabTileHex");
                if (fitGroundMat == null)
                {
                    fitGroundMat = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave };
                    fitGroundMat.SetFloat("_Glossiness", 0f);
                }
                // boats float on water-blue (the pawn's own Boat capability profile — characteristic, not name)
                fitGroundMat.color = ModelFactoryWindow.IsBoatPawn(cur?.pawnDescription)
                    ? new Color(0.23f, 0.36f, 0.47f) : new Color(0.33f, 0.40f, 0.29f);
                fitPRU.DrawMesh(fitGroundMesh, Matrix4x4.Translate(new Vector3(0f, -0.02f, 0f)), fitGroundMat, 0);
                if (fitArrowMesh == null) fitArrowMesh = ModelFactoryWindow.BuildForwardArrow("AnimLabForwardArrow");
                fitPRU.DrawMesh(fitArrowMesh, Matrix4x4.Translate(new Vector3(0f, -0.01f, 0f)), fitFallbackMat, 0);
                // REFERENCE MAN (2026-08-19) — shared with the Factory (one mesh builder, one height constant)
                if (fitRefMan)
                {
                    if (fitRefManMesh == null) fitRefManMesh = ModelFactoryWindow.BuildRefMan("AnimLabRefMan");
                    // at the USER-DIALED spot (header fields) — mirrors the Factory
                    fitPRU.DrawMesh(fitRefManMesh, Matrix4x4.TRS(new Vector3(fitRefManPos.x, -0.02f, fitRefManPos.y), Quaternion.identity, Vector3.one * ModelFactoryWindow.HumanRefHeight), fitFallbackMat, 0);
                }
            }
            bool anyDead = false;
            // LIVE runtime Position offset (the plugin adds the registry `position` to the pawn every frame; Lab
            // entries are all animated). Registry -> preview axes: x sway -> X, y fore/aft -> Z, z -> up Y.
            var liveOff = fitGrounded && cur != null && cur.position != Vector3.zero
                ? Matrix4x4.Translate(new Vector3(cur.position.x, cur.position.z, cur.position.y))
                : Matrix4x4.identity;
            // While a clip plays, the live instance IS the model (the camera renders it, skinned); drawing the
            // bind-pose list too would ghost a second motionless copy through it.
            foreach (var (mesh, mats, mtx) in (fitAnimInst != null ? Enumerable.Empty<(Mesh, Material[], Matrix4x4)>() : fitDraws.Select(d => (d.mesh, d.mats, d.mtx))))
            {
                // A cached mesh can be DESTROYED under us (the slim FBX deleted/reimported outside the window —
                // e.g. a forced cache clear); Unity fake-null catches it. Drop the stale cache instead of spamming
                // MissingReferenceException on every repaint; the next Refresh/Bake rebuilds it.
                if (mesh == null) { anyDead = true; continue; }
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var mat = mats != null && mats.Length > 0 ? (mats[Mathf.Min(s, mats.Length - 1)] ?? fitFallbackMat) : fitFallbackMat;
                    fitPRU.DrawMesh(mesh, liveOff * mtx, mat, s);
                }
            }
            if (anyDead) fitDraws.Clear();
            cam.Render();
        }
        finally { fitTex = fitPRU.EndPreview(); }
        if (fitTex != null) GUI.DrawTexture(rect, fitTex, ScaleMode.StretchToFill, false);
        // keep the frames coming while a clip runs (an editor window only repaints on demand)
        if (fitAnimInst != null && fitAnimPlaying && fitAnimClip != null && fitAnimClip.length > 0.01f) Repaint();
    }

    // FIT PREVIEW: the model's slim FBX (its rest pose IS the idle stance after rest-normalization, and its bone
    // hierarchy is exactly what the game composes) with the hand prop's SHIPPED mesh parented to the glue bone using
    // the same math as the runtime: identity local transform + the registry handPropAngles rotation (default zero).
    // What you see here is what the game glues — dial the Prop Lab's Rotation/Position offsets, re-bake the prop,
    // press Refresh: no relaunch needed for fit iteration.
    void BuildFitPreview()
    {
        try
        {
            string res = (cur.resourceName ?? "").Trim();
            string prop = (cur.handPropName ?? "").Trim();
            if (res.Length == 0) { status = "Preview needs a loaded entry."; return; }
            bool withProp = prop.Length > 0 && !string.IsNullOrEmpty((cur.handPropGuid ?? "").Trim());
            string fbxRel = "Assets/FactorySource/" + res + "/anim/" + res + "_anim.fbx";
            var fbxGo = AssetDatabase.LoadAssetAtPath<GameObject>(fbxRel);
            if (fbxGo == null)
            {
                // No rig FBX (a STATIC model): the baked preview prefab is all there is. Animated models always
                // take the FBX route below — the rest-pose hierarchy previews UPRIGHT and faithful, unlike the
                // old preview prefab whose fixed display flips stood the howitzer on end.
                string pp = "Assets/FactorySource/" + res + "/" + res + "_Preview.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(pp) == null) { status = "No preview assets for '" + res + "' — bake the model first."; return; }
                fitGrounded = false;   // display-flipped prefab — the origin square would lie here
                LoadFitPreview(pp);
                return;
            }
            Mesh propMesh = null; Material propMat = null;
            if (withProp)
            {
                propMesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Resources/" + prop + "_ModelMesh.asset");
                propMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/" + prop + "_Mat.mat");
                if (propMesh == null) { status = "Fit preview: no baked mesh for '" + prop + "' (bake it in the Prop Lab)."; return; }
            }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbxGo);
            try
            {
                PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                // texture the body with the model's baked atlas (reuse the Factory's preview material when present)
                var bodyMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/FactorySource/" + res + "/" + res + "_PreviewMat.mat");
                if (bodyMat != null)
                    foreach (var r in inst.GetComponentsInChildren<Renderer>())
                    {
                        var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                        for (int i = 0; i < mats.Length; i++) mats[i] = bodyMat;
                        r.sharedMaterials = mats;
                    }
                if (withProp)
                {
                // the glue bone — same substring match as the plugin (renamed b###_<orig> bones)
                string sub = string.IsNullOrEmpty(cur.handPropBone) ? "R_Hand" : cur.handPropBone;
                Transform bone = inst.GetComponentsInChildren<Transform>()
                    .FirstOrDefault(t => t.name.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0);
                if (bone == null) { status = $"Fit preview: no bone matches '{sub}' in the FBX."; return; }
                // the prop, glued exactly like the runtime: identity local + the LIVE rotation (same composition
                // stage as the runtime handPropAngles stamp — 'Save rotation to game' ships this exact value)
                var pgo = new GameObject(prop);
                pgo.transform.SetParent(bone, false);
                pgo.transform.localRotation = Quaternion.Euler(fitAngles);
                pgo.AddComponent<MeshFilter>().sharedMesh = propMesh;
                var pmr = pgo.AddComponent<MeshRenderer>();
                var pmats = new Material[Mathf.Max(1, propMesh.subMeshCount)];
                for (int i = 0; i < pmats.Length; i++) pmats[i] = propMat;
                pmr.sharedMaterials = pmats;
                }
                string outPath = "Assets/FactorySource/" + res + "/" + res + "_PropFit.prefab";
                AssetDatabase.DeleteAsset(outPath);
                PrefabUtility.SaveAsPrefabAsset(inst, outPath);
                fitGrounded = true;    // rest-pose rig route — origin plane = the in-game ground
                // multi-material bakes persist atlas-remapped clones (one per skinned renderer); single-material rigs have none (their UVs wrap fine)
                LoadFitPreview(outPath, UniversalBaker.LoadPreviewSubstitutes(res));
                status = withProp
                    ? "Fit preview rebuilt (" + outPath + ") — model + prop. NOT shipped; preview-only."
                    : "Model preview rebuilt (rest pose from the rig FBX). NOT shipped; preview-only.";
            }
            finally { DestroyImmediate(inst); }
        }
        catch (Exception e) { status = "Fit preview FAILED: " + e.Message; Debug.LogError("[AnimLab] " + e); }
    }

    // Context handoff from the Model Factory ("Edit in Animation Lab"): land on the RIGHT entry. Match an
    // existing animated entry by resource name, then by model file; otherwise pre-fill a NEW animated entry from the
    // Factory's current form (a fresh rigged model getting its animation configured for the first time).
    internal static void OpenFor(string resourceName, string modelFile, string pawnDescription, ModelDef fullForm = null)
    {
        Open();
        var w = GetWindow<AnimationLabWindow>();
        var all = ModelRegistry.Load();
        // Match by RESOURCE NAME only when the Factory form carries one: the model-file fallback hijacked the WRONG
        // entry once several entries share a model (the sandbox pattern — e.g. a state-machine test entry reusing the
        // howitzer's GLB). File-matching remains only for an UNNAMED form.
        var e = all.FirstOrDefault(x => x.animated && !string.IsNullOrEmpty(resourceName) && x.resourceName == resourceName)
             ?? (string.IsNullOrEmpty(resourceName) ? all.FirstOrDefault(x => x.animated && !string.IsNullOrEmpty(modelFile) && x.modelFile == modelFile) : null)
             ?? all.FirstOrDefault(x => !string.IsNullOrEmpty(resourceName) && x.resourceName == resourceName);   // not-yet-animated entry being upgraded
        if (e != null)
        {
            w.cur = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(e));   // clone, as OnSelectResource does
            w.cur.animated = true;
            // Carry the Factory form's CURRENT model file over the registry's (fix 2026-08-01): the user may have just
            // Browsed to the rigged _Spin.glb in the Factory but not saved it — the registry entry still has the OLD/
            // static file, so OpenFor was handing the Lab the static model → Auto-detect saw no Spin clip → nothing
            // happened. The model file is Factory-owned and RebaseOnRegistry keeps the Lab's copy, so honoring the
            // handed-over file is exactly what "Edit in Animation Lab" should do.
            if (!string.IsNullOrWhiteSpace(modelFile)) w.cur.modelFile = modelFile.Replace('\\', '/');
            w.status = "Loaded '" + e.resourceName + "' (handed over from the Model Factory"
                     + (!string.IsNullOrWhiteSpace(modelFile) && modelFile.Replace('\\', '/') != (e.modelFile ?? "") ? ", using the Factory's current model file" : "") + ").";
        }
        else if (fullForm != null)
        {
            // UNSAVED entry handed over WHOLE (2026-07-27, the Clone flow): the Factory's in-memory form — a
            // clone carrying a full animation recipe — isn't in the registry yet, but "not saved" must not mean
            // "starts from scratch": the entire point of Clone is arriving here with the recipe intact.
            w.cur = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(fullForm));
            w.cur.animated = true;
            w.status = "Unsaved entry handed over from the Model Factory (full settings — e.g. a Clone). Bake to create it.";
        }
        else
        {
            w.cur = new ModelDef { animated = true, resourceName = resourceName ?? "", modelFile = modelFile ?? "", pawnDescription = pawnDescription ?? "" };
            w.status = "New animated entry pre-filled from the Model Factory — pick a clip and Bake.";
        }
        w.RefreshList();   // re-derives the dropdown index from the loaded name
        w.Repaint();
    }

    // Only ANIMATED entries are listed (static models have no animation to configure).
    void RefreshList()
    {
        var names = ModelRegistry.Load().Where(m => m.animated).Select(m => m.resourceName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        names.Insert(0, "<New>");
        existing = names.ToArray();
        // Index-by-NAME, never a persisted numeric index: the list is filtered + rebuilt per reload, so a stale index
        // would present a different entry than the form holds.
        selected = Array.IndexOf(existing, cur.resourceName);
        if (selected < 0) selected = 0;
    }

    // Re-read the model's clip/bone names only when the path changes (OnGUI runs every frame; file I/O must not).
    // With deploy conversion on, the clips the bake (and the roles) actually see live in the CONVERTED file — the
    // Pick dropdowns and the ▶ picker must inspect that, not the raw source. convertIfNeeded=true may run Blender
    // (progress bar) to bring the conversion up to date; false only swaps in an already-converted file (safe per-GUI).
    string EffectiveModelFile(bool convertIfNeeded)
    {
        if (!cur.deployConvert) return cur.modelFile;
        if (convertIfNeeded)
        {
            string conv = UniversalBaker.EnsureDeployConverted(ModelFactoryWindow.ConfigFor(cur), out string err);
            if (conv == null) { ShowNotification(new GUIContent(err)); Debug.LogError("[AnimLab] " + err); }
            return conv;
        }
        string p = UniversalBaker.DeployConvertedPath((cur.resourceName ?? "").Trim());
        return System.IO.File.Exists(p) ? p : cur.modelFile;
    }

    void EnsureClips()
    {
        string f = EffectiveModelFile(false) ?? "";
        if (f == clipProbeFile) return;
        clipProbeFile = f;
        (animClips, animBonePrefixes, animBoneNames) = ModelFactoryWindow.InspectModelFull(f);
    }

    // AUTO-DETECT: read the model's clip names + rig and fill the animation config with a best guess, explained in the
    // status so it's always reviewable. Recognises (in order): a Vehicle Lab 'Spin' rig (wheels/tracks/floating — the
    // exact recipe that window prints), a DEPLOY-conversion model (hint only — its frame ranges can't be inferred), a
    // character idle/run, and a single continuous clip. Nothing here bakes; it only pre-fills the form.
    void AutoDetect()
    {
        EnsureClips();
        var clips = animClips;
        if (clips == null || clips.Count == 0)
        {
            status = "Auto-detect: no clips readable from the model (glb/gltf inspection only). Set the Model file (or Browse the rigged GLB), then retry.";
            return;
        }
        string spin   = clips.FirstOrDefault(c => c.Equals("Spin", StringComparison.OrdinalIgnoreCase));
        string deploy = clips.FirstOrDefault(c => c.IndexOf("deploy", StringComparison.OrdinalIgnoreCase) >= 0);

        if (spin != null)
        {
            // Vehicle Lab rig — the recipe that window prints on success: wheels/tracks roll on move, still when parked.
            cur.animStateDriven = true;
            cur.animClip = spin + "[0..0]";                                                   // Idle/reference = the motionless rest frame
            cur.animClipMove = spin;                                                          // Movement = the full linear spin
            cur.animClipAfter = ""; cur.animClipAttack = ""; cur.animClipCombat = ""; cur.animClipPreMove = ""; cur.animClipIdle = "";
            cur.convertRig = true; cur.autoGroundWheels = true; cur.keepTranslations = true; cur.animUnitFix = false; cur.deployConvert = false;
            status = $"Auto-detected a Vehicle Lab rig ('{spin}' clip):  State-driven ON · Idle/reference = {spin}[0..0] (still) · Movement = {spin} (rolls) · Convert raw rig ON · Auto-ground ON · Keep bone translations ON · Fix 100× OFF.  Review, set Size in the Model Factory if needed, then Bake.";
            Repaint(); return;
        }
        if (deploy != null)
        {
            status = $"Auto-detect: looks like a DEPLOY-conversion model ('{deploy}' clip). Those need the Deploy-conversion flag + frame ranges (ready-frame, recoil…) that can't be read from the baked clip — configure it from the howitzer recipe in the docs. Auto-detect leaves deploy settings alone.";
            return;
        }
        string idle = clips.FirstOrDefault(c => c.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0);
        string move = clips.FirstOrDefault(c => c.IndexOf("run", StringComparison.OrdinalIgnoreCase) >= 0
                                             || c.IndexOf("walk", StringComparison.OrdinalIgnoreCase) >= 0
                                             || c.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0);
        if (idle != null && move != null)
        {
            cur.animStateDriven = true; cur.animClip = idle; cur.animClipMove = move;
            status = $"Auto-detected a character rig:  State-driven ON · Idle = {idle} · Movement = {move}.  Check 'Convert raw rig' (ON for an auto-rigged download, OFF for a game-ready rig) + Size, then Bake.";
            Repaint(); return;
        }
        if (clips.Count == 1)
        {
            cur.animStateDriven = false; cur.animClip = clips[0];
            status = $"Auto-detected a single clip '{clips[0]}': continuous loop (State-driven OFF).  Check 'Convert raw rig' + Size, then Bake.";
            Repaint(); return;
        }
        cur.animStateDriven = false; cur.animClip = clips[0];
        status = $"Auto-detect: {clips.Count} clips found ({string.Join(", ", clips.Take(4))}{(clips.Count > 4 ? "…" : "")}) with no obvious Spin/idle-move pattern. Set a continuous loop of '{clips[0]}' (State-driven OFF) as a starting point — pick the right clip/roles, then Bake.";
        Repaint();
    }

    // One clip text field + Pick dropdown — shared by the single-clip field and the three state-role fields, so
    // every role gets the same pick-from-model UX.
    void ClipRow(string label, string tooltip, Func<string> get, Action<string> set)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            // VARIABLE-width value box: expands from the label to the right-pinned Pick/▶ buttons, shrinks freely
            // with the window (MinWidth 0 — never starves the buttons).
            set(EditorGUILayout.TextField(new GUIContent(label, tooltip), get(), GUILayout.MinWidth(0), GUILayout.ExpandWidth(true)));
            using (new EditorGUI.DisabledScope(animClips.Count == 0))
                if (GUILayout.Button(new GUIContent("Pick", animClips.Count == 0 ? "No clips readable (glb/gltf only) — type the name" : null), GUILayout.Width(70)))
                {
                    var r = GUILayoutUtility.GetLastRect();
                    var arr = animClips.ToArray();
                    // labels carry the clip LENGTH (frames 0..N) so a clip[start..end] slice can be authored
                    // straight from the dropdown; the picked VALUE stays the pure clip name.
                    var labels = animClips.Select(n => { var len = ModelFactoryWindow.ClipLengthOf(cur.modelFile, n); return len != null ? n + "   —   " + len : n; }).ToArray();
                    new StringDropdown(new AdvancedDropdownState(), labels, arr, "Clips", n =>
                    {
                        // picking the SAME clip the field already holds (plain or sliced) keeps it — Pick must not
                        // wipe a hand-authored [start..end] range. "Same" = exactly the name, or the name followed
                        // by a slice bracket (so picking 'deploy' can't preserve a different clip named 'deploy2').
                        var curV = (get() ?? "").Trim();
                        if (!(curV == n || curV.StartsWith(n + "[", StringComparison.Ordinal))) set(n);
                        Repaint();
                    }).Show(r);
                }
            if (GUILayout.Button(new GUIContent("▶", "Open the CLIP RANGE PICKER: preview + play/scrub the model's clips, set a Start..End " +
                "frame slice, and Confirm to fill this field (clip[start..end])."), GUILayout.Width(26)))
            {
                string mf = EffectiveModelFile(true);   // deploy conversion on -> preview the CONVERTED rig (runs/updates the conversion first)
                if (mf != null) ClipRangeDialog.Open(mf, cur.resourceName, get(), s => { set(s); Repaint(); });
            }
        }
    }

    void OnSelectResource(bool rebuildPreview = true)
    {
        formDiffersFromRegistry = false;   // loading fresh = in sync by definition
        if (selected <= 0) { cur = new ModelDef { animated = true }; status = ""; if (rebuildPreview) { DestroyFitPreview(); previewPath = ""; } return; }
        var e = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == existing[selected]);
        if (e == null) return;
        cur = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(e));   // clone so edits don't mutate the stored copy
        cur.animated = true;
        status = "Loaded '" + e.resourceName + "'.";
        // PREVIEW FOLLOWS THE SELECTION (2026-07-27): switching entries used to keep the PREVIOUS model's
        // preview on screen (the T-62 grinning under the m114's settings). Rebuild for the new entry; if it
        // has no preview assets yet, clear the pane rather than show a stale model.
        // BUT ↻ Reload passes rebuildPreview=false (user request 2026-08-01): reloading the SAME entry must ONLY
        // reload the data and NOT touch the preview — the rebuild path corrupts the texture on some models
        // (tiling-UV rigs), whereas the existing correct preview is fine to keep for the same model.
        if (rebuildPreview) { DestroyFitPreview(); previewPath = ""; BuildFitPreview(); }
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        // Wider label column so the descriptive field labels ("Also remove (adds to defaults)", "Deploy
        // conversion (rigid-parts source)", "Muzzle bone (flash fires from)"…) read in full instead of clipping
        // at Unity's 150px EditorWindow default. Responsive: scales with the window, clamped 210–245px. The two
        // local overrides below (deploy End = 40, recoil row = 88) save/restore against THIS value.
        EditorGUIUtility.labelWidth = Mathf.Clamp(position.width * 0.35f, 210f, 245f);
        EditorGUILayout.LabelField("Animation Lab", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Configures the ANIMATION of a model: which clip plays and how (continuous loop, fire-on-attack, " +
            "deploy-on-stop + recoil). The model itself (file, transform, size, shading) is set up in the " +
            "Model Factory — each setting lives in exactly one of the two windows.", MessageType.None);

        // --- Pick the animated entry to edit ---
        using (new EditorGUILayout.HorizontalScope())
        {
            int newSel = EditorGUILayout.Popup("Edit existing", selected, existing);
            if (newSel != selected) { selected = newSel; OnSelectResource(); GUI.FocusControl(null); }
            using (new EditorGUI.DisabledScope(selected <= 0))
                if (GUILayout.Button(new GUIContent("↻ Reload", "Discard the form and RE-LOAD this entry fresh from the registry file — " +
                    "the explicit escape from any stale window copy. (Selecting the same entry in the dropdown does NOT reload it.)"), GUILayout.Width(72)))
                { RefreshList(); OnSelectResource(rebuildPreview: false); GUI.FocusControl(null); }
            using (new EditorGUI.DisabledScope(selected <= 0))
                if (GUILayout.Button("Remove", GUILayout.Width(72)))
                    if (EditorUtility.DisplayDialog("Remove entry",
                        $"Remove '{existing[selected]}' from the registry? (Baked assets stay on disk.)", "Remove", "Cancel"))
                    { bool rem = ModelRegistry.Remove(existing[selected]); cur = new ModelDef { animated = true }; selected = 0; RefreshList(); status = rem ? "Removed." : "Remove FAILED — see the Console (registry locked or corrupt)."; }
        }
        if (formDiffersFromRegistry)
            EditorGUILayout.HelpBox("Form ≠ saved registry entry (your edits survived a compile, or the registry changed " +
                "outside this window). Nothing was discarded. Choose: ↻ Reload = take the registry (drops these form " +
                "values) · Save (no bake) or Bake = keep exactly what you see here.", MessageType.Warning);
        EditorGUILayout.Space();

        // --- Model identity: READ-ONLY here (the Factory owns it) ---
        bool hasModel = !string.IsNullOrWhiteSpace(cur.resourceName);
        if (!hasModel)
        {
            EditorGUILayout.HelpBox("No model loaded. Pick one under 'Edit existing', or set the model up in the " +
                "Model Factory first (file, transform, size) and press its 'Edit in Animation Lab' button.", MessageType.Info);
        }
        else
        {
            EditorGUILayout.LabelField("Resource", cur.resourceName);
            EditorGUILayout.LabelField("Target pawn", string.IsNullOrWhiteSpace(cur.pawnDescription) ? "(set in the Model Factory)" : cur.pawnDescription);
            using (new EditorGUILayout.HorizontalScope())
            {
                // MinWidth(0) lets the path label SHRINK: without it the label claims all remaining width and, at
                // narrower window widths, starves the fixed-width buttons to nothing ("where did my button go?").
                EditorGUILayout.LabelField("Model file", string.IsNullOrWhiteSpace(cur.modelFile) ? "(re-bake uses the extracted files)" : cur.modelFile, EditorStyles.wordWrappedMiniLabel, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(cur.modelFile)))
                    if (GUILayout.Button(new GUIContent("▶ Play clip",
                        "Play the RAW model file's ENTIRE source animation (every take, full length — no conversion, no " +
                        "slicing): play, scrub, analyze. Frame numbers found here feed the recipe (Deploy frames, Recoil " +
                        "a..b). Confirm copies the picked a..b range to the clipboard; Cancel just closes."), GUILayout.Width(80)))
                        ClipRangeDialog.Open(cur.modelFile, "raw/" + (cur.resourceName ?? "").Trim(), "", s =>   // ONE shared FactorySource/raw/<name>/ tree: outside every model folder (raw FBXs INSIDE a model's subtree hijacked the anim FBX's by-name texture search — greyed/scrambled previews), and one folder in the listing instead of seven "_raw" siblings
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"\[(\d+)\.\.(\d+)\]");
                            string range = m.Success ? (m.Groups[1].Value + ".." + m.Groups[2].Value) : (s ?? "");
                            EditorGUIUtility.systemCopyBuffer = range;
                            ShowNotification(new GUIContent("Range " + range + " copied to clipboard"));
                        });
                if (GUILayout.Button("Browse…", GUILayout.Width(70)))
                {
                    string start = string.IsNullOrWhiteSpace(cur.modelFile) ? "" : System.IO.Path.GetDirectoryName(cur.modelFile);
                    // If that folder no longer exists (e.g. the source folder was renamed/moved), walk up to the nearest
                    // existing ancestor so Browse opens somewhere useful instead of falling back to the project root.
                    while (!string.IsNullOrEmpty(start) && !System.IO.Directory.Exists(start))
                        start = System.IO.Path.GetDirectoryName(start);
                    string picked = EditorUtility.OpenFilePanel("Model file", start ?? "", "glb,gltf,fbx,blend,obj");
                    if (!string.IsNullOrEmpty(picked)) cur.modelFile = picked.Replace('\\', '/');
                }
            }
            // BROKEN-LINK REPORT: if a model file IS referenced but isn't on disk (source moved/renamed), warn right here
            // so it's obvious the moment the entry is open — instead of only failing when you press ▶ Play clip or Bake.
            if (!string.IsNullOrWhiteSpace(cur.modelFile) && !System.IO.File.Exists(cur.modelFile))
                EditorGUILayout.HelpBox("Model file not found on disk:\n" + cur.modelFile +
                    "\nThe source was moved or renamed. Fix it with Browse… (or press ⟳ Reload if the registry was already corrected outside this window).", MessageType.Warning);
        }
        // --- Disable override (debug: fall back to the original vanilla unit) ---
        EditorGUILayout.Space();
        bool wasDisabled = cur.disabled;
        cur.disabled = EditorGUILayout.ToggleLeft(new GUIContent("● Disable override — show the ORIGINAL unit",
            "DEBUG: when ticked, this model override is skipped entirely and the game's ORIGINAL vanilla unit renders — " +
            "to compare against your custom model or watch the donor's own animation. RUNTIME-ONLY: Save (no bake) + relaunch."),
            cur.disabled);
        if (cur.disabled != wasDisabled) formDiffersFromRegistry = true;
        if (cur.disabled) EditorGUILayout.HelpBox("Override DISABLED — this entry is ignored at load; the original unit shows in-game. Untick + Save (no bake) + relaunch to restore your model.", MessageType.Warning);

        // --- Deploy conversion (raw rigid-parts source → bone-per-part rig, part of the recipe) ---
        EditorGUILayout.Space();
        cur.deployConvert = EditorGUILayout.Toggle(new GUIContent("Deploy conversion (rigid-parts source)",
            "Model file is a RAW original whose animation moves rigid PARTS (Sketchfab howitzer/crane/landing gear — node " +
            "transforms, no skinning)? Tick this: the bake first runs the deploy converter (Blender, automatic, cached) to " +
            "build a bone-per-part rig, then bakes the CONVERTED file. All knobs live on this entry — the full pipeline " +
            "reproduces from the registry, nothing hand-run. The converted file adds ready-made role clips " +
            "(deployed/folded/unfold/fold/recoil) to pick below."), cur.deployConvert);
        if (cur.deployConvert)
        {
            EditorGUI.indentLevel++;
            // VARIABLE-width fields (match the Clip rows): expand with the window, shrink freely (MinWidth 0).
            var flex = new[] { GUILayout.MinWidth(0), GUILayout.ExpandWidth(true) };
            using (new EditorGUILayout.HorizontalScope())
            {
                cur.deployStart = EditorGUILayout.IntField(new GUIContent("Deploy frames",
                    "Source frame where the deploy motion STARTS (usually 0)."), cur.deployStart, flex);
                float lw = EditorGUIUtility.labelWidth; EditorGUIUtility.labelWidth = 40;
                cur.deployEnd = EditorGUILayout.IntField(new GUIContent("End",
                    "Source frame where the deploy COMPLETES (fully deployed). REQUIRED. Find it by scrubbing the raw file in the ▶ picker."), cur.deployEnd, flex);
                EditorGUIUtility.labelWidth = lw;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                cur.deployStripExtra = EditorGUILayout.TextField(new GUIContent("Also remove (adds to defaults)",
                    "Extra parts to DELETE on top of the converter's default crew/prop kill-list — build it with the picker " +
                    "so you never re-type the defaults. The M114's mis-animated control hand-wheels live here. A ticked name " +
                    "also removes any sub-part whose name contains it."), cur.deployStripExtra, flex);
                if (GUILayout.Button(new GUIContent("Pick parts…", "Tick parts of the SOURCE model to remove (defaults stay automatic)."), GUILayout.Width(84)))
                {
                    var (_, _, srcParts) = ModelFactoryWindow.InspectModelFull((cur.modelFile ?? "").Trim());
                    StripPartsDialog.Open(cur.deployStripExtra, srcParts, s => { cur.deployStripExtra = s; Repaint(); });
                }
            }
            cur.deployStrip = EditorGUILayout.TextField(new GUIContent("Strip parts (replace defaults)",
                "ADVANCED — name substrings that REPLACE the default kill-list wholesale (usually leave EMPTY; the canoe's " +
                "'camera' is the rare case). To ADD parts, use 'Also remove' above — it keeps the defaults."), cur.deployStrip, flex);
            cur.deployReadyFrame = EditorGUILayout.TextField(new GUIContent("Barrel ready frame",
                "Source frame of the FULLY-ELEVATED barrel. Sources often pause at an aim angle and only rise much later; " +
                "this re-keys barrel/cannon parts to rise over the deploy's back half instead. Empty = leave the barrel as authored."), cur.deployReadyFrame, flex);
            cur.deployLegScale = EditorGUILayout.TextField(new GUIContent("Leg spread scale",
                "EMPTY = keep the original leg animation VERBATIM (no re-authoring — recommended first try). A number re-keys " +
                "*leg* parts as a clean travel→spread interpolation scaled by it: 1 = the source's full spread as pure " +
                "rotation (needed if the game's rotation-only bake mangles sliding legs), 0.5 = half as wide."), cur.deployLegScale, flex);
            cur.deployBarrelScale = EditorGUILayout.TextField(new GUIContent("Barrel elevation scale",
                "Empty = 1. >1 exaggerates the elevation past the source's firing max."), cur.deployBarrelScale, flex);
            cur.deployRecoil = EditorGUILayout.TextField(new GUIContent("Recoil frames (a..b)",
                "The recoil SLAM's sub-range IN THE SOURCE clip (e.g. 445..451) — the slam ONLY: the source's post-slam " +
                "frames are usually reload choreography (crew lowering the barrel), not a run-out. The return to battery " +
                "is synthesized (see Return slow). The whole fire cycle becomes the 'recoil' role clip — set the Attack " +
                "clip to plain 'recoil'. Empty = no recoil."), cur.deployRecoil, flex);
            using (new EditorGUILayout.HorizontalScope())
            {
                // NO EMPTY FIELDS (user rule — blanks confuse): the recoil knobs materialize their explicit
                // defaults on sight, so what you read is exactly what bakes (and what saves).
                MaterializeRecoilDefaults(cur);
                // compact labels + zero-minimum fields: four standard 150px label columns don't fit a row — the
                // last field (Return slow) was starved clean off at normal window widths.
                float lw2 = EditorGUIUtility.labelWidth; EditorGUIUtility.labelWidth = 88;
                // Slide scale + Arc R deliberately NOT shown: under Slam-degrees the slide scale self-cancels (the
                // radius derives from the scaled peak) and raw Arc R is superseded — dead knobs confuse. Both registry
                // fields survive for legacy recipes.
                cur.deployRecoilStep = EditorGUILayout.TextField(new GUIContent("Recoil step", "0 = the ENTIRE fire animation OFF (default — the 'recoil' clip bakes as a held stance, a graceful no-op attack). 1 = enable at finest sampling; 2 = coarser."), cur.deployRecoilStep, GUILayout.MinWidth(0));
                cur.deployRecoilReturn = EditorGUILayout.TextField(new GUIContent("Return slow", "The fire-cycle's palindrome return: the recoil window played BACKWARD at this multiple of its duration (raises the barrel back to battery). 1 = natural speed, 4 = quarter speed, 0 = forward-only (ends as the source ends, then snaps to idle)."), cur.deployRecoilReturn, GUILayout.MinWidth(0));
                cur.deploySlamDeg = EditorGUILayout.TextField(new GUIContent("Slam (deg)", "The kick's SLAM PITCH in DEGREES — what you type is what renders. 0 = no kick. POSITIVE = muzzle-DOWN dip (the legacy look); NEGATIVE = muzzle-UP jump. ~5 = subtle, 8-12 = clearly visible, 20+ = dramatic."), cur.deploySlamDeg, GUILayout.MinWidth(0));
                cur.deploySlamSettle = EditorGUILayout.TextField(new GUIContent("Slam settle", "The slam's RECOVERY as a multiple of its rise. 1 = a symmetric snap; 3 = a heavy gun easing back. The rise always follows the source's own slam timing."), cur.deploySlamSettle, GUILayout.MinWidth(0));
                EditorGUIUtility.labelWidth = lw2;
            }
            // WHEEL SPIN in the travel stance (2026-08-22, the towed howitzer): the converter keys a LINEAR roll of the
            // named wheel bones into the 'folded' role clip, so Movement = folded[1..N] plays folded legs + rolling
            // wheels — fully baked, previewable here, no runtime layer.
            // Pivot lift LEADS this row, then the bones (user's layout, 2026-08-22): as a fourth field on the knobs
            // row below it was unfindable, and it is the knob you actually dial while getting a wheel to sit right.
            // Both labels get their own narrow label width — the window's ~245px default would starve the second field.
            using (new EditorGUILayout.HorizontalScope())
            {
                float lwW = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 84;
                cur.deployWheelBones = EditorGUILayout.TextField(new GUIContent("Wheel bones",
                    "Comma-separated WHEEL part/bone names of the source (substrings OK — 'l_wheel,r_wheel'). Pick them " +
                    "with the picker. The 'folded' role clip becomes an N-frame loop with these rolling; then set the " +
                    "Movement clip to folded[1..N]. Empty = wheels stay still. Re-convert + re-bake after changing."),
                    cur.deployWheelBones, flex);
                if (GUILayout.Button(new GUIContent("Pick wheels…", "Tick the wheel parts of the SOURCE model."), GUILayout.Width(90)))
                {
                    var (_, _, srcParts) = ModelFactoryWindow.InspectModelFull((cur.modelFile ?? "").Trim());
                    StripPartsDialog.Open(cur.deployWheelBones, srcParts, s => { cur.deployWheelBones = s; Repaint(); });
                }
                EditorGUIUtility.labelWidth = lwW;
            }
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(cur.deployWheelBones)))
            using (new EditorGUILayout.HorizontalScope())
            {
                float lw3 = EditorGUIUtility.labelWidth; EditorGUIUtility.labelWidth = 88;
                cur.deployWheelAxis = EditorGUILayout.TextField(new GUIContent("Axle axis", "X, Y or Z (world axis of the axle), or EMPTY = AUTO: each wheel's thinnest skinned extent. If a wheel wobbles instead of rolling, force the axis."), cur.deployWheelAxis, flex);
                cur.deployWheelFrames = EditorGUILayout.TextField(new GUIContent("Loop frames", "Frames in the rolling loop (empty = 15). Movement clip = folded[1..N]."), cur.deployWheelFrames, flex);
                cur.deployWheelDegrees = EditorGUILayout.TextField(new GUIContent("Degrees", "Wheel rotation over the loop (empty = -360 = one full turn per loop). Flip the sign if the wheels roll backwards."), cur.deployWheelDegrees, flex);
                EditorGUIUtility.labelWidth = lw3;
            }
            EditorGUI.indentLevel--;
        }
        EnsureClips();

        // --- Clip(s) ---
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Clip", EditorStyles.miniBoldLabel);
        // AUTO-DETECT (user request): inspect the model's clips + rig and fill the animation config so you don't
        // re-enter the Vehicle Lab's recipe by hand. Fills a best guess + explains it in the status; always reviewable.
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(cur.modelFile)))
                if (GUILayout.Button(new GUIContent("Auto-detect settings from the model",
                    "Reads the model's clips + rig and configures the animation automatically: a Vehicle Lab 'Spin' rig (wheels/tracks), a character's idle/run, or a single continuous loop. Fills the fields + explains what it chose — review, then Bake."), GUILayout.Height(22)))
                    AutoDetect();
        }
        cur.animStateDriven = EditorGUILayout.Toggle(new GUIContent("State-driven (idle / move / after / attack)",
            "OFF = today's single-clip modes: one clip, played as a continuous loop or via the Behavior flags below " +
            "(the drone, the howitzer). ON = a STATE MACHINE for characters: the IDLE clip plays standing, the " +
            "MOVEMENT clip plays while the unit moves (fixes the idle-slide), and the optional AFTER-MOVEMENT clip " +
            "plays once on stopping before settling into Idle. All clips come from the same model file and bake " +
            "against ONE shared skeleton. Mutually exclusive with Fire-on-attack / Deploy-when-stopped. Re-Bake after changing."),
            cur.animStateDriven);
        ClipRow(cur.animStateDriven ? "Idle / reference clip" : "Clip name",
            cur.animStateDriven
                ? "Plays while the unit stands still, AND serves as the model's REFERENCE clip (its first frame defines the " +
                  "shared rest pose all role clips encode against — use the FULL source motion here, never a stance slice). " +
                  "If the resting look is a POSE from mid-motion (a deployed howitzer), keep this the full clip and put the " +
                  "stance in 'Idle stance (override)' below."
                : "Which animation to bake when the model has several clips. Use Pick to choose from the clips found in the " +
                  "model. Leave EMPTY to use the model's assigned/first clip. Changing the clip needs a re-Bake.",
            () => cur.animClip ?? "", v => cur.animClip = v);
        if (cur.animStateDriven)
        {
            ClipRow("Idle stance (override)", "Optional. A held STANCE played instead of the reference clip while standing " +
                "(e.g. 'deploy[179..180]' — a 2-frame hold of the deployed pose). REQUIRED for stance idles: a stance baked " +
                "as the reference clip itself encodes as its own rest and renders as the TRAVEL pose in-game (the " +
                "\"forgot to deploy\" trap). Empty = idle plays the reference clip (characters with a real idle loop).",
                () => cur.animClipIdle ?? "", v => cur.animClipIdle = v);
            ClipRow("Idle-alt clip (occasional)", "Optional. An OCCASIONAL flavor one-shot while plain-idle — the tiger's howl. " +
                "Plays once every ~'Idle-alt interval' seconds (jittered), one pawn of the unit per firing, then back to Idle. " +
                "Never during move/attack/combat. Empty = none.",
                () => cur.animClipIdleAlt ?? "", v => cur.animClipIdleAlt = v);
            ClipRow("Idle-alt clip 2 (mixed in)", "Optional. A SECOND flavor one-shot (eat/groom) — each firing picks randomly " +
                "between the two idle-alts, so the unit doesn't repeat the same trick. Empty = only the first plays.",
                () => cur.animClipIdleAlt2 ?? "", v => cur.animClipIdleAlt2 = v);
            if (!string.IsNullOrWhiteSpace(cur.animClipIdleAlt) || !string.IsNullOrWhiteSpace(cur.animClipIdleAlt2))
                cur.idleAltInterval = EditorGUILayout.Slider(new GUIContent("Idle-alt interval (s)",
                    "Average seconds between idle-alt one-shots, jittered 0.6-1.4x (like the idle growl's cadence). " +
                    "RUNTIME-ONLY: Save (no bake) + rebuild is enough to retune. 0 disables without unbaking the clips."),
                    cur.idleAltInterval, 0f, 120f);
            ClipRow("Movement clip", "REQUIRED. Plays in a loop while the unit moves (e.g. a run cycle like 'a_RunN'). " +
                "Slices support a SPEED step: 'deploy[0..179/3]' = every 3rd frame = 3× faster (pacing is baked, never a runtime knob).",
                () => cur.animClipMove ?? "", v => cur.animClipMove = v);
            ClipRow("After-movement clip", "Optional. Played ONCE when the unit stops (a settle/plant motion), then Idle. Empty = stop straight into Idle.",
                () => cur.animClipAfter ?? "", v => cur.animClipAfter = v);
            ClipRow("Attack clip", "Optional. Played ONCE when the unit fires a ranged attack (e.g. 'shootAR2s'), overriding every other state for its duration. Empty = no attack animation.",
                () => cur.animClipAttack ?? "", v => cur.animClipAttack = v);
            ClipRow("Combat-idle clip", "Optional. Replaces Idle while the army is locked in a battle (a weapon-raised stance like 'CombatIdle1' — a single-frame pose clip is fine). Empty = normal Idle in battle.",
                () => cur.animClipCombat ?? "", v => cur.animClipCombat = v);
            ClipRow("Pre-movement clip", "Optional. Played ONCE when the unit STARTS moving (e.g. a howitzer folding its legs), then the Movement loop. Empty = straight into Movement.",
                () => cur.animClipPreMove ?? "", v => cur.animClipPreMove = v);
            if (!string.IsNullOrWhiteSpace(cur.animClipAttack))
                cur.attackRepeats = EditorGUILayout.IntSlider(new GUIContent("Attack repeats",
                    "How many times the Attack clip replays per trigger (the sim fires ONCE per attack, so a short " +
                    "recoil-pop clip like shootAR2s (0.17s) reads as a blip at 1; e.g. 6 ≈ 1s of sustained fire). " +
                    "RUNTIME-ONLY — 'Save (no bake)' + rebuild is enough, no re-bake."),
                    Mathf.Max(1, cur.attackRepeats), 1, 20);
            if (cur.fireOnAttack || cur.deployOnStop)
                EditorGUILayout.HelpBox("State-driven is mutually exclusive with Fire-on-attack / Deploy-when-stopped — " +
                    "those flags are ignored while State-driven is ON.", MessageType.Warning);
            cur.clearAimLayer = EditorGUILayout.Toggle(new GUIContent("Clear aim layer (artillery)",
                "Clear the game's procedural bone-rotation layer for this model. ARTILLERY needs it ON (the donor " +
                "streams aim/wheel junk that twists the rig — the legacy Fire/Deploy behaviors cleared it " +
                "implicitly); CHARACTERS need it OFF (the layer carries their facing). RUNTIME-ONLY — Save (no " +
                "bake) + relaunch."), cur.clearAimLayer);
            // Turret bone — free text + a Pick that lists the model's actual bones (you can't guess the internal name).
            using (new EditorGUILayout.HorizontalScope())
            {
                cur.turretBone = EditorGUILayout.TextField(new GUIContent("Turret bone (aims at target)",
                    "TURRETIZE: bone-name SUBSTRING of a VEHICLE turret to aim at the target — use Pick, or type e.g. " +
                    "'Turret'. The plugin retargets the game's own aim/heading angle onto this bone, so the turret tracks " +
                    "the enemy (no per-frame math). Empty = no turret aim. RUNTIME-ONLY — Save (no bake) + relaunch."),
                    cur.turretBone ?? "");
                using (new EditorGUI.DisabledScope(animBonePrefixes.Count == 0))
                    if (GUILayout.Button(new GUIContent("Pick", animBonePrefixes.Count == 0 ? "No bones readable from this model (glb/gltf only) — type the name" : null), GUILayout.Width(70)))
                    {
                        var r = GUILayoutUtility.GetLastRect();
                        var labels = animBonePrefixes.Select(kv => $"{kv.Key}  ({kv.Value} part{(kv.Value == 1 ? "" : "s")})").ToArray();
                        var values = animBonePrefixes.Select(kv => kv.Key).ToArray();
                        new StringDropdown(new AdvancedDropdownState(), labels, values, "Turret bone", p => { cur.turretBone = p; Repaint(); }).Show(r);
                    }
            }
            // aim axis only exists when there IS a turret bone (user: it should disappear when the bone is empty)
            if (!string.IsNullOrWhiteSpace(cur.turretBone))
                cur.turretAxis = EditorGUILayout.IntPopup(new GUIContent("Turret aim axis",
                    "Which local axis the aim angle rotates the turret bone around. 'Default' = the game's axis (usually reads " +
                    "as PITCH — turns up). A vehicle TURRET wants its YAW axis (turns around); a mechanized HOWITZER/ARTILLERY " +
                    "barrel wants its PITCH axis (elevates). Try each to find it — RUNTIME-ONLY, Save (no bake) + relaunch."),
                    cur.turretAxis,
                    new[] { new GUIContent("Default (game aim)"), new GUIContent("Axis 0 (X)"), new GUIContent("Axis 1 (Y)"), new GUIContent("Axis 2 (Z)") },
                    new[] { -1, 0, 1, 2 });
            // GUN vs TURRET distinction (user design, 2026-08-06): a TURRET yaws at targets (Turret bone above —
            // and declares the vehicle turreted for category turn ease); a fixed GUN aims with the hull and only
            // ELEVATES. Name the gun FIRST, then tune its elevation (user: identifier above modifier).
            using (new EditorGUILayout.HorizontalScope())
            {
                cur.muzzleBone = EditorGUILayout.TextField(new GUIContent("Gun bone (flash + elevation)",
                    "THE GUN: bone-name SUBSTRING of the model's weapon — use Pick, or type e.g. 'Gun'. The muzzle flash " +
                    "fires from it (the donor's fire clip names ITS own socket, absent on our renamed rig, so the flash " +
                    "lands off-side without this), and Gun elevation raises it when no Turret bone is set. A fixed gun " +
                    "(casemate/assault gun) needs ONLY this + elevation — leave Turret bone empty. Empty = leave vanilla. " +
                    "RUNTIME-ONLY — Save (no bake) + relaunch. (Stored as muzzleBone in the registry.)"),
                    cur.muzzleBone ?? "");
                using (new EditorGUI.DisabledScope(animBonePrefixes.Count == 0))
                    if (GUILayout.Button(new GUIContent("Pick", animBonePrefixes.Count == 0 ? "No bones readable from this model (glb/gltf only) — type the name" : null), GUILayout.Width(70)))
                    {
                        var r = GUILayoutUtility.GetLastRect();
                        var labels = animBonePrefixes.Select(kv => $"{kv.Key}  ({kv.Value} part{(kv.Value == 1 ? "" : "s")})").ToArray();
                        var values = animBonePrefixes.Select(kv => kv.Key).ToArray();
                        new StringDropdown(new AdvancedDropdownState(), labels, values, "Gun bone", p => { cur.muzzleBone = p; Repaint(); }).Show(r);
                    }
            }
            cur.gunElevMax = EditorGUILayout.Slider(new GUIContent("Gun elevation — max (deg)",
                "Raise the gun barrel during a bombard, PROPORTIONAL TO TARGET DISTANCE up to this max angle (full at " +
                "~3 tiles): a short lob barely lifts, a max-range shot elevates fully — rising while the hull turns, held " +
                "through the shot, lowered after — ramping across a 1..8 TILE band, so a point-blank shot commands none of " +
                "the angle and an 8-tile shot all of it. POSITIVE raises the barrel. Applies to the Turret bone if " +
                "set, else the Gun bone below. 0 = off; " +
                "NEGATIVE if the gun pitches the wrong way — which direction a bone raises depends on the rig, so " +
                "if the barrel DROPS with range, flip the sign rather than the axis. " +
                "negative if the barrel dips the wrong way. RUNTIME-ONLY — Save (no bake) + relaunch."),
                cur.gunElevMax, -60f, 60f);
            // axis only exists once an elevation is set (same rule as the turret axis; != 0 because a NEGATIVE
            // max is a valid configuration — it flips the raise direction)
            if (cur.gunElevMax != 0f)
                cur.gunElevAxis = EditorGUILayout.IntPopup(new GUIContent("   Elevation axis",
                    "The gun bone's local PITCH axis (which axis raises the barrel). Usually X; try each if it swings sideways."),
                    cur.gunElevAxis,
                    new[] { new GUIContent("Axis 0 (X)"), new GUIContent("Axis 1 (Y)"), new GUIContent("Axis 2 (Z)") },
                    new[] { 0, 1, 2 });
            // THE TIMING OF THE MOVE, foldable so it stays out of the way once dialled — four timing fields under a
            // dial most models leave at 0 is a lot of permanent vertical space. All RUNTIME: Save, no bake.
            if (cur.gunElevMax != 0f)
            {
                EditorGUILayout.Space(2);
                elevTimingOpen = EditorGUILayout.Foldout(elevTimingOpen,
                    elevTimingOpen ? "   Elevation timing"
                                   : $"   Elevation timing  (rise {(cur.gunElevRise > 0.01f ? cur.gunElevRise.ToString("0.##") + "s" : "with the turn")} · " +
                                     $"hold {cur.gunElevHold:0.##}s · lower {cur.gunElevFall:0.##}s)", true);
                if (elevTimingOpen)
                {
                    cur.gunElevRise = EditorGUILayout.Slider(new GUIContent("      Raise over (s)",
                        "Seconds to raise to the firing angle. 0 = TRACK THE TURN (recommended): the gun finishes " +
                        "elevating exactly as it finishes slewing, so it arrives aimed and elevated together. A " +
                        "fixed time cannot do that — on a long turn the gun waits at full elevation, on a short one " +
                        "it is still climbing when the shot goes off. Set a value only for a deliberately slow, " +
                        "cranked-up look regardless of how far the unit had to turn.\n\nA value here also DELAYS THE SHOT: the " +
                        "strike waits for the gun to finish elevating, so the muzzle flash, the shell and the recoil " +
                        "all hold together rather than firing at a half-raised barrel. Capped at 3.5s by the " +
                        "failsafes that bound those holds."), cur.gunElevRise, 0f, 4f);
                    cur.gunElevHold = EditorGUILayout.Slider(new GUIContent("      Hold after firing (s)",
                        "Seconds the barrel stays at its firing angle after the shot, before easing back down. Must " +
                        "be LONGER than the attack clip or the gun starts dropping mid-recoil. 0 = start down the " +
                        "instant the shot ends."), cur.gunElevHold, 0f, 6f);
                    cur.gunElevFall = EditorGUILayout.Slider(new GUIContent("      Lower over (s)",
                        "Seconds to ease from the firing angle back to the resting elevation. Small values snap it " +
                        "down; large values look like the gun is being cranked back by hand."), cur.gunElevFall, 0.05f, 6f);
                }
            }
            // name the bone the elevation will actually move, so "how does it know what's the gun?" answers itself
            if (cur.gunElevMax != 0f)
            {
                bool viaTurret = !string.IsNullOrWhiteSpace(cur.turretBone);
                string gunBone = viaTurret ? cur.turretBone : cur.muzzleBone;
                EditorGUILayout.HelpBox(string.IsNullOrWhiteSpace(gunBone)
                    ? "No gun bone: set the Muzzle bone (fixed gun) or a Turret bone — elevation has nothing to move."
                    : $"Elevates bone '{gunBone}' (from the {(viaTurret ? "Turret bone" : "Gun bone")}).", MessageType.None);
            }
            // Donor sockets — the BAKE-TIME endgame for donor-anchored fire effects: bake EXACT-NAMED donor socket
            // bones onto our rig so the game's own lookups (flash, launch smoke, projectile origin) resolve natively.
            using (new EditorGUILayout.HorizontalScope())
            {
                cur.socketBones = EditorGUILayout.TextField(new GUIContent("Donor sockets (bake)",
                    "BAKE-TIME donor-socket bones: \"DonorName=OurBoneSubstr[@x,y,z];...\" — creates zero-weight bones with " +
                    "the EXACT names the donor's fire events ask for, parented to your bone so flash, smoke AND projectile " +
                    "origin all anchor natively and follow it (a tracking turret). Use Edit… for the guided mapping dialog " +
                    "(harvests the hardpoint names from the [Muzzle] log; picks bones from the model). Socketed models bake " +
                    "with the 'A###_' bone prefix (alphabetical topology). Needs a re-BAKE."),
                    cur.socketBones ?? "");
                if (GUILayout.Button(new GUIContent("Edit…", "Guided mapping: link the donor's logged hardpoints to your model's bones (with optional offsets) instead of hand-writing the spec."), GUILayout.Width(70)))
                    SocketBonesDialog.Open(cur.socketBones, cur.pawnDescription, animBoneNames, s => { cur.socketBones = s; Repaint(); });
            }
            // Muzzle offset — the runtime dial that finished the ArmouredCar: iterate value -> relaunch, no bake,
            // no mod rebuild (the plugin reads the pack registry directly at launch). BONE-LOCAL since 2026-08-06:
            // rotated by the gun bone's live rotation (aim + elevation), so a forward Z rides the barrel as the
            // hull turns. Three separate float fields in the UI; the registry keeps the plugin's "x,y,z" string.
            var muzzleOff = ParseCsvVec3(cur.muzzleOffset);
            var muzzleOffNew = EditorGUILayout.Vector3Field(new GUIContent("Muzzle offset (gun-local X,Y,Z)",
                "RUNTIME dial: offset added to the pinned fire origin (muzzle flash, tracer AND smoke), in the GUN BONE'S " +
                "OWN axes — it rotates with the aim and elevation, so once dialed it stays on the barrel end at every " +
                "facing. WHICH axis is 'along the barrel' depends on the rig's bone frames: Unity-convention rigs often " +
                "use Z, CONVERTED (Blender-style) rigs often X — dial one axis at a time and watch which way the flash " +
                "moves (the Jagdpanzer landed on X=2 forward, Y=1 up). Same warning for the Elevation axis: pick the " +
                "axis PERPENDICULAR to the barrel, or the 'elevation' is an invisible roll. All zero = off. " +
                "Save (no bake) + relaunch per iteration — no re-bake, no mod rebuild."),
                muzzleOff);
            if (muzzleOffNew != muzzleOff)
                cur.muzzleOffset = muzzleOffNew == Vector3.zero ? ""
                    : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", muzzleOffNew.x, muzzleOffNew.y, muzzleOffNew.z);
        }

        // --- Hand prop (runtime-only: Save (no bake) + rebuild the mod) ---
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Hand prop (weapon — runtime-only)", EditorStyles.miniBoldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            // Combobox over the baked props (every Assets/Resources/<name>_Collection.asset the Prop Lab produced):
            // picking one fills BOTH the name and the collection GUID — no clipboard round-trip. "(none)" clears.
            var propFiles = System.IO.Directory.Exists("Assets/Resources")
                ? System.IO.Directory.GetFiles("Assets/Resources", "*_Collection.asset")
                    .Select(p => System.IO.Path.GetFileName(p))
                    .Select(f => f.Substring(0, f.Length - "_Collection.asset".Length))
                    .OrderBy(n => n).ToArray()
                : new string[0];
            var options = new[] { "(none)" }.Concat(propFiles).ToArray();
            int curIdx = System.Array.IndexOf(options, string.IsNullOrEmpty(cur.handPropName) ? "(none)" : cur.handPropName);
            if (curIdx < 0) curIdx = 0;
            int pick = EditorGUILayout.Popup(new GUIContent("Hand prop",
                "A weapon glued to a bone of THIS model's skeleton at runtime. The list shows every prop baked in " +
                "Tools ▸ HAF ▸ Prop Lab (its <name>_Collection assets); picking one fills the collection GUID " +
                "automatically. Bake the weapon there first (e.g. 'M60'). '(none)' = no hand prop."),
                curIdx, options);
            if (pick != curIdx)
            {
                if (pick == 0) { cur.handPropName = ""; cur.handPropGuid = ""; }
                else
                {
                    cur.handPropName = options[pick];
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/Resources/" + options[pick] + "_Collection.asset");
                    cur.handPropGuid = asset != null ? PropBakerWindow.AmplitudeGuid(asset) : "";
                    if (string.IsNullOrEmpty(cur.handPropGuid))
                        Debug.LogWarning("[AnimLab] could not read the Amplitude GUID of " + options[pick] + "_Collection.asset — re-bake the prop in the Prop Lab.");
                }
            }
        }
        if (!string.IsNullOrEmpty(cur.handPropName) && string.IsNullOrEmpty(cur.handPropGuid))
            EditorGUILayout.HelpBox("No collection GUID for '" + cur.handPropName + "' — re-pick it (or re-bake the prop).", MessageType.Warning);
        if (!string.IsNullOrWhiteSpace(cur.handPropGuid))
        {
            cur.handPropBone = EditorGUILayout.TextField(new GUIContent("Bone substring",
                "Which bone of THIS model's skeleton the prop glues to — a case-insensitive SUBSTRING of the bone name " +
                "(the conversion renames bones to b###_<original>, so match the original part, e.g. 'R_Hand'). " +
                "Empty = 'R_Hand'."), cur.handPropBone ?? "");
            cur.handPropMat = EditorGUILayout.TextField(new GUIContent("Material GUID (borrowed)",
                "MaterialRef whose output layer the prop renders with — \"a,b,c,d\". Empty = the shared " +
                "EQ_DLC04_Weapons material (verified working for weapon props)."), cur.handPropMat ?? "");
            // NOTE: the prop's orientation is authored in the PROP LAB recipe (Rotation offset — baked vertices).
            // The registry still supports a per-model runtime override (`handPropAngles` "x,y,z", hand-editable in
            // haf_models.json): the plugin stamps it onto the FxMesh asset before encoding, making orientation
            // dial-in relaunch-only. Deliberately not exposed here to keep one owner per setting.
            if (GUILayout.Button(new GUIContent("Refresh fit preview (model + prop, as glued in-game)",
                "Rebuilds the combined preview below: the model's rig at rest (the idle stance) with the prop's SHIPPED " +
                "mesh parented to the glue bone using the exact runtime math. Press after any bake.")))
                BuildFitPreview();
            EditorGUI.BeginChangeCheck();
            fitAngles = EditorGUILayout.Vector3Field(new GUIContent("Prop rotation (LIVE, deg)",
                "Rotates the prop around its glue bone LIVE in the preview below — no bake, no relaunch. When it " +
                "looks right, press 'Save rotation to game': the value goes to the registry and the plugin applies " +
                "the SAME rotation at the SAME stage in-game (relaunch to see it; no re-bake, no mod rebuild)."), fitAngles);
            if (EditorGUI.EndChangeCheck() && fitDraws != null) BuildFitPreview();
            if (GUILayout.Button(new GUIContent("Save rotation to game",
                "Writes the live rotation above into the registry (handPropAngles) — the plugin stamps it at load. " +
                "Relaunch the game to see it; no bake or mod rebuild needed.")))
            {
                cur.handPropAngles = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.###},{1:0.###},{2:0.###}", fitAngles.x, fitAngles.y, fitAngles.z);
                SaveOnly();
            }
        }
        // Animate only bones — free text + a Pick that appends a bone-name prefix (grouped, with counts) from the model.
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.animateBones = EditorGUILayout.TextField(new GUIContent("Animate only bones",
                "Optional. Comma-separated bone-name PREFIXES to keep animation on — e.g. 'prop' keeps the spinning parts " +
                "and strips camera / body-bob curves that make the model wobble. Use Pick to add a prefix found in the model. " +
                "Leave EMPTY to keep the whole clip. The frame range is always auto-clamped (kills the ~1s per-loop stall)."), cur.animateBones ?? "");
            using (new EditorGUI.DisabledScope(animBonePrefixes.Count == 0))
                if (GUILayout.Button(new GUIContent("Pick", animBonePrefixes.Count == 0 ? "No bones readable from this model (glb/gltf only) — type prefixes" : null), GUILayout.Width(70)))
                {
                    var r = GUILayoutUtility.GetLastRect();
                    var labels = animBonePrefixes.Select(kv => $"{kv.Key}  ({kv.Value} part{(kv.Value == 1 ? "" : "s")})").ToArray();
                    var values = animBonePrefixes.Select(kv => kv.Key).ToArray();
                    new StringDropdown(new AdvancedDropdownState(), labels, values, "Bone prefixes", p =>
                    {
                        var set = (cur.animateBones ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                        if (!set.Contains(p)) set.Add(p);
                        cur.animateBones = string.Join(",", set);
                        Repaint();
                    }).Show(r);
                }
        }
        cur.staticParts = EditorGUILayout.TextField(new GUIContent("Static parts (no bone-bind)",
            "Optional, conversion path only. Comma-separated MESH- or MATERIAL-name substrings kept WEIGHTLESS — they ride " +
            "the root bone statically at their authored position instead of being bone-bound. For rigid decor hung off a " +
            "skeleton whose ANIMATED pose sits far from its static layout: the canoe's sail slats ('Cloth') bound to rib " +
            "bones 100+ units from rest, dragging the sail off the mast — static keeps it ON the mast (like the mast " +
            "itself, which is never bound). Empty = bind every rigidly-hung part (mech behaviour). Re-bake after changing."),
            cur.staticParts ?? "");
        cur.localNodeAnim = EditorGUILayout.Toggle(new GUIContent("Node animation → bones (local delta)",
            "Bake-time, conversion path only. Transplant OBJECT-level node animation (motion keyed on nodes, not " +
            "bones: a canoe's hull rock, log bob, paddle strokes, sail sway) into LOCAL-DELTA bones — one bone per " +
            "animated node at its STATIC placement, keyed with only that node's own wiggle. For models that fall " +
            "APART when the full hierarchy replays (animated pose frame ≠ static layout: the plain path freezes " +
            "them, deploy conversion disassembles them). Off = node animation is dropped (default). Re-bake after changing."),
            cur.localNodeAnim);
        cur.animUnitFix = EditorGUILayout.Toggle(new GUIContent("Fix 100× oversize (FBX unit scale)",
            "Bake-time scale fix for rigged exports that embed a metre→centimetre unit scale (bake ~100× too big, float " +
            "in the sky). PER-MODEL: if the model bakes huge/floating, tick it; if ticking makes it vanish, untick (the " +
            "drone bakes right OFF; the howitzer needs it ON). Re-bake after changing."),
            cur.animUnitFix);
        cur.convertRig = EditorGUILayout.Toggle(new GUIContent("Convert raw rig (auto-rigged models)",
            "Bake-time PIPELINE switch. ON = the raw-rig conversion: rest-normalize + visual rebake (for rigs whose clips " +
            "assemble the body with location keys), no-op root collapse, topological bone rename, clean-unit export — what " +
            "made the Combine soldier work. OFF = the byte-identical legacy pipeline for purpose-made rigs (drone, howitzer). " +
            "Tick it when a rig plays fine in the preview but tears apart / displaces in-game; usually paired with " +
            "Fix 100× OFF. Re-bake after changing."),
            cur.convertRig);
        cur.autoGroundWheels = EditorGUILayout.Toggle(new GUIContent("Auto-ground (sit on terrain)",
            "Sit a rigged VEHICLE on the terrain automatically — no manual Position-offset dial. The bake drops the " +
            "model's LOWEST point (the tyre contact) to the skeleton origin (the same keel→ground the static bake does). " +
            "Self-correcting, so re-baking never floats it. Tick it for a car/tank. Leave OFF for a flyer/hover model " +
            "(it would be pinned to the ground). Re-bake after changing."),
            cur.autoGroundWheels);
        cur.keepTranslations = EditorGUILayout.Toggle(new GUIContent("Keep bone translations",
            "CONVERSION path only: keep genuinely TRANSLATION-animated bone location curves through the bake — " +
            "the engine plays them (vanilla tank tread shuttle bones are translation-driven). Historically all " +
            "location keys were stripped (rotation-only); that stays the default. Tick only for models authored " +
            "with deliberate translating bones (tread shuttles, sliding parts). Re-bake after changing."),
            cur.keepTranslations);
        cur.animPhaseSpread = EditorGUILayout.Slider(new GUIContent("Per-instance offset",
            "Stops a multi-pawn unit moving as ONE BODY. Fed the same clip time, every pawn animates in lockstep — " +
            "twelve canoes rocking as a rigid raft, eight monsters swinging their heads in unison. This offsets each " +
            "pawn by a share of the clip. DEFAULT 0.5 (half the clip) desynchronises convincingly while the unit " +
            "still reads as one group; 1 spreads them over the whole clip; 0 restores lockstep. Each pawn keeps its " +
            "phase across LOD rebuilds, so zooming can't reset it. LOOPING animation only — attack/deploy/fire " +
            "one-shots stay tied to their trigger. RUNTIME-ONLY: Save (no bake) + relaunch, no re-bake."),
            cur.animPhaseSpread, 0f, 1f);
        if (!UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox("The animated bake needs Blender (to slim the rig + bake the clip) — it wasn't found. " +
                "Install Blender or set EditorPrefs 'ENC.blenderPath' to blender.exe.", MessageType.Warning);

        // --- Behavior (LEGACY single-clip runtime flags — superseded by the state machine and HIDDEN whenever
        //     State-driven is ON: the two are mutually exclusive, and dead sliders confuse. Full removal is staged
        //     for after the TowedGunHowitzers migration is verified in-game (the last user of this path). ---
        if (!cur.animStateDriven)
        {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Behavior (legacy single-clip)", EditorStyles.miniBoldLabel);
        cur.fireOnAttack = EditorGUILayout.Toggle(new GUIContent("Fire on attack (play once)",
            "Play the baked clip ONCE when this unit attacks, instead of looping — the model rests, then plays a single pass " +
            "on the shot and returns to rest (e.g. a howitzer barrel that elevates only when it bombards). AUTHOR THE CLIP TO " +
            "START AND END AT REST. Leave OFF for a continuous loop (a drone's spinning prop)."),
            cur.fireOnAttack);
        cur.deployOnStop = EditorGUILayout.Toggle(new GUIContent("Deploy when stopped",
            "Hold the baked clip's DEPLOYED pose while idle, and snap to the UNDEPLOYED pose (frame 0) the instant it moves — " +
            "e.g. a howitzer that deploys its barrel/trails when it stops and folds them for travel. AUTHOR THE CLIP so frame 0 " +
            "= travelling and the deployed pose sits at 'Deployed pose time'."),
            cur.deployOnStop);
        using (new EditorGUI.DisabledScope(!cur.deployOnStop))
            cur.deployPoseTime = EditorGUILayout.Slider(new GUIContent("Deployed pose time",
                "Normalized clip time (0..1) of the DEPLOYED pose held when idle. 1 = a purpose-made deploy clip's end frame."),
                cur.deployPoseTime <= 0f ? 1f : cur.deployPoseTime, 0f, 1f);
        using (new EditorGUI.DisabledScope(!cur.deployOnStop))
            cur.deploySpeed = EditorGUILayout.Slider(new GUIContent("Deploy speed",
                "Speed multiplier on the gradual deploy-on-stop ramp: 1 = the clip's authored speed. Folding on move is always instant."),
                cur.deploySpeed <= 0f ? 1f : cur.deploySpeed, 0.25f, 5f);
        using (new EditorGUI.DisabledScope(!cur.deployOnStop || !cur.fireOnAttack))
            cur.recoilSpeed = EditorGUILayout.Slider(new GUIContent("Recoil speed",
                "Speed multiplier on the recoil-on-fire kickback (needs Deploy-when-stopped + Fire-on-attack): 1 = the clip " +
                "tail's authored speed."),
                cur.recoilSpeed <= 0f ? 1f : cur.recoilSpeed, 0.25f, 8f);
        }

        // --- Bake / Save ---
        EditorGUILayout.Space();
        // BAKE LOCK: protects an in-game-VERIFIED bake from accidental regeneration when the shared tooling has
        // moved on underneath it (the m114 after the 2026-07-26 engine-contract rework). While locked, Bake is
        // disabled in BOTH windows; unlocking is deliberate: untick -> bake -> RE-VERIFY IN-GAME.
        cur.bakeLocked = EditorGUILayout.ToggleLeft(new GUIContent("Lock bake (verified entry — protect from rebake)",
            "While ticked, the Bake button is disabled for this entry. Use for entries whose baked assets are in-game " +
            "verified and must not be regenerated casually (tool changes can alter the output). Save (no bake) records the lock."),
            cur.bakeLocked);
        bool hasBaked = cur.skel != null && cur.skel.Length == 4 && !(cur.skel[0] == 0 && cur.skel[1] == 0 && cur.skel[2] == 0 && cur.skel[3] == 0);
        bool canBake = hasModel && !string.IsNullOrWhiteSpace(cur.pawnDescription)
                    && (hasBaked || !string.IsNullOrWhiteSpace(cur.modelFile));
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!canBake || cur.bakeLocked))
                if (GUILayout.Button(new GUIContent(cur.bakeLocked ? "Bake (locked)" : "Bake",
                    cur.bakeLocked ? "This entry is bake-locked (verified assets). Untick 'Lock bake' above to deliberately rebake — then re-verify in-game."
                                   : "Re-run the animated pipeline (Blender slim + skeleton + clip + atlas) with the settings above, then save the registry entry."), GUILayout.Height(34))) DoBake();
            using (new EditorGUI.DisabledScope(!hasBaked || !hasModel))
                if (GUILayout.Button(new GUIContent("Save (no bake)",
                    "Write the registry entry with the current settings WITHOUT re-baking assets — enough for the runtime " +
                    "Behavior flags/sliders (the clip fields need a real Bake). Relaunch the game to see it."),
                    GUILayout.Height(34), GUILayout.Width(110)))
                    SaveOnly();
            if (GUILayout.Button("Reset", GUILayout.Height(34), GUILayout.Width(72)))
            { cur = new ModelDef { animated = true }; selected = 0; status = ""; GUI.FocusControl(null); }
        }
        if (!canBake && hasModel)
            EditorGUILayout.HelpBox("This entry has no baked assets and no model file — open it in the Model Factory to set the file, then bake.", MessageType.Warning);
        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
        EditorGUILayout.HelpBox("Registry (source): " + ModelRegistry.SourcePath + "  ·  deploys to the BepInEx artifact on every Save", MessageType.None);
        // --- FIT PREVIEW (model + hand prop, glued as in-game; own camera => real zoom) ---
        if (fitDraws != null)
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    (string.IsNullOrEmpty((cur.handPropGuid ?? "").Trim())
                        ? "Model preview  (drag = orbit · middle/right-drag = pan · scroll = zoom"
                        : "Fit preview — model + hand prop  (drag = orbit · middle/right-drag = pan · scroll = zoom")
                    + (fitGrounded ? " · arrow = forward)" : ")")
                    + (cur != null && cur.position != Vector3.zero ? "  · Position offset shown LIVE (runtime-applied, no bake needed)" : ""),
                    EditorStyles.miniBoldLabel);
                if (fitGrounded)
                {
                    fitRefMan = GUILayout.Toggle(fitRefMan, new GUIContent("Ref man",
                        $"Draw a human figure at in-game pawn height ({ModelFactoryWindow.HumanRefHeight:0.0}u) beside the model — the size reference (shared with the Factory preview)."), GUILayout.Width(66));
                    if (fitRefMan)
                    {
                        fitRefManPos.x = EditorGUILayout.FloatField(fitRefManPos.x, GUILayout.Width(38));
                        fitRefManPos.y = EditorGUILayout.FloatField(fitRefManPos.y, GUILayout.Width(38));
                    }
                }
                if (GUILayout.Button(new GUIContent("Center", "Re-center the view on the model (resets pan + zoom; keeps the orbit angle)"), GUILayout.Width(60)))
                { fitPan = Vector2.zero; fitZoom = 1.4f; Repaint(); }
            }
            // --- PLAY THE CLIP: pick a baked role clip and watch it run, TEXTURED, in this same view. The raw-model
            // ▶ picker scrubs the SOURCE untextured (where a wheel's rotation is invisible — user report); this plays
            // what was actually baked. Restored after a domain reload at Layout, when `cur` is loaded again.
            var roles = FitRoles();
            if (roles.Count > 0)
            {
                if (fitAnimInst == null && !string.IsNullOrEmpty(fitAnimRole) && Event.current.type == EventType.Layout)
                    BuildAnimPreview(fitAnimRole);
                // THIS WINDOW RUNS A ~240px labelWidth (set once at the top for the settings columns). A Popup whose
                // total Width is 240 then has NOTHING left for its field: it drew as an empty box with a bare arrow
                // ("how do I play the clip?" — user, 2026-08-22). Narrow the label for this row, restore it after.
                float lwRow = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 96;
                using (new EditorGUILayout.HorizontalScope())
                {
                    var labels = new List<string> { "Rest pose (no clip)" };
                    foreach (var r in roles) labels.Add("▶ " + r.label);
                    int idx = string.IsNullOrEmpty(fitAnimRole) ? 0 : roles.FindIndex(r => r.dir == fitAnimRole) + 1;
                    if (idx < 0) idx = 0;
                    // "Play in preview", not "Play clip": the Model-file row's ▶ Play clip button opens the RAW-source
                    // scrubber — two different things, and the shared name is half of why this one wasn't found.
                    int pick = EditorGUILayout.Popup(new GUIContent("Play in preview",
                        "Play a BAKED role clip here, textured and skinned — the honest preview of what the game will play " +
                        "(the Model-file row's ▶ Play clip scrubs the untextured RAW source, where a rolling wheel is invisible). " +
                        "Re-Bake to refresh after changing a clip range."), idx, labels.ToArray(), GUILayout.Width(300));
                    if (pick != idx) BuildAnimPreview(pick == 0 ? null : roles[pick - 1].dir);
                    using (new EditorGUI.DisabledScope(fitAnimInst == null || fitAnimClip == null || fitAnimClip.length <= 0.01f))
                    {
                        if (GUILayout.Button(new GUIContent(fitAnimPlaying ? "Pause" : "Play", "Freeze the clip to inspect a pose"), GUILayout.Width(52)))
                        { fitAnimPlaying = !fitAnimPlaying; fitAnimTick = EditorApplication.timeSinceStartup; Repaint(); }
                        float len = fitAnimClip != null ? Mathf.Max(fitAnimClip.length, 0.01f) : 1f;
                        float t = GUILayout.HorizontalSlider(Mathf.Clamp(fitAnimT, 0f, len), 0f, len, GUILayout.MinWidth(60));
                        if (!Mathf.Approximately(t, fitAnimT)) { fitAnimT = t; fitAnimPlaying = false; Repaint(); }   // scrubbing pauses — that's what scrubbing is for
                        GUILayout.Label(fitAnimClip != null ? $"{fitAnimT:0.00}/{len:0.00}s" : "—", GUILayout.Width(76));
                        float lw = EditorGUIUtility.labelWidth; EditorGUIUtility.labelWidth = 42;
                        fitAnimSpeed = EditorGUILayout.Slider(new GUIContent("Speed", "Playback speed — slow a 0.5 s wheel loop down to judge the roll"), fitAnimSpeed, 0.1f, 2f, GUILayout.Width(130));
                        EditorGUIUtility.labelWidth = lw;
                    }
                }
                EditorGUIUtility.labelWidth = lwRow;
            }
            var rect = GUILayoutUtility.GetRect(200, 360, GUILayout.ExpandWidth(true));
            DrawFitPreview(rect);
        }
        EditorGUILayout.EndScrollView();
    }

    // ENFORCED OWNERSHIP: a bake/save from this window rebases onto the FRESHEST registry entry and applies only the
    // ANIMATION-owned fields from this form. Without this, whichever window held a stale copy silently clobbered the
    // other's work at bake time (cost three bakes on the Combine soldier: the Factory bake lost the Lab's Fix-100×,
    // then the Lab bake lost the Factory's rotation AND size). Model-owned fields (rotation, position, size, tris,
    // material, shading, …) always come from the registry — this window can't even display them, so it must not
    // write its stale copies of them either. No-op for a brand-new entry (nothing saved yet to rebase on).
    void RebaseOnRegistry()
    {
        var reg = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == (cur.resourceName ?? "").Trim());
        if (reg == null) return;
        var mine = cur;
        cur = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(reg));
        cur.animated = true;
        // The Lab has its OWN Model-file Browse (to repoint a moved file, or to swap a static export for the rigged one).
        // Without preserving it here, RebaseOnRegistry silently reverts modelFile to the registry's value at bake time —
        // so Browsing to the animated GLB looked like it worked (the clip picker reads the live field) but the bake fed
        // Blender the OLD file and failed with "no clip". Keep the Lab's chosen file so Browse actually sticks.
        if (!string.IsNullOrWhiteSpace(mine.modelFile)) cur.modelFile = mine.modelFile;
        cur.animClip = mine.animClip; cur.animateBones = mine.animateBones; cur.staticParts = mine.staticParts; cur.localNodeAnim = mine.localNodeAnim; cur.animUnitFix = mine.animUnitFix;
        cur.convertRig = mine.convertRig; cur.autoGroundWheels = mine.autoGroundWheels; cur.keepTranslations = mine.keepTranslations;
        cur.deployConvert = mine.deployConvert; cur.deployStart = mine.deployStart; cur.deployEnd = mine.deployEnd;
        cur.deployStrip = mine.deployStrip; cur.deployStripExtra = mine.deployStripExtra; cur.deployReadyFrame = mine.deployReadyFrame; cur.deployLegScale = mine.deployLegScale; cur.deployBarrelScale = mine.deployBarrelScale;
        cur.deployWheelBones = mine.deployWheelBones; cur.deployWheelAxis = mine.deployWheelAxis; cur.deployWheelFrames = mine.deployWheelFrames; cur.deployWheelDegrees = mine.deployWheelDegrees;
        cur.deployRecoil = mine.deployRecoil; cur.deployRecoilStep = mine.deployRecoilStep; cur.deployRecoilMag = mine.deployRecoilMag; cur.deployArcR = mine.deployArcR; cur.deployRecoilReturn = mine.deployRecoilReturn; cur.deploySlamDeg = mine.deploySlamDeg; cur.deploySlamSettle = mine.deploySlamSettle;
        cur.animStateDriven = mine.animStateDriven; cur.animClipMove = mine.animClipMove; cur.animClipAfter = mine.animClipAfter; cur.animClipAttack = mine.animClipAttack; cur.animClipCombat = mine.animClipCombat; cur.animClipPreMove = mine.animClipPreMove; cur.animClipIdle = mine.animClipIdle; cur.animClipIdleAlt = mine.animClipIdleAlt; cur.animClipIdleAlt2 = mine.animClipIdleAlt2; cur.idleAltInterval = mine.idleAltInterval; cur.animPhaseSpread = mine.animPhaseSpread; cur.attackRepeats = mine.attackRepeats; cur.clearAimLayer = mine.clearAimLayer; cur.turretBone = mine.turretBone; cur.turretAxis = mine.turretAxis; cur.gunElevMax = mine.gunElevMax; cur.gunElevAxis = mine.gunElevAxis; cur.gunElevRise = mine.gunElevRise; cur.gunElevHold = mine.gunElevHold; cur.gunElevFall = mine.gunElevFall; cur.muzzleBone = mine.muzzleBone; cur.muzzleOffset = mine.muzzleOffset; cur.socketBones = mine.socketBones; cur.disabled = mine.disabled; cur.bakeLocked = mine.bakeLocked;
        cur.handPropName = mine.handPropName; cur.handPropGuid = mine.handPropGuid; cur.handPropMat = mine.handPropMat; cur.handPropBone = mine.handPropBone;
        cur.handPropAngles = mine.handPropAngles;   // Lab-owned again since the LIVE fit knob edits it ('Save rotation to game')
        cur.fireOnAttack = mine.fireOnAttack; cur.deployOnStop = mine.deployOnStop;
        cur.deployPoseTime = mine.deployPoseTime; cur.deploySpeed = mine.deploySpeed; cur.recoilSpeed = mine.recoilSpeed;
    }

    // Persist runtime-only tweaks without touching the baked assets: the entry keeps its existing skeleton/clip/atlas
    // GUIDs (loaded with the entry), so the plugin re-reads the new settings on the next game launch.
    void SaveOnly()
    {
        RebaseOnRegistry();
        cur.animated = true;
        cur.resourceName = (cur.resourceName ?? "").Trim();
        cur.pawnDescription = (cur.pawnDescription ?? "").Trim();
        bool saved = ModelRegistry.Upsert(cur);
        if (saved) formDiffersFromRegistry = false;   // form is now the saved truth
        RefreshList();
        status = saved
            ? $"Saved '{cur.resourceName}' (registry only, assets untouched). Relaunch the game — no re-bake, no mod rebuild."
            : "REGISTRY SAVE FAILED (see Console). Close whatever's locking haf_models.json and retry.";
        Debug.Log("[AnimLab] " + status);
    }

    // Same flow as ModelFactoryWindow.DoBake, scoped to the animated path: trim -> ConfigFor -> BuildAnimated ->
    // capture the baked GUIDs onto the entry -> Upsert to the registry.
    void DoBake()
    {
        // Snapshot the form FIRST so a FAILED bake leaves every setting exactly as you had it. RebaseOnRegistry + the
        // trims below mutate cur in place (pulling model-owned fields back from the registry), so without this a bake
        // that errors out reverts the form and you re-enter the whole config — the annoyance this fixes.
        string formSnapshot = JsonUtility.ToJson(cur);
        ModelFactoryWindow.ReleasePreviews();   // the bake rewrites the preview prefab — a live Factory preview watching it throws from Unity internals
        InvalidateFitPreviews();                // the combined fit view would go stale (magenta) — retire it; Refresh rebuilds from fresh assets
        RebaseOnRegistry();   // bake with the freshest model-owned fields (rotation/size/…) — only animation fields are ours
        cur.animated = true;
        cur.resourceName = (cur.resourceName ?? "").Trim();
        cur.pawnDescription = (cur.pawnDescription ?? "").Trim();
        cur.modelFile = (cur.modelFile ?? "").Trim();
        cur.animClip = (cur.animClip ?? "").Trim();
        cur.animateBones = (cur.animateBones ?? "").Trim();
        cur.staticParts = (cur.staticParts ?? "").Trim();
        cur.animClipMove = (cur.animClipMove ?? "").Trim();
        cur.animClipAfter = (cur.animClipAfter ?? "").Trim();
        cur.animClipAttack = (cur.animClipAttack ?? "").Trim();
        cur.animClipCombat = (cur.animClipCombat ?? "").Trim();
        cur.animClipPreMove = (cur.animClipPreMove ?? "").Trim();
        cur.animClipIdle = (cur.animClipIdle ?? "").Trim();
        cur.animClipIdleAlt = (cur.animClipIdleAlt ?? "").Trim();
        cur.animClipIdleAlt2 = (cur.animClipIdleAlt2 ?? "").Trim();
        var cfg = ModelFactoryWindow.ConfigFor(cur);
        // Geometry caching is AUTOMATIC (mirror of the Factory's DoBake): re-slim exactly when a Blender-step input
        // changed; the 'Reuse extracted' checkbox only protects the hand-edited extracted texture (cfg.keepTexture).
        cfg.reuseExtracted = !ModelFactoryWindow.AnimatedSlimInputsChanged(cur);
        if (!cfg.reuseExtracted) Debug.Log("[AnimLab] " + cur.resourceName + ": Blender-step settings changed — re-slimming (automatic).");
        var r = UniversalBaker.BuildAnimated(cfg);
        if (!r.ok) { cur = JsonUtility.FromJson<ModelDef>(formSnapshot); status = "Bake FAILED (settings kept): " + r.error; Debug.LogError("[AnimLab] " + r.error); return; }
        cur.skel = ModelRegistry.ParseGuid(r.skeletonGuid);
        cur.atlas = ModelRegistry.ParseGuid(r.atlasGuid);
        cur.clip = ModelRegistry.ParseGuid(r.clipGuid);
        cur.clipMove = cur.animStateDriven ? ModelRegistry.ParseGuid(r.clipMoveGuid) : new int[4];
        cur.clipAfter = cur.animStateDriven && !string.IsNullOrEmpty(r.clipAfterGuid) ? ModelRegistry.ParseGuid(r.clipAfterGuid) : new int[4];
        cur.clipAttack = cur.animStateDriven && !string.IsNullOrEmpty(r.clipAttackGuid) ? ModelRegistry.ParseGuid(r.clipAttackGuid) : new int[4];
        cur.clipCombat = cur.animStateDriven && !string.IsNullOrEmpty(r.clipCombatGuid) ? ModelRegistry.ParseGuid(r.clipCombatGuid) : new int[4];
        cur.clipPreMove = cur.animStateDriven && !string.IsNullOrEmpty(r.clipPreMoveGuid) ? ModelRegistry.ParseGuid(r.clipPreMoveGuid) : new int[4];
        cur.clipIdle = cur.animStateDriven && !string.IsNullOrEmpty(r.clipIdleGuid) ? ModelRegistry.ParseGuid(r.clipIdleGuid) : new int[4];
        cur.clipIdleAlt = cur.animStateDriven && !string.IsNullOrEmpty(r.clipIdleAltGuid) ? ModelRegistry.ParseGuid(r.clipIdleAltGuid) : new int[4];
        cur.clipIdleAlt2 = cur.animStateDriven && !string.IsNullOrEmpty(r.clipIdleAlt2Guid) ? ModelRegistry.ParseGuid(r.clipIdleAlt2Guid) : new int[4];
        bool saved = ModelRegistry.Upsert(cur);
        if (saved) formDiffersFromRegistry = false;   // form is now the saved truth
        RefreshList();
        ModelFactoryWindow.ReloadPreviews();   // give the Factory tab its preview back (fresh from this bake)
        BuildFitPreview();                     // and OUR preview: combined (hand prop) or plain model — fresh from this bake, never stale
        status = saved
            ? $"Baked ANIMATED '{cur.resourceName}' -> '{cur.pawnDescription}'\nskeleton {r.skeletonGuid}\nclip {r.clipGuid}{(cur.animStateDriven ? $"\nmove clip {r.clipMoveGuid}{(string.IsNullOrEmpty(r.clipAfterGuid) ? "" : $"  after clip {r.clipAfterGuid}")}{(string.IsNullOrEmpty(r.clipAttackGuid) ? "" : $"  attack clip {r.clipAttackGuid}")}{(string.IsNullOrEmpty(r.clipCombatGuid) ? "" : $"  combat clip {r.clipCombatGuid}")}" : "")}\nRebuild the mod + relaunch."
            : $"Baked '{cur.resourceName}', but the REGISTRY SAVE FAILED (see Console). Close whatever's locking haf_models.json and re-bake.";
        Debug.Log("[AnimLab] " + status);
    }

    // Parse a registry "x,y,z" CSV (invariant culture) into a Vector3; anything malformed = zero. Used by the
    // muzzle-offset triple-field UI, which stores back the same CSV so the plugin's reader is untouched.
    static Vector3 ParseCsvVec3(string s)
    {
        var p = (s ?? "").Split(',');
        if (p.Length != 3) return Vector3.zero;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return float.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out var x)
            && float.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out var y)
            && float.TryParse(p[2], System.Globalization.NumberStyles.Float, inv, out var z)
            ? new Vector3(x, y, z) : Vector3.zero;
    }
}
