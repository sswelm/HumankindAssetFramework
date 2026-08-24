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

    static int _asPackage = -1;   // -1 unknown, 0 home project, 1 installed package
    static string _version;       // read from the manifest, never a const — see below

    /// The installed version, read from the package manifest itself. It was a `const string "0.1.0"` for one
    /// commit, and the first install of 0.2.0 greeted the user with "0.1.0" — a version number is exactly the
    /// kind of fact that must be derived, not restated. Empty string in the home project (no manifest to read).
    internal static string Version
    {
        get
        {
            if (_version == null)
            {
                try
                {
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                                   typeof(HafPackageContext).Assembly);
                    _version = info?.version ?? "";
                }
                catch { _version = ""; }
            }
            return _version;
        }
    }

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
                    // NOT "is this a package at all" — the HOME project consumes these scripts as a file: package
                    // too (Packages/manifest.json -> file:../../HumankindAssetFramework/editor), so a bare
                    // FindForAssembly != null classified ENCReload itself as a guest and silently defaulted its
                    // own delete guard and daily backups OFF. The split that means something is HOW the package
                    // is installed: a git/registry/tarball install is an immutable copy on a consumer's machine
                    // (guest); an embedded or local file: install is the developer's own working copy (home) —
                    // as is compiling straight out of Assets/ (info == null).
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                                   typeof(HafPackageContext).Assembly);
                    v = info != null && (info.source == UnityEditor.PackageManager.PackageSource.Git
                                      || info.source == UnityEditor.PackageManager.PackageSource.Registry
                                      || info.source == UnityEditor.PackageManager.PackageSource.LocalTarball);
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

    /// Resolve a Blender/converter helper under Tools/. Per FILE, not per layout: the host project's own
    /// Tools/<rel> wins (the home layout, and any project that vendors its own copies), else the package's
    /// shipped Tools~/<rel> (the '~' suffix keeps Unity's asset database out of it — no .meta files, nothing
    /// imported). When neither exists the project-relative path is returned anyway, so an error message names
    /// the conventional location rather than a package-cache hash path.
    internal static string ToolPath(params string[] rel)
    {
        string sub = Path.Combine(rel);
        try
        {
            string proj = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Tools", sub);
            if (File.Exists(proj) || Directory.Exists(proj)) return proj;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(HafPackageContext).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
            {
                string pkg = Path.Combine(info.resolvedPath, "Tools~", sub);
                if (File.Exists(pkg) || Directory.Exists(pkg)) return pkg;
            }
            return proj;
        }
        catch { return Path.Combine("Tools", sub); }
    }

    // ---- PACK IDENTITY (2026-08-24, the same afternoon as everything else in this file) ----
    //
    // The tools used to hardcode ONE pack: haf_packs/ENCReload, modId "enc". ConfigDir auto-detects the GAME's
    // BepInEx config — machine-global — so on any machine where ENC is installed as a player mod, the tools in a
    // DIFFERENT project loaded ENC's deployed pack.json and the bake tests tried to re-bake ENC's models there.
    // The first outside author to run them saw five model names he had never heard of failing in his own project
    // and concluded, reasonably, that the package had infested his system. Nothing was written — the registry was
    // only read — but an authoring tool showing you someone else's content as if it were yours is indistinguishable
    // from exactly that.
    //
    // So the pack the tools read and write is now THEIRS by default: an authored override if set, else derived
    // from the host project's own name. A guest install starts with an EMPTY pack and bakes into it; it can no
    // longer see, touch, or attempt to bake any other mod's pack. The home project keeps its historical identity
    // until its own override is set (its productName is not guaranteed to equal the pack folder shipped for years,
    // so deriving there would silently orphan the real registry — the one rename this class must never do).

    internal const string PrefPackName = "HAF.Pack.Name";     // the haf_packs/<name> folder (== HK module name by convention)
    internal const string PrefModId    = "HAF.Pack.ModId";    // the pack's modId in its pack.json

    static string _packName, _modId;

    /// The pack folder this project authors into: authored override > derived from the project's own name (guest)
    /// > the home project's historical identity.
    internal static string PackName
    {
        get
        {
            if (_packName == null)
            {
                string over = "";
                try { over = EditorPrefs.GetString(PrefPackName, ""); } catch { }
                _packName = !string.IsNullOrWhiteSpace(over) ? over.Trim()
                          : RunningAsPackage ? SanitizedProjectName
                          : "ENCReload";
            }
            return _packName;
        }
    }

    /// The modId written into a NEW pack.json (an existing file's own value always wins on load).
    internal static string DefaultModId
    {
        get
        {
            if (_modId == null)
            {
                string over = "";
                try { over = EditorPrefs.GetString(PrefModId, ""); } catch { }
                // a modId is a bare token other packs name in dependsOn/loadAfter — lowercase, no spaces
                _modId = !string.IsNullOrWhiteSpace(over) ? over.Trim().ToLowerInvariant().Replace(" ", "")
                       : RunningAsPackage ? SanitizedProjectName.ToLowerInvariant().Replace(" ", "")
                       : "enc";
            }
            return _modId;
        }
    }

    internal const string PrefBuildPrefix = "HAF.Pack.BuildPrefix";   // the "<Name>.<guid>.<version>" prefix of this mod's builds

    static string _buildPrefix;

    /// The prefix of this mod's BUILT module folders in Humankind's Community directory
    /// ("<ModuleName>.<guid>.<version>"). The module name is the mod's display title, SPACES INCLUDED
    /// ("Long Range Nuclear Missiles.…"), so this is the raw product name, not the sanitized pack token.
    /// Used by the build-freshness checks (Ship Status, the District Factory health row) and CleanExport.
    internal static string BuildPrefix
    {
        get
        {
            if (_buildPrefix == null)
            {
                string over = "";
                try { over = EditorPrefs.GetString(PrefBuildPrefix, ""); } catch { }
                if (!string.IsNullOrWhiteSpace(over)) _buildPrefix = over.Trim();
                else if (!RunningAsPackage) _buildPrefix = "ENCReload";
                else
                {
                    string raw = "";
                    try { raw = (Application.productName ?? "").Trim(); } catch { }
                    _buildPrefix = raw.Length > 0 ? raw : SanitizedProjectName;
                }
            }
            return _buildPrefix;
        }
    }

    /// The host project's name reduced to a safe folder/id token. productName first (the author's own title),
    /// then the project folder; never empty. Spaces survive — the pack folder name doubles as the Humankind
    /// MODULE name for the runtime's automatic load-order match, and module names routinely contain them.
    internal static string SanitizedProjectName
    {
        get
        {
            string raw = "";
            try { raw = Application.productName; } catch { }
            if (string.IsNullOrWhiteSpace(raw))
                try { raw = new DirectoryInfo(Directory.GetParent(Application.dataPath).FullName).Name; } catch { }
            var sb = new System.Text.StringBuilder();
            foreach (char c in raw ?? "")
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' ') sb.Append(c);
            string s = sb.ToString().Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length > 0 ? s : "MyHafPack";
        }
    }
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
            string ver = HafPackageContext.Version;
            EditorApplication.delayCall += () =>
                Debug.Log("[HAF] HAF Authoring Tools " + (ver.Length > 0 ? ver + " " : "") + "installed — the tools are " +
                          "under Tools \u25B8 HAF.\n" +
                          "Nothing runs on its own in an installed package: automatic backups, the asset-delete " +
                          "guard and console filtering are all OFF, and this project has not been modified. " +
                          "Turn any of them on in Tools \u25B8 HAF \u25B8 Backup & Restore.");
        }
        catch { }   // a greeting must never be the thing that breaks an install
    }
}
