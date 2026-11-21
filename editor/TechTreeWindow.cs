using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

/// <summary>
/// Tech-tree viewer/editor. Edit mode adds position drag (snap to grid) and prereq
/// removal, staged in an in-memory overlay and committed on Save. Save materializes
/// each change: edit-in-place when the asset is under Databases/, copy-on-write into
/// New Additions when it's reference-only (TechTreeData.EnsureWritable). The +prereq
/// picker (step 4) and new-tech creation (step 5) are the remaining pieces.
///
/// LOCAL CHANGES to this file (it came from another modder's tool, see the note at TechPickerDropdown), 2026-11-21:
///  - The prerequisite RULE per technology. The game unlocks a technology on ANY ONE prerequisite (OR); HAF's Tech
///    Requires All list (TechRequireAllRegistry, applied by the plugin) names the ones that wait for ALL (AND). The
///    side strip shows which rule the selected technology has and switches it; the switch is staged like every other
///    edit and written to that list on Save. A node under the AND rule carries "AND" in its lower left corner.
///  - Scroll bars under and beside the canvas. They move the same pan as the middle-mouse drag.
///  - X and Y fields in the side strip: the selected node's position as numbers, staged like a drag.
///  - The selection is kept by name across a reload (it was the node object from before it).
///  - The canvas looks like the game's tech screen (asked for by the user, 2026-11-21): cards in the game's
///    proportions and spacing, and links routed by the game's own rule. See the GEOMETRY block and LinkPath.
///    A card shows its artwork where the project holds the image (TechTreeData.ArtPathOf, ArtRect).
/// </summary>
public class TechTreeWindow : EditorWindow
{
    // ── Pending overlay: staged, not-yet-saved edits per tech name ────────────
    class Pending
    {
        public int? X, Y;                // staged position (null = unchanged)
        public List<string> Prereqs;     // staged prereq list (null = unchanged)
        public bool? RequireAll;         // staged rule: true = ALL prerequisites (AND), false = any one (OR); null = unchanged
        public bool IsEmpty => X == null && Y == null && Prereqs == null && RequireAll == null;
    }
    readonly Dictionary<string, Pending> _pending = new();

    List<TechTreeData.Node> _nodes;
    Dictionary<string, TechTreeData.Node> _byName;
    TechTreeData.Node _selected;

    // The Tech Requires All list as this window last read it: the technologies under the AND rule.
    HashSet<string> _requireAll = new(StringComparer.Ordinal);
    bool _rulesUnreadable;    // the list could not be read: the rule is not shown and cannot be switched
    string _rulesVersion;     // TechRequireAllRegistry.LoadedVersion when _requireAll was read (null after a failed read)

    // mod path (editable, persisted)
    string _modPath = TechTreeData.DefaultModPath;
    string ModPathKey => "TechTree.ModPath";

    // reference path mirror (lives in the shared Index EditorPrefs key); set in OnEnable
    string _refPath = "";

    // Editing is ON when the window opens (the user, 2026-11-21: moving cards is what this window is for) and the choice
    // is remembered. OFF is for looking only: a click on the canvas then never moves a card.
    bool _editMode = true;
    const string EditModeKey = "TechTree.EditMode";

    // position-drag state (edit mode)
    TechTreeData.Node _dragNode;
    Vector2 _dragStartMouse;
    int _dragStartX, _dragStartY;

    // view transform
    Vector2 _pan;
    float   _zoom = 1f;
    bool    _framed, _panning;
    bool    _fitWholeTree;   // the next framing shows the whole tree (the Frame All button); otherwise its height, as the game does

    // ── GEOMETRY of the game's tech screen ────────────────────────────────────
    // The game puts a card at (TechTreeX * columnsSize, TechTreeY * rowsSize) (TechnologyScreen) and routes the links
    // between the cards' rectangles (TechnologyScreen_Links). The two sizes and the card's own size are serialized in
    // the game's prefabs, not in its code, so they are MEASURED: from a screenshot of the game's tech screen (ENC,
    // 2026-11-21) with six cards whose TechTreeX / Y are known. Six columns were 720 px, 26 rows 927 px, a card
    // 502 x 187 px. Brought to a card width of 251: only the ratios matter here.
    const float COL = 60f, ROW = 17.8f;            // one step of TechTreeX / TechTreeY
    const float NODE_W = 251f, NODE_H = 93.5f;     // a card: 4.18 columns wide, 5.25 rows high
    const float LINK_CURVE = 12f;                  // radius of a link's elbow (the game's value is serialized too: a guess)
    const int   CORNER_STEPS = 6;                  // line pieces an elbow is drawn with
    const float LABEL_MIN_ZOOM = 0.3f;
    const float SIDE_W = 300f;
    const float BAR = 15f;             // thickness of the canvas scroll bars
    const float SCROLL_MARGIN = 60f;   // empty space the bars allow around the tree, in pixels

    static readonly Color EDIT_DOT = new Color(1f, 0.65f, 0.1f);     // amber: unsaved
    static readonly Color MOD_BADGE = new Color(0.4f, 0.7f, 1f);     // blue: modded on disk
    static readonly Color AND_MARK = new Color(1f, 0.85f, 0.45f);    // the "AND" on a card
    // The game's look for a technology that is not researched yet: a dark card with a thin gold edge on dark indigo.
    // The colours are read off the same screenshot; GOLD is the game's own "available" link colour
    // (TechnologyScreen_Links.availableColor).
    static readonly Color CANVAS = new Color(0.075f, 0.065f, 0.19f);
    static readonly Color CARD = new Color(0.25f, 0.24f, 0.38f);
    static readonly Color CARD_SELECTED = new Color(0.36f, 0.35f, 0.52f);
    static readonly Color CARD_EDGE = new Color(0.72f, 0.62f, 0.42f);
    static readonly Color GOLD = new Color(1f, 0.87f, 0.58f);
    static readonly Color LINK = new Color(1f, 0.87f, 0.58f, 0.8f);

    static readonly GUIContent[] RuleChoices =
    {
        new GUIContent("OR — any one", "The game's own rule: the technology unlocks when ANY ONE of its prerequisites is researched."),
        new GUIContent("AND — all", "The technology unlocks only when ALL its prerequisites are researched. HAF's plugin applies this to the technologies in the Tech Requires All list; Save writes the switch to that list. Relaunch the game to apply."),
    };

    [MenuItem("Tools/HAF/Tech Tree/Open Viewer")]
    static void Open()
    {
        var w = GetWindow<TechTreeWindow>("Tech Tree");
        w.minSize = new Vector2(500, 300);
        w.maxSize = new Vector2(4000, 4000);   // large max so it stays freely resizable
        w.Show();
        w.Focus();
    }

    void OnEnable()
    {
        _modPath = EditorPrefs.GetString(ModPathKey, TechTreeData.DefaultModPath);
        _editMode = EditorPrefs.GetBool(EditModeKey, true);
        _refPath = TechTreeData.ReferenceFolder;   // pick up whatever the Index set
        Undo.undoRedoPerformed += OnUndoRedo;
        try { Reload(); }
        catch (Exception e) { Debug.LogError($"[TechTree] Build failed on open: {e}"); _nodes = new(); _byName = new(); }
    }

    void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
    }

    void OnUndoRedo()
    {
        // An undo/redo changed an asset on disk; rebuild from disk but keep the view.
        Reload(keepView: true);
        Repaint();
    }

    // Seam for the drill (tools/tech_requires_all_unity_drill.sh): its temporary project holds no technology assets.
    internal static Func<string, List<TechTreeData.Node>> BuildTreeImpl = modPath => TechTreeData.Build(modPath);

    void Reload(bool keepView = false)
    {
        _nodes = BuildTreeImpl(_modPath);
        _byName = _nodes.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.First());
        // keep pending across reload only for names that still exist
        foreach (var k in _pending.Keys.Where(k => !_byName.ContainsKey(k)).ToList()) _pending.Remove(k);
        _art.Clear();
        // The nodes are new objects: keep the selection by NAME. The old object holds the values from before this
        // reload, so after a Save the side strip showed the previous position and prerequisites, the X / Y fields
        // compared against them, and the node lost its highlight (local change 2026-11-21).
        if (_selected != null && !_byName.TryGetValue(_selected.Name, out _selected)) _selected = null;
        LoadRules();
        if (!keepView) _framed = false;   // re-frame only on first load / explicit Frame All
        Repaint();
    }

    // Read the Tech Requires All list. A staged switch that the list now already holds is no longer a change.
    void LoadRules()
    {
        try
        {
            _requireAll = new HashSet<string>(TechRequireAllRegistry.Load(), StringComparer.Ordinal);
            _rulesUnreadable = TechRequireAllRegistry.Snapshot().Failed;
        }
        catch (Exception e)
        {
            _requireAll = new HashSet<string>(StringComparer.Ordinal); _rulesUnreadable = true;
            Debug.LogError("[TechTree] the Tech Requires All list could not be read: " + e);
        }
        _rulesVersion = TechRequireAllRegistry.LoadedVersion;
        if (_rulesUnreadable) return;   // what the list holds is unknown: every staged switch stays
        foreach (var kv in _pending.ToList())
            if (kv.Value.RequireAll == _requireAll.Contains(kv.Key))
            {
                kv.Value.RequireAll = null;
                if (kv.Value.IsEmpty) _pending.Remove(kv.Key);
            }
    }

    // ── Effective (pending-over-base) accessors ───────────────────────────────
    int EffX(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.X.HasValue ? p.X.Value : n.BaseX;
    int EffY(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.Y.HasValue ? p.Y.Value : n.BaseY;
    IReadOnlyList<string> EffPrereqs(TechTreeData.Node n) =>
        _pending.TryGetValue(n.Name, out var p) && p.Prereqs != null ? p.Prereqs : (IReadOnlyList<string>)n.BasePrereqs;
    // true = the technology waits for ALL its prerequisites (AND); false = the game's rule, any one (OR)
    bool EffRequireAll(TechTreeData.Node n) =>
        _pending.TryGetValue(n.Name, out var p) && p.RequireAll.HasValue ? p.RequireAll.Value : _requireAll.Contains(n.Name);

    bool IsEdited(TechTreeData.Node n) => _pending.ContainsKey(n.Name);
    bool Dirty => _pending.Count > 0;

    Pending Stage(TechTreeData.Node n)
    {
        if (!_pending.TryGetValue(n.Name, out var p)) _pending[n.Name] = p = new Pending();
        return p;
    }
    void DropIfClean(TechTreeData.Node n)
    {
        if (_pending.TryGetValue(n.Name, out var p) && p.IsEmpty) _pending.Remove(n.Name);
    }

    Vector2 W2S(float cx, float cy) => new Vector2(cx * COL * _zoom + _pan.x, cy * ROW * _zoom + _pan.y);
    // a card's rectangle on screen
    Rect NodeRect(TechTreeData.Node n)
    {
        Vector2 p = W2S(EffX(n), EffY(n));
        return new Rect(p.x, p.y, NODE_W * _zoom, NODE_H * _zoom);
    }

    // The tech tree is far wider than high. Like the game, the view opens on the tree's full HEIGHT at its left end;
    // the Frame All button shows the whole tree.
    void FrameAll(Rect canvas)
    {
        if (_nodes == null || _nodes.Count == 0) return;
        const float margin = 30f;
        float w = _nodes.Max(EffX) * COL + NODE_W + 2 * margin;
        float h = _nodes.Max(EffY) * ROW + NODE_H + 2 * margin;
        float zx = canvas.width / w, zy = canvas.height / h;
        if (_fitWholeTree)
        {
            _zoom = Mathf.Clamp(Mathf.Min(zx, zy), 0.05f, 2f);
            _pan = new Vector2(canvas.x + (canvas.width - w * _zoom) * 0.5f + margin * _zoom,
                               canvas.y + (canvas.height - h * _zoom) * 0.5f + margin * _zoom);
        }
        else
        {
            _zoom = Mathf.Clamp(zy, 0.05f, 1f);
            _pan = new Vector2(canvas.x + margin * _zoom, canvas.y + (canvas.height - h * _zoom) * 0.5f + margin * _zoom);
        }
        _fitWholeTree = false;
        _framed = true;
    }

    void OnGUI()
    {
        if (Event.current.type == EventType.MouseMove) Repaint();
        // The Tech Requires All window (or this one) saved the list since this window read it: show the list as it is now.
        if (Event.current.type == EventType.Layout && TechRequireAllRegistry.LoadedVersion != _rulesVersion) LoadRules();

        // ── Toolbar ──
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUI.backgroundColor = _editMode ? EDIT_DOT : Color.white;
        bool edit = GUILayout.Toggle(_editMode, new GUIContent(_editMode ? "● Edit: ON" : "Edit: OFF",
            "ON: drag a card or type its X / Y, switch OR / AND, add or remove prerequisites. Nothing is written until Save. OFF: look only."),
            EditorStyles.toolbarButton, GUILayout.Width(80));
        GUI.backgroundColor = Color.white;
        if (edit != _editMode) { _editMode = edit; EditorPrefs.SetBool(EditModeKey, edit); }
        if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(60))) Reload(keepView: true);
        if (GUILayout.Button("Frame All", EditorStyles.toolbarButton, GUILayout.Width(70))) { _framed = false; _fitWholeTree = true; }

        using (new EditorGUI.DisabledScope(!Dirty))
        {
            GUI.backgroundColor = Dirty ? EDIT_DOT : Color.white;
            if (GUILayout.Button($"Save{(Dirty ? $" ({_pending.Count})" : "")}", EditorStyles.toolbarButton, GUILayout.Width(80)))
                CommitChanges();
            GUI.backgroundColor = Color.white;
            if (GUILayout.Button("Revert", EditorStyles.toolbarButton, GUILayout.Width(60))) RevertChanges();
        }

        GUILayout.FlexibleSpace();

        // reference (read-only archive) path — shared with the Index tool
        bool refMissing = !AssetDatabase.IsValidFolder(_refPath);
        EditorGUILayout.LabelField("Ref", GUILayout.Width(26));
        GUI.color = refMissing ? new Color(1f, 0.6f, 0.6f) : Color.white;
        string newRef = EditorGUILayout.TextField(_refPath, GUILayout.Width(170));
        GUI.color = Color.white;
        if (newRef != _refPath) { _refPath = newRef; TechTreeData.ReferenceFolder = _refPath; }
        if (GUILayout.Button("…", EditorStyles.toolbarButton, GUILayout.Width(24)))
        {
            string picked = EditorUtility.OpenFolderPanel("Reference (vanilla) folder", _refPath, "");
            if (!string.IsNullOrEmpty(picked))
            {
                // store as a project-relative path if inside the project
                string dataDir = Application.dataPath;
                if (picked.StartsWith(dataDir)) picked = "Assets" + picked.Substring(dataDir.Length);
                _refPath = picked; TechTreeData.ReferenceFolder = _refPath; Reload(keepView: true);
            }
        }

        EditorGUILayout.LabelField("Mod path", GUILayout.Width(55));
        string newPath = EditorGUILayout.TextField(_modPath, GUILayout.Width(200));
        if (newPath != _modPath) { _modPath = newPath; EditorPrefs.SetString(ModPathKey, _modPath); }
        if (GUILayout.Button("Apply", EditorStyles.toolbarButton, GUILayout.Width(50))) Reload(keepView: true);
        EditorGUILayout.EndHorizontal();

        if (refMissing)
            EditorGUILayout.HelpBox("Reference folder not found — showing Databases content only (no vanilla fallback).", MessageType.Info);

        float top = EditorStyles.toolbar.fixedHeight > 0 ? EditorStyles.toolbar.fixedHeight : 21f;
        Rect canvas = new Rect(0, top, position.width - SIDE_W - BAR, position.height - top - BAR);
        Rect side   = new Rect(position.width - SIDE_W, top, SIDE_W, position.height - top);

        if (!_framed) FrameAll(canvas);
        DrawScrollBars(canvas);
        HandleInput(canvas);
        DrawCanvas(canvas);
        DrawSide(side);
    }

    // ── Scroll bars ───────────────────────────────────────────────────────────
    /// <summary>
    /// The range one scroll bar covers: the tree's extent, widened to wherever the view is now. Panning past the tree
    /// (middle-mouse drag, zooming at the cursor) therefore never makes the bar pull the view back.
    /// </summary>
    internal static void ScrollRange(float contentMin, float contentMax, float view, float viewSize, out float lo, out float hi)
    {
        lo = Mathf.Min(contentMin, view);
        hi = Mathf.Max(contentMax, view + viewSize);
    }

    void DrawScrollBars(Rect canvas)
    {
        if (_nodes == null || _nodes.Count == 0 || canvas.width <= 0 || canvas.height <= 0) return;
        // The view's top-left corner and the tree's extent, both in the tree's own pixels at this zoom.
        float viewX = canvas.x - _pan.x, viewY = canvas.y - _pan.y;
        ScrollRange(Mathf.Min(0, _nodes.Min(EffX)) * COL * _zoom - SCROLL_MARGIN, (_nodes.Max(EffX) * COL + NODE_W) * _zoom + SCROLL_MARGIN,
                    viewX, canvas.width, out float loX, out float hiX);
        ScrollRange(Mathf.Min(0, _nodes.Min(EffY)) * ROW * _zoom - SCROLL_MARGIN, (_nodes.Max(EffY) * ROW + NODE_H) * _zoom + SCROLL_MARGIN,
                    viewY, canvas.height, out float loY, out float hiY);
        float newX = GUI.HorizontalScrollbar(new Rect(canvas.x, canvas.yMax, canvas.width, BAR), viewX, canvas.width, loX, hiX);
        float newY = GUI.VerticalScrollbar(new Rect(canvas.xMax, canvas.y, BAR, canvas.height), viewY, canvas.height, loY, hiY);
        // only a real move: the round trip through the bar may differ in the last bit, and that must not drift the view
        if (Mathf.Abs(newX - viewX) > 0.01f || Mathf.Abs(newY - viewY) > 0.01f)
        {
            _pan = new Vector2(canvas.x - newX, canvas.y - newY);
            Repaint();
        }
    }

    // ── Input ─────────────────────────────────────────────────────────────────
    void HandleInput(Rect canvas)
    {
        var e = Event.current;
        // End an active drag on ANY MouseUp, even outside the canvas — the early-return below used to skip the
        // MouseUp when the button was released over the side panel / outside the window, leaving a stale _dragNode:
        // the next drag then teleported that node by the old delta and silently staged a position edit (review
        // 2026-07-19). Releasing a drag out-of-bounds keeps the position it had at the last in-canvas drag event.
        if (e.rawType == EventType.MouseUp && _dragNode != null && !canvas.Contains(e.mousePosition))
        { _dragNode = null; Repaint(); return; }
        if (!canvas.Contains(e.mousePosition) && !_panning) return;

        if (e.type == EventType.ScrollWheel)
        {
            float old = _zoom;
            _zoom = Mathf.Clamp(_zoom * (1f - e.delta.y * 0.05f), 0.05f, 3f);
            Vector2 m = e.mousePosition;
            _pan = m - (m - _pan) * (_zoom / old);
            e.Use(); Repaint();
        }
        else if (e.type == EventType.MouseDown && (e.button == 2 || (e.button == 0 && e.alt)))
        { _panning = true; e.Use(); }
        else if (e.type == EventType.MouseDrag && _panning)
        { _pan += e.delta; e.Use(); Repaint(); }
        else if (e.type == EventType.MouseUp && _panning)
        { _panning = false; e.Use(); }
        else if (e.type == EventType.MouseDown && e.button == 0)
        {
            var hit = NodeAt(e.mousePosition);
            if (hit != null)
            {
                _selected = hit;
                GUIUtility.keyboardControl = 0;   // a focused X / Y field would keep showing the previous node's number
                EditorGUIUtility.PingObject(hit.Asset);
                Selection.activeObject = hit.Asset;
                if (_editMode)   // begin a potential position drag
                {
                    _dragNode = hit;
                    _dragStartMouse = e.mousePosition;
                    _dragStartX = EffX(hit);
                    _dragStartY = EffY(hit);
                }
                e.Use(); Repaint();
            }
        }
        else if (e.type == EventType.MouseDrag && _dragNode != null)
        {
            int dx = Mathf.RoundToInt((e.mousePosition.x - _dragStartMouse.x) / (COL * _zoom));
            int dy = Mathf.RoundToInt((e.mousePosition.y - _dragStartMouse.y) / (ROW * _zoom));
            int nx = Mathf.Max(0, _dragStartX + dx);
            int ny = Mathf.Max(0, _dragStartY + dy);
            var p = Stage(_dragNode);
            p.X = nx != _dragNode.BaseX ? (int?)nx : null;   // snap; equal to base = no change
            p.Y = ny != _dragNode.BaseY ? (int?)ny : null;
            DropIfClean(_dragNode);
            e.Use(); Repaint();
        }
        else if (e.type == EventType.MouseUp && _dragNode != null)
        {
            _dragNode = null;
            e.Use(); Repaint();
        }
    }

    TechTreeData.Node NodeAt(Vector2 screen)
    {
        // cards can overlap (two rows apart is closer than a card is high): the one drawn last is on top
        for (int i = _nodes.Count - 1; i >= 0; i--)
            if (NodeRect(_nodes[i]).Contains(screen)) return _nodes[i];
        return null;
    }

    // ── Canvas ────────────────────────────────────────────────────────────────
    void DrawCanvas(Rect canvas)
    {
        EditorGUI.DrawRect(canvas, CANVAS);
        GUI.BeginClip(canvas);
        Rect local = new Rect(0, 0, canvas.width, canvas.height);
        Vector2 panSave = _pan; _pan -= new Vector2(canvas.x, canvas.y);

        bool showLabels = _zoom >= LABEL_MIN_ZOOM;

        // links, routed as the game routes them (from the effective prerequisites)
        if (Event.current.type == EventType.Repaint)
        {
            Handles.color = LINK;
            float width = Mathf.Clamp(1.5f * _zoom, 1f, 2.5f);
            foreach (var n in _nodes)
            {
                Rect to = NodeRect(n);
                foreach (var pr in EffPrereqs(n))
                {
                    if (!_byName.TryGetValue(pr, out var src)) continue;
                    Rect from = NodeRect(src);
                    // a link stays inside the box around its two cards
                    if (!Rect.MinMaxRect(Mathf.Min(from.xMin, to.xMin), Mathf.Min(from.yMin, to.yMin), Mathf.Max(from.xMax, to.xMax), Mathf.Max(from.yMax, to.yMax)).Overlaps(local)) continue;
                    LinkPath(from, to, LINK_CURVE * _zoom, _linkPath);
                    if (_linkPoints.Length < _linkPath.Count) _linkPoints = new Vector3[_linkPath.Count];
                    for (int i = 0; i < _linkPath.Count; i++) _linkPoints[i] = _linkPath[i];
                    Handles.DrawAAPolyLine(width, _linkPath.Count, _linkPoints);
                }
            }
            Handles.color = Color.white;
        }

        // cards
        float z = Mathf.Min(_zoom, 1.6f);
        var titleStyle = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft, fontSize = Mathf.Clamp(Mathf.RoundToInt(12f * z), 6, 19),
            wordWrap = false, clipping = TextClipping.Clip, normal = { textColor = GOLD }
        };
        var infoStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleLeft, fontSize = Mathf.Clamp(Mathf.RoundToInt(9f * z), 6, 14),
            wordWrap = false, clipping = TextClipping.Clip, normal = { textColor = new Color(1, 1, 1, 0.7f) }
        };
        var nameStyle = new GUIStyle(infoStyle) { normal = { textColor = new Color(1, 1, 1, 0.38f) } };
        var andStyle = new GUIStyle(infoStyle) { fontStyle = FontStyle.Bold, normal = { textColor = AND_MARK } };

        foreach (var n in _nodes)
        {
            Rect r = NodeRect(n);
            Rect artRect = ArtRect(r);
            if (!r.Overlaps(local) && !(n.ArtPath != null && artRect.Overlaps(local))) continue;

            bool selected = n == _selected;
            float radius = 4f * _zoom;
            // the card: fill, then the thin gold edge (brighter and thicker on the selected one)
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, selected ? CARD_SELECTED : CARD, 0, radius);
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, selected ? GOLD : CARD_EDGE, selected ? 2f : 1f, radius);

            // the artwork, over the card's right end and standing out of it
            var art = ArtOf(n);
            if (art != null) GUI.DrawTexture(artRect, art, ScaleMode.ScaleToFit, true);

            float pad = 8f * _zoom;
            float titleH = 0.29f * r.height;   // the game's title band: the divider sits at 29% of the card's height

            // markers: modded on disk (blue bar inside the left edge) + unsaved edit (amber dot, top right)
            if (n.AnyModded)
                EditorGUI.DrawRect(new Rect(r.x + 1, r.y + radius, Mathf.Max(2f, 3f * _zoom), r.height - 2 * radius), MOD_BADGE);
            if (IsEdited(n))
            {
                float d = Mathf.Max(5f, 8f * _zoom);
                GUI.DrawTexture(new Rect(r.xMax - d - pad * 0.5f, r.y + (titleH - d) * 0.5f, d, d), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, EDIT_DOT, 0, d * 0.5f);
            }

            if (!showLabels) continue;

            // title, upper case as in the game, over the divider
            GUI.Label(new Rect(r.x + pad, r.y, r.width - 2 * pad - (IsEdited(n) ? 10f * _zoom : 0), titleH), (n.Label ?? n.Name ?? "").ToUpperInvariant(), titleStyle);
            EditorGUI.DrawRect(new Rect(r.x + pad, r.y + titleH, r.width - 2 * pad, 1f), new Color(1f, 1f, 1f, 0.22f));

            // under it, where the game shows the turns: era and tier, and the AND rule where it has an effect
            float rowH = 0.24f * r.height;
            var info = new Rect(r.x + pad, r.y + titleH + 2f * _zoom, r.width - 2 * pad, rowH);
            string eraTier = ShortEra(n.Era) + (string.IsNullOrEmpty(n.Era) || string.IsNullOrEmpty(n.Tier) ? "" : " · ") + ShortTier(n.Tier);
            GUI.Label(info, eraTier, infoStyle);
            if (!_rulesUnreadable && EffRequireAll(n) && EffPrereqs(n).Count >= 2)
            {
                // right behind era and tier: the card's right end belongs to the artwork
                float used = infoStyle.CalcSize(new GUIContent(eraTier)).x + 8f * _zoom;
                GUI.Label(new Rect(info.x + used, info.y, info.width - used, info.height), "AND", andStyle);
            }
            // at the bottom, where the game shows what the technology unlocks: its internal name
            GUI.Label(new Rect(r.x + pad, r.yMax - rowH - 3f * _zoom, r.width - 2 * pad, rowH), n.Name, nameStyle);
        }

        _pan = panSave;
        GUI.EndClip();
    }

    // ── Artwork ───────────────────────────────────────────────────────────────
    readonly Dictionary<string, Texture> _art = new Dictionary<string, Texture>();   // asset path -> texture, loaded when first drawn

    Texture ArtOf(TechTreeData.Node n)
    {
        if (string.IsNullOrEmpty(n.ArtPath)) return null;
        if (!_art.TryGetValue(n.ArtPath, out var tex)) _art[n.ArtPath] = tex = AssetDatabase.LoadAssetAtPath<Texture>(n.ArtPath);
        return tex;
    }

    /// <summary>
    /// Where a card's artwork goes: a square one and a half card heights high, centred on the card's height and just
    /// inside its right edge, so it stands out above, below and to the right of the card as it does in the game.
    /// Measured from a screenshot of the game (2026-11-21, three researched cards); the game's own values are in a prefab.
    /// </summary>
    internal static Rect ArtRect(Rect card)
    {
        float side = 1.5f * card.height;
        return new Rect(card.xMax - 0.06f * card.width - side * 0.5f, card.center.y - side * 0.5f, side, side);
    }

    readonly List<Vector2> _linkPath = new List<Vector2>();
    Vector3[] _linkPoints = new Vector3[16];

    /// <summary>
    /// The path of one link from the prerequisite's card to the technology's card: the game's own routing
    /// (TechnologyScreen_Links.Link.RebuildCurve), with its Bezier elbow cut into line pieces.
    ///  - The two cards share some height: one straight horizontal line, at the middle of the shared part, from the
    ///    first card's right edge to the second card's left edge.
    ///  - They share none: out of the first card's top or bottom edge at its middle (the edge that faces the second
    ///    card), straight up or down to the height of the second card's middle, round the elbow, then horizontal to the
    ///    second card's left edge. The elbow's radius is cut down to the room there is.
    ///  - The second card does not start right of the first one's end: the game logs "right-to-left links are not
    ///    handled" and draws centre to centre.
    /// </summary>
    internal static void LinkPath(Rect start, Rect end, float curve, List<Vector2> path)
    {
        path.Clear();
        if (start.xMax > end.xMin)
        {
            path.Add(start.center); path.Add(end.center);
            return;
        }
        if (end.yMax < start.yMin || start.yMax < end.yMin)
        {
            bool endAbove = end.yMax < start.yMin;
            var from = new Vector2((start.xMin + start.xMax) * 0.5f, endAbove ? start.yMin : start.yMax);
            var to = new Vector2(end.xMin, (end.yMin + end.yMax) * 0.5f);
            var elbow = new Vector2(from.x, to.y);
            float c = Mathf.Min(Mathf.Max(0f, curve), Mathf.Abs(from.y - elbow.y), Mathf.Abs(elbow.x - to.x));
            var before = new Vector2(elbow.x, elbow.y + (endAbove ? c : -c));
            var after = new Vector2(elbow.x + c, elbow.y);
            path.Add(from); path.Add(before);
            // the game's corner: a cubic Bezier from `before` to `after` with both control points on the elbow
            for (int i = 1; i < CORNER_STEPS; i++)
            {
                float t = i / (float)CORNER_STEPS, u = 1f - t;
                path.Add(u * u * u * before + 3f * u * t * elbow + t * t * t * after);
            }
            path.Add(after); path.Add(to);
            return;
        }
        float y = (Mathf.Max(start.yMin, end.yMin) + Mathf.Min(start.yMax, end.yMax)) * 0.5f;
        path.Add(new Vector2(start.xMax, y)); path.Add(new Vector2(end.xMin, y));
    }

    // ── Side strip ────────────────────────────────────────────────────────────
    Vector2 _sideScroll;
    void DrawSide(Rect side)
    {
        GUILayout.BeginArea(side, EditorStyles.helpBox);
        _sideScroll = EditorGUILayout.BeginScrollView(_sideScroll);

        if (_selected == null)
            EditorGUILayout.LabelField("Click a node to inspect.", EditorStyles.wordWrappedMiniLabel);
        else
        {
            var n = _selected;
            EditorGUILayout.LabelField(n.Label, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(n.Name, EditorStyles.miniLabel);

            // state line
            string state = n.AnyModded ? "Modded" : "Vanilla";
            if (IsEdited(n)) state += " · edited *";
            string detail = $"def:{(n.DefModded ? "mod" : "vanilla")}  map:{(n.MapperModded ? "mod" : "vanilla")}";
            EditorGUILayout.LabelField($"{state}   ({detail})", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"{n.Era} · {n.Tier} · ({EffX(n)},{EffY(n)})", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(ArtLine(n), EditorStyles.miniLabel);

            if (!_editMode)
                EditorGUILayout.HelpBox("Edit is OFF, so the position, the rule and the prerequisites are greyed out. Switch on \"Edit\" at the top left to change them.", MessageType.Info);

            // the position as numbers (grid cells), staged exactly like a drag on the canvas
            using (new EditorGUI.DisabledScope(!_editMode))
            {
                float labelWidth = EditorGUIUtility.labelWidth;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("Position", "The node's place on the tech screen, in grid cells. Type a number or drag the X / Y label; dragging the node on the canvas changes the same values."), GUILayout.Width(54));
                EditorGUIUtility.labelWidth = 14;
                int x = EditorGUILayout.IntField("X", EffX(n));
                int y = EditorGUILayout.IntField("Y", EffY(n));
                EditorGUIUtility.labelWidth = labelWidth;
                EditorGUILayout.EndHorizontal();
                if (x != EffX(n) || y != EffY(n)) SetPosition(n, x, y);
            }

            EditorGUILayout.Space(6);
            bool all = EffRequireAll(n);
            var prereqs = EffPrereqs(n);
            EditorGUILayout.LabelField(RuleHeader(_rulesUnreadable, all), EditorStyles.boldLabel);
            if (_rulesUnreadable)
                EditorGUILayout.HelpBox("The Tech Requires All list could not be read, so the rule (OR / AND) is not shown and cannot be switched here. " +
                                        "Tools ▸ HAF ▸ Tech Tree ▸ Requires All Prerequisites says why.", MessageType.Warning);
            else
            {
                using (new EditorGUI.DisabledScope(!_editMode))
                {
                    int now = all ? 1 : 0;
                    int picked = GUILayout.Toolbar(now, RuleChoices, EditorStyles.miniButton);
                    if (picked != now) SetRule(n, picked == 1);
                }
                if (all && prereqs.Count < 2)
                    EditorGUILayout.LabelField("AND changes nothing while there are fewer than two prerequisites.", EditorStyles.wordWrappedMiniLabel);
            }
            if (prereqs.Count == 0)
                EditorGUILayout.LabelField("  (none — root)", EditorStyles.miniLabel);
            else
                foreach (var pr in prereqs.ToList())
                {
                    EditorGUILayout.BeginHorizontal();
                    string label = _byName.TryGetValue(pr, out var s) ? $"{s.Label}" : pr;
                    if (GUILayout.Button(label, EditorStyles.miniButton) && _byName.TryGetValue(pr, out var sn))
                    { _selected = sn; GUIUtility.keyboardControl = 0; EditorGUIUtility.PingObject(sn.Asset); Selection.activeObject = sn.Asset; }
                    using (new EditorGUI.DisabledScope(!_editMode))
                        if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(22)))
                            RemovePrereq(n, pr);   // step 3 fills the body; staging works now
                    EditorGUILayout.EndHorizontal();
                }

            using (new EditorGUI.DisabledScope(!_editMode))
                if (GUILayout.Button("+ add prerequisite"))
                    AddPrereqPrompt(n, GUILayoutUtility.GetLastRect());

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Select asset"))
            { Selection.activeObject = n.Asset; EditorGUIUtility.PingObject(n.Asset); }
        }

        EditorGUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    // what the side strip says about the card's image, so a card without artwork is explained
    internal static string ArtLine(TechTreeData.Node n) =>
        !string.IsNullOrEmpty(n.ArtPath) ? "Image: " + System.IO.Path.GetFileName(n.ArtPath)
        : n.ArtNamed ? "Image: not in this project (one of the game's own)"
        : "Image: none set";

    internal static string RuleHeader(bool unreadable, bool requireAll) =>
        unreadable ? "Prerequisites:" : requireAll ? "Prerequisites (AND):" : "Prerequisites (OR):";

    // ── Edit operations (staging into the overlay; disk write is at Save) ──────
    void SetRule(TechTreeData.Node n, bool requireAll)
    {
        var p = Stage(n);
        p.RequireAll = requireAll != _requireAll.Contains(n.Name) ? (bool?)requireAll : null;   // back to the list's own = no change
        DropIfClean(n);
        Repaint();
    }

    void SetPosition(TechTreeData.Node n, int x, int y)
    {
        x = Mathf.Max(0, x); y = Mathf.Max(0, y);   // the same floor a drag has
        var p = Stage(n);
        p.X = x != n.BaseX ? (int?)x : null;   // equal to base = no change
        p.Y = y != n.BaseY ? (int?)y : null;
        DropIfClean(n);
        Repaint();
    }

    void RemovePrereq(TechTreeData.Node n, string prereq)
    {
        var p = Stage(n);
        p.Prereqs ??= new List<string>(EffPrereqs(n));
        p.Prereqs.Remove(prereq);
        if (p.Prereqs.SequenceEqual(n.BasePrereqs)) p.Prereqs = null;  // back to base = no change
        DropIfClean(n);
        Repaint();
    }

    void AddPrereqPrompt(TechTreeData.Node n, Rect anchor)
    {
        var current = new HashSet<string>(EffPrereqs(n));
        var candidates = _nodes
            .Where(c => c.Name != n.Name)           // not self
            .Where(c => !current.Contains(c.Name))  // not already a prereq
            .Where(c => !WouldCycle(n, c.Name))     // no cycle (uses effective graph)
            .ToList();

        if (candidates.Count == 0)
        { Debug.Log($"[TechTree] No valid prerequisites to add to {n.Name} (all would self/dup/cycle)."); return; }

        var dd = new TechPickerDropdown(new AdvancedDropdownState(), candidates, picked => AddPrereq(n, picked));
        dd.Show(anchor);
    }

    void AddPrereq(TechTreeData.Node n, string prereq)
    {
        var p = Stage(n);
        p.Prereqs ??= new List<string>(EffPrereqs(n));
        if (!p.Prereqs.Contains(prereq)) p.Prereqs.Add(prereq);
        if (p.Prereqs.SequenceEqual(n.BasePrereqs)) p.Prereqs = null;
        DropIfClean(n);
        Repaint();
    }

    // Adding "target requires candidate" cycles iff candidate already (transitively)
    // depends on target. Walk candidate's prerequisite ancestors via the EFFECTIVE
    // graph (staged edits included); if we reach target, it would cycle.
    bool WouldCycle(TechTreeData.Node target, string candidateName)
    {
        var visited = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(candidateName);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == target.Name) return true;
            if (!visited.Add(cur)) continue;
            if (_byName.TryGetValue(cur, out var node))
                foreach (var pr in EffPrereqs(node)) stack.Push(pr);
        }
        return false;
    }

    // ── Save / Revert ─────────────────────────────────────────────────────────
    void CommitChanges()
    {
        if (_pending.Count == 0) return;
        int pos = 0, pre = 0, skipped = 0;
        var committed = new List<string>();   // only fully-written entries leave the overlay — a SKIPPED edit must
                                              // survive the save (the old unconditional Clear discarded them, so
                                              // after creating the missing collection there was nothing left to save)
        foreach (var kv in _pending)
        {
            if (!_byName.TryGetValue(kv.Key, out var node)) { committed.Add(kv.Key); continue; }   // node gone — nothing to keep
            var p = kv.Value;
            bool anySkipped = false;

            if (p.X.HasValue || p.Y.HasValue)
            {
                var mapper = TechTreeData.EnsureWritable(node, true, _modPath);
                if (mapper != null)
                {
                    Undo.RecordObject(mapper, "Tech position");
                    TechTreeData.WritePosition(mapper, p.X ?? node.BaseX, p.Y ?? node.BaseY);
                    EditorUtility.SetDirty(mapper);
                    pos++;
                }
                else { skipped++; anySkipped = true; }
            }

            if (p.Prereqs != null)
            {
                var def = TechTreeData.EnsureWritable(node, false, _modPath);
                if (def != null)
                {
                    Undo.RecordObject(def, "Tech prerequisites");
                    if (TechTreeData.WritePrereqs(def, p.Prereqs.ToArray())) pre++;
                    EditorUtility.SetDirty(def);
                }
                else { skipped++; anySkipped = true; }
            }

            if (!anySkipped) committed.Add(kv.Key);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // The rule switches go to the Tech Requires All list in one write: each as an operation on the list as it is
        // now (add or remove that one name), so entries made in that list's own window meanwhile are kept.
        var rules = _pending.Where(kv => kv.Value.RequireAll.HasValue && _byName.ContainsKey(kv.Key))
                            .ToDictionary(kv => kv.Key, kv => kv.Value.RequireAll.Value);
        string ruleNote = "";
        if (rules.Count > 0)
        {
            var outcome = TechRequireAllRegistry.SetRules(rules, out _);
            if (outcome == RegistryRules.SaveOutcome.Saved)
            {
                foreach (var k in rules.Keys) _pending[k].RequireAll = null;
                ruleNote = TechRequireAllRegistry.DeployPending
                    ? $", {rules.Count} rule switch(es) (OR / AND) — written to the project, but the game's {TechRequireAllRegistry.ArtifactName} is not refreshed yet (see the Console)"
                    : $", {rules.Count} rule switch(es) (OR / AND) -> {TechRequireAllRegistry.ArtifactName}; relaunch the game to apply";
                TechRequireAllWindow.RefreshOpenWindows();
            }
            else
                ruleNote = outcome == RegistryRules.SaveOutcome.Refused
                    ? $" · {rules.Count} rule switch(es) (OR / AND) REFUSED — nothing of them was written (the Console says why); they are KEPT staged"
                    : $" · {rules.Count} rule switch(es) (OR / AND) could NOT be confirmed — the list may or may not hold them (see the Console); they are KEPT staged";
        }

        Debug.Log($"[TechTree] Saved: {pos} position, {pre} prerequisite change(s)" + ruleNote
                + (skipped > 0 ? $" · {skipped} skipped (no writable target — see step 5; those edits are KEPT staged)" : "") + ".");
        foreach (var k in committed)
        {
            // everything else of this entry is written; a rule switch that was not stays staged on its own
            if (_pending.TryGetValue(k, out var left) && left.RequireAll.HasValue && _byName.ContainsKey(k)) { left.X = left.Y = null; left.Prereqs = null; }
            else _pending.Remove(k);
        }
        Reload(keepView: true);
    }

    void RevertChanges()
    {
        if (_pending.Count == 0) return;
        if (EditorUtility.DisplayDialog("Revert", $"Discard {_pending.Count} unsaved change(s)?", "Discard", "Cancel"))
        { _pending.Clear(); Reload(keepView: true); }
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    static string ShortEra(string era)  => string.IsNullOrEmpty(era) ? "" : era.Replace("Era", "E");
    static string ShortTier(string tier) => string.IsNullOrEmpty(tier) ? "" : tier.Replace("Tier", "T");
}

/// <summary>
/// Searchable tech picker for adding a prerequisite. Groups candidates by era for
/// navigability; AdvancedDropdown's built-in search filters across all of them.
/// Maps the picked item back to a tech name by item reference (no id juggling).
/// </summary>
class TechPickerDropdown : AdvancedDropdown
{
    readonly List<TechTreeData.Node> _candidates;
    readonly Action<string> _onPick;
    readonly Dictionary<AdvancedDropdownItem, string> _map = new();
    // The list's heading. LOCAL CHANGE to this file (2026-11-20): the heading was the fixed text "Add prerequisite";
    // TechRequireAllWindow.cs reuses this picker and passes "Add technology" as the fourth constructor argument.
    // This viewer came from another modder's tool (the user, 2026-11-20; the file carries no credit line and the
    // history starts at its move into this repo). If the file is ever replaced by a newer copy of that tool, keep
    // the `title` parameter or TechRequireAllWindow stops compiling. Recorded in docs/Review-Backlog.md.
    readonly string _title;

    public TechPickerDropdown(AdvancedDropdownState state, List<TechTreeData.Node> candidates, Action<string> onPick,
                              string title = "Add prerequisite")
        : base(state)
    {
        _candidates = candidates;
        _onPick = onPick;
        _title = title;
        minimumSize = new Vector2(320, 420);
    }

    protected override AdvancedDropdownItem BuildRoot()
    {
        var root = new AdvancedDropdownItem(_title);
        foreach (var era in _candidates.Select(c => c.Era).Distinct()
                                       .OrderBy(e => string.IsNullOrEmpty(e) ? "zzz" : e))
        {
            var eraItem = new AdvancedDropdownItem(string.IsNullOrEmpty(era) ? "(no era)" : era);
            foreach (var c in _candidates.Where(c => c.Era == era).OrderBy(c => c.Label))
            {
                var it = new AdvancedDropdownItem($"{c.Label}   ({c.Name})");
                _map[it] = c.Name;
                eraItem.AddChild(it);
            }
            root.AddChild(eraItem);
        }
        return root;
    }

    protected override void ItemSelected(AdvancedDropdownItem item)
    {
        if (_map.TryGetValue(item, out var name)) _onPick(name);
    }
}