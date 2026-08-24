// VanillaConsoleNoiseFilter.cs  (LOCAL, editor-only, removable)
//
// Quiets a few harmless vanilla Mod-Tools-SDK console errors thrown while mounting/loading vanilla scenario data at
// startup (a VANILLA SDK bug — a fresh empty project throws the same lines; see NarrativeEventDefinition.
// EnumerateSimulationEventVariables hitting a null array element from a scenario type the SDK doesn't include).
//
// IMPORTANT — it does NOT silently hide anything:
//   * Matching messages are NOT shown in the console, BUT they are appended to  Logs/SuppressedConsoleNoise.log
//     (next to the project) and counted, and the filter announces itself ONCE per session.
//   * So if a *real* problem ever matches a pattern (e.g. a broken reference in YOUR OWN narrative event also goes
//     through EnumerateSimulationEventVariables), it's still recorded in that file — nothing is lost, just relocated.
//   * To see everything live again, delete this file. To tune what's matched, edit Patterns[].
//
// Editor only (Editor folder, [InitializeOnLoad]) — never ships, never runs in-game.

#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
internal static class VanillaConsoleNoiseFilter
{
    // Substrings identifying the known-harmless vanilla SDK noise. Matched against message text, exception message,
    // exception stack trace, and throwing method name. Keep these as specific as possible.
    private static readonly string[] Patterns =
    {
        "EnumerateSimulationEventVariables",             // NarrativeEventDefinition NRE at startup
        "Datatable element collection guid is missing",  // vanilla collection-mount warning spam
        // The SDK's ModuleEditor (mod-build window) aborts a layout scope when a build is CANCELLED and spams this
        // per repaint (ModuleEditor.cs:438, GUI/Scope:Dispose). Message-only match — a LogFormat carries no stack to
        // pair on — so a genuine layout bug in OUR windows would be relocated too; acceptable because relocated ≠
        // lost (see the side-log), and ours would show up loudly in other ways (broken window layout on screen).
        "EndLayoutGroup: BeginLayoutGroup must be called first",
        "Missing AssetBundleLoadingConfiguration",       // SDK asset-bundle config warning at editor load — harmless in a mod project
        "Load AssetBundleLoadingConfiguration fail",      // the paired info line right after it
    };

    // Two-part exception rules: BOTH the message AND the stack trace must match. For Unity-internal noise whose
    // message alone is too generic to filter safely — our own EditorWindows can throw the same message for a REAL
    // layout bug, and theirs must stay red (their stacks don't go through the Inspector's IMGUI host).
    private static readonly (string msg, string stack)[] ExceptionPairs =
    {
        // IMGUI Layout/Repaint control-count mismatch inside the INSPECTOR pane ("Getting control N's position in a
        // group with only M controls when doing repaint", rethrown as ImmediateModeException). A single-frame repaint
        // abort in whatever custom/SDK inspector is hosted for the current selection — harmless, recurring, Unity
        // 2021-era. Only suppressed when the stack shows the Inspector IMGUI host (InspectorElement).
        ("position in a group with only", "InspectorElement"),
    };

    private static int _count;
    private static bool _announced;

    internal const string PrefOn = "HAF.Console.Filter";

    static VanillaConsoleNoiseFilter()
    {
        // CONTEXTUAL DEFAULT (HafPackageContext). This replaces Debug.unityLogger.logHandler process-wide, and one
        // of the patterns ("EndLayoutGroup: BeginLayoutGroup must be called first") is matched on message text
        // alone — so in a guest project it could relocate a layout error thrown by THEIR windows, not the SDK's.
        // Relocated is not lost (the side-log keeps everything), but a stranger has no reason to know that, and an
        // authoring tool quietly taking over the console is exactly what makes a new user uninstall. Home project:
        // on, as before. Installed package: off until asked.
        if (!EditorPrefs.GetBool(PrefOn, HafPackageContext.AutoDefault)) return;

        // Keep the side-log bounded (1,500+ entries accumulate fast — the SDK NRE fires once per narrative-event
        // validate). Over 2 MB the file is rotated to .old (one generation kept), so it never grows unbounded.
        try
        {
            var p = LogFile();
            if (p != null && File.Exists(p) && new FileInfo(p).Length > 2_000_000)
            { File.Copy(p, p + ".old", true); File.Delete(p); }
        }
        catch { }
        if (Debug.unityLogger.logHandler is FilterHandler) return;   // already installed
        Debug.unityLogger.logHandler = new FilterHandler(Debug.unityLogger.logHandler);
    }

    [MenuItem("Tools/HAF/Suppressed Console Noise — open log")]
    private static void OpenSuppressedLog()
    {
        var p = LogFile();
        if (p != null && File.Exists(p)) EditorUtility.OpenWithDefaultApp(p);
        else EditorUtility.DisplayDialog("Suppressed console noise", "Nothing has been suppressed yet — the side-log doesn't exist.", "OK");
    }

    [MenuItem("Tools/HAF/Suppressed Console Noise — clear log")]
    private static void ClearSuppressedLog()
    {
        try
        {
            var p = LogFile();
            if (p != null && File.Exists(p)) File.Delete(p);
            if (p != null && File.Exists(p + ".old")) File.Delete(p + ".old");
            Debug.Log("[VanillaNoiseFilter] side-log cleared.");
        }
        catch (Exception e) { Debug.LogWarning("[VanillaNoiseFilter] clear failed: " + e.Message); }
    }

    private static bool Matches(string s) =>
        !string.IsNullOrEmpty(s) && Patterns.Any(p => s.IndexOf(p, StringComparison.Ordinal) >= 0);

    private static string LogFile()
    {
        try { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Logs", "SuppressedConsoleNoise.log"); }
        catch { return null; }
    }

    // Relocate (don't drop): count it, append it to the side-log, and announce ONCE via the inner handler so the
    // announcement itself isn't filtered.
    private static void Record(ILogHandler inner, string detail)
    {
        _count++;
        var path = LogFile();
        if (path != null)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {detail}\n\n"); }
            catch { }
        }
        if (!_announced)
        {
            _announced = true;
            inner.LogFormat(LogType.Log, null,
                "[VanillaNoiseFilter] Active — moving console lines matching {{ {0} }} to {1} (still recorded, not lost). " +
                "Delete Assets/Scripts/Editor/VanillaConsoleNoiseFilter.cs to see everything live.",
                string.Join(" | ", Patterns), path ?? "(file unavailable)");
        }
    }

    private sealed class FilterHandler : ILogHandler
    {
        private readonly ILogHandler _inner;
        public FilterHandler(ILogHandler inner) { _inner = inner; }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            if (exception != null && (Matches(exception.StackTrace) || Matches(exception.Message)
                                      || (exception.TargetSite != null && Matches(exception.TargetSite.Name))))
            {
                Record(_inner, "EXCEPTION " + exception.GetType().Name + ": " + exception.Message + "\n" + exception.StackTrace);
                return;
            }
            if (exception != null && exception.Message != null && exception.StackTrace != null)
                foreach (var p in ExceptionPairs)
                    if (exception.Message.IndexOf(p.msg, StringComparison.Ordinal) >= 0
                        && exception.StackTrace.IndexOf(p.stack, StringComparison.Ordinal) >= 0)
                    {
                        Record(_inner, "EXCEPTION " + exception.GetType().Name + ": " + exception.Message + "\n" + exception.StackTrace);
                        return;
                    }
            _inner.LogException(exception, context);
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            if (Matches(format) || (args != null && args.Any(a => Matches(a as string))))
            {
                string msg = format;
                try { msg = string.Format(format ?? "", args ?? Array.Empty<object>()); } catch { }
                Record(_inner, "[" + logType + "] " + msg);
                return;
            }
            _inner.LogFormat(logType, context, format, args);
        }
    }
}
#endif
