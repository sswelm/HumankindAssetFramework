// HafPaths.cs — the machine-specific folders the editor needs, RESOLVED instead of hardcoded.
//
// The Community folder (Humankind's mod folder) used to be `const string @"C:\GameData\Humankind\Community"`,
// copied into THREE files: DistrictFactoryWindow, ShipStatusWindow and HafCli.CleanExport. That is one
// developer's junctioned layout. On any other machine all three silently did NOTHING — every use is guarded by
// Directory.Exists, which is false there, and a guard that finds nothing reads exactly like "nothing to report".
// So the stale-bundle health check, the Ship Status verdict and the CLI's export clean were, off this box, three
// features that looked healthy and were not running at all.
//
// It cannot be read back from anywhere. Humankind COMPUTES the path (GetCommunityFolderPath() = GameDirectory +
// "/../Humankind/Community") and never records it: HKCU\Software\AMPLITUDE Studios\Humankind holds Unity
// PlayerPrefs only — window positions, FloatingWindow.* — with no path value at all. So it is DERIVED here.
//
// WHY Environment.GetFolderPath AND NOT a literal "%USERPROFILE%\Documents\Humankind\Community": on the machine
// this was written, Documents is `D:\OneDrive\Documenten` — OneDrive-redirected, on a different drive, and
// LOCALIZED (Dutch) — and `Humankind` inside it is a REPARSE POINT to C:\GameData\Humankind. A literal path is
// wrong there three separate ways, and neither OneDrive redirection nor a localized folder name is exotic: they
// are the common case, the second one for most non-English players. GetFolderPath performs the same resolution
// the shell does, so it follows the redirect, the localization and the junction transparently.
//
// Resolution order — and the last step is the whole point: DETECT, and when detection fails ASK. Never guess a
// path, never fail silently.
//   1. a saved override — written by the folder picker, and settable by hand for an unusual install
//   2. <Documents>/Humankind/Community, if it actually exists
//   3. null — the caller PROMPTS (a GUI window) or FAILS LOUDLY (batch mode, which has nobody to ask)
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

internal static class HafPaths
{
    // Public so a stuck adopter can set it by hand, and so the CLI's error message can name it.
    internal const string PrefCommunity = "HAF.Paths.CommunityDir";

    internal const string CommunityHelp =
        "Humankind's Community (mods) folder could not be found automatically. It is normally " +
        "Documents/Humankind/Community, but Documents may be redirected (OneDrive) or localized. " +
        "Point HAF at it once and the choice is remembered.";

    /// The Community folder, or null when it is not known. Callers MUST handle null — that is the
    /// "ask the user" signal, not an error.
    internal static string CommunityDir
    {
        get
        {
            string saved = EditorPrefs.GetString(PrefCommunity, "");
            if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved)) return saved;   // a saved path that
            // vanished (drive unplugged, game moved) deliberately falls through to detection rather than sticking.
            return Detected();
        }
    }

    /// Where it lives on a stock install. Null when that folder is not there.
    internal static string Detected()
    {
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) return null;
            string p = Path.Combine(Path.Combine(docs, "Humankind"), "Community");
            return Directory.Exists(p) ? p : null;
        }
        catch (Exception e)   // named, not swallowed: a folder we could not even probe is worth one line
        {
            Debug.LogWarning("[HAF] could not probe the Documents folder for Humankind/Community: " + e.Message);
            return null;
        }
    }

    /// GUI only (opens a modal picker). Returns the chosen folder, saved for next time, or null if cancelled.
    internal static string PromptForCommunityDir()
    {
        string start = CommunityDir;
        if (string.IsNullOrEmpty(start))
        {
            try { start = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch { start = ""; }
        }
        string picked = EditorUtility.OpenFolderPanel("Locate Humankind's Community (mods) folder", start ?? "", "");
        if (string.IsNullOrEmpty(picked)) return null;
        EditorPrefs.SetString(PrefCommunity, picked);
        Debug.Log("[HAF] Community folder set to: " + picked);
        return picked;
    }
}
