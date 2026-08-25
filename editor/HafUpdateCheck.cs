// HafUpdateCheck.cs — the "a new version exists" signal a git install never gets (2026-08-25).
//
// A git-URL package has no registry to ask, so Package Manager shows the installed version with a green check no
// matter what has shipped — verified live: a release, an editor restart and a refresh showed nothing, and the
// user only found 0.4.5 by pressing Update on faith. The package knows its own version and the repository it was
// installed from, so it asks GitHub itself: one anonymous GET of the raw editor/package.json on master, compared
// against the installed manifest.
//
// Deliberately quiet and disclosed, in line with "nothing runs on its own":
//   * READ-ONLY — fetches one public file from the same repo the package came from; sends nothing, no telemetry.
//   * once per day at most, guest installs only (a file:/embedded install reads its own working copy — nothing
//     a remote check could tell it), result is ONE console line naming the fix and how to disable the check.
//   * Tools ▸ HAF ▸ Check for Updates… runs the same check on demand, and also reports "up to date".
internal static class HafUpdateCheck
{
    const string RawManifest =
        "https://raw.githubusercontent.com/sswelm/HumankindAssetFramework/master/editor/package.json";
    internal const string PrefAuto = "HAF.UpdateCheck";        // bool, default ON (read-only fetch, log-only result)
    const string PrefLast = "HAF.UpdateCheck.LastTicks";

    [UnityEditor.MenuItem("Tools/HAF/Check for Updates…", false, 31)]
    static void Manual() => Check(manual: true);

    [UnityEditor.InitializeOnLoadMethod]
    static void AutoDaily()
    {
        try
        {
            if (!HafPackageContext.RunningAsPackage) return;
            if (!UnityEditor.EditorPrefs.GetBool(PrefAuto, true)) return;
            long last = long.TryParse(UnityEditor.EditorPrefs.GetString(PrefLast, "0"), out var t) ? t : 0;
            if ((System.DateTime.UtcNow - new System.DateTime(last, System.DateTimeKind.Utc)).TotalHours < 24) return;
            UnityEditor.EditorPrefs.SetString(PrefLast, System.DateTime.UtcNow.Ticks.ToString());
            Check(manual: false);
        }
        catch { }   // an update check must never break editor start-up
    }

    static void Check(bool manual)
    {
        var req = UnityEngine.Networking.UnityWebRequest.Get(RawManifest);
        req.timeout = 10;
        var op = req.SendWebRequest();
        op.completed += _ =>
        {
            try
            {
                if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                { if (manual) UnityEngine.Debug.LogWarning("[HAF] update check failed: " + req.error); return; }
                var m = System.Text.RegularExpressions.Regex.Match(
                    req.downloadHandler.text, "\"version\"\\s*:\\s*\"([0-9.]+)\"");
                if (!m.Success)
                { if (manual) UnityEngine.Debug.LogWarning("[HAF] update check: could not read the remote version."); return; }
                string remote = m.Groups[1].Value;
                string local = HafPackageContext.Version;
                if (string.IsNullOrEmpty(local)) local = "0.0.0";
                if (Newer(remote, local))
                    UnityEngine.Debug.Log(
                        $"[HAF] HAF Authoring Tools {remote} is available (installed: {local}). " +
                        "Window ▸ Package Manager ▸ HAF Authoring Tools ▸ Update — updates never touch your " +
                        "project's data. (This once-a-day check reads one public file from the package's own " +
                        $"repository and sends nothing; disable it with EditorPrefs '{PrefAuto}' = false.)");
                else if (manual)
                    UnityEngine.Debug.Log($"[HAF] up to date — installed {local}; newest release is {remote}.");
            }
            catch (System.Exception e) { if (manual) UnityEngine.Debug.LogWarning("[HAF] update check: " + e.Message); }
            finally { req.Dispose(); }
        };
    }

    // a > b for "X.Y.Z" (missing segments count as 0; non-numeric segments as 0 — additive-only versioning here)
    static bool Newer(string a, string b)
    {
        var pa = a.Split('.'); var pb = b.Split('.');
        for (int i = 0; i < System.Math.Max(pa.Length, pb.Length); i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb) return va > vb;
        }
        return false;
    }
}
