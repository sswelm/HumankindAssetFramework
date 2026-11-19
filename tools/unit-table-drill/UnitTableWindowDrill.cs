using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEditor;
using UnityEngine;

// Render the plugin's real Units frame without BepInEx Awake or a game. Mouse routing in the game's host is not tested.
public sealed class UnitTableWindowDrill : EditorWindow
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static string output;
    static readonly System.Text.StringBuilder log = new System.Text.StringBuilder();
    static int failed;
    object plugin; Type pluginType; int draws, phase; double deadline; Rect drawn;
    static readonly Vector2[] Sizes = { new Vector2(600, 350), new Vector2(500, 250), new Vector2(420, 160), new Vector2(1000, 600) };
    FieldInfo Field(string name) => pluginType.GetField(name, Private);
    Rect Bounds { get => (Rect)Field("unitsRect").GetValue(plugin); set => Field("unitsRect").SetValue(plugin, value); }
    static void Check(string name, bool ok) { log.AppendLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }

    public static void Run()
    {
        var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "-hafUnitTableOutput");
        output = args[at + 1];
        try
        {
            var window = CreateInstance<UnitTableWindowDrill>();
            window.pluginType = Assembly.Load("HumankindAssetFramework").GetType("HumankindAssetFramework.Plugin", true);
            // The frame uses only managed fields. Avoid MonoBehaviour construction and the plugin's game startup.
            window.plugin = FormatterServices.GetUninitializedObject(window.pluginType);
            window.Bounds = new Rect(new Vector2(10, 10), Sizes[0]);
            var rows = window.Field("unitRows"); rows.SetValue(window.plugin, Activator.CreateInstance(rows.FieldType));
            window.position = new Rect(80, 80, 1100, 750);
            window.deadline = EditorApplication.timeSinceStartup + 30;
            window.ShowUtility(); window.Repaint();
            EditorApplication.update += window.Step;
        }
        catch (Exception ex) { Finish(ex); }
    }

    void OnGUI()
    {
        if (plugin == null || pluginType == null) return;
        try
        {
            BeginWindows();
            drawn = (Rect)pluginType.GetMethod("DrawUnitsFrame", Private).Invoke(plugin, null);
            EndWindows();
            if (Event.current.type == EventType.Repaint) draws++;
        }
        catch (Exception ex) { EditorApplication.update -= Step; Finish(ex); }
    }

    void Step()
    {
        try
        {
            if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("window did not draw");
            if (draws < 2)
            {
                SendEvent(new Event { type = EventType.Layout });
                SendEvent(new Event { type = EventType.Repaint });
                Repaint(); return;
            }
            var size = Sizes[phase];
            Check(size + ": rendered width", Math.Abs(drawn.width - size.x) < 0.1f);
            Check(size + ": rendered height", Math.Abs(drawn.height - size.y) < 0.1f);
            Check(size + ": requested size survives layout", Bounds.size == size);
            if (++phase < Sizes.Length)
            {
                Bounds = new Rect(new Vector2(10, 10), Sizes[phase]); draws = 0; Repaint(); return;
            }
            EditorApplication.update -= Step; Close(); Finish(null);
        }
        catch (Exception ex) { EditorApplication.update -= Step; Finish(ex); }
    }

    static void Finish(Exception ex)
    {
        if (ex != null) { failed++; log.AppendLine("FAIL " + ex); }
        log.AppendLine("TOTAL failed " + failed);
        File.WriteAllText(output, log.ToString()); Debug.Log(log.ToString());
        EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
