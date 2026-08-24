// HafPackageContext.cs — AM I THE HOME PROJECT, OR A GUEST? (2026-08-24)
//
// Written the day the tools were first installed into a project that was not ENCReload. The .meta omission that
// broke that install was the visible failure; this is the one underneath it. Four classes run [InitializeOnLoad]
// the moment the package compiles, and every one of them defaulted ON:
//
//   HafDeleteGuard          intercepts EVERY asset delete under Assets/Databases, FactorySource, Scripts/Editor
//   BackupAuto (daily)      first editor load of a day: a full silent backup of assets AND configuration,
//                           to "D:/HAF_Backups" — a drive most machines do not have
//   BackupAuto (offsite)    a second zipped copy alongside it
//   VanillaConsoleNoiseFilter  replaces Debug.unityLogger.logHandler process-wide
//
// In ENCReload every one of those is wanted: they exist because work was lost once. In somebody else's project
// they are an authoring tool silently hooking their deletes, writing their assets to a drive letter it invented,
// and filtering their console — none of it asked for. That is not a bug in any one of them; it is the tools
// assuming they ARE the project, which is true here and false everywhere else.
//
// So the default becomes contextual rather than constant. UnityEditor.PackageManager tells us which we are:
// FindForAssembly returns null for scripts compiled from Assets/ (the home project) and a PackageInfo for scripts
// resolved from a package (a guest). Home keeps today's behaviour exactly; a guest gets an inert install that
// changes nothing until asked. Both are one EditorPrefs toggle away from the other.
//
// NOTE the prefs are machine-wide, not per-project: once a user opts in anywhere, that choice follows them. That
// is Unity's EditorPrefs, not a decision made here — worth knowing before reading a surprising default.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

internal static class HafPackageContext
{
    internal const string PackageName = "com.sswelm.haf-authoring";
    internal const string Version = "0.1.0";

    static int _asPackage = -1;   // -1 unknown, 0 home project, 1 installed package

    /// True when these scripts were resolved from a package rather than compiled out of the host's Assets/.
    internal static bool RunningAsPackage
    {
        get
        {
            if (_asPackage < 0)
            {
                bool v = false;
                try
                {
                    // Null for Assets/-compiled scripts; a PackageInfo when resolved from Packages/ or the cache.
                    v = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                            typeof(HafPackageContext).Assembly) != null;
                }
                catch (Exception e)   // never let a context probe break editor start-up
                {
                    Debug.LogWarning("[HAF] could not determine package context (" + e.Message +
                                     ") — assuming home project, automatic behaviour stays as configured.");
                    v = false;
                }
                _asPackage = v ? 1 : 0;
            }
            return _asPackage == 1;
        }
    }

    /// The default for anything that MUTATES the host project or process without being asked:
    /// on in the home project (where it is the point), off in a guest (where it is a surprise).
    internal static bool AutoDefault => !RunningAsPackage;
}

// ONE friendly line on first install, instead of silence — or, before this, two red errors. A new user who sees
// nothing assumes it failed; a new user who sees red uninstalls. Shown once per project (keyed by project path),
// not once per session, so it never becomes noise of its own.
[InitializeOnLoad]
static class HafFirstRunNotice
{
    static HafFirstRunNotice()
    {
        if (!HafPackageContext.RunningAsPackage) return;   // the home project does not need introducing to itself
        try
        {
            string key = "HAF.FirstRun." + Application.dataPath.GetHashCode().ToString("X8");
            if (EditorPrefs.GetBool(key, false)) return;
            EditorPrefs.SetBool(key, true);
            EditorApplication.delayCall += () =>
                Debug.Log("[HAF] HAF Authoring Tools " + HafPackageContext.Version + " installed — the tools are " +
                          "under Tools \u25B8 HAF.\n" +
                          "Nothing runs on its own in an installed package: automatic backups, the asset-delete " +
                          "guard and console filtering are all OFF, and this project has not been modified. " +
                          "Turn any of them on in Tools \u25B8 HAF \u25B8 Backup & Restore.");
        }
        catch { }   // a greeting must never be the thing that breaks an install
    }
}
