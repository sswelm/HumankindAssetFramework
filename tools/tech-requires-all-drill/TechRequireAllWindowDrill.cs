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
// (TechTreeData.Build over a real modding project) is NOT exercised here.
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
    static string goodSource;

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
            tree[1].BaseX = 8; tree[2].BaseX = 16; tree[2].BaseY = 4; tree[3].BaseX = 16; tree[3].BaseY = 9;
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
                    VCall("SetRule", N("Technology_T"), true);
                    Check("viewer: the switch to AND is staged, nothing is written", All("Technology_T") && Staged == 1 && Deployed == "", Deployed + " staged " + Staged);
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
                    Check("    and the viewer shows no rule for a list it cannot read", VGet<bool>("_rulesUnreadable") && TechTreeWindow.RuleHeader(true, true) == "Prerequisites:");
                    Check("    the refusal was logged", errors.Any(e => e.Contains("[TechAll]")), string.Join(" | ", errors));
                    errors.RemoveAll(e => e.Contains("[TechAll]"));
                    break;
                case 9:
                    errors.RemoveAll(e => e.Contains("[TechAll]"));   // the broken list, said again by the draws in between
                    File.WriteAllText(TechRequireAllRegistry.SourcePath, goodSource);
                    VCall("Reload", true);
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
