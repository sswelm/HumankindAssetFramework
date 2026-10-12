// BakeGoldenFiles.cs — the golden files of the Bake Tests' Workshop and Vehicle Lab rows, read and written in one
// place (2026-10-12). Both rows captured a missing golden with File.WriteAllLines and judged with a bare line diff;
// a golden rewritten by hand (2026-10-10: eleven of them, to the failing values, no run after) read exactly like a
// blessed one. Every golden now carries BakeGoldenRules' header - capture time, the editor commit, the inputs'
// hashes, its own checksum - and is judged through BakeGoldenRules.Judge. A legacy golden (no header) that a run
// MATCHES is given its header on the spot: verified by that run, not re-blessed.
using System;
using System.Collections.Generic;
using System.IO;

public static class BakeGoldenFiles
{
    /// The editor package's commit, short, for a golden's header; "unknown" outside a git checkout. In Unity the
    /// package's resolved path is the checkout (a file: dependency on <repo>/editor); outside Unity (the drills) an
    /// environment variable may name it.
    internal static string EditorStamp()
    {
        try
        {
#if UNITY_EDITOR
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BakeGoldenFiles).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath)) return GitStamp.ShortHead(info.resolvedPath);
            return "unknown";
#else
            string env = Environment.GetEnvironmentVariable("HAF_EDITOR_STAMP");
            return string.IsNullOrWhiteSpace(env) ? "unknown" : env.Trim();
#endif
        }
        catch { return "unknown"; }
    }

    /// Write a golden captured from this run: header, then the lines.
    internal static void Capture(string goldFile, IEnumerable<string> snap, IDictionary<string, string> inputs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(goldFile));
        File.WriteAllLines(goldFile, BakeGoldenRules.Compose(snap, DateTime.UtcNow, EditorStamp(), inputs));
    }

    /// Judge a run against a golden file. `note` is appended to a PASS line: a legacy golden that matched was given
    /// its header here (written in place), and says so. `reason` is the FAIL text for anything but Match.
    internal static BakeGoldenRules.GoldenVerdict Judge(string goldFile, IEnumerable<string> snap, IDictionary<string, string> inputs, out string reason, out string note)
    {
        note = "";
        var verdict = BakeGoldenRules.Judge(File.ReadAllLines(goldFile), snap, inputs, out reason, out bool legacy);
        if (verdict == BakeGoldenRules.GoldenVerdict.Match && legacy)
        {
            try
            {
                File.WriteAllLines(goldFile, BakeGoldenRules.Compose(snap, DateTime.UtcNow, EditorStamp(), inputs));
                note = "; legacy golden verified by this run, provenance header added";
            }
            catch (Exception ex) { note = "; legacy golden verified, but its header could not be written (" + ex.Message + ")"; }
        }
        return verdict;
    }
}
