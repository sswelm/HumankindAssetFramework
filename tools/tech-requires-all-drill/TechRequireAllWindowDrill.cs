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
public static class TechRequireAllWindowDrill
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static string output, config;
    static readonly System.Text.StringBuilder log = new System.Text.StringBuilder();
    static readonly List<string> errors = new List<string>();
    static int failed, step, draws;
    static double deadline;
    static TechRequireAllWindow window;

    static void Check(string name, bool ok, string detail = "") { log.AppendLine((ok ? "PASS " : "FAIL ") + name + (ok || detail == "" ? "" : "   [" + detail + "]")); if (!ok) failed++; }
    static T Get<T>(string field) => (T)typeof(TechRequireAllWindow).GetField(field, Private).GetValue(window);
    static void Set(string field, object value) => typeof(TechRequireAllWindow).GetField(field, Private).SetValue(window, value);
    static void Call(string method, params object[] args) => typeof(TechRequireAllWindow).GetMethod(method, Private).Invoke(window, args);
    static bool Dirty => (bool)typeof(TechRequireAllWindow).GetProperty("Dirty", Private).GetValue(window, null);
    static string Artifact => Path.Combine(config, TechRequireAllRegistry.ArtifactName);

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

    static void InjectTree()
    {
        var nodes = new List<TechTreeData.Node>
        {
            Node("Technology_A", "Alpha"), Node("Technology_B", "Beta"),
            Node("Technology_T", "Two Parents", "Technology_A", "Technology_B"),
            Node("Technology_S", "Single Parent", "Technology_A"),
        };
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
                default:
                    Check("no error or exception was logged while the window drew", errors.Count == 0, string.Join(" | ", errors));
                    EditorApplication.update -= Step; window.Close(); Finish(null);
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
