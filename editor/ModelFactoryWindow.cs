// ModelFactoryWindow.cs (ENC editor) — Tools > Model Factory.
// Create a NEW 3D resource or pick an existing one, choose a target pawn description + a model file (.glb/.obj/.fbx),
// and configure EVERYTHING we learned makes a model work: rotation, position (z = waterline), size, normals mode,
// smoothing angle, conversion grid. Press Bake -> skeleton + atlas + a JSON registry entry the in-game plugin reads.

using System;
using System.Collections.Generic;
using System.IO;                            // remove-undo snapshot (2026-08-17)
using System.Linq;
using Newtonsoft.Json.Linq;                 // SDK-provided (mod.io) — robust glTF parse for the Clip/Bone pickers
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class ModelFactoryWindow : EditorWindow
{
    // [SerializeField] so Unity preserves the form across a DOMAIN RELOAD (any script recompile, entering/exiting Play
    // mode, etc.). Without it these are wiped back to defaults mid-edit — the "fields went empty on their own" bug.
    // (ModelDef is [Serializable], so the whole edited entry round-trips.)
    [SerializeField] ModelDef cur = new ModelDef();
    [SerializeField] int selected;      // 0 = <New>, else index into `existing`
    string[] existing = { "<New>" };
    string status = "";
    Vector2 scroll;
    Vector2 stripScroll;                // scroll position of the multi-line Strip-parts text area
    GUIStyle wrapArea;                  // cached word-wrapping text-area style (lazy-init in OnGUI; EditorStyles isn't ready earlier)
    bool showSettings;
    // Custom PreviewRenderUtility renderer (ported from the Animation Lab): Unity's built-in GameObject preview has NO
    // scroll-zoom AND the window's outer scroll view steals the wheel, so "scroll to zoom" never worked. Owning the
    // camera gives real orbit + zoom. The non-serializable PRU is lazily created and cleaned before every domain reload.
    PreviewRenderUtility previewPRU;
    List<(Mesh mesh, Material[] mats, Matrix4x4 mtx)> previewDraws;   // flattened draw list from the baked prefab
    Bounds previewBounds;
    [SerializeField] Vector2 previewOrbit = new Vector2(135f, -20f);  // yaw/pitch (deg); survives domain reload
    [SerializeField] float previewZoom = 1.4f;                        // camera distance factor (scroll wheel)
    [SerializeField] Vector2 previewPan;                              // camera-plane pan offset (middle/right-drag), in radius units
    static Material previewFallbackMat;
    string previewFor = "";
    [SerializeField] string lastRemovedName = "", lastRemovedSnap = "";   // recycle-bin undo state (survives domain reload)

    // THE SELECTION FUNNEL (entry-state coherence slice 2, 2026-08-18): every change of "which entry does this
    // window show" routes through here, so dropdown + form + preview + coherence flag can never desynchronize
    // again — the 08-16..18 stale-window family (sel-reset over a clone, preview-after-remove, blank-after-undo,
    // stale-dropdown-after-restore) were each ONE forgotten surface at ONE bypassing call site. The ONE deliberate
    // bypass: Clone, whose unsaved form owns the window (routing it through the funnel would wipe the clone).
    void SelectEntry(int idx)
    {
        selected = idx > 0 && idx < existing.Length ? idx : 0;
        OnSelectResource();   // loads the entry or resets to <New>; clears the coherence flag; loads/clears the preview
        GUI.FocusControl(null);
    }
    void SelectEntry(string name) { RefreshList(); SelectEntry(Array.IndexOf(existing, (name ?? "").Trim())); }

    // Form-vs-registry comparison shared by the domain-reload reconciliation and the cross-window nudge.
    // Keyed on `loadedName` — the form's OWN registry identity — NEVER on the `selected` dropdown index:
    // RefreshList() re-derives that index by name and resets it to 0 when the entry vanished from the registry,
    // which made the old `selected <= 0` guard swallow EXACTLY the vanished-entry case rule (b) exists for
    // (2026-08-18 drill 3: Bears hand-removed from pack.json, no banner). Two more lessons folded in
    // (self-review, 2026-08-18): (a) the Lab's spurious-banner lesson — OnGUI mutates the form (the `animated`
    // self-heal), so the SAME normalization must hit the registry copy before comparing, or the banner fires
    // after every compile with no user edit and gets ignored; (b) an entry REMOVED from the registry while
    // shown here is the STRONGEST desync — report it as differing, don't silently shrug.
    bool ComputeFormDiffers()
    {
        var name = (loadedName ?? "").Trim();
        if (name.Length == 0) return false;   // <New>/clone form — no saved registry identity to drift from
        var reg = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == name);
        if (reg == null) return true;   // the shown entry no longer exists in the registry — maximal difference
        if (!reg.animated && LooksAnimated(reg)) reg.animated = true;   // mirror the OnGUI self-heal (see (a))
        return JsonUtility.ToJson(reg) != JsonUtility.ToJson(cur);
    }

    // Cross-window nudge (2026-08-17 drill: "I pressed restore and it still doesn't work!!!" — the restore HAD
    // worked; the Factory's dropdown was stale because another window changed the registry). Any window that
    // writes the registry calls this so every open Factory re-reads and repaints immediately. COHERENCE-AWARE
    // (slice 2): it never silently reloads the form — if the nudged window's form now differs from the registry,
    // the yellow banner appears and the USER chooses, exactly like after a domain reload.
    internal static void RefreshAllOpen()
    {
        foreach (var w in Resources.FindObjectsOfTypeAll<ModelFactoryWindow>())
            try
            {
                w.RefreshList();
                if (w.ComputeFormDiffers()) w.formDiffersFromRegistry = true;
                w.Repaint();
            }
            catch { }
    }

    // Restore the last-removed model — ONE shared implementation with the Backup window's _removed_-row Restore
    // (BackupWindow.RestoreRemovedSnapshot: baked files back additively, registry entry via Upsert). This wrapper
    // adds the Factory-side proof: select + LOAD the restored entry (drill: "I expect it to be selected again").
    void UndoRemove()
    {
        status = BackupWindow.RestoreRemovedSnapshot(lastRemovedSnap, out var restoredName);
        RefreshList();
        if (!string.IsNullOrEmpty(restoredName))
        {
            SelectEntry(restoredName);   // the funnel: dropdown + form + preview + coherence flag, atomically
            lastRemovedName = ""; lastRemovedSnap = "";
        }
    }
    // GROUND REFERENCE (ported back from the District Factory pane, user request): a tile-sized square pinned at the
    // ORIGIN plane — the static bake grounds the keel to the origin and Position offset moves the model relative to it,
    // so this square IS the in-game ground truth. Never anchor it to the model's bounds (that HID a half-sunk district
    // bake); a model below the square previews sunk because it ships sunk.
    // GAME WATER LEVEL vs model origin (2026-08-18 submarine/cruiser calibration): the game floats naval pawns
    // with the water surface ABOVE the model origin — the preview's plane at origin height showed every vessel
    // riding ~half a unit too high vs the in-game waterline (cruiser evidence: preview calibrated to the
    // red/black paint boundary, game water at the TOP of the black band). Measured empirically against the
    // user's calibrated cruiser, like the 6.93u tile spacing — the dial next to the preview refines it; stored
    // in EditorPrefs so the calibration is done ONCE. Ground (land) keeps the origin contract untouched.
    // THE HAF WATER STANDARD (2026-08-18): mean water ~0.05 + ~0.11 wave allowance, verified in-game (cruiser
    // paint line, submarine deck-awash). Every vessel's registry Z is calibrated against it. Lives in the PACK
    // CONFIGURATION (RegistryFile.waterLevel — versioned, dual-written, backed up; user call after the dial
    // experiment: one source of truth, unmodifiable from the UI, part of HAF configuration). Refreshed by every
    // registry Load(); changing it is a deliberate pack.json edit + recalibration of every vessel's Z.
    static float PreviewWaterY => ModelRegistry.WaterLevel;
    float PreviewPlaneY => previewWater ? PreviewWaterY : -0.02f;

    Material previewGroundMat;
    Mesh previewGroundMesh;
    Mesh previewArrowMesh;   // flat FORWARD arrow on the square (+Z = the game's forward: yaw 0 faces it, a
                             // projectile's mesh-Z welds to its velocity) — the direction to dial the model toward
    bool previewGrounded;   // only the STATIC _Model.prefab preview is in game space; the animated FactorySource
                            // preview is a display-flipped bind pose (tanks stand on their tail) — a ground square
                            // there would be a LIE, so it only draws when this is set
    bool previewWater;      // target pawn is a BOAT (AnimationCapabilityProfile 7 — the game's own characteristic,
                            // the same signal the runtime classifies ships by; never by name) — square renders water-blue

    // Cheap animation probe (no Blender), cached per model-file path. State: 0 = unknown (allow), 1 = animation
    // detected (allow + hint), 2 = definitely none (disable the Animated toggle). Keeps the checkbox from being ticked
    // on a static model. Runs once when the path changes, not every OnGUI frame.
    string animProbeFile = "";   // sentinel != any real path so the first real path always probes
    int animProbeState;
    List<string> animClips = new List<string>();                                   // clip names read from the model (Clip picker)
    List<KeyValuePair<string, int>> animBonePrefixes = new List<KeyValuePair<string, int>>();  // bone-name prefix -> count (Bones picker)

    [MenuItem("Tools/HAF/Model Factory")]
    static void Open()
    {
        var w = GetWindow<ModelFactoryWindow>(false, "Model Factory");
        w.minSize = new Vector2(500, 470);
        w.RefreshList();
    }

    [SerializeField] bool formDiffersFromRegistry;   // ENTRY-STATE COHERENCE (2026-08-18, backlog fix #1): the Lab's
                                                     // domain-reload reconciliation, ported — the Factory was the one
                                                     // window WITHOUT it ("external registry edits are detected by the
                                                     // Lab (yellow banner) but not by the Factory").
    [SerializeField] string loadedName = "";         // which registry entry this form was loaded from / last saved as
    // Recomputed every OnGUI in the collision block below: the form's Resource name already belongs to a DIFFERENT
    // saved entry. Blocks Bake and Save — Upsert is a blind replace, so allowing it would destroy that entry.
    bool nameCollides;
                                                     // ("" = <New>/clone). The coherence compare keys on THIS, because
                                                     // the `selected` index is re-derived by name on every RefreshList
                                                     // and resets to 0 when the entry vanishes — the one case the
                                                     // banner most needs to catch (drill 3 finding, 2026-08-18).
    [SerializeField] bool previewCombat;             // "In combat" preview toggle (2026-08-19): draw the model at its
                                                     // battle-locked height (combatZ applied) — the calibration view
                                                     // for "only the periscope above the water"
    [SerializeField] bool previewRefMan;             // "Ref man" toggle (2026-08-19): a human-pawn-height figure beside
                                                     // the model — the size reference (see HumanRefHeight)
    [SerializeField] Vector2 previewRefManPos = new Vector2(1.5f, 0f);   // his spot on the plane (X sideways, Z fore/aft) — user-dialed
    [SerializeField] bool previewRuler;              // measuring stick (0.5u ticks, 3u tall) at the model's left
    Mesh previewRefManMesh, previewRulerMesh;
    bool bakedNotShipped;                            // SHIP STATUS inline flag (user request 2026-08-18): this entry's
                                                     // baked outputs are newer than the last mod build — the game still
                                                     // loads the previous assets. Recomputed at the same trigger points
                                                     // as the coherence flag (never per-OnGUI-frame: it's file I/O).

    void OnEnable()
    {
        titleContent = new GUIContent("Model Factory");   // rename any pre-existing docked instance: Unity caches the tab title in the window's serialized state, so the GetWindow title alone never reaches already-open windows
        EditorPrefs.DeleteKey("HAF.Preview.WaterY");      // retired 2026-08-18 (same day it was added): the water level is the WaterStandard code constant now — delete the machine-local copy so no zombie value can ever shadow the standard
        RefreshList(); LoadPreview(cur.resourceName);
        // DOMAIN-RELOAD RECONCILIATION (mirror of AnimationLabWindow.OnEnable v2): the form's serialized state
        // survives the reload untouched; if it differs from the saved registry entry, a warning banner appears with
        // an explicit choice — never a silent resync in either direction.
        formDiffersFromRegistry = ComputeFormDiffers();
        bakedNotShipped = ShipStatus.IsBakedNotShipped(loadedName);
        // Reload-reconciliation evidence line (drill 3 debugging, 2026-08-18): one log per domain reload, so a
        // missing banner is diagnosable from the Console (identity empty? compare said equal?) instead of guessed at.
        Debug.Log($"[Factory] coherence after reload: loadedName='{loadedName}' differs={formDiffersFromRegistry}");
        // Clean the PreviewRenderUtility BEFORE the domain unloads (and before editor quit): a PRU that survives a
        // domain reload leaks its camera/scene and spams errors. Cleaning at beforeAssemblyReload, while everything is
        // still alive, is clean; OnDisable also cleans for a plain window close.
        AssemblyReloadEvents.beforeAssemblyReload += DestroyPreview;
        EditorApplication.quitting += DestroyPreview;
    }
    void OnDisable()
    {
        AssemblyReloadEvents.beforeAssemblyReload -= DestroyPreview;
        EditorApplication.quitting -= DestroyPreview;
        DestroyPreview();
    }

    // ANY window that re-bakes (the Animation Lab included) must release the Factory's live preview first: the bake
    // rewrites the preview prefab, and a GameObjectInspector still watching it throws
    // "InstantiateForAnimatorPreview(null)" from Unity internals mid-bake (seen on the first Lab state-bake).
    internal static void ReleasePreviews()
    {
        foreach (var w in Resources.FindObjectsOfTypeAll<ModelFactoryWindow>()) w.LoadPreview(null);
    }
    internal static void ReloadPreviews()
    {
        foreach (var w in Resources.FindObjectsOfTypeAll<ModelFactoryWindow>())
        { w.LoadPreview(w.cur != null ? w.cur.resourceName : null, forceReimport: true); w.Repaint(); }
    }

    // Release the preview: drop the draw list and clean the PreviewRenderUtility (safe if already null). Called on
    // selection change (draw list only, via LoadPreview) and on window close / domain reload / quit (full clean).
    void DestroyPreview()
    {
        previewDraws = null;
        if (previewGroundMat != null) { DestroyImmediate(previewGroundMat); previewGroundMat = null; }
        if (previewGroundMesh != null) { DestroyImmediate(previewGroundMesh); previewGroundMesh = null; }
        if (previewArrowMesh != null) { DestroyImmediate(previewArrowMesh); previewArrowMesh = null; }
        if (previewPRU == null) return;
        try { previewPRU.Cleanup(); } catch { }
        previewPRU = null;
    }

    // Load the baked prefab (animated <name>_Preview, else static <name>_Model) and build an interactive preview editor.
    // forceReimport: after a bake, the static path overwrites the mesh/prefab IN PLACE, so Unity can serve the preview a
    // stale cached copy until a manual reimport. Force a synchronous reimport of the mesh + prefab so the preview is current.
    void LoadPreview(string name, bool forceReimport = false)
    {
        previewDraws = null;   // drop the old draw list; keep the PRU alive for reuse (it's cleaned on reload/close)
        previewFor = name ?? "";
        if (string.IsNullOrEmpty(name)) return;
        // ANIMATED entries preview the REST-POSE rig FBX — the same source the Animation Lab previews: upright,
        // faithful (the rest pose IS what the game composes) and grounded at the origin. READ-ONLY: the FBX is NEVER
        // force-reimported here — attempt #1 did, post-bake, and that reimport scrambled the Lab's preview texture on
        // tiling-UV rigs (see animlab notes); the FBX needs no reimport insurance anyway, every animated bake
        // regenerates it from scratch. The old display-flipped <name>_Preview.prefab is only a fallback for entries
        // whose rig FBX is gone; the static _Model.prefab is a shipped OUTPUT and stays in Resources root.
        string animFbx = "Assets/FactorySource/" + name + "/anim/" + name + "_anim.fbx";
        string animPath = "Assets/FactorySource/" + name + "/" + name + "_Preview.prefab";
        string staticPath = "Assets/Resources/" + name + "_Model.prefab";
        // (2026-08-18: a first attempt preferred the texture-correct _Preview.prefab here — REVERTED same hour:
        // that prefab is a display-flipped bind pose with no ground plane, so the cure was worse than the disease
        // ("why is it heading up without a surface?"). The real fix must keep THIS route's upright grounded
        // geometry and fix the texture pairing instead — see the atlas-UV preview work.)
        string path = AssetDatabase.LoadMainAssetAtPath(animFbx) != null ? animFbx
                    : AssetDatabase.LoadMainAssetAtPath(animPath) != null ? animPath
                    : AssetDatabase.LoadMainAssetAtPath(staticPath) != null ? staticPath : null;
        if (path == null) return;
        previewGrounded = path == staticPath || path == animFbx;   // game-space previews only (see the field comment)
        previewWater = IsBoatPawn(cur?.pawnDescription);           // boats stand on water-blue, everything else on grass
        if (forceReimport)
            foreach (var dep in new[] { "Assets/Resources/" + name + "_ModelMesh.asset", path })
                if (dep != animFbx && AssetDatabase.LoadMainAssetAtPath(dep) != null)   // NEVER the FBX — see above
                    AssetDatabase.ImportAsset(dep, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        // the rig FBX's own imported materials are grey stand-ins — texture it with the bake's atlas material, exactly
        // like the Animation Lab's fit preview does (tiling UVs render fine: the texture wraps, same as in-game)
        var over = path == animFbx
            ? AssetDatabase.LoadAssetAtPath<Material>("Assets/FactorySource/" + name + "/" + name + "_PreviewMat.mat")
            : null;
        // ATLAS-UV SUBSTITUTION (2026-08-18 — the REAL fix for the 08-01 deferred item, the user's #1 problem):
        // a multi-material bake remaps the FBX's skinned-mesh UVs into the packed atlas IN MEMORY ONLY
        // (BuildMultiAtlasAndRemap assigns clones onto the imported asset), which is why the preview was correct
        // right after a bake and WRONG on every fresh select — a reimport/restart reverts the FBX to original UVs
        // while PreviewMat still carries the packed atlas. The bake also PERSISTS that clone (_PreviewMesh.asset:
        // same FBX-space geometry, remapped UVs, submeshes merged). Substituting it for the renderer it was cloned
        // from (matched by mesh name) pairs mesh and texture correctly WITHOUT losing the upright grounded FBX
        // view. Single-material rigs have no _PreviewMesh and stay on the plain route (their UVs wrap correctly).
        // MULTI-SMR SLICE (2026-08-19): the bake persists ONE remapped clone PER skinned renderer
        // (_PreviewMesh, _PreviewMesh1, …) — load them all and substitute per-renderer, not just the first hit.
        var uvSubs = path == animFbx ? UniversalBaker.LoadPreviewSubstitutes(name) : null;
        if (go != null) BuildDrawList(go, over, uvSubs);
        // (A donor-clip "footprint centering" briefly lived here — REMOVED with the double-application discovery:
        // the placement quirks it approximated were largely the runtime ApplyPositionOffset adding the registry
        // position on top of a bake that ALSO carried it. With the bake-side copy gone, the FBX view + the LIVE
        // runtime offset below is the honest prediction.)
    }

    // Flatten the baked prefab's renderers into a draw list + combined bounds for the PreviewRenderUtility (same
    // approach as the Animation Lab fit preview). The prefab's shared materials already carry the baked atlas;
    // overrideMat replaces them (the rest-pose FBX route, whose imported materials are untextured stand-ins).
    void BuildDrawList(GameObject go, Material overrideMat = null, List<Mesh> uvSubstitutes = null)
    {
        previewDraws = new List<(Mesh, Material[], Matrix4x4)>();
        bool first = true;
        // MULTI-SMR SLICE (2026-08-19): the bake persists one remapped clone PER skinned renderer; each renderer
        // consumes its own match from the pool (match-and-remove), so a rig with several skinned renderers gets
        // every part texture-correct — not just whichever renderer happened to match first.
        var pool = uvSubstitutes != null ? new List<Mesh>(uvSubstitutes.Where(s => s != null)) : null;
        int subTotal = pool?.Count ?? 0, subUsed = 0;
        foreach (var rr in go.GetComponentsInChildren<Renderer>(true))
        {
            Mesh m = rr is SkinnedMeshRenderer smr ? smr.sharedMesh : rr.GetComponent<MeshFilter>()?.sharedMesh;
            if (m == null) continue;
            // Swap in the persisted atlas-remapped clone for the renderer it was cloned from. Matched by GEOMETRY
            // IDENTITY (same vertex count — the clone has the exact same vertices), NOT by name: CreateAsset renames
            // the persisted mesh to its filename, which made a name match silently never fire (drill-caught,
            // 2026-08-18, TankDestroyers still corrupt).
            if (pool != null && rr is SkinnedMeshRenderer)
            {
                int hit = pool.FindIndex(s => s.vertexCount == m.vertexCount);
                if (hit >= 0) { m = pool[hit]; pool.RemoveAt(hit); subUsed++; }
            }
            var mtx = rr.transform.localToWorldMatrix;
            var mats = rr.sharedMaterials;
            if (overrideMat != null)
            {
                mats = new Material[Mathf.Max(1, m.subMeshCount)];
                for (int i = 0; i < mats.Length; i++) mats[i] = overrideMat;
            }
            previewDraws.Add((m, mats, mtx));
            var wb = TransformBounds(mtx, m.bounds);
            if (first) { previewBounds = wb; first = false; } else previewBounds.Encapsulate(wb);
        }
        // Loud diagnostic (drill aid): when remapped preview meshes exist, say whether they were actually used —
        // a silent no-match is exactly how the first two versions of this fix hid their failure.
        if (subTotal > 0)
            Debug.Log($"[Factory] preview UV substitution for '{previewFor}': " +
                      (subUsed == subTotal ? $"APPLIED {subUsed}/{subTotal} (texture-correct)"
                                           : $"APPLIED {subUsed}/{subTotal} — {subTotal - subUsed} clone(s) UNMATCHED ({string.Join(", ", pool.Select(s => s.vertexCount + " verts"))}) — FBX re-slimmed since the last bake? Re-bake to refresh the _PreviewMesh set"));
        if (previewDraws.Count == 0) previewDraws = null;
    }

    // THE TILE HEX — one in-game tile at TRUE size: center-to-center tile spacing is ~6.93 units (measured on the map,
    // the terrain-hug work), so across-flats = 6.93 (inradius 3.465, corner radius 4.0). Orientation is a parameter:
    // UNITS use cornerBaseDeg 30 (a flat EDGE faces +Z — units face their six neighbors edge-on, the forward arrow
    // crosses an edge like a unit leaving its tile); DISTRICTS use 0 (a CORNER faces +Z — the in-game district cell
    // presents a corner toward the model's forward, user-measured on the reactor). Factory / Anim Lab / District panes.
    internal const float TileInradius = 3.465f, TileCornerRadius = 4.001f;

    // REFERENCE MAN (2026-08-19, user: "a default humankind man as a reference point would really help assess
    // size"). A stylized low-poly figure at in-game human pawn height, drawn beside the model on the reference
    // plane. HumanRefHeight is a CALIBRATION CONSTANT in the waterLevel tradition: 0.9u is the starting
    // estimate — calibrate once by comparing the preview against an infantry pawn standing next to a known
    // unit in-game, then pin the measured value here (and note it in Factory-Manual).
    internal const float HumanRefHeight = 1.85f;  // calibrated stepwise by the user against a human-scale soldier
                                                  // model (0.9 → 1.1 → 1.85, 2026-08-19) — the waterline tradition.
                                                  // Matches the game's stylized pawn scale rather than strict
                                                  // vehicle-bake meters (game humans run large vs vehicles).
    // Shared box-prop machinery for the preview reference props (Ref man, measuring stick). BULLETPROOF
    // RENDERING (two drill rounds, 2026-08-19: "I don't see any man"): round 1 — hand-rolled winding faced
    // inward, every face culled; round 2 — shared-vertex double-siding averaged opposing face normals to ~zero.
    // Final form: every triangle FLAT-SHADED (its own 3 verts) and emitted in BOTH windings.
    static void AddBox(List<Vector3> v, List<int> t, float cx, float cy, float cz, float sx, float sy, float sz)
    {
        int b = v.Count;
        for (int i = 0; i < 8; i++)
            v.Add(new Vector3(cx + ((i & 1) == 0 ? -sx : sx) / 2f, cy + ((i & 2) == 0 ? -sy : sy) / 2f, cz + ((i & 4) == 0 ? -sz : sz) / 2f));
        foreach (var i in new[] { 0,2,1, 1,2,3, 4,5,6, 5,7,6, 0,1,4, 1,5,4, 2,6,3, 3,6,7, 0,4,2, 2,4,6, 1,3,5, 3,7,5 })
            t.Add(b + i);
    }
    static void AddSphere(List<Vector3> v, List<int> t, float cx, float cy, float cz, float r, int rings = 14, int segs = 20)   // fine enough that FLAT shading still reads round ("very square" at 6×10)
    {
        int b = v.Count;
        for (int ri = 0; ri <= rings; ri++)
        {
            float phi = Mathf.PI * ri / rings;
            for (int si = 0; si <= segs; si++)
            {
                float th = 2f * Mathf.PI * si / segs;
                v.Add(new Vector3(cx + r * Mathf.Sin(phi) * Mathf.Cos(th), cy + r * Mathf.Cos(phi), cz + r * Mathf.Sin(phi) * Mathf.Sin(th)));
            }
        }
        for (int ri = 0; ri < rings; ri++)
            for (int si = 0; si < segs; si++)
            {
                int a = b + ri * (segs + 1) + si, c = a + segs + 1;
                t.Add(a); t.Add(c); t.Add(a + 1);
                t.Add(a + 1); t.Add(c); t.Add(c + 1);
            }
    }

    static Mesh FinishFlatDoubleSided(string name, List<Vector3> v, List<int> t)
    {
        var fv = new List<Vector3>(); var ft = new List<int>();
        for (int i = 0; i < t.Count; i += 3)
        {
            Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
            int k = fv.Count; fv.Add(a); fv.Add(b); fv.Add(c); ft.Add(k); ft.Add(k + 1); ft.Add(k + 2);
            k = fv.Count; fv.Add(a); fv.Add(c); fv.Add(b); ft.Add(k); ft.Add(k + 1); ft.Add(k + 2);
        }
        var m = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        m.SetVertices(fv); m.SetTriangles(ft, 0); m.RecalculateNormals(); m.RecalculateBounds();
        return m;
    }

    internal static Mesh BuildRefMan(string name)
    {
        var v = new List<Vector3>(); var t = new List<int>();
        // Proportions of a 1.0-tall figure, scaled to HumanRefHeight at draw time. Classical figure ratios
        // (2026-08-19 v2, user: "proportion the stickman better" — v1 read chunky beside the soldier): head
        // ~1/7 of height, legs half the height with a visible gap, shoulders under a quarter of the width,
        // arms hanging to mid-thigh, a small pelvis block joining legs to torso.
        AddBox(v, t, -0.058f, 0.25f, 0f, 0.075f, 0.50f, 0.075f);   // legs (gap between them reads as two)
        AddBox(v, t,  0.058f, 0.25f, 0f, 0.075f, 0.50f, 0.075f);
        AddBox(v, t, 0f, 0.53f, 0f, 0.20f, 0.08f, 0.075f);         // pelvis
        AddBox(v, t, 0f, 0.705f, 0f, 0.235f, 0.29f, 0.085f);       // torso (0.56–0.85); depths slimmed ("less thick")
        AddBox(v, t, -0.153f, 0.64f, 0f, 0.055f, 0.40f, 0.06f);    // arms (shoulder to mid-thigh)
        AddBox(v, t,  0.153f, 0.64f, 0f, 0.055f, 0.40f, 0.06f);
        AddBox(v, t, 0f, 0.87f, 0f, 0.05f, 0.06f, 0.05f);          // neck
        AddSphere(v, t, 0f, 0.932f, 0f, 0.063f);                   // head — user-calibrated bisection: 0.072 too big, 0.055 too small, 0.063 lands it; top ≈ 1.0
        return FinishFlatDoubleSided(name, v, t);
    }

    // MEASURING STICK (2026-08-19, user request): a vertical ruler in GAME UNITS — ticks every 0.5u, long ticks
    // at whole units, 3u tall. Units, not meters: each bake picks its own world scale (Size dial), so units are
    // the one honest common measure; the Ref man is the human-scale anchor beside it.
    internal static Mesh BuildMeasureStick(string name)
    {
        var v = new List<Vector3>(); var t = new List<int>();
        const float H = 3f;
        AddBox(v, t, 0f, H / 2f, 0f, 0.035f, H, 0.035f);   // the pole
        for (float y = 0.5f; y <= H + 0.01f; y += 0.5f)
        {
            bool whole = Mathf.Abs(y - Mathf.Round(y)) < 0.01f;
            AddBox(v, t, whole ? 0.15f : 0.10f, y, 0f, whole ? 0.28f : 0.16f, 0.025f, 0.025f);   // tick bars, longer at whole units
        }
        return FinishFlatDoubleSided(name, v, t);
    }
    internal static Mesh BuildTileHex(string name, float cornerBaseDeg = 30f)
    {
        var m = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        var v = new Vector3[7];
        v[0] = Vector3.zero;
        for (int k = 0; k < 6; k++)
        {
            float a = (cornerBaseDeg + 60f * k) * Mathf.Deg2Rad;   // corners at base+k·60° from +Z
            v[k + 1] = new Vector3(Mathf.Sin(a) * TileCornerRadius, 0f, Mathf.Cos(a) * TileCornerRadius);
        }
        // DOUBLE-SIDED (2026-08-18, user request): the water tile must stay visible from BELOW the waterline —
        // orbiting under a submarine hull made the single-sided plane vanish. Mirrored vertex set (7 up-normal +
        // 7 down-normal) with reversed winding, so lit shading is correct from both sides. Shared by every
        // BuildTileHex user (District pane included) — a ground tile seen from below is equally deliberate there.
        var vv = new Vector3[14]; var nn = new Vector3[14]; var uv = new Vector2[14];
        for (int i = 0; i < 7; i++)
        {
            vv[i] = vv[i + 7] = v[i];
            nn[i] = Vector3.up; nn[i + 7] = Vector3.down;
            // planar UVs (XZ → 0..1 across the tile) so a ground texture maps across the hex; unused by the
            // solid-colour tile material, needed by the District Factory's terrain-paint texture.
            uv[i] = uv[i + 7] = new Vector2(v[i].x / (2f * TileCornerRadius) + 0.5f, v[i].z / (2f * TileCornerRadius) + 0.5f);
        }
        m.vertices = vv; m.normals = nn; m.uv = uv;
        m.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6, 0, 6, 1,          // top face (seen from above)
                              7, 9, 8, 7, 10, 9, 7, 11, 10, 7, 12, 11, 7, 13, 12, 7, 8, 13 }; // underside (reversed winding)
        return m;
    }

    // A flat compass rose in the XZ plane: a line from the origin toward +Z (map NORTH, how the tile presents
    // in-game) ending in an "N" glyph past the hex corner, plus E/S/W letters at their cardinal corners. All
    // letters read upright with North up (map convention). Districts have no facing of their own.
    internal static Mesh BuildCompass(string name)
    {
        var m = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        var v = new System.Collections.Generic.List<Vector3>();
        var t = new System.Collections.Generic.List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count; v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }
        Vector2 off = Vector2.zero;   // letter placement offset for Bar()
        void Bar(Vector2 from, Vector2 to, float w)   // thick 2D segment in the XZ plane
        {
            from += off; to += off;
            var dir = (to - from).normalized; var side = new Vector2(-dir.y, dir.x) * (w * 0.5f);
            Quad(new Vector3(from.x + side.x, 0f, from.y + side.y), new Vector3(to.x + side.x, 0f, to.y + side.y),
                 new Vector3(to.x - side.x, 0f, to.y - side.y), new Vector3(from.x - side.x, 0f, from.y - side.y));
        }
        const float W2 = 0.13f, LetterDist = 5.05f;
        Bar(new Vector2(0f, 0.3f), new Vector2(0f, 4.3f), 0.10f);    // the North line, center to past the corner
        Bar(new Vector2(0.3f, 0f), new Vector2(4.3f, 0f), 0.10f);    // East line
        Bar(new Vector2(0f, -0.3f), new Vector2(0f, -4.3f), 0.10f);  // South line
        Bar(new Vector2(-0.3f, 0f), new Vector2(-4.3f, 0f), 0.10f);  // West line
        // N — on the line
        off = new Vector2(0f, LetterDist);
        Bar(new Vector2(-0.32f, -0.45f), new Vector2(-0.32f, 0.45f), W2);
        Bar(new Vector2(0.32f, -0.45f), new Vector2(0.32f, 0.45f), W2);
        Bar(new Vector2(-0.32f, 0.45f), new Vector2(0.32f, -0.45f), W2);
        // E — at +X
        off = new Vector2(LetterDist, 0f);
        Bar(new Vector2(-0.28f, -0.45f), new Vector2(-0.28f, 0.45f), W2);
        Bar(new Vector2(-0.28f, 0.45f), new Vector2(0.30f, 0.45f), W2);
        Bar(new Vector2(-0.28f, 0f), new Vector2(0.22f, 0f), W2);
        Bar(new Vector2(-0.28f, -0.45f), new Vector2(0.30f, -0.45f), W2);
        // S — at -Z
        off = new Vector2(0f, -LetterDist);
        Bar(new Vector2(-0.30f, 0.45f), new Vector2(0.30f, 0.45f), W2);
        Bar(new Vector2(-0.30f, 0.45f), new Vector2(-0.30f, 0.02f), W2);
        Bar(new Vector2(-0.30f, 0.02f), new Vector2(0.30f, 0.02f), W2);
        Bar(new Vector2(0.30f, 0.02f), new Vector2(0.30f, -0.45f), W2);
        Bar(new Vector2(-0.30f, -0.45f), new Vector2(0.30f, -0.45f), W2);
        // W — at -X
        off = new Vector2(-LetterDist, 0f);
        Bar(new Vector2(-0.34f, 0.45f), new Vector2(-0.17f, -0.45f), W2);
        Bar(new Vector2(-0.17f, -0.45f), new Vector2(0f, 0.15f), W2);
        Bar(new Vector2(0f, 0.15f), new Vector2(0.17f, -0.45f), W2);
        Bar(new Vector2(0.17f, -0.45f), new Vector2(0.34f, 0.45f), W2);
        m.vertices = v.ToArray();
        var n = new Vector3[v.Count]; for (int i = 0; i < n.Length; i++) n[i] = Vector3.up;
        m.normals = n;
        m.triangles = t.ToArray();
        return m;
    }

    // A flat arrow from the origin toward +Z (the in-game FORWARD), drawn just above the tile hex; the head pokes
    // over the +Z edge (inradius 3.465) like a unit leaving its tile. Shared shape, per-window instances.
    internal static Mesh BuildForwardArrow(string name)
    {
        var m = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        m.vertices = new[]
        {
            new Vector3(-0.06f, 0f, 0.3f), new Vector3(0.06f, 0f, 0.3f), new Vector3(0.06f, 0f, 3.5f), new Vector3(-0.06f, 0f, 3.5f),   // shaft
            new Vector3(-0.25f, 0f, 3.5f), new Vector3(0.25f, 0f, 3.5f), new Vector3(0f, 0f, 4.3f),                                     // head
        };
        m.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        m.triangles = new[] { 0, 3, 2, 0, 2, 1, 4, 6, 5 };
        return m;
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

    // Interactive 3D preview embedded in the window, so a bake's result shows immediately (no hunting in the Project view).
    void DrawPreview()
    {
        if (previewDraws == null) return;
        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            // KEEL READOUT (2026-08-18, submarine waterline dispute): numbers, not eyeballs — the drawn geometry's
            // lowest/highest point vs the reference plane, INCLUDING the live offset when it applies. Separates
            // "the preview plane is at the wrong height" from "the preview is drawing a stale bake" in one glance:
            // a stale bake reads keel 0.00 despite a dialed offset; a wrong plane reads the right keel under the
            // wrong picture.
            string keelInfo = "";
            if (previewGrounded && previewDraws != null && previewDraws.Count > 0)
            {
                float liveY = cur != null && cur.animated && cur.position != Vector3.zero ? cur.position.z : 0f;
                if (previewCombat && cur != null) liveY += cur.combatZ;   // "In combat" preview: the battle-locked height
                float plane = previewWater ? PreviewWaterY : 0f;   // ground contract: keel vs ORIGIN; water: vs the calibrated game water level
                float keel = previewBounds.min.y + liveY - plane, top = previewBounds.max.y + liveY - plane;
                keelInfo = string.Format("  ·  keel {0:+0.00;-0.00;0.00}u / top {1:+0.00;-0.00;0.00}u vs {2}", keel, top, previewWater ? "waterline" : "ground");
            }
            EditorGUILayout.LabelField("Preview — " + previewFor + "   (drag = orbit · middle-drag = pan · scroll = zoom" +
                (previewGrounded ? (previewWater ? " · hex = one tile at water level · arrow = forward)" : " · hex = one tile at ground level · arrow = forward)")
                                 : ")  — legacy display pose (no rig FBX found), orientation not faithful") +
                (cur != null && cur.animated && cur.position != Vector3.zero ? "  · Position offset shown LIVE (runtime-applied, no bake needed)" : "") +
                keelInfo, EditorStyles.miniBoldLabel);
            if (previewGrounded)
            {
                previewRefMan = GUILayout.Toggle(previewRefMan, new GUIContent("Ref man",
                    $"Draw a human figure at in-game pawn height ({ModelFactoryWindow.HumanRefHeight:0.0}u) beside the model — the size reference. (Calibration constant; see the manual.)"), GUILayout.Width(66));
                if (previewRefMan)
                {
                    // his spot, user-dialed (X sideways, Z fore/aft on the plane) — walk him around the model
                    previewRefManPos.x = EditorGUILayout.FloatField(previewRefManPos.x, GUILayout.Width(38));
                    previewRefManPos.y = EditorGUILayout.FloatField(previewRefManPos.y, GUILayout.Width(38));
                }
                previewRuler = GUILayout.Toggle(previewRuler, new GUIContent("Ruler",
                    "A vertical measuring stick left of the model: ticks every 0.5 game units, long ticks at whole units, 3u tall. Units, not meters — each bake picks its own world scale, so units are the honest common measure."), GUILayout.Width(52));
            }
            if (previewGrounded)
                previewCombat = GUILayout.Toggle(previewCombat, new GUIContent("In combat",
                    "Preview the unit at its BATTLE-LOCKED height: the Combat height offset (Flight character section) applied on top of everything else — the position the unit eases to during a battle. Calibrate a submarine so only the periscope clears the waterline."), GUILayout.Width(78));
            if (previewWater)
                EditorGUILayout.LabelField(new GUIContent($"water @ {PreviewWaterY:0.00}",
                    "The HAF water standard: where the game's water surface sits above the model origin (mean + wave allowance, calibrated in-game 2026-08-18). A fixed code constant — every vessel's Z is calibrated against it."), GUILayout.Width(85));
            if (GUILayout.Button(new GUIContent("Center", "Re-center the view on the model (resets pan + zoom; keeps the orbit angle)"), GUILayout.Width(60)))
            { previewPan = Vector2.zero; previewZoom = 1.4f; Repaint(); }
        }
        var r = GUILayoutUtility.GetRect(200, 260, GUILayout.ExpandWidth(true));
        var e = Event.current;
        if (r.Contains(e.mousePosition))
        {
            if (e.type == EventType.ScrollWheel)
            {
                // Consume the wheel HERE so the window's outer scroll view never sees it — THIS is the zoom. (The
                // built-in GameObject preview had no zoom at all, and the scroll view ate the wheel — the old bug.)
                previewZoom = Mathf.Clamp(previewZoom * Mathf.Pow(1.12f, e.delta.y > 0 ? 1f : -1f), 0.02f, 5f);   // min 0.02 (0.1 → 0.05 → 0.02, user-requested closer inspection zoom) — near clip 0.01 still clears it on normal-size models
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0)
            {
                previewOrbit += new Vector2(e.delta.x, -e.delta.y) * 0.7f;
                previewOrbit.y = Mathf.Clamp(previewOrbit.y, -89f, 89f);
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && (e.button == 1 || e.button == 2))
            {
                // middle- or right-drag pans in the camera plane; scaled by radius (below) so it tracks the cursor at any zoom
                previewPan += new Vector2(-e.delta.x, e.delta.y) * 0.0035f;
                e.Use(); Repaint();
            }
        }
        if (e.type != EventType.Repaint) return;
        if (previewPRU == null) previewPRU = new PreviewRenderUtility();
        if (previewFallbackMat == null) previewFallbackMat = new Material(Shader.Find("Standard"));
        previewPRU.BeginPreview(r, GUIStyle.none);
        // try/finally so a throw in DrawMesh/Render can never skip EndPreview — an unclosed PRU errors and renders
        // garbage on EVERY later frame (the "BeginPreview not closed" cascade).
        Texture tex = null;
        try
        {
            var cam = previewPRU.camera;
            // frame the ground square too when it's drawn — the model can sit away from the origin, and a square
            // outside the frustum reads as "no square" (the TankDestroyers rebake proved it)
            var frame = previewBounds;
            if (previewGrounded) frame.Encapsulate(new Bounds(new Vector3(0f, PreviewPlaneY, 0f), new Vector3(2f * TileCornerRadius, 0.04f, 2f * TileInradius)));
            float radius = Mathf.Max(frame.extents.magnitude, 0.1f);
            float dist = radius * 2.0f * previewZoom;
            var rot = Quaternion.Euler(-previewOrbit.y, previewOrbit.x, 0f);
            // pan shifts the look target along the camera's right/up axes (× radius so it feels consistent at any size)
            var center = frame.center + rot * new Vector3(previewPan.x * radius, previewPan.y * radius, 0f);
            cam.transform.position = center + rot * (Vector3.back * dist);
            cam.transform.rotation = Quaternion.LookRotation(center - cam.transform.position);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = dist + radius * 4f;
            cam.fieldOfView = 30f;
            previewPRU.lights[0].intensity = 1.3f;
            previewPRU.lights[0].transform.rotation = Quaternion.Euler(45f, 45f, 0f);
            if (previewPRU.lights.Length > 1) previewPRU.lights[1].intensity = 0.6f;
            previewPRU.ambientColor = new Color(0.3f, 0.3f, 0.3f);
            // ground square first, at the ORIGIN plane (the in-game ground; see the field comment) — one tile ~10 across
            if (previewGrounded)
            {
                if (previewGroundMesh == null) previewGroundMesh = BuildTileHex("FactoryTileHex");
                if (previewGroundMat == null)
                {
                    previewGroundMat = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave };
                    previewGroundMat.SetFloat("_Glossiness", 0f);
                }
                previewGroundMat.color = previewWater ? new Color(0.23f, 0.36f, 0.47f) : new Color(0.33f, 0.40f, 0.29f);
                previewPRU.DrawMesh(previewGroundMesh, Matrix4x4.Translate(new Vector3(0f, PreviewPlaneY, 0f)), previewGroundMat, 0);
                if (previewArrowMesh == null) previewArrowMesh = BuildForwardArrow("FactoryForwardArrow");
                previewPRU.DrawMesh(previewArrowMesh, Matrix4x4.Translate(new Vector3(0f, PreviewPlaneY + 0.01f, 0f)), previewFallbackMat, 0);
                if (previewRefMan)
                {
                    if (previewRefManMesh == null) previewRefManMesh = BuildRefMan("FactoryRefMan");
                    // at the USER-DIALED spot (header fields; default 1.5u right of origin)
                    previewPRU.DrawMesh(previewRefManMesh, Matrix4x4.TRS(new Vector3(previewRefManPos.x, PreviewPlaneY, previewRefManPos.y), Quaternion.identity, Vector3.one * HumanRefHeight), previewFallbackMat, 0);
                }
                if (previewRuler)
                {
                    if (previewRulerMesh == null) previewRulerMesh = BuildMeasureStick("FactoryRuler");
                    previewPRU.DrawMesh(previewRulerMesh, Matrix4x4.Translate(new Vector3(-1.5f, PreviewPlaneY, 0f)), previewFallbackMat, 0);
                }
            }
            bool anyDead = false;
            // LIVE runtime Position offset (animated entries only — the plugin adds the registry `position` to the
            // pawn every frame; statics bake it into the mesh instead, so live-applying would double-show those).
            // Registry semantics -> preview axes: x sway -> X, y fore/aft -> Z (the pawn faces +Z here), z -> up Y.
            // The "In combat" toggle adds combatZ for EVERY entry type — that offset is runtime for statics too.
            var liveVec = Vector3.zero;
            if (previewGrounded && cur != null)
            {
                if (cur.animated && cur.position != Vector3.zero) liveVec += new Vector3(cur.position.x, cur.position.z, cur.position.y);
                if (previewCombat) liveVec += new Vector3(0f, cur.combatZ, 0f);
            }
            var liveOff = liveVec != Vector3.zero ? Matrix4x4.Translate(liveVec) : Matrix4x4.identity;
            foreach (var (mesh, mats, mtx) in previewDraws)
            {
                // A cached mesh can be DESTROYED under us (the baked prefab/FBX deleted or reimported outside the
                // window — e.g. a re-bake or forced cache clear); Unity's fake-null catches it. Drop the stale cache
                // instead of spamming MissingReferenceException on every repaint; the next Refresh/Bake rebuilds it.
                // (Mirror of AnimationLabWindow.DrawFitPreview — this window lacked the guard.)
                if (mesh == null) { anyDead = true; continue; }
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var mat = mats != null && mats.Length > 0 ? (mats[Mathf.Min(s, mats.Length - 1)] ?? previewFallbackMat) : previewFallbackMat;
                    previewPRU.DrawMesh(mesh, liveOff * mtx, mat, s);
                }
            }
            if (anyDead) previewDraws.Clear();
            cam.Render();
        }
        finally { tex = previewPRU.EndPreview(); }
        if (tex != null) GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, false);
    }

    void RefreshList()
    {
        // STATIC entries only — ANIMATED entries are authored exclusively in Tools ▸ ENC ▸ Animation Lab (which in
        // turn lists only animated ones). One entry = one owning window, so the same model can never be edited (and
        // silently overwritten, last-save-wins) from two places at once.
        var names = ModelRegistry.Load().Select(e => e.resourceName).ToList();
        names.Insert(0, "<New>");
        existing = names.ToArray();
        // The dropdown INDEX follows the loaded entry by NAME — the list is rebuilt on every reload, so a persisted
        // numeric index can silently point at a different entry than the form holds.
        selected = Array.IndexOf(existing, cur.resourceName);
        if (selected < 0) selected = 0;
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        // Widen the label column so the longer labels ("Position offset (Z = waterline)", "Double-sided (single-sided/CAD)",
        // "Animated (own rig + clip)") aren't clipped. Scales with window width so fields still get room when widened.
        EditorGUIUtility.labelWidth = Mathf.Clamp(position.width * 0.42f, 210f, 320f);
        GUILayout.Space(10f);
        EditorGUILayout.LabelField("Model Factory", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        DrawSettings();
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            int sel = EditorGUILayout.Popup("3D resource", selected, existing);
            // Refresh = "look at the registry NOW" (user design, drill 5 follow-up): re-read the dropdown AND
            // re-run the coherence compare — an on-demand banner, no recompile needed. Full recompute in both
            // directions (raise if drifted, clear if the registry caught back up); the form itself is never touched.
            if (GUILayout.Button(new GUIContent("Refresh", "Re-read the registry: update the dropdown and show the Form ≠ registry banner if this form has drifted from the saved entry."), GUILayout.Width(70)))
            { RefreshList(); formDiffersFromRegistry = ComputeFormDiffers(); bakedNotShipped = ShipStatus.IsBakedNotShipped(loadedName); }
            // CLONE (2026-07-27, user-designed): duplicate the ACTIVE entry into an UNSAVED new one — the fast
            // path for re-pointing a proven recipe at another unit (the T-62 -> MediumTanks "universal tank").
            // Pawn description is deliberately BLANK (pick the new target), the name gets a Clone suffix (so a
            // save can't overwrite the source), the baked GUIDs are cleared (a clone owns no assets until its
            // own bake) and the bake lock never travels (a clone is unbaked by definition).
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(cur.resourceName)))
                if (GUILayout.Button(new GUIContent("Clone", "Copy the loaded entry into a NEW unsaved entry: same recipe, blank Pawn description, name + 'Clone', no baked assets, no bake lock. Nothing is written until you Bake or Save."), GUILayout.Width(60)))
                {
                    var clone = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(cur));
                    clone.resourceName = "";     // name the clone yourself — and an unnamed clone can't Bake/Save, so the source can never be overwritten by accident
                    clone.pawnDescription = "";
                    clone.skel = new int[4]; clone.atlas = new int[4]; clone.clip = new int[4];
                    clone.clipMove = new int[4]; clone.clipAfter = new int[4]; clone.clipAttack = new int[4]; clone.clipCombat = new int[4];
                    // EVERY int[4] guid in ModelDef must be listed here — a clone owns no assets until its own bake,
                    // and an inherited guid silently points the clone at the SOURCE's ClipCollection. clipIdleAlt2
                    // was missing from this list (caught 2026-08-22 by asking "no hidden guids?" and counting the
                    // fields rather than trusting the list). If you add a guid field to ModelDef, add it here too.
                    clone.clipPreMove = new int[4]; clone.clipIdle = new int[4];
                    clone.clipIdleAlt = new int[4]; clone.clipIdleAlt2 = new int[4];
                    clone.bakeLocked = false; clone.disabled = false;
                    cur = clone; selected = 0; sel = 0; GUI.FocusControl(null);   // sel too — else the popup-apply below reads the stale index as a "selection change" and reloads the source entry right over the clone
                    formDiffersFromRegistry = false;   // the banner is about a SAVED entry's form; a clone is unsaved by definition (and the banner's Reload would wipe it)
                    loadedName = "";                   // no registry identity until first Save/Bake
                    status = "Cloned — set a Resource name and Pawn description, then Bake. Nothing saved yet.";
                }
            // Remove the selected registry entry (disabled on <New>). Prompts, then drops it from haf_models.json.
            using (new EditorGUI.DisabledScope(selected <= 0))
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    // E2: key on the SELECTED entry, NOT the (possibly edited) resource-name text field. Keying on the
                    // text field meant editing the name then Remove would delete a DIFFERENT model — or nothing — while
                    // still reporting "Removed". Also branch the status on Remove's actual result.
                    var name = selected > 0 && selected < existing.Length ? existing[selected] : null;
                    if (!string.IsNullOrEmpty(name))
                    {
                        // ONE dialog with the delete question built in (was two sequential modals — the second could be
                        // missed, and once the entry was gone it could NEVER be re-triggered, leaving orphan baked assets
                        // with no in-editor cleanup). DisplayDialogComplex → 0 = ok / 1 = cancel / 2 = alt.
                        int choice = EditorUtility.DisplayDialogComplex("Remove model",
                            $"Remove '{name}' from the registry? The plugin will stop injecting it on next launch.\n\n" +
                            "Also delete its BAKED assets (skeleton, atlas, clips, pose data, mesh, prefab) from " +
                            "Assets/Resources? Only the baker's own outputs are deleted — unit portraits and other " +
                            "unit-side files are never touched, and the FactorySource working folder is left alone.",
                            "Remove + delete files",   // 0
                            "Cancel",                  // 1
                            "Remove, keep files");     // 2
                        if (choice != 1)   // 0 or 2 = remove (1 = cancel)
                        {
                            // RECYCLE-BIN SNAPSHOT (2026-08-17 drill finding): Remove guarantees its OWN undo — the
                            // entry's JSON + (when deleting files) the exact baked-output whitelist are copied to
                            // <backup root>/_removed_<ts>_<name>/ BEFORE anything is touched. If the snapshot can't
                            // be taken, the remove is ABORTED (never destroy what can't be restored).
                            string undoDir = Path.Combine(EditorPrefs.GetString("HAF.Backup.Dest", "D:/HAF_Backups"),
                                "_removed_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_" + name);
                            try
                            {
                                var defSnap = ModelRegistry.Load().FirstOrDefault(d => d.resourceName == name);
                                if (defSnap == null) { status = $"Remove ABORTED — '{name}' not found in the registry (refresh and retry)."; GUIUtility.ExitGUI(); }
                                Directory.CreateDirectory(undoDir);
                                File.WriteAllText(Path.Combine(undoDir, "entry.json"), JsonUtility.ToJson(defSnap, true));
                                if (choice == 0) UniversalBaker.CopyAllOutputs(name, undoDir);
                                lastRemovedName = name; lastRemovedSnap = undoDir;
                            }
                            catch (Exception ex)
                            {
                                status = $"Remove ABORTED — could not take the undo snapshot ({ex.Message}). Nothing was removed.";
                                GUIUtility.ExitGUI();
                            }
                            bool removed = ModelRegistry.Remove(name);
                            // sel = 0 too: the popup-apply below reads a stale `sel` as a "selection change" and
                            // reloads existing[sel] on the SHRUNKEN list. Everything else — form reset, preview
                            // clear, coherence flag — is the FUNNEL's job (SelectEntry -> OnSelectResource): the
                            // 08-16..18 stale-window family were each one of these surfaces forgotten at one site.
                            sel = 0; RefreshList(); SelectEntry(0);
                            status = removed ? $"Removed '{name}' from the registry."
                                             : $"'{name}' was not in the registry — nothing removed.";
                            // Curated asset cleanup (2026-07-27, the lost-portrait lesson): delete the BAKED outputs via
                            // the exact whitelist ONLY — never a name wildcard, because unit-side files share the prefix
                            // (a manual 'rm <name>*' once deleted the AntiTank Halftrack's card portrait '<name>512.png'
                            // — magenta unit card). Portraits/UI images are never touched.
                            if (removed && choice == 0)
                            {
                                UniversalBaker.SweepAllOutputs(name);
                                AssetDatabase.Refresh();
                                status += " Baked assets deleted (whitelisted outputs only).";
                            }
                        }
                    }
                }
            // UNDO REMOVE (2026-08-17 drill, user-designed placement: "a restore button where the deletion button
            // is"): visible only after a remove this session; restores the entry + its baked outputs from the
            // _removed_ snapshot the Remove took. Survives a domain reload ([SerializeField] fields).
            if (!string.IsNullOrEmpty(lastRemovedName))
                if (GUILayout.Button(new GUIContent($"Undo remove", $"Restore '{lastRemovedName}' (registry entry + baked assets) from {Path.GetFileName(lastRemovedSnap)}"), GUILayout.Width(94)))
                { UndoRemove(); GUIUtility.ExitGUI(); }
            if (sel != selected) SelectEntry(sel);
        }
        // CORRUPT-SOURCE RECOVERY banner (2026-08-19, user design): a hand-edit broke the registry source — the
        // fault is PINPOINTED (line/column via Newtonsoft) and recovery is ONE CLICK, each path validated before
        // it writes and the broken file already preserved timestamped. Save stays locked until recovered.
        if (ModelRegistry.LastLoadCorrupt)
        {
            EditorGUILayout.HelpBox("REGISTRY SOURCE IS CORRUPT — " + ModelRegistry.LastCorruptDetail + "\n" +
                "The broken file is preserved beside the source; Save/Bake are locked so nothing can be wiped. Recover:", MessageType.Error);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Restore last deploy", "Copy the deployed artifact (refreshed on every Save — usually the freshest valid copy) back over the source. Validated before writing; the corrupt file stays preserved."), GUILayout.Width(140)))
                { status = ModelRegistry.RecoverFromArtifact(); RefreshList(); formDiffersFromRegistry = ComputeFormDiffers(); Debug.Log("[Factory] " + status); GUIUtility.ExitGUI(); }
                if (GUILayout.Button(new GUIContent("Restore last commit", "git checkout the source — the last committed version. Validated before accepting; the corrupt file stays preserved."), GUILayout.Width(140)))
                { status = ModelRegistry.RecoverFromGit(); RefreshList(); formDiffersFromRegistry = ComputeFormDiffers(); Debug.Log("[Factory] " + status); GUIUtility.ExitGUI(); }
                if (GUILayout.Button(new GUIContent("Open broken file", "Reveal the source in Explorer to fix the reported line by hand — then press Refresh."), GUILayout.Width(120)))
                { EditorUtility.RevealInFinder(ModelRegistry.SourcePath); }
            }
        }
        // ENTRY-STATE COHERENCE banner (the Lab's, ported): loud choice, never a silent resync in either direction.
        if (formDiffersFromRegistry)
        {
            EditorGUILayout.HelpBox("Form ≠ saved registry entry (your edits survived a compile, or the registry changed " +
                "outside this window). Nothing was discarded. Choose: ↻ Reload entry = take the registry (drops these " +
                "form values) · Save settings or Bake = keep exactly what you see here.", MessageType.Warning);
            if (GUILayout.Button(new GUIContent("↻ Reload entry (take the registry)", "Discard the form and re-load this entry fresh from the registry file. If the entry was removed from the registry, this resets to <New>."), GUILayout.Width(230)))
            { SelectEntry(loadedName); GUIUtility.ExitGUI(); }   // loadedName, not the name FIELD — a half-typed rename must reload the ORIGINAL entry
        }
        // SHIP STATUS inline notice (user request 2026-08-18, the HandCrankedSubmarine trap): baked assets only
        // reach the game through a mod build — a fresh bake is invisible in-game (dead GUIDs, pre-flight warning)
        // until the next build. Info, not warning: it's the NORMAL state right after baking, alarming only if forgotten.
        if (bakedNotShipped)
            EditorGUILayout.HelpBox("Baked, but NOT in the mod build yet — this bake is newer than the last mod build, so " +
                "the game still loads the previous assets (the boot pre-flight will warn about unresolved GUIDs). " +
                "Run the mod build, then relaunch the game. Tools ▸ HAF ▸ Ship Status lists all entries in this state.", MessageType.Info);
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
        // COLLISION WARNINGS (2026-08-22). Upsert is a blind `RemoveAll(name) + Add` — no duplicate check anywhere —
        // so a form whose Resource name matches a SAVED entry REPLACES it on Save, without a word, even while the
        // dropdown still reads <New>. The near-miss that prompted this: a cloned entry saved fine, the form was then
        // left on <New> holding the same name and an EMPTY model file, and one more Save would have overwritten the
        // working entry with a model-less one and orphaned its baked assets.
        {
            string formName = (cur.resourceName ?? "").Trim();
            string formPawn = (cur.pawnDescription ?? "").Trim();
            var saved = ModelRegistry.Load();
            nameCollides = formName.Length > 0
                             && !formName.Equals((loadedName ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                             && saved.Any(m => string.Equals(m.resourceName, formName, StringComparison.OrdinalIgnoreCase));
            if (nameCollides)
                EditorGUILayout.HelpBox($"Not allowed: the key '{formName}' already exists in this mod. " +
                    "Remove that entry first, or use a different name.", MessageType.Error);
            // TWO ENTRIES ON ONE PAWN is decided by longest-match at runtime and is near-impossible to spot by eye.
            var pawnClash = formPawn.Length == 0 ? null : saved.FirstOrDefault(m =>
                string.Equals(m.pawnDescription, formPawn, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(m.resourceName, formName, StringComparison.OrdinalIgnoreCase));
            if (pawnClash != null)
                EditorGUILayout.HelpBox($"'{pawnClash.resourceName}' already targets this pawn. Which of the two " +
                    "renders is decided by longest pawn-description match — remove or repoint the other entry.",
                    MessageType.Warning);
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.modelFile = EditorGUILayout.TextField("Model file", cur.modelFile);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                // Start at the current model file's folder (walking up to the nearest existing ancestor if it was
                // renamed/moved), so Browse opens near the source instead of the project root.
                string start = string.IsNullOrWhiteSpace(cur.modelFile) ? "" : System.IO.Path.GetDirectoryName(cur.modelFile);
                while (!string.IsNullOrEmpty(start) && !System.IO.Directory.Exists(start))
                    start = System.IO.Path.GetDirectoryName(start);
                var p = EditorUtility.OpenFilePanel("Select 3D model", start ?? "", "glb,gltf,obj,fbx,blend");
                if (!string.IsNullOrEmpty(p))
                {
                    cur.modelFile = p;
                    // Prefill "Fix 100x oversize" from the model's TRUE size (only on an explicit new pick, so a loaded
                    // entry keeps its saved value). Best-effort guess the user can override — see SuggestUnitFix.
                    float sz; bool guess = SuggestUnitFix(p, out sz);
                    if (sz > 0f) { cur.animUnitFix = guess; status = $"Auto-set 'Fix 100× oversize' = {(guess ? "ON" : "off")} (model true size ≈ {sz:0.###}u). Override if the bake comes out wrong. " +
                        "(Carried by the next Bake; 'Save settings' alone won't keep it — it's an animation-owned field the save rebases from the registry.)"; }   // 2026-08-19 audit: honest about the ownership rebase
                }
            }
        }
        // BROKEN-LINK REPORT: a referenced model file that isn't on disk (source moved/renamed) — warn as soon as the
        // entry is shown, not only when Bake fails.
        if (!string.IsNullOrWhiteSpace(cur.modelFile) && !System.IO.File.Exists(cur.modelFile))
            EditorGUILayout.HelpBox("Model file not found on disk:\n" + cur.modelFile +
                "\nThe source was moved or renamed — fix it with Browse (or reload the entry if the registry was already corrected).", MessageType.Warning);
        if ((cur.modelFile ?? "").ToLowerInvariant().EndsWith(".blend") && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox(".blend import needs Blender installed (auto-detected). Install it, or set EditorPrefs 'ENC.blenderPath' to blender.exe.", MessageType.Warning);

        // --- Animation: SUMMARY ONLY. The settings themselves (clip, bones, behaviors) are edited exclusively in the
        //     Animation Lab — mutually exclusive settings, working together: this window shows what's configured and
        //     jumps there; Bake here still uses the saved animation config, so baking works from either window.
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Animation", EditorStyles.miniBoldLabel);
        EnsureAnimProbe(cur.modelFile);
        if (!cur.animated && LooksAnimated(cur)) cur.animated = true;   // self-heal a lost flag (entry carries animation config)
        if (cur.animated)
        {
            var beh = new List<string>();
            if (cur.animStateDriven) beh.Add($"STATE-DRIVEN (idle '{cur.animClip}', move '{cur.animClipMove}'{(string.IsNullOrWhiteSpace(cur.animClipAfter) ? "" : $", after '{cur.animClipAfter}'")}{(string.IsNullOrWhiteSpace(cur.animClipAttack) ? "" : $", attack '{cur.animClipAttack}'")}{(string.IsNullOrWhiteSpace(cur.animClipCombat) ? "" : $", combat '{cur.animClipCombat}'")})");
            else if (!string.IsNullOrWhiteSpace(cur.animClip)) beh.Add("clip '" + cur.animClip + "'");
            if (!string.IsNullOrWhiteSpace(cur.animateBones)) beh.Add("bones '" + cur.animateBones + "'");
            if (cur.convertRig) beh.Add("raw-rig conversion");
            if (cur.fireOnAttack) beh.Add("fire-on-attack");
            if (cur.deployOnStop) beh.Add($"deploy-on-stop (pose {cur.deployPoseTime:0.##}, speed {cur.deploySpeed:0.##})");
            if (cur.fireOnAttack && cur.deployOnStop) beh.Add($"recoil (speed {cur.recoilSpeed:0.##})");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox("ANIMATED — " + (beh.Count > 0 ? string.Join(", ", beh) : "no clip/behaviors configured yet") +
                    "\nAnimation settings are edited in the Animation Lab; Bake here uses them as saved.", MessageType.None);
                if (GUILayout.Button("Edit in\nAnimation Lab", GUILayout.Width(110), GUILayout.Height(38)))
                    AnimationLabWindow.OpenFor(cur.resourceName, cur.modelFile, cur.pawnDescription, cur);
                // Greyed on a name collision like Bake and Save settings beside it — this button writes the registry
                // too, and it used to be the one door the guard was not wired to (review 2026-08-22).
                using (new EditorGUI.DisabledScope(nameCollides))
                    if (GUILayout.Button(new GUIContent("Make\nstatic…", "Deletes this entry's ANIMATION configuration from the saved registry (clip, state roles, behaviors, turret/muzzle) so the next Bake takes the static path. Removal STICKS — nothing rebases it back."), GUILayout.Width(70), GUILayout.Height(38)))
                        MakeStatic();
            }
            if (!UniversalBaker.BlenderAvailable())
                EditorGUILayout.HelpBox("The animated path needs Blender (to slim the rig + bake the clip) — it wasn't found. " +
                    "Install Blender or set EditorPrefs 'ENC.blenderPath' to blender.exe.", MessageType.Warning);
            EditorGUILayout.HelpBox("Animated mode uses Size + Reduce-to-tris; the static Mesh/shading options below " +
                "(normals, winding, double-sided, height UVs, convert grid) don't apply.", MessageType.None);
        }
        else if (animProbeState == 1)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox("Animation detected in this model — configure its clip + behaviors in the " +
                    "Animation Lab (it will bake as ANIMATED from then on).", MessageType.Info);
                if (GUILayout.Button("Open\nAnimation Lab", GUILayout.Width(110), GUILayout.Height(38)))
                    AnimationLabWindow.OpenFor(cur.resourceName, cur.modelFile, cur.pawnDescription, cur);
            }
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Transform", EditorStyles.miniBoldLabel);
        // wideMode: a custom EditorWindow defaults it to false, which makes Vector3Field wrap the XYZ onto a second
        // line under the label. Force it true so label + X/Y/Z sit on one line (the window is plenty wide for both).
        bool prevWide = EditorGUIUtility.wideMode;
        EditorGUIUtility.wideMode = true;
        cur.rotation = EditorGUILayout.Vector3Field("Rotation offset (XYZ)", cur.rotation);
        cur.position = EditorGUILayout.Vector3Field(new GUIContent("Position offset (Z = waterline)",
            "Move the model relative to its pawn, in GAME units: X sway, Y fore/aft, Z vertical (− sinks; the Zumwalt " +
            "waterline). STATIC models: baked into the mesh at Bake. ANIMATED models: applied by the PLUGIN at runtime " +
            "every frame, in the pawn's frame (turns with the unit) — previewed LIVE, and needs only Save settings + a " +
            "mod rebuild, no re-bake. (One dial, one application: a bake-time copy briefly existed and DOUBLED every " +
            "offset — the helicopter flew at exactly 2× its dialed height; removed 2026-08-07.)"), cur.position);
        EditorGUIUtility.wideMode = prevWide;
        using (new EditorGUILayout.HorizontalScope())
        {
            // Keep the Size input compact (not full-width) but sized RELATIVE to the label column — a fixed 220 left the
            // input a ~10px sliver once the label column widened to ~210. label + ~90px input keeps the box usable.
            cur.size = EditorGUILayout.FloatField(new GUIContent("Size (units)", "Length of the model's longest axis, in world units"), cur.size, GUILayout.Width(EditorGUIUtility.labelWidth + 90f));
            GUILayout.FlexibleSpace();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Source geometry — pre-bake (Blender)", EditorStyles.miniBoldLabel);
        // Sequential transforms on YOUR model BEFORE the bake, in the order the baker runs them: strip → reduce → convert.
        // Strip parts — BAKE-TIME (Blender). Deletes objects from YOUR model before baking (mirror of Hide donor meshes,
        // which acts on the DONOR at runtime). Use it to drop your model's own rotor so the donor's spinning rotor shows
        // through, or to remove a crew figure / weapon pod. Comma-separated object-name substrings (case-insensitive).
        if (wrapArea == null) wrapArea = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
        using (new EditorGUILayout.HorizontalScope())
        {
            // Label + a fixed-height (~3-line) scroll view holding a word-wrapping text area, so a long list (a rotor's
            // hub + blades + tail + their doubled -FACES copies) stays readable and scrolls instead of running off-screen.
            GUILayout.Label(new GUIContent("Strip parts (names)",
                "BAKE-TIME: comma-separated object-name substrings to DELETE from your model before baking (each match takes " +
                "its children too). Use it to remove parts you don't want baked in — e.g. a helicopter's OWN rotor (so the " +
                "donor's animated rotor spins through), a crew figure, or a weapon pod. Case-insensitive substring match on the " +
                "source object names. This is the mirror of 'Hide donor meshes' (which hides the DONOR's parts at runtime) — " +
                "this edits YOUR model at bake time. Needs Blender. Empty = keep everything."),
                GUILayout.Width(EditorGUIUtility.labelWidth));
            using (var sv = new EditorGUILayout.ScrollViewScope(stripScroll, GUILayout.Height(37f)))
            {
                stripScroll = sv.scrollPosition;
                // ExpandHeight so the editable box FILLS the 37px viewport even when it's near-empty — without it the
                // text area only grows to the content, so you'd see a single line. It still grows past the box (scrollbar).
                cur.stripParts = EditorGUILayout.TextArea(cur.stripParts ?? "", wrapArea, GUILayout.ExpandHeight(true));
            }
            if (GUILayout.Button(new GUIContent("Pick", "List the object names in the Model file so you can choose which to strip (reads GLB/glTF directly)."), GUILayout.Width(70), GUILayout.Height(37f)))
            {
                var r = GUILayoutUtility.GetLastRect();
                var names = UniversalBaker.ListModelObjectNames((cur.modelFile ?? "").Trim());
                if (names.Length == 0)
                    EditorUtility.DisplayDialog("Strip parts",
                        "Couldn't read object names from the Model file.\n\n" +
                        "Pick reads names directly from GLB / glTF — make sure the Model file above points at a .glb/.gltf. " +
                        "For FBX / OBJ / .blend, open the model in Blender to see the object names and type the substrings by " +
                        "hand (each match strips that object + its children).", "OK");
                else
                    new StringDropdown(new AdvancedDropdownState(), names, names, "Model objects", m =>
                    {
                        var set = (cur.stripParts ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                        if (!set.Contains(m)) set.Add(m);
                        cur.stripParts = string.Join(",", set);
                        Repaint();
                    }).Show(r);
            }
        }
        if (!string.IsNullOrWhiteSpace(cur.stripParts) && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox("Strip parts uses Blender — it wasn't found, so Bake will fail. Clear the field or set " +
                "Blender's path in Settings above.", MessageType.Warning);
        // Reduce-to-tris — runs AFTER strip (so the tri budget is spent only on the geometry you keep, not on a rotor
        // you're about to delete). There is NO hard per-model cap in the engine (verified: maxMeshTriangleCount ships
        // 0/unlimited) — the real budget is the SHARED pawn-layer pool (~1M verts, ~700k used by the full roster at load;
        // see HAF docs/Vertex-Budget.md). Slider range 0..100000: default 24000 is a sensible share of the pool; go higher
        // for a hero unit (mind the F8 'Mesh Budget' readout), or grow the pool itself via [Buffers] BufferOverrides.
        // Two geometry-reduction knobs share one row (you reach for one OR the other): 'Reduce to ~tris' = Blender
        // quadric decimation (UV-preserving); 'Weld & simplify' = glbconv vertex welding (GLB/glTF/.blend, runs after
        // strip+reduce, untextured only). The slider fills the row; the weld field is a compact trailing int.
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.targetTris = EditorGUILayout.IntSlider(new GUIContent("Reduce to ~tris (0 = off)",
                "Quadric-decimate a heavy model to about this many triangles (via Blender) before baking. There's NO hard " +
                "per-model limit — the budget is the SHARED pawn buffer (~1,000,000 verts across ALL loaded model types; " +
                "~300k free with vanilla + the current set — check F8 ▸ Mesh Budget in-game, or raise the pool with the " +
                "plugin's [Buffers] BufferOverrides). Runs AFTER 'Strip parts', so the budget covers only the geometry you " +
                "keep. Default 24000 is a good roster citizen; 50k+ is fine for a hero unit. It's a CEILING, not a quota: a " +
                "model already under it passes through untouched (never upscaled). Toggling Double-sided automatically HALVES " +
                "the effective target (it doubles the baked geometry). Preserves thin parts (per-object). 0 = no reduction. " +
                "Needs Blender (auto-detected)."), cur.targetTris, 0, 100000);
            GUILayout.Space(14);
            float lw = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 96;
            cur.convertGrid = EditorGUILayout.IntField(new GUIContent("Weld & simplify",
                "GLB / glTF / .blend only — controls how the source mesh is converted to OBJ.\n\n" +
                "0 = keep exact: every vertex and UV preserved (texture seams intact). This is the right value for " +
                "textured models — any welding averages UVs across seams and scrambles the skin.\n\n" +
                ">0 = weld/simplify: merge nearby vertices at this resolution along the longest axis " +
                "(higher = more vertices kept). Use only for heavy UNtextured meshes that need simplifying — for a " +
                "textured model, decimate with 'Reduce to ~tris' instead (it preserves UVs).\n\n" +
                "Ignored for OBJ/FBX (already meshes)."), cur.convertGrid, GUILayout.Width(150));
            EditorGUIUtility.labelWidth = lw;
        }
        if (cur.targetTris > 0 && !UniversalBaker.BlenderAvailable())
            EditorGUILayout.HelpBox("Reduce-to-tris uses Blender (quadric decimation) — Blender wasn't found, so Bake will " +
                "fail. Either set this to 0, use 'Weld & simplify' (Blender-free GLB decimation), or install Blender / " +
                "set its path in Settings above.", MessageType.Warning);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Shading / normals — bake-time", EditorStyles.miniBoldLabel);
        // Parameters read when the final mesh is built (import/bake) — NOT sequential steps, so order among them is
        // irrelevant to the result. Grouped apart from the geometry transforms above for that reason.
        cur.normalsMode = (int)(NormalsMode)EditorGUILayout.EnumPopup("Normals", (NormalsMode)cur.normalsMode);
        using (new EditorGUI.DisabledScope(cur.normalsMode != (int)NormalsMode.Recalculate))
            cur.smoothingAngle = EditorGUILayout.Slider("Smoothing angle", cur.smoothingAngle, 0f, 180f);
        // Three geometry/shading toggles on one row (ToggleLeft = checkbox then label, so they pack compactly),
        // right-aligned via a leading FlexibleSpace.
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            cur.heightUV = EditorGUILayout.ToggleLeft(new GUIContent("Height-gradient UVs (untextured)",
                "Override UVs with V = normalized height, so a vertical-gradient albedo maps by height regardless of the " +
                "model's own UVs — e.g. a black skirt low + grey hull high (put a bottom-black / top-grey PNG named " +
                "'*albedo*.png' in the resource folder). For untextured CAD models that need a simple gradient skin."), cur.heightUV, GUILayout.Width(240));
            cur.windingFix = EditorGUILayout.ToggleLeft(new GUIContent("Winding fix (CAD/convex)",
                "Rewind faces outward so single-sided / CAD 'sketch' meshes render single-sided instead of culling to invisible " +
                "(e.g. a hovercraft skirt). Lighter than double-sided (no extra geometry). Assumes a roughly convex hull — " +
                "true for vehicles/ships. Preferred for CAD hulls; use Double-sided for genuinely non-convex thin shells."), cur.windingFix, GUILayout.Width(190));
            cur.doubleSided = EditorGUILayout.ToggleLeft(new GUIContent("Double-sided (single-sided/CAD)",
                "Add a back face to every surface so single-sided or CAD 'sketch' meshes don't render invisible in-game (the " +
                "engine culls backfaces). Enable for models with missing / see-through parts — e.g. a hovercraft skirt. " +
                "Doubles the triangle count."), cur.doubleSided, GUILayout.Width(235));
        }
        // Albedo tone (baked into the atlas). The injection path ships a FLAT albedo — the donor's PBR normal/metallic/
        // roughness maps are neutralized so its camo can't bleed onto our model — so a skin that relied on shiny metal,
        // or a dark/washed-out texture, reads muddy in-game. These lift it at bake time (1.0 = unchanged). Slider ranges
        // are generous headroom; the number box takes exact values. No re-import needed — tweak + re-bake to preview.
        cur.albedoBrightness = EditorGUILayout.Slider(new GUIContent("Albedo brightness",
            "Multiply the baked atlas RGB. 1 = unchanged; >1 lifts a dark skin (the in-game look is flat albedo with the " +
            "donor's PBR neutralized, so shiny/dark models come out muddy). Baked into the atlas — re-bake to apply."), cur.albedoBrightness <= 0f ? 1f : cur.albedoBrightness, 0.5f, 2f);
        cur.albedoSaturation = EditorGUILayout.Slider(new GUIContent("Albedo saturation",
            "Colour vividness of the baked skin. 1 = unchanged, 0 = greyscale, >1 = punchier. Fixes a washed-out/" +
            "desaturated albedo (the game's lighting can't add colour back). Baked into the atlas — re-bake to apply."), cur.albedoSaturation < 0f ? 1f : cur.albedoSaturation, 0f, 2f);
        cur.keepBlack = EditorGUILayout.Toggle(new GUIContent("Keep black (glass/cockpit)",
            "MULTI-MATERIAL models only. By default the bake repaints near-black atlas regions neutral grey to hide UV " +
            "dead-zones and packing gaps (which would render as black patches). That also flattens an INTENTIONALLY black " +
            "material — a glossy canopy, a dark cockpit — to grey. Tick this to keep true black on such a model. Re-bake to apply."), cur.keepBlack);
        if (cur.atlasMaxDim <= 0) cur.atlasMaxDim = 512;   // default / migrate old registries
        cur.atlasMaxDim = EditorGUILayout.IntPopup(new GUIContent("Atlas size",
            "Longest side of the baked atlas, in pixels. The atlas is DXT1-compressed and saved to the shipped _Atlas.asset, " +
            "so SMALLER = smaller mod bundle. A unit is ~80px at map zoom (and its info card uses your 2D portrait, not the " +
            "model), so 512-1024 is plenty; pick 2048 for a unit you zoom in on closely, and 4096 only when the source " +
            "texture is that large AND you really want to read fine detail (heaviest bundle + VRAM). Re-bake to apply."),
            cur.atlasMaxDim, new[] { new GUIContent("256"), new GUIContent("512"), new GUIContent("1024"), new GUIContent("2048"), new GUIContent("4096") }, new[] { 256, 512, 1024, 2048, 4096 });
        cur.materialMode = (MaterialMode)EditorGUILayout.EnumPopup(new GUIContent("Material mode",
            "How the bake handles a model with MORE THAN ONE material. Auto = pack a multi-material atlas when the model has >1 " +
            "material, else a single texture (right for most). Single = force one texture — correct for CLOSED models (tanks, " +
            "planes) sharing a skin. Multi = force the multi-material atlas — needed for OPEN kit (a towed gun's wheels/legs/" +
            "barrel each on their own material) where the wheel would otherwise sample the wrong texture. Costs atlas space. Re-bake to apply."),
            cur.materialMode);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Runtime — applied on load, no re-bake", EditorStyles.miniBoldLabel);
        // Hide donor meshes — runtime field. The donor's fragment names only exist at runtime, so Pick reads them back
        // from the BepInEx log (the plugin dumps "[Uni] <name> donor fragment[i] mesh='...'" once per launch).
        using (new EditorGUILayout.HorizontalScope())
        {
            cur.hideMeshes = EditorGUILayout.TextField(new GUIContent("Hide donor meshes",
                "RUNTIME, not baked. Comma-separated name substrings of the DONOR unit's extra parts to hide on this unit — " +
                "e.g. 'Rotor' to remove the attack-helicopter rotor from a drone. Leave EMPTY to keep them (a custom " +
                "helicopter can borrow the donor's spinning rotor by leaving this blank). Use Pick to choose from the donor " +
                "fragments the plugin logged (launch the game once first). Takes effect on reload — no re-bake. " +
                "NOTE: only hides FRAGMENT-based extras; a donor's animated skinned sub-parts (helicopter rotor, spinning " +
                "wheels) are encoded at pawn-spawn and can't be hidden — pick a donor without them."), cur.hideMeshes ?? "");
            if (GUILayout.Button(new GUIContent("Pick", "List the donor's fragment meshes the plugin logged for this resource. Launch the game once with the model injected first."), GUILayout.Width(70)))
            {
                var r = GUILayoutUtility.GetLastRect();
                var frags = ReadDonorFragments(cur.resourceName);
                if (frags.Count == 0)
                    EditorUtility.DisplayDialog("Hide donor meshes",
                        $"No donor fragment meshes have been logged for '{cur.resourceName}' yet.\n\n" +
                        "Launch the game once with this model injected — the plugin writes the donor's fragment mesh names " +
                        "to BepInEx\\LogOutput.log — then click Pick again. (If the donor has no extra fragments, there's " +
                        "nothing to hide and you can leave this empty.)", "OK");
                else
                {
                    var arr = frags.ToArray();
                    new StringDropdown(new AdvancedDropdownState(), arr, arr, "Donor fragments", m =>
                    {
                        var set = (cur.hideMeshes ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                        if (!set.Contains(m)) set.Add(m);
                        cur.hideMeshes = string.Join(",", set);
                        Repaint();
                    }).Show(r);
                }
            }
        }
        // Four runtime toggles on one right-aligned row (ToggleLeft = checkbox then label; leading FlexibleSpace right-aligns).
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            cur.respawnAfterLoad = EditorGUILayout.ToggleLeft(new GUIContent("Respawn after load",
                "FIX for the save-load first-instance rotor bug: on a save-load the engine draws the FIRST custom-helicopter " +
                "pawn with its borrowed donor rotor ~1 unit low; anything (re)built after load is correct. Tick this and the " +
                "plugin re-runs the game's own pawn rebuild on this model's units ~3s after load, clearing it (a brief one-time " +
                "flicker as they rebuild). Tick ONLY for models that borrow a donor's animated sub-part (a spinning rotor); " +
                "pointless flicker otherwise."), cur.respawnAfterLoad, GUILayout.Width(150));
            cur.freezeDonorAnim = EditorGUILayout.ToggleLeft(new GUIContent("Freeze donor animation",
                "Stop the DONOR's idle/move animation from bobbing your STATIC mesh. A borrowed mesh inherits the donor's pose " +
                "wiggle (e.g. a Recon-Drone donor's hover bob looks wrong on a big airship). Tick this and the plugin pins every " +
                "pose's time to frame 0 each frame, holding the mesh rigid — it still glides tile-to-tile (that's transform-driven, " +
                "not animation). Static models only; animated models play their own baked clip. No re-bake, just rebuild the mod."),
                cur.freezeDonorAnim, GUILayout.Width(165));
            cur.silenceDonorVfx = EditorGUILayout.ToggleLeft(new GUIContent("Silence donor VFX (flashes)",
                "Suppress the DONOR's animation-driven VFX on this unit — muzzle flashes, animator smoke puffs. Those effects " +
                "anchor to DONOR bone names that don't exist on your injected skeleton, so they render misplaced (the AA-gun " +
                "flash floating in mid-air). VFX only: the donor's SOUNDS are untouched (use 'Silence donor sound' in the Sound " +
                "Studio for those). Runtime-only — no re-bake, just rebuild the mod."),
                cur.silenceDonorVfx, GUILayout.Width(195));
            cur.useDonorClip = EditorGUILayout.ToggleLeft(new GUIContent("Use donor animation clip",
                "Keep the DONOR's own animation playing on your injected skeleton instead of your baked clip (helicopter " +
                "flight motion: body bob + rotor spin from the donor). The donor's channels grab our bones BY INDEX, so the " +
                "rig must be built to donor convention — same bone count/order (body, main rotor, tail rotor; no extra Gun " +
                "bone) with identity rests (the Vehicle Lab's rotor bones + the plugin's root rebase handle this). " +
                "Runtime-only — no re-bake, just rebuild the mod."),
                cur.useDonorClip, GUILayout.Width(175));
        }

        // FLIGHT CHARACTER: the family of knobs that decide how a unit CARRIES ITSELF while moving — whose
        // animation plays (donor clip, above), how it changes heading (turn ease), how it holds altitude
        // (terrain hug). Grouped so they read as one idea instead of four unrelated sliders.
        EditorGUILayout.LabelField("Flight character — how the unit carries itself", EditorStyles.miniBoldLabel);
        cur.turnRate = EditorGUILayout.Slider(new GUIContent("Turn ease — rate (deg/s)",
            "Smooth the engine's instant facing SNAP when a move order OR AN ATTACK changes heading: the model TURNS " +
            "toward the new direction at this rate instead of teleporting to it — any model, not just flyers. A map " +
            "bombard then also WAITS for the pivot: muzzle flash, shot sound, shell and the fire-on-attack clip all " +
            "hold until the barrel faces the target. 0 = off (vanilla snap), 180 = a 90-degree turn in half a second; " +
            "lower is more majestic, higher more military. Every angle eases (180s included) while teleports and " +
            "battle placement still snap. Runtime-only — no re-bake. See docs/Turn-Ease.md."), cur.turnRate, 0f, 720f);
        // sub-knobs stay VISIBLE but greyed while their feature is off — hiding them made the Runtime section
        // look like it only had two flight settings (user report), with no hint that more exist.
        using (new EditorGUI.DisabledScope(cur.turnRate <= 0f))
            cur.turnBank = EditorGUILayout.Slider(new GUIContent("   Bank into turn (deg)",
                "How far the model rolls INTO the turn while it swings around — a few degrees sells an aircraft, 0 " +
                "keeps it flat for ground and naval units. Negative if the lean reads backwards."), cur.turnBank, -30f, 30f);

        cur.hugDrop = EditorGUILayout.Slider(new GUIContent("Terrain hug — drop (units)",
            "Fly LOWER over open country and climb only for built city districts. The game already flies air units at " +
            "a terrain-relative altitude (they follow hills), but it ignores BUILDINGS — which is why Position Z has to " +
            "clear the skyline. This subtracts that lift again wherever no built district is under or ahead of the unit. " +
            "0 = off; -2 is a good start (negative = lower). Cultivated tiles don't count as city, so it stays low over " +
            "farmland. Runtime-only — no re-bake."), cur.hugDrop, -10f, 0f);
        using (new EditorGUI.DisabledScope(cur.hugDrop == 0f))
            cur.hugLookahead = EditorGUILayout.Slider(new GUIContent("   Climb anticipation (units)",
                "How far AHEAD of the model the district probe sits, so it starts climbing BEFORE it reaches the " +
                "buildings — like a pilot — instead of reacting once it's inside them. 0 = purely reactive."),
                cur.hugLookahead, 0f, 8f);
        cur.combatZ = EditorGUILayout.Slider(new GUIContent("Combat height offset (units)",
            "Raise or LOWER the unit while its army is locked in battle (deployment → resolution), eased ~2s both " +
            "ways. Negative submerges — a submarine at around -0.5 fights submerged and resurfaces afterwards; " +
            "positive lifts (a drone climbing to combat altitude). 0 = off. Works for static and animated models. " +
            "Runtime-only — no re-bake (but relaunch to apply)."), cur.combatZ, -3f, 3f);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Texture / import", EditorStyles.miniBoldLabel);
        cur.reuseExtracted = EditorGUILayout.Toggle(new GUIContent("Keep extracted texture (hand-edits)",
            "Protect the extracted albedo from being regenerated, so a hand-edited texture (e.g. in paint.net) survives " +
            "re-bakes. ANIMATED models: this is the checkbox's ONLY effect — geometry is re-processed automatically " +
            "whenever a relevant setting changes (rotation, tris, clip, bones, material, model file), so rotation etc. " +
            "always respond. STATIC models: also reuses the extracted OBJ for fast iteration, but re-extracts " +
            "automatically when the source file, converter, or a convert arg (grid/strip/reduce/double-sided) changes."), cur.reuseExtracted);

        EditorGUILayout.Space();
        // A brand-new resource (<New>) has nothing to re-bake, so it also needs a model file; an existing one may leave
        // Model file empty to re-bake with new settings. Missing either target field greys Bake out with a reason.
        bool isNew = selected <= 0;
        // The resource name becomes a folder name, a filename prefix, and a converter argument — a space or other
        // path-hostile char breaks the bake (a space made glbconv parse the next word as the grid int). Require a
        // single token so the modder is told BEFORE baking, not by a cryptic shell-out error after.
        char badChar = '\0';
        foreach (char c in cur.resourceName ?? "")
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) { badChar = c; break; }
        bool nameValid = badChar == '\0';
        bool canBake = !string.IsNullOrWhiteSpace(cur.resourceName)
                    && nameValid
                    && !string.IsNullOrWhiteSpace(cur.pawnDescription)
                    && (!isNew || !string.IsNullOrWhiteSpace(cur.modelFile))
                    && !nameCollides;   // never let a blind-replace Upsert destroy another entry
        using (new EditorGUILayout.HorizontalScope())
        {
            // BAKE LOCK (mirrors the Animation Lab's checkbox): a bake-locked entry can't be regenerated from
            // EITHER window — its baked assets are in-game verified and the shared tooling may have moved on
            // underneath it (the m114 after the engine-contract rework). Untick in the Lab to deliberately rebake.
            using (new EditorGUI.DisabledScope(!canBake || cur.bakeLocked))
                if (GUILayout.Button(new GUIContent(cur.bakeLocked ? "Bake (locked)" : "Bake",
                    cur.bakeLocked ? "This entry is bake-locked (verified assets). Untick 'Lock bake' in the Animation Lab to deliberately rebake — then re-verify in-game." : ""), GUILayout.Height(34))) DoBake();
            // SAVE WITHOUT BAKING: the Runtime section (flight character, VFX/audio flags, donor clip, textures)
            // is applied on load and needs no baked asset — yet Bake used to be the ONLY way to persist it, i.e.
            // a full Blender round-trip to change one slider, and impossible at all on a bake-locked entry.
            // Same ownership rebase as the bake path, so Lab-owned fields are still protected.
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(cur.resourceName) || !nameValid
                                               || string.IsNullOrWhiteSpace(cur.pawnDescription) || nameCollides))
                if (GUILayout.Button(new GUIContent("Save settings",
                    "Write this entry's settings to the registry WITHOUT re-baking — for the runtime knobs " +
                    "(turn ease, terrain hug, donor clip, VFX/sound flags, rotation/position/textures). Rebuild " +
                    "the mod afterwards; no relaunch of Blender, no new assets."), GUILayout.Height(34), GUILayout.Width(110)))
                    SaveSettingsOnly();
            if (GUILayout.Button("Reset", GUILayout.Height(34), GUILayout.Width(72))) { cur = new ModelDef(); selected = 0; status = ""; GUI.FocusControl(null); }
        }
        if (!canBake)
            EditorGUILayout.HelpBox(
                !nameValid && !string.IsNullOrWhiteSpace(cur.resourceName)
                    ? $"Resource name can't contain '{(badChar == ' ' ? "space" : badChar.ToString())}'. Use letters, digits, '_' or '-' only — e.g. 'AttackHelicopter'."
                : isNew ? "New resource: set Resource name, Pawn description and a Model file to bake."
                        : "Set Resource name and Pawn description to bake.", MessageType.Warning);

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
        DrawPreview();
        EditorGUILayout.HelpBox(
            "Bake imports the model, bakes a skeleton + atlas, and writes the JSON registry the in-game plugin reads.\n" +
            "• Formats: GLB / glTF / OBJ / FBX, and .blend (auto-converted via installed Blender).\n" +
            "• Model file empty = re-bake the existing resource with new settings (fast iteration).\n" +
            "• Normals: KeepModel = the artist's; Recalculate = hard edges via smoothing angle; Faceted = fully flat.\n" +
            "• Convert grid: 0 keeps UV seams (textured models); >0 decimates (heavy untextured meshes).\n" +
            "Registry (source): " + ModelRegistry.SourcePath + "\nDeploys to (artifact): " + ModelRegistry.RegistryPath, MessageType.None);
        using (new EditorGUILayout.HorizontalScope())
        {
            // One-click way to reach the config folder (haf_models.json + the plugin's .cfg live here).
            if (GUILayout.Button("Open config folder", GUILayout.Width(150)))
                EditorUtility.RevealInFinder(System.IO.File.Exists(ModelRegistry.RegistryPath)
                    ? ModelRegistry.RegistryPath : ModelRegistry.ConfigDir);
            if (GUILayout.Button(new GUIContent("Validate pack", "PRE-SHIP gate (Pack-Validator-Design): runs the shared rule core over EVERY registry entry — real pawn names, sound/skin files exist, bone names exist on each entry's baked skeleton, ranges/formats/exclusions. Same rules the plugin re-runs on the player's machine at load. Warnings explain; nothing is blocked."), GUILayout.Width(100)))
                ValidatePack();
            GUILayout.Label("↑ haf_models.json + the plugin .cfg", EditorStyles.miniLabel);
        }
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
            EditorGUILayout.HelpBox("Registry SOURCE (edit this one, git-tracked):\n" + ModelRegistry.SourcePath +
                "\nDeployed ARTIFACT (what the game reads — regenerated on every Save):\n" + ModelRegistry.RegistryPath +
                (exists ? "" : "\n(game folder doesn't exist yet — created on Bake; check the path if this looks wrong)"),
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

    // An entry that CARRIES animation config is an animated entry, whatever its (window-state-fragile) 'animated'
    // bool says: a named clip, animation behaviors, bone filter, or an actually-baked clip GUID. Used to self-heal
    // the flag so a stale unticked checkbox can't silently downgrade a working animated unit to a static bake
    // (the "howitzers on their side" incident).
    internal static bool LooksAnimated(ModelDef d) =>
        !string.IsNullOrWhiteSpace(d.animClip) || d.fireOnAttack || d.deployOnStop || d.convertRig || d.animStateDriven ||
        !string.IsNullOrWhiteSpace(d.animateBones) ||
        (d.clip != null && d.clip.Length == 4 && !(d.clip[0] == 0 && d.clip[1] == 0 && d.clip[2] == 0 && d.clip[3] == 0));

    void OnSelectResource()
    {
        formDiffersFromRegistry = false;   // loading fresh = in sync by definition
        if (selected <= 0) { cur = new ModelDef(); loadedName = ""; status = ""; LoadPreview(null); return; }
        var e = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == existing[selected]);
        if (e == null) return;
        cur = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(e));   // clone so edits don't mutate the stored copy
        loadedName = cur.resourceName;   // the form's registry identity (survives a rename typed into the name field)
        bakedNotShipped = ShipStatus.IsBakedNotShipped(loadedName);
        status = "Loaded '" + e.resourceName + "'. Edit + Bake; leave Model file empty to re-bake with new settings.";
        if (!cur.animated && LooksAnimated(cur))
        {
            cur.animated = true;   // self-heal: the entry carries animation config, so it IS animated
            status += "\nRe-marked ANIMATED (the entry carries a clip/animation behaviors — the flag had been lost).";
        }
        LoadPreview(cur.resourceName);
    }

    // Re-probe only when the model-file path changes (OnGUI runs every frame; file I/O must not).
    void EnsureAnimProbe(string file)
    {
        file = file ?? "";
        if (file == animProbeFile) return;
        animProbeFile = file;
        animProbeState = ProbeAnimation(file);
        (animClips, animBonePrefixes) = InspectModel(file);   // populate the Clip / Bones pickers
    }

    // Read clip names + bone-name prefixes from the model, for the Pick dropdowns. glTF/GLB only (no Blender); returns
    // empties for FBX/.blend (fields stay manual). Primary parse is Newtonsoft (SDK-provided; robust on any valid glTF,
    // where JsonUtility silently fails) — it maps skin joints -> node names so bone COUNTS are accurate. A scoped
    // bracket-matching fallback (NamesInArray) handles truncated/odd JSON with no dependency. Clips = animations[].name;
    // bones grouped into prefixes (text before the first _ . - or space) with counts.
    // Guess the "Fix 100× oversize (FBX unit scale)" default from the model's TRUE final size (POSITION accessor extent ×
    // node world-scale, exactly what glbconv would report). Rationale (proven on 2 models, mechanism-backed): a metre-scale
    // model (~2u gun) hits the metre→cm FBX-unit issue and needs the fix ON; a tiny-authored model (a GLB with a 0.01 root
    // node scale → ~0.0025u, e.g. the drone) is re-inflated by Blender's FBX export and bakes correct with the fix OFF.
    // Best-effort: glTF/GLB only (FBX/.blend/OBJ can't be read cheaply → sz=0, caller keeps the existing value). Node
    // `matrix` transforms are not decomposed (→ sz=0, no guess) — most rigged glTF use TRS.
    internal static bool SuggestUnitFix(string file, out float trueSize)
    {
        trueSize = 0f;
        try
        {
            string ext = System.IO.Path.GetExtension(file ?? "").ToLowerInvariant();
            string json = ext == ".glb" ? ReadGlbJson(file) : (ext == ".gltf" ? System.IO.File.ReadAllText(file) : null);
            if (json == null) return false;
            var root = JObject.Parse(json);
            var nodes = root["nodes"] as JArray; var meshes = root["meshes"] as JArray; var accessors = root["accessors"] as JArray;
            if (nodes == null || meshes == null || accessors == null) return false;
            // largest POSITION extent of a mesh (in its own space)
            float MeshExtent(int mi)
            {
                float e = 0f;
                foreach (var prim in (meshes[mi]?["primitives"] as JArray ?? new JArray()))
                {
                    var ai = (int?)prim["attributes"]?["POSITION"]; if (ai == null || ai < 0 || ai >= accessors.Count) continue;
                    var a = accessors[ai.Value]; var mn = a["min"] as JArray; var mx = a["max"] as JArray;
                    if (mn == null || mx == null || mn.Count < 3 || mx.Count < 3) continue;
                    for (int k = 0; k < 3; k++) e = Mathf.Max(e, Mathf.Abs((float)mx[k] - (float)mn[k]));
                }
                return e;
            }
            // DFS from the scene roots, accumulating uniform-ish world scale; track max(meshExtent × worldScale)
            float best = 0f;
            var child = new HashSet<int>();
            foreach (var n in nodes) if (n["children"] is JArray ch) foreach (var c in ch) child.Add((int)c);
            var stack = new Stack<KeyValuePair<int, float>>();
            for (int i = 0; i < nodes.Count; i++) if (!child.Contains(i)) stack.Push(new KeyValuePair<int, float>(i, 1f));
            int guard = 0;
            while (stack.Count > 0 && guard++ < 100000)
            {
                var kv = stack.Pop(); var n = nodes[kv.Key];
                float ns = 1f; var s = n["scale"] as JArray;
                if (s != null && s.Count == 3) ns = ((float)s[0] + (float)s[1] + (float)s[2]) / 3f;   // uniform-ish average
                float ws = kv.Value * ns;
                var mesh = (int?)n["mesh"]; if (mesh != null && mesh >= 0 && mesh < meshes.Count) best = Mathf.Max(best, MeshExtent(mesh.Value) * ws);
                if (n["children"] is JArray chn) foreach (var c in chn) stack.Push(new KeyValuePair<int, float>((int)c, ws));
            }
            if (best <= 1e-6f) return false;   // no positional data (or matrix-only nodes) → don't guess
            trueSize = best;
            return best >= 0.1f;   // metre-scale → fix ON; tiny-authored → OFF
        }
        catch { trueSize = 0f; return false; }
    }

    // clip name -> human length ("frames 0..250 (10.4s @24fps)"), PER FILE — the Pick dropdowns append it so slicing
    // ranges (clip[start..end]) are discoverable without opening Blender. Keyed "file|clip" because SEVERAL windows
    // (Factory + Lab, both visible) inspect DIFFERENT files: a single last-writer dict got wiped by whichever window
    // inspected last, so the other window's dropdown showed plain names.
    internal static readonly Dictionary<string, string> ClipLengths = new Dictionary<string, string>();
    internal static string ClipLengthOf(string file, string clip)
        => ClipLengths.TryGetValue((file ?? "") + "|" + clip, out var len) ? len : null;

    internal static (List<string>, List<KeyValuePair<string, int>>) InspectModel(string file)
    {
        var (c, p, _) = InspectModelFull(file);
        return (c, p);
    }

    // Full variant: also returns the individual bone NAMES (the prefixes lose per-bone precision — a donor-socket
    // parent must name ONE bone, "MW_T" not "MW"). Used by the Donor-sockets mapping dialog.
    internal static (List<string>, List<KeyValuePair<string, int>>, List<string>) InspectModelFull(string file)
    {
        var clips = new List<string>();
        var prefixes = new List<KeyValuePair<string, int>>();
        var allBones = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file)) return (clips, prefixes, allBones);
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            string json = ext == ".glb" ? ReadGlbJson(file) : (ext == ".gltf" ? System.IO.File.ReadAllText(file) : null);
            if (json == null) return (clips, prefixes, allBones);
            List<string> boneNames;
            try
            {
                // Robust parse: Newtonsoft handles any valid glTF, and maps skin joints -> node names by index so bone
                // COUNTS are accurate (the real bones, not every node). (JsonUtility silently fails on real glTF.)
                var root = JObject.Parse(json);
                clips = (root["animations"] as JArray)?.Select(a => (string)a["name"])
                    .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList() ?? new List<string>();
                // Clip LENGTHS for the Pick dropdowns — the answer to "how do I know the frame range for a
                // clip[start..end] slice?" glTF stores times in SECONDS; frames assume the Blender-standard 24 fps
                // export (verified exact on every model so far: deploy 10.417s=250f, Idle1 14.208s=341f).
                var accsJ = root["accessors"] as JArray;
                if (root["animations"] is JArray animsJ && accsJ != null)
                    foreach (var a in animsJ)
                    {
                        string nm = (string)a["name"]; if (string.IsNullOrEmpty(nm)) continue;
                        float maxSec = 0f;
                        foreach (var s in (a["samplers"] as JArray ?? new JArray()))
                        {
                            var ii = (int?)s["input"];
                            if (ii == null || ii < 0 || ii >= accsJ.Count) continue;
                            if (accsJ[ii.Value]?["max"] is JArray mxa && mxa.Count > 0)
                                maxSec = Mathf.Max(maxSec, (float)mxa[0]);
                        }
                        if (maxSec > 0f) ClipLengths[file + "|" + nm] = $"frames 0..{Mathf.RoundToInt(maxSec * 24f)}  ({maxSec:0.0}s @24fps)";
                    }
                var nodes = root["nodes"] as JArray;
                var joints = new HashSet<int>();
                if (root["skins"] is JArray skins)
                    foreach (var s in skins)
                        if (s["joints"] is JArray js)
                            foreach (var j in js) joints.Add((int)j);
                IEnumerable<string> bn = (joints.Count > 0 && nodes != null)
                    ? joints.Where(i => i >= 0 && i < nodes.Count).Select(i => (string)nodes[i]?["name"])
                    : (nodes?.Select(n => (string)n?["name"]) ?? Enumerable.Empty<string>());
                boneNames = bn.Where(n => !string.IsNullOrEmpty(n)).ToList();
            }
            catch (Exception ix)   // truncated / odd JSON -> zero-dependency bracket-matching fallback (glTF-specific)
            {
                Debug.LogWarning("[Factory] InspectModel: structured parse failed (" + ix.Message + ") — name-only fallback, no clip lengths.");
                clips = NamesInArray(json, "\"animations\"\\s*:\\s*\\[").Distinct().ToList();
                boneNames = NamesInArray(json, "\"nodes\"\\s*:\\s*\\[(?=\\s*\\{)");
            }
            prefixes = boneNames.Where(n => !string.IsNullOrEmpty(n)).GroupBy(PrefixOf)
                .Where(gr => !string.IsNullOrEmpty(gr.Key))
                .Select(gr => new KeyValuePair<string, int>(gr.Key, gr.Count()))
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
            allBones = boneNames.Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { }
        return (clips, prefixes, allBones);
    }

    // Collect every "name":"…" inside the JSON array opened by `openRegex` (matched through its '['), by tracking bracket
    // depth (strings respected) to find the array's matching ']'. Within that array the only "name" fields are the
    // entities' names (glTF animation channels/samplers and node transforms carry no "name").
    static List<string> NamesInArray(string json, string openRegex)
    {
        var res = new List<string>();
        var m = System.Text.RegularExpressions.Regex.Match(json, openRegex);
        if (!m.Success) return res;
        int i = m.Index + m.Length, start = i, depth = 1; bool inStr = false, esc = false;
        for (; i < json.Length && depth > 0; i++)
        {
            char c = json[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; }
            else if (c == '"') inStr = true;
            else if (c == '[' || c == '{') depth++;
            else if (c == ']' || c == '}') depth--;
        }
        string arr = json.Substring(start, System.Math.Max(0, i - 1 - start));
        foreach (System.Text.RegularExpressions.Match nm in System.Text.RegularExpressions.Regex.Matches(arr, "\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
            res.Add(nm.Groups[1].Value);
        return res;
    }

    // Read the donor fragment mesh names the plugin logged for this resource, from BepInEx/LogOutput.log. The plugin
    // emits "[Uni] <name> donor fragment[i] mesh='...'" (and "HID donor fragment") once per launch. We stream the WHOLE
    // log with a shared read (the running game holds it open) — the fragments are logged early (first unit load), so on a
    // big verbose log they're nowhere near the tail; a cheap substring pre-filter keeps the full scan fast. Deduped.
    static List<string> ReadDonorFragments(string resourceName)
    {
        var res = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(resourceName)) return res;
            string log = System.IO.Path.GetFullPath(System.IO.Path.Combine(ModelRegistry.ConfigDir, "..", "LogOutput.log"));
            if (!System.IO.File.Exists(log)) return res;
            var rx = new System.Text.RegularExpressions.Regex(
                @"\[Uni\] " + System.Text.RegularExpressions.Regex.Escape(resourceName) + @" (?:HID )?donor fragment\[\d+\] mesh='([^']*)'");
            var seen = new HashSet<string>();
            // Scan the WHOLE log (shared-read, so it works while the game holds it open). The plugin logs each donor
            // fragment ONCE per session, early (first unit load) — on a big verbose log (300 MB+) that's nowhere near the
            // tail, so tailing missed it. A cheap substring pre-filter keeps the regex off the millions of non-fragment
            // lines, so a full streaming scan stays quick (disk-bound, a couple of seconds even on a huge log).
            using (var fs = new System.IO.FileStream(log, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            using (var sr = new System.IO.StreamReader(fs))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.IndexOf("donor fragment[", System.StringComparison.Ordinal) < 0) continue;   // cheap pre-filter
                    var m = rx.Match(line);
                    if (m.Success) { var nm = m.Groups[1].Value; if (nm.Length > 0 && seen.Add(nm)) res.Add(nm); }
                }
            }
        }
        catch { }
        return res;
    }

    // Prefix = the name up to the first separator (_ . - space); "prop_1_jnt" -> "prop", "Center" -> "Center".
    static string PrefixOf(string bone)
    {
        if (string.IsNullOrEmpty(bone)) return "";
        int i = bone.IndexOfAny(new[] { '_', '.', '-', ' ' });
        return i > 0 ? bone.Substring(0, i) : bone;
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

    // Check a GLB's JSON chunk for a non-empty "animations" array.
    static int ProbeGlb(string file)
    {
        string json = ReadGlbJson(file);
        return json == null ? 0 : (HasGltfAnim(json) ? 1 : 2);
    }

    // Extract the JSON (first) chunk of a binary glTF as a string, or null if it isn't a GLB.
    static string ReadGlbJson(string file)
    {
        using (var fs = System.IO.File.OpenRead(file))
        using (var br = new System.IO.BinaryReader(fs))
        {
            if (fs.Length < 20 || br.ReadUInt32() != 0x46546C67u) return null;   // "glTF" magic
            br.ReadUInt32(); br.ReadUInt32();                                    // version, total length
            uint clen = br.ReadUInt32(); uint ctype = br.ReadUInt32();           // first chunk = JSON
            if (ctype != 0x4E4F534Au) return null;                              // "JSON"
            var bytes = br.ReadBytes((int)System.Math.Min(clen, 16u * 1024 * 1024));
            return System.Text.Encoding.UTF8.GetString(bytes);
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
    // Is this target pawn a BOAT? Read the game's own characteristic — AnimationCapabilityProfile 7 (Boat) on the
    // PresentationPawnDefinition — never the name (user rule; the same signal the runtime's ship category uses).
    // Cached per name: the first lookup scans the definition databases, later ones are a dictionary hit.
    static readonly Dictionary<string, bool> boatPawnCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    internal static bool IsBoatPawn(string pawnDescription)
    {
        string key = (pawnDescription ?? "").Trim();
        if (key.Length == 0) return false;
        if (boatPawnCache.TryGetValue(key, out bool cached)) return cached;
        bool boat = false;
        foreach (var guid in AssetDatabase.FindAssets("PresentationPawnDefinition"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".asset")) continue;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && o.GetType().Name == "PresentationPawnDefinition" && o.name == key)
                {
                    var p = new SerializedObject(o).FindProperty("AnimationCapabilityProfile");
                    boat = p != null && p.intValue == 7;   // 7 = Boat in the game's profile enum
                    boatPawnCache[key] = boat;
                    return boat;
                }
        }
        boatPawnCache[key] = false;
        return false;
    }

    // PACK VALIDATE — the pre-flight's editor half (Pack-Validator-Design Phase 1, built 2026-08-18): the pre-ship
    // gate. ONE shared rule core (Haf.Schema.PackValidator — unit-tested in the plugin repo; the plugin re-runs the
    // same rules at boot on the player's machine) with the lookups only the editor can answer pre-ship: the real
    // pawn-name list (the Pick dropdown's source), file existence in the deployed pack + legacy shared dirs, and
    // bone names against each entry's BAKED skeleton asset.
    sealed class EditorValidationCtx : Haf.Schema.IValidationContext
    {
        public System.Collections.Generic.HashSet<string> Pawns;
        public string SkeletonPath;
        Array bones; bool bonesLoaded;
        public bool? PawnExists(string p) => Pawns == null ? (bool?)null : Pawns.Contains(p);
        public bool? SoundFileExists(string f) => FileIn("sounds", "haf_sounds", f);
        public bool? SkinFileExists(string f) => FileIn("skins", "haf_skins", f);
        static bool? FileIn(string sub, string legacy, string f) =>
            System.IO.File.Exists(System.IO.Path.Combine(ModelRegistry.PackLiveDir, sub, f)) ||
            System.IO.File.Exists(System.IO.Path.Combine(ModelRegistry.ConfigDir, legacy, f));
        public bool? BoneExists(string subName)
        {
            if (!bonesLoaded)
            {
                bonesLoaded = true;
                var sk = AssetDatabase.LoadMainAssetAtPath(SkeletonPath);
                bones = sk == null ? null : ReflectMember(sk, "BoneInfos") as Array;
            }
            if (bones == null) return null;   // no baked skeleton (static borrow / retex / not yet baked) — can't judge
            foreach (var b in bones)
            {
                var n = (b == null ? null : ReflectMember(b, "Name"))?.ToString() ?? "";
                if (n.IndexOf(subName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
        static object ReflectMember(object o, string name)
        {
            var t = o.GetType();
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var fi = t.GetField(name, F); if (fi != null) return fi.GetValue(o);
            var pi = t.GetProperty(name, F); return pi != null ? pi.GetValue(o, null) : null;
        }
    }

    // Shared core — the button, the pre-bake check, AND the headless mod build (HAF.Cli.BuildMod) all run this.
    internal static string ValidatePackCore(out int warns, out int errors, out int entryCount)
    {
        warns = 0; errors = 0; entryCount = 0;
        var defs = ModelRegistry.Load();
        if (defs == null || defs.Count == 0) return "";
        entryCount = defs.Count;
        var pawnArr = GatherPawnNames();
        var pawns = pawnArr != null && pawnArr.Length > 0 ? new System.Collections.Generic.HashSet<string>(pawnArr) : null;
        var sb = new System.Text.StringBuilder();

        // PACK WRAPPER first (2026-08-23): modId / schemaVersion / dependsOn / loadAfter / overrides. These rules
        // existed nowhere until now — a broken wrapper failed soft on the PLAYER's machine and was named only in
        // haf_load_report.txt, which the author never sees. They come first because a wrapper mistake can cost the
        // whole pack (an unsatisfiable dependsOn means it is SKIPPED outright), where an entry mistake costs one unit.
        var wrapperIssues = Haf.Schema.PackValidator.ValidatePack(new Haf.Schema.PackValidator.PackWrapper
        {
            ModId = ModelRegistry.PackModId,
            SchemaVersion = ModelRegistry.PackSchemaVersion,
            DependsOn = ModelRegistry.PackDependsOn,
            LoadAfter = ModelRegistry.PackLoadAfter,
            Overrides = ModelRegistry.PackOverrides
                .Select(o => new Haf.Schema.PackValidator.PackOverrideRef { ModId = o.modId, Pawn = o.pawnDescription }).ToList(),
        });
        if (wrapperIssues.Count > 0)
        {
            sb.AppendLine($"pack wrapper ('{ModelRegistry.PackModId}'):");
            foreach (var i in wrapperIssues)
            {
                sb.AppendLine("    " + i);
                if (i.Severity == Haf.Schema.ValidationSeverity.Error) errors++; else warns++;
            }
        }

        foreach (var d in defs)
        {
            var ctx = new EditorValidationCtx { Pawns = pawns, SkeletonPath = "Assets/Resources/" + d.resourceName + "_Skeleton.asset" };
            var issues = Haf.Schema.PackValidator.ValidateEntry(d, ctx);
            if (issues.Count == 0) continue;
            sb.AppendLine($"'{d.resourceName}' (pawn '{d.pawnDescription}'):");
            foreach (var i in issues)
            {
                sb.AppendLine("    " + i);
                if (i.Severity == Haf.Schema.ValidationSeverity.Error) errors++; else warns++;
            }
        }
        return sb.ToString();
    }

    void ValidatePack()
    {
        // try/catch + registry path in the output (2026-08-18 drill: "validate detects nothing" — a headless run of
        // the same core on the same file found the planted fault, so the editor side had failed INVISIBLY. A
        // validator that can die silently is the exact disease it exists to cure.)
        try
        {
            string detail = ValidatePackCore(out int warns, out int errors, out int count);
            if (count == 0) { status = "Validate pack: no entries in the registry (" + ModelRegistry.SourcePath + ")."; return; }
            string summary = $"Validate pack: {count} entr(y/ies) — {warns} warning(s), {errors} error(s).";
            if (detail.Length > 0) Debug.LogWarning("[Validate] " + summary + " (registry: " + ModelRegistry.SourcePath + ")\n" + detail);
            else Debug.Log("[Validate] " + summary + " Clean. (registry: " + ModelRegistry.SourcePath + ")");
            status = summary + (detail.Length > 0 ? " Details in the Console." : " Clean — ready to ship.");
            // RESULTS IN A DIALOG (2026-08-18 drill: "I expected it to appear in the dialog instead") — a validation
            // you clicked for answers to your face. Long lists are truncated here; the Console keeps the full,
            // copyable record.
            const int DialogMax = 1600;
            string body = detail.Length == 0
                ? $"All {count} entries validated clean — ready to ship.\n\nRegistry: {ModelRegistry.SourcePath}"
                : summary + "\n\n" + (detail.Length > DialogMax ? detail.Substring(0, DialogMax) + "\n… (full list in the Console)" : detail);
            EditorUtility.DisplayDialog("Validate pack", body, "OK");
        }
        catch (Exception ex)
        {
            status = "Validate pack FAILED: " + ex.Message + " — full stack in the Console.";
            Debug.LogError("[Validate] " + ex);
        }
    }

    internal static string[] GatherPawnNames()
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
    internal static string DeriveResourceName(string pawnName)
    {
        if (string.IsNullOrEmpty(pawnName)) return "";
        var parts = pawnName.Split('_');
        int end = parts.Length - 1;
        if (end > 0 && int.TryParse(parts[end], out _)) end--;
        return end >= 0 ? parts[end] : pawnName;
    }

    // 'Reuse extracted' is a CACHE, not a switch: these are the settings the ANIMATED Blender step consumes — if any
    // of them changed vs the saved entry, reusing the old FBX would silently ignore the change (the "rotation doesn't
    // respond" trap). The bake then re-runs the Blender step for that one bake; the ticked checkbox itself is kept as
    // the user's fast-iteration preference. A never-baked entry always slims.
    internal static bool AnimatedSlimInputsChanged(ModelDef cur)
    {
        var e = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == cur.resourceName);
        if (e == null) return true;
        return cur.rotation != e.rotation
            || cur.targetTris != e.targetTris
            || (cur.animClip ?? "") != (e.animClip ?? "")
            || (cur.animateBones ?? "") != (e.animateBones ?? "")
            || cur.materialMode != e.materialMode
            || cur.convertRig != e.convertRig
            || cur.autoGroundWheels != e.autoGroundWheels
            || cur.keepTranslations != e.keepTranslations
            || (cur.socketBones ?? "") != (e.socketBones ?? "")
            || cur.animStateDriven != e.animStateDriven
            || (cur.animClipMove ?? "") != (e.animClipMove ?? "")
            || (cur.animClipAfter ?? "") != (e.animClipAfter ?? "")
            || (cur.animClipAttack ?? "") != (e.animClipAttack ?? "")
            || (cur.animClipCombat ?? "") != (e.animClipCombat ?? "")
            || (cur.animClipPreMove ?? "") != (e.animClipPreMove ?? "")
            || (cur.animClipIdle ?? "") != (e.animClipIdle ?? "")
            || (cur.animClipIdleAlt ?? "") != (e.animClipIdleAlt ?? "")
            || (cur.animClipIdleAlt2 ?? "") != (e.animClipIdleAlt2 ?? "")
            || (cur.modelFile ?? "") != (e.modelFile ?? "");
    }

    // Map a registry ModelDef to a BakeConfig. SHARED so the bake smoke test (BakeSmokeTest.cs) bakes through the exact
    // same config path as the Bake button — a parallel copy would silently drift from what ships.
    internal static BakeConfig ConfigFor(ModelDef cur) => new BakeConfig
    {
        resourceName = cur.resourceName, modelFile = cur.modelFile, pawnDescription = cur.pawnDescription,
        rotationEuler = cur.rotation, positionOffset = cur.position, size = cur.size,
        normals = (NormalsMode)cur.normalsMode, smoothingAngle = cur.smoothingAngle, convertGrid = cur.convertGrid,
        reuseExtracted = cur.reuseExtracted, doubleSided = cur.doubleSided, windingFix = cur.windingFix, heightUV = cur.heightUV, targetTris = cur.targetTris,
        albedoBrightness = cur.albedoBrightness, albedoSaturation = cur.albedoSaturation, keepBlack = cur.keepBlack, materialMode = cur.materialMode,
        atlasMaxDim = cur.atlasMaxDim <= 0 ? 512 : cur.atlasMaxDim,
        stripParts = cur.stripParts,
        animated = cur.animated, animClip = (cur.animClip ?? "").Trim(), animateBones = (cur.animateBones ?? "").Trim(), staticParts = (cur.staticParts ?? "").Trim(), localNodeAnim = cur.localNodeAnim, animUnitFix = cur.animUnitFix, convertRig = cur.convertRig, autoGroundWheels = cur.autoGroundWheels, keepTranslations = cur.keepTranslations, socketBones = (cur.socketBones ?? "").Trim(),
        deployConvert = cur.deployConvert, deployStart = cur.deployStart, deployEnd = cur.deployEnd,
        deployStrip = (cur.deployStrip ?? "").Trim(), deployReadyFrame = (cur.deployReadyFrame ?? "").Trim(), deployLegScale = (cur.deployLegScale ?? "").Trim(), deployBarrelScale = (cur.deployBarrelScale ?? "").Trim(),
        deployRecoil = (cur.deployRecoil ?? "").Trim(), deployRecoilStep = (cur.deployRecoilStep ?? "").Trim(), deployRecoilMag = (cur.deployRecoilMag ?? "").Trim(), deployArcR = (cur.deployArcR ?? "").Trim(), deployRecoilReturn = (cur.deployRecoilReturn ?? "").Trim(), deploySlamDeg = (cur.deploySlamDeg ?? "").Trim(), deploySlamSettle = (cur.deploySlamSettle ?? "").Trim(),
        deployWheelBones = (cur.deployWheelBones ?? "").Trim(), deployWheelAxis = (cur.deployWheelAxis ?? "").Trim(), deployWheelFrames = (cur.deployWheelFrames ?? "").Trim(), deployWheelDegrees = (cur.deployWheelDegrees ?? "").Trim(), deployStripExtra = (cur.deployStripExtra ?? "").Trim(),
        animStateDriven = cur.animStateDriven, animClipMove = (cur.animClipMove ?? "").Trim(), animClipAfter = (cur.animClipAfter ?? "").Trim(), animClipAttack = (cur.animClipAttack ?? "").Trim(), animClipCombat = (cur.animClipCombat ?? "").Trim(), animClipPreMove = (cur.animClipPreMove ?? "").Trim(), animClipIdle = (cur.animClipIdle ?? "").Trim(), animClipIdleAlt = (cur.animClipIdleAlt ?? "").Trim(), animClipIdleAlt2 = (cur.animClipIdleAlt2 ?? "").Trim(),
        keepTexture = cur.reuseExtracted   // on the ANIMATED path the checkbox's ONLY meaning is 'protect the hand-edited extracted texture'
    };

    // ENFORCED OWNERSHIP (2026-08-01, fail-safe AND fully type-safe). The Model Factory owns only the model geometry /
    // transform / shading / its own bake GUIDs + the runtime toggles it actually shows; the ANIMATION / sound / skin /
    // resize fields belong to their own windows. Whenever THIS window writes the registry, we must keep their freshest
    // SAVED values so a stale Factory copy can't clobber a value changed elsewhere since `cur` was loaded here.
    //
    // Structure mirrors the Lab's already-fail-safe RebaseOnRegistry: START from the saved entry (so every field the
    // Factory does NOT own is preserved), then OVERLAY only the Factory-owned fields from the form. Overwriting cur in
    // place from the entry's JSON copies ALL fields — including any newly-added one — so the historical "forgot to add
    // the field to the rebase list" drift (keepTranslations burned three T-62 bakes; animPhaseSpread re-synced pawns;
    // 'disabled' silently un-disabled; ~13 Sound fields never listed) is structurally impossible: a new field is kept
    // BY DEFAULT. And unlike a reflection loop, every overlay line below is a plain compile-checked assignment — a typo
    // or a renamed field is a BUILD error, not a silent revert.
    // Persist the form to the registry with NO bake — see the "Save settings" button. Runtime-only knobs take
    // effect on the next mod rebuild; baked assets (skeleton/atlas/clips) are carried over untouched by the
    // ownership rebase, so this can never orphan them.
    void SaveSettingsOnly()
    {
        if (BlockedByRenameClobber()) return;
        RebaseLabOwnedOnRegistry();
        bool saved = ModelRegistry.Upsert(cur);
        if (saved) { formDiffersFromRegistry = false; loadedName = cur.resourceName; }   // form is now the saved truth
        string renameNote = saved ? FinishRename() : "";
        RefreshList();
        selected = System.Array.IndexOf(existing, cur.resourceName); if (selected < 0) selected = 0;
        status = (saved
            ? $"Saved '{cur.resourceName}' settings (no bake). Rebuild the mod to apply them in-game."
            : "REGISTRY SAVE FAILED (see Console) — settings were NOT written.") + renameNote;
        Debug.Log("[Factory] " + status);
        GUI.FocusControl(null);
    }

    // The registry key the form was LOADED under. `existing[selected]` is the same reliable signal the Remove
    // button keys on (see the Remove handler): it tracks the loaded entry and is "<New>"/absent for a fresh or
    // cloned form (selected<=0), so a Clone or a brand-new entry is NEVER mistaken for a rename. It differs from
    // cur.resourceName ONLY when the user edited the Resource-name field of a loaded entry = a RENAME.
    string LoadedResourceKey() => (selected > 0 && selected < existing.Length) ? existing[selected] : null;

    // Pre-Upsert rename guard. A rename onto a name a DIFFERENT model already owns would make Upsert silently
    // overwrite that model — block it (sets a status; the caller returns without writing). Not a rename, or the
    // new name is free → returns false and the save proceeds unchanged.
    // THE ONE QUESTION EVERY WRITE PATH MUST ASK: would this Upsert destroy a DIFFERENT entry?
    // Upsert is a blind `RemoveAll(name) + Add`, so writing under a name that belongs to someone else deletes them
    // and orphans their baked assets. Three of the four write paths asked; "Make static…" did not, and it sat in no
    // disabled scope either — so with the red "Not allowed" box visibly on screen, one click destroyed the colliding
    // entry (review 2026-08-22). Rather than bolt a fourth hand-check on, this is now the single definition, and it
    // covers BOTH shapes:
    //   * RENAME  — a loaded entry retyped to a name that already exists (the original case), and
    //   * NEW     — ＜new model＞ typed straight onto an existing name, which the rename test could never see
    //               because there is no previous key to compare against. That is the same hole one door along.
    // Case-insensitive throughout: the registry key is a filename on a case-insensitive filesystem.
    bool BlockedByRenameClobber()
    {
        string oldKey = LoadedResourceKey();
        string name = cur.resourceName ?? "";
        if (name.Length == 0) return false;                                                          // empty name: other validation owns it
        if (!string.IsNullOrEmpty(oldKey) && string.Equals(oldKey, name, StringComparison.OrdinalIgnoreCase))
            return false;                                                                            // writing over ITSELF — the normal edit
        if (!ModelRegistry.Load().Any(x => string.Equals(x.resourceName, name, StringComparison.OrdinalIgnoreCase)))
            return false;                                                                            // the name is free
        status = $"A model named '{name}' already exists — rename to a free name, or Remove that model first. Nothing was written.";
        Debug.LogWarning("[Factory] " + status);
        return true;
    }

    // Apply a rename as a RENAME, not a silent second copy. Without this a rename kept the OLD entry (a duplicate)
    // and orphaned its baked assets, because Upsert adds under the new name while the source entry lingers. The
    // rebase + GUID-carry key on LoadedResourceKey(), so the new entry already inherits the source's Lab-owned
    // fields + baked GUIDs (Unity GUIDs are filename-independent, so a no-bake rename still resolves in-game).
    // Call AFTER a successful Upsert; returns a status suffix ("" when it isn't a rename).
    string FinishRename()
    {
        string oldKey = LoadedResourceKey();
        if (string.IsNullOrEmpty(oldKey) || oldKey == cur.resourceName) return "";
        ModelRegistry.Remove(oldKey);
        return $"  (Renamed from '{oldKey}' — old registry entry removed.)";
    }

    void RebaseLabOwnedOnRegistry()
    {
        var regE = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == (LoadedResourceKey() ?? cur.resourceName));
        if (regE == null) return;
        var form = JsonUtility.FromJson<ModelDef>(JsonUtility.ToJson(cur));   // snapshot the form — the Factory-owned values live here
        JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(regE), cur);         // cur := the saved entry IN PLACE (every field preserved, no reference swap)
        // …then re-apply ONLY the fields the Factory owns, from the form:
        cur.resourceName = form.resourceName; cur.pawnDescription = form.pawnDescription; cur.modelFile = form.modelFile;
        cur.rotation = form.rotation; cur.position = form.position; cur.size = form.size;
        cur.normalsMode = form.normalsMode; cur.smoothingAngle = form.smoothingAngle; cur.convertGrid = form.convertGrid;
        cur.reuseExtracted = form.reuseExtracted; cur.doubleSided = form.doubleSided; cur.windingFix = form.windingFix; cur.heightUV = form.heightUV;
        cur.albedoBrightness = form.albedoBrightness; cur.albedoSaturation = form.albedoSaturation; cur.keepBlack = form.keepBlack;
        cur.materialMode = form.materialMode; cur.atlasMaxDim = form.atlasMaxDim; cur.targetTris = form.targetTris;
        cur.stripParts = form.stripParts; cur.hideMeshes = form.hideMeshes;
        cur.skel = form.skel; cur.atlas = form.atlas; cur.clip = form.clip;
        cur.respawnAfterLoad = form.respawnAfterLoad; cur.freezeDonorAnim = form.freezeDonorAnim; cur.silenceDonorVfx = form.silenceDonorVfx;
        cur.useDonorClip = form.useDonorClip;
        cur.turnRate = form.turnRate; cur.turnBank = form.turnBank;
        cur.hugDrop = form.hugDrop; cur.hugLookahead = form.hugLookahead;
        cur.combatZ = form.combatZ;
        // MAINTENANCE TRAP (2026-08-19, drill-caught the day combatZ landed): every NEW field edited in the
        // Factory MUST be added to this Factory-owned list — a field missing here is silently RESET to the
        // registry value on every Save ("I tried to save but the offset reset to 0"). The schema-parity gate
        // does NOT check this list. When adding a Factory field: schema, regex fallback, UI, and THIS list.
        cur.animated = regE.animated || form.animated;   // one-way OR: a prior animated bake wins; a static Factory re-bake must not silently un-animate the entry
    }

    // ANIMATED -> STATIC downgrade that STICKS (user verdict 2026-07-26: "when I removed the animation
    // configuration I expect it to be removed, not get cursed"). The bake-time ownership rebase pulls the SAVED
    // animation config back before the static guard runs, so any in-window untick was silently resurrected and
    // deleting the whole entry was the only escape. This clears the animation-owned fields IN THE SAVED REGISTRY
    // (immediately, registry-only) — after which the rebase pulls cleared values and the static path is native.
    void MakeStatic()
    {
        // Ask BEFORE the confirm dialog: being told "this would destroy another entry" is more useful than being
        // asked to confirm an action that is about to be refused. This is the door the 08-22 review found unlocked.
        if (BlockedByRenameClobber()) return;
        if (!EditorUtility.DisplayDialog("Make static?",
            $"Delete '{cur.resourceName}' animation configuration from the saved registry?\n\n" +
            "Removed: clip + state roles, deploy conversion, behaviors (fire/deploy), turret/muzzle/sockets, " +
            "hand prop, Convert-rig/Keep-translations flags. Baked clip assets on disk stay until the next Bake " +
            "(which will be STATIC).\n\nThe model file, transform, size and shading settings are kept.",
            "Make static", "Cancel")) return;
        cur.animated = false;
        cur.animClip = ""; cur.animateBones = ""; cur.staticParts = ""; cur.localNodeAnim = false; cur.animUnitFix = false;
        cur.convertRig = false; cur.autoGroundWheels = false; cur.keepTranslations = false;
        cur.deployConvert = false; cur.deployStart = 0; cur.deployEnd = 0; cur.deployStrip = ""; cur.deployStripExtra = "";
        cur.deployReadyFrame = ""; cur.deployLegScale = ""; cur.deployBarrelScale = "";
        cur.deployRecoil = ""; cur.deployRecoilStep = ""; cur.deployRecoilMag = ""; cur.deployArcR = "";
        cur.deployRecoilReturn = ""; cur.deploySlamDeg = ""; cur.deploySlamSettle = "";
        cur.deployWheelBones = ""; cur.deployWheelAxis = ""; cur.deployWheelFrames = ""; cur.deployWheelDegrees = "";
        cur.animStateDriven = false; cur.animClipMove = ""; cur.animClipAfter = ""; cur.animClipAttack = "";
        cur.animClipCombat = ""; cur.animClipPreMove = ""; cur.animClipIdle = ""; cur.animClipIdleAlt = ""; cur.animClipIdleAlt2 = "";
        cur.clip = new int[4]; cur.clipMove = new int[4]; cur.clipAfter = new int[4]; cur.clipAttack = new int[4];
        cur.clipCombat = new int[4]; cur.clipPreMove = new int[4]; cur.clipIdle = new int[4]; cur.clipIdleAlt = new int[4]; cur.clipIdleAlt2 = new int[4];
        cur.idleAltInterval = 0; cur.attackRepeats = 0; cur.clearAimLayer = false;
        cur.turretBone = ""; cur.turretAxis = -1; cur.muzzleBone = ""; cur.muzzleOffset = ""; cur.socketBones = "";
        // 2026-08-19 hand-list audit: these three survived Make static — gunElev is applied at RUNTIME to every
        // non-donor entry, so a leftover gunElevMax kept elevating a made-static gun (the exact "cursed leftover"
        // class this function was built to kill). animPhaseSpread is dormant on statics; reset for hygiene.
        cur.gunElevMax = 0f; cur.gunElevAxis = 0; cur.gunElevRise = 1f; cur.gunElevHold = 1f; cur.gunElevFall = 1f; cur.animPhaseSpread = 0.5f;
        cur.handPropName = ""; cur.handPropGuid = ""; cur.handPropMat = ""; cur.handPropBone = ""; cur.handPropAngles = "";
        cur.fireOnAttack = false; cur.deployOnStop = false;
        cur.deployPoseTime = 0f; cur.deploySpeed = 0f; cur.recoilSpeed = 0f;
        bool saved = ModelRegistry.Upsert(cur);
        if (saved) { formDiffersFromRegistry = false; loadedName = cur.resourceName; }   // form is now the saved truth
        RefreshList();
        status = saved
            ? $"'{cur.resourceName}' animation configuration DELETED from the registry — the next Bake is static. (Relaunch also stops the animated override.)"
            : "REGISTRY SAVE FAILED (see Console) — the animation config was NOT removed.";
        Debug.Log("[Factory] " + status);
    }

    // Registry-only save — the button the runtime section deserved all along (user request, 2026-07-26, after
    // the hideMeshes spike-fix had no way to save without a full re-bake): writes the entry with the current
    // Factory-owned fields, Lab-owned fields rebased from the registry, and the BAKED GUIDs taken from the
    // registry too (assets are untouched, so this window's possibly-stale GUID copies must never be written).
    void SaveOnly()
    {
        cur.resourceName = (cur.resourceName ?? "").Trim();
        cur.pawnDescription = (cur.pawnDescription ?? "").Trim();
        cur.modelFile = (cur.modelFile ?? "").Trim();
        cur.stripParts = (cur.stripParts ?? "").Trim();
        cur.hideMeshes = (cur.hideMeshes ?? "").Trim();
        if (BlockedByRenameClobber()) return;
        var regE = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == (LoadedResourceKey() ?? cur.resourceName));
        RebaseLabOwnedOnRegistry();
        bool modelFileChanged = false;
        if (regE != null)
        {
            cur.skel = regE.skel; cur.atlas = regE.atlas; cur.clip = regE.clip;
            cur.clipMove = regE.clipMove; cur.clipAfter = regE.clipAfter; cur.clipAttack = regE.clipAttack;
            cur.clipCombat = regE.clipCombat; cur.clipPreMove = regE.clipPreMove; cur.clipIdle = regE.clipIdle;
            cur.clipIdleAlt = regE.clipIdleAlt; cur.clipIdleAlt2 = regE.clipIdleAlt2;
            if (regE.animated) cur.animated = true;
            modelFileChanged = !string.Equals(regE.modelFile ?? "", cur.modelFile, StringComparison.OrdinalIgnoreCase);
        }
        bool saved = ModelRegistry.Upsert(cur);
        if (saved) { formDiffersFromRegistry = false; loadedName = cur.resourceName; }   // form is now the saved truth
        string renameNote = saved ? FinishRename() : "";
        RefreshList();
        selected = System.Array.IndexOf(existing, cur.resourceName); if (selected < 0) selected = 0;
        status = (saved
            ? $"Saved '{cur.resourceName}' (registry only, baked assets untouched). Relaunch the game — runtime fields " +
              // 2026-08-19 label-lies audit: this line claimed Position offset/Size "apply on load" unconditionally —
              // for STATIC entries both are baked into the mesh and silently need a Bake (the submarine-waterline trap).
              (cur.animated
                ? "(Hide donor meshes, Freeze/Silence, Position offset, Combat height) apply on load; bake-time fields still need a Bake."
                : "(Hide donor meshes, Freeze/Silence, Combat height, Turn ease/Hug) apply on load; Position offset and Size are BAKED on a static entry — they need a Bake to change.") +
              (modelFileChanged ? "  NOTE: the Model file differs from what was last baked — the assets on disk are still the old bake." : "")
            : "REGISTRY SAVE FAILED (see Console). Close whatever's locking the registry and retry.") + renameNote;
        Debug.Log("[Factory] " + status);
    }

    void DoBake()
    {
        // Snapshot the form so a FAILED bake keeps every setting (mirror of AnimationLabWindow.DoBake — the ownership
        // rebase + trims below mutate cur in place; a failure would otherwise revert the form and force re-entry).
        string formSnapshot = JsonUtility.ToJson(cur);
        cur.resourceName = (cur.resourceName ?? "").Trim();   // trim early so the rename guard compares clean names
        if (BlockedByRenameClobber()) return;                 // block a name collision BEFORE tearing down the preview / spending a bake
        // BAKE-TIME MODEL-FILE CONFIRM (2026-08-18, entry-state coherence fix #1 — the translation-cube-over-
        // Jagdpanzer ambush): a stale form Model-file once silently baked the WRONG MODEL over a good entry.
        // If the form's file differs from the SAVED entry's, ask loudly with both paths shown.
        var regEntry = ModelRegistry.Load().FirstOrDefault(x => x.resourceName == cur.resourceName);
        if (regEntry != null && !string.IsNullOrWhiteSpace(regEntry.modelFile) && !string.IsNullOrWhiteSpace(cur.modelFile) &&
            !string.Equals(regEntry.modelFile.Trim(), cur.modelFile.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            if (!EditorUtility.DisplayDialog("Model file differs from the saved entry",
                $"The form is about to bake:\n    {cur.modelFile}\n\nbut the SAVED registry entry uses:\n    {regEntry.modelFile}\n\n" +
                "A stale form file once silently overwrote a good bake with the wrong model. Bake with the FORM's file?",
                "Bake with the form's file", "Cancel"))
            { status = "Bake cancelled (model-file mismatch — ↻ Reload entry to take the registry's file)."; return; }
        }
        // PRE-BAKE VALIDATE (2026-08-18, user: "validate should also run before build"): the same shared rule core,
        // on the CURRENT form, at the exact moment authoring mistakes are made. Warnings NEVER block the bake
        // (fail-soft) — they land in the Console with the field named, while the model is still in front of you.
        // Bone checks run against the PREVIOUS bake's skeleton if one exists (a fresh model skips them — the
        // Validate-pack button and the plugin's boot pre-flight cover post-bake).
        try
        {
            var preIssues = Haf.Schema.PackValidator.ValidateEntry(cur, new EditorValidationCtx
            { Pawns = new System.Collections.Generic.HashSet<string>(GatherPawnNames()), SkeletonPath = "Assets/Resources/" + cur.resourceName + "_Skeleton.asset" });
            if (preIssues.Count > 0)
                Debug.LogWarning($"[Validate] '{cur.resourceName}' pre-bake: {preIssues.Count} issue(s) (bake proceeds — fix and re-Save/re-Bake):\n    "
                                 + string.Join("\n    ", preIssues));
        }
        catch (Exception vex) { Debug.LogWarning("[Validate] pre-bake validation failed (bake proceeds): " + vex.Message); }
        // Tear down the preview editor BEFORE baking. The baked prefab has an Animator, so the preview is a GameObjectInspector
        // with a live animator preview; the bake's delete-first (DeleteAsset _Model.prefab) would then null its target mid-bake,
        // and SaveAsPrefabAsset's OnPostprocessAllAssets fires InstantiateForAnimatorPreview(null) -> ArgumentException. Destroying
        // it up front means nothing watches the prefab while it's deleted; we rebuild the preview after the bake.
        LoadPreview(null);
        AnimationLabWindow.InvalidateFitPreviews();   // the Lab's combined fit view would go stale (magenta) on this bake — retire it; its Refresh rebuilds
        // Trim the text fields ON cur ITSELF, not just into the bake config: Upsert(cur) below persists cur, and a
        // pasted trailing space in pawnDescription used to bake fine but write the untrimmed string to the registry —
        // the plugin's substring match then never fired: "Baked ✓", model silently never injected (review finding E1).
        // Trimming cur keeps what's baked and what's registered identical.
        cur.resourceName = (cur.resourceName ?? "").Trim();
        cur.pawnDescription = (cur.pawnDescription ?? "").Trim();
        cur.modelFile = (cur.modelFile ?? "").Trim();
        cur.stripParts = (cur.stripParts ?? "").Trim();
        cur.hideMeshes = (cur.hideMeshes ?? "").Trim();
        cur.animClip = (cur.animClip ?? "").Trim();
        cur.animateBones = (cur.animateBones ?? "").Trim();
        cur.staticParts = (cur.staticParts ?? "").Trim();
        // ENFORCED OWNERSHIP (mirror of AnimationLabWindow.RebaseOnRegistry): the ANIMATION fields belong to the
        // Animation Lab — before baking, always take their freshest saved values from the registry so a stale Factory
        // copy can't clobber what the Lab configured (a Factory bake once silently dropped the Lab's Fix-100×,
        // shipping a 100×-giant soldier). This window contributes everything else (model file, transform, size, …).
        RebaseLabOwnedOnRegistry();
        // GUARD against a silent animated->static downgrade (the "howitzers on their side" incident). Two layers:
        // (1) the ENTRY carries animation config (clip/behaviors) -> it IS animated; self-heal the flag, no dialog.
        // (2) only the FILE has animation (a fresh rigged model, no config yet) -> unticked may be deliberate; ask.
        // A static bake would strip clip + behaviors and bake the (animated-path-ignored) Rotation offset into the
        // mesh, so the unit renders tipped over — never let that happen silently.
        EnsureAnimProbe(cur.modelFile);
        if (!cur.animated && LooksAnimated(cur))
        {
            cur.animated = true;
            Debug.Log("[Factory] " + cur.resourceName + ": re-marked ANIMATED before bake (entry carries animation config).");
        }
        if (!cur.animated && animProbeState == 1 &&
            !EditorUtility.DisplayDialog("Bake static?",
                "This model contains animation, but 'Animated (own rig + clip)' is UNTICKED.\n\n" +
                "Baking now produces a STATIC model: no clip, no fire/deploy behaviors, and the Rotation offset gets " +
                "baked into the mesh.\n\nBake static anyway?",
                "Bake static", "Cancel"))
        { status = "Bake cancelled — tick 'Animated (own rig + clip)' to bake the animated version."; return; }
        var cfg = ConfigFor(cur);
        if (cfg.animated)
        {
            // Geometry caching is AUTOMATIC on the animated path: the Blender step re-runs exactly when one of its
            // inputs changed (rotation/tris/clip/bones/material/model), regardless of the checkbox — the checkbox's
            // only meaning here is 'keep the hand-edited extracted texture' (cfg.keepTexture, set in ConfigFor).
            cfg.reuseExtracted = !AnimatedSlimInputsChanged(cur);
            if (!cfg.reuseExtracted) Debug.Log("[Factory] " + cur.resourceName + ": Blender-step settings changed — re-slimming (automatic).");
        }
        var r = cfg.animated ? UniversalBaker.BuildAnimated(cfg) : UniversalBaker.Build(cfg);
        if (!r.ok) { cur = JsonUtility.FromJson<ModelDef>(formSnapshot); status = "Bake FAILED (settings kept): " + r.error; return; }
        cur.skel = ModelRegistry.ParseGuid(r.skeletonGuid);
        cur.atlas = ModelRegistry.ParseGuid(r.atlasGuid);
        cur.clip = cfg.animated ? ModelRegistry.ParseGuid(r.clipGuid) : new int[4];   // static models carry {0,0,0,0}
        cur.clipMove = cfg.animated && cfg.animStateDriven ? ModelRegistry.ParseGuid(r.clipMoveGuid) : new int[4];
        cur.clipAfter = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipAfterGuid) ? ModelRegistry.ParseGuid(r.clipAfterGuid) : new int[4];
        cur.clipAttack = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipAttackGuid) ? ModelRegistry.ParseGuid(r.clipAttackGuid) : new int[4];
        cur.clipCombat = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipCombatGuid) ? ModelRegistry.ParseGuid(r.clipCombatGuid) : new int[4];
        cur.clipPreMove = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipPreMoveGuid) ? ModelRegistry.ParseGuid(r.clipPreMoveGuid) : new int[4];
        cur.clipIdle = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipIdleGuid) ? ModelRegistry.ParseGuid(r.clipIdleGuid) : new int[4];   // was DROPPED — a Factory bake shipped a dead idle-override GUID (the "forgot to deploy" trap); mirrors the Lab's capture
        cur.clipIdleAlt = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipIdleAltGuid) ? ModelRegistry.ParseGuid(r.clipIdleAltGuid) : new int[4];
        cur.clipIdleAlt2 = cfg.animated && cfg.animStateDriven && !string.IsNullOrEmpty(r.clipIdleAlt2Guid) ? ModelRegistry.ParseGuid(r.clipIdleAlt2Guid) : new int[4];
        bool saved = ModelRegistry.Upsert(cur);
        if (saved) { formDiffersFromRegistry = false; loadedName = cur.resourceName; }   // form is now the saved truth
        bakedNotShipped = ShipStatus.IsBakedNotShipped(loadedName);   // a fresh bake is always newer than the build — show the ship notice immediately
        string renameNote = saved ? FinishRename() : "";
        RefreshList();
        selected = System.Array.IndexOf(existing, cur.resourceName); if (selected < 0) selected = 0;
        if (!saved)
        {
            // The asset baked, but writing the registry entry failed (Save logged why). Say so plainly instead of a false
            // "Baked ✓" — otherwise the user assumes it's registered when the plugin will never see it. Re-bake retries.
            status = $"Baked '{cur.resourceName}', but the REGISTRY SAVE FAILED (see Console). The asset is baked; close " +
                     "whatever's locking haf_models.json (AV / indexer / the running game) and re-bake to write the entry.";
            Debug.LogError("[Factory] " + status);
            LoadPreview(cur.resourceName, forceReimport: true);
            return;
        }
        status = (cfg.animated
            ? $"Baked ANIMATED '{cur.resourceName}' -> '{cur.pawnDescription}'\nskeleton {r.skeletonGuid}\nclip {r.clipGuid}\nNow rebuild the mod + relaunch."
            : $"Baked '{cur.resourceName}' -> '{cur.pawnDescription}'  (raw bbox {r.bbox})\nskeleton {r.skeletonGuid}\nNow rebuild the mod + relaunch.") + renameNote;
        Debug.Log("[Factory] " + status);
        LoadPreview(cur.resourceName, forceReimport: true);   // show the just-baked model (force reimport so it isn't stale)
        AnimationLabWindow.RebuildFitPreviews();              // rebuild the Lab's (fit) preview from the fresh assets
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

// Searchable dropdown over parallel label/value arrays (label shown, value returned). Used for the Clip and Bone pickers.
class StringDropdown : AdvancedDropdown
{
    readonly string[] labels, values; readonly string title; readonly Action<string> onPick;
    readonly Dictionary<int, string> map = new Dictionary<int, string>();
    public StringDropdown(AdvancedDropdownState s, string[] labels, string[] values, string title, Action<string> onPick) : base(s)
    { this.labels = labels; this.values = values; this.title = title; this.onPick = onPick; minimumSize = new Vector2(260, 320); }
    protected override AdvancedDropdownItem BuildRoot()
    {
        var root = new AdvancedDropdownItem(title + " (" + labels.Length + ")");
        for (int i = 0; i < labels.Length; i++) { var it = new AdvancedDropdownItem(labels[i]); root.AddChild(it); map[it.id] = values[i]; }
        return root;
    }
    protected override void ItemSelected(AdvancedDropdownItem item) { if (map.TryGetValue(item.id, out var v)) onPick(v); }
}
