// BlenderParityGateTest.cs — the Bake Tests row that holds the C# ports to Blender's own output (2026-10-30).
//
// User: "for pushes to github it should no longer use any blender!!! ... it should only be part of the bake test to verify
// if it still behaves correctly". The nine parity drills (GLB reader and writer, Workshop compaction, the vehicle probe,
// the mesh layout, the prep, the deploy conversion, rig_anim, vehicle_rig: every C# port against the rows, bytes and
// folders Blender itself produces) ran on every push until 2026-10-28 and replayed a RECORD of Blender from then on - a
// record that went stale with every change to a script, and asked for Blender again to refresh it. They are no longer
// part of the push at all: `tools/check.sh` runs them only as HAF_GATE=parity, with Blender, and this row is that run.
//
// The row starts Git's bash on tools/check.sh from the package's own repository checkout (the package folder editor/ is
// inside it) with HAF_GATE=parity and HAF_ORACLE=live, hands it the Blender the Factory uses and this project as the
// modding project, and reads the gate's own verdict lines back: every `[PASS]`, `[FAIL]` and `[SKIP]` step is a line of
// this section, and the CHECK line is the row's verdict. The gate takes its lock (one gate at a time on this machine),
// so a push started meanwhile waits for it; it takes about twenty minutes, Blender's share included.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public static class BlenderParityGateTest
{
    const int TimeoutMs = 90 * 60 * 1000;   // the parity gate takes about twenty minutes; a hung drill is the gate's own step limit (30 min) first

    public static BakeTestSection RunSection()
    {
        var s = new BakeTestSection { title = "the C# ports against Blender: the nine parity drills (tools/check.sh, HAF_GATE=parity), with Blender, outside Unity" };
        var body = new System.Text.StringBuilder();
        string root = RepoRoot();
        string gate = root == null ? null : Path.Combine(root, "tools", "check.sh").Replace('\\', '/');
        if (gate == null || !File.Exists(gate))
        {
            s.skip = 1; body.AppendLine("SKIP — tools/check.sh not found beside the package (the package must be the repository's editor/ folder, not a copy): " + (gate ?? "no package path"));
            s.body = body.ToString(); return s;
        }
        string bash = FindBash();
        if (bash == null)
        {
            s.skip = 1; body.AppendLine("SKIP — Git's bash.exe not found (Git for Windows under Program Files, or bash on PATH outside System32): the drills are shell scripts");
            s.body = body.ToString(); return s;
        }
        string blender = UniversalBaker.FindBlender();
        if (string.IsNullOrEmpty(blender) || !File.Exists(blender))
        {
            s.skip = 1; body.AppendLine("SKIP — no Blender found (UniversalBaker.FindBlender): the parity drills need the other side");
            s.body = body.ToString(); return s;
        }
        // the modding project the drills read (the registry, the recipes, the Workshop files): this project - unless the caller named
        // one (HAF_UNITY_PROJECT, as the batch drill does from its throwaway project, and as the gate itself reads it)
        string project = Environment.GetEnvironmentVariable("HAF_UNITY_PROJECT");
        if (string.IsNullOrEmpty(project) || !Directory.Exists(project)) project = Directory.GetParent(Application.dataPath).FullName;
        var psi = new System.Diagnostics.ProcessStartInfo(bash, "\"" + gate.Replace('\\', '/') + "\"")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
        psi.EnvironmentVariables["HAF_GATE"] = "parity";
        psi.EnvironmentVariables["HAF_ORACLE"] = "live";
        psi.EnvironmentVariables["HAF_ALLOW_SKIP"] = "0";
        psi.EnvironmentVariables["BLENDER"] = blender.Replace('\\', '/');
        psi.EnvironmentVariables["HAF_UNITY_PROJECT"] = project.Replace('\\', '/');
        // a hook's variables would make the gate's git calls answer for another repository; none are set in an editor, but a
        // shell that started Unity from inside a hook would pass them on
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY",
            "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_GRAFT_FILE", "GIT_PREFIX", "GIT_SHALLOW_FILE", "GIT_IMPLICIT_WORK_TREE",
            "GIT_CONFIG", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT", "GIT_INTERNAL_SUPER_PREFIX", "GIT_NO_REPLACE_OBJECTS",
            "GIT_REPLACE_REF_BASE" }) psi.EnvironmentVariables.Remove(name);
        string cancelFile = Path.Combine(Path.GetTempPath(), "haf-gate-cancel-" + Guid.NewGuid().ToString("N"));
        psi.EnvironmentVariables["HAF_GATE_CANCEL_FILE"] = cancelFile.Replace('\\', '/');
        body.AppendLine("gate: " + gate + "  (bash " + bash + ")");
        body.AppendLine("Blender: " + blender + "   modding project: " + project);
        string o = "", e = ""; bool finished, drained; int rc;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                finished = RunGate(p, cancelFile, TimeoutMs, () => BakeTestRunnerWindow.Progress.CancelRequested,
                    () => BakeTestRunnerWindow.Progress.Heartbeat(), out o, out e, out drained);
                rc = finished ? p.ExitCode : -1;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            s.fail = 1; body.AppendLine("FAIL — could not start the gate: " + ex.Message);
            s.body = body.ToString(); return s;
        }
        finally { try { File.Delete(cancelFile); } catch { } }
        ReadResult(s, o, e, finished, drained, rc, watch.Elapsed.TotalSeconds);
        s.body = body + s.body;
        return s;
    }

    // Killing bash outright bypasses check.sh's TERM trap and leaves its drills and lock heartbeat alive. Ask the
    // gate's watcher to signal its own shell, including while waiting for another gate's lock, then let it clean up.
    internal static bool RunGate(System.Diagnostics.Process p, string cancelFile, int timeoutMs, Func<bool> cancelled,
        Action heartbeat, out string stdout, out string stderr, out bool drained)
    {
        var output = Task.Run(() => p.StandardOutput.ReadToEnd());
        var error = Task.Run(() => p.StandardError.ReadToEnd());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool cancel = false, timedOut = false;
        while (!p.WaitForExit(250))
        {
            try { heartbeat(); } catch { }   // progress rendering must not orphan the gate
            cancel = cancelled(); timedOut = watch.ElapsedMilliseconds >= timeoutMs;
            if (!cancel && !timedOut) continue;
            File.WriteAllText(cancelFile, "stop\n");
            // The gate's abort trap stops workers, releases the lock and exits 130. Do not dispose its pipes early.
            if (!p.WaitForExit(60000))
            {
                try { p.Kill(); } catch { }
                throw new TimeoutException("The parity gate did not acknowledge the stop request within 60 seconds");
            }
            break;
        }
        drained = false;
        try { drained = Task.WaitAll(new Task[] { output, error }, 5000); } catch { }
        stdout = output.Status == TaskStatus.RanToCompletion ? output.Result : "";
        stderr = error.Status == TaskStatus.RanToCompletion ? error.Result : "";
        drained = drained && output.Status == TaskStatus.RanToCompletion && error.Status == TaskStatus.RanToCompletion;
        if (cancel) throw new OperationCanceledException("Bake Tests: cancelled by the user");
        return !timedOut;
    }

    internal static void ReadResult(BakeTestSection s, string o, string e, bool finished, bool drained, int rc, double seconds)
    {
        var body = new System.Text.StringBuilder();
        var lines = (o ?? "").Replace("\r", "").Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith("[PASS] ")) { s.pass++; body.AppendLine(line); }
            else if (line.StartsWith("[FAIL] ")) { s.fail++; body.AppendLine(line); }
            else if (line.StartsWith("[SKIP] ")) { s.skip++; body.AppendLine(line); }
        }
        string verdict = lines.LastOrDefault(l => l.StartsWith("CHECK: ")) ?? "";
        body.AppendLine(verdict.Length > 0 ? verdict : "(the gate printed no CHECK verdict)");
        body.AppendLine(string.Format("exit {0} after {1:0} s", rc, seconds));
        if (!finished) { s.fail++; body.AppendLine("FAIL — the gate did not finish within " + (TimeoutMs / 60000) + " minutes and was killed"); }
        else if (rc != 0 && s.fail == 0) { s.fail++; body.AppendLine("FAIL — the gate exited " + rc); }
        else if (rc == 0 && s.pass == 0) { s.fail++; body.AppendLine("FAIL — the gate exited 0 but no step passed: nothing was tested"); }
        if (s.skip > 0 && s.fail == 0) { s.fail++; body.AppendLine("FAIL — parity steps were skipped; the row requires every claim to be tested"); }
        if (!drained || lines.Count(l => l.StartsWith("CHECK: ")) != 1 || verdict != "CHECK: PASS — safe to push."
            || lines.LastOrDefault(l => l.Length > 0) != verdict)
        {
            if (s.fail == 0) s.fail++;
            body.AppendLine("FAIL — missing, unsuccessful or incomplete gate verdict/output");
        }
        if (rc != 0 || !finished || s.fail > 0)
        {
            var tail = lines.Where(l => l.Length > 0).ToList();
            body.AppendLine("--- the gate's last lines ---");
            foreach (var line in tail.Skip(Math.Max(0, tail.Count - 60))) body.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(e)) { body.AppendLine("--- stderr ---"); body.AppendLine(e.Trim()); }
        }
        s.body = body.ToString();
    }

    /// <summary>The repository checkout the package lives in: the package folder (editor/) is inside it, and tools/ beside it.
    /// HAF_REPO_ROOT names it outright for a copy of the editor sources that is not the repository's editor/ folder (the batch drill).</summary>
    internal static string RepoRoot()
    {
        try
        {
            string named = Environment.GetEnvironmentVariable("HAF_REPO_ROOT");
            if (!string.IsNullOrEmpty(named) && Directory.Exists(named)) return named;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(HafPackageContext).Assembly);
            if (info == null || string.IsNullOrEmpty(info.resolvedPath)) return null;
            var parent = Directory.GetParent(info.resolvedPath);
            return parent == null ? null : parent.FullName;
        }
        catch { return null; }
    }

    /// <summary>Git for Windows' bash (never System32's, which is WSL's launcher): the usual install folders, then PATH.</summary>
    internal static string FindBash()
    {
        var candidates = new List<string>();
        foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            candidates.Add(Path.Combine(pf, "Git", "bin", "bash.exe"));
            candidates.Add(Path.Combine(pf, "Git", "usr", "bin", "bash.exe"));
        }
        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(local)) candidates.Add(Path.Combine(local, "Programs", "Git", "bin", "bash.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.IndexOf("System32", StringComparison.OrdinalIgnoreCase) >= 0 || dir.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            candidates.Add(Path.Combine(dir.Trim(), "bash.exe"));
        }
        return candidates.FirstOrDefault(c => { try { return File.Exists(c); } catch { return false; } });
    }
}
