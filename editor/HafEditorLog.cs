using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// DURABLE EDITOR ACTION LOG (2026-08-19 logging audit; user: "shouldn't every button click be logged somewhere?").
// Unity's Editor.log is per-session (one generation survives as Editor-prev.log) and stamps no timestamps — so an
// editor drill older than two sessions was unreconstructable. This appends every HAF-prefixed Console line
// ("[Factory] …", "[AnimLab] …", "[HAF Backup] …", "[ShipStatus] …", "[Validate] …", …) to ONE timestamped file
// that survives restarts:  <project>/Logs/haf_editor_actions.log
// Result-bearing button presses already log their outcomes, so this captures the action trail for free; silent
// state changes (toggles, sliders, selection) remain unlogged by design — log the RESULT, not the mouse.
// Rotation: at ~5 MB the file moves to .old (one generation) — bounded disk, months of history.
// NOT in the backup groups by policy: logs are regenerated evidence, not configuration (see BuildGroups).
[InitializeOnLoad]
static class HafEditorLog
{
    static readonly string LogPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Logs", "haf_editor_actions.log");
    const long RotateBytes = 5 * 1024 * 1024;
    static readonly object gate = new object();

    static HafEditorLog()
    {
        Application.logMessageReceived += OnLog;   // main-thread Unity messages; threaded logs use …Threaded below
        Application.logMessageReceivedThreaded += OnLogThreaded;
    }

    static void OnLog(string msg, string stack, LogType type) => Append(msg, type);
    static void OnLogThreaded(string msg, string stack, LogType type) { if (!object.ReferenceEquals(System.Threading.Thread.CurrentThread, null)) Append(msg, type); }

    static void Append(string msg, LogType type)
    {
        if (string.IsNullOrEmpty(msg) || msg[0] != '[') return;   // HAF's own prefixed lines only — Unity noise stays out
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > RotateBytes)
                {
                    var old = LogPath + ".old";
                    try { File.Delete(old); } catch { }
                    File.Move(LogPath, old);
                }
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) +
                    "  " + (type == LogType.Log ? "INFO " : type == LogType.Warning ? "WARN " : "ERROR") + "  " + msg + "\n");
            }
        }
        catch { }   // a logging failure must never break the editor (or recurse via Debug.LogError)
    }
}
