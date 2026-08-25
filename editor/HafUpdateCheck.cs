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

    // IN-FLIGHT LATCH (2026-08-25, drilled into existence by the user hitting the gap three times). Between
    // "Update now" and the domain reload that installs the new version, the OLD assembly keeps answering the
    // menu — Package Manager already shows the new version while the running code still reports itself, so a
    // re-click re-offered an update that was already applied ("weird, I updated but it still says the previous
    // version"). SessionState survives the reload; the flag clears itself the first time the running version
    // matches what was being fetched.
    const string InflightKey = "HAF.UpdateCheck.InflightVersion";

    static void Check(bool manual)
    {
        string inflight = UnityEditor.SessionState.GetString(InflightKey, "");
        if (!string.IsNullOrEmpty(inflight))
        {
            if (inflight == HafPackageContext.Version)
                UnityEditor.SessionState.EraseString(InflightKey);   // the update landed — back to normal checks
            else
            {
                if (manual) UnityEngine.Debug.Log(
                    $"[HAF] update to {inflight} is in progress — Package Manager is fetching and Unity will " +
                    "reload when it's done. Nothing to do; check again after the reload.");
                return;
            }
        }

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
                {
                    // The MANUAL check doesn't just report — it offers to apply. Package Manager's own Update
                    // button is Client.Add() with the package's install URL, and PackageInfo carries that URL,
                    // so "found a newer version" can end in one click instead of a trip through another window.
                    // The DAILY check stays a console line: an unrequested modal dialog on editor start is
                    // exactly the kind of surprise this package promises not to be.
                    string installUrl = InstallUrl();
                    if (manual && installUrl != null && UnityEditor.EditorUtility.DisplayDialog(
                            "HAF Authoring Tools",
                            $"{remote} is available (installed: {local}).\n\n" +
                            "Update now? Package Manager fetches the new version; your project's data — packs, " +
                            "bakes, skins, settings — is never touched by an update.",
                            "Update now", "Later"))
                        ApplyUpdate(installUrl, remote);
                    else
                        UnityEngine.Debug.Log(
                            $"[HAF] HAF Authoring Tools {remote} is available (installed: {local}). " +
                            "Update from Tools ▸ HAF ▸ Check for Updates…, or Window ▸ Package Manager ▸ " +
                            "HAF Authoring Tools ▸ Update — updates never touch your project's data. " +
                            "(This once-a-day check reads one public file from the package's own repository " +
                            $"and sends nothing; disable it with EditorPrefs '{PrefAuto}' = false.)");
                }
                else if (manual)
                    UnityEngine.Debug.Log($"[HAF] up to date — installed {local}; newest release is {remote}.");
            }
            catch (System.Exception e) { if (manual) UnityEngine.Debug.LogWarning("[HAF] update check: " + e.Message); }
            finally { req.Dispose(); }
        };
    }

    /// The URL this install came from — the part of PackageInfo.packageId after the '@'
    /// ("com.sswelm.haf-authoring@https://github.com/….git?path=/editor"). Null when it isn't a git install
    /// (home working copy, registry) — there is nothing sensible to re-Add then.
    static string InstallUrl()
    {
        try
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(HafUpdateCheck).Assembly);
            if (info == null || info.source != UnityEditor.PackageManager.PackageSource.Git) return null;
            int at = info.packageId.IndexOf('@');
            return at > 0 ? info.packageId.Substring(at + 1) : null;
        }
        catch { return null; }
    }

    // Client.Add with the SAME git URL is what Package Manager's Update button does: re-resolve, fetch, update
    // the lock. The request is polled on the editor tick; the domain reload that installs the new version tears
    // the poller down mid-flight, which is fine — by then the update is already in Unity's hands.
    static UnityEditor.PackageManager.Requests.AddRequest updateReq;
    static void ApplyUpdate(string url, string remote)
    {
        UnityEditor.SessionState.SetString(InflightKey, remote);   // latch until the running version IS this one
        UnityEngine.Debug.Log($"[HAF] updating to {remote} — Package Manager is fetching {url} …");
        updateReq = UnityEditor.PackageManager.Client.Add(url);
        UnityEditor.EditorApplication.update += PollUpdate;
    }
    static void PollUpdate()
    {
        if (updateReq == null || !updateReq.IsCompleted) return;
        UnityEditor.EditorApplication.update -= PollUpdate;
        if (updateReq.Status == UnityEditor.PackageManager.StatusCode.Success)
            UnityEngine.Debug.Log($"[HAF] updated — HAF Authoring Tools {updateReq.Result.version} is installed.");
        else
        {
            UnityEditor.SessionState.EraseString(InflightKey);   // a FAILED fetch must not latch checks off
            UnityEngine.Debug.LogWarning("[HAF] update failed: " + updateReq.Error?.message +
                                         " — Window ▸ Package Manager ▸ HAF Authoring Tools ▸ Update works as the fallback.");
        }
        updateReq = null;
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
