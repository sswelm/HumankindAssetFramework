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
    static readonly List<string> warnings = new List<string>();   // the viewer's own warnings, for the checks that expect one
    static int failed, step, draws;
    static double deadline;
    static TechRequireAllWindow window;
    static TechTreeWindow viewer;
    static Vector2 pan;
    static string goodSource, watchedVersion;
    static Func<string, List<TechTreeData.Node>> plainTree;

    static readonly List<string> saves = new List<string>();   // the viewer's "[TechTree] Saved: ..." lines
    static string LastSave => saves.Count > 0 ? saves[saves.Count - 1] : "(none)";
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

    // ---- what a technology unlocks (TechTreeUnlocks): stand-ins of the game's shapes, by the names the reader looks for
    const string IconPath = "Assets/Icons/drill_icon.png";
    static string IconGuid()   // a real image asset of the temporary project, made once
    {
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), IconPath)))
        {
            Folder("Assets/Icons");
            var pixels = new Texture2D(4, 4);
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), IconPath), pixels.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(pixels);
            AssetDatabase.ImportAsset(IconPath);
        }
        return AssetDatabase.AssetPathToGUID(IconPath);
    }
    // T's card artwork: a file of its own (the unlock row's check asks whether THE ROW loaded its image), opaque red,
    // so that a pixel says whether the artwork or something of the card is on top
    const string ArtPath = "Assets/Icons/drill_art.png", BluePath = "Assets/Icons/drill_blue.png";
    static void MakeArt() { MakePicture(ArtPath, new Color32(255, 0, 0, 255)); MakePicture(BluePath, new Color32(0, 0, 255, 255)); }
    static void MakePicture(string assetPath, Color32 colour)
    {
        string file = Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath);
        if (File.Exists(file)) return;
        Folder("Assets/Icons");
        var pixels = new Texture2D(8, 8, TextureFormat.RGBA32, false);
        pixels.SetPixels32(Enumerable.Repeat(colour, 64).ToArray());
        File.WriteAllBytes(file, pixels.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(pixels);
        AssetDatabase.ImportAsset(assetPath);
    }
    static string BlueGuid => AssetDatabase.AssetPathToGUID(BluePath);
    static bool Blue(Color32 c) => c.b > 180 && c.r < 70 && c.g < 70;
    static Func<List<TechTreeData.Era>> productionEras;   // the window's own reader, before the drill replaced it
    static List<TechTreeData.Era> DrillEras() => Enumerable.Range(0, 4).Select(i => new TechTreeData.Era { Name = "Era" + i, Index = i }).ToList();
    // the viewer's pixels as its last repaint left them (TechTreeWindow.AfterCanvasDrawn), rows from the bottom
    static Color32[] shot; static int shotWidth, shotHeight; static float shotScale;
    static void Capture(TechTreeWindow w)
    {
        shotScale = EditorGUIUtility.pixelsPerPoint;
        shotWidth = Mathf.RoundToInt(w.position.width * shotScale); shotHeight = Mathf.RoundToInt(w.position.height * shotScale);
        var read = new Texture2D(shotWidth, shotHeight, TextureFormat.RGB24, false);
        read.ReadPixels(new Rect(0, 0, shotWidth, shotHeight), 0, 0); read.Apply();
        shot = read.GetPixels32(); UnityEngine.Object.DestroyImmediate(read);
    }
    // the pixels of a small square around a point of the window (in the window's own coordinates, y down)
    static List<Color32> Around(Vector2 point, int reach)
    {
        var found = new List<Color32>();
        int cx = Mathf.RoundToInt(point.x * shotScale), cy = shotHeight - 1 - Mathf.RoundToInt(point.y * shotScale);
        for (int y = cy - reach; y <= cy + reach; y++)
            for (int x = cx - reach; x <= cx + reach; x++)
                if (x >= 0 && y >= 0 && x < shotWidth && y < shotHeight) found.Add(shot[y * shotWidth + x]);
        return found;
    }
    static bool Red(Color32 c) => c.r > 180 && c.g < 70 && c.b < 70;

    // a Unity GUID text as the four ints a UI mapper stores (the reverse of TechTreeData.AmplitudeGuidToUnity)
    static TechTreeDrillTypes.DrillImage Image(string key, string unityGuid)
    {
        var words = new int[4];
        if (unityGuid != null)
            for (int w = 0; w < 4; w++)
                for (int b = 0; b < 4; b++)
                {
                    int low = Convert.ToInt32(unityGuid.Substring(w * 8 + b * 2, 1), 16), high = Convert.ToInt32(unityGuid.Substring(w * 8 + b * 2 + 1, 1), 16);
                    words[w] |= ((high << 4) | low) << (8 * b);
                }
        return new TechTreeDrillTypes.DrillImage { serializableKey = key, Value = new TechTreeDrillTypes.DrillTexture { guid = new TechTreeDrillTypes.DrillGuid { a = words[0], b = words[1], c = words[2], d = words[3] } } };
    }
    static T Constructible<T>(string name, int level, TechTreeDrillTypes.FactionOperator op, params string[] factions) where T : Amplitude.Mercury.Data.Simulation.ConstructibleDefinition
    {
        var c = ScriptableObject.CreateInstance<T>(); c.name = name; c.Level = level; c.FactionPrerequisite = new TechTreeDrillTypes.FactionPrerequisite(op, factions); return c;
    }
    // a plain UI mapper, and a constructible's (the one with a SortPriority)
    static TechTreeDrillTypes.DrillMapper Mapper(string name, string title, params TechTreeDrillTypes.DrillImage[] images)
    {
        var m = ScriptableObject.CreateInstance<TechTreeDrillTypes.DrillMapper>(); m.name = name; m.Title = title; m.Images = images.ToList(); return m;
    }
    static TechTreeDrillTypes.DrillMapper CMapper(string name, string title, int sortPriority, params TechTreeDrillTypes.DrillImage[] images)
    {
        var m = ScriptableObject.CreateInstance<TechTreeDrillTypes.DrillConstructibleMapper>(); m.name = name; m.Title = title; m.SortPriority = sortPriority; m.Images = images.ToList(); return m;
    }
    static TechTreeDrillTypes.Reference Ref(string name) => new TechTreeDrillTypes.Reference(name);
    static TechTreeDrillTypes.Reference[] Refs(params string[] names) => names.Select(n => new TechTreeDrillTypes.Reference(n)).ToArray();
    static TechTreeDrillTypes.TechDefinition Effects(params object[] effects) => new TechTreeDrillTypes.TechDefinition { SimulationEventEffects = effects };
    static TechTreeDrillTypes.SimulationEventEffect_UnlockConstructible Unlocks(params string[] names) => new TechTreeDrillTypes.SimulationEventEffect_UnlockConstructible { ConstructibleReferences = Refs(names) };
    static string Names(IEnumerable<TechTreeUnlocks.Unlock> entries) => string.Join(",", entries.Select(u => u.ElementName == "" ? "(none)" : u.ElementName));
    // a texture's pixels through the graphics card (the icon copies are not readable from C#)
    static Color32[] PixelsOf(Texture texture)
    {
        var before = RenderTexture.active;
        var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(texture, target);
        RenderTexture.active = target;
        var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
        read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false); read.Apply();
        RenderTexture.active = before; RenderTexture.ReleaseTemporary(target);
        var pixels = read.GetPixels32(); UnityEngine.Object.DestroyImmediate(read);
        return pixels;
    }

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
        if (map != null)
        {
            node.BaseX = map.TechTreeX; node.BaseY = map.TechTreeY;
            node.TitleKey = map.Title; if (!string.IsNullOrEmpty(map.Title)) node.Label = map.Title;
            node.BaseDescription = node.Description = map.Description ?? "";
            node.ArtPath = TechTreeData.ImageOf(map, new[] { TechTreeData.CardImageKey }, null, out node.ArtNamed, out node.ArtGuid);
        }
        if (def != null) { node.BasePrereqs = NamesOf(def).ToArray(); node.BaseVisibility = TechTreeData.ReadVisibility(def); node.Era = TechTreeData.ReadEra(def); }
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
            // eras: A and B in the first, T and S in the second; T's card has an artwork (a file of its own)
            tree[0].Era = "Era1"; tree[1].Era = "Era1"; tree[2].Era = "Era2"; tree[3].Era = "Era2";
            tree[2].ArtPath = ArtPath; tree[2].ArtNamed = true; tree[2].ArtGuid = AssetDatabase.AssetPathToGUID(ArtPath);
            // T's card carries an icon row: a project image, an entry with a blank icon, one the game does not draw,
            // more entries than fit, and one only another culture gets
            tree[2].Unlocks = new List<TechTreeUnlocks.Unlock>
            {
                new TechTreeUnlocks.Unlock { Effect = "UnlockConstructible", Kind = TechTreeUnlocks.Kind.Unit, ElementName = "Unit_X", MapperName = "Unit_X", Title = "Unit X", MapperFound = true, ImagePath = IconPath, ImageGuid = IconGuid() },
                new TechTreeUnlocks.Unlock { Effect = "ApplyDescriptor", Kind = TechTreeUnlocks.Kind.Other, ElementName = "Bonus_Y", MapperName = "Bonus_Y", Title = "Bonus Y" },
                new TechTreeUnlocks.Unlock { Effect = "UnlockAction", Kind = TechTreeUnlocks.Kind.Other, ElementName = "Action_Z", MapperName = "ActionTypeAction_Z", Title = "Action_Z", NotShown = "there is no UI mapper 'ActionTypeAction_Z'" },
            };
            for (int extra = 0; extra < 6; extra++)
                tree[2].Unlocks.Add(new TechTreeUnlocks.Unlock { Effect = "UnlockConstructible", Kind = extra % 2 == 0 ? TechTreeUnlocks.Kind.Extension : TechTreeUnlocks.Kind.Infrastructure, ElementName = "C" + extra, MapperName = "C" + extra, Title = "C" + extra, MapperFound = true });
            tree[2].Unlocks.Add(new TechTreeUnlocks.Unlock { Effect = "UnlockConstructible", Kind = TechTreeUnlocks.Kind.Unit, ElementName = "Unit_Q", MapperName = "Unit_Q", Title = "Unit Q", MapperFound = true, OnlyFor = new[] { "Civilization_Q" } });
            return tree;
        };
        MakeArt();
        if (productionEras == null) productionEras = TechTreeWindow.ErasImpl;
        TechTreeWindow.ErasImpl = DrillEras;
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
                if (type == LogType.Warning && msg.StartsWith("[TechTree]")) warnings.Add(msg);
                if (type == LogType.Log && msg.StartsWith("[TechTree] Saved:")) saves.Add(msg);
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
                    VCall("SetPosition", N(T), 20, 6); VCall("SetVisibility", N(T), 0); VCall("SetRule", N(T), true); VCall("RemovePrereq", N(T), "Technology_B"); VCall("SetEra", N(T), "Era3");
                    VCall("SetTitle", N(T), "Steam Power"); VCall("SetDescription", N(T), "Makes\nsteam."); VCall("SetImage", N(T), BlueGuid);
                    var fullTree = TechTreeWindow.BuildTreeImpl;
                    TechTreeWindow.BuildTreeImpl = _ => fullTree(null).Where(n => n.Name != T).ToList();
                    VCall("Reload", true, false);
                    Check("a reload that does not find a technology keeps its staged edits", Staged == 1, "staged " + Staged);
                    VCall("CommitChanges");
                    Check("    a save while it is absent writes nothing of it and keeps it staged", Staged == 1 && Deployed == "", Deployed + " staged " + Staged);
                    TechTreeWindow.BuildTreeImpl = fullTree;
                    VCall("Reload", true, false);
                    Check("    and with the technology back the same edits apply", (int)VCall("EffX", N(T)) == 20 && (int)VCall("EffY", N(T)) == 6 && (int)VCall("EffVisibility", N(T)) == 0 && All(T) && Prereqs(T).SequenceEqual(new[] { "Technology_A" }) && (string)VCall("EffEra", N(T)) == "Era3" && Staged == 1);
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
                          && (string)VCall("EffEra", N(T)) == "Era3" && viewer.EffLabel(N(T)) == "Steam Power" && (string)VCall("EffDescription", N(T)) == "Makes\nsteam." && viewer.EffImage(N(T)) == BlueGuid
                          && ReferenceEquals(VGet<TechTreeData.Node>("_selected"), N(T)) && VGet<Vector2>("_pan") == panWas && VGet<float>("_zoom") == zoomWas,
                          "staged " + Staged + " selected " + (VGet<TechTreeData.Node>("_selected")?.Name ?? "none") + " pan " + VGet<Vector2>("_pan") + " zoom " + VGet<float>("_zoom"));
                    VCall("CommitChanges");   // the rule is written; position, prerequisites and visibility have no asset here and stay
                    Check("save: the parts without an asset are counted as skipped in the Save line, the era with them",
                          LastSave.Contains(" · 7 skipped (no writable target"), LastSave);
                    Check("save: a saved rule switch leaves the staged set, the parts without an asset stay",
                          Deployed == T && Staged == 1 && (int)VCall("EffX", N(T)) == 20 && (int)VCall("EffVisibility", N(T)) == 0 && (string)VCall("EffEra", N(T)) == "Era3"
                          && VGet<HashSet<string>>("_requireAll").Contains(T), Deployed + " staged " + Staged);
                    VCall("SetPosition", N(T), 16, 4); VCall("SetVisibility", N(T), -1); VCall("AddPrereq", N(T), "Technology_B");
                    Check("    the card shows the staged title; typed back to the saved title, description and image, they are no change",
                          viewer.EffLabel(N(T)) == "Steam Power" && N(T).Label == "Two Parents" && viewer.CardTitle(N(T)) == "STEAM POWER", viewer.CardTitle(N(T)));
                    VCall("SetTitle", N(T), ""); VCall("SetDescription", N(T), ""); VCall("SetImage", N(T), N(T).ArtGuid);
                    N(T).Tier = "Tier2";
                    Check("    the era alone is still staged, and it is the era the card shows", Staged == 1 && (string)VCall("EffEra", N(T)) == "Era3" && N(T).Era == "Era2" && viewer.CardEraTier(N(T)) == "E3 · T2", "staged " + Staged + " card " + viewer.CardEraTier(N(T)));
                    VCall("SetEra", N(T), "Era2");
                    Check("    the saved era again on the card, and no tier is no dot", viewer.CardEraTier(N(T)) == "E2 · T2" && viewer.CardEraTier(N("Technology_A")) == "E1", viewer.CardEraTier(N(T)) + " / " + viewer.CardEraTier(N("Technology_A")));
                    VCall("SetEra", N(T), "");
                    Check("    no era: the tier alone, no dot", viewer.CardEraTier(N(T)) == "T2", viewer.CardEraTier(N(T)));
                    VCall("SetEra", N(T), "Era2");
                    var erasInWindow = VGet<List<TechTreeData.Era>>("_eras");
                    Check("the window holds the project's eras by index", string.Join(",", erasInWindow.Select(e => e.Name)) == "Era0,Era1,Era2,Era3", string.Join(",", erasInWindow.Select(e => e.Name)));
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

                    // ---- Shift: every card right of one card, moved together (A 0,0 · B 8,-2 · T 16,4 · S 16,9) ----
                    {
                        int applied; var shiftArgs = new object[] { N(B), 5, 0 };
                        int moved = (int)typeof(TechTreeWindow).GetMethod("ShiftRightOf", Private).Invoke(viewer, shiftArgs);
                        Check("shift: the cards right of B move 5 columns, B and the card left of it stay",
                              moved == 2 && (int)shiftArgs[2] == 5 && (int)VCall("EffX", N(T)) == 21 && (int)VCall("EffX", N("Technology_S")) == 21 && (int)VCall("EffY", N(T)) == 4
                              && (int)VCall("EffX", N(B)) == 8 && (int)VCall("EffX", N("Technology_A")) == 0 && Staged == 2, "moved " + moved + " staged " + Staged);
                        shiftArgs = new object[] { N(B), -5, 0 };
                        typeof(TechTreeWindow).GetMethod("ShiftRightOf", Private).Invoke(viewer, shiftArgs);
                        Check("shift: the opposite number takes it back, nothing staged", (int)VCall("EffX", N(T)) == 16 && (int)VCall("EffX", N("Technology_S")) == 16 && Staged == 0, "staged " + Staged);
                        shiftArgs = new object[] { N(B), -20, 0 };
                        typeof(TechTreeWindow).GetMethod("ShiftRightOf", Private).Invoke(viewer, shiftArgs);
                        applied = (int)shiftArgs[2];
                        Check("shift to the left stops in the column after the anchor's (7 of the 20 asked)", applied == -7 && (int)VCall("EffX", N(T)) == 9 && (int)VCall("EffX", N("Technology_S")) == 9, "applied " + applied);
                        shiftArgs = new object[] { N(B), 7, 0 };
                        typeof(TechTreeWindow).GetMethod("ShiftRightOf", Private).Invoke(viewer, shiftArgs);
                        Check("    and what was applied, reversed, takes it back", Staged == 0 && (int)VCall("EffX", N(T)) == 16, "staged " + Staged);
                        shiftArgs = new object[] { N(T), 5, 0 };
                        moved = (int)typeof(TechTreeWindow).GetMethod("ShiftRightOf", Private).Invoke(viewer, shiftArgs);
                        Check("shift: a card in the anchor's own column is not right of it", moved == 0 && Staged == 0 && (int)VCall("EffX", N("Technology_S")) == 16);
                        Check("shift: the field starts at 6 columns, one card's step in the tree", TechTreeWindow.DefaultShift == 6 && VGet<int>("_shiftBy") == 6);
                        Check("side strip: its rows are laid out to the strip's width, and it never scrolls sideways",
                              TechTreeWindow.SideContentWidth(300) == 272 && VGet<Vector2>("_sideScroll").x == 0);
                        Check("shift: the limit as a function",
                              TechTreeWindow.ShiftLimit(8, new[] { 16, 16 }, -20) == -7 && TechTreeWindow.ShiftLimit(8, new[] { 9, 30 }, -3) == 0 && TechTreeWindow.ShiftLimit(8, new int[0], -3) == 0
                              && TechTreeWindow.ShiftLimit(8, new[] { 16 }, 5) == 5 && TechTreeWindow.ShiftLimit(8, new[] { 16 }, -4) == -4);
                        Check("shift: what the side strip says about it",
                              TechTreeWindow.ShiftLabel(87, 5) == "Move the 87 cards right of this by 5" && TechTreeWindow.ShiftLabel(0, 5) == "No card is right of this one"
                              && TechTreeWindow.ShiftNote(87, 5, 5) == "Moved 87 cards 5 columns to the right. Staged: Save writes it, -5 takes it back."
                              && TechTreeWindow.ShiftNote(2, -20, -7) == "Moved 2 cards 7 columns to the left. Staged: Save writes it, +7 takes it back. (-20 was asked: the nearest card stops in the column after this one.)"
                              && TechTreeWindow.ShiftNote(1, -3, 0).StartsWith("Nothing moved"),
                              TechTreeWindow.ShiftNote(2, -20, -7));
                    }

                    // the Save line says what the outcome guarantees
                    Check("save line: a switch the list already held is not called deployed",
                          TechTreeWindow.RuleNote(1, RegistryRules.SaveOutcome.Saved, false, false) == ", 1 rule switch (OR / AND): the list already held it, so nothing was written or deployed",
                          TechTreeWindow.RuleNote(1, RegistryRules.SaveOutcome.Saved, false, false));
                    Check("save line: written, refused and unsettled each say their own thing",
                          TechTreeWindow.RuleNote(2, RegistryRules.SaveOutcome.Saved, true, false).EndsWith("relaunch the game to apply")
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
                    NewAsset<TechTreeDrillDefinition>(DbDefs + "/" + M + ".asset", d => { d.TechnologyPrerequisite.SerializableTechnologyNames = new[] { "Technology_A", "Technology_B" }; d.EraReference = new TechTreeDrillTypes.Reference("Era1"); });
                    NewAsset<TechTreeDrillMapper>(DbMaps + "/" + M + ".asset", m =>
                    {
                        m.TechTreeX = 24; m.TechTreeY = 2; m.Title = "Em"; m.Description = "Em's text.";
                        m.Images = new List<TechTreeDrillTypes.DrillImage> { Image("TechnologyPopupBackground", AssetDatabase.AssetPathToGUID(ArtPath)), Image("Picto", "0123456789abcdef0123456789abcdef"), Image("TechnologyScreenItemBackground", AssetDatabase.AssetPathToGUID(ArtPath)) };
                    });
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
                    // ---- era separators, from the cards the game's screen shows (the game's BindAllSeparators) ----
                    // Only M and R have a UI mapper. M is in Era1 at column 24, R has no era yet. T (Era2) has no mapper.
                    const string R0 = "Technology_R";
                    Check("assets: the era is read from the definition", N(M).Era == "Era1" && N(R0).Era == "" && (string)VCall("EffEra", N(M)) == "Era1");
                    var none = viewer.EraSeparatorsNow();
                    Check("era separators: a technology without a UI mapper has no card in the game, so none comes from it (T is in Era2, M in Era1)", none.Count == 0, none.Count.ToString());
                    VCall("SetEra", N(R0), "Era2"); VCall("SetPosition", N(R0), 40, 8);
                    var one = viewer.EraSeparatorsNow();
                    // M's right edge 24 * 74 + 310 = 2086, R's left edge 40 * 74 = 2960: halfway 2523
                    Check("era separators: one between two eras that both have a card, halfway between the earlier era's right end and the later one's left end, from the STAGED era and column",
                          one.Count == 1 && one[0].LeftEra == 1 && Mathf.Abs(one[0].X - 2523f) < 0.01f,
                          one.Count == 1 ? one[0].LeftEra + " at " + one[0].X : one.Count.ToString());
                    // ... and as the canvas DRAWS it (review C: no check observed the drawing). Zoom 0.5 and a pan that puts the
                    // separator inside the canvas: everything the drawing records must follow the view.
                    var viewWas = VGet<Vector2>("_pan"); float zoomWas = VGet<float>("_zoom");
                    var drawPan = new Vector2(300f - 2523f * 0.5f, 40f);
                    VSet("_zoom", 0.5f); VSet("_pan", drawPan);
                    viewer.SendEvent(new Event { type = EventType.Layout }); viewer.SendEvent(new Event { type = EventType.Repaint });
                    var drawn = viewer.SeparatorDraws;
                    var d0 = drawn.Count == 1 ? drawn[0] : null;
                    Vector2 At(Vector2 unit) => unit * 0.5f + drawPan;   // the game's units to the window, as the view says
                    Vector2 lineTopWant = At(TechTreeWindow.EraLinePoint(2523f, -56f)), lineBottomWant = At(TechTreeWindow.EraLinePoint(2523f, 928f));
                    // The numerals are pinned to the top of the view (the user, 2026-11-22), beside the line where it crosses
                    // that height: the line's own place there is read from the line's two ends the drawing recorded.
                    float canvasTop = ((Vector2)typeof(TechTreeWindow).GetField("_artOrigin", Private).GetValue(viewer)).y;
                    float LineXAt(TechTreeWindow.SeparatorDraw d, float y) => Mathf.Lerp(d.LineTop.x, d.LineBottom.x, Mathf.InverseLerp(d.LineTop.y, d.LineBottom.y, y));
                    bool Beside(TechTreeWindow.SeparatorDraw d, float gap) =>
                        d.LeftLabel.y == d.RightLabel.y && Mathf.Abs((d.LeftLabel.x + d.RightLabel.x) * 0.5f - LineXAt(d, d.LeftLabel.y)) < 0.05f && Mathf.Abs(d.RightLabel.x - d.LeftLabel.x - gap) < 0.05f;
                    Check("era separators, drawn: under the cards, the line from 56 above the first row to 928 where the view puts it; the numerals I and II either side of it, at the game's size (100 at zoom 1), at the TOP of the view",
                          d0 != null && d0.LeftEra == 1 && d0.CardsDrawnBefore == 0 && (d0.LineTop - lineTopWant).magnitude < 0.01f && (d0.LineBottom - lineBottomWant).magnitude < 0.01f
                          && d0.LeftText == "I" && d0.RightText == "II" && d0.FontSize == 50 && Beside(d0, 139f) && d0.LeftLabel.y - canvasTop > 4f && d0.LeftLabel.y - canvasTop < 4f + 32.5f + 0.05f,
                          d0 == null ? "drawn " + drawn.Count : "top " + d0.LineTop + " want " + lineTopWant + " left " + d0.LeftText + "@" + d0.LeftLabel + " right " + d0.RightText + "@" + d0.RightLabel
                          + " size " + d0.FontSize + " canvas top " + canvasTop + " cards before " + d0.CardsDrawnBefore);
                    Check("era separators, drawn: the line is visible and drawn piece by piece where it is inside the canvas",
                          d0 != null && d0.PiecesDrawn >= 18 && d0.MaxAlpha > 0.5f && d0.MinAlpha < d0.MaxAlpha * 0.2f && d0.Width >= 1f,
                          d0 == null ? "none" : d0.PiecesDrawn + " pieces, alpha " + d0.MinAlpha + ".." + d0.MaxAlpha + " width " + d0.Width);
                    float pinnedY = d0 != null ? d0.LeftLabel.y : float.NaN;
                    VSet("_pan", drawPan + new Vector2(0f, -150f));   // scrolled down: the tree moves up, the numerals stay
                    viewer.SendEvent(new Event { type = EventType.Layout }); viewer.SendEvent(new Event { type = EventType.Repaint });
                    var scrolled = viewer.SeparatorDraws.Count == 1 ? viewer.SeparatorDraws[0] : null;
                    Check("era separators, drawn: scrolled up or down, the numerals stay at the top of the view, still beside the line",
                          scrolled != null && Mathf.Abs(scrolled.LeftLabel.y - pinnedY) < 0.01f && Mathf.Abs(scrolled.LineTop.y - (d0.LineTop.y - 150f)) < 0.01f && Beside(scrolled, 139f),
                          scrolled == null ? "none" : scrolled.LeftLabel + " was y " + pinnedY + " line top " + scrolled.LineTop);
                    VSet("_pan", drawPan + new Vector2(0f, 260f));    // the line starts below the view's top: the numerals stay at its top end
                    viewer.SendEvent(new Event { type = EventType.Layout }); viewer.SendEvent(new Event { type = EventType.Repaint });
                    var below = viewer.SeparatorDraws.Count == 1 ? viewer.SeparatorDraws[0] : null;
                    Check("era separators, drawn: where the line begins below the view's top, the numerals sit at the line's top end",
                          below != null && below.LineTop.y > canvasTop + 40f && Mathf.Abs(below.LeftLabel.y - below.LineTop.y) < 0.01f && Beside(below, 139f),
                          below == null ? "none" : below.LeftLabel + " line top " + below.LineTop);
                    VSet("_zoom", 0.05f); VSet("_pan", new Vector2(300f - 2523f * 0.05f, 40f));
                    viewer.SendEvent(new Event { type = EventType.Layout }); viewer.SendEvent(new Event { type = EventType.Repaint });
                    var far = viewer.SeparatorDraws.Count == 1 ? viewer.SeparatorDraws[0] : null;
                    Check("era separators, drawn: zoomed far out the numerals keep a readable size (14) and are less faint than the game's; the line is drawn too",
                          far != null && far.LeftText == "I" && far.FontSize == 14 && far.LabelAlpha > 0.5f && far.PiecesDrawn > 0 && Beside(far, 2f * 139f * 0.14f),
                          far == null ? "none" : "size " + far.FontSize + " alpha " + far.LabelAlpha + " gap " + (far.RightLabel.x - far.LeftLabel.x));
                    VSet("_pan", new Vector2(-100000f, 40f));
                    viewer.SendEvent(new Event { type = EventType.Layout }); viewer.SendEvent(new Event { type = EventType.Repaint });
                    Check("era separators, drawn: a separator outside the canvas draws no piece of its line", viewer.SeparatorDraws.Count == 1 && viewer.SeparatorDraws[0].PiecesDrawn == 0);
                    VSet("_zoom", zoomWas); VSet("_pan", viewWas);
                    VCall("SetVisibility", N(R0), 2);
                    Check("    a technology in the game but not on its tech screen (Visibility without the TechnologyScreen bit) has no card: no separator", viewer.EraSeparatorsNow().Count == 0);
                    VCall("SetVisibility", N(R0), 0);
                    Check("    a technology switched off has no card: no separator", viewer.EraSeparatorsNow().Count == 0);
                    VCall("SetVisibility", N(R0), -1); VCall("SetEra", N(R0), ""); VCall("SetPosition", N(R0), 24, 8);
                    Check("    typed back: nothing staged", Staged == 0, "staged " + Staged);

                    // ---- the project's eras as the window reads them (TechTreeData.Eras, not the drill's seam): the copy under
                    // Databases wins over the reference folder's, which wins over any other; by index; the cost per tier
                    Folder("Assets/Databases/Eras"); Folder("Assets/VanillaReference/Eras"); Folder("Assets/Elsewhere");
                    void EraAsset(string path, int index, params string[] costs) => NewAsset<Amplitude.Mercury.Data.Simulation.EraDefinition>(path, e =>
                    {
                        e.EraIndex = index;
                        e.TechnologyCostRpnPerTier = costs.Select((c, i) => new Amplitude.Mercury.Data.Simulation.EraDefinition.TechnologyCostAndTier { Tier = (Amplitude.Mercury.Data.Simulation.TechnologyTiers)i, TechnologyCostRpnReference = new TechTreeDrillTypes.Reference(c) })
                                                       .Where(c => c.TechnologyCostRpnReference.ElementName != "-").ToArray();
                    });
                    EraAsset("Assets/Databases/Eras/EraB.asset", 2, "B_T1", "", "B_T3");
                    EraAsset("Assets/VanillaReference/Eras/EraB.asset", 5, "wrong", "wrong", "wrong");
                    EraAsset("Assets/VanillaReference/Eras/EraA.asset", 1, "A_T1");
                    EraAsset("Assets/Elsewhere/EraA.asset", 7, "wrong");
                    EraAsset("Assets/Elsewhere/EraC.asset", 0, "-", "C_T2");
                    AssetDatabase.SaveAssets();
                    var read = TechTreeData.Eras("Assets/VanillaReference");
                    string Shown(TechTreeData.Era e) => e.Name + "=" + e.Index + "[" + string.Join("|", e.TierCosts ?? new string[0]) + "]";
                    var fromWindow = productionEras();
                    var fromReader = TechTreeData.Eras(TechTreeData.ReferenceFolder);   // the machine's reference folder, read only
                    Check("eras: the window's own reader is TechTreeData.Eras (the drill puts its own in after keeping it)",
                          fromWindow.Count > 0 && string.Join(" ", fromWindow.Select(Shown)) == string.Join(" ", fromReader.Select(Shown)), string.Join(" ", fromWindow.Select(Shown)) + " / " + string.Join(" ", fromReader.Select(Shown)));
                    Check("eras, read from the project: Databases over the reference folder over elsewhere, by index, each with its cost per tier (by the entry's Tier, empty where none)",
                          string.Join(" ", read.Select(Shown)) == "EraC=0[|C_T2] EraA=1[A_T1] EraB=2[B_T1||B_T3]", string.Join(" ", read.Select(Shown)));

                    VSet("_pan", new Vector2(-123, 45)); VSet("_zoom", 0.7f);   // a view no framing produces
                    VCall("SetPosition", N(M), 20, 6); VCall("RemovePrereq", N(M), "Technology_B"); VCall("SetVisibility", N(M), 0); VCall("SetEra", N(M), "Era3");
                    VCall("CommitChanges");
                    var mMap = Asset<TechTreeDrillMapper>(DbMaps + "/" + M + ".asset"); var mDef = Asset<TechTreeDrillDefinition>(DbDefs + "/" + M + ".asset");
                    Check("save into assets: X and Y are in the mapper, each in its own field", mMap.TechTreeX == 20 && mMap.TechTreeY == 6, mMap.TechTreeX + "," + mMap.TechTreeY);
                    Check("save into assets: prerequisites and visibility are in the definition", NamesOf(mDef).SequenceEqual(new[] { "Technology_A" }) && mDef.Visibility == TechTreeDrillDefinition.Flags.Never, string.Join(",", NamesOf(mDef)) + " " + mDef.Visibility);
                    string mFile = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), DbDefs + "/" + M + ".asset"));
                    Check("save: the Save line counts the era with the other parts", LastSave.StartsWith("[TechTree] Saved: 1 position, 1 prerequisite, 1 visibility change(s), 1 era change"), LastSave);
                    Check("save into assets: the era is in the definition's FILE, as the one string the game's reference keeps, beside the prerequisites and the visibility of the same save",
                          TechTreeData.ReadEra(mDef) == "Era3" && mFile.Contains("serializableElementName: Era3") && !mFile.Contains("Era1") && mFile.Contains("- Technology_A") && !mFile.Contains("- Technology_B") && mFile.Contains("Visibility: 0"),
                          TechTreeData.ReadEra(mDef) + " | " + mFile.Replace("\n", " ").Replace("\r", ""));
                    Check("save into assets: every written part left the staged set, and the reload reads them back",
                          Staged == 0 && (int)VCall("EffX", N(M)) == 20 && (int)VCall("EffY", N(M)) == 6 && (int)VCall("EffVisibility", N(M)) == 0 && Prereqs(M).SequenceEqual(new[] { "Technology_A" })
                          && N(M).Era == "Era3" && (string)VCall("EffEra", N(M)) == "Era3", "staged " + Staged + " era " + N(M).Era);
                    VCall("SetEra", N(M), "Era2"); VCall("CommitChanges");
                    mDef = Asset<TechTreeDrillDefinition>(DbDefs + "/" + M + ".asset");
                    Check("save into assets: an era alone is a save too, and leaves the definition's other values as they were",
                          TechTreeData.ReadEra(mDef) == "Era2" && N(M).Era == "Era2" && Staged == 0 && NamesOf(mDef).SequenceEqual(new[] { "Technology_A" }) && mDef.Visibility == TechTreeDrillDefinition.Flags.Never
                          && File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), DbDefs + "/" + M + ".asset")).Contains("serializableElementName: Era2"),
                          TechTreeData.ReadEra(mDef) + " staged " + Staged);
                    Check("save into assets: the reference's in-memory name follows the written era (the game's ElementName, rebuilt by InitializeStaticStrings)",
                          mDef.EraReference.ElementName == "Era2", mDef.EraReference.ElementName);
                    Check("the era's writer refuses what it cannot write: an asset without an era reference, and no era at all",
                          !TechTreeData.WriteEra(mMap, "Era1") && !TechTreeData.WriteEra(mDef, "") && !TechTreeData.WriteEra(null, "Era1") && TechTreeData.ReadEra(mDef) == "Era2" && TechTreeData.ReadEra(null) == "");
                    Check("save: the view is the one from before it", VGet<Vector2>("_pan") == new Vector2(-123, 45) && VGet<float>("_zoom") == 0.7f, VGet<Vector2>("_pan") + " zoom " + VGet<float>("_zoom"));
                    // A Save is not part of Undo. On the real ENC data (review round 3) Ctrl+Z after a Save took the saved
                    // positions back in memory, silently, and the next Save wrote the old values over them.
                    Undo.PerformUndo();
                    mMap = Asset<TechTreeDrillMapper>(DbMaps + "/" + M + ".asset"); mDef = Asset<TechTreeDrillDefinition>(DbDefs + "/" + M + ".asset");
                    Check("undo after a save takes nothing of the save back, the era either", mMap.TechTreeX == 20 && mMap.TechTreeY == 6 && mDef.Visibility == TechTreeDrillDefinition.Flags.Never && NamesOf(mDef).SequenceEqual(new[] { "Technology_A" })
                          && TechTreeData.ReadEra(mDef) == "Era2" && N(M).Era == "Era2"
                          && (int)VCall("EffX", N(M)) == 20 && Staged == 0, mMap.TechTreeX + "," + mMap.TechTreeY + " " + mDef.Visibility);
                    // An undo from elsewhere that does change a technology in memory is shown AND said
                    int warnedBefore = warnings.Count(w => w.Contains("Undo / Redo changed"));
                    mMap.TechTreeX = 99;   // what such an undo leaves: the object changed, the file not
                    VCall("OnUndoRedo");
                    Check("an undo that changes a technology in memory is shown and said in the Console",
                          (int)VCall("EffX", N(M)) == 99 && warnings.Count(w => w.Contains("Undo / Redo changed 1 technology in memory (Em)")) == warnedBefore + 1, string.Join(" | ", warnings.Where(w => w.Contains("Undo"))));
                    mMap.TechTreeX = 20;
                    VCall("OnUndoRedo");
                    Check("    and the wording for several", TechTreeWindow.UndoNote(3, "Em").StartsWith("Undo / Redo changed 3 technologies in memory (Em, ...): position, era, title, description, image, prerequisites or visibility are now different from the file on disk"));
                    // an undo elsewhere that changes only the era is one of those too
                    int eraWarned = warnings.Count(w => w.Contains("Undo / Redo changed 1 technology in memory (Em)"));
                    mDef.EraReference = new TechTreeDrillTypes.Reference("Era0");
                    VCall("OnUndoRedo");
                    Check("    an undo that changes only the era is said too", N(M).Era == "Era0" && warnings.Count(w => w.Contains("Undo / Redo changed 1 technology in memory (Em)")) == eraWarned + 1);
                    mDef.EraReference = new TechTreeDrillTypes.Reference("Era2");
                    VCall("OnUndoRedo");

                    // ---- the card's title, description and image, saved into the mapper (2026-11-22) ----
                    Check("assets: the title, the description and the image are read from the mapper",
                          N(M).Label == "Em" && N(M).TitleKey == "Em" && N(M).BaseDescription == "Em's text." && N(M).ArtPath == ArtPath && N(M).ArtGuid == AssetDatabase.AssetPathToGUID(ArtPath));
                    VCall("SetTitle", N(M), "Em Renamed"); VCall("SetDescription", N(M), "A new\nline."); VCall("SetImage", N(M), BlueGuid);
                    VCall("CommitChanges");
                    mMap = Asset<TechTreeDrillMapper>(DbMaps + "/" + M + ".asset");
                    string mapFile = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), DbMaps + "/" + M + ".asset"));
                    TechTreeData.UnityGuidToAmplitude(BlueGuid, out int ba, out int bb, out int bc, out int bd);
                    bool IsBlue(TechTreeDrillTypes.DrillImage i) => i.Value.guid.a == ba && i.Value.guid.b == bb && i.Value.guid.c == bc && i.Value.guid.d == bd;
                    var picto = Image("Picto", "0123456789abcdef0123456789abcdef");
                    Check("save into assets: the title and the description are in the mapper; the image in its card and popup entries, in their places; the Picto untouched",
                          mMap.Title == "Em Renamed" && mMap.Description == "A new\nline." && mMap.Images.Count == 3 && mMap.Images[0].serializableKey == "TechnologyPopupBackground" && IsBlue(mMap.Images[0])
                          && mMap.Images[1].serializableKey == "Picto" && mMap.Images[1].Value.guid.a == picto.Value.guid.a && mMap.Images[1].Value.guid.d == picto.Value.guid.d
                          && mMap.Images[2].serializableKey == "TechnologyScreenItemBackground" && IsBlue(mMap.Images[2])
                          && mapFile.Contains("Title: Em Renamed") && mapFile.Split(new[] { "a: " + ba + "\n" }, StringSplitOptions.None).Length - 1 == 2,
                          mMap.Title + " | " + string.Join(", ", mMap.Images.Select(i => i.serializableKey + "=" + i.Value.guid.a)) + " | blue a " + ba);
                    Check("save: the Save line counts the texts and the image", LastSave.Contains(", 2 title / description changes, 1 image change"), LastSave);
                    Check("save: the reload reads them back, nothing stays staged, the card shows them",
                          Staged == 0 && N(M).Label == "Em Renamed" && N(M).BaseDescription == "A new\nline." && N(M).ArtGuid == BlueGuid && N(M).ArtPath == BluePath && viewer.EffLabel(N(M)) == "Em Renamed",
                          "staged " + Staged + " label " + N(M).Label + " art " + N(M).ArtPath);
                    Undo.PerformUndo();
                    mMap = Asset<TechTreeDrillMapper>(DbMaps + "/" + M + ".asset");
                    Check("undo after that save takes nothing of it back", mMap.Title == "Em Renamed" && IsBlue(mMap.Images[2]) && N(M).Label == "Em Renamed");
                    // the writers on their own
                    var lone = ScriptableObject.CreateInstance<TechTreeDrillMapper>();
                    lone.Images = new List<TechTreeDrillTypes.DrillImage> { picto };
                    bool wrote = TechTreeData.WriteImage(lone, TechTreeData.TechnologyImageKeys, BlueGuid);
                    Check("image writer: a mapper without the card's or the popup's entry gets them, after the others; the others stay",
                          wrote && string.Join(",", lone.Images.Select(i => i.serializableKey)) == "Picto,TechnologyScreenItemBackground,TechnologyPopupBackground"
                          && lone.Images[0].Value.guid.a == picto.Value.guid.a && IsBlue(lone.Images[1]) && IsBlue(lone.Images[2]),
                          string.Join(",", lone.Images.Select(i => i.serializableKey)));
                    var plain = ScriptableObject.CreateInstance<TechTreeDrillPlain>();
                    Check("image and text writers refuse what they cannot write: no GUID, no Images list, no such text field, a field that is no text",
                          !TechTreeData.WriteImage(lone, TechTreeData.TechnologyImageKeys, "not a guid") && !TechTreeData.WriteImage(plain, TechTreeData.TechnologyImageKeys, BlueGuid)
                          && !TechTreeData.WriteText(plain, "Title", "x") && !TechTreeData.WriteText(lone, "TechTreeX", "x") && TechTreeData.WriteText(lone, "Title", "Lone") && lone.Title == "Lone");
                    UnityEngine.Object.DestroyImmediate(lone); UnityEngine.Object.DestroyImmediate(plain);
                    break;
                }
                case 12:
                {
                    // ---- a technology that exists only in the reference folder ----
                    const string R = "Technology_R";
                    string modDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), ModPath);
                    VCall("RemovePrereq", N(R), "Technology_B"); VCall("SetVisibility", N(R), 0); VCall("SetEra", N(R), "Era3");
                    VCall("CommitChanges");
                    int copies = Directory.Exists(modDir) ? Directory.GetFiles(modDir, "Technology_R*.asset").Length : 0;
                    var rCopy = Asset<TechTreeDrillDefinition>(ModPath + "/" + R + ".asset"); var rRef = Asset<TechTreeDrillDefinition>(RefDefs + "/" + R + ".asset");
                    Check("reference-only definition, prerequisites AND visibility in one save: one copy, holding both (it was copied twice)",
                          copies == 1 && rCopy != null && NamesOf(rCopy).SequenceEqual(new[] { "Technology_A" }) && rCopy.Visibility == TechTreeDrillDefinition.Flags.Never && Staged == 0, "copies " + copies + " staged " + Staged);
                    Check("    the era of that save is in the copy too", rCopy != null && TechTreeData.ReadEra(rCopy) == "Era3" && N(R).Era == "Era3", rCopy != null ? TechTreeData.ReadEra(rCopy) : "no copy");
                    Check("    the reference copy is untouched", NamesOf(rRef).SequenceEqual(new[] { "Technology_A", "Technology_B" }) && rRef.Visibility == TechTreeDrillDefinition.Flags.Always && TechTreeData.ReadEra(rRef) == "");
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
                case 16:
                {
                    // ---- what a technology unlocks: the reader on stand-ins of the game's shapes (2026-11-22) ----
                    // Every expectation below is derived from the game's rules (TechTreeUnlocks.cs's header) and this
                    // input, not from the reader's output; the review's 125 planted defects showed which shapes the
                    // first stand-ins were too friendly to catch (a "" override, an int Operator, a concrete
                    // UnitDefinition), and they are the game's shapes now.
                    var index = new TechTreeUnlocks.Index();
                    void Def(Amplitude.Mercury.Data.Simulation.ConstructibleDefinition c) { index.Constructibles[c.name] = c; }
                    void M(TechTreeDrillTypes.DrillMapper m) { index.Mappers[m.name] = m; }
                    const TechTreeDrillTypes.FactionOperator Any = TechTreeDrillTypes.FactionOperator.Any, InHistory = TechTreeDrillTypes.FactionOperator.AnyInFactionsHistory, NoneOf = TechTreeDrillTypes.FactionOperator.None;
                    Def(Constructible<TechTreeDrillTypes.LandUnitDefinition>("Unit_Common", 6, InHistory));                                     // no culture named: for everyone
                    Def(Constructible<TechTreeDrillTypes.LandUnitDefinition>("Unit_Emblem", 7, InHistory, "Civilization_A"));                   // is or was A: emblematic
                    Def(Constructible<TechTreeDrillTypes.LandUnitDefinition>("Unit_Common_B", 6, Any));
                    Def(Constructible<TechTreeDrillTypes.LandUnitDefinition>("Unit_Common_Late", 9, Any));
                    Def(Constructible<TechTreeDrillTypes.ExtensionDistrictDefinition>("Harbour_Base", 0, NoneOf, "Civilization_A", "Civilization_B"));   // for everyone BUT these
                    Def(Constructible<TechTreeDrillTypes.ExtensionDistrictDefinition>("Harbour_A", 1, Any, "Civilization_A"));
                    Def(Constructible<TechTreeDrillTypes.ExtensionDistrictDefinition>("Tribe_District", 0, InHistory, TechTreeUnlocks.StartingCulture));   // ENC's idiom: everyone was the tribe
                    Def(Constructible<TechTreeDrillTypes.ExtensionDistrictDefinition>("Tribe_Now", 0, Any, TechTreeUnlocks.StartingCulture));              // IS the tribe now: not a later culture
                    Def(Constructible<TechTreeDrillTypes.SettlementImprovementDefinition>("Infra_One", 3, Any));
                    Def(Constructible<TechTreeDrillTypes.AirportDefinition>("Zed_Airport", 0, Any));
                    Def(Constructible<TechTreeDrillTypes.SettlementEvolutionDefinition>("Evolution", 0, Any));
                    Def(Constructible<TechTreeDrillTypes.LandUnitDefinition>("Emblem_Only_A", 2, Any, "Civilization_A"));
                    Def(Constructible<TechTreeDrillTypes.LandUnitDefinition>("Emblem_Only_B", 3, Any, "Civilization_B"));
                    Def(Constructible<TechTreeDrillTypes.LandUnitDefinition>("Tie_A", 1, Any));
                    Def(Constructible<TechTreeDrillTypes.LandUnitDefinition>("Tie_B", 1, Any));

                    bool forBase;
                    Check("unlocks: the constructible for everyone stands for an unlock, not the emblematic one of a higher level",
                          TechTreeUnlocks.Choose(new[] { "Unit_Emblem", "Unit_Common" }, index, out forBase) == "Unit_Common" && forBase
                          && TechTreeUnlocks.Choose(new[] { "Unit_Common", "Unit_Emblem" }, index, out forBase) == "Unit_Common" && forBase);
                    Check("unlocks: a prerequisite that names the cultures it is NOT for (the base Harbour) is for everyone",
                          TechTreeUnlocks.ForBaseCulture(index.Constructibles["Harbour_Base"]) && !TechTreeUnlocks.ForBaseCulture(index.Constructibles["Harbour_A"])
                          && TechTreeUnlocks.Choose(new[] { "Harbour_Base", "Harbour_A" }, index, out forBase) == "Harbour_Base" && forBase);
                    Check("unlocks: 'is or ever was the starting tribe' is everyone; 'is the tribe now', and 'is or ever was' another culture, are not",
                          TechTreeUnlocks.ForBaseCulture(index.Constructibles["Tribe_District"]) && !TechTreeUnlocks.ForBaseCulture(index.Constructibles["Tribe_Now"])
                          && !TechTreeUnlocks.ForBaseCulture(index.Constructibles["Unit_Emblem"]) && TechTreeUnlocks.ForBaseCulture(index.Constructibles["Unit_Common"]));
                    Check("unlocks: among those for everyone the highest level, the later one on a tie (the game's rule)",
                          TechTreeUnlocks.Choose(new[] { "Unit_Common", "Unit_Common_Late", "Unit_Common_B" }, index, out forBase) == "Unit_Common_Late"
                          && TechTreeUnlocks.Choose(new[] { "Unit_Common", "Unit_Common_B" }, index, out forBase) == "Unit_Common_B" && TechTreeUnlocks.Choose(new[] { "Unit_Common_B", "Unit_Common" }, index, out forBase) == "Unit_Common");
                    string onlyEmblems = TechTreeUnlocks.Choose(new[] { "Emblem_Only_B", "Emblem_Only_A" }, index, out bool emblemsForBase);
                    string overGhost = TechTreeUnlocks.Choose(new[] { "Ghost", "Infra_One" }, index, out bool ghostForBase);
                    Check("unlocks: only other cultures' ones named: the highest of them, and said to be for no culture-less player; a name without a definition is passed over; none known: no answer",
                          onlyEmblems == "Emblem_Only_B" && !emblemsForBase && overGhost == "Infra_One" && ghostForBase
                          && TechTreeUnlocks.Choose(new[] { "Ghost_1", "Ghost_2" }, index, out forBase) == null && !forBase && TechTreeUnlocks.Choose(new string[0], index, out forBase) == null,
                          onlyEmblems + " " + emblemsForBase + " " + overGhost);
                    Check("unlocks, kind: by the definition's type or a base of it (a land unit is a unit, an airport a district, an evolution a district)",
                          TechTreeUnlocks.KindOf("Unit_Common", index) == TechTreeUnlocks.Kind.Unit && TechTreeUnlocks.KindOf("Zed_Airport", index) == TechTreeUnlocks.Kind.Extension
                          && TechTreeUnlocks.KindOf("Evolution", index) == TechTreeUnlocks.Kind.Extension && TechTreeUnlocks.KindOf("Infra_One", index) == TechTreeUnlocks.Kind.Infrastructure
                          && TechTreeUnlocks.KindOf("Ghost", index) == TechTreeUnlocks.Kind.Infrastructure);

                    string icon = IconGuid();
                    const string elsewhere = "0123456789abcdef0123456789abcdef";   // a GUID no asset of this project has: one of the game's own
                    const string wrong = "fedcba9876543210fedcba9876543210";       // the image of a key the game does NOT ask for
                    M(CMapper("Unit_Common", "%UnitCommonTitle", 30, Image("Picto", wrong), Image("Default_Small", icon), Image("Small", wrong), Image("Variation1_Small", wrong)));
                    M(CMapper("Unit_Emblem", "%EmblemTitle", 2));
                    M(CMapper("Harbour_Base", "Harbour", 10, Image("Default_Small", null), Image("Small", elsewhere), Image("Variation1_Small", wrong)));   // an empty Default_Small: the plain Small
                    M(CMapper("Infra_One", "Infra", 20, Image("Picto", wrong), Image("Variation1_Small", wrong)));                                         // neither key: no icon
                    M(CMapper("Tribe_District", "Tribe district", 5, Image("Small", elsewhere)));
                    M(CMapper("Emblem_Only_B", "Emblem B", 1, Image("Default_Small", elsewhere)));
                    M(CMapper("Tie_A", "Tie A", 7)); M(CMapper("Tie_B", "Tie B", 7));
                    M(Mapper("Bonus_Mapper", "A bonus", Image("Picto", wrong), Image("Small", elsewhere)));
                    M(Mapper("Effect_Plain", "Plain", Image("Default_Small", wrong), Image("Picto", wrong)));       // a bonus asks for Small only
                    M(Mapper("Resource_R", "Horses", Image("Small", wrong), Image("Picto", elsewhere)));
                    M(Mapper("Resource_S", "DECOY"));                                                               // a mapper, but no resource definition
                    M(Mapper("ActionTypeCuttingForest", "Clear forest", Image("Small", wrong), Image("Picto", elsewhere)));
                    M(Mapper("CuttingForest", "DECOY", Image("Picto", wrong)));                                     // the game asks for "ActionType" + name
                    M(Mapper("ReinforceBattle", "DECOY", Image("Picto", wrong)));                                   // ... and there is no "ActionTypeReinforceBattle"
                    M(Mapper("Prohibit_Mapper", "No more moves", Image("Small", wrong)));                                // an action asks for Picto only
                    M(Mapper("Package_1", "A package", Image("Small", elsewhere)));
                    M(Mapper("Ability_1", "An ability", Image("Small", elsewhere), Image("Small", wrong)));              // a key twice: the first entry counts
                    M(Mapper("Cost_1", "Cheaper", Image("Small", elsewhere)));
                    M(Mapper("FameGains", "Fame", Image("Small", elsewhere)));
                    M(Mapper("Fame_Own", "Own fame", Image("Small", null), Image("Small", wrong)));                      // ... also when the first is empty
                    M(Mapper("Effect_ResetAllCivics", "Reset", Image("Small", elsewhere)));
                    index.Resources.Add("Resource_R"); index.CostModifiers.Add("Cost_1");

                    var definition = Effects(
                        Unlocks("Unit_Emblem", "Unit_Common", "Ghost"),
                        new TechTreeDrillTypes.SimulationEventEffect_UnlockConstructible { Hidden = true, ConstructibleReferences = Refs("Infra_One") },
                        Unlocks("Harbour_A", "Harbour_Base"),
                        Unlocks("Infra_One"),
                        new TechTreeDrillTypes.SimulationEventEffect_ApplyDescriptor { Descriptor = Ref("Effect_Raw"), UIMapperOverride = "Bonus_Mapper" },
                        new TechTreeDrillTypes.SimulationEventEffect_ApplyDescriptor { Descriptor = Ref("Effect_Plain") },                       // no override: null, as the assets store it
                        new TechTreeDrillTypes.SimulationEventEffect_ForceCalendar(),
                        new TechTreeDrillTypes.SimulationEventEffect_UnlockResource { ResourceReferences = Refs("Resource_R", "Resource_S") },
                        new TechTreeDrillTypes.SimulationEventEffect_UnlockAction { ActionType = TechTreeDrillTypes.DrillAction.CuttingForest },
                        new TechTreeDrillTypes.SimulationEventEffect_UnlockAction { ActionType = TechTreeDrillTypes.DrillAction.ReinforceBattle },
                        new TechTreeDrillTypes.SimulationEventEffect_ProhibitAction { ActionType = TechTreeDrillTypes.DrillAction.MoveCapital, UIMapperOverride = "Prohibit_Mapper" },
                        new TechTreeDrillTypes.SimulationEventEffect_UnlockSettlementStartingPackage { StartingPackageReference = Ref("Package_1"), UIMapperOverride = "Bonus_Mapper" },
                        new TechTreeDrillTypes.SimulationEventEffect_AddEmpireBattleAbility { BattleAbilityDefinition = Ref("Ability_1") },
                        new TechTreeDrillTypes.SimulationEventEffect_ApplyCostModifier { CostModifierReference = Ref("Cost_1") },
                        new TechTreeDrillTypes.SimulationEventEffect_ApplyCostModifier { CostModifierReference = Ref("Cost_Ghost") },
                        new TechTreeDrillTypes.SimulationEventEffect_AddFame(),
                        new TechTreeDrillTypes.SimulationEventEffect_AddFame { UIMapperOverride = "Fame_Own" },
                        new TechTreeDrillTypes.SimulationEventEffect_AddFame { UIMapperOverride = "Fame_None" },
                        new TechTreeDrillTypes.SimulationEventEffect_ResetAllCivics(),
                        new TechTreeDrillTypes.SimulationEventEffect_UpgradeAllRoads { UIMapperOverride = "Roads_Mapper" },
                        Unlocks("Emblem_Only_A", "Emblem_Only_B"),
                        Unlocks("Ghost_1"),
                        new TechTreeDrillTypes.SimulationEventEffect_UnlockConstructible { ConstructibleReferences = Refs("Unit_Common_B"), UIMapperOverride = "Not_A_Constructible" },
                        new TechTreeDrillTypes.SimulationEventEffect_UnlockConstructible { ConstructibleReferences = Refs("Tribe_District"), DlcPrerequisite = new TechTreeDrillTypes.DlcPrerequisite { DownloadableContent = TechTreeDrillTypes.DrillDlc.Dlc6 } },
                        null);
                    var read = TechTreeUnlocks.Read(definition, index, text => text == "%UnitCommonTitle" ? "Warriors" : text == "%EmblemTitle" ? "Emblem!" : text);
                    TechTreeUnlocks.Unlock E(string name) => read.FirstOrDefault(u => u.ElementName == name) ?? new TechTreeUnlocks.Unlock { ElementName = "MISSING " + name };
                    // The game's order (TechnologyUnlocksComparer): cost / fame / package / other first, then bonus, resource,
                    // action, constructible; inside a group by ordinal name; two constructibles that both have a constructible
                    // mapper by its SortPriority (5, 10, 20, 30), else by the kind of definition (none: last), then by name.
                    // No two entries of this input compare equal, so the order does not depend on the sort's algorithm.
                    const string expectedOrder = "(none),Cost_1,Cost_Ghost,Effect_ResetAllCivics,Fame_None,Fame_Own,Package_1,Roads_Mapper,"
                        + "Ability_1,Bonus_Mapper,Effect_Plain,Resource_R,Resource_S,CuttingForest,Prohibit_Mapper,ReinforceBattle,"
                        + "Tribe_District,Harbour_Base,Infra_One,Unit_Common,Ghost_1,Not_A_Constructible,Emblem_Only_B";
                    Check("unlocks: one entry per effect that is not hidden (one per resource), in the GAME's order, the entry only another culture gets after them",
                          Names(read) == expectedOrder, Names(read));
                    Check("unlocks, order: three entries of which two compare equal come out as the game's List.Sort leaves them (the bonus, then the LATER unit first: Military Reforms)",
                          Names(TechTreeUnlocks.Read(Effects(Unlocks("Tie_A"), Unlocks("Tie_B"), new TechTreeDrillTypes.SimulationEventEffect_ApplyDescriptor { Descriptor = Ref("Effect_Plain") }), index, null)) == "Effect_Plain,Tie_B,Tie_A",
                          Names(TechTreeUnlocks.Read(Effects(Unlocks("Tie_A"), Unlocks("Tie_B"), new TechTreeDrillTypes.SimulationEventEffect_ApplyDescriptor { Descriptor = Ref("Effect_Plain") }), index, null)));
                    Check("unlocks, order: a constructible without a constructible mapper is placed by its kind (a district before a unit), whatever the names and the effects' order",
                          Names(TechTreeUnlocks.Read(Effects(Unlocks("Unit_Common"), Unlocks("Zed_Airport")), index, null)) == "Zed_Airport,Unit_Common");
                    Check("unlocks: nothing to read is an empty row, not an exception",
                          TechTreeUnlocks.Read(null, index, null).Count == 0 && TechTreeUnlocks.Read(new TechTreeDrillTypes.TechDefinition(), index, null).Count == 0 && TechTreeUnlocks.Read(Effects(Unlocks()), index, null).Count == 0);

                    Check("unlocks, constructible: kind, localized title, and the others of the same unlock WITH their cultures",
                          E("Unit_Common").Kind == TechTreeUnlocks.Kind.Unit && E("Unit_Common").Title == "Warriors" && E("Unit_Common").NotShown == null && E("Unit_Common").OnlyFor == null
                          && string.Join(";", E("Unit_Common").Others.Select(o => o.Name + "=" + o.Title + "[" + string.Join("+", o.Cultures) + "]")) == "Unit_Emblem=Emblem![Civilization_A];Ghost=Ghost[]"
                          && E("Harbour_Base").Kind == TechTreeUnlocks.Kind.Extension && string.Join(";", E("Harbour_Base").Others.Select(o => o.Name + "[" + string.Join("+", o.Cultures) + "]")) == "Harbour_A[Civilization_A]"
                          && E("Infra_One").Kind == TechTreeUnlocks.Kind.Infrastructure && E("Infra_One").Others.Count == 0,
                          string.Join(";", E("Unit_Common").Others.Select(o => o.Name + "=" + o.Title + "[" + string.Join("+", o.Cultures) + "]")));
                    Check("unlocks, constructible: an effect that names ONLY other cultures' constructibles is kept, marked with the culture, and names the other one",
                          E("Emblem_Only_B").OnlyFor != null && string.Join("+", E("Emblem_Only_B").OnlyFor ?? new string[0]) == "Civilization_B" && E("Emblem_Only_B").NotShown == null
                          && string.Join(";", E("Emblem_Only_B").Others.Select(o => o.Name + "[" + string.Join("+", o.Cultures) + "]")) == "Emblem_Only_A[Civilization_A]"
                          && read.Count(u => u.OnlyFor != null) == 1);
                    Check("unlocks, constructible: a name without a definition, and an override that is no constructible, are entries the game does not draw",
                          E("Ghost_1").NotShown != null && E("Ghost_1").NotShown.Contains("no constructible definition") && E("Not_A_Constructible").Effect == "UnlockConstructible"
                          && E("Not_A_Constructible").NotShown != null && E("Not_A_Constructible").NotShown.Contains("is no constructible definition") && !read.Any(u => u.ElementName == "Unit_Common_B"),
                          E("Ghost_1").NotShown + " | " + E("Not_A_Constructible").NotShown);
                    Check("unlocks, icon: a constructible's Default_Small, found as a file of the project; an empty Default_Small falls to the plain Small; no other key is taken",
                          E("Unit_Common").MapperFound && E("Unit_Common").ImagePath == IconPath && E("Unit_Common").ImageGuid == icon
                          && E("Harbour_Base").ImagePath == null && E("Harbour_Base").ImageGuid == elsewhere && E("Infra_One").MapperFound && E("Infra_One").ImageGuid == null,
                          E("Unit_Common").ImagePath + " " + E("Unit_Common").ImageGuid + " | " + E("Harbour_Base").ImageGuid + " | " + E("Infra_One").ImageGuid);
                    Check("unlocks, bonus: the override replaces the descriptor's name, no override keeps it; the icon is the mapper's Small and only that",
                          E("Bonus_Mapper").Effect == "ApplyDescriptor" && E("Bonus_Mapper").Title == "A bonus" && E("Bonus_Mapper").ImageGuid == elsewhere && !read.Any(u => u.ElementName == "Effect_Raw")
                          && E("Effect_Plain").MapperFound && E("Effect_Plain").Title == "Plain" && E("Effect_Plain").ImageGuid == null && E("Effect_Plain").NotShown == null
                          && E("Ability_1").Effect == "AddEmpireBattleAbility" && E("Ability_1").Title == "An ability" && E("Ability_1").ImageGuid == elsewhere,
                          E("Bonus_Mapper").ImageGuid + " | " + E("Effect_Plain").ImageGuid);
                    Check("unlocks, resource and cost: the icon is Picto / Small; a name that is no such definition is not drawn by the game, mapper or not",
                          E("Resource_R").ImageGuid == elsewhere && E("Resource_R").NotShown == null && E("Resource_S").NotShown != null && E("Resource_S").NotShown.Contains("no resource definition")
                          && E("Cost_1").Effect == "ApplyCostModifier" && E("Cost_1").ImageGuid == elsewhere && E("Cost_1").NotShown == null && E("Cost_Ghost").NotShown != null && E("Cost_Ghost").NotShown.Contains("no cost modifier definition"),
                          E("Resource_R").ImageGuid + " | " + E("Resource_S").NotShown + " | " + E("Cost_Ghost").NotShown);
                    Check("unlocks, action: looked up as 'ActionType' + its name, never by the bare name; without that mapper the game draws nothing; an override that is no action is its own mapper",
                          E("CuttingForest").MapperName == "ActionTypeCuttingForest" && E("CuttingForest").Title == "Clear forest" && E("CuttingForest").ImageGuid == elsewhere && E("CuttingForest").NotShown == null
                          && E("ReinforceBattle").MapperName == "ActionTypeReinforceBattle" && !E("ReinforceBattle").MapperFound && E("ReinforceBattle").NotShown != null && E("ReinforceBattle").Title == "ReinforceBattle"
                          && E("Prohibit_Mapper").Effect == "ProhibitAction" && E("Prohibit_Mapper").MapperName == "Prohibit_Mapper" && E("Prohibit_Mapper").MapperFound && E("Prohibit_Mapper").NotShown == null
                          && E("Prohibit_Mapper").ImageGuid == null,
                          E("CuttingForest").MapperName + " " + E("CuttingForest").Title + " | " + E("ReinforceBattle").MapperName + " " + E("ReinforceBattle").NotShown);
                    Check("unlocks, package / fame / civics / roads: the package ignores the override; fame takes its override's mapper when there is one, else the shared FameGains",
                          E("Package_1").Effect == "UnlockSettlementStartingPackage" && E("Package_1").Kind == TechTreeUnlocks.Kind.Extension && E("Package_1").Title == "A package" && E("Package_1").ImageGuid == elsewhere
                          && E("").Effect == "AddFame" && E("").MapperName == "FameGains" && E("").Title == "Fame" && E("").ImageGuid == elsewhere
                          && E("Fame_Own").MapperName == "Fame_Own" && E("Fame_Own").Title == "Own fame" && E("Fame_Own").ImageGuid == null && E("Fame_None").MapperName == "FameGains" && E("Fame_None").Title == "Fame"
                          && E("Effect_ResetAllCivics").Effect == "ResetAllCivics" && E("Effect_ResetAllCivics").Title == "Reset"
                          && E("Roads_Mapper").Effect == "UpgradeAllRoads" && !E("Roads_Mapper").MapperFound && E("Roads_Mapper").NotShown == null,
                          E("Package_1").Title + " | " + E("").MapperName + " | " + E("Fame_None").MapperName);
                    Check("unlocks: the downloadable content an effect needs is kept with its entry, and only with it",
                          E("Tribe_District").Dlc == "Dlc6" && E("Tribe_District").OnlyFor == null && read.Count(u => u.Dlc != "") == 1, E("Tribe_District").Dlc);

                    // ---- the index, on real assets of the temporary project: Databases over the reference folder over anywhere else
                    Folder("Assets/Databases/Idx"); Folder("Assets/VanillaReference/Idx"); Folder("Assets/Elsewhere");
                    void MapperAsset(string folder, string name, string title) => NewAsset<TechTreeDrillTypes.DrillConstructibleMapper>(folder + "/" + name + ".asset", m => { m.Title = title; m.Images = new List<TechTreeDrillTypes.DrillImage> { Image("Small", elsewhere) }; });
                    MapperAsset("Assets/Elsewhere", "Idx_M", "else"); MapperAsset("Assets/VanillaReference/Idx", "Idx_M", "ref"); MapperAsset("Assets/Databases/Idx", "Idx_M", "db");
                    MapperAsset("Assets/VanillaReference/Idx", "Idx_R", "ref"); MapperAsset("Assets/Elsewhere", "Idx_R", "else");
                    MapperAsset("Assets/Elsewhere", "Idx_E", "else");
                    NewAsset<TechTreeDrillTypes.LandUnitDefinition>("Assets/Elsewhere/Idx_C.asset", c => c.Level = 2);
                    NewAsset<TechTreeDrillTypes.LandUnitDefinition>("Assets/Databases/Idx/Idx_C.asset", c => c.Level = 1);
                    NewAsset<TechTreeDrillTypes.ResourceDefinition>("Assets/Elsewhere/Idx_Res.asset", r => { });
                    NewAsset<TechTreeDrillTypes.CostModifierDefinition>("Assets/Elsewhere/Idx_Cost.asset", c => { });
                    NewAsset<TechTreeDrillPlain>("Assets/Elsewhere/Idx_Plain.asset", m => { });   // no Images list: not a UI mapper
                    AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
                    var built = TechTreeUnlocks.BuildIndex("Assets/VanillaReference");
                    string TitleIn(string name) => built.Mappers.TryGetValue(name, out var found) ? ((TechTreeDrillTypes.DrillMapper)found).Title : "(none)";
                    Check("unlock index: a UI mapper is found by its name; the copy under Databases wins, then the reference folder's, then any other",
                          TitleIn("Idx_M") == "db" && TitleIn("Idx_R") == "ref" && TitleIn("Idx_E") == "else", TitleIn("Idx_M") + " " + TitleIn("Idx_R") + " " + TitleIn("Idx_E"));
                    Check("unlock index: constructible, resource and cost modifier definitions are told apart by their types; an asset without an Images list is no mapper",
                          built.Constructibles.TryGetValue("Idx_C", out var builtUnit) && ((TechTreeDrillTypes.LandUnitDefinition)builtUnit).Level == 1 && !built.Mappers.ContainsKey("Idx_C") && !built.Constructibles.ContainsKey("Idx_M")
                          && built.Resources.Contains("Idx_Res") && !built.Mappers.ContainsKey("Idx_Res") && built.CostModifiers.Contains("Idx_Cost") && !built.Resources.Contains("Idx_Cost")
                          && !built.Mappers.ContainsKey("Idx_Plain"),
                          "constructibles " + string.Join(",", built.Constructibles.Keys) + " resources " + string.Join(",", built.Resources) + " costs " + string.Join(",", built.CostModifiers));
                    var fromAssets = TechTreeUnlocks.Read(Effects(Unlocks("Idx_C")), built, null);
                    Check("unlock index: an unlock read through it finds the mapper asset and its image",
                          fromAssets.Count == 1 && fromAssets[0].Kind == TechTreeUnlocks.Kind.Unit && fromAssets[0].NotShown == null && !fromAssets[0].MapperFound
                          && TechTreeUnlocks.Read(Effects(new TechTreeDrillTypes.SimulationEventEffect_ApplyDescriptor { Descriptor = Ref("Idx_M") }), built, null)[0].ImageGuid == elsewhere);

                    // ---- the row on the card: the game's 310 x 116 card keeps 46-high icons 4 from its bottom, six fit
                    var row = TechTreeWindow.UnlockRects(new Rect(100, 200, 310, 116), 8);
                    Check("unlock row: six icons of 46 fit the card, along its bottom from the left",
                          row.Count == 6 && Mathf.Abs(row[0].x - 106) < 0.01f && Mathf.Abs(row[0].y - 266) < 0.01f && Mathf.Abs(row[0].width - 46) < 0.01f && Mathf.Abs(row[1].x - 155) < 0.01f && row[5].xMax <= 410,
                          row.Count + " first " + (row.Count > 0 ? row[0].ToString() : ""));
                    var half = TechTreeWindow.UnlockRects(new Rect(0, 0, 155, 58), 2);
                    Check("unlock row: scaled with the card (the margin and the gap too), no more rectangles than unlocks",
                          half.Count == 2 && Mathf.Abs(half[0].width - 23) < 0.01f && Mathf.Abs(half[0].y - 33) < 0.01f && Mathf.Abs(half[0].x - 3) < 0.01f && Mathf.Abs(half[1].x - 27.5f) < 0.01f,
                          half.Count == 2 ? half[0] + " " + half[1] : half.Count.ToString());
                    // 23 entries, 5 the game does not draw: 18 boxes wanted, six fit: the first five, then "+13"
                    var plan = TechTreeWindow.PlanUnlockRow(new Rect(100, 200, 310, 116), read);
                    Check("unlock row, plan: the entries the game draws, in order, skipping the others; when more than fit, the last box counts the rest",
                          plan.Count == 6 && string.Join(",", plan.Take(5).Select(b => b.Unlock == null ? "+" : b.Unlock.ElementName == "" ? "(none)" : b.Unlock.ElementName)) == "(none),Cost_1,Effect_ResetAllCivics,Fame_None,Fame_Own"
                          && plan[5].Unlock == null && plan[5].More == 13 && plan.Take(5).All(b => b.More == 0 && !b.Cultural) && plan[5].Rect == row[5] && plan[0].Rect == row[0],
                          plan.Count + ": " + string.Join(",", plan.Select(b => b.Unlock == null ? "+" + b.More : b.Unlock.ElementName)));
                    var small = TechTreeWindow.PlanUnlockRow(new Rect(100, 200, 310, 116), new List<TechTreeUnlocks.Unlock> { E("Unit_Common"), E("Resource_S"), E("Emblem_Only_B") });
                    var six = TechTreeWindow.PlanUnlockRow(new Rect(100, 200, 310, 116), read.Where(u => u.NotShown == null).Take(6).ToList());
                    Check("unlock row, plan: an entry only another culture gets has its box, marked; exactly as many as fit need no count",
                          small.Count == 2 && small[0].Unlock == E("Unit_Common") && !small[0].Cultural && small[1].Unlock == E("Emblem_Only_B") && small[1].Cultural && small[1].More == 0
                          && six.Count == 6 && six.All(b => b.Unlock != null && b.More == 0));
                    Check("unlock list: the heading counts what a card shows, what only other cultures get, and what the game does not draw",
                          TechTreeWindow.UnlockHeader(read) == "Unlocks (17) + 1 for other cultures + 5 without an icon in the game:" && TechTreeWindow.UnlockHeader(new List<TechTreeUnlocks.Unlock> { E("Unit_Common") }) == "Unlocks (1):",
                          TechTreeWindow.UnlockHeader(read));
                    string L(string name, string iconProblem = "") => string.Join(" / ", TechTreeWindow.UnlockLines(E(name), iconProblem));
                    Check("unlock list: an entry's line, then one line per constructible another culture gets instead, by culture",
                          L("Unit_Common") == "Unit · Warriors  (Unit_Common) /       A get Emblem!  (Unit_Emblem) /       also named: Ghost"
                          && L("Emblem_Only_B") == "Unit · Emblem B  (Emblem_Only_B)  — ONLY for B: the game shows it to no other culture /       A get Emblem_Only_A"
                          && L("") == "Fame · Fame" && L("Bonus_Mapper") == "Bonus · A bonus  (Bonus_Mapper)",
                          L("Unit_Common") + " | " + L("Emblem_Only_B") + " | " + L(""));
                    Check("unlock list: what is in the way of an icon is said: the game draws none, no mapper, no image in the mapper, the DLC it needs, the image not readable here",
                          L("Resource_S") == "Resource · DECOY  (Resource_S)  — the game shows NO icon: 'Resource_S' is no resource definition of this project"
                          && L("Roads_Mapper") == "Roads · Roads_Mapper  — no UI mapper 'Roads_Mapper' in the project: a blank icon"
                          && L("Effect_Plain") == "Bonus · Plain  (Effect_Plain)  — its UI mapper names no icon: a blank icon"
                          && L("Tribe_District") == "District · Tribe district  (Tribe_District)  — needs Dlc6"
                          && L("Harbour_Base", "the game's files were not found") == "District · Harbour  (Harbour_Base)  — icon not shown here: the game's files were not found /       A get Harbour_A",
                          L("Resource_S") + " | " + L("Roads_Mapper") + " | " + L("Effect_Plain") + " | " + L("Tribe_District") + " | " + L("Harbour_Base", "x"));
                    Check("unlock list: each kind of entry has its word",
                          L("Infra_One") == "Infrastructure · Infra  (Infra_One)  — its UI mapper names no icon: a blank icon" && L("Package_1") == "Starting package · A package  (Package_1)"
                          && L("Ability_1") == "Battle ability · An ability  (Ability_1)" && L("Resource_R") == "Resource · Horses  (Resource_R)" && L("Cost_1") == "Cost · Cheaper  (Cost_1)"
                          && L("CuttingForest") == "Action · Clear forest  (CuttingForest)" && L("Prohibit_Mapper") == "Prohibits · No more moves  (Prohibit_Mapper)  — its UI mapper names no icon: a blank icon"
                          && L("Effect_ResetAllCivics") == "Civics · Reset  (Effect_ResetAllCivics)",
                          L("Infra_One") + " | " + L("Package_1") + " | " + L("Ability_1") + " | " + L("Cost_1") + " | " + L("Prohibit_Mapper"));
                    Check("card: the internal name is shown without the prefix every technology has; a culture without the data's prefix",
                          TechTreeWindow.ShortName("Technology_Era1_01") == "Era1_01" && TechTreeWindow.ShortName("Other") == "Other" && TechTreeWindow.ShortName(null) == ""
                          && TechTreeWindow.CultureWord("Civilization_Era1_Phoenicia") == "Era1_Phoenicia" && TechTreeWindow.CultureWord("Tribe") == "Tribe");

                    // ---- the game's icons as textures of their own (TechTreeVanillaArt.Bake): a pictogram is an alpha-only
                    // distance field, black as stored (the first version drew it so); the copy is white with a ramped alpha
                    var field = new Texture2D(8, 8, TextureFormat.Alpha8, false);
                    var fieldPixels = new Color32[64];
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) fieldPixels[y * 8 + x] = new Color32(0, 0, 0, (byte)(x < 4 ? 0 : 255));   // outside | inside
                    field.SetPixels32(fieldPixels); field.Apply();
                    var marker = RenderTexture.GetTemporary(4, 4); RenderTexture.active = marker;   // as inside OnGUI: something else is the render target
                    var shape = TechTreeVanillaArt.Bake(field, new Rect(0, 0, 1, 1), 8, 8, true);
                    bool targetKept = RenderTexture.active == marker;
                    var shapePixels = PixelsOf(shape);
                    Check("icon copy, pictogram: twice the field's size, WHITE in every pixel, transparent outside and solid inside, with mip levels, the render target put back",
                          shape.width == 16 && shape.height == 16 && shape.mipmapCount > 1 && targetKept && shapePixels.All(c => c.r == 255 && c.g == 255 && c.b == 255)
                          && shapePixels[8 * 16 + 0].a == 0 && shapePixels[8 * 16 + 15].a == 255,
                          shape.width + "x" + shape.height + " mips " + shape.mipmapCount + " target kept " + targetKept + " left " + shapePixels[8 * 16] + " right " + shapePixels[8 * 16 + 15]);
                    Check("icon copy, pictogram: the field's 0.5 is the contour; the ramp around it is steep but not a step",
                          TechTreeVanillaArt.FieldAlpha(0) == 0 && TechTreeVanillaArt.FieldAlpha(100) == 0 && TechTreeVanillaArt.FieldAlpha(156) == 255 && TechTreeVanillaArt.FieldAlpha(255) == 255
                          && TechTreeVanillaArt.FieldAlpha(124) > 0 && TechTreeVanillaArt.FieldAlpha(124) < TechTreeVanillaArt.FieldAlpha(128) && TechTreeVanillaArt.FieldAlpha(128) < TechTreeVanillaArt.FieldAlpha(132) && TechTreeVanillaArt.FieldAlpha(132) < 255
                          && Mathf.Abs(TechTreeVanillaArt.FieldAlpha(128) - 136) <= 1,
                          TechTreeVanillaArt.FieldAlpha(124) + " " + TechTreeVanillaArt.FieldAlpha(128) + " " + TechTreeVanillaArt.FieldAlpha(132));
                    var colours = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                    var colourPixels = new Color32[64];
                    // left: red below, yellow above; right: blue below, green above (both half transparent)
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                        colourPixels[y * 8 + x] = x < 4 ? (y < 4 ? new Color32(255, 0, 0, 255) : new Color32(255, 255, 0, 255)) : (y < 4 ? new Color32(0, 0, 255, 128) : new Color32(0, 255, 0, 128));
                    colours.SetPixels32(colourPixels); colours.Apply();
                    var control = PixelsOf(colours);   // the drill's own reading must not mirror or flip, or the checks below prove nothing
                    Check("icon copy, control: this drill reads a texture's pixels where they are",
                          control[0].r == 255 && control[0].g == 0 && control[0].b == 0 && control[7 * 8].r == 255 && control[7 * 8].g == 255 && control[7].b == 255 && control[7 * 8 + 7].g == 255 && control[7 * 8 + 7].b == 0,
                          control[0] + " " + control[7] + " " + control[56] + " " + control[63]);
                    var region = TechTreeVanillaArt.Bake(colours, new Rect(0.5f, 0, 0.5f, 1), 4, 8, false);   // the right half only
                    var regionPixels = PixelsOf(region);
                    var large = TechTreeVanillaArt.Bake(colours, new Rect(0, 0, 1, 1), 512, 256, false);
                    Check("icon copy, colour icon: only its own rectangle of the atlas, the right way up, colours and alpha as stored, with mip levels; a large one is made smaller",
                          region.width == 4 && region.height == 8 && region.mipmapCount > 1 && targetKept
                          && regionPixels.Take(16).All(c => c.r <= 2 && c.g <= 2 && c.b >= 253 && Mathf.Abs(c.a - 128) <= 2) && regionPixels.Skip(16).All(c => c.r <= 2 && c.g >= 253 && c.b <= 2 && Mathf.Abs(c.a - 128) <= 2)
                          && large.width == 128 && large.height == 64,
                          region.width + "x" + region.height + " " + regionPixels[0] + " " + regionPixels[31] + " large " + large.width + "x" + large.height);
                    RenderTexture.active = null; RenderTexture.ReleaseTemporary(marker);
                    foreach (var made in new UnityEngine.Object[] { field, shape, colours, region, large }) UnityEngine.Object.DestroyImmediate(made);
                    Check("icon copy: no image reference, no icon (and no reading of the game's files for it)", TechTreeVanillaArt.Icon(null) == null && TechTreeVanillaArt.Icon("") == null);

                    // ---- eras (2026-11-22): where the game puts its separators, the numerals, the side strip's choice ----
                    KeyValuePair<int, float> Card(int era, int column) => new KeyValuePair<int, float>(era, column * 74f);
                    string Separators(int eraCount, params KeyValuePair<int, float>[] cards) =>
                        string.Join(" ", TechTreeWindow.EraSeparators(cards, eraCount).Select(e => e.LeftEra + "|" + (e.LeftEra + 1) + "@" + e.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
                    // era 0 at columns 0 and 6, era 1 at 12 and 20, era 3 at 30, era 2 without a card: the right end of
                    // era 0 is 6 * 74 + 310 = 754, the left end of era 1 is 888, halfway 821; no separator beside era 2
                    Check("era separators: halfway between the right end of an era and the left end of the next; none beside an era without a card",
                          Separators(4, Card(1, 20), Card(0, 6), Card(3, 30), Card(0, 0), Card(1, 12)) == "0|1@821" && Separators(4, Card(1, 12), Card(0, 0), Card(1, 20), Card(0, 6)) == "0|1@821",
                          Separators(4, Card(1, 20), Card(0, 6), Card(3, 30), Card(0, 0), Card(1, 12)) + " / " + Separators(4, Card(1, 12), Card(0, 0), Card(1, 20), Card(0, 6)));
                    // a later era's card LEFT of an earlier era's: (5 * 74 + 310 + 1 * 74) / 2 = 377, inside the overlap
                    Check("era separators: where two eras overlap the separator is inside the overlap, as in the game; every pair of neighbours gets its own",
                          Separators(3, Card(0, 5), Card(1, 1)) == "0|1@377" && Separators(3, Card(0, 0), Card(1, 10), Card(2, 20)) == "0|1@525 1|2@1265",
                          Separators(3, Card(0, 5), Card(1, 1)) + " / " + Separators(3, Card(0, 0), Card(1, 10), Card(2, 20)));
                    Check("era separators: an era index the project does not have is no era; fewer than two eras, no separator",
                          Separators(2, Card(0, 0), Card(-1, 3), Card(2, 9)) == "" && Separators(2, Card(0, 0), Card(1, 9), Card(5, 2)) == "0|1@488" && Separators(1, Card(0, 0)) == "" && Separators(0) == "");
                    Check("era numerals: the game's Roman numerals, nothing for the era before the first",
                          TechTreeWindow.ToRoman(0) == "" && TechTreeWindow.ToRoman(1) == "I" && TechTreeWindow.ToRoman(2) == "II" && TechTreeWindow.ToRoman(3) == "III" && TechTreeWindow.ToRoman(4) == "IV"
                          && TechTreeWindow.ToRoman(5) == "V" && TechTreeWindow.ToRoman(6) == "VI" && TechTreeWindow.ToRoman(7) == "VII" && TechTreeWindow.ToRoman(8) == "VIII"
                          && TechTreeWindow.ToRoman(9) == "IX" && TechTreeWindow.ToRoman(14) == "XIV" && TechTreeWindow.ToRoman(40) == "XL" && TechTreeWindow.ToRoman(1994) == "MCMXCIV" && TechTreeWindow.ToRoman(-2) == "");
                    // The game's numerals: at half its content's height (920 / 2) less the 24 above the first row = 436, whatever
                    // the cards. The line crosses that height 13 right of the separator's place (measured on the user's
                    // screenshot) and leans 47 across per 120 up (its texture).
                    Vector2 onLine = TechTreeWindow.EraLinePoint(1000f, 436f), above = TechTreeWindow.EraLinePoint(1000f, 316f), below = TechTreeWindow.EraLinePoint(1000f, 556f);
                    Check("era line: crosses the numerals' height (436, fixed) 13 right of the separator's place, leaning 47 across per 120 up",
                          Near(onLine, 1013f, 436f) && Near(above, 1060f, 316f) && Near(below, 966f, 556f),
                          onLine + " " + above + " " + below);
                    var projectEras = new List<TechTreeData.Era> { new TechTreeData.Era { Name = "Era0", Index = 0 }, new TechTreeData.Era { Name = "Era1", Index = 1 }, new TechTreeData.Era { Name = "Era5", Index = 5 } };
                    var choice = TechTreeWindow.EraChoices(projectEras, "Era5", "Era1", out var choiceLabels, out int choiceAt);
                    var foreign = TechTreeWindow.EraChoices(projectEras, "Era9", "Era9", out var foreignLabels, out int foreignAt);
                    var noEra = TechTreeWindow.EraChoices(projectEras, null, null, out var noEraLabels, out int noEraAt);
                    Check("era choice: the project's eras by index, each with its numeral (none for the first, as in the game); the staged era is the one shown",
                          string.Join(",", choice) == "Era0,Era1,Era5" && choiceAt == 2 && string.Join(" / ", choiceLabels) == "Era0 / I  ·  Era1 / V  ·  Era5", string.Join(" / ", choiceLabels) + " at " + choiceAt);
                    var away = TechTreeWindow.EraChoices(projectEras, "Era1", "Era9", out var awayLabels, out int awayAt);
                    var awayNone = TechTreeWindow.EraChoices(projectEras, "Era1", "", out var awayNoneLabels, out int awayNoneAt);
                    Check("era choice: a SAVED era the project does not have stays in the list after another era is staged, so it can be chosen back",
                          string.Join(",", away) == "Era9,Era0,Era1,Era5" && awayAt == 2 && awayLabels[0] == "Era9  (not an era of this project)"
                          && string.Join(",", awayNone) == ",Era0,Era1,Era5" && awayNoneAt == 2 && awayNoneLabels[0] == "(no era)", string.Join(" / ", awayLabels) + " at " + awayAt);
                    var empty = TechTreeWindow.EraChoices(new List<TechTreeData.Era>(), "Era5", "Era5", out var emptyLabels, out int emptyAt);
                    Check("era choice: with no eras read in the project the list says so, and holds the technology's era",
                          string.Join(",", empty) == "Era5" && emptyAt == 0 && emptyLabels[0] == "Era5  (no eras found in this project)", string.Join(" / ", emptyLabels));
                    Check("era choice: an era the project does not have, or none, stays the first entry, so opening the list changes nothing",
                          string.Join(",", foreign) == "Era9,Era0,Era1,Era5" && foreignAt == 0 && foreignLabels[0] == "Era9  (not an era of this project)"
                          && string.Join(",", noEra) == ",Era0,Era1,Era5" && noEraAt == 0 && noEraLabels[0] == "(no era)", string.Join(" / ", foreignLabels) + " | " + string.Join(" / ", noEraLabels));
                    Check("era choice: a pick of another entry asks for that era; the same entry, or one outside the list, asks for nothing",
                          TechTreeWindow.PickedEra(choice, choiceAt, 1) == "Era1" && TechTreeWindow.PickedEra(choice, choiceAt, choiceAt) == null
                          && TechTreeWindow.PickedEra(choice, choiceAt, -1) == null && TechTreeWindow.PickedEra(choice, choiceAt, choice.Length) == null && TechTreeWindow.PickedEra(away, awayAt, 0) == "Era9");
                    Check("era note: nothing for an era of the project; a missing or unknown era is said; with no eras read, nothing is held against the name",
                          TechTreeWindow.EraNote(projectEras, "Era1") == "" && TechTreeWindow.EraNote(projectEras, "Era0") == "" && TechTreeWindow.EraNote(projectEras, "").StartsWith("This technology names no era.")
                          && TechTreeWindow.EraNote(projectEras, "Era9") == "The era 'Era9' is not one of this project's eras (Era0, Era1, Era5). The game would not find it."
                          && TechTreeWindow.EraNote(new List<TechTreeData.Era>(), "Era9") == "" && TechTreeWindow.EraIndexOf(projectEras, "Era5") == 5 && TechTreeWindow.EraIndexOf(projectEras, "Era9") == -1 && TechTreeWindow.EraIndexOf(projectEras, null) == -1,
                          TechTreeWindow.EraNote(projectEras, "Era9"));
                    // what an era decides in the game besides the separators: the cost per tier (DepartmentOfScience.UpdateInitialCosts)
                    var costEras = new List<TechTreeData.Era>
                    {
                        new TechTreeData.Era { Name = "Era0", Index = 0, TierCosts = new[] { "E0_T1", "", "", "" } },
                        new TechTreeData.Era { Name = "Era5", Index = 5, TierCosts = new[] { "E5_T1", "E5_T2", "E5_T3", "" } },
                        new TechTreeData.Era { Name = "Era6", Index = 6, TierCosts = new[] { "E6_T1", "E6_T2", "E6_T3", "E6_T4" } },
                        new TechTreeData.Era { Name = "EraShort", Index = 7, TierCosts = new[] { "S_T1" } },
                        new TechTreeData.Era { Name = "EraUnread", Index = 8 },
                    };
                    Check("era cost: a technology on a tier its era names no cost for, and without a cost of its own, is said to cost nothing, with the eras that do have a cost",
                          TechTreeWindow.EraCostNote(costEras, "Era5", 3, "Tier4", "") == "Era5 has no research cost for Tier4, and this technology has no cost of its own: in the game it would cost NOTHING. Eras with a cost for Tier4: Era6."
                          && TechTreeWindow.EraCostNote(costEras, "EraShort", 1, "Tier2", "").StartsWith("EraShort has no research cost for Tier2,"),
                          TechTreeWindow.EraCostNote(costEras, "Era5", 3, "Tier4", ""));
                    Check("era cost: nothing to say with a cost of the era, a cost of the technology's own, or what could not be read",
                          TechTreeWindow.EraCostNote(costEras, "Era6", 3, "Tier4", "") == "" && TechTreeWindow.EraCostNote(costEras, "Era5", 2, "Tier3", "") == ""
                          && TechTreeWindow.EraCostNote(costEras, "Era5", 3, "Tier4", "Own_Cost") == "" && TechTreeWindow.EraCostNote(costEras, "EraUnread", 3, "Tier4", "") == ""
                          && TechTreeWindow.EraCostNote(costEras, "Era9", 3, "Tier4", "") == "" && TechTreeWindow.EraCostNote(costEras, "Era5", -1, "", "") == "" && TechTreeWindow.EraCostNote(null, "Era5", 3, "Tier4", "") == "");
                    Check("era-exclusive: the side strip says until when it can be researched",
                          TechTreeWindow.ExclusiveNote("Era6") == "Exclusive to its era: an empire can research it only while the empire's technological era is no later than Era6. Moving it to an earlier era shortens that time."
                          && TechTreeWindow.ExclusiveNote("").Contains("no later than the technology's era."));

                    // ---- the card's texts and image: the GUID conversion, the notes ----
                    // A real pair of the ENC project: Assets/Resources/Images/diplomat512.png (its .meta) and the image of
                    // Technology_Era1_Diplomacy's UI mapper (TechnologyUIMappersENC.asset).
                    Check("image GUID: a Unity GUID as the four ints the game's mapper stores, and back",
                          TechTreeData.UnityGuidToAmplitude("0be8caed819adde4d9be34986d03cb03", out int ga, out int gb, out int gc, out int gd)
                          && ga == -559116624 && gb == 1323149592 && gc == -1992037475 && gd == 817639638 && TechTreeData.AmplitudeGuidToUnity(ga, gb, gc, gd) == "0be8caed819adde4d9be34986d03cb03"
                          && TechTreeData.UnityGuidToAmplitude("0BE8CAED819ADDE4D9BE34986D03CB03", out int ua, out _, out _, out _) && ua == ga
                          && !TechTreeData.UnityGuidToAmplitude("0be8caed819adde4d9be34986d03cb0", out _, out _, out _, out _) && !TechTreeData.UnityGuidToAmplitude("0be8caed819adde4d9be34986d03cbzz", out _, out _, out _, out _)
                          && !TechTreeData.UnityGuidToAmplitude(null, out _, out _, out _, out _),
                          ga + "," + gb + "," + gc + "," + gd);
                    Check("title note: nothing for a typed title; an empty one, a localization key kept or replaced, is said",
                          TechTreeWindow.TextNote("Writing", "Writing", "Writing", "Text", "Text") == ""
                          && TechTreeWindow.TextNote("Writing", "Writing", " ", "", "") == "The title is empty: the game would show no name."
                          && TechTreeWindow.TextNote("%TechWritingTitle", "Writing", "%TechWritingTitle", "", "").StartsWith("The title is the localization key %TechWritingTitle, shown as \"Writing\".")
                          && TechTreeWindow.TextNote("%TechWritingTitle", "Writing", "Scribes", "", "") == "This replaces the localization key %TechWritingTitle with the text itself, the same in every language."
                          && TechTreeWindow.TextNote("A", "A", "A", "%Desc", "New").Contains("replaces the localization key %Desc"),
                          TechTreeWindow.TextNote("%TechWritingTitle", "Writing", "%TechWritingTitle", "", ""));
                    Check("image note: what the image is, staged or not, and where the project's other technology pictures are",
                          TechTreeWindow.ImageNote("", "", false, "") == "No image is set." && TechTreeWindow.ImageNote(BlueGuid, "Assets/Icons/drill_blue.png", true, "Assets/Icons") == "New image (staged): drill_blue.png."
                          && TechTreeWindow.ImageNote(BlueGuid, "Assets/Icons/drill_blue.png", true, "Assets/Resources/Images") == "New image (staged): drill_blue.png. The project's other technology pictures are in Assets/Resources/Images."
                          && TechTreeWindow.ImageNote("0123456789abcdef0123456789abcdef", "", true, "Assets/Resources/Images") == "New image (staged): one of the game's own images."
                          && TechTreeWindow.ImageNote(BlueGuid, "Assets/Icons/drill_blue.png", false, "Assets/Icons") == "",
                          TechTreeWindow.ImageNote(BlueGuid, "Assets/Icons/drill_blue.png", true, "Assets/Resources/Images"));

                    // ---- the artwork is on top of its card (the user, 2026-11-22: the title's line ran through the images).
                    // T's artwork is opaque red. The line under the title runs from the card's left to its right, so it
                    // passes where the artwork stands; a pixel there says which of the two was drawn last.
                    VSet("_selected", null); VSet("_zoom", 1f);
                    VSet("_pan", new Vector2(60f, 120f) - new Vector2((int)VCall("EffX", N("Technology_T")) * 74f, (int)VCall("EffY", N("Technology_T")) * 22f));
                    TechTreeWindow.AfterCanvasDrawn = Capture;
                    viewer.SendEvent(new Event { type = EventType.Layout }); viewer.SendEvent(new Event { type = EventType.Repaint });
                    TechTreeWindow.AfterCanvasDrawn = null;
                    var cardNow = (Rect)VCall("NodeRect", N("Technology_T"));
                    float lineY = cardNow.y + 0.29f * cardNow.height;
                    var onArt = Around(new Vector2(cardNow.x + 280f, lineY), 3);          // the title's line, where the artwork is
                    var onCard = Around(new Vector2(cardNow.x + 250f, cardNow.y + 80f), 3);   // the card's face, where the artwork is
                    var offArt = Around(new Vector2(cardNow.x + 100f, cardNow.y + 80f), 3);   // the card's face, left of the artwork
                    var outside = Around(new Vector2(cardNow.x + 350f, cardNow.y - 30f), 3);  // the artwork where it stands out of the card
                    Check("artwork, control: the drill reads the window's pixels, and the card stands where the check looks",
                          shot != null && Near(cardNow.position, 60f, 120f) && offArt.Count == 49 && !offArt.Any(Red) && outside.Count == 49 && outside.All(Red),
                          (shot == null ? "no pixels" : shotWidth + "x" + shotHeight + " scale " + shotScale) + " card " + cardNow + " left " + (offArt.Count > 0 ? offArt[0].ToString() : "-") + " outside " + (outside.Count > 0 ? outside[0].ToString() : "-"));
                    Check("artwork: drawn on top of its card: neither the title's line nor the card's face shows through it",
                          onArt.Count == 49 && onArt.All(Red) && onCard.Count == 49 && onCard.All(Red),
                          "on the line " + onArt.Count(Red) + " of " + onArt.Count + " red, on the face " + onCard.Count(Red) + " of " + onCard.Count);

                    // T's artwork stands over S's card (S is five rows under T). A click there: the artwork is on top, so T.
                    // Then S selected: its gold edge must show over T's artwork (review B, 2026-11-22: a neighbour's
                    // artwork covered the selected card's edge).
                    var sCard = (Rect)VCall("NodeRect", N("Technology_S"));
                    var tArt = TechTreeWindow.ArtRect(cardNow);
                    var both = Rect.MinMaxRect(Mathf.Max(sCard.xMin, tArt.xMin), Mathf.Max(sCard.yMin, tArt.yMin), Mathf.Min(sCard.xMax, tArt.xMax), Mathf.Min(sCard.yMax, tArt.yMax));
                    // NodeRect, ArtRect and NodeAt all speak the window's coordinates (the canvas's clip is the window's own offset)
                    Vector2 click = both.center, beside = new Vector2(sCard.x + 20f, sCard.y + 60f), canvasOnly = new Vector2(tArt.xMax - 5f, tArt.yMin + 5f);
                    var hit = (TechTreeData.Node)VCall("NodeAt", click);
                    var besideHit = (TechTreeData.Node)VCall("NodeAt", beside);
                    var emptyHit = (TechTreeData.Node)VCall("NodeAt", canvasOnly);
                    Check("artwork: a click where an artwork stands over another card selects the artwork's card; beside the artwork, the card under the click; on the canvas, nothing",
                          both.width > 20f && both.height > 20f && hit != null && hit.Name == "Technology_T" && besideHit != null && besideHit.Name == "Technology_S"
                          && !cardNow.Contains(canvasOnly) && !sCard.Contains(canvasOnly) && emptyHit == null,
                          "overlap " + both + " hit " + (hit?.Name ?? "none") + " beside " + (besideHit?.Name ?? "none") + " empty " + (emptyHit?.Name ?? "none"));
                    VSet("_selected", N("Technology_S"));
                    TechTreeWindow.AfterCanvasDrawn = Capture;
                    viewer.SendEvent(new Event { type = EventType.Layout }); viewer.SendEvent(new Event { type = EventType.Repaint });
                    TechTreeWindow.AfterCanvasDrawn = null;
                    var edge = Around(new Vector2(both.center.x, sCard.yMin + 0.5f), 0);   // the top edge of S, under T's artwork
                    var inside = Around(new Vector2(both.center.x, both.center.y), 1);     // inside S, under T's artwork: still red
                    bool gold(Color32 c) => c.r > 200 && c.g > 170 && c.b > 90 && c.b < 190;
                    Check("artwork: the selected card's gold edge is drawn over a neighbour's artwork; inside the card the artwork stays on top",
                          edge.Count == 1 && gold(edge[0]) && inside.Count == 9 && inside.All(Red),
                          "edge " + (edge.Count > 0 ? edge[0].ToString() : "-") + " inside " + (inside.Count > 0 ? inside[0].ToString() : "-"));
                    // the artworks of one draw only: the list is rebuilt per repaint (without that, old rectangles were drawn again)
                    viewer.SendEvent(new Event { type = EventType.Layout }); viewer.SendEvent(new Event { type = EventType.Repaint });
                    var artList = (System.Collections.IList)typeof(TechTreeWindow).GetField("_artDraws", Private).GetValue(viewer);
                    Check("artwork: each repaint draws the artworks of that view, once", artList.Count == 1, artList.Count + " artworks listed");
                    // a technology without a card in the game is faint, its artwork too
                    VCall("SetVisibility", N("Technology_T"), 0);
                    TechTreeWindow.AfterCanvasDrawn = Capture;
                    viewer.SendEvent(new Event { type = EventType.Layout }); viewer.SendEvent(new Event { type = EventType.Repaint });
                    TechTreeWindow.AfterCanvasDrawn = null;
                    var faint = Around(new Vector2(cardNow.x + 350f, cardNow.y - 30f), 1);   // the artwork over the bare canvas
                    Check("artwork: a switched-off technology's artwork is drawn faint", faint.Count == 9 && faint.All(c => c.r > 70 && c.r < 170 && c.g < 60), faint.Count > 0 ? faint[0].ToString() : "-");
                    VCall("SetVisibility", N("Technology_T"), -1);
                    // a staged image is the one the card draws (blue where T's own is red)
                    VCall("SetImage", N("Technology_T"), BlueGuid);
                    TechTreeWindow.AfterCanvasDrawn = Capture;
                    viewer.SendEvent(new Event { type = EventType.Layout }); viewer.SendEvent(new Event { type = EventType.Repaint });
                    TechTreeWindow.AfterCanvasDrawn = null;
                    var staged = Around(new Vector2(cardNow.x + 350f, cardNow.y - 30f), 1);
                    Check("image: the card draws a staged image at once", staged.Count == 9 && staged.All(Blue), staged.Count > 0 ? staged[0].ToString() : "-");
                    VCall("SetImage", N("Technology_T"), N("Technology_T").ArtGuid);
                    Check("    picked back to its own image: no change staged", !(bool)VCall("IsEdited", N("Technology_T")));
                    VSet("_selected", null);

                    // The card's row must load its image ITSELF. The side strip asks for the selected technology's icons
                    // too (a planted "the row is never drawn" survived on that, 2026-11-22), so: nothing selected, the
                    // loaded images forgotten, one draw.
                    var art = (System.Collections.IDictionary)typeof(TechTreeWindow).GetField("_art", Private).GetValue(viewer);
                    bool loadedBefore = art.Contains(IconPath);
                    VSet("_selected", null); art.Clear();
                    viewer.SendEvent(new Event { type = EventType.Layout }); viewer.SendEvent(new Event { type = EventType.Repaint });
                    Check("unlock row: the viewer's own card (T) draws its row: with nothing selected, one draw loads the project image as a texture",
                          N("Technology_T").Unlocks.Count == 10 && loadedBefore && art.Contains(IconPath) && (art[IconPath] as Texture) != null
                          && TechTreeWindow.PlanUnlockRow(new Rect(0, 0, 310, 116), N("Technology_T").Unlocks).Count == 6,
                          N("Technology_T").Unlocks.Count + " before " + loadedBefore + " after " + art.Contains(IconPath));
                    break;
                }
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
    public TechTreeDrillTypes.Reference EraReference;   // the game's shape: a struct that keeps one serialized string
}

// Stands in for the game's TechnologyUIMapper: the two fields a position is written to.
public class TechTreeDrillMapper : ScriptableObject
{
    public string Title = "", Description = "";
    public List<TechTreeDrillTypes.DrillImage> Images = new List<TechTreeDrillTypes.DrillImage>();
    public int TechTreeX, TechTreeY;
}

// An asset that is none of the game's: no Images list, no texts.
public class TechTreeDrillPlain : ScriptableObject { }

// Stand-ins of the game's shapes for TechTreeUnlocks, which reads by NAME: the effect types' names, the fields'
// names, and for a UI mapper the serialized paths Images[i].serializableKey / .Value.guid.a..d. They keep the game's
// KINDS of shape too, because the review of 2026-11-22 planted 125 defects and the friendlier first stand-ins let
// these through: the override is null unless set (the game's default and what the assets store), the faction
// operator is an enum, a reference is a struct with a private field, UnitDefinition is abstract with concrete
// subclasses, and the constructible base type has the game's full name (the index finds it by that).
namespace Amplitude.Mercury.Data.Simulation
{
    public enum TechnologyTiers { Tier1, Tier2, Tier3, Tier4 }
    public class EraDefinition : ScriptableObject
    {
        [Serializable] public struct TechnologyCostAndTier { public TechnologyTiers Tier; public TechTreeDrillTypes.Reference TechnologyCostRpnReference; }
        public int EraIndex;
        public TechnologyCostAndTier[] TechnologyCostRpnPerTier;
    }
    public abstract class ConstructibleDefinition : ScriptableObject { public int Level; public TechTreeDrillTypes.FactionPrerequisite FactionPrerequisite; }
}

namespace TechTreeDrillTypes
{
    using Amplitude.Mercury.Data.Simulation;

    // The game's DatatableElementReference is a struct: the serialized string, and an in-memory name (a StaticString in
    // the game) built from it by InitializeStaticStrings, not serialized.
    [Serializable]
    public struct Reference
    {
        [SerializeField] string serializableElementName;
        [NonSerialized] string elementName;
        public Reference(string name) { serializableElementName = name; elementName = name; }
        public string ElementName => elementName;
        public void InitializeStaticStrings() { elementName = serializableElementName; }
    }
    public enum DrillDlc { None = 0, Dlc6 = 6 }
    [Serializable] public struct DlcPrerequisite { public DrillDlc DownloadableContent; }
    public abstract class SimulationEventEffect { public string UIMapperOverride; public bool Hidden; public DlcPrerequisite DlcPrerequisite; }
    public class SimulationEventEffect_UnlockConstructible : SimulationEventEffect { public Reference[] ConstructibleReferences; }
    public class SimulationEventEffect_UnlockSettlementStartingPackage : SimulationEventEffect { public Reference StartingPackageReference; }
    public class SimulationEventEffect_ApplyDescriptor : SimulationEventEffect { public Reference Descriptor; }
    public class SimulationEventEffect_AddEmpireBattleAbility : SimulationEventEffect { public Reference BattleAbilityDefinition; }
    public class SimulationEventEffect_UnlockResource : SimulationEventEffect { public Reference[] ResourceReferences; }
    public class SimulationEventEffect_ApplyCostModifier : SimulationEventEffect { public Reference CostModifierReference; }
    public enum DrillAction { CuttingForest, MoveCapital, ReinforceBattle }
    public class SimulationEventEffect_UnlockAction : SimulationEventEffect { public DrillAction ActionType; }
    public class SimulationEventEffect_ProhibitAction : SimulationEventEffect { public DrillAction ActionType; }
    public class SimulationEventEffect_AddFame : SimulationEventEffect { }
    public class SimulationEventEffect_ResetAllCivics : SimulationEventEffect { }
    public class SimulationEventEffect_UpgradeAllRoads : SimulationEventEffect { }
    public class SimulationEventEffect_ForceCalendar : SimulationEventEffect { }
    public class TechDefinition { public object[] SimulationEventEffects; }

    public enum FactionOperator { Any, AnyInFactionsHistory, None }
    [Serializable]
    public struct FactionPrerequisite
    {
        public FactionOperator Operator;
        [SerializeField] string[] serializableFactionNames;
        public FactionPrerequisite(FactionOperator op, string[] names) { Operator = op; serializableFactionNames = names; }
    }
    public abstract class UnitDefinition : ConstructibleDefinition { }
    public class LandUnitDefinition : UnitDefinition { }
    public class ExtensionDistrictDefinition : ConstructibleDefinition { }
    public class AirportDefinition : ExtensionDistrictDefinition { }
    public class SettlementEvolutionDefinition : ConstructibleDefinition { }
    public class SettlementImprovementDefinition : ConstructibleDefinition { }
    public class ResourceDefinition : ScriptableObject { }
    public class CostModifierDefinition : ScriptableObject { }

    [Serializable] public struct DrillGuid { public int a, b, c, d; }
    [Serializable] public struct DrillTexture { public DrillGuid guid; }
    [Serializable] public struct DrillImage { public DrillTexture Value; public string serializableKey; }
    public class DrillMapper : ScriptableObject { public string Title; public List<DrillImage> Images = new List<DrillImage>(); }
    public class DrillConstructibleMapper : DrillMapper { public int SortPriority; }
}
