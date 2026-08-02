using System;
using System.Collections.Generic;
using System.Linq;

namespace HumankindAssetFramework
{
    // In-game smoke harness — a runtime INTEGRATION test of the reflection half (which can't be unit-tested; Humankind
    // has no headless mode). Run from the F8 window (or shortly after a load) in a loaded game: it asserts the plugin
    // came up and injected cleanly and logs a single PASS/FAIL line. Semi-automated (a human launches; the harness does
    // the checking) — the honest form of integration test for a Unity-game mod.
    //
    // The VERDICT is a pure function (SmokeVerdict) so it's unit-tested; RunSmokeTest just gathers the live numbers via
    // reflection/state and calls it. That keeps the quality (the assertion logic) testable and the untestable part thin.
    internal static partial class UniversalInject
    {
        internal static int InjectionErrors;   // bumped in the injection-path catch blocks (RepointMatch / register / fragments / pose)

        internal struct SmokeResult { public bool Pass; public string Summary; }

        // PASS = every catalogued game binding resolved, no injection errors, and the registry actually loaded models.
        // `repointed` is informational only (how many entry types have injected so far — depends which units are present).
        internal static SmokeResult SmokeVerdict(int gbMissing, int injectionErrors, int models, int repointed)
        {
            var fails = new List<string>();
            if (gbMissing > 0) fails.Add($"{gbMissing} game type/member(s) missing");
            if (injectionErrors > 0) fails.Add($"{injectionErrors} injection error(s)");
            if (models <= 0) fails.Add("no models loaded from the registry");
            bool pass = fails.Count == 0;
            string head = pass ? "PASS" : "FAIL (" + string.Join("; ", fails) + ")";
            return new SmokeResult
            {
                Pass = pass,
                Summary = $"{head} — bindings {(gbMissing == 0 ? "ok" : gbMissing + " MISSING")}, {models} model(s) loaded " +
                          $"({repointed} injected so far), {injectionErrors} injection error(s)"
            };
        }

        internal static void RunSmokeTest()
        {
            try
            {
                var gb = GameBinding.Validate(GameBinding.Catalog);
                int gbMissing = gb.Count(r => !r.TypeFound) + gb.Where(r => r.TypeFound).Sum(r => r.MissingMembers.Count);
                int models = entries?.Count ?? 0;
                int repointed = entries?.Count(e => e.repointed) ?? 0;
                var res = SmokeVerdict(gbMissing, InjectionErrors, models, repointed);
                if (res.Pass) Plugin.Log.LogInfo("[SmokeTest] " + res.Summary);
                else Plugin.Log.LogWarning("[SmokeTest] " + res.Summary);
                Prober.Report.Clear();
                Prober.Report.Add("Smoke Test — " + res.Summary);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[SmokeTest] " + ex);
                Prober.Report.Clear();
                Prober.Report.Add("Smoke Test — ERROR (see log): " + ex.Message);
            }
        }
    }
}
