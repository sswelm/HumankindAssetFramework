using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class DatabaseBrowser : EditorWindow
{
    class Entry
    {
        public UnityEngine.Object obj;
        public string name;
        public string typeName;
        public string scope;   // "Vanilla" | "Mod"
    }

    // Flattened display item: a type header (group mode) or an entry row
    class DI
    {
        public bool isHeader;
        public string headerType;
        public int count;
        public bool collapsed;
        public Entry entry;
    }

    // ── State ─────────────────────────────────────────────────────────────────
    List<Entry> _all = new();
    List<Entry> _view = new();
    List<DI>    _display = new();
    string _vanillaFolder = "Assets/VanillaReference";
    string VanillaFolderKey => "DescIndex.VanillaFolder." + Application.dataPath.GetHashCode();

    // filters
    string _search = "";
    List<string> _types = new();
    string _typeFilter = "";          // "" = any
    AdvancedDropdownState _typeDdState = new();
    bool _groupByType;
    HashSet<string> _collapsed = new();
    enum ScopeFilter { All, MyMod, Vanilla }
    static readonly string[] SCOPE_LABELS = { "All", "My Mod", "Vanilla" };
    ScopeFilter _scope = ScopeFilter.All;

    enum ViewMode { Window, ListOnly }
    static readonly string[] MODE_LABELS = { "Window", "Only List" };
    ViewMode _mode = ViewMode.Window;
    string ModeKey => "DatabaseBrowser.Mode";

    // selection + embedded inspector
    UnityEngine.Object _selected;
    Editor _editor;

    // layout
    float _leftWidth = 340f;
    bool  _draggingSplit;
    Vector2 _listScroll, _inspScroll;
    const float ROW_H = 18f;
    const float SPLIT_W = 5f;
    const float TYPE_COL_W = 160f;

    static readonly Color ROW_ALT    = new Color(1f, 1f, 1f, 0.03f);
    static readonly Color ROW_HOVER  = new Color(0.3f, 0.5f, 0.9f, 0.18f);
    static readonly Color ROW_SEL    = new Color(0.3f, 0.5f, 0.9f, 0.35f);
    static readonly Color ROW_LINE   = new Color(0f, 0f, 0f, 0.20f);
    static readonly Color HEADER_BG  = new Color(1f, 1f, 1f, 0.07f);

    GUIStyle _typeColStyle, _headerStyle;

    [MenuItem("Tools/HAF/Database Browser")]
    static void Open() => GetWindow<DatabaseBrowser>("Database Browser");

    void OnEnable()
    {
        wantsMouseMove = true;
        _vanillaFolder = EditorPrefs.GetString(VanillaFolderKey, "Assets/VanillaReference");
        _mode = (ViewMode)EditorPrefs.GetInt(ModeKey, 0);
        Refresh();
    }

    void OnDisable()
    {
        if (_editor != null) DestroyImmediate(_editor);
    }

    void InitStyles()
    {
        if (_typeColStyle == null)
        {
            _typeColStyle = new GUIStyle(EditorStyles.miniLabel)
            { alignment = TextAnchor.MiddleRight, normal = { textColor = new Color(0.6f, 0.6f, 0.6f) } };
        }
        if (_headerStyle == null)
            _headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
    }

    bool UnderVanilla(string p) =>
        !string.IsNullOrEmpty(_vanillaFolder) &&
        (p == _vanillaFolder || p.StartsWith(_vanillaFolder + "/", StringComparison.Ordinal));

    // ── Scan ──────────────────────────────────────────────────────────────────
    void Refresh()
    {
        var list = new List<Entry>();
        try
        {
            var guids = AssetDatabase.FindAssets("t:ScriptableObject");
            int n = guids.Length, i = 0;
            foreach (var guid in guids)
            {
                if (++i % 64 == 0)
                    EditorUtility.DisplayProgressBar("Database Browser", "Loading assets…", i / (float)n);
                var path = AssetDatabase.GUIDToAssetPath(guid);
                string scope = UnderVanilla(path) ? "Vanilla" : "Mod";
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (obj == null || obj is not ScriptableObject) continue;
                    list.Add(new Entry { obj = obj, name = obj.name, typeName = obj.GetType().Name, scope = scope });
                }
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        _all = list.OrderBy(e => e.typeName).ThenBy(e => e.name).ToList();
        _types = _all.Select(e => e.typeName).Distinct().OrderBy(s => s).ToList();
        ApplyFilters();
    }

    void ApplyFilters()
    {
        string s = _search.Trim().ToLowerInvariant();
        _view = _all.Where(e =>
            (_typeFilter.Length == 0 || e.typeName == _typeFilter) &&
            (_scope == ScopeFilter.All ||
             (_scope == ScopeFilter.MyMod && e.scope == "Mod") ||
             (_scope == ScopeFilter.Vanilla && e.scope == "Vanilla")) &&
            (s.Length == 0 || e.name.ToLowerInvariant().Contains(s))
        ).ToList();
        BuildDisplay();
        Repaint();
    }

    void BuildDisplay()
    {
        _display = new List<DI>(_view.Count + 32);
        if (!_groupByType)
        {
            foreach (var e in _view) _display.Add(new DI { entry = e });
        }
        else
        {
            foreach (var g in _view.GroupBy(e => e.typeName).OrderBy(g => g.Key))
            {
                bool collapsed = _collapsed.Contains(g.Key);
                _display.Add(new DI { isHeader = true, headerType = g.Key, count = g.Count(), collapsed = collapsed });
                if (!collapsed)
                    foreach (var e in g.OrderBy(x => x.name))
                        _display.Add(new DI { entry = e });
            }
        }
    }

    // ── GUI ───────────────────────────────────────────────────────────────────
    void OnGUI()
    {
        InitStyles();
        if (Event.current.type == EventType.MouseMove) Repaint();

        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        EditorGUI.BeginChangeCheck();
        _mode = (ViewMode)GUILayout.Toolbar((int)_mode, MODE_LABELS, EditorStyles.toolbarButton, GUILayout.Width(150));
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetInt(ModeKey, (int)_mode);
            if (_mode == ViewMode.ListOnly && _editor != null) { DestroyImmediate(_editor); _editor = null; }
        }
        GUILayout.Space(8);
        if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70))) Refresh();
        if (GUILayout.Button("Save Assets", EditorStyles.toolbarButton, GUILayout.Width(90))) AssetDatabase.SaveAssets();
        GUILayout.FlexibleSpace();
        GUILayout.Label($"{_view.Count} / {_all.Count}", EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        float toolbarH = EditorStyles.toolbar.fixedHeight > 0f ? EditorStyles.toolbar.fixedHeight : 21f;
        float top = toolbarH;
        float h = position.height - top;

        if (_mode == ViewMode.Window)
        {
            DrawLeft(new Rect(0, top, _leftWidth, h));
            DrawSplitter(new Rect(_leftWidth, top, SPLIT_W, h));
            DrawRight(new Rect(_leftWidth + SPLIT_W, top, position.width - _leftWidth - SPLIT_W, h));
        }
        else // ListOnly — full-width list; clicks drive the docked Inspector
        {
            DrawLeft(new Rect(0, top, position.width, h));
        }
    }

    // ── Left: filters + list ──────────────────────────────────────────────────
    void DrawLeft(Rect area)
    {
        GUILayout.BeginArea(area);

        EditorGUI.BeginChangeCheck();
        _search = EditorGUILayout.TextField("Search", _search);
        if (EditorGUI.EndChangeCheck()) ApplyFilters();

        // Searchable type dropdown
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Type", GUILayout.Width(40));
        if (GUILayout.Button(_typeFilter.Length == 0 ? "(any)" : _typeFilter, EditorStyles.popup))
        {
            var dd = new TypeDropdown(_typeDdState, _types, picked => { _typeFilter = picked; ApplyFilters(); });
            dd.Show(GUILayoutUtility.GetLastRect());
        }
        bool g = GUILayout.Toggle(_groupByType, "Group", EditorStyles.miniButton, GUILayout.Width(54));
        if (g != _groupByType) { _groupByType = g; BuildDisplay(); }
        EditorGUILayout.EndHorizontal();

        EditorGUI.BeginChangeCheck();
        _scope = (ScopeFilter)GUILayout.Toolbar((int)_scope, SCOPE_LABELS);
        if (EditorGUI.EndChangeCheck()) ApplyFilters();

        EditorGUILayout.Space(2);

        // Virtualized list over _display
        Rect listArea = GUILayoutUtility.GetRect(10, 10000, 10, 100000,
            GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        float contentW = listArea.width - 16;
        Rect content = new Rect(0, 0, contentW, _display.Count * ROW_H);

        _listScroll = GUI.BeginScrollView(listArea, _listScroll, content);
        int first = Mathf.Max(0, Mathf.FloorToInt(_listScroll.y / ROW_H));
        int last  = Mathf.Min(_display.Count, Mathf.CeilToInt((_listScroll.y + listArea.height) / ROW_H) + 1);
        Vector2 mouse = Event.current.mousePosition;

        string toggleType = null;
        for (int i = first; i < last; i++)
        {
            var di = _display[i];
            Rect row = new Rect(0, i * ROW_H, contentW, ROW_H);

            if (di.isHeader)
            {
                EditorGUI.DrawRect(row, HEADER_BG);
                bool expanded = EditorGUI.Foldout(new Rect(row.x + 2, row.y, row.width - 4, row.height),
                    !di.collapsed, $"{di.headerType}  ({di.count})", true, _headerStyle);
                if (expanded == di.collapsed) toggleType = di.headerType; // state flipped
                EditorGUI.DrawRect(new Rect(0, row.yMax - 1, contentW, 1), ROW_LINE);
                continue;
            }

            var e = di.entry;
            bool selected = e.obj == _selected;
            bool hover = row.Contains(mouse);
            if (selected)          EditorGUI.DrawRect(row, ROW_SEL);
            else if (hover)        EditorGUI.DrawRect(row, ROW_HOVER);
            else if ((i & 1) == 1) EditorGUI.DrawRect(row, ROW_ALT);

            float indent = _groupByType ? 14f : 0f;
            float typeW = _groupByType ? 0f : TYPE_COL_W;   // type column only in flat mode
            Rect nameRect = new Rect(row.x + 4 + indent, row.y, row.width - 8 - indent - typeW, row.height);
            string label = (e.scope == "Mod" ? "● " : "  ") + e.name;
            if (GUI.Button(nameRect, new GUIContent(label, $"{e.typeName} ({e.scope})"), EditorStyles.label))
                Select(e.obj);

            if (!_groupByType)
            {
                Rect typeRect = new Rect(row.xMax - TYPE_COL_W - 4, row.y, TYPE_COL_W, row.height);
                GUI.Label(typeRect, new GUIContent(e.typeName, e.typeName), _typeColStyle);
            }

            EditorGUI.DrawRect(new Rect(0, row.yMax - 1, contentW, 1), ROW_LINE);
        }
        GUI.EndScrollView();

        if (toggleType != null)
        {
            if (_collapsed.Contains(toggleType)) _collapsed.Remove(toggleType);
            else _collapsed.Add(toggleType);
            BuildDisplay();
            Repaint();
        }

        GUILayout.EndArea();
    }

    void Select(UnityEngine.Object obj)
    {
        bool changed = _selected != obj;
        _selected = obj;
        if (_mode == ViewMode.ListOnly)
        {
            // Drive the user's docked Inspector; re-ping even on repeat click
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
        }
        else if (changed)
        {
            if (_editor != null) { DestroyImmediate(_editor); _editor = null; }
            _inspScroll = Vector2.zero;
        }
        Repaint();
    }

    // ── Splitter ──────────────────────────────────────────────────────────────
    void DrawSplitter(Rect r)
    {
        EditorGUI.DrawRect(r, new Color(0, 0, 0, 0.3f));
        EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeHorizontal);
        var e = Event.current;
        if (e.type == EventType.MouseDown && r.Contains(e.mousePosition)) { _draggingSplit = true; e.Use(); }
        if (_draggingSplit && e.type == EventType.MouseDrag)
        {
            _leftWidth = Mathf.Clamp(e.mousePosition.x, 220f, position.width - 280f);
            Repaint(); e.Use();
        }
        if (e.type == EventType.MouseUp) _draggingSplit = false;
    }

    // ── Right: embedded inspector ─────────────────────────────────────────────
    void DrawRight(Rect area)
    {
        GUILayout.BeginArea(area);

        if (_selected == null)
        {
            EditorGUILayout.HelpBox("Select an element on the left to edit it.", MessageType.Info);
            GUILayout.EndArea();
            return;
        }

        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label(_selected.name, EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Ping", EditorStyles.toolbarButton, GUILayout.Width(50)))
        { EditorGUIUtility.PingObject(_selected); Selection.activeObject = _selected; }
        EditorGUILayout.EndHorizontal();

        if (_editor == null || _editor.target != _selected)
        {
            if (_editor != null) DestroyImmediate(_editor);
            _editor = Editor.CreateEditor(_selected);
        }

        // Constrain the embedded inspector to the pane width. Editors that size
        // controls to currentViewWidth (the window) overflow into a horizontal
        // scrollbar contained within this pane rather than the whole window.
        float prevLabel = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = Mathf.Clamp(area.width * 0.38f, 110f, 200f);

        _inspScroll = EditorGUILayout.BeginScrollView(_inspScroll);
        EditorGUILayout.BeginVertical(GUILayout.Width(area.width - 24));
        try { _editor.OnInspectorGUI(); }
        catch (ExitGUIException) { throw; }   // IMGUI control flow (object picker etc.), NOT an error — swallowing it breaks the layout contract mid-frame
        catch (Exception ex)
        {
            EditorGUILayout.HelpBox(
                "This element's custom inspector failed to render embedded (often an " +
                "Odin editor expecting the real Inspector). Use Ping to edit it in the " +
                "docked Inspector.\n\n" + ex.Message, MessageType.Warning);
        }
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndScrollView();

        EditorGUIUtility.labelWidth = prevLabel;
        GUILayout.EndArea();
    }

    // ── Searchable type dropdown ──────────────────────────────────────────────
    class TypeDropdown : AdvancedDropdown
    {
        readonly List<string> _types;
        readonly Action<string> _onPick;
        public TypeDropdown(AdvancedDropdownState state, List<string> types, Action<string> onPick) : base(state)
        {
            _types = types; _onPick = onPick;
            minimumSize = new Vector2(240, 320);
        }
        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem("Type");
            root.AddChild(new AdvancedDropdownItem("(any)"));
            foreach (var t in _types) root.AddChild(new AdvancedDropdownItem(t));
            return root;
        }
        protected override void ItemSelected(AdvancedDropdownItem item)
            => _onPick(item.name == "(any)" ? "" : item.name);
    }
}