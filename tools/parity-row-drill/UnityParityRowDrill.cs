// The Bake Tests row "Do the C# ports still match Blender?" run from a real Unity editor process (batch mode), the way
// the window runs it: BlenderParityGateTest.RunSection, its section written out for tools/parity_row_unity_drill.sh.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class UnityParityRowDrill
{
    public static void RunSelfTests()
    {
        string output = OutputPath();
        string temp = Path.Combine(Path.GetTempPath(), "haf-parity-selftest-" + Guid.NewGuid().ToString("N"));
        string oldRoot = Environment.GetEnvironmentVariable("HAF_REPO_ROOT");
        var log = new System.Text.StringBuilder();
        int failures = 0;
        void Check(string name, bool ok) { log.AppendLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "tools"));
            Environment.SetEnvironmentVariable("HAF_REPO_ROOT", temp);
            const string pass = "[PASS] one (0 s)\n";
            const string verdict = "CHECK: PASS — safe to push.\n";
            string[] names = { "complete", "skip with failing exit", "allowed skip", "missing summary", "failed summary", "duplicate summary", "no steps" };
            string[] texts = { pass + verdict, pass + "[SKIP] missing (0 s)\nCHECK: FAIL\n", pass + "[SKIP] missing (0 s)\nCHECK: PASS WITH 1 SKIP\n",
                pass, pass + "CHECK: FAIL\n", pass + verdict + verdict, verdict };
            for (int i = 0; i < texts.Length; i++)
            {
                var s = new BakeTestSection();
                BlenderParityGateTest.ReadResult(s, texts[i], "", true, true, i == 1 ? 1 : 0, 0);
                Check(names[i], i == 0 ? s.pass == 1 && s.fail == 0 && s.skip == 0 : s.fail > 0);
            }
            var partial = new BakeTestSection();
            BlenderParityGateTest.ReadResult(partial, pass + verdict, "", true, false, 0, 0);
            Check("undrained output", partial.fail > 0);

            // Exercise the actual shell's cancellation trap and worker cleanup, and cancellation while waiting on
            // somebody else's lock. No Blender or registry is needed for these bounded-process checks.
            string root = oldRoot;
            string source = File.ReadAllText(Path.Combine(root, "tools/check.sh")).Replace("\r", "");
            string helpers = source.Substring(source.IndexOf("fail=0\n", StringComparison.Ordinal));
            helpers = helpers.Substring(0, helpers.IndexOf("# ==== the gate itself", StringComparison.Ordinal));
            foreach (int scenario in new[] { 0, 1, 2 })
            {
                bool waiting = scenario == 1, timeout = scenario == 2;
                string lockPath = Path.Combine(temp, waiting ? "foreign.lock" : "own.lock");
                if (waiting)
                {
                    Directory.CreateDirectory(lockPath);
                    File.WriteAllText(Path.Combine(lockPath, "holder"), "another gate pid 0\n");
                    File.WriteAllText(Path.Combine(lockPath, "beat"), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "\n");
                }
                string script = Path.Combine(temp, "cancel.sh"), cancel = Path.Combine(temp, "cancel"), started = Path.Combine(temp, "started");
                File.Delete(cancel); File.Delete(started);
                File.WriteAllText(script, "set -uo pipefail\nROOT=\"$1\"\n" + helpers +
                    "\ntrap gate_abort TERM INT\ntrap gate_unlock EXIT\ngate_cancel_watch\n" +
                    (waiting ? "printf ready > \"$ROOT/started\"\n" : "") +
                    "gate_lock\nrun_bg worker python -c 'import pathlib,sys,time; p=pathlib.Path(sys.argv[1]); (p/\"started\").write_text(\"ready\"); time.sleep(60); (p/\"leaked\").write_text(\"leaked\")' \"$ROOT\" \"$BG_LOGS\"\nrun_bg_wait\n");
                var psi = new System.Diagnostics.ProcessStartInfo(BlenderParityGateTest.FindBash(), "\"" + script.Replace('\\', '/') + "\" \"" + temp.Replace('\\', '/') + "\"")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                psi.EnvironmentVariables["HAF_GATE_CANCEL_FILE"] = cancel.Replace('\\', '/');
                psi.EnvironmentVariables["HAF_GATE_LOCK"] = lockPath.Replace('\\', '/');
                psi.EnvironmentVariables["HAF_GATE_LOCK_POLL"] = "1";
                psi.EnvironmentVariables["HAF_PROGRESS"] = Path.Combine(temp, "no-progress");
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    bool cancelled = false, drained = false, finished = true;
                    try { finished = BlenderParityGateTest.RunGate(p, cancel, timeout ? 1000 : 15000, () => !timeout && File.Exists(started), () => { }, out _, out _, out drained); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(timeout ? "timeout running workers" : waiting ? "cancel lock waiter" : "cancel running workers",
                        (timeout ? !finished && !cancelled : cancelled) && drained && p.HasExited && p.ExitCode == 130);
                }
                Check(waiting ? "foreign lock preserved" : "owned lock released", Directory.Exists(lockPath) == waiting);
            }
            Check("no worker continued", !File.Exists(Path.Combine(temp, "leaked")));
        }
        catch (Exception ex) { failures++; log.AppendLine("FAIL " + ex); }
        finally
        {
            Environment.SetEnvironmentVariable("HAF_REPO_ROOT", oldRoot);
            try { Directory.Delete(temp, true); } catch { }
        }
        if (output != null) File.WriteAllText(output, log.ToString());
        Debug.Log(log.ToString());
        EditorApplication.Exit(failures == 0 ? 0 : 1);
    }

    static string OutputPath()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-hafParityOutput") return args[i + 1];
        return null;
    }

    public static void Run()
    {
        string output = OutputPath();
        int code = 1;
        try
        {
            var s = BlenderParityGateTest.RunSection();
            string text = "TITLE " + s.title + "\nPASS " + s.pass + "\nFAIL " + s.fail + "\nSKIP " + s.skip + "\n--- body ---\n" + s.body;
            if (output != null) File.WriteAllText(output, text);
            Debug.Log(text);
            code = s.fail == 0 && s.skip == 0 && s.pass > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            if (output != null) File.WriteAllText(output, "CRASH " + ex);
            Debug.LogException(ex);
        }
        EditorApplication.Exit(code);
    }
}
