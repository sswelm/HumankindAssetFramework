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
/// </summary>
public class TechTreeWindow : EditorWindow
{
    // ── Pending overlay: staged, not-yet-saved edits per tech name ────────────
    class Pending
    {
        public int? X, Y;                // staged position (null = unchanged)
        public List<string> Prereqs;     // staged prereq list (null = unchanged)
        public bool IsEmpty => X == null && Y == null && Prereqs == null;
    }
    readonly Dictionary<string, Pending> _pending = new();

    List<TechTreeData.Node> _nodes;
    Dictionary<string, TechTreeData.Node> _byName;
    TechTreeData.Node _selected;

    // mod path (editable, persisted)
    string _modPath = TechTreeData.DefaultModPath;
    string ModPathKey => "TechTree.ModPath";

    // reference path mirror (lives in the shared Index EditorPrefs key); set in OnEnable
    string _refPath = "";

    bool _editMode;

    // position-drag state (edit mode)
    TechTreeData.Node _dragNode;
    Vector2 _dragStartMouse;
    int _dragStartX, _dragStartY;

    // view transform
    Vector2 _pan;
    float   _zoom = 1f;
    bool    _framed, _panning;

    const float CELL = 26f;
    const float NODE_W = 150f, NODE_H = 34f;
    const float LABEL_MIN_ZOOM = 0.45f;
    const float SIDE_W = 300f;

    static readonly Color EDIT_DOT = new Color(1f, 0.65f, 0.1f);     // amber: unsaved
    static readonly Color MOD_BADGE = new Color(0.4f, 0.7f, 1f);     // blue: modded on disk

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

    void Reload(bool keepView = false)
    {
        _nodes = TechTreeData.Build(_modPath);
        _byName = _nodes.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.First());
        // keep pending across reload only for names that still exist
        foreach (var k in _pending.Keys.Where(k => !_byName.ContainsKey(k)).ToList()) _pending.Remove(k);
        if (!keepView) _framed = false;   // re-frame only on first load / explicit Frame All
        Repaint();
    }

    // ── Effective (pending-over-base) accessors ───────────────────────────────
    int EffX(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.X.HasValue ? p.X.Value : n.BaseX;
    int EffY(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.Y.HasValue ? p.Y.Value : n.BaseY;
    IReadOnlyList<string> EffPrereqs(TechTreeData.Node n) =>
        _pending.TryGetValue(n.Name, out var p) && p.Prereqs != null ? p.Prereqs : (IReadOnlyList<string>)n.BasePrereqs;

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

    Vector2 W2S(float cx, float cy) => new Vector2(cx * CELL * _zoom + _pan.x, cy * CELL * _zoom + _pan.y);

    void FrameAll(Rect canvas)
    {
        if (_nodes == null || _nodes.Count == 0) return;
        float maxX = _nodes.Max(EffX) + 2;
        float maxY = _nodes.Max(EffY) + 2;
        float zx = canvas.width / (maxX * CELL), zy = canvas.height / (maxY * CELL);
        _zoom = Mathf.Clamp(Mathf.Min(zx, zy), 0.05f, 2f);
        _pan = new Vector2(canvas.x + (canvas.width - maxX * CELL * _zoom) * 0.5f,
                           canvas.y + (canvas.height - maxY * CELL * _zoom) * 0.5f);
        _framed = true;
    }

    void OnGUI()
    {
        if (Event.current.type == EventType.MouseMove) Repaint();

        // ── Toolbar ──
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        _editMode = GUILayout.Toggle(_editMode, _editMode ? "● Edit" : "View", EditorStyles.toolbarButton, GUILayout.Width(70));
        if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(60))) Reload(keepView: true);
        if (GUILayout.Button("Frame All", EditorStyles.toolbarButton, GUILayout.Width(70))) _framed = false;

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
        Rect canvas = new Rect(0, top, position.width - SIDE_W, position.height - top);
        Rect side   = new Rect(canvas.xMax, top, SIDE_W, position.height - top);

        if (!_framed) FrameAll(canvas);
        HandleInput(canvas);
        DrawCanvas(canvas);
        DrawSide(side);
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
            float inv = 1f / (CELL * _zoom);
            int dx = Mathf.RoundToInt((e.mousePosition.x - _dragStartMouse.x) * inv);
            int dy = Mathf.RoundToInt((e.mousePosition.y - _dragStartMouse.y) * inv);
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
        foreach (var n in _nodes)
        {
            Vector2 p = W2S(EffX(n), EffY(n));
            if (new Rect(p.x, p.y, NODE_W * _zoom, NODE_H * _zoom).Contains(screen)) return n;
        }
        return null;
    }

    // ── Canvas ────────────────────────────────────────────────────────────────
    void DrawCanvas(Rect canvas)
    {
        EditorGUI.DrawRect(canvas, new Color(0.16f, 0.16f, 0.17f));
        GUI.BeginClip(canvas);
        Rect local = new Rect(0, 0, canvas.width, canvas.height);
        Vector2 panSave = _pan; _pan -= new Vector2(canvas.x, canvas.y);

        bool showLabels = _zoom >= LABEL_MIN_ZOOM;

        // edges (from effective prereqs)
        foreach (var n in _nodes)
        {
            Vector2 to = W2S(EffX(n), EffY(n)) + new Vector2(0, NODE_H * _zoom * 0.5f);
            foreach (var pr in EffPrereqs(n))
            {
                if (!_byName.TryGetValue(pr, out var src)) continue;
                Vector2 from = W2S(EffX(src), EffY(src)) + new Vector2(NODE_W * _zoom, NODE_H * _zoom * 0.5f);
                if (!local.Contains(from) && !local.Contains(to) && !LineCrossesRect(from, to, local)) continue;
                Handles.DrawBezier(from, to, from + Vector2.right * 40f * _zoom, to - Vector2.right * 40f * _zoom,
                    new Color(0.5f, 0.6f, 0.7f, 0.7f), null, 2f);
            }
        }

        // nodes
        foreach (var n in _nodes)
        {
            Vector2 p = W2S(EffX(n), EffY(n));
            var r = new Rect(p.x, p.y, NODE_W * _zoom, NODE_H * _zoom);
            if (!r.Overlaps(local)) continue;

            Color tint = EraColor(n.Era);
            if (n == _selected) tint = Color.Lerp(tint, Color.white, 0.4f);
            EditorGUI.DrawRect(r, tint);
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), new Color(0, 0, 0, 0.4f));

            // markers: modded badge (left stripe, scales with zoom) + edited dot (top-right)
            if (n.AnyModded)
            {
                float bw = Mathf.Max(3f, 4f * _zoom);
                EditorGUI.DrawRect(new Rect(r.x, r.y, bw, r.height), MOD_BADGE);
            }
            if (IsEdited(n))
            {
                float d = Mathf.Max(5f, 7f * _zoom);
                EditorGUI.DrawRect(new Rect(r.xMax - d - 2, r.y + 2, d, d), EDIT_DOT);
            }

            if (showLabels)
            {
                int fs = Mathf.Clamp(Mathf.RoundToInt(11f * Mathf.Min(_zoom, 1.3f)), 7, 14);
                var style = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = fs,
                    fontStyle = FontStyle.Bold,
                    wordWrap = true,
                    normal = { textColor = Color.white }
                };
                GUI.Label(r, n.Label, style);

                if (_zoom >= LABEL_MIN_ZOOM)
                {
                    int cfs = Mathf.Clamp(Mathf.RoundToInt(9f * Mathf.Min(_zoom, 1.3f)), 7, 12);
                    var corner = new GUIStyle(EditorStyles.miniLabel)
                    { fontSize = cfs, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1, 1, 1, 0.75f) } };
                    float ch = cfs + 4f;
                    corner.alignment = TextAnchor.UpperLeft;
                    GUI.Label(new Rect(r.x + 5, r.y + 1, r.width - 8, ch), ShortEra(n.Era), corner);
                    corner.alignment = TextAnchor.UpperRight;
                    GUI.Label(new Rect(r.x + 2, r.y + 1, r.width - 12, ch), ShortTier(n.Tier), corner);
                }
            }
        }

        _pan = panSave;
        GUI.EndClip();
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

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Prerequisites (OR):", EditorStyles.boldLabel);
            var prereqs = EffPrereqs(n);
            if (prereqs.Count == 0)
                EditorGUILayout.LabelField("  (none — root)", EditorStyles.miniLabel);
            else
                foreach (var pr in prereqs.ToList())
                {
                    EditorGUILayout.BeginHorizontal();
                    string label = _byName.TryGetValue(pr, out var s) ? $"{s.Label}" : pr;
                    if (GUILayout.Button(label, EditorStyles.miniButton) && _byName.TryGetValue(pr, out var sn))
                    { _selected = sn; EditorGUIUtility.PingObject(sn.Asset); Selection.activeObject = sn.Asset; }
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

    // ── Edit operations (staging into the overlay; disk write is at Save) ──────
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
        Debug.Log($"[TechTree] Saved: {pos} position, {pre} prerequisite change(s)"
                + (skipped > 0 ? $" · {skipped} skipped (no writable target — see step 5; those edits are KEPT staged)" : "") + ".");
        foreach (var k in committed) _pending.Remove(k);
        Reload(keepView: true);
    }

    void RevertChanges()
    {
        if (_pending.Count == 0) return;
        if (EditorUtility.DisplayDialog("Revert", $"Discard {_pending.Count} unsaved change(s)?", "Discard", "Cancel"))
        { _pending.Clear(); Reload(keepView: true); }
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    static Color EraColor(string era) => era switch
    {
        "Era1" => new Color(0.35f, 0.45f, 0.55f),
        "Era2" => new Color(0.35f, 0.55f, 0.45f),
        "Era3" => new Color(0.55f, 0.50f, 0.35f),
        "Era4" => new Color(0.55f, 0.40f, 0.40f),
        "Era5" => new Color(0.45f, 0.40f, 0.55f),
        "Era6" => new Color(0.50f, 0.45f, 0.50f),
        _      => new Color(0.40f, 0.40f, 0.42f),
    };
    static string ShortEra(string era)  => string.IsNullOrEmpty(era) ? "" : era.Replace("Era", "E");
    static string ShortTier(string tier) => string.IsNullOrEmpty(tier) ? "" : tier.Replace("Tier", "T");

    static bool LineCrossesRect(Vector2 a, Vector2 b, Rect r)
    {
        float minX = Mathf.Min(a.x, b.x), maxX = Mathf.Max(a.x, b.x);
        float minY = Mathf.Min(a.y, b.y), maxY = Mathf.Max(a.y, b.y);
        return !(maxX < r.xMin || minX > r.xMax || maxY < r.yMin || minY > r.yMax);
    }
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

    public TechPickerDropdown(AdvancedDropdownState state, List<TechTreeData.Node> candidates, Action<string> onPick)
        : base(state)
    {
        _candidates = candidates;
        _onPick = onPick;
        minimumSize = new Vector2(320, 420);
    }

    protected override AdvancedDropdownItem BuildRoot()
    {
        var root = new AdvancedDropdownItem("Add prerequisite");
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