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
/// New Additions when it's reference-only (TechTreeData.EnsureWritable). New-tech
/// creation (step 5) is the remaining piece.
///
/// LOCAL CHANGES to this file (it came from another modder's tool, see the note at TechPickerDropdown), 2026-11-21.
/// The list names what a user or a maintainer meets; the full set is the file's diff since 2026-08-24.
///  - The prerequisite RULE per technology. The game unlocks a technology on ANY ONE prerequisite (OR); HAF's Tech
///    Requires All list (TechRequireAllRegistry, applied by the plugin) names the ones that wait for ALL (AND). The
///    side strip shows which rule the selected technology has and switches it; the switch is staged like every other
///    edit and written to that list on Save. A card under the AND rule carries "AND" under its title.
///  - Scroll bars under and beside the canvas. They move the same pan as the middle-mouse drag.
///  - X and Y fields in the side strip: the selected node's position as numbers, staged like a drag.
///  - The selection is kept by name across a reload (it was the node object from before it).
///  - In the game / Switched off (TechnologyDefinition.Visibility), as a switch and a note in the side strip. Never
///    switches a technology OFF in the game (see TechTreeData.Node.BaseVisibility); its card is faint and marked OFF.
///  - Staged edits, the selection and the view survive a script reload (they were lost without a word), and a reload
///    that does not find a technology keeps its staged edits.
///  - The canvas looks like the game's tech screen (asked for by the user, 2026-11-21): cards in the game's
///    proportions and spacing, and links routed by the game's own rule. See the GEOMETRY block and LinkPath.
///    A card shows its artwork: the project's own image file, or one of the game's own out of the game's UI bundle
///    (TechTreeData.ArtPathOf, TechTreeVanillaArt, ArtRect).
///  - Editing is ON when the window opens and the choice is remembered (EditorPrefs "TechTree.EditMode").
///  - Save writes part by part (CommitChanges), says what each outcome guarantees (RuleNote), never writes a
///    technology that is not in the tree, and is NOT registered with Undo; an undo that changes technology assets
///    in memory is said (OnUndoRedo).
///  - Frame All may zoom below the wheel's floor (MIN_ZOOM); a drag starts after a few pixels and not on a card
///    that is only a pixel or two high.
///  - Seams and hooks for the list's window and the drill: BuildTreeImpl, RepaintOpenWindows.
///  - What a technology unlocks (asked for by the user, 2026-11-22): a row of icons along the bottom of its card,
///    where the game's card has it, in the game's order, and the list with titles in the side strip. Read by
///    TechTreeUnlocks.cs from the definition's own effects; shown only, not edited here. For it the internal name
///    moved from the card's bottom row to the row of era and tier.
///  - The era (asked for by the user, 2026-11-22): a technology's era is a choice in the side strip, staged and
///    saved like the rest (TechnologyDefinition.EraReference), and the canvas draws the game's era separators: the
///    slanted line with a Roman numeral on each side, where the game's TechnologyScreen.BindAllSeparators puts it
///    (EraSeparators); its numerals are pinned to the top of the view, the user's choice. The side strip warns where an era change would make a technology free, and says when an
///    era-exclusive technology can be researched. A card's artwork is drawn after EVERY card, so nothing of a card
///    crosses it: the user's choice. In the game the artwork is the last child of its own card (over its title, the
///    title's line and its unlock row), but a later card can cover an earlier card's artwork.
///  - The title, the description and the image of a technology (asked for by the user, 2026-11-22), staged and
///    saved into its UI mapper like a position: Title and Description as the text the game shows, the image into
///    the card's and the popup's entries (TechTreeData.TechnologyImageKeys). A project picture, or the image of
///    another technology (which covers the game's own).
///  - Shift: every card right of the selected one moved by a number of columns, as one staged edit per card
///    (ShiftRightOf; asked for by the user, 2026-11-21, to make room in the tree without moving cards one by one).
/// </summary>
public class TechTreeWindow : EditorWindow, ISerializationCallbackReceiver
{
    // ── Pending overlay: staged, not-yet-saved edits per tech name ────────────
    class Pending
    {
        public int? X, Y;                // staged position (null = unchanged)
        public List<string> Prereqs;     // staged prereq list (null = unchanged)
        public bool? RequireAll;         // staged rule: true = ALL prerequisites (AND), false = any one (OR); null = unchanged
        public int? Visibility;          // staged TechnologyDefinition.Visibility (null = unchanged)
        public string Era;               // staged era name, TechnologyDefinition.EraReference (null = unchanged)
        public string Title, Description;   // staged mapper texts (null = unchanged)
        public string Image;             // staged image as a Unity GUID text (null = unchanged)
        public bool IsEmpty => X == null && Y == null && Prereqs == null && RequireAll == null && Visibility == null && Era == null
                               && Title == null && Description == null && Image == null;
    }
    readonly Dictionary<string, Pending> _pending = new();

    // The overlay as Unity can carry it across a script reload (a recompile, entering Play mode): a Dictionary of
    // nullables is not serialized, so every staged edit was lost without a word (review 2026-11-21).
    [Serializable]
    struct PendingRecord
    {
        public string name;
        public bool hasX, hasY, hasPrereqs, hasRule, hasVisibility, hasEra, hasTitle, hasDescription, hasImage;
        public int x, y, visibility;
        public string era, title, description, image;
        public bool requireAll;
        public List<string> prereqs;
    }
    [SerializeField] List<PendingRecord> _pendingRecords = new List<PendingRecord>();
    [SerializeField] string _selectedName;   // the selection across a script reload
    [SerializeField] string _stagedSession;  // the editor session the staged edits belong to (see OnEnable)
    // The side strip's Shift field: columns, negative = to the left. 6 to start with (the user, 2026-11-21): the
    // step between two neighbouring cards in ENC's tree, so one shift makes room for one card.
    [SerializeField] int _shiftBy = DefaultShift;
    internal const int DefaultShift = 6;
    string _shiftNote = "";                  // what the last Shift did, shown under its row until the selection changes
    string _shiftNoteFor;                    // the technology that note belongs to

    void ISerializationCallbackReceiver.OnBeforeSerialize()
    {
        _pendingRecords.Clear();
        foreach (var kv in _pending)
        {
            var p = kv.Value;
            _pendingRecords.Add(new PendingRecord
            {
                name = kv.Key,
                hasX = p.X.HasValue, x = p.X ?? 0, hasY = p.Y.HasValue, y = p.Y ?? 0,
                hasPrereqs = p.Prereqs != null, prereqs = p.Prereqs != null ? new List<string>(p.Prereqs) : new List<string>(),
                hasRule = p.RequireAll.HasValue, requireAll = p.RequireAll ?? false,
                hasVisibility = p.Visibility.HasValue, visibility = p.Visibility ?? 0,
                hasEra = p.Era != null, era = p.Era ?? "",
                hasTitle = p.Title != null, title = p.Title ?? "",
                hasDescription = p.Description != null, description = p.Description ?? "",
                hasImage = p.Image != null, image = p.Image ?? "",
            });
        }
        if (_selected != null) _selectedName = _selected.Name;
    }

    void ISerializationCallbackReceiver.OnAfterDeserialize()
    {
        _pending.Clear();
        foreach (var r in _pendingRecords)
        {
            if (string.IsNullOrEmpty(r.name)) continue;
            var p = new Pending
            {
                X = r.hasX ? r.x : (int?)null, Y = r.hasY ? r.y : (int?)null,
                Prereqs = r.hasPrereqs ? new List<string>(r.prereqs ?? new List<string>()) : null,
                RequireAll = r.hasRule ? r.requireAll : (bool?)null,
                Visibility = r.hasVisibility ? r.visibility : (int?)null,
                Era = r.hasEra ? (r.era ?? "") : null,
                Title = r.hasTitle ? (r.title ?? "") : null,
                Description = r.hasDescription ? (r.description ?? "") : null,
                Image = r.hasImage ? (r.image ?? "") : null,
            };
            if (!p.IsEmpty) _pending[r.name] = p;
        }
    }

    List<TechTreeData.Node> _nodes;
    Dictionary<string, TechTreeData.Node> _byName;
    List<TechTreeData.Era> _eras = new List<TechTreeData.Era>();   // the project's eras, by index
    TechTreeData.Node _selected;

    // The Tech Requires All list as this window last read it: the technologies under the AND rule.
    HashSet<string> _requireAll = new(StringComparer.Ordinal);
    bool _rulesUnreadable;    // the list could not be read: the rule is not shown and cannot be switched
    bool _rulesStoreFailed;   // TechRequireAllRegistry.LastLoadFailed when _requireAll was read
    bool _rulesStale;         // the list changed outside the editor and the game still reads an older copy of it
    bool _rulesStoreStale;    // TechRequireAllRegistry.LastLoadStale when _requireAll was read
    bool _dropFocus;          // a reload changed values under a possibly focused field: drop the focus at the next draw
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
    const string SessionKey = "TechTree.StagedInThisSession";

    // position-drag state (edit mode)
    bool _dragMoved;          // the mouse has moved far enough for this click to be a drag
    TechTreeData.Node _dragNode;
    Vector2 _dragStartMouse;
    int _dragStartX, _dragStartY;

    // view transform (kept across a script reload)
    [SerializeField] Vector2 _pan;
    [SerializeField] float   _zoom = 1f;
    [SerializeField] bool    _framed;
    bool    _panning;
    bool    _fitWholeTree;   // the next framing shows the whole tree (the Frame All button); otherwise its height, as the game does

    // ── GEOMETRY of the game's tech screen ────────────────────────────────────
    // The game puts a card at (TechTreeX * columnsSize, TechTreeY * rowsSize) (TechnologyScreen) and routes the links
    // between the cards' rectangles (TechnologyScreen_Links). The numbers are the game's own, READ from its prefabs
    // (AssetBundles/UI_Textures, with UnityPy, 2026-11-21), in the game's own units:
    //   TechnologyScreen        columnsSize 74, rowsSize 22 (the code's defaults, 148 / 66, are overridden there)
    //   TechnologyItem          310 x 116
    //   TechnologyScreen_Links  curviness 40, thickness 1, availableColor = completedColor = (1, 0.8745, 0.5843)
    //   the card's "Artwork"    200 x 200, from 200 right of the card's left edge and 60 above its top
    // A first version measured the ratios from a screenshot of the game (six columns 720 px, 26 rows 927 px, a card
    // 502 x 187 px): 4.18 columns and 5.25 rows per card against the prefab's 4.19 and 5.27.
    const float COL = 74f, ROW = 22f;              // one step of TechTreeX / TechTreeY
    const float NODE_W = 310f, NODE_H = 116f;      // a card
    const float LINK_CURVE = 40f;                  // radius of a link's elbow
    const int   CORNER_STEPS = 6;                  // line pieces an elbow is drawn with
    const float LABEL_MIN_ZOOM = 0.25f;
    // How far out the WHEEL goes. 0.05 was the floor of the old, narrower geometry and Frame All stopped there too:
    // with the game's column width the ENC tree (TechTreeX up to 411) needs about 0.02 to fit a 1000 px window, so
    // both its ends were cut off. Frame All is not floored at this (FIT_MIN_ZOOM): at the window's minimum width it
    // needs less still.
    const float MIN_ZOOM = 0.01f;
    const float FIT_MIN_ZOOM = 0.001f;
    const float DRAG_START = 4f;        // pixels the mouse must move before a click on a card becomes a drag
    const float DRAG_MIN_ROW = 2f;      // no card drag while a row is fewer pixels than this: a one-pixel slip would move a card rows
    const float SIDE_W = 300f;
    const float BAR = 15f;             // thickness of the canvas scroll bars
    const float SCROLL_MARGIN = 60f;   // empty space the bars allow around the tree, in pixels

    static readonly Color EDIT_DOT = new Color(1f, 0.65f, 0.1f);     // amber: unsaved
    static readonly Color MOD_BADGE = new Color(0.4f, 0.7f, 1f);     // blue: modded on disk
    static readonly Color AND_MARK = new Color(1f, 0.85f, 0.45f);    // the "AND" on a card
    // The game's look for a technology that is not researched yet: a dark card with a thin gold edge on dark indigo.
    // The card's and the canvas's colours are read off a screenshot by eye; GOLD is the game's own link colour
    // (TechnologyScreen_Links.availableColor, as its prefab holds it).
    static readonly Color CANVAS = new Color(0.075f, 0.065f, 0.19f);
    static readonly Color CARD = new Color(0.25f, 0.24f, 0.38f);
    static readonly Color CARD_SELECTED = new Color(0.36f, 0.35f, 0.52f);
    static readonly Color CARD_EDGE = new Color(0.72f, 0.62f, 0.42f);
    static readonly Color GOLD = new Color(1f, 0.8745f, 0.5843f);
    static readonly Color LINK = new Color(1f, 0.8745f, 0.5843f, 0.8f);

    static readonly GUIContent[] RuleChoices =
    {
        new GUIContent("OR — any one", "The game's own rule: the technology unlocks when ANY ONE of its prerequisites is researched."),
        new GUIContent("AND — all", "The technology unlocks only when ALL its prerequisites are researched. HAF's plugin applies this to the technologies in the Tech Requires All list; Save writes the switch to that list. Relaunch the game to apply."),
    };
    static readonly GUIContent[] VisibilityChoices =
    {
        new GUIContent("In the game", "The game creates this technology (Visibility = Always, or the value its definition already had)."),
        new GUIContent("Switched off", "Visibility = Never. The game does not create this technology for any empire: it is not on the tech screen and, by the game's own rules, cannot be researched, traded or granted. Scenarios use this to disable a technology. Save writes it to the technology's definition."),
    };
    static readonly Color HIDDEN_MARK = new Color(0.75f, 0.78f, 0.9f);   // the "OFF" / "HIDDEN" on a card
    // The frames of the icons in a card's unlock row, one colour per kind (the game uses differently shaped frames;
    // the colours are this window's own).
    static readonly Color UNLOCK_UNIT = new Color(0.13f, 0.24f, 0.42f);
    static readonly Color UNLOCK_EXTENSION = new Color(0.13f, 0.32f, 0.30f);
    static readonly Color UNLOCK_INFRASTRUCTURE = new Color(0.34f, 0.27f, 0.17f);
    static readonly Color UNLOCK_OTHER = new Color(0.27f, 0.22f, 0.36f);
    // The game's card (310 x 116) keeps a table 50 high along its bottom for these icons, 4 from the bottom edge.
    const float UNLOCK_SIDE = 46f, UNLOCK_GAP = 3f, UNLOCK_LEFT = 6f, UNLOCK_BOTTOM = 4f;
    const float NAME_MIN_ZOOM = 0.6f;     // the internal name is small print: not drawn below this zoom
    const float HIDDEN_ALPHA = 0.4f;                                    // how faint the card and links of a technology without a card in the game are
    // The game's era separator (the prefab "_SeparatorSample" of its tech screen, read with UnityPy 2026-11-22): a
    // group 428 wide centred on the separator's X, a label 150 x 130 at each end, font size 100, colour (0.47, 0.42,
    // 0.53, 0.15). The group is stretched over the screen's content, 920 high at the game's reference resolution
    // (1080 lines); the labels sit at half of that, and the cards start 24 below the content's top (verticalPadding).
    // In this window's units (a card's top at TechTreeY * 22) the labels are therefore at a fixed 460 - 24 = 436, not
    // at half the tree's height (the review of 2026-11-22; on the user's screenshot the "VI" sits at 437). The line
    // is an image 984 high from 32 above the content to 32 below it: -56 to 928 here.
    // The SLANT is the line's own texture (a 64 x 128 distance field whose edge runs 47 px across over 120 px down,
    // drawn at its own aspect; derived by the review, 0.394 measured on the screenshot). That the line crosses the
    // labels' height 13 units right of the separator's place is measured on the screenshot only.
    const float ERA_LABEL_OFFSET = 139f;   // from the separator's X to the middle of each numeral: 428 / 2 - 150 / 2
    const float ERA_LABEL_SIZE = 100f;
    const float ERA_LABEL_Y = 436f;        // the numerals' height, and where the line crosses it
    const float ERA_LINE_TOP = -56f, ERA_LINE_BOTTOM = 928f;
    const float ERA_SLANT = 47f / 120f;    // units to the right per unit up
    const float ERA_LINE_SHIFT = 13f;      // the line at the numerals' height, right of the separator's place
    // The numerals are PINNED to the top of the view (asked for by the user, 2026-11-22: "so I can always see it"),
    // where the game has them at ERA_LABEL_Y: they stand beside the line where it crosses the view's top, at the
    // game's size and faintness when zoomed in, never smaller than ERA_LABEL_MIN pixels and less faint when small.
    const int   ERA_LABEL_MIN = 14;
    const float ERA_LABEL_TOP_GAP = 4f;    // pixels between the view's top and the numerals' box
    const float ERA_NUMBER_SMALL_ALPHA = 0.55f;
    static readonly Color ERA_NUMBER = new Color(0.4706f, 0.4196f, 0.5294f, 0.149f);
    // The prefab's line colour is (0.30, 0.30, 0.54, 0.56) under a stroke material this window does not have; drawn
    // as a plain line that is all but invisible on the canvas, so it is the colour the line HAS on the screenshot.
    static readonly Color ERA_LINE = new Color(0.40f, 0.38f, 0.72f, 0.85f);

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
        // Staged edits survive a SCRIPT reload, not an editor restart or a layout from another day: Unity also writes
        // a window's serialized state into the layout, and an old staged prerequisite list would go over whatever the
        // project holds by then. SessionState lives exactly as long as the editor process.
        // The edits carry the id of the session they were staged in. Only a DIFFERENT id drops them: edits without one
        // come from the window as it was before this id existed, in this same session, and are kept.
        string session = SessionState.GetString(SessionKey, "");
        if (session == "") { session = Guid.NewGuid().ToString("N"); SessionState.SetString(SessionKey, session); }
        if (_pending.Count > 0 && !string.IsNullOrEmpty(_stagedSession) && _stagedSession != session)
        {
            Debug.LogWarning($"[TechTree] {_pending.Count} staged edit(s) from an earlier editor session were dropped (staged edits are kept across script reloads, not across a restart).");
            _pending.Clear();
        }
        _stagedSession = session;
        try { Reload(keepView: _framed); }   // after a script reload the view is still the one the user had
        catch (Exception e) { Debug.LogError($"[TechTree] Build failed on open: {e}"); _nodes = new(); _byName = new(); }
    }

    internal static string SaveLabel(int live, int missing) =>
        "Save" + (live > 0 ? $" ({live})" : "") + (missing > 0 ? $" · {missing} missing" : "");

    /// <summary>The Tech Requires All window saved or reloaded the list: draw again, the rules are re-read then.</summary>
    internal static void RepaintOpenWindows()
    {
        RepaintRequests++;
        foreach (var w in Resources.FindObjectsOfTypeAll<TechTreeWindow>()) w.Repaint();
    }
    internal static int RepaintRequests;   // counted so that a drill, which draws the windows itself, can see the request

    void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
    }

    void OnUndoRedo()
    {
        // Undo is not this window's: a Save here is not registered with it (see CommitChanges) and staged edits never
        // were. But an undo anywhere in the editor can still change technology assets IN MEMORY (an Inspector edit, a
        // Save by an older version of this window in the same session). The window shows that state and SAYS so: it is
        // not on disk yet, and the project's next save writes it. Not the rules: the list is not under Undo, and this
        // fires for every undo anywhere in the editor.
        var before = _byName;
        Reload(keepView: true, rules: false);
        int changed = 0; string example = null;
        if (before != null)
            foreach (var n in _nodes)
                if (before.TryGetValue(n.Name, out var was)
                    && (was.BaseX != n.BaseX || was.BaseY != n.BaseY || was.BaseVisibility != n.BaseVisibility || (was.Era ?? "") != (n.Era ?? "")
                        || (was.TitleKey ?? "") != (n.TitleKey ?? "") || (was.BaseDescription ?? "") != (n.BaseDescription ?? "") || (was.ArtGuid ?? "") != (n.ArtGuid ?? "")
                        || !(was.BasePrereqs ?? new string[0]).SequenceEqual(n.BasePrereqs ?? new string[0])))
                { changed++; if (example == null) example = n.Label ?? n.Name; }
        if (changed > 0)
        {
            string note = UndoNote(changed, example);
            Debug.LogWarning("[TechTree] " + note);
            ShowNotification(new GUIContent(note), 10);
        }
        Repaint();
    }

    internal static string UndoNote(int changed, string example) =>
        $"Undo / Redo changed {changed} technolog{(changed == 1 ? "y" : "ies")} in memory ({example}{(changed > 1 ? ", ..." : "")}): position, era, title, description, image, prerequisites or visibility " +
        "are now different from the file on disk, and the next save of the project writes them as they are shown here. Redo (or Undo) once more takes this back.";

    // Seam for the drill (tools/tech_requires_all_unity_drill.sh): its temporary project holds no technology assets.
    internal static Func<string, List<TechTreeData.Node>> BuildTreeImpl = modPath => TechTreeData.Build(modPath);
    internal static Func<List<TechTreeData.Era>> ErasImpl = () => TechTreeData.Eras();

    // `rules` = also read the Tech Requires All list again. Not needed where the list cannot have changed (undo,
    // Revert, a save without a rule switch): in a project without the list each read waits a quarter of a second.
    void Reload(bool keepView = false, bool rules = true)
    {
        _nodes = BuildTreeImpl(_modPath);
        _byName = _nodes.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.First());
        try { _eras = ErasImpl() ?? new List<TechTreeData.Era>(); }
        catch (Exception e) { _eras = new List<TechTreeData.Era>(); Debug.LogWarning("[TechTree] the project's eras could not be read (no era separators, no era choice): " + e.Message); }
        // Staged edits of a technology this reload did not find are KEPT (they were dropped: one reload with the
        // reference path half typed lost them for good). They are not drawn and not written while it is missing;
        // Save says so, Revert discards them.
        _art.Clear();
        _bundleFilters = null; _imageRefusal = "";
        // A focused X / Y field would keep showing what was typed before the reload, and the next key would build on
        // it. Reload also runs outside OnGUI (undo), where clearing the focus does not hold: the next draw does it.
        _dropFocus = true;
        // The nodes are new objects: keep the selection by NAME. The old object holds the values from before this
        // reload, so after a Save the side strip showed the previous position and prerequisites, the X / Y fields
        // compared against them, and the node lost its highlight (local change 2026-11-21).
        string keep = _selected != null ? _selected.Name : _selectedName;
        _selected = keep != null && _byName.TryGetValue(keep, out var again) ? again : null;
        _selectedName = keep;   // also while the technology is missing: it is selected again when it is back
        if (rules) LoadRules();
        if (!keepView) _framed = false;   // re-frame only on first load / explicit Frame All
        Repaint();
    }

    // Read the Tech Requires All list. A staged switch that the list now already holds is no longer a change.
    void LoadRules()
    {
        try
        {
            _requireAll = new HashSet<string>(TechRequireAllRegistry.Load(), StringComparer.Ordinal);
            var verdict = TechRequireAllRegistry.Snapshot();
            _rulesUnreadable = verdict.Failed; _rulesStale = verdict.Stale;
            // What this read did on the way (adopted a deployed copy, finished an owed deploy) is said HERE: left for
            // the list's own window it surfaced later, under another event.
            string notice = TechRequireAllRegistry.TakeNotice();
            if (notice != "") Debug.Log("[TechTree] reading the Tech Requires All list: " + notice);
        }
        catch (Exception e)
        {
            _requireAll = new HashSet<string>(StringComparer.Ordinal); _rulesUnreadable = true; _rulesStale = false;
            Debug.LogError("[TechTree] the Tech Requires All list could not be read: " + e);
        }
        _rulesVersion = TechRequireAllRegistry.LoadedVersion;
        _rulesStoreFailed = TechRequireAllRegistry.LastLoadFailed;
        _rulesStoreStale = TechRequireAllRegistry.LastLoadStale;
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
    int EffVisibility(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.Visibility.HasValue ? p.Visibility.Value : n.BaseVisibility;
    bool Hidden(TechTreeData.Node n) => !TechTreeData.OnTechScreen(EffVisibility(n));
    /// <summary>The title as stored (staged or saved): the text, or a "%..." key.</summary>
    string EffTitle(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.Title != null ? p.Title : (n.TitleKey ?? "");
    /// <summary>The title as the game would show it: a staged "%..." key translated where the project knows it.</summary>
    internal string EffLabel(TechTreeData.Node n)
    {
        if (!(_pending.TryGetValue(n.Name, out var p) && p.Title != null)) return n.Label ?? n.Name ?? "";
        return p.Title.StartsWith("%", StringComparison.Ordinal) && TechTreeData.TryLocalize(p.Title, out var text) ? text : p.Title;
    }
    /// <summary>What the Title field shows: the staged text, else the saved title as the game shows it (a key translated).</summary>
    internal string TitleShown(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.Title != null ? p.Title : (n.Label ?? "");
    /// <summary>What the Description box shows: the staged text, else the saved description as the game shows it.</summary>
    internal string DescriptionShown(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.Description != null ? p.Description : (n.Description ?? "");
    string EffDescription(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.Description != null ? p.Description : (n.BaseDescription ?? "");
    /// <summary>The card's image as a Unity GUID text (staged or saved); null when none is named.</summary>
    internal string EffImage(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.Image != null ? p.Image : n.ArtGuid;
    string EffEra(TechTreeData.Node n) => _pending.TryGetValue(n.Name, out var p) && p.Era != null ? p.Era : (n.Era ?? "");
    /// <summary>The game's index of that era (EraDefinition.EraIndex); -1 for a name the project has no era for.</summary>
    internal static int EraIndexOf(IList<TechTreeData.Era> eras, string name)
    {
        if (eras != null && !string.IsNullOrEmpty(name))
            foreach (var era in eras) if (era.Name == name) return era.Index;
        return -1;
    }
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
    // the same for a point in the game's units (a card is 310 x 116 of them, its left edge at TechTreeX * 74)
    Vector2 U2S(float ux, float uy) => new Vector2(ux * _zoom + _pan.x, uy * _zoom + _pan.y);
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
        // the tree's extent starts at 0, or lower where a card sits at a negative column or row
        float minX = Mathf.Min(0, _nodes.Min(EffX)), minY = Mathf.Min(0, _nodes.Min(EffY));
        float w = (_nodes.Max(EffX) - minX) * COL + NODE_W + 2 * margin;
        float h = (_nodes.Max(EffY) - minY) * ROW + NODE_H + 2 * margin;
        float zx = canvas.width / w, zy = canvas.height / h;
        if (_fitWholeTree)
        {
            _zoom = Mathf.Clamp(Mathf.Min(zx, zy), FIT_MIN_ZOOM, 2f);
            _pan = new Vector2(canvas.x + (canvas.width - w * _zoom) * 0.5f + (margin - minX * COL) * _zoom,
                               canvas.y + (canvas.height - h * _zoom) * 0.5f + (margin - minY * ROW) * _zoom);
        }
        else
        {
            _zoom = Mathf.Clamp(zy, FIT_MIN_ZOOM, 1f);
            _pan = new Vector2(canvas.x + (margin - minX * COL) * _zoom, canvas.y + (canvas.height - h * _zoom) * 0.5f + (margin - minY * ROW) * _zoom);
        }
        _fitWholeTree = false;
        _framed = true;
    }

    void OnGUI()
    {
        if (Event.current.type == EventType.MouseMove) Repaint();
        if (_dropFocus && Event.current.type == EventType.Layout)
        { _dropFocus = false; GUIUtility.keyboardControl = 0; EditorGUIUtility.editingTextField = false; }
        // The Tech Requires All window (or this one) saved the list since this window read it: show the list as it is now.
        // Also when only its readability or staleness changed: "Keep it empty" and "Deploy the source" in the list's
        // window change those under the same version.
        if (Event.current.type == EventType.Layout
            && (TechRequireAllRegistry.LoadedVersion != _rulesVersion || TechRequireAllRegistry.LastLoadFailed != _rulesStoreFailed
                || TechRequireAllRegistry.LastLoadStale != _rulesStoreStale)) LoadRules();

        // ── Toolbar ──
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUI.backgroundColor = _editMode ? EDIT_DOT : Color.white;
        bool edit = GUILayout.Toggle(_editMode, new GUIContent(_editMode ? "● Edit: ON" : "Edit: OFF",
            "ON: drag a card or type its X / Y, switch it off in the game, switch OR / AND, add or remove prerequisites. Nothing is written until Save. OFF: look only."),
            EditorStyles.toolbarButton, GUILayout.Width(80));
        GUI.backgroundColor = Color.white;
        if (edit != _editMode) { _editMode = edit; EditorPrefs.SetBool(EditModeKey, edit); }
        if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(60))) { TechTreeVanillaArt.RetryFailed(); Reload(keepView: true); }
        if (GUILayout.Button("Frame All", EditorStyles.toolbarButton, GUILayout.Width(70))) { _framed = false; _fitWholeTree = true; }

        // Staged edits of a technology that is not in the tree right now cannot be saved: they are counted apart,
        // and Save is for the ones that can.
        int live = _byName == null ? 0 : _pending.Keys.Count(k => _byName.ContainsKey(k)), missing = _pending.Count - live;
        using (new EditorGUI.DisabledScope(live == 0))
        {
            GUI.backgroundColor = live > 0 ? EDIT_DOT : Color.white;
            if (GUILayout.Button(new GUIContent(SaveLabel(live, missing), "Writes the staged edits into the assets and the Tech Requires All list. A Save is not part of Undo: Ctrl+Z does not take it back (Revert discards edits BEFORE they are saved)."),
                                 EditorStyles.toolbarButton, GUILayout.Width(missing > 0 ? 150 : 80)))
                CommitChanges();
            GUI.backgroundColor = Color.white;
        }
        using (new EditorGUI.DisabledScope(!Dirty))
            if (GUILayout.Button("Revert", EditorStyles.toolbarButton, GUILayout.Width(60))) RevertChanges();

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
            _zoom = Mathf.Clamp(_zoom * (1f - e.delta.y * 0.05f), MIN_ZOOM, 3f);
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
                if (_editMode && ROW * _zoom >= DRAG_MIN_ROW)   // begin a potential position drag
                {
                    _dragNode = hit; _dragMoved = false;
                    _dragStartMouse = e.mousePosition;
                    _dragStartX = EffX(hit);
                    _dragStartY = EffY(hit);
                }
                e.Use(); Repaint();
            }
        }
        else if (e.type == EventType.MouseDrag && _dragNode != null)
        {
            // a click that slips a pixel or two is a click
            if (!_dragMoved && (e.mousePosition - _dragStartMouse).sqrMagnitude < DRAG_START * DRAG_START) { e.Use(); return; }
            _dragMoved = true;
            int dx = Mathf.RoundToInt((e.mousePosition.x - _dragStartMouse.x) / (COL * _zoom));
            int dy = Mathf.RoundToInt((e.mousePosition.y - _dragStartMouse.y) / (ROW * _zoom));
            int nx = Mathf.Max(FloorOf(_dragNode.BaseX, _dragStartX), _dragStartX + dx);
            int ny = Mathf.Max(FloorOf(_dragNode.BaseY, _dragStartY), _dragStartY + dy);
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
        TechTreeData.Node card = null;
        for (int i = _nodes.Count - 1; i >= 0 && card == null; i--)
            if (NodeRect(_nodes[i]).Contains(screen)) card = _nodes[i];
        if (card == null) return null;   // a click on the canvas beside the cards selects nothing, artwork or not
        // Over a card, an artwork is on top of it (they are drawn after every card): the last one drawn there wins.
        // The rectangles are the last repaint's, in the canvas's own coordinates (review of 2026-11-22).
        for (int i = _artDraws.Count - 1; i >= 0; i--)
            if (_artDraws[i].Node != null && _artDraws[i].Rect.Contains(screen - _artOrigin)) return _artDraws[i].Node;
        return card;
    }

    // ── Canvas ────────────────────────────────────────────────────────────────
    void DrawCanvas(Rect canvas)
    {
        EditorGUI.DrawRect(canvas, CANVAS);
        GUI.BeginClip(canvas);
        Rect local = new Rect(0, 0, canvas.width, canvas.height);
        Vector2 panSave = _pan; _pan -= new Vector2(canvas.x, canvas.y);

        // Everything below only draws; input is HandleInput's. Skipping the other events saves the per-card work
        // (styles, upper-casing, measuring) on each of them.
        if (Event.current.type != EventType.Repaint) { _pan = panSave; GUI.EndClip(); return; }

        bool showLabels = _zoom >= LABEL_MIN_ZOOM;

        _artOrigin = new Vector2(canvas.x, canvas.y);
        _cardsDrawn = 0;
        DrawEraSeparators(local);   // under the links and the cards, as in the game

        // links, routed as the game routes them (from the effective prerequisites)
        if (Event.current.type == EventType.Repaint)
        {
            Handles.color = LINK;
            float width = Mathf.Clamp(1.5f * _zoom, 1f, 2.5f);
            foreach (var n in _nodes)
            {
                Rect to = NodeRect(n);
                bool toHidden = Hidden(n);
                foreach (var pr in EffPrereqs(n))
                {
                    if (!_byName.TryGetValue(pr, out var src)) continue;
                    Rect from = NodeRect(src);
                    // the game draws no link to or from a technology it does not show: faint here
                    Handles.color = toHidden || Hidden(src) ? new Color(LINK.r, LINK.g, LINK.b, LINK.a * HIDDEN_ALPHA) : LINK;
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
            alignment = TextAnchor.MiddleLeft, fontSize = Mathf.Clamp(Mathf.RoundToInt(15f * z), 6, 22),
            wordWrap = false, clipping = TextClipping.Clip, normal = { textColor = GOLD }
        };
        var infoStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleLeft, fontSize = Mathf.Clamp(Mathf.RoundToInt(11f * z), 6, 16),
            wordWrap = false, clipping = TextClipping.Clip, normal = { textColor = new Color(1, 1, 1, 0.7f) }
        };
        var nameStyle = new GUIStyle(infoStyle) { normal = { textColor = new Color(1, 1, 1, 0.38f) } };
        var andStyle = new GUIStyle(infoStyle) { fontStyle = FontStyle.Bold, normal = { textColor = AND_MARK } };
        var hiddenStyle = new GUIStyle(infoStyle) { fontStyle = FontStyle.Bold, normal = { textColor = HIDDEN_MARK } };
        var unknownStyle = new GUIStyle(infoStyle) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };

        // The artwork of a card is drawn after ALL the cards (the second loop below): nothing of any card may cross it
        // (the user, 2026-11-22: the title's line ran through the images). In the game the artwork is the last child
        // of its own card, over its title, the title's line and its unlock row; a card that comes later in the game's
        // list of technologies is drawn over an earlier card's artwork, which this window does not copy.
        _artDraws.Clear();
        foreach (var n in _nodes)
        {
            Rect r = NodeRect(n);
            Rect artRect = ArtRect(r);
            if (!r.Overlaps(local) && !((n.ArtNamed || EffImage(n) != n.ArtGuid) && artRect.Overlaps(local))) continue;

            bool selected = n == _selected;
            bool hidden = Hidden(n);
            float fade = hidden ? HIDDEN_ALPHA : 1f;   // a technology without a card in the game is faint, and says OFF or HIDDEN
            float radius = 5f * _zoom;
            // the card: fill, then the thin gold edge (brighter and thicker on the selected one)
            Color fill = selected ? CARD_SELECTED : CARD, edge = selected ? GOLD : CARD_EDGE;
            fill.a *= fade; if (!selected) edge.a *= fade;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, fill, 0, radius);
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, edge, selected ? 2f : 1f, radius);
            _cardsDrawn++;
            GUI.color = new Color(1f, 1f, 1f, fade);   // the texts; reset at the end of this card

            // the artwork, over the card's right end and standing out of it: remembered, drawn after the cards
            if (TryArt(n, out var art, out var artUv, out float artAspect))
                _artDraws.Add(new ArtDraw { Rect = FitInside(artRect, artAspect), Texture = art, Uv = artUv, Fade = fade, Node = n });

            float pad = 10f * _zoom;
            float titleH = 0.29f * r.height;   // the game's title band: the divider sits at 29% of the card's height

            // markers: modded on disk (blue bar inside the left edge) + unsaved edit (amber dot, top right)
            if (n.AnyModded)
                EditorGUI.DrawRect(new Rect(r.x + 1, r.y + radius, Mathf.Max(2f, 3f * _zoom), r.height - 2 * radius), MOD_BADGE);
            if (IsEdited(n))
            {
                float d = Mathf.Max(5f, 8f * _zoom);
                GUI.DrawTexture(new Rect(r.xMax - d - pad * 0.5f, r.y + (titleH - d) * 0.5f, d, d), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, EDIT_DOT, 0, d * 0.5f);
            }

            if (!showLabels) { GUI.color = Color.white; continue; }

            // title, upper case as in the game, over the divider. The game does not shrink or cut a long title: its
            // label grows to the text's width and runs on under the artwork, which covers it (the review of
            // 2026-11-22, the game's Title label: autoAdjustWidth without a maximum). The same here.
            GUI.Label(new Rect(r.x + pad, r.y, r.width - 2 * pad - (IsEdited(n) ? 10f * _zoom : 0), titleH), CardTitle(n), titleStyle);
            EditorGUI.DrawRect(new Rect(r.x + pad, r.y + titleH, r.width - 2 * pad, 1f), new Color(1f, 1f, 1f, 0.22f));

            // under it, where the game shows the turns: era and tier, and the AND rule where it has an effect
            float rowH = 0.24f * r.height;
            var info = new Rect(r.x + pad, r.y + titleH + 2f * _zoom, r.width - 2 * pad, rowH);
            string eraTier = CardEraTier(n);
            GUI.Label(info, eraTier, infoStyle);
            // the marks, right behind era and tier: the card's right end belongs to the artwork
            float used = infoStyle.CalcSize(new GUIContent(eraTier)).x + 8f * _zoom;
            if (!_rulesUnreadable && EffRequireAll(n) && EffPrereqs(n).Count >= 2)
            {
                GUI.Label(new Rect(info.x + used, info.y, info.width - used, info.height), "AND", andStyle);
                used += andStyle.CalcSize(new GUIContent("AND")).x + 6f * _zoom;
            }
            if (hidden)
            {
                GUI.color = Color.white;   // the mark itself is not faint
                // OFF = Never, not in the game at all; HIDDEN = in the game, only without a card on its tech screen
                GUI.Label(new Rect(info.x + used, info.y, info.width - used, info.height), EffVisibility(n) == TechTreeData.VisibilityNever ? "OFF" : "HIDDEN", hiddenStyle);
                GUI.color = new Color(1f, 1f, 1f, fade);
            }
            // the internal name, faint, behind the marks (the bottom of the card is the unlock row's). Every name
            // starts with "Technology_": left out, so the part that tells the names apart has room.
            if (hidden) used += hiddenStyle.CalcSize(new GUIContent(EffVisibility(n) == TechTreeData.VisibilityNever ? "OFF" : "HIDDEN")).x + 6f * _zoom;
            if (_zoom >= NAME_MIN_ZOOM && info.x + used < r.xMax - pad - 20f * _zoom)
                GUI.Label(new Rect(info.x + used, info.y, r.xMax - pad - info.x - used, info.height), ShortName(n.Name), nameStyle);

            // what the technology unlocks, where the game's card shows it: a row of icons along the bottom
            if (n.Unlocks.Count > 0 && r.Overlaps(local))
            {
                foreach (var box in PlanUnlockRow(r, n.Unlocks))
                {
                    Color frame = box.Unlock == null ? UNLOCK_OTHER : UnlockFrame(box.Unlock.Kind); frame.a *= fade;
                    // an entry only another culture gets: an amber edge, and its picture faint
                    Color boxEdge = box.Cultural ? new Color(AND_MARK.r, AND_MARK.g, AND_MARK.b, 0.9f * fade) : new Color(1f, 1f, 1f, 0.3f * fade);
                    GUI.color = Color.white;
                    GUI.DrawTexture(box.Rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, frame, 0, 3f * _zoom);
                    GUI.DrawTexture(box.Rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, boxEdge, 1f, 3f * _zoom);
                    GUI.color = new Color(1f, 1f, 1f, fade * (box.Cultural ? 0.55f : 1f));
                    float inset = 2f * _zoom;
                    var inner = new Rect(box.Rect.x + inset, box.Rect.y + inset, box.Rect.width - 2 * inset, box.Rect.height - 2 * inset);
                    if (box.Unlock == null) GUI.Label(box.Rect, "+" + box.More, unknownStyle);
                    else if (TryIcon(box.Unlock, out var icon, out float iconAspect)) GUI.DrawTexture(FitInside(inner, iconAspect), icon, ScaleMode.StretchToFill, true);
                    else GUI.Label(box.Rect, "?", unknownStyle);   // an unlock without an icon is still an unlock
                }
            }
            GUI.color = Color.white;
        }

        // the artworks, on top of every card
        foreach (var draw in _artDraws)
        {
            GUI.color = new Color(1f, 1f, 1f, draw.Fade);
            GUI.DrawTextureWithTexCoords(draw.Rect, draw.Texture, draw.Uv, true);
        }
        GUI.color = Color.white;
        // the selected card's gold edge once more, over them: an artwork of a neighbouring card covered it (review B)
        if (_selected != null && _byName != null && _byName.ContainsKey(_selected.Name))
        {
            Rect sel = NodeRect(_selected);
            if (sel.Overlaps(local))
                GUI.DrawTexture(sel, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, GOLD, 2f, 5f * _zoom);
        }

        _pan = panSave;
        GUI.EndClip();
        AfterCanvasDrawn?.Invoke(this);
    }

    /// <summary>Called when a repaint has drawn the canvas (for the drill, which reads the window's pixels there).</summary>
    internal static Action<TechTreeWindow> AfterCanvasDrawn;

    struct ArtDraw { public Rect Rect; public Texture Texture; public Rect Uv; public float Fade; public TechTreeData.Node Node; }
    readonly List<ArtDraw> _artDraws = new List<ArtDraw>();
    Vector2 _artOrigin;   // where the canvas's clip started in the window when the artworks were drawn (NodeAt)
    /// <summary>The title as a card shows it: upper case, as in the game, from the STAGED title.</summary>
    internal string CardTitle(TechTreeData.Node n) => EffLabel(n).ToUpperInvariant();

    /// <summary>The era and tier as a card shows them ("E5 · T3"), from the STAGED era.</summary>
    internal string CardEraTier(TechTreeData.Node n)
    {
        string era = EffEra(n);
        return ShortEra(era) + (string.IsNullOrEmpty(era) || string.IsNullOrEmpty(n.Tier) ? "" : " · ") + ShortTier(n.Tier);
    }

    // ── Era separators ────────────────────────────────────────────────────────
    /// <summary>One separator of the tech screen: between the era of that index and the next, at X in the game's units.</summary>
    internal struct EraSeparator { public int LeftEra; public float X; }

    /// <summary>
    /// Where the game puts its era separators (TechnologyScreen.DoBindAll and BindAllSeparators). Per era index: the
    /// left edge of its leftmost card and the right edge of its rightmost, over the cards the screen SHOWS. Between
    /// two consecutive indices that both have a card: one separator, halfway between the right end of the earlier
    /// era and the left end of the later one. Where the eras overlap that is inside the overlap, as in the game.
    /// `cards` = era index and left edge (TechTreeX * 74) of each shown card; an index outside 0..eraCount-1 is no era.
    /// </summary>
    internal static List<EraSeparator> EraSeparators(IEnumerable<KeyValuePair<int, float>> cards, int eraCount)
    {
        var separators = new List<EraSeparator>();
        if (eraCount < 2) return separators;
        var min = new float[eraCount]; var max = new float[eraCount]; var count = new int[eraCount];
        for (int i = 0; i < eraCount; i++) { min[i] = float.MaxValue; max[i] = float.MinValue; }
        foreach (var card in cards)
        {
            int era = card.Key;
            if (era < 0 || era >= eraCount) continue;
            min[era] = Mathf.Min(min[era], card.Value);
            max[era] = Mathf.Max(max[era], card.Value + NODE_W);
            count[era]++;
        }
        for (int k = 0; k < eraCount - 1; k++)
            if (count[k] > 0 && count[k + 1] > 0)
                separators.Add(new EraSeparator { LeftEra = k, X = (max[k] + min[k + 1]) * 0.5f });
        return separators;
    }

    /// <summary>The game's FormatUtils.ToRoman: "" for 0 (the era before the first has no numeral).</summary>
    internal static string ToRoman(int number)
    {
        if (number <= 0 || number > 3999) return "";
        int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        string[] signs = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < values.Length; i++)
            while (number >= values[i]) { sb.Append(signs[i]); number -= values[i]; }
        return sb.ToString();
    }

    /// <summary>A point of a separator's line at height `y`: through (x + ERA_LINE_SHIFT, ERA_LABEL_Y), leaning right towards the top.</summary>
    internal static Vector2 EraLinePoint(float x, float y) => new Vector2(x + ERA_LINE_SHIFT + (ERA_LABEL_Y - y) * ERA_SLANT, y);

    /// <summary>
    /// The separators as the window would draw them now: from the cards the game's tech screen shows (a technology
    /// that is on the screen and has a UI mapper), at their staged eras and columns.
    /// </summary>
    internal List<EraSeparator> EraSeparatorsNow()
    {
        var cards = new List<KeyValuePair<int, float>>();
        int eraCount = 0;
        foreach (var era in _eras) eraCount = Mathf.Max(eraCount, era.Index + 1);
        foreach (var n in _nodes)
        {
            if (Hidden(n) || n.ActiveMapper == null) continue;   // the game binds no item for these
            cards.Add(new KeyValuePair<int, float>(EraIndexOf(_eras, EffEra(n)), EffX(n) * COL));
        }
        return EraSeparators(cards, eraCount);
    }

    /// <summary>What the last repaint drew of the era separators, in the window's coordinates (for the drill).</summary>
    internal sealed class SeparatorDraw
    {
        public int LeftEra;
        public Vector2 LineTop, LineBottom;         // the whole line's ends, drawn or not
        public int PiecesDrawn; public float MaxAlpha, MinAlpha = 1f, Width;
        public string LeftText, RightText; public Vector2 LeftLabel, RightLabel;
        public int FontSize; public float LabelAlpha;
        public int CardsDrawnBefore;                // how many cards were already drawn: 0 = under the cards
    }
    internal readonly List<SeparatorDraw> SeparatorDraws = new List<SeparatorDraw>();
    int _cardsDrawn;

    void DrawEraSeparators(Rect local)
    {
        SeparatorDraws.Clear();
        if (_eras.Count < 2) return;
        var separators = EraSeparatorsNow();
        if (separators.Count == 0) return;
        Vector2 origin = new Vector2(_artOrigin.x, _artOrigin.y);   // the clip's place in the window
        float width = Mathf.Clamp(1.5f * _zoom, 1f, 2.5f);
        // the numerals: the game's size where that is ERA_LABEL_MIN or more, and the game's box and gap at that size
        int fontSize = Mathf.Clamp(Mathf.RoundToInt(ERA_LABEL_SIZE * _zoom), ERA_LABEL_MIN, 255);
        float scale = fontSize / ERA_LABEL_SIZE;
        float faint = Mathf.Lerp(ERA_NUMBER_SMALL_ALPHA, ERA_NUMBER.a, Mathf.InverseLerp(ERA_LABEL_MIN, ERA_LABEL_SIZE, fontSize));
        var numberStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = fontSize, wordWrap = false, clipping = TextClipping.Overflow,
                                                             normal = { textColor = new Color(ERA_NUMBER.r, ERA_NUMBER.g, ERA_NUMBER.b, faint) } };
        Vector2 half = new Vector2(75f, 65f) * scale;   // the game's label box is 150 x 130 at size 100
        float offset = ERA_LABEL_OFFSET * scale;
        // the height: at the view's top, but never past the line's ends (in the game's units, then back on screen)
        float labelUnitY = Mathf.Clamp((local.yMin + ERA_LABEL_TOP_GAP + half.y - _pan.y) / _zoom, ERA_LINE_TOP, ERA_LINE_BOTTOM);
        const int pieces = 20;
        foreach (var separator in separators)
        {
            var record = new SeparatorDraw { LeftEra = separator.LeftEra, CardsDrawnBefore = _cardsDrawn };
            Vector2 top = EraLinePoint(separator.X, ERA_LINE_TOP), bottom = EraLinePoint(separator.X, ERA_LINE_BOTTOM);
            record.LineTop = U2S(top.x, top.y) + origin; record.LineBottom = U2S(bottom.x, bottom.y) + origin;
            SeparatorDraws.Add(record);
            // the line fades in over its first third and out over its last (the prefab's alpha keys: 0.3 and 0.7)
            for (int i = 0; i < pieces; i++)
            {
                float t0 = i / (float)pieces, t1 = (i + 1) / (float)pieces, t = (t0 + t1) * 0.5f;
                float alpha = t < 0.3f ? t / 0.3f : t > 0.7f ? (1f - t) / 0.3f : 1f;
                Vector2 a = EraLinePoint(separator.X, Mathf.Lerp(ERA_LINE_TOP, ERA_LINE_BOTTOM, t0)), b = EraLinePoint(separator.X, Mathf.Lerp(ERA_LINE_TOP, ERA_LINE_BOTTOM, t1));
                Vector2 sa = U2S(a.x, a.y), sb = U2S(b.x, b.y);
                if (Mathf.Max(sa.x, sb.x) < local.xMin || Mathf.Min(sa.x, sb.x) > local.xMax || sb.y < local.yMin || sa.y > local.yMax) continue;
                Handles.color = new Color(ERA_LINE.r, ERA_LINE.g, ERA_LINE.b, ERA_LINE.a * alpha);
                Handles.DrawAAPolyLine(width, sa, sb);
                record.PiecesDrawn++; record.MaxAlpha = Mathf.Max(record.MaxAlpha, Handles.color.a); record.MinAlpha = Mathf.Min(record.MinAlpha, Handles.color.a); record.Width = width;
            }
            Vector2 onLine = EraLinePoint(separator.X, labelUnitY), at = U2S(onLine.x, onLine.y);
            Vector2 left = new Vector2(at.x - offset, at.y), right = new Vector2(at.x + offset, at.y);
            string leftText = ToRoman(separator.LeftEra), rightText = ToRoman(separator.LeftEra + 1);
            GUI.Label(new Rect(left.x - half.x, left.y - half.y, 2 * half.x, 2 * half.y), leftText, numberStyle);
            GUI.Label(new Rect(right.x - half.x, right.y - half.y, 2 * half.x, 2 * half.y), rightText, numberStyle);
            record.LeftText = leftText; record.RightText = rightText; record.FontSize = fontSize; record.LabelAlpha = faint;
            record.LeftLabel = left + origin; record.RightLabel = right + origin;
        }
        Handles.color = Color.white;
    }

    // ── Artwork ───────────────────────────────────────────────────────────────
    readonly Dictionary<string, Texture> _art = new Dictionary<string, Texture>();   // asset path -> texture, loaded when first drawn

    // The card's image: the project's own file, else the game's own from its atlases. `uv` is the part of the
    // texture to draw, `aspect` the image's width over its height.
    bool TryArt(TechTreeData.Node n, out Texture tex, out Rect uv, out float aspect)
    {
        string guid = EffImage(n);
        if (guid == n.ArtGuid) return TryImage(n.ArtPath, n.ArtGuid, out tex, out uv, out aspect);
        string path = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);   // a staged image: a file of the project, or one of the game's own
        return TryImage(string.IsNullOrEmpty(path) ? null : path, guid, out tex, out uv, out aspect);
    }

    // One image of a UI mapper: the project's own file by its path, else the game's own by its GUID from the atlases.
    bool TryImage(string path, string guid, out Texture tex, out Rect uv, out float aspect)
    {
        tex = null; uv = new Rect(0, 0, 1, 1); aspect = 1f;
        if (!string.IsNullOrEmpty(path))
        {
            if (!_art.TryGetValue(path, out tex)) _art[path] = tex = AssetDatabase.LoadAssetAtPath<Texture>(path);
            if (tex != null) { aspect = tex.height > 0 ? tex.width / (float)tex.height : 1f; return true; }
        }
        if (TechTreeVanillaArt.TryRegion(guid, out var region))
        {
            tex = TechTreeVanillaArt.Atlas(region.Output);
            if (tex != null) { uv = region.Uv; aspect = region.Height > 0 ? region.Width / (float)region.Height : 1f; return true; }
        }
        return false;
    }

    // An unlock's icon: the project's own file by its path, else the game's own as a small texture of its own
    // (TechTreeVanillaArt.Icon: a white shape for a pictogram, a copy with mip levels for a colour icon).
    bool TryIcon(TechTreeUnlocks.Unlock u, out Texture tex, out float aspect)
    {
        tex = null; aspect = 1f;
        string path = u.ImagePath;
        if (!string.IsNullOrEmpty(path))
        {
            if (!_art.TryGetValue(path, out tex)) _art[path] = tex = AssetDatabase.LoadAssetAtPath<Texture>(path);
        }
        if (tex == null) tex = TechTreeVanillaArt.Icon(u.ImageGuid);
        if (tex == null) return false;
        aspect = tex.height > 0 ? tex.width / (float)tex.height : 1f;
        return true;
    }

    /// <summary>One box of a card's unlock row, as it is decided before anything is drawn.</summary>
    internal struct UnlockBox
    {
        public Rect Rect;
        public TechTreeUnlocks.Unlock Unlock;   // null for the "+N" box
        public int More;                        // the "+N" box: how many entries did not fit
        public bool Cultural;                   // an entry only other cultures get: drawn with the amber edge
    }

    /// <summary>
    /// A card's unlock row: the entries the game draws an icon for, in Read's order, in the rectangles that fit the
    /// card (six on the game's card). When there are more, the last rectangle says how many are left out.
    /// </summary>
    internal static List<UnlockBox> PlanUnlockRow(Rect card, IList<TechTreeUnlocks.Unlock> unlocks)
    {
        var shown = new List<TechTreeUnlocks.Unlock>();
        foreach (var u in unlocks) if (u.NotShown == null) shown.Add(u);
        var rects = UnlockRects(card, shown.Count);
        var row = new List<UnlockBox>(rects.Count);
        for (int i = 0; i < rects.Count; i++)
        {
            bool last = i == rects.Count - 1 && shown.Count > rects.Count;
            row.Add(last ? new UnlockBox { Rect = rects[i], More = shown.Count - rects.Count + 1 }
                         : new UnlockBox { Rect = rects[i], Unlock = shown[i], Cultural = shown[i].OnlyFor != null });
        }
        return row;
    }

    /// <summary>
    /// Where the icons of a card's unlock row go: squares along the card's bottom from its left, as many of `count` as
    /// fit before the card's right edge (six on the game's 310-wide card).
    /// </summary>
    internal static List<Rect> UnlockRects(Rect card, int count)
    {
        var rects = new List<Rect>();
        float s = card.width / NODE_W, side = UNLOCK_SIDE * s, x = card.x + UNLOCK_LEFT * s, y = card.yMax - (UNLOCK_BOTTOM + UNLOCK_SIDE) * s;
        for (int i = 0; i < count && x + side <= card.xMax - 2f * s; i++, x += side + UNLOCK_GAP * s)
            rects.Add(new Rect(x, y, side, side));
        return rects;
    }

    static Color UnlockFrame(TechTreeUnlocks.Kind kind) =>
        kind == TechTreeUnlocks.Kind.Unit ? UNLOCK_UNIT : kind == TechTreeUnlocks.Kind.Extension ? UNLOCK_EXTENSION
        : kind == TechTreeUnlocks.Kind.Infrastructure ? UNLOCK_INFRASTRUCTURE : UNLOCK_OTHER;

    /// <summary>What kind of thing an unlock is, in a word, for the side strip's list.</summary>
    internal static string UnlockWord(TechTreeUnlocks.Unlock u)
    {
        switch (u.Effect)
        {
            case "UnlockConstructible": return u.Kind == TechTreeUnlocks.Kind.Unit ? "Unit" : u.Kind == TechTreeUnlocks.Kind.Extension ? "District" : "Infrastructure";
            case "UnlockSettlementStartingPackage": return "Starting package";
            case "ApplyDescriptor": return "Bonus";
            case "AddEmpireBattleAbility": return "Battle ability";
            case "UnlockResource": return "Resource";
            case "ApplyCostModifier": return "Cost";
            case "UnlockAction": return "Action";
            case "ProhibitAction": return "Prohibits";
            case "AddFame": return "Fame";
            case "ResetAllCivics": return "Civics";
            case "UpgradeAllRoads": return "Roads";
            default: return u.Effect;
        }
    }

    /// <summary>The heading of the side strip's list: what a card shows, and what it does not.</summary>
    internal static string UnlockHeader(IList<TechTreeUnlocks.Unlock> unlocks)
    {
        int row = 0, cultural = 0, none = 0;
        foreach (var u in unlocks) { if (u.NotShown != null) none++; else if (u.OnlyFor != null) cultural++; else row++; }
        string head = "Unlocks (" + row + ")";
        if (cultural > 0) head += " + " + cultural + " for other cultures";
        if (none > 0) head += " + " + none + " without an icon in the game";
        return head + ":";
    }

    /// <summary>A culture's name as the side strip writes it: without the data's "Civilization_" prefix.</summary>
    internal static string CultureWord(string culture) =>
        culture != null && culture.StartsWith("Civilization_", StringComparison.Ordinal) ? culture.Substring("Civilization_".Length) : (culture ?? "");

    static string Cultures(string[] cultures) => string.Join(", ", cultures.Select(CultureWord));

    /// <summary>
    /// The side strip's lines for one unlock: the entry, then one line per constructible other cultures get instead.
    /// `icon` = why no icon shows for an entry that should have one ("" when one shows, or when the entry has none
    /// to show by the game's own rule).
    /// </summary>
    internal static List<string> UnlockLines(TechTreeUnlocks.Unlock u, string icon)
    {
        var lines = new List<string>();
        string name = string.IsNullOrEmpty(u.Title) ? (u.ElementName != "" ? u.ElementName : "(no name)") : u.Title;
        string line = UnlockWord(u) + " · " + name;
        if (u.ElementName != "" && u.Title != u.ElementName) line += "  (" + u.ElementName + ")";
        if (u.OnlyFor != null) line += "  — ONLY for " + (u.OnlyFor.Length > 0 ? Cultures(u.OnlyFor) : "some cultures") + ": the game shows it to no other culture";
        if (u.Dlc != "") line += "  — needs " + u.Dlc;
        if (u.NotShown != null) line += "  — the game shows NO icon: " + u.NotShown;
        else if (!u.MapperFound) line += "  — no UI mapper '" + u.MapperName + "' in the project: a blank icon";
        else if (u.ImageGuid == null) line += "  — its UI mapper names no icon: a blank icon";
        else if (!string.IsNullOrEmpty(icon)) line += "  — icon not shown here: " + icon;
        lines.Add(line);
        foreach (var other in u.Others)
            lines.Add("      " + (other.Cultures.Length > 0 ? Cultures(other.Cultures) + " get" : "also named:") + " " + other.Title + (other.Title != other.Name ? "  (" + other.Name + ")" : ""));
        return lines;
    }

    /// <summary>The largest rectangle of that aspect (width over height) inside `box`, centred.</summary>
    internal static Rect FitInside(Rect box, float aspect)
    {
        if (aspect <= 0f) return box;
        float w = box.width, h = w / aspect;
        if (h > box.height) { h = box.height; w = h * aspect; }
        return new Rect(box.center.x - w * 0.5f, box.center.y - h * 0.5f, w, h);
    }

    /// <summary>
    /// Where a card's artwork goes, as the game's card prefab places its "Artwork": a 200 x 200 square on a 310 x 116
    /// card, from 200 right of the card's left edge and 60 above its top. So it stands 90 beyond the card's right
    /// edge, 60 above it and 24 below it.
    /// </summary>
    internal static Rect ArtRect(Rect card)
    {
        float s = card.width / NODE_W;
        return new Rect(card.x + 200f * s, card.y - 60f * s, 200f * s, 200f * s);
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
    /// <summary>The width the side strip's rows get: the strip without its frame and the room of a vertical scroll bar.</summary>
    internal static float SideContentWidth(float stripWidth) => Mathf.Max(60f, stripWidth - 28f);
    void DrawSide(Rect side)
    {
        GUILayout.BeginArea(side, EditorStyles.helpBox);
        // The strip scrolls up and down only, and its content is laid out to the strip's own width. Before, one row
        // that asked for more (the Shift button's text) gave the strip a horizontal bar, and scrolled along it the
        // left end of every row was out of sight (the user's screenshot, 2026-11-22).
        _sideScroll.x = 0;
        _sideScroll = EditorGUILayout.BeginScrollView(_sideScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
        EditorGUILayout.BeginVertical(GUILayout.Width(SideContentWidth(side.width)));

        if (_selected == null)
            EditorGUILayout.LabelField("Click a node to inspect.", EditorStyles.wordWrappedMiniLabel);
        else
        {
            var n = _selected;
            EditorGUILayout.LabelField(EffLabel(n), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(n.Name, EditorStyles.miniLabel);

            // state line
            string state = n.AnyModded ? "Modded" : "Vanilla";
            if (IsEdited(n)) state += " · edited *";
            string detail = $"def:{(n.DefModded ? "mod" : "vanilla")}  map:{(n.MapperModded ? "mod" : "vanilla")}";
            EditorGUILayout.LabelField($"{state}   ({detail})", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(string.Join(" · ", new[] { EffEra(n), n.Tier }.Where(t => !string.IsNullOrEmpty(t))) + (string.IsNullOrEmpty(EffEra(n)) && string.IsNullOrEmpty(n.Tier) ? "" : " · ") + $"({EffX(n)},{EffY(n)})", EditorStyles.miniLabel);
            TechTreeVanillaArt.Region shown = default(TechTreeVanillaArt.Region);
            bool gameImage = string.IsNullOrEmpty(n.ArtPath) && TechTreeVanillaArt.TryRegion(n.ArtGuid, out shown);
            string artProblem = TechTreeVanillaArt.Problem;
            if (gameImage && TechTreeVanillaArt.Atlas(shown.Output) == null)
            { gameImage = false; artProblem = "the atlas " + shown.Output + " did not load (Reload tries again)"; }
            if (EffImage(n) == n.ArtGuid)
                EditorGUILayout.LabelField(ArtLine(n, gameImage ? shown.Output : null, artProblem), EditorStyles.wordWrappedMiniLabel);

            if (!_editMode)
                EditorGUILayout.HelpBox("Edit is OFF, so the title, description, image, position, era, game switch, rule and prerequisites are greyed out. Switch on \"Edit\" at the top left to change them.", MessageType.Info);

            // the card's title, description and image, staged like the rest and saved into the UI mapper
            using (new EditorGUI.DisabledScope(!_editMode))
                DrawCardTexts(n, SideContentWidth(side.width));

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

                // everything right of this card, moved as one: to make room for new technologies, or to close a gap
                int rightOf = _nodes.Count(o => o != n && EffX(o) > EffX(n));
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("Shift", "Moves EVERY card whose column is right of this card's by that many columns (negative = to the left). This card and the cards in its own column stay. Staged like a drag: Save writes it, the opposite number takes it back."), GUILayout.Width(54));
                _shiftBy = EditorGUILayout.IntField(_shiftBy, GUILayout.Width(46));
                GUILayout.Label("columns", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
                // the button on a row of its own: its text is as long as the strip is wide
                using (new EditorGUI.DisabledScope(rightOf == 0 || _shiftBy == 0))
                    if (GUILayout.Button(ShiftLabel(rightOf, _shiftBy), EditorStyles.miniButton))
                    {
                        int asked = _shiftBy;
                        int moved = ShiftRightOf(n, asked, out int applied);
                        _shiftNote = ShiftNote(moved, asked, applied); _shiftNoteFor = n.Name;
                        GUIUtility.keyboardControl = 0;
                    }
                if (_shiftNote != "" && _shiftNoteFor == n.Name) EditorGUILayout.LabelField(_shiftNote, EditorStyles.wordWrappedMiniLabel);

                // the era (TechnologyDefinition.EraReference), staged like the rest
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("Era", "The technology's era (EraReference in its definition). In the game it decides WHEN an empire can research it (not before the empire's technological era reaches it; an era-exclusive technology also not after), what it COSTS (the era's cost for its tier, unless the technology has a cost of its own), and which side of the era separators its card is on. The technologies of the eras before the one a game starts in are completed at the start. Save writes it to the definition."), GUILayout.Width(54));
                string[] eraNames = EraChoices(_eras, EffEra(n), n.Era, out string[] eraLabels, out int eraAt);
                using (new EditorGUI.DisabledScope(_eras.Count == 0))
                {
                    string eraNow = PickedEra(eraNames, eraAt, EditorGUILayout.Popup(eraAt, eraLabels));
                    if (eraNow != null) SetEra(n, eraNow);
                }
                EditorGUILayout.EndHorizontal();

                // in the game or switched off (TechnologyDefinition.Visibility), staged like the rest
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("Game", "Whether the game has this technology at all. Switched off = Visibility Never: no empire gets it."), GUILayout.Width(54));
                int stateNow = EffVisibility(n) != TechTreeData.VisibilityNever ? 0 : 1;
                int statePicked = GUILayout.Toolbar(stateNow, VisibilityChoices, EditorStyles.miniButton);
                EditorGUILayout.EndHorizontal();
                if (statePicked != stateNow) SetVisibility(n, SwitchedVisibility(n.BaseVisibility, statePicked == 0));
            }
            string eraNote = EraNote(_eras, EffEra(n));
            if (eraNote != "") EditorGUILayout.HelpBox(eraNote, MessageType.Warning);
            string costNote = EraCostNote(_eras, EffEra(n), n.TierIndex, n.Tier, n.CostOverride);
            if (costNote != "") EditorGUILayout.HelpBox(costNote, MessageType.Warning);
            if (n.ExclusiveToEra)
                EditorGUILayout.HelpBox(ExclusiveNote(EffEra(n)), MessageType.Info);
            string visibilityNote = SideVisibilityNote(out bool othersDepend);
            if (visibilityNote != "") EditorGUILayout.HelpBox(visibilityNote, othersDepend ? MessageType.Warning : MessageType.Info);

            EditorGUILayout.Space(6);
            bool all = EffRequireAll(n);
            var prereqs = EffPrereqs(n);
            EditorGUILayout.LabelField(SideHeader(), EditorStyles.boldLabel);
            if (_rulesUnreadable)
                EditorGUILayout.HelpBox("The Tech Requires All list could not be read, so the rule (OR / AND) is not shown and cannot be switched here. " +
                                        "Tools ▸ HAF ▸ Tech Tree ▸ Requires All Prerequisites says why; Reload (top left) reads it again.", MessageType.Warning);
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
                if (_rulesStale)
                    EditorGUILayout.HelpBox("The list changed outside the editor and the game still reads an older copy of it. Tools ▸ HAF ▸ Tech Tree ▸ Requires All Prerequisites can deploy it.", MessageType.Warning);
            }
            if (prereqs.Count == 0)
                EditorGUILayout.LabelField("  (none — root)", EditorStyles.miniLabel);
            else
                foreach (var pr in prereqs.ToList())
                {
                    EditorGUILayout.BeginHorizontal();
                    string label = _byName.TryGetValue(pr, out var s) ? EffLabel(s) : pr;
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

            // what the technology unlocks, as its card's icon row shows it (read from the definition's effects; not edited here)
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(UnlockHeader(n.Unlocks), EditorStyles.boldLabel);
            if (n.Unlocks.Count == 0)
                EditorGUILayout.LabelField("  (nothing that shows on its card)", EditorStyles.miniLabel);
            else
                foreach (var u in n.Unlocks)
                {
                    // why an entry that should have an icon shows "?" here (the game's bundle not readable, an atlas not loaded)
                    string iconProblem = u.NotShown == null && u.MapperFound && u.ImageGuid != null && !TryIcon(u, out _, out _)
                        ? (TechTreeVanillaArt.Problem != "" ? TechTreeVanillaArt.Problem : "the image is neither a file of this project nor in the game's atlases (Reload tries again)") : "";
                    foreach (string line in UnlockLines(u, iconProblem))
                        EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
                }

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Select asset"))
            { Selection.activeObject = n.Asset; EditorGUIUtility.PingObject(n.Asset); }
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    // what the side strip says about the card's image, so a card without artwork is explained
    // `gameAtlas` = the game's atlas texture that holds the image (null when it is not one of the game's);
    // `gameProblem` = why the game's images cannot be read ("" when they can).
    internal static string ArtLine(TechTreeData.Node n, string gameAtlas, string gameProblem) =>
        !string.IsNullOrEmpty(n.ArtPath) ? "Image: " + System.IO.Path.GetFileName(n.ArtPath)
        : !n.ArtNamed ? "Image: none set"
        : !string.IsNullOrEmpty(gameAtlas) ? "Image: one of the game's own (in " + gameAtlas + ")"
        : !string.IsNullOrEmpty(gameProblem) ? "Image: not in this project, and the game's own cannot be read: " + gameProblem
        : "Image: not found, neither in this project nor among the game's own";

    // What the side strip shows for the selected technology, as the window's state makes it. Separate from the
    // wording functions below so that a check can hold the WIRING too (which state goes into which text).
    internal string SideHeader() => RuleHeader(_rulesUnreadable, _selected != null && EffRequireAll(_selected));

    internal string SideVisibilityNote(out bool othersDepend)
    {
        othersDepend = false;
        if (_selected == null) return "";
        var n = _selected;
        int visibility = EffVisibility(n);
        List<string> dependents = null;
        if (visibility == TechTreeData.VisibilityNever)
            dependents = _nodes.Where(o => o != n && EffPrereqs(o).Contains(n.Name)).Select(o => o.Label ?? o.Name).OrderBy(t => t, StringComparer.Ordinal).ToList();
        othersDepend = dependents != null && dependents.Count > 0;
        return VisibilityNote(n.Label, visibility, dependents);
    }

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

    /// <summary>
    /// What the side strip says about a technology's Visibility; "" for the usual case (Always). `dependents` = the
    /// titles of the technologies that name it as a prerequisite (asked for a switched-off one only).
    /// </summary>
    internal static string VisibilityNote(string title, int visibility, IList<string> dependents)
    {
        if (visibility == TechTreeData.VisibilityAlways) return "";
        if (visibility == TechTreeData.VisibilityNever)
        {
            string note = title + " is SWITCHED OFF (Visibility = Never): the game does not create it for any empire. It is not on the tech screen and, by the game's own rules, cannot be researched, traded or granted.";
            int n = dependents?.Count ?? 0;
            if (n > 0)
                note += " " + n + " technolog" + (n == 1 ? "y names" : "ies name") + " it as a prerequisite (" + string.Join(", ", dependents.Take(4)) + (n > 4 ? ", ..." : "")
                      + "): one that names no other can never unlock.";
            return note;
        }
        return TechTreeData.OnTechScreen(visibility)
            ? title + " is in the game and on its tech screen, with Visibility = " + TechTreeData.VisibilityName(visibility) + " (not Always)."
            : title + " is in the game but has NO card on its tech screen (Visibility = " + TechTreeData.VisibilityName(visibility) + ").";
    }

    /// <summary>
    /// The Visibility the switch asks for. "In the game" keeps a value that already is in the game (TechnologyScreen,
    /// ControlBannerTechnologyPopup, ...) and only turns Never into Always; "Switched off" is Never. So switching
    /// there and back is no change, whatever the value was.
    /// </summary>
    internal static int SwitchedVisibility(int baseVisibility, bool inGame) =>
        !inGame ? TechTreeData.VisibilityNever
        : baseVisibility != TechTreeData.VisibilityNever ? baseVisibility : TechTreeData.VisibilityAlways;

    // ── The card's title, description and image ──────────────────────────────
    GUIStyle _descriptionStyle;
    string _imageRefusal = "";                              // why the last picked picture was refused
    List<List<TechTreeData.BundleFilter>> _bundleFilters;   // the project's bundle filters, read when first needed after a Reload

    void DrawCardTexts(TechTreeData.Node n, float width)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent("Title", "The technology's name as the game shows it, on its card and everywhere else. Save writes it into the technology's UI mapper (Title), the same in every language."), GUILayout.Width(54));
        string title = EditorGUILayout.TextField(TitleShown(n));
        EditorGUILayout.EndHorizontal();
        if (title != TitleShown(n)) SetTitle(n, title);

        GUILayout.Label(new GUIContent("Description", "The text the game shows in the technology's tooltip. Save writes it into the technology's UI mapper (Description), the same in every language."));
        if (_descriptionStyle == null) _descriptionStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
        // as high as its text (the real descriptions are 3 to 6 lines at this width), within 3 and 16 lines
        string shownDescription = DescriptionShown(n);
        float lines = _descriptionStyle.lineHeight > 0 ? _descriptionStyle.lineHeight : 13f;
        float descriptionHeight = Mathf.Clamp(_descriptionStyle.CalcHeight(new GUIContent(shownDescription), width - 4f) + lines, 3f * lines + 6f, 16f * lines + 6f);   // + one line: the measure ran one short in the strip
        string description = EditorGUILayout.TextArea(shownDescription, _descriptionStyle, GUILayout.Height(descriptionHeight));
        if (description != shownDescription) SetDescription(n, description);

        _pending.TryGetValue(n.Name, out var staged);
        string textNote = TextNote(n.TitleKey, staged?.Title, n.BaseDescription, staged?.Description, out bool textWarning);
        if (textNote != "") EditorGUILayout.HelpBox(textNote, textWarning ? MessageType.Warning : MessageType.Info);

        // the image: a picture of the project, or the image of another technology (the game's own ones too)
        string image = EffImage(n);
        string imagePath = string.IsNullOrEmpty(image) ? "" : AssetDatabase.GUIDToAssetPath(image);
        var current = string.IsNullOrEmpty(imagePath) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(imagePath);
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent("Image", "The technology's picture: on its card and in the popup when it is researched (both image entries of its UI mapper; they are the same image in all of ENC's technologies). Pick a picture of the project, or take the image of another technology, which can be one of the game's own."), GUILayout.Width(54));
        var picked = (Texture2D)EditorGUILayout.ObjectField(current, typeof(Texture2D), false);
        if (GUILayout.Button(new GUIContent("Same as…", "The image of another technology: one of the project's pictures or one of the game's own."), EditorStyles.miniButton, GUILayout.Width(68)))
        {
            var others = _nodes.Where(o => o != n && !string.IsNullOrEmpty(EffImage(o))).ToList();
            new TechPickerDropdown(new AdvancedDropdownState(), others, name => { if (_byName.TryGetValue(name, out var other)) SetImage(n, EffImage(other)); }, "Image of").Show(GUILayoutUtility.GetLastRect());
        }
        bool imageStaged = _pending.TryGetValue(n.Name, out var pi) && pi.Image != null;
        // back to the saved image: the only way back to one of the game's own, which neither the field nor
        // "Same as…" can offer when no other technology has it (review B: 94 of ENC's technologies)
        using (new EditorGUI.DisabledScope(!imageStaged))
            if (GUILayout.Button(new GUIContent("Own", "Back to the technology's saved image."), EditorStyles.miniButton, GUILayout.Width(40))) ClearImage(n);
        EditorGUILayout.EndHorizontal();
        if (picked == null && current != null && imageStaged) ClearImage(n);   // "None" in the field: back to the saved image
        else if (picked != null && picked != current)
        {
            string refusal = ImageRefusal(picked);
            if (refusal == null) { SetImage(n, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(picked))); _imageRefusal = ""; }
            else _imageRefusal = refusal;
        }
        if (_imageRefusal != "") EditorGUILayout.HelpBox(_imageRefusal, MessageType.Warning);
        if (_bundleFilters == null) _bundleFilters = TechTreeData.ModBundleFilters();
        string imageNote = ImageNote(image, imagePath, image != n.ArtGuid, TechTreeData.InModBundle(imagePath, _bundleFilters), TechTreeData.BundleTextureFolders(_bundleFilters), CommonImageFolder(), out bool imageWarning);
        if (imageNote != "")
        {
            if (imageWarning) EditorGUILayout.HelpBox(imageNote, MessageType.Warning);
            else EditorGUILayout.LabelField(imageNote, EditorStyles.wordWrappedMiniLabel);
        }
    }

    /// <summary>
    /// What the side strip says about the texts. 88 of ENC's titles and 86 of its descriptions are localization keys
    /// ("%..."), shown in the fields translated; a text typed there replaces the key, and the game then shows that
    /// text in every language. A typed text that starts with "%" is taken by the game as a key (TextUtils.Localize
    /// looks at the first character only): said, and a warning when the project knows no such key. `stagedTitle`,
    /// `stagedDescription`: null when not changed. `warning` = something the game would show wrongly.
    /// </summary>
    internal static string TextNote(string baseTitle, string stagedTitle, string baseDescription, string stagedDescription, out bool warning)
    {
        warning = false;
        var notes = new List<string>();
        if (stagedTitle != null && stagedTitle.Trim() == "") { notes.Add("The title is empty: the game would show no name."); warning = true; }
        foreach (var (what, saved, staged) in new[] { ("title", baseTitle, stagedTitle), ("description", baseDescription, stagedDescription) })
        {
            if (staged != null && staged.StartsWith("%", StringComparison.Ordinal))
            {
                if (TechTreeData.TryLocalize(staged, out string shown))
                    notes.Add("The " + what + " starts with %, so the game takes it as the localization key " + staged + " (\"" + shown + "\").");
                else
                {
                    notes.Add("The " + what + " starts with %, so the game takes it as a localization key, and the project knows no key " + staged + ": the game would show the key itself and log an error (with a { or } in it, the screen can fail).");
                    warning = true;
                }
            }
            else if (saved != null && saved.StartsWith("%", StringComparison.Ordinal))
                notes.Add(staged == null
                    ? "The " + what + " is the localization key " + saved + ", shown here translated. Typing replaces the key: the game then shows the typed text in every language."
                    : "This replaces the localization key " + saved + " with the typed " + what + ", the same in every language (the game's translations of it are no longer used).");
        }
        return string.Join(" ", notes);
    }

    /// <summary>Why a picked object cannot be a technology's image; null when it can (a picture file of its own).</summary>
    internal static string ImageRefusal(UnityEngine.Object picked)
    {
        if (picked == null) return null;
        string path = AssetDatabase.GetAssetPath(picked);
        if (string.IsNullOrEmpty(path)) return "That texture is not a file of the project.";
        if (AssetDatabase.IsSubAsset(picked) || AssetDatabase.GetMainAssetTypeAtPath(path) != typeof(Texture2D))
            return "That texture is part of another file (" + System.IO.Path.GetFileName(path) + "): the game finds an image by its file, so pick a picture file.";
        return null;
    }

    /// <summary>
    /// What the side strip says about the image. `changed` = it is a staged one. `commonFolder` = where most of the
    /// project's technology pictures are, said when a picked picture is elsewhere ("" to say nothing).
    /// </summary>
    internal static string ImageNote(string guid, string path, bool changed, bool? inBundle, string bundleFolders, string commonFolder, out bool warning)
    {
        warning = false;
        if (string.IsNullOrEmpty(guid)) return "No image is set.";
        string what = !string.IsNullOrEmpty(path) ? System.IO.Path.GetFileName(path) : "one of the game's own images";
        string note = changed ? "New image (staged): " + what + "." : "";
        if (string.IsNullOrEmpty(path)) return note;
        if (inBundle == false)
        {
            warning = true;
            return (note == "" ? "" : note + " ") + "This picture is not in the mod's asset bundle: the project's build settings take pictures only from "
                   + (bundleFolders != "" ? bundleFolders : "no folder") + ". The game would show a placeholder; move the picture there first.";
        }
        if (inBundle == null && !string.IsNullOrEmpty(commonFolder) && System.IO.Path.GetDirectoryName(path).Replace('\\', '/') != commonFolder)
            note += (note == "" ? "" : " ") + "The project's other technology pictures are in " + commonFolder + ".";
        return note;
    }

    // where most of the technologies' pictures that are files of the project are ("" for none)
    string CommonImageFolder()
    {
        var folders = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in _nodes)
            if (!string.IsNullOrEmpty(o.ArtPath))
            {
                string folder = System.IO.Path.GetDirectoryName(o.ArtPath).Replace('\\', '/');
                folders[folder] = folders.TryGetValue(folder, out int k) ? k + 1 : 1;
            }
        return folders.Count == 0 ? "" : folders.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
    }

    // Windows line ends (a paste) are stored as the asset's text gives them back after a restart: one "\n"
    internal static string OneLineEnd(string text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    void SetTitle(TechTreeData.Node n, string title)
    {
        title = OneLineEnd(title);
        var p = Stage(n);
        // equal to the saved title as shown (a key's translation) or as stored (the key): no change, the key stays
        p.Title = title != (n.Label ?? "") && title != (n.TitleKey ?? "") ? title : null;
        DropIfClean(n);
        Repaint();
    }

    void SetDescription(TechTreeData.Node n, string description)
    {
        description = OneLineEnd(description);
        var p = Stage(n);
        p.Description = description != (n.Description ?? "") && description != (n.BaseDescription ?? "") ? description : null;
        DropIfClean(n);
        Repaint();
    }

    void ClearImage(TechTreeData.Node n)
    {
        if (!_pending.TryGetValue(n.Name, out var p)) return;
        p.Image = null;
        DropIfClean(n);
        Repaint();
    }

    void SetImage(TechTreeData.Node n, string unityGuid)
    {
        if (!TechTreeData.UnityGuidToAmplitude(unityGuid, out _, out _, out _, out _)) return;   // not an asset of the project or the game
        var p = Stage(n);
        p.Image = unityGuid != n.ArtGuid ? unityGuid : null;
        DropIfClean(n);
        Repaint();
    }

    // ── The era of a technology ───────────────────────────────────────────────
    /// <summary>
    /// The side strip's era choice: the project's eras by index, each as its Roman numeral and its name (the first
    /// era, index 0, has no numeral in the game). An era the technology has SAVED but the project does not (or none
    /// at all) is kept as the first entry, so it can be chosen again after another era was staged (review B: it went
    /// from the list, and only Revert, which drops every staged edit, took it back); the same for a staged one. With
    /// no eras read at all the one entry says so. Returns the names; `at` = the entry of `current`.
    /// </summary>
    internal static string[] EraChoices(IList<TechTreeData.Era> eras, string current, string saved, out string[] labels, out int at)
    {
        current = current ?? ""; saved = saved ?? "";
        var names = new List<string>(); var shown = new List<string>();
        foreach (var era in eras ?? new List<TechTreeData.Era>())
        {
            names.Add(era.Name);
            string numeral = ToRoman(era.Index);
            shown.Add(numeral != "" ? numeral + "  ·  " + era.Name : era.Name);
        }
        bool none = names.Count == 0;
        int extra = 0;
        foreach (string name in new[] { saved, current })
            if (!names.Contains(name))
            {
                names.Insert(extra, name);
                shown.Insert(extra, none ? (name == "" ? "(no eras found in this project)" : name + "  (no eras found in this project)")
                                         : name == "" ? "(no era)" : name + "  (not an era of this project)");
                extra++;
            }
        at = names.IndexOf(current);
        labels = shown.ToArray();
        return names.ToArray();
    }

    /// <summary>The era the popup's pick asks for; null when nothing changed or the pick is out of the list.</summary>
    internal static string PickedEra(string[] names, int at, int picked) =>
        picked != at && names != null && picked >= 0 && picked < names.Length ? names[picked] : null;

    /// <summary>What the side strip says about a technology's era; "" when it is one of the project's.</summary>
    internal static string EraNote(IList<TechTreeData.Era> eras, string era)
    {
        if (eras == null || eras.Count == 0) return "";   // no eras read at all: nothing to hold the name against
        if (string.IsNullOrEmpty(era)) return "This technology names no era. The game needs one: it reads the technology's cost and its place among the era separators from it.";
        return EraIndexOf(eras, era) >= 0 ? "" : "The era '" + era + "' is not one of this project's eras (" + string.Join(", ", eras.Select(e => e.Name)) + "). The game would not find it.";
    }

    /// <summary>
    /// The warning for a technology that would cost nothing in the game: its era names no cost for its tier and it
    /// has no cost of its own. The game then leaves the cost at zero (DepartmentOfScience.UpdateInitialCosts; the
    /// review of 2026-11-22). "" otherwise, also when the eras or the tier could not be read.
    /// </summary>
    internal static string EraCostNote(IList<TechTreeData.Era> eras, string era, int tierIndex, string tierName, string costOverride)
    {
        if (eras == null || tierIndex < 0 || !string.IsNullOrEmpty(costOverride)) return "";
        TechTreeData.Era found = null;
        foreach (var e in eras) if (e.Name == era) found = e;
        if (found == null || found.TierCosts == null) return "";
        if (tierIndex < found.TierCosts.Length && found.TierCosts[tierIndex] != "") return "";
        string tier = string.IsNullOrEmpty(tierName) ? "its tier" : tierName;
        var with = new List<string>();
        foreach (var e in eras)
            if (e.TierCosts != null && tierIndex < e.TierCosts.Length && e.TierCosts[tierIndex] != "") with.Add(e.Name);
        return era + " has no research cost for " + tier + ", and this technology has no cost of its own: in the game it would cost NOTHING."
             + (with.Count > 0 ? " Eras with a cost for " + tier + ": " + string.Join(", ", with) + "." : "");
    }

    /// <summary>What the side strip says about an era-exclusive technology (TechnologyDefinition.IsExclusiveToEra).</summary>
    internal static string ExclusiveNote(string era) =>
        "Exclusive to its era: an empire can research it only while the empire's technological era is no later than "
        + (string.IsNullOrEmpty(era) ? "the technology's era" : era) + ". Moving it to an earlier era shortens that time.";

    void SetEra(TechTreeData.Node n, string era)
    {
        var p = Stage(n);
        p.Era = era != (n.Era ?? "") ? era : null;   // equal to base = no change
        DropIfClean(n);
        Repaint();
    }

    void SetVisibility(TechTreeData.Node n, int visibility)
    {
        var p = Stage(n);
        p.Visibility = visibility != n.BaseVisibility ? (int?)visibility : null;   // equal to base = no change
        DropIfClean(n);
        Repaint();
    }

    // ── Shift: every card right of one card, moved together ───────────────────
    /// <summary>
    /// Moves every card whose column is right of `anchor`'s by `columns` (negative = left), each as a staged position
    /// like a drag's. The anchor and the cards in its own column stay. A move to the left stops where the nearest
    /// card would reach the anchor's column (ShiftLimit), so the cards stay right of the anchor and the opposite
    /// number always takes the move back. Returns how many cards are right of the anchor; `applied` = the columns
    /// they were moved by.
    /// </summary>
    internal int ShiftRightOf(TechTreeData.Node anchor, int columns, out int applied)
    {
        int anchorX = EffX(anchor);
        var right = _nodes.Where(o => o != anchor && EffX(o) > anchorX).ToList();   // decided before anything moves
        applied = ShiftLimit(anchorX, right.Select(EffX), columns);
        if (applied != 0)
            foreach (var o in right) SetPosition(o, EffX(o) + applied, EffY(o));
        return right.Count;
    }

    /// <summary>The columns a shift may move by: all of them to the right; to the left, up to the column after the anchor's.</summary>
    internal static int ShiftLimit(int anchorX, IEnumerable<int> rightColumns, int columns)
    {
        if (columns >= 0) return columns;
        int nearest = int.MaxValue;
        foreach (int x in rightColumns) nearest = Math.Min(nearest, x);
        if (nearest == int.MaxValue) return 0;
        return Math.Max(columns, -(nearest - anchorX - 1));
    }

    internal static string ShiftLabel(int cards, int columns) =>
        cards == 0 ? "No card is right of this one"
        : $"Move the {cards} card{(cards == 1 ? "" : "s")} right of this by {columns}";

    internal static string ShiftNote(int cards, int asked, int applied)
    {
        if (cards == 0) return "No card is right of this one.";
        string side = applied < 0 ? "left" : "right";
        int by = Math.Abs(applied);
        string what = applied == 0
            ? $"Nothing moved: the nearest of the {cards} card{(cards == 1 ? "" : "s")} right of this one is already in the next column."
            : $"Moved {cards} card{(cards == 1 ? "" : "s")} {by} column{(by == 1 ? "" : "s")} to the {side}. Staged: Save writes it, {(-applied).ToString("+0;-0")} takes it back.";
        if (applied != asked && applied != 0)
            what += $" ({asked} was asked: the nearest card stops in the column after this one.)";
        return what;
    }

    /// <summary>
    /// How low one coordinate may go: 0, or as low as it already is (its saved value, or where it stands now). A card
    /// that sits at a negative coordinate can be moved along it and typed back to it; nothing else goes below 0.
    /// </summary>
    internal static int FloorOf(int saved, int current) => Mathf.Min(0, Mathf.Min(saved, current));

    void SetPosition(TechTreeData.Node n, int x, int y)
    {
        // A floored field loses the focus, or it would go on showing the typed number.
        int floorX = FloorOf(n.BaseX, EffX(n)), floorY = FloorOf(n.BaseY, EffY(n));
        if (x < floorX) { x = floorX; _dropFocus = true; }
        if (y < floorY) { y = floorY; _dropFocus = true; }
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
        GUIUtility.keyboardControl = 0;
        // Each PART leaves the overlay when it is written, and only then: a skipped part stays staged, a written one
        // does not (it stayed staged with a skipped one, and was written again by the next Save; review 2026-11-21).
        int pos = 0, pre = 0, vis = 0, eras = 0, texts = 0, images = 0, skipped = 0, absent = 0;
        foreach (var kv in _pending)
        {
            if (!_byName.TryGetValue(kv.Key, out var node)) { absent++; continue; }   // not in the tree right now: kept
            var p = kv.Value;

            // ONE writable mapper for all of its parts: the position, the texts, the image
            bool positionStaged = p.X.HasValue || p.Y.HasValue;
            if (positionStaged || p.Title != null || p.Description != null || p.Image != null)
            {
                var mapper = TechTreeData.EnsureWritable(node, true, _modPath);
                if (mapper == null) skipped += (positionStaged ? 1 : 0) + (p.Title != null ? 1 : 0) + (p.Description != null ? 1 : 0) + (p.Image != null ? 1 : 0);
                else
                {
                    // NOT registered with Undo, on purpose (review 2026-11-21, executed on the real ENC data). With it,
                    // a later Ctrl+Z — pressed to take back a staged drag, which Undo never held — took the SAVED
                    // positions back in memory while the file kept them, said nothing, and the next Save wrote the
                    // old values over the saved ones under "Saved: 1 position".
                    if (positionStaged)
                    {
                        TechTreeData.WritePosition(mapper, p.X ?? node.BaseX, p.Y ?? node.BaseY);
                        pos++; p.X = null; p.Y = null;
                    }
                    if (p.Title != null) { if (TechTreeData.WriteText(mapper, "Title", p.Title)) { texts++; p.Title = null; } else skipped++; }
                    if (p.Description != null) { if (TechTreeData.WriteText(mapper, "Description", p.Description)) { texts++; p.Description = null; } else skipped++; }
                    EditorUtility.SetDirty(mapper);
                    // the image LAST: it is written through the serialized form, which takes the object as the writes above left it
                    if (p.Image != null) { if (TechTreeData.WriteImage(mapper, TechTreeData.TechnologyImageKeys, p.Image)) { images++; p.Image = null; } else skipped++; }
                }
            }

            // ONE writable definition for all of its parts.
            if (p.Prereqs != null || p.Visibility.HasValue || p.Era != null)
            {
                var def = TechTreeData.EnsureWritable(node, false, _modPath);
                if (def == null) skipped += (p.Prereqs != null ? 1 : 0) + (p.Visibility.HasValue ? 1 : 0) + (p.Era != null ? 1 : 0);
                else
                {
                    if (p.Prereqs != null)
                    {
                        if (TechTreeData.WritePrereqs(def, p.Prereqs.ToArray())) { pre++; p.Prereqs = null; }
                        else skipped++;
                    }
                    if (p.Visibility.HasValue)
                    {
                        if (TechTreeData.WriteVisibility(def, p.Visibility.Value)) { vis++; p.Visibility = null; }
                        else skipped++;
                    }
                    // the era LAST: it is written through the serialized form, which takes the object as the writes
                    // above left it
                    if (p.Era != null)
                    {
                        if (TechTreeData.WriteEra(def, p.Era)) { eras++; p.Era = null; }
                        else skipped++;
                    }
                    EditorUtility.SetDirty(def);
                }
            }
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // The rule switches go to the Tech Requires All list in one write: each as an operation on the list as it is
        // now (add or remove that one name), so entries made in that list's own window meanwhile are kept.
        var rules = _pending.Where(kv => kv.Value.RequireAll.HasValue && _byName.ContainsKey(kv.Key))
                            .ToDictionary(kv => kv.Key, kv => kv.Value.RequireAll.Value);
        string ruleNote = "";
        var outcome = RegistryRules.SaveOutcome.Saved;
        if (rules.Count > 0)
        {
            outcome = TechRequireAllRegistry.SetRules(rules, out bool changed);
            ruleNote = RuleNote(rules.Count, outcome, changed, TechRequireAllRegistry.DeployPending);
            if (outcome == RegistryRules.SaveOutcome.Saved)
                foreach (var k in rules.Keys) _pending[k].RequireAll = null;
        }
        // The list's own window judges its rows by the prerequisites and shows the list: both may have changed. Also
        // after an unsettled write, which may have landed.
        if (pre > 0 || (rules.Count > 0 && outcome != RegistryRules.SaveOutcome.Refused)) TechRequireAllWindow.RefreshOpenWindows();

        Debug.Log($"[TechTree] Saved: {pos} position, {pre} prerequisite, {vis} visibility change(s)" + (eras > 0 ? $", {eras} era change{(eras == 1 ? "" : "s")}" : "")
                + (texts > 0 ? $", {texts} title / description change{(texts == 1 ? "" : "s")}" : "") + (images > 0 ? $", {images} image change{(images == 1 ? "" : "s")}" : "") + ruleNote
                + (skipped > 0 ? $" · {skipped} skipped (no writable target — see step 5; those edits are KEPT staged)" : "")
                + (absent > 0 ? $" · {absent} technolog{(absent == 1 ? "y" : "ies")} with staged edits {(absent == 1 ? "is" : "are")} not in the tree right now: those edits are KEPT staged (Revert discards them)" : "") + ".");
        foreach (var k in _pending.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList()) _pending.Remove(k);
        Reload(keepView: true, rules: rules.Count > 0);
    }

    /// <summary>The part of the Save line about the rule switches: what the outcome guarantees, and no more.</summary>
    internal static string RuleNote(int count, RegistryRules.SaveOutcome outcome, bool changed, bool deployPending)
    {
        string what = $"{count} rule switch{(count == 1 ? "" : "es")} (OR / AND)";
        if (outcome == RegistryRules.SaveOutcome.Refused)
            return $" · {what} REFUSED — nothing of {(count == 1 ? "it" : "them")} was written (the Console says why); KEPT staged";
        if (outcome != RegistryRules.SaveOutcome.Saved)
            return $" · {what} could NOT be confirmed — the list may or may not hold {(count == 1 ? "it" : "them")} (see the Console); kept staged unless the reload that follows finds {(count == 1 ? "it" : "them")} there";
        if (!changed)
            return $", {what}: the list already held {(count == 1 ? "it" : "them")}, so nothing was written or deployed";
        return (deployPending
            ? $", {what} — written to the project, but the game's {TechRequireAllRegistry.ArtifactName} is not refreshed yet (see the Console)"
            : $", {what} -> {TechRequireAllRegistry.ArtifactName}; relaunch the game to apply");
    }

    void RevertChanges()
    {
        if (_pending.Count == 0) return;
        int gone = _pending.Keys.Count(k => !_byName.ContainsKey(k));
        if (EditorUtility.DisplayDialog("Revert", $"Discard {_pending.Count} unsaved change(s)?" + (gone > 0 ? $"\n\n{gone} of them belong to technologies that are not in the tree right now." : ""), "Discard", "Cancel"))
        { _pending.Clear(); Reload(keepView: true, rules: false); }
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    static string ShortEra(string era)  => string.IsNullOrEmpty(era) ? "" : era.Replace("Era", "E");
    /// <summary>A technology's internal name as a card shows it: without the "Technology_" every one of them starts with.</summary>
    internal static string ShortName(string name) =>
        name != null && name.StartsWith("Technology_", StringComparison.Ordinal) ? name.Substring("Technology_".Length) : (name ?? "");
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