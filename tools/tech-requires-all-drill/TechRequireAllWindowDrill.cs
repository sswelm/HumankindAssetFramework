using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Drives the real Tech Requires All window in a temporary Unity project: add, save, reload, remove - and reads each
// deployed file back with the PLUGIN's own parser (the editor writes with JsonUtility, the plugin reads with
// Newtonsoft). The game's config folder is replaced by a temp folder through the registry's seam, never through
// EditorPrefs. The tech tree is injected: a temporary project holds no TechnologyDefinition, so the picker's own list
// (TechTreeData.Build over a real modding project) is NOT exercised here. From the viewer's first save on, the list
// window reads the tree through the viewer's seam (TechTreeWindow.BuildTreeImpl), at its next draw.
// The second half drives the Tech Tree viewer beside it: its OR / AND switch writes the same list, so the two windows
// are checked against each other. No button is clicked: the viewer's own staging and save methods are called, and its
// OnGUI is drawn between the steps (an exception in it fails the drill).
public static class TechRequireAllWindowDrill
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static string output, config;
    static readonly System.Text.StringBuilder log = new System.Text.StringBuilder();
    static readonly List<string> errors = new List<string>();
    static int failed, step, draws;
    static double deadline;
    static TechRequireAllWindow window;
    static TechTreeWindow viewer;
    static Vector2 pan;
    static string goodSource, watchedVersion;
    static Func<string, List<TechTreeData.Node>> plainTree;

    static void Check(string name, bool ok, string detail = "") { log.AppendLine((ok ? "PASS " : "FAIL ") + name + (ok || detail == "" ? "" : "   [" + detail + "]")); if (!ok) failed++; }
    static T Get<T>(string field) => (T)typeof(TechRequireAllWindow).GetField(field, Private).GetValue(window);
    static void Set(string field, object value) => typeof(TechRequireAllWindow).GetField(field, Private).SetValue(window, value);
    static void Call(string method, params object[] args) => typeof(TechRequireAllWindow).GetMethod(method, Private).Invoke(window, args);
    static bool Dirty => (bool)typeof(TechRequireAllWindow).GetProperty("Dirty", Private).GetValue(window, null);
    static string Artifact => Path.Combine(config, TechRequireAllRegistry.ArtifactName);

    // the Tech Tree viewer (its OR / AND switch writes the same list)
    static T VGet<T>(string field) => (T)typeof(TechTreeWindow).GetField(field, Private).GetValue(viewer);
    static void VSet(string field, object value) => typeof(TechTreeWindow).GetField(field, Private).SetValue(viewer, value);
    static object VCall(string method, params object[] args) => typeof(TechTreeWindow).GetMethod(method, Private).Invoke(viewer, args);
    static TechTreeData.Node N(string name) => VGet<List<TechTreeData.Node>>("_nodes").First(n => n.Name == name);   // the viewer's CURRENT node
    static bool All(string name) => (bool)VCall("EffRequireAll", N(name));
    static int Staged => ((System.Collections.IDictionary)typeof(TechTreeWindow).GetField("_pending", Private).GetValue(viewer)).Count;
    static string Deployed => File.Exists(Artifact) ? PluginReads(Artifact) : "no file";
    static bool Near(Vector2 p, float x, float y) => Mathf.Abs(p.x - x) < 0.01f && Mathf.Abs(p.y - y) < 0.01f;
    static string Points(List<Vector2> path) => string.Join(" ", path.Select(q => "(" + q.x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "," + q.y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")"));

    // ---- real assets in the temporary project (the mutation run of 2026-11-21: nothing in this drill wrote one)
    const string DbDefs = "Assets/Databases/Defs", DbMaps = "Assets/Databases/Maps", RefDefs = "Assets/VanillaReference/Defs", RefMaps = "Assets/VanillaReference/Maps";
    const string ModPath = "Assets/Databases/New Additions";
    static void Folder(string assetPath) { Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath)); AssetDatabase.Refresh(); }
    static T NewAsset<T>(string path, Action<T> init) where T : ScriptableObject
    {
        var o = ScriptableObject.CreateInstance<T>(); init(o); AssetDatabase.CreateAsset(o, path); return o;
    }
    static T Asset<T>(string path) where T : UnityEngine.Object { var o = AssetDatabase.LoadAssetAtPath<T>(path); return o == null ? null : o; }
    static string[] NamesOf(TechTreeDrillDefinition d) => d.TechnologyPrerequisite.SerializableTechnologyNames ?? new string[0];
    static string[] Prereqs(string name) => ((IReadOnlyList<string>)VCall("EffPrereqs", N(name))).ToArray();
    // A node as TechTreeData.Build makes it: the Databases copy when there is one, else the reference copy, and the
    // base values READ from those assets (so a check after a save and its reload holds the write and the read).
    static TechTreeData.Node AssetNode(string name, string label)
    {
        var node = new TechTreeData.Node { Name = name, Label = label, BasePrereqs = new string[0] };
        node.ModDef = Asset<TechTreeDrillDefinition>(DbDefs + "/" + name + ".asset");
        if (node.ModDef == null) node.ModDef = Asset<TechTreeDrillDefinition>(ModPath + "/" + name + ".asset");
        node.ModMapper = Asset<TechTreeDrillMapper>(DbMaps + "/" + name + ".asset");
        if (node.ModMapper == null) node.ModMapper = Asset<TechTreeDrillMapper>(ModPath + "/" + name + ".asset");
        node.VanillaDef = Asset<TechTreeDrillDefinition>(RefDefs + "/" + name + ".asset");
        node.VanillaMapper = Asset<TechTreeDrillMapper>(RefMaps + "/" + name + ".asset");
        var def = node.ActiveDef as TechTreeDrillDefinition; var map = node.ActiveMapper as TechTreeDrillMapper;
        if (map != null) { node.BaseX = map.TechTreeX; node.BaseY = map.TechTreeY; }
        if (def != null) { node.BasePrereqs = NamesOf(def).ToArray(); node.BaseVisibility = TechTreeData.ReadVisibility(def); }
        return node;
    }

    // the plugin's reader, from the plugin DLL in Assets/Plugins: what the game will make of the deployed file
    static string PluginReads(string path)
    {
        var rules = Assembly.Load("HumankindAssetFramework").GetType("HumankindAssetFramework.TechRequireAllRules", true);
        var args = new object[] { File.ReadAllText(path), null };
        var names = (HashSet<string>)rules.GetMethod("ParseList", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
        return ((string)args[1] != "" ? "PROBLEM " + args[1] + " " : "") + string.Join(",", names.OrderBy(n => n, StringComparer.Ordinal));
    }

    static TechTreeData.Node Node(string name, string label, params string[] prereqs) =>
        new TechTreeData.Node { Name = name, Label = label, BasePrereqs = prereqs };

    static List<TechTreeData.Node> Tree() => new List<TechTreeData.Node>
    {
        Node("Technology_A", "Alpha"), Node("Technology_B", "Beta"),
        Node("Technology_T", "Two Parents", "Technology_A", "Technology_B"),
        Node("Technology_S", "Single Parent", "Technology_A"),
    };

    // The viewer reads its tree through a seam. Like the real builder it returns NEW node objects on every reload
    // (every save reloads). The nodes have no assets behind them, so a position cannot be written here: the viewer
    // must keep such an edit staged.
    static void OpenViewer()
    {
        TechTreeWindow.BuildTreeImpl = _ =>
        {
            var tree = Tree();
            tree[1].BaseX = 8; tree[1].BaseY = -2; tree[2].BaseX = 16; tree[2].BaseY = 4; tree[3].BaseX = 16; tree[3].BaseY = 9;
            return tree;
        };
        viewer = ScriptableObject.CreateInstance<TechTreeWindow>();   // OnEnable: reads the tree and the list
        VSet("_editMode", true);
        VSet("_selected", N("Technology_T"));
        viewer.position = new Rect(700, 80, 900, 520);
        viewer.ShowUtility();
    }

    static void InjectTree()
    {
        var nodes = Tree();
        Set("nodes", nodes);
        Set("byName", nodes.ToDictionary(n => n.Name));
        Set("treeProblem", "");
    }

    static void OpenWindow()
    {
        window = ScriptableObject.CreateInstance<TechRequireAllWindow>();   // OnEnable: reads the tree and the list
        InjectTree();
        window.position = new Rect(80, 80, 560, 520);
        window.ShowUtility();
    }

    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        output = args[Array.IndexOf(args, "-hafTechAllOutput") + 1];
        config = args[Array.IndexOf(args, "-hafTechAllConfig") + 1];
        try
        {
            Directory.CreateDirectory(config);
            TechRequireAllRegistry.ConfigDirImpl = () => config;   // before anything loads the registry
            Application.logMessageReceived += (msg, stack, type) =>
            {
                // the tech-tree read finds no game types in a bare project and says so: expected here, and replaced by InjectTree
                if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && !msg.StartsWith("[TechTree] Tech/Mapper type not found"))
                    errors.Add(type + ": " + msg);
            };
            OpenWindow();
            deadline = EditorApplication.timeSinceStartup + 60;
            EditorApplication.update += Step;
        }
        catch (Exception ex) { Finish(ex); }
    }

    // one full IMGUI pass of the real OnGUI; an exception inside it reaches the log hook above
    static bool Drawn()
    {
        if (draws >= 2) { draws = 0; return true; }
        window.SendEvent(new Event { type = EventType.Layout });
        window.SendEvent(new Event { type = EventType.Repaint });
        if (viewer != null)
        {
            viewer.SendEvent(new Event { type = EventType.Layout });
            viewer.SendEvent(new Event { type = EventType.Repaint });
            viewer.Repaint();
        }
        draws++; window.Repaint();
        return false;
    }

    static void Step()
    {
        try
        {
            if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("the window drill did not finish (step " + step + ")");
            if (!Drawn()) return;
            switch (step++)
            {
                case 0:
                    Check("opens empty, nothing to save", Get<List<string>>("entries").Count == 0 && !Dirty);
                    Check("opening wrote nothing to the game's folder", !File.Exists(Artifact));
                    Call("Add", "Technology_T");
                    Check("add: listed and unsaved", Get<List<string>>("entries").SequenceEqual(new[] { "Technology_T" }) && Dirty);
                    break;
                case 1:
                    Call("Save");
                    Check("save: reported as saved", Get<string>("status").StartsWith("Saved 1 technology"), Get<string>("status"));
                    Check("save: nothing unsaved left", !Dirty);
                    Check("save: project source written", File.Exists(TechRequireAllRegistry.SourcePath));
                    Check("save: the plugin reads the deployed file as [Technology_T]", File.Exists(Artifact) && PluginReads(Artifact) == "Technology_T", File.Exists(Artifact) ? PluginReads(Artifact) : "no file");
                    Call("Add", "Technology_S");       // one prerequisite: the row says it has no effect
                    Call("Add", "Technology_Ghost");   // not in the tree: the row says the game ignores it
                    Call("Add", "Technology_T");       // already listed
                    Check("add twice: no duplicate", Get<List<string>>("entries").Count == 3);
                    break;
                case 2:
                    Call("Save");
                    Check("save three: the plugin reads all three, sorted", PluginReads(Artifact) == "Technology_Ghost,Technology_S,Technology_T", PluginReads(Artifact));
                    window.Close();
                    OpenWindow();                      // a new window reads the list from disk
                    Check("reopen: the three entries come back", Get<List<string>>("entries").SequenceEqual(new[] { "Technology_Ghost", "Technology_S", "Technology_T" }) && !Dirty);
                    break;
                case 3:
                    Get<List<string>>("entries").Clear();
                    Check("remove all: unsaved", Dirty);
                    break;
                case 4:
                    Call("Save");
                    Check("save empty: reported as saved", Get<string>("status").StartsWith("Saved 0 technologies"), Get<string>("status"));
                    Check("save empty: the plugin reads an empty list, not a broken file", PluginReads(Artifact) == "", PluginReads(Artifact));
                    break;
                case 5:
                    // ---- the Tech Tree viewer: the OR / AND switch, the typed position, the scroll bars ----
                    OpenViewer();
                    Check("viewer: an unlisted technology is under the game's rule (OR)", !All("Technology_T") && TechTreeWindow.RuleHeader(false, false) == "Prerequisites (OR):" && TechTreeWindow.RuleHeader(false, true) == "Prerequisites (AND):");
                    Check("viewer: the side strip's heading for it says OR", (string)VCall("SideHeader") == "Prerequisites (OR):", (string)VCall("SideHeader"));
                    VCall("SetRule", N("Technology_T"), true);
                    Check("viewer: the switch to AND is staged, nothing is written", All("Technology_T") && Staged == 1 && Deployed == "", Deployed + " staged " + Staged);
                    Check("viewer: the side strip's heading follows the staged switch", (string)VCall("SideHeader") == "Prerequisites (AND):", (string)VCall("SideHeader"));
                    VCall("SetRule", N("Technology_T"), false);
                    Check("viewer: switching back is no change", !All("Technology_T") && Staged == 0);
                    VCall("SetRule", N("Technology_T"), true);
                    VCall("SetPosition", N("Technology_T"), 7, -3);
                    Check("viewer: a typed position is staged, with the drag's floor of 0", (int)VCall("EffX", N("Technology_T")) == 7 && (int)VCall("EffY", N("Technology_T")) == 0 && Staged == 1);
                    break;
                case 6:
                    pan = VGet<Vector2>("_pan");   // framed by the draws before this step
                    VCall("CommitChanges");
                    Check("viewer save: the plugin reads the deployed list as [Technology_T]", Deployed == "Technology_T", Deployed);
                    Check("viewer save: the rule now comes from the list", VGet<HashSet<string>>("_requireAll").SetEquals(new[] { "Technology_T" }) && All("Technology_T"));
                    Check("viewer save: the position, which has no asset to go to here, stays staged", Staged == 1 && (int)VCall("EffX", N("Technology_T")) == 7);
                    Check("viewer save: the selection is the reloaded tree's own node, not the object from before the save", ReferenceEquals(VGet<TechTreeData.Node>("_selected"), N("Technology_T")));
                    Check("viewer save: the open list window (nothing unsaved) shows the new entry", Get<List<string>>("entries").SequenceEqual(new[] { "Technology_T" }) && !Dirty, string.Join(",", Get<List<string>>("entries")));
                    VCall("SetPosition", N("Technology_T"), 16, 4);   // its saved position again
                    Check("viewer: the position typed back is no change", Staged == 0);
                    Call("Add", "Technology_S");                      // the list window now holds an unsaved change
                    VCall("SetRule", N("Technology_T"), false);
                    break;
                case 7:
                    VCall("CommitChanges");
                    Check("viewer save (back to OR): the plugin reads an empty list", Deployed == "", Deployed);
                    Check("viewer save: a list window WITH unsaved changes keeps them", Get<List<string>>("entries").SequenceEqual(new[] { "Technology_S", "Technology_T" }) && Dirty, string.Join(",", Get<List<string>>("entries")));
                    Call("Save");
                    Check("    and its save is refused: the list changed since it loaded", Get<string>("status").StartsWith("SAVE REFUSED") && Deployed == "", Get<string>("status") + " | " + Deployed);
                    Call("Reload"); Call("Add", "Technology_S"); Call("Save");
                    Check("list window: reloaded, S added and saved", Deployed == "Technology_S", Deployed);
                    VCall("SetRule", N("Technology_S"), true);        // staged in the viewer, whose copy of the list is older
                    Check("viewer: the same switch staged before it has seen that save", Staged == 1);
                    break;
                case 8:
                    Check("viewer: its next draw shows the list window's save (S is AND), and the staged switch is no longer a change", All("Technology_S") && Staged == 0, "staged " + Staged);
                    Check("viewer: drawing with the scroll bars did not move the view", VGet<Vector2>("_pan") == pan, VGet<Vector2>("_pan") + " vs " + pan);
                    goodSource = File.ReadAllText(TechRequireAllRegistry.SourcePath);
                    File.WriteAllText(TechRequireAllRegistry.SourcePath, "{ this is not the list");
                    VCall("SetRule", N("Technology_S"), false);
                    VCall("CommitChanges");                           // logs the refusal as an error: expected here
                    Check("viewer save over a broken list: nothing is written", Deployed == "Technology_S" && File.ReadAllText(TechRequireAllRegistry.SourcePath) == "{ this is not the list", Deployed);
                    Check("    the switch stays staged", Staged == 1 && !All("Technology_S"), "staged " + Staged);
                    Check("    and the viewer shows no rule for a list it cannot read", VGet<bool>("_rulesUnreadable") && TechTreeWindow.RuleHeader(true, true) == "Prerequisites:" && (string)VCall("SideHeader") == "Prerequisites:", (string)VCall("SideHeader"));
                    Check("    the refusal was logged", errors.Any(e => e.Contains("[TechAll]")), string.Join(" | ", errors));
                    errors.RemoveAll(e => e.Contains("[TechAll]"));
                    break;
                case 9:
                    errors.RemoveAll(e => e.Contains("[TechAll]"));   // the broken list, said again by the draws in between
                    File.WriteAllText(TechRequireAllRegistry.SourcePath, goodSource);
                    VCall("Reload", true, true);
                    Check("viewer: the repaired list is read again and the staged switch survived", !VGet<bool>("_rulesUnreadable") && Staged == 1 && !All("Technology_S"), "staged " + Staged);
                    VCall("CommitChanges");
                    Check("viewer save: S is back under OR and the list is empty", Deployed == "" && Staged == 0, Deployed + " staged " + Staged);
                    float lo, hi;
                    TechTreeWindow.ScrollRange(-60, 1000, 100, 300, out lo, out hi);
                    Check("scroll range: the tree's extent while the view is inside it", lo == -60 && hi == 1000, lo + ".." + hi);
                    TechTreeWindow.ScrollRange(-60, 1000, -500, 300, out lo, out hi);
                    Check("scroll range: widened to a view panned past the left edge", lo == -500 && hi == 1000, lo + ".." + hi);
                    TechTreeWindow.ScrollRange(-60, 1000, 900, 300, out lo, out hi);
                    Check("scroll range: widened to a view panned past the right edge", lo == -60 && hi == 1200, lo + ".." + hi);
                    // ---- the links, routed as the game routes them (TechnologyScreen_Links.Link.RebuildCurve). The
                    // expected points are worked out by hand from that rule, for cards of 251 x 93.5.
                    var path = new List<Vector2>();
                    var card = new Rect(0, 0, 251, 93.5f);
                    TechTreeWindow.LinkPath(card, new Rect(360, 71.2f, 251, 93.5f), 12, path);
                    Check("link, cards share some height: one horizontal line at the middle of the shared part",
                          path.Count == 2 && Near(path[0], 251, 82.35f) && Near(path[1], 360, 82.35f), Points(path));
                    TechTreeWindow.LinkPath(card, new Rect(360, 200, 251, 93.5f), 12, path);
                    Check("link, second card lower: out of the bottom edge's middle, down, round the elbow, to the left edge's middle",
                          path.Count > 4 && Near(path[0], 125.5f, 93.5f) && Near(path[1], 125.5f, 234.75f) && Near(path[path.Count - 2], 137.5f, 246.75f) && Near(path[path.Count - 1], 360, 246.75f), Points(path));
                    Check("    the elbow is the game's curve: nine points, the middle one at (127, 245.25)", path.Count == 9 && Near(path[4], 127f, 245.25f), Points(path));
                    Check("    the elbow stays inside its corner", path.Skip(1).Take(path.Count - 2).All(q => q.x >= 125.5f - 0.01f && q.x <= 137.51f && q.y >= 234.74f && q.y <= 246.76f), Points(path));
                    TechTreeWindow.LinkPath(new Rect(0, 300, 251, 93.5f), new Rect(360, 0, 251, 93.5f), 12, path);
                    Check("link, second card higher: out of the TOP edge's middle, up to the second card's middle",
                          Near(path[0], 125.5f, 300) && Near(path[1], 125.5f, 58.75f) && Near(path[path.Count - 2], 137.5f, 46.75f) && Near(path[path.Count - 1], 360, 46.75f), Points(path));
                    TechTreeWindow.LinkPath(card, new Rect(260, 100, 251, 93.5f), 500, path);
                    Check("link: an elbow radius larger than the room is cut down to it",
                          Near(path[1], 125.5f, 93.5f) && Near(path[path.Count - 2], 125.5f + 53.25f, 146.75f), Points(path));
                    TechTreeWindow.LinkPath(card, new Rect(260, 300, 251, 93.5f), 500, path);
                    Check("link: the radius is cut to the horizontal room too (134.5 here, the vertical is 253.25)",
                          Near(path[1], 125.5f, 212.25f) && Near(path[path.Count - 2], 260, 346.75f), Points(path));
                    TechTreeWindow.LinkPath(card, new Rect(100, 300, 251, 93.5f), 12, path);
                    Check("link, second card not right of the first: centre to centre, as the game draws it",
                          path.Count == 2 && Near(path[0], 125.5f, 46.75f) && Near(path[1], 225.5f, 346.75f), Points(path));
                    // ---- visible / invisible on the game's tech screen (TechnologyDefinition.Visibility)
                    Check("visibility: the tech-screen bit decides (Always and TechnologyScreen show, Never and the popup-only value do not)",
                          TechTreeData.OnTechScreen(-1) && TechTreeData.OnTechScreen(1) && !TechTreeData.OnTechScreen(0) && !TechTreeData.OnTechScreen(2));
                    VCall("SetVisibility", N("Technology_T"), 0);
                    Check("visibility: switching off is staged, and the card counts as not on the tech screen", (int)VCall("EffVisibility", N("Technology_T")) == 0 && (bool)VCall("Hidden", N("Technology_T")) && Staged == 1);
                    VCall("SetVisibility", N("Technology_T"), -1);
                    Check("visibility: switching back is no change", (int)VCall("EffVisibility", N("Technology_T")) == -1 && Staged == 0);
                    // The switch is "in the game" (any value but Never) against "switched off" (Never): the game creates a
                    // technology for an empire only when Visibility != 0 (DepartmentOfScience.CreateTechnologies).
                    Check("visibility: 'In the game' keeps a value that already is in the game and turns Never into Always; 'Switched off' is Never",
                          TechTreeWindow.SwitchedVisibility(-1, true) == -1 && TechTreeWindow.SwitchedVisibility(0, true) == -1
                          && TechTreeWindow.SwitchedVisibility(1, true) == 1 && TechTreeWindow.SwitchedVisibility(2, true) == 2
                          && TechTreeWindow.SwitchedVisibility(-1, false) == 0 && TechTreeWindow.SwitchedVisibility(2, false) == 0 && TechTreeWindow.SwitchedVisibility(0, false) == 0);
                    Check("visibility: the note says a Never technology is switched off in the game, not merely hidden",
                          TechTreeWindow.VisibilityNote("Future Technology", 0, null) == "Future Technology is SWITCHED OFF (Visibility = Never): the game does not create it for any empire. It is not on the tech screen and, by the game's own rules, cannot be researched, traded or granted."
                          && TechTreeWindow.VisibilityNote("Future Technology", -1, null) == "",
                          TechTreeWindow.VisibilityNote("Future Technology", 0, null));
                    Check("visibility: the note names what depends on a switched-off technology",
                          TechTreeWindow.VisibilityNote("X", 0, new List<string> { "Alpha", "Beta" }).EndsWith(" 2 technologies name it as a prerequisite (Alpha, Beta): one that names no other can never unlock.")
                          && TechTreeWindow.VisibilityNote("X", 0, new List<string> { "Alpha" }).Contains(" 1 technology names it as a prerequisite (Alpha)"),
                          TechTreeWindow.VisibilityNote("X", 0, new List<string> { "Alpha", "Beta" }));
                    Check("visibility: a value that is in the game is named as it is, with or without a card on the tech screen",
                          TechTreeWindow.VisibilityNote("X", 1, null) == "X is in the game and on its tech screen, with Visibility = TechnologyScreen (not Always)."
                          && TechTreeWindow.VisibilityNote("X", 2, null) == "X is in the game but has NO card on its tech screen (Visibility = ControlBannerTechnologyPopup).",
                          TechTreeWindow.VisibilityNote("X", 2, null));
                    {
                        // the side strip's note in the window's own state: Alpha switched off, Two Parents and Single Parent name it
                        VSet("_selected", N("Technology_A"));
                        VCall("SetVisibility", N("Technology_A"), 0);
                        var noteArgs = new object[] { false };
                        string sideNote = (string)typeof(TechTreeWindow).GetMethod("SideVisibilityNote", Private).Invoke(viewer, noteArgs);
                        Check("visibility: the side strip's note for the selected technology names the technologies that depend on it, as a warning",
                              sideNote.StartsWith("Alpha is SWITCHED OFF") && sideNote.Contains(" 2 technologies name it as a prerequisite (Single Parent, Two Parents)") && (bool)noteArgs[0], sideNote);
                        VCall("SetVisibility", N("Technology_A"), -1);
                        sideNote = (string)typeof(TechTreeWindow).GetMethod("SideVisibilityNote", Private).Invoke(viewer, noteArgs);
                        Check("visibility: and nothing once it is back in the game", sideNote == "" && !(bool)noteArgs[0] && Staged == 0, sideNote);
                        VSet("_selected", N("Technology_T"));
                    }
                    var fakeDef = ScriptableObject.CreateInstance<TechTreeDrillDefinition>();
                    fakeDef.Visibility = TechTreeDrillDefinition.Flags.TechnologyScreen;   // not the reader's fallback (Always)
                    Check("visibility: read from a definition's field as its number", TechTreeData.ReadVisibility(fakeDef) == 1);
                    fakeDef.Visibility = TechTreeDrillDefinition.Flags.Always;
                    Check("visibility: written to the field as the enum's value, and read back",
                          TechTreeData.WriteVisibility(fakeDef, 0) && fakeDef.Visibility == TechTreeDrillDefinition.Flags.Never && TechTreeData.ReadVisibility(fakeDef) == 0, fakeDef.Visibility.ToString());
                    UnityEngine.Object.DestroyImmediate(fakeDef);
                    // ---- the artwork. The expected GUID strings are what the GAME's own Amplitude.Framework.Guid.ToString()
                    // prints for these four ints (run against Amplitude.Framework.dll, 2026-11-21).
                    Check("art: an image reference becomes the Unity GUID the game prints for it",
                          TechTreeData.AmplitudeGuidToUnity(1853069741, 1283173340, 2015694476, -658824994) == "da9937e6cdbab7c4c8e05287ed02bb8d"
                          && TechTreeData.AmplitudeGuidToUnity(209776758, 1286721164, -1264855650, 577032575) == "670f08c0c8ec1bc4e95db94bf71d4622"
                          && TechTreeData.AmplitudeGuidToUnity(-1, 0, 1, int.MinValue) == "ffffffff000000001000000000000008",
                          TechTreeData.AmplitudeGuidToUnity(-1, 0, 1, int.MinValue));
                    // The game's card prefab (read from its UI_Textures bundle): a 310 x 116 card whose "Artwork" is a
                    // 200 x 200 square at (200, -60) from the card's top left corner.
                    var artRect = TechTreeWindow.ArtRect(new Rect(100, 200, 310, 116));
                    Check("art: where the game's card prefab puts it (200 square, 200 right of the left edge, 60 above the top)",
                          Mathf.Abs(artRect.x - 300) < 0.01f && Mathf.Abs(artRect.y - 140) < 0.01f && Mathf.Abs(artRect.width - 200) < 0.01f && Mathf.Abs(artRect.height - 200) < 0.01f, artRect.ToString());
                    var artHalf = TechTreeWindow.ArtRect(new Rect(0, 0, 155, 58));
                    Check("art: scaled with the card", Mathf.Abs(artHalf.x - 100) < 0.01f && Mathf.Abs(artHalf.y + 30) < 0.01f && Mathf.Abs(artHalf.width - 100) < 0.01f, artHalf.ToString());
                    Check("art: the side strip explains a card without artwork",
                          TechTreeWindow.ArtLine(new TechTreeData.Node { ArtPath = "Assets/Resources/Images/x512.png", ArtNamed = true }, null, "") == "Image: x512.png"
                          && TechTreeWindow.ArtLine(new TechTreeData.Node { ArtNamed = true }, "UI_Technology_Era6_00.png", "") == "Image: one of the game's own (in UI_Technology_Era6_00.png)"
                          && TechTreeWindow.ArtLine(new TechTreeData.Node { ArtNamed = true }, null, "no bundle") == "Image: not in this project, and the game's own cannot be read: no bundle"
                          && TechTreeWindow.ArtLine(new TechTreeData.Node { ArtNamed = true }, null, "") == "Image: not found, neither in this project nor among the game's own"
                          && TechTreeWindow.ArtLine(new TechTreeData.Node(), null, "") == "Image: none set");
                    // The game's atlas rectangles are measured from the texture's TOP. The numbers are World-Wide Web's
                    // entry as read from the game's UI bundle (UnityPy, 2026-11-21): x .70751953125, y .00048828125,
                    // 0.17578125 square, on a 2048 atlas: the picture is in the top rows, so its v starts at 0.82373046875.
                    var uv = TechTreeVanillaArt.UvOf(new Rect(0.70751953125f, 0.00048828125f, 0.17578125f, 0.17578125f));
                    Check("art: a game atlas rectangle (from the top) becomes Unity texture coordinates (from the bottom)",
                          Mathf.Abs(uv.x - 0.70751953125f) < 1e-6f && Mathf.Abs(uv.y - 0.82373046875f) < 1e-6f && Mathf.Abs(uv.width - 0.17578125f) < 1e-6f && Mathf.Abs(uv.height - 0.17578125f) < 1e-6f, uv.ToString("0.000000"));
                    var fit = TechTreeWindow.FitInside(new Rect(0, 0, 100, 100), 2f);
                    Check("art: an image twice as wide as high is fitted into the square at half its height, centred", Mathf.Abs(fit.width - 100) < 0.01f && Mathf.Abs(fit.height - 50) < 0.01f && Mathf.Abs(fit.y - 25) < 0.01f, fit.ToString());
                    var tall = TechTreeWindow.FitInside(new Rect(0, 0, 100, 100), 0.5f);
                    Check("art: an image twice as high as wide at half the square's width, centred", Mathf.Abs(tall.width - 50) < 0.01f && Mathf.Abs(tall.height - 100) < 0.01f && Mathf.Abs(tall.x - 25) < 0.01f && Mathf.Abs(tall.y) < 0.01f, tall.ToString());
                    // the canvas: a click in the middle of a card finds that card (cards sit on the game's grid)
                    var rectT = (Rect)VCall("NodeRect", N("Technology_T"));
                    float zoomNow = VGet<float>("_zoom");
                    Check("canvas: a card is the game's 310 x 116 at zoom 1", Mathf.Abs(rectT.width / zoomNow - 310f) < 0.01f && Mathf.Abs(rectT.height / zoomNow - 116f) < 0.01f, rectT + " at zoom " + zoomNow);
                    Check("canvas: a click in the middle of a card finds it", ReferenceEquals(VCall("NodeAt", rectT.center), N("Technology_T")));
                    var rectS = (Rect)VCall("NodeRect", N("Technology_S"));
                    // T (16,4) and S (16,9) overlap in a strip a quarter row high; S is drawn later, so it is on top
                    var inBoth = new Vector2(rectT.center.x, rectS.y + 2 * zoomNow);
                    Check("canvas: where two cards overlap, a click takes the one drawn on top", rectT.Contains(inBoth) && rectS.Contains(inBoth) && ReferenceEquals(VCall("NodeAt", inBoth), N("Technology_S")));
                    Check("canvas: five rows down is 110 px at zoom 1 (the game's 22 per row), the same column the same x",
                          Mathf.Abs((rectS.y - rectT.y) / zoomNow - 5 * 22f) < 0.01f && Mathf.Abs(rectS.x - rectT.x) < 0.01f, rectS + " vs " + rectT);
                    var rectB = (Rect)VCall("NodeRect", N("Technology_B"));
                    var rectA = (Rect)VCall("NodeRect", N("Technology_A"));
                    Check("canvas: eight columns right is 592 px at zoom 1 (the game's 74 per column)", Mathf.Abs((rectB.x - rectA.x) / zoomNow - 8 * 74f) < 0.01f, rectB + " vs " + rectA);
                    break;
                case 10:
                {
                    // ---- the fixes of the first review round (2026-11-21) ----
                    const string T = "Technology_T";
                    VCall("SetPosition", N(T), -3, 4);
                    Check("position: a typed negative X is floored at 0 too", (int)VCall("EffX", N(T)) == 0 && (int)VCall("EffY", N(T)) == 4);
                    VCall("SetPosition", N(T), 20, 6); VCall("SetVisibility", N(T), 0); VCall("SetRule", N(T), true); VCall("RemovePrereq", N(T), "Technology_B");
                    var fullTree = TechTreeWindow.BuildTreeImpl;
                    TechTreeWindow.BuildTreeImpl = _ => fullTree(null).Where(n => n.Name != T).ToList();
                    VCall("Reload", true, false);
                    Check("a reload that does not find a technology keeps its staged edits", Staged == 1, "staged " + Staged);
                    VCall("CommitChanges");
                    Check("    a save while it is absent writes nothing of it and keeps it staged", Staged == 1 && Deployed == "", Deployed + " staged " + Staged);
                    TechTreeWindow.BuildTreeImpl = fullTree;
                    VCall("Reload", true, false);
                    Check("    and with the technology back the same edits apply", (int)VCall("EffX", N(T)) == 20 && (int)VCall("EffY", N(T)) == 6 && (int)VCall("EffVisibility", N(T)) == 0 && All(T) && Prereqs(T).SequenceEqual(new[] { "Technology_A" }) && Staged == 1);
                    // a script reload: Unity serializes the window and builds its fields again
                    VSet("_selected", N(T));
                    // Everything the window holds in memory is wiped after the freeze, mirrors included: what comes back
                    // has to come out of Unity's own serialized form (the mutation run: with the mirrors left filled,
                    // the check passed without [SerializeField] on them).
                    var panWas = VGet<Vector2>("_pan"); float zoomWas = VGet<float>("_zoom");
                    string frozen = EditorJsonUtility.ToJson(viewer);
                    ((System.Collections.IDictionary)typeof(TechTreeWindow).GetField("_pending", Private).GetValue(viewer)).Clear();
                    ((System.Collections.IList)typeof(TechTreeWindow).GetField("_pendingRecords", Private).GetValue(viewer)).Clear();
                    VSet("_selected", null); VSet("_selectedName", null); VSet("_pan", new Vector2(-999, -999)); VSet("_zoom", 0.123f);
                    EditorJsonUtility.FromJsonOverwrite(frozen, viewer);
                    VCall("Reload", true, false);
                    Check("a script reload keeps every kind of staged edit, the selection and the view",
                          Staged == 1 && (int)VCall("EffX", N(T)) == 20 && (int)VCall("EffY", N(T)) == 6 && (int)VCall("EffVisibility", N(T)) == 0 && All(T) && Prereqs(T).SequenceEqual(new[] { "Technology_A" })
                          && ReferenceEquals(VGet<TechTreeData.Node>("_selected"), N(T)) && VGet<Vector2>("_pan") == panWas && VGet<float>("_zoom") == zoomWas,
                          "staged " + Staged + " selected " + (VGet<TechTreeData.Node>("_selected")?.Name ?? "none") + " pan " + VGet<Vector2>("_pan") + " zoom " + VGet<float>("_zoom"));
                    VCall("CommitChanges");   // the rule is written; position, prerequisites and visibility have no asset here and stay
                    Check("save: a saved rule switch leaves the staged set, the parts without an asset stay",
                          Deployed == T && Staged == 1 && (int)VCall("EffX", N(T)) == 20 && (int)VCall("EffVisibility", N(T)) == 0 && VGet<HashSet<string>>("_requireAll").Contains(T), Deployed + " staged " + Staged);
                    VCall("SetPosition", N(T), 16, 4); VCall("SetVisibility", N(T), -1); VCall("AddPrereq", N(T), "Technology_B");
                    Check("    typed back: nothing staged", Staged == 0, "staged " + Staged);
                    VCall("SetRule", N(T), false); VCall("CommitChanges");
                    Check("    the list is empty again", Deployed == "" && Staged == 0, Deployed);

                    // the floor at 0 is for the axis being changed: B sits at (8, -2)
                    const string B = "Technology_B";
                    VCall("SetPosition", N(B), 9, -2);
                    Check("position: typing X leaves a negative Y alone", (int)VCall("EffX", N(B)) == 9 && (int)VCall("EffY", N(B)) == -2);
                    VCall("SetPosition", N(B), 9, -5);
                    Check("position: a Y typed below its saved negative value stops there", (int)VCall("EffY", N(B)) == -2, "Y " + VCall("EffY", N(B)));
                    Check("position: the floor is 0, or as low as the coordinate already is",
                          TechTreeWindow.FloorOf(4, 4) == 0 && TechTreeWindow.FloorOf(-12, 3) == -12 && TechTreeWindow.FloorOf(5, -3) == -3 && TechTreeWindow.FloorOf(0, 0) == 0);
                    VCall("SetPosition", N(B), 8, -2);
                    Check("position: the saved negative value can be typed back", Staged == 0, "staged " + Staged);

                    // Frame All: a tree as wide as ENC's (TechTreeX up to 411) in a narrow window
                    TechTreeWindow.BuildTreeImpl = _ => { var wide = fullTree(null); var far = Node("Technology_Far", "Far"); far.BaseX = 411; far.BaseY = 33; wide.Add(far); return wide; };
                    VCall("Reload", true, false);
                    var narrow = new Rect(0, 21, 200, 479);   // narrower than the window can get: the fit must still hold
                    VSet("_fitWholeTree", true);
                    VCall("FrameAll", narrow);
                    var all = VGet<List<TechTreeData.Node>>("_nodes").Select(n => (Rect)VCall("NodeRect", n)).ToList();
                    Check("Frame All fits a tree as wide as ENC's into a 200 px canvas, below the wheel's floor of 0.01 (it stopped at 0.05 and cut both ends off)",
                          all.Min(r => r.xMin) >= narrow.xMin && all.Max(r => r.xMax) <= narrow.xMax && all.Min(r => r.yMin) >= narrow.yMin && all.Max(r => r.yMax) <= narrow.yMax && VGet<float>("_zoom") < 0.01f,
                          "zoom " + VGet<float>("_zoom") + " x " + all.Min(r => r.xMin) + ".." + all.Max(r => r.xMax));
                    // the view a window opens with: the tree's full HEIGHT, at its left end
                    var opening = new Rect(0, 21, 485, 479);
                    VSet("_fitWholeTree", false);
                    VCall("FrameAll", opening);
                    float openZoom = VGet<float>("_zoom");
                    all = VGet<List<TechTreeData.Node>>("_nodes").Select(n => (Rect)VCall("NodeRect", n)).ToList();
                    Check("opening view: the tree's full height in the canvas, a margin above and below, its left end at the left",
                          Mathf.Abs(all.Min(r => r.yMin) - (opening.yMin + 30 * openZoom)) < 1f && Mathf.Abs(all.Max(r => r.yMax) - (opening.yMax - 30 * openZoom)) < 1f && Mathf.Abs(all.Min(r => r.xMin) - (opening.xMin + 30 * openZoom)) < 1f && openZoom > 0.3f,
                          "zoom " + openZoom + " y " + all.Min(r => r.yMin) + ".." + all.Max(r => r.yMax) + " x from " + all.Min(r => r.xMin));
                    TechTreeWindow.BuildTreeImpl = fullTree;
                    VCall("Reload", true, false);

                    // ---- the second review round (2026-11-21) ----
                    // a drag through the window's real OnGUI: a click that slips is a click, a column of movement is a column
                    var canvasRect = new Rect(0, 21, 585, 484);   // the 900 x 520 window without the side strip and the bars
                    VSet("_fitWholeTree", true); VCall("FrameAll", canvasRect);
                    float dz = VGet<float>("_zoom");
                    var cardT = (Rect)VCall("NodeRect", N(T));
                    viewer.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = cardT.center });
                    viewer.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = cardT.center + new Vector2(2, 2) });
                    Check("drag: a click that slips two pixels moves nothing", Staged == 0 && canvasRect.Contains(cardT.center), "staged " + Staged + " card " + cardT);
                    viewer.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = cardT.center + new Vector2(74 * dz, 0) });
                    Check("drag: one column of movement is one column", Staged == 1 && (int)VCall("EffX", N(T)) == 17 && (int)VCall("EffY", N(T)) == 4, "X " + VCall("EffX", N(T)) + " Y " + VCall("EffY", N(T)));
                    viewer.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = cardT.center + new Vector2(74 * dz, 0) });
                    VCall("SetPosition", N(T), 16, 4);
                    // a row of four pixels: without the threshold a slip of three pixels would be a row (0.75 rounds to 1)
                    VSet("_zoom", 4f / 22f);
                    var smallT = (Rect)VCall("NodeRect", N(T));
                    viewer.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = smallT.center });
                    viewer.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = smallT.center + new Vector2(0, 3) });
                    Check("drag: at four pixels a row, a slip of three pixels moves nothing", Staged == 0 && canvasRect.Contains(smallT.center), "staged " + Staged + " Y " + VCall("EffY", N(T)) + " card " + smallT);
                    viewer.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = smallT.center + new Vector2(0, 8) });
                    Check("drag: and eight pixels are two rows", (int)VCall("EffY", N(T)) == 6 && (int)VCall("EffX", N(T)) == 16, "Y " + VCall("EffY", N(T)));
                    viewer.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = smallT.center + new Vector2(0, 8) });
                    VCall("SetPosition", N(T), 16, 4);
                    VSet("_zoom", 0.05f);   // a row is 1.1 px: a slip of a pixel would be a row
                    var tinyT = (Rect)VCall("NodeRect", N(T));
                    viewer.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = tinyT.center });
                    viewer.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = tinyT.center + new Vector2(30, 30) });
                    viewer.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = tinyT.center + new Vector2(30, 30) });
                    Check("drag: no card moves while a row is under two pixels", Staged == 0 && canvasRect.Contains(tinyT.center) && ReferenceEquals(VGet<TechTreeData.Node>("_selected"), N(T)), "staged " + Staged + " card " + tinyT);
                    VSet("_fitWholeTree", true); VCall("FrameAll", canvasRect);
                    Check("save button: counts what can be saved and names the rest",
                          TechTreeWindow.SaveLabel(0, 0) == "Save" && TechTreeWindow.SaveLabel(2, 0) == "Save (2)" && TechTreeWindow.SaveLabel(1, 3) == "Save (1) · 3 missing" && TechTreeWindow.SaveLabel(0, 1) == "Save · 1 missing");
                    // the list's window reads the tree through the viewer's seam, at its next draw after a viewer save
                    Check("list window: it holds the tree of the viewer's seam after the viewer's saves", Get<List<TechTreeData.Node>>("nodes").Count == 4 && Get<List<TechTreeData.Node>>("nodes").Any(n => n.Name == T && n.BaseX == 16),
                          "nodes " + Get<List<TechTreeData.Node>>("nodes").Count);

                    // the Save line says what the outcome guarantees
                    Check("save line: a switch the list already held is not called deployed",
                          TechTreeWindow.RuleNote(1, RegistryRules.SaveOutcome.Saved, false, false) == ", 1 rule switch (OR / AND): the list already held it, so nothing was written or deployed",
                          TechTreeWindow.RuleNote(1, RegistryRules.SaveOutcome.Saved, false, false));
                    Check("save line: written, refused and unsettled each say their own thing",
                          TechTreeWindow.RuleNote(2, RegistryRules.SaveOutcome.Saved, true, false).Contains("relaunch the game to apply") && TechTreeWindow.RuleNote(2, RegistryRules.SaveOutcome.Saved, true, false).EndsWith("(a rule switch is not part of Undo)")
                          && TechTreeWindow.RuleNote(2, RegistryRules.SaveOutcome.Saved, true, true).Contains("not refreshed yet")
                          && TechTreeWindow.RuleNote(2, RegistryRules.SaveOutcome.Refused, false, false).Contains("REFUSED") && TechTreeWindow.RuleNote(2, RegistryRules.SaveOutcome.Unknown, false, false).Contains("could NOT be confirmed")
                          && TechTreeWindow.RuleNote(2, RegistryRules.SaveOutcome.Unknown, false, false).EndsWith("kept staged unless the reload that follows finds them there"));
                    break;
                }
                case 11:
                {
                    // ---- Save into real assets: a mapper and a definition under Databases ----
                    const string M = "Technology_M";
                    Folder(DbDefs); Folder(DbMaps); Folder(RefDefs); Folder(RefMaps);
                    NewAsset<TechTreeDrillDefinition>(DbDefs + "/" + M + ".asset", d => d.TechnologyPrerequisite.SerializableTechnologyNames = new[] { "Technology_A", "Technology_B" });
                    NewAsset<TechTreeDrillMapper>(DbMaps + "/" + M + ".asset", m => { m.TechTreeX = 24; m.TechTreeY = 2; });
                    foreach (string reference in new[] { "Technology_R", "Technology_Q" })
                    {
                        NewAsset<TechTreeDrillDefinition>(RefDefs + "/" + reference + ".asset", d => d.TechnologyPrerequisite.SerializableTechnologyNames = new[] { "Technology_A", "Technology_B" });
                        NewAsset<TechTreeDrillMapper>(RefMaps + "/" + reference + ".asset", m => { m.TechTreeX = 24; m.TechTreeY = 8; });
                    }
                    AssetDatabase.SaveAssets();
                    plainTree = TechTreeWindow.BuildTreeImpl;
                    TechTreeWindow.BuildTreeImpl = _ => { var t = plainTree(null); t.Add(AssetNode(M, "Em")); t.Add(AssetNode("Technology_R", "Ar")); return t; };
                    VSet("_modPath", ModPath);
                    VCall("Reload", true, false);
                    Check("assets: a node's saved values are read from its mapper and definition", (int)VCall("EffX", N(M)) == 24 && (int)VCall("EffY", N(M)) == 2 && Prereqs(M).SequenceEqual(new[] { "Technology_A", "Technology_B" }) && N(M).DefModded && N(M).MapperModded);
                    VSet("_pan", new Vector2(-123, 45)); VSet("_zoom", 0.7f);   // a view no framing produces
                    VCall("SetPosition", N(M), 20, 6); VCall("RemovePrereq", N(M), "Technology_B"); VCall("SetVisibility", N(M), 0);
                    VCall("CommitChanges");
                    var mMap = Asset<TechTreeDrillMapper>(DbMaps + "/" + M + ".asset"); var mDef = Asset<TechTreeDrillDefinition>(DbDefs + "/" + M + ".asset");
                    Check("save into assets: X and Y are in the mapper, each in its own field", mMap.TechTreeX == 20 && mMap.TechTreeY == 6, mMap.TechTreeX + "," + mMap.TechTreeY);
                    Check("save into assets: prerequisites and visibility are in the definition", NamesOf(mDef).SequenceEqual(new[] { "Technology_A" }) && mDef.Visibility == TechTreeDrillDefinition.Flags.Never, string.Join(",", NamesOf(mDef)) + " " + mDef.Visibility);
                    Check("save into assets: every written part left the staged set, and the reload reads them back",
                          Staged == 0 && (int)VCall("EffX", N(M)) == 20 && (int)VCall("EffY", N(M)) == 6 && (int)VCall("EffVisibility", N(M)) == 0 && Prereqs(M).SequenceEqual(new[] { "Technology_A" }), "staged " + Staged);
                    Check("save: the view is the one from before it", VGet<Vector2>("_pan") == new Vector2(-123, 45) && VGet<float>("_zoom") == 0.7f, VGet<Vector2>("_pan") + " zoom " + VGet<float>("_zoom"));
                    break;
                }
                case 12:
                {
                    // ---- a technology that exists only in the reference folder ----
                    const string R = "Technology_R";
                    string modDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), ModPath);
                    VCall("RemovePrereq", N(R), "Technology_B"); VCall("SetVisibility", N(R), 0);
                    VCall("CommitChanges");
                    int copies = Directory.Exists(modDir) ? Directory.GetFiles(modDir, "Technology_R*.asset").Length : 0;
                    var rCopy = Asset<TechTreeDrillDefinition>(ModPath + "/" + R + ".asset"); var rRef = Asset<TechTreeDrillDefinition>(RefDefs + "/" + R + ".asset");
                    Check("reference-only definition, prerequisites AND visibility in one save: one copy, holding both (it was copied twice)",
                          copies == 1 && rCopy != null && NamesOf(rCopy).SequenceEqual(new[] { "Technology_A" }) && rCopy.Visibility == TechTreeDrillDefinition.Flags.Never && Staged == 0, "copies " + copies + " staged " + Staged);
                    Check("    the reference copy is untouched", NamesOf(rRef).SequenceEqual(new[] { "Technology_A", "Technology_B" }) && rRef.Visibility == TechTreeDrillDefinition.Flags.Always);
                    Check("    and the viewer now shows the copy", N(R).DefModded && Prereqs(R).SequenceEqual(new[] { "Technology_A" }) && (int)VCall("EffVisibility", N(R)) == 0);
                    VCall("AddPrereq", N(R), "Technology_B");
                    VCall("CommitChanges");
                    Check("    a second save edits that copy in place", Directory.GetFiles(modDir, "Technology_R*.asset").Length == 1 && NamesOf(Asset<TechTreeDrillDefinition>(ModPath + "/" + R + ".asset")).SequenceEqual(new[] { "Technology_A", "Technology_B" }) && Staged == 0);
                    // EnsureWritable itself
                    var q = AssetNode("Technology_Q", "Cue");
                    var first = TechTreeData.EnsureWritable(q, false, ModPath); var second = TechTreeData.EnsureWritable(q, false, ModPath);
                    Check("EnsureWritable: the copy is remembered on the node, and a second ask returns it",
                          first != null && ReferenceEquals(first, second) && ReferenceEquals(q.ModDef, first) && AssetDatabase.GetAssetPath(first).StartsWith("Assets/Databases/") && Directory.GetFiles(modDir, "Technology_Q*.asset").Length == 1);
                    // R's standalone mapper would be copied onto the file of its definition's copy
                    VCall("SetPosition", N(R), 30, 3);
                    VCall("CommitChanges");
                    Check("a standalone mapper is NOT copied over its definition's file: the position stays staged, the definition is whole",
                          Staged == 1 && (int)VCall("EffX", N(R)) == 30 && Asset<TechTreeDrillDefinition>(ModPath + "/" + R + ".asset") != null && Asset<TechTreeDrillMapper>(ModPath + "/" + R + ".asset") == null
                          && Asset<TechTreeDrillMapper>(RefMaps + "/" + R + ".asset").TechTreeX == 24, "staged " + Staged);
                    VCall("SetPosition", N(R), 24, 8);
                    Check("    typed back: nothing staged", Staged == 0);
                    TechTreeWindow.BuildTreeImpl = plainTree;
                    VCall("Reload", true, false);
                    break;
                }
                case 13:
                {
                    // ---- the rule write as a function, then two things the windows could not show ----
                    const string T = "Technology_T", S = "Technology_S";
                    var file = new TechRequireAllFile { technologies = new List<string> { T } };
                    Check("rules: a name with spaces around it removes its entry", TechRequireAllRegistry.ApplyRules(file, new Dictionary<string, bool> { { " " + T + " ", false } }) && file.technologies.Count == 0);
                    file = new TechRequireAllFile { technologies = new List<string> { T } };
                    Check("rules: nothing to change is reported as no change",
                          !TechRequireAllRegistry.ApplyRules(file, new Dictionary<string, bool> { { T, true } }) && !TechRequireAllRegistry.ApplyRules(file, new Dictionary<string, bool> { { "Technology_X", false } }) && file.technologies.SequenceEqual(new[] { T }));
                    file = new TechRequireAllFile { technologies = new List<string> { "Technology_A" } };
                    Check("rules: one name added and another removed is a change, though the count is the same",
                          TechRequireAllRegistry.ApplyRules(file, new Dictionary<string, bool> { { "Technology_A", false }, { "Technology_B", true } }) && file.technologies.SequenceEqual(new[] { "Technology_B" }));

                    VCall("SetRule", N(S), true); VCall("CommitChanges");
                    string held = File.ReadAllText(TechRequireAllRegistry.SourcePath);
                    var outcome = TechRequireAllRegistry.SetRules(new Dictionary<string, bool> { { S, true } }, out bool changed);
                    Check("rules: a switch the list already holds is Saved, NOT changed, and the file is as it was",
                          Deployed == S && outcome == RegistryRules.SaveOutcome.Saved && !changed && File.ReadAllText(TechRequireAllRegistry.SourcePath) == held, outcome + " changed " + changed);

                    // the list breaks between the viewer's write and its re-read: what was written must not stay staged
                    VCall("SetRule", N(T), true);
                    var realTree = TechTreeWindow.BuildTreeImpl;
                    TechTreeWindow.BuildTreeImpl = path => { goodSource = File.ReadAllText(TechRequireAllRegistry.SourcePath); File.WriteAllText(TechRequireAllRegistry.SourcePath, "{ broken"); return realTree(path); };
                    VCall("CommitChanges");
                    TechTreeWindow.BuildTreeImpl = realTree;
                    Check("save: a written switch leaves the staged set even when the list cannot be read back", Deployed == S + "," + T && Staged == 0 && VGet<bool>("_rulesUnreadable"), Deployed + " staged " + Staged);
                    File.WriteAllText(TechRequireAllRegistry.SourcePath, goodSource);
                    VCall("Reload", true, true);
                    Check("    and after the repair both are under AND", !VGet<bool>("_rulesUnreadable") && All(S) && All(T));
                    errors.RemoveAll(e => e.Contains("[TechAll]"));   // the broken list, as in step 8

                    // the list's window asks open viewers to draw again
                    int asked = TechTreeWindow.RepaintRequests;
                    Call("Reload");
                    Check("list window: its reload asks the open viewer to draw again", TechTreeWindow.RepaintRequests > asked);

                    // readability changes under the SAME version: emptied by hand while the game's copy still has entries
                    File.WriteAllText(TechRequireAllRegistry.SourcePath, "{\n    \"technologies\": []\n}");
                    VCall("Reload", true, true);
                    Check("watch: a list emptied by hand while the game's copy has entries cannot be used", VGet<bool>("_rulesUnreadable") && TechRequireAllRegistry.LastLoadFailed);
                    watchedVersion = TechRequireAllRegistry.LoadedVersion;
                    string accepted = TechRequireAllRegistry.BannerActions.AcceptSource(watchedVersion);   // the list window's "Keep it empty"
                    Call("Reload");                                                                        // ... and its reload
                    Check("    'Keep it empty' in the list's window: usable again, under the same version", !TechRequireAllRegistry.LastLoadFailed && TechRequireAllRegistry.LoadedVersion == watchedVersion && watchedVersion != null, accepted);
                    errors.RemoveAll(e => e.Contains("[TechAll]"));
                    break;
                }
                case 14:
                {
                    errors.RemoveAll(e => e.Contains("[TechAll]"));
                    Check("watch: the viewer's next draw reads the list again when only its readability changed", !VGet<bool>("_rulesUnreadable") && !All("Technology_S"));
                    // staleness: the list gains T outside the editor; the game's copy is the older, empty one
                    File.WriteAllText(TechRequireAllRegistry.SourcePath, "{\n    \"technologies\": [\n        \"Technology_T\"\n    ]\n}");
                    VCall("Reload", true, true);
                    Check("watch: a list changed outside the editor is shown as one the game does not read yet", VGet<bool>("_rulesStale") && All("Technology_T") && Deployed == "", "stale " + VGet<bool>("_rulesStale") + " deployed " + Deployed);
                    string deployed = TechRequireAllRegistry.BannerActions.AcceptSource(TechRequireAllRegistry.LoadedVersion);   // the list window's "Deploy the source"
                    Call("Reload");
                    Check("    'Deploy the source' in the list's window deploys it", Deployed == "Technology_T" && !TechRequireAllRegistry.LastLoadStale, deployed);
                    errors.RemoveAll(e => e.Contains("[TechAll]"));
                    break;
                }
                case 15:
                    errors.RemoveAll(e => e.Contains("[TechAll]"));
                    Check("watch: the viewer's next draw drops the stale warning", !VGet<bool>("_rulesStale") && All("Technology_T"));
                    break;
                default:
                    Check("no error or exception was logged while the windows drew", errors.Count == 0, string.Join(" | ", errors));
                    EditorApplication.update -= Step; window.Close(); if (viewer != null) viewer.Close(); Finish(null);
                    break;
            }
        }
        catch (Exception ex) { EditorApplication.update -= Step; Finish(ex); }
    }

    static void Finish(Exception ex)
    {
        if (ex != null) { failed++; log.AppendLine("FAIL " + ex); }
        // the registry engine's bookkeeping for the temp paths (EditorPrefs is shared by every project on the machine)
        try
        {
            EditorPrefs.DeleteKey("HAF.Registry.LastWrite|" + TechRequireAllRegistry.SourcePath);
            EditorPrefs.DeleteKey("HAF.Registry.PendingDeploy|" + TechRequireAllRegistry.SourcePath);
            EditorPrefs.DeleteKey("HAF.TechRequireAll.SingleSource|" + TechRequireAllRegistry.RegistryPath);
        }
        catch { }
        log.AppendLine("TOTAL failed " + failed);
        File.WriteAllText(output, log.ToString()); Debug.Log(log.ToString());
        EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}

// Stands in for the game's TechnologyDefinition where the drill writes a visibility: the same field name and the same
// kind of field (a flags enum with the game's four values).
public class TechTreeDrillDefinition : ScriptableObject
{
    [Flags] public enum Flags { Never = 0, TechnologyScreen = 1, ControlBannerTechnologyPopup = 2, Always = -1 }
    [Serializable] public struct Prerequisite { public string[] SerializableTechnologyNames; }   // a struct, as the game's is
    public Flags Visibility = Flags.Always;
    public Prerequisite TechnologyPrerequisite;
}

// Stands in for the game's TechnologyUIMapper: the two fields a position is written to.
public class TechTreeDrillMapper : ScriptableObject
{
    public int TechTreeX, TechTreeY;
}
