// BlenderDeployHeadlessTest.cs - the deploy conversion in C# (BlenderDeployInProcess, the Factory's EnsureDeployConverted)
// in UNITY'S OWN RUNTIME, as a Bake Tests row (2026-10-13). The push gate holds the port to deploy_convert.py outside
// Unity (tools/deploy_drill.sh: the decisions, the bake, the export and the written bytes, on the fixtures and the
// deploy sources under 40 MB; the T-62 only under FULL=1) - in a .NET Framework process. The port was wired into the
// Factory on 2026-10-11 and, until this row, had run zero times in Unity: what only Unity can show is that the same
// code writes the same file under Unity's Mono (which evaluates an uncast float product in double), on the registry's
// own deploys - today the T-62, the >127-bone pair-merge path the gate sample never reaches. For every registry entry
// that deploys, the argv the Factory would hand both paths (UniversalBaker.DeployArgv) runs the port into one file and
// deploy_convert.py into another, and the two must be equal byte for byte. A fallback the port NAMES is a SKIP that
// says why (that is the fallback working); a port that CRASHES is a FAIL here even though the Factory would quietly
// let Blender do the work - a crash is not a named shape. The script's own exits must be the script's on both sides.
using System;
using System.IO;
using System.Linq;
using UnityEngine;

public static class BlenderDeployHeadlessTest
{
    const int InProcessLimitMs = 600000;   // the T-62's 377 frames through every step on the editor thread; the Factory allows 300 s

    public static BakeTestSection RunSection()
    {
        var s = new BakeTestSection { title = "deploy conversion in C#: the in-process deploy_convert against Blender's, byte for byte, in Unity" };
        var body = new System.Text.StringBuilder();
        var entries = ModelRegistry.Load();
        if (ModelRegistry.LastLoadFailed) { s.skip++; s.body = "SKIP: the model registry " + ModelRegistry.LastLoadProblem; return s; }
        if (!UniversalBaker.BlenderAvailable()) { s.skip++; s.body = "SKIP: Blender is not installed - there is nothing to hold the in-process conversion to"; return s; }
        if (!File.Exists(HafPackageContext.ToolPath("deploy_convert.py"))) { s.skip++; s.body = "SKIP: Tools/deploy_convert.py is not there - nothing to hold the in-process conversion to"; return s; }
        var jobs = entries.Where(e => e.deployConvert).ToList();
        if (jobs.Count == 0) { s.skip++; s.body = "SKIP: no registry entry deploys (deployConvert) - the port has nothing of yours to be judged on"; return s; }
        string root = Path.Combine(Path.GetTempPath(), "haf_deploy_test");
        int left = 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            var e = jobs[i];
            BakeTestRunnerWindow.Progress.Step($"{e.resourceName} ({i + 1}/{jobs.Count}) - in process, then Blender", (float)i / Math.Max(1, jobs.Count));
            BakeTestRunnerWindow.Progress.ThrowIfCancelled();
            if (string.IsNullOrWhiteSpace(e.modelFile) || !File.Exists(e.modelFile)) { s.skip++; body.AppendLine($"SKIP: {e.resourceName}: {e.modelFile} is not on this machine"); continue; }
            try
            {
                var cfg = ModelFactoryWindow.ConfigFor(e);
                string[] argv = UniversalBaker.DeployArgv(cfg, out string argErr);
                if (argv == null) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: {argErr}"); continue; }
                string dirA = Path.Combine(root, "cs"), dirB = Path.Combine(root, "blender");
                foreach (var d in new[] { dirA, dirB }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }
                string glbA = Path.Combine(dirA, "deploy_converted.glb"), glbB = Path.Combine(dirB, "deploy_converted.glb");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                void Checkpoint()
                {
                    try { BakeTestRunnerWindow.Progress.Heartbeat(); } catch { }
                    BakeTestRunnerWindow.Progress.ThrowIfCancelled();
                    if (sw.ElapsedMilliseconds >= InProcessLimitMs) throw new TimeoutException($"the in-process deploy conversion exceeded {InProcessLimitMs / 1000} seconds");
                }
                var inProc = BlenderDeployInProcess.Run(e.modelFile, glbA, argv, Checkpoint);
                double secondsA = sw.Elapsed.TotalSeconds;
                if (inProc.UseBlender != null && inProc.UseBlender.StartsWith("the C# conversion failed", StringComparison.Ordinal))
                { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the port crashed - {inProc.UseBlender} (the Factory would let Blender convert; a crash is not a named shape)"); continue; }
                if (inProc.UseBlender != null) { s.skip++; left++; body.AppendLine($"SKIP: {e.resourceName}: left to Blender - {inProc.UseBlender}"); continue; }
                BakeTestRunnerWindow.Progress.Step($"{e.resourceName} ({i + 1}/{jobs.Count}) - Blender's deploy_convert.py", (float)(i + 0.5) / Math.Max(1, jobs.Count));
                sw.Restart();
                bool okB = UniversalBaker.DeployViaBlender(e.modelFile, glbB, argv, out string stdout, out string bErr);
                double secondsB = sw.Elapsed.TotalSeconds;
                if (inProc.ScriptExit)
                {
                    // the script stops itself (no animated part, no tube for the recoil): Blender must stop the same way
                    string why = inProc.Log.LastOrDefault() ?? "(no DEPLOY line)";
                    if (okB || File.Exists(glbB)) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the port stopped before writing ({why}) but Blender's script wrote a file"); continue; }
                    s.pass++; body.AppendLine($"PASS: {e.resourceName}: both stop before writing - {why}"); continue;
                }
                if (!okB) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: Blender's conversion failed on a file the port wrote - {bErr}"); continue; }
                byte[] a = File.ReadAllBytes(glbA), b = File.ReadAllBytes(glbB);
                if (!a.SequenceEqual(b))
                {
                    string reason = $"{a.Length} bytes in process, {b.Length} from Blender";
                    try { reason = HafModelDiff.FirstDifference(GlbReader.Read(glbA), GlbReader.Read(glbB)) ?? reason; } catch (Exception dx) { reason += " (" + dx.GetType().Name + " reading them back)"; }
                    s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the written files differ - {reason}"); continue;
                }
                s.pass++;
                body.AppendLine(FormattableString.Invariant($"PASS: {e.resourceName}: {a.Length:N0} bytes equal (frames {argv[0]}..{argv[1]}{(argv[6].Length > 0 ? ", recoil " + argv[6] + ".." + argv[7] : "")}; in process {secondsA:0.0} s, Blender {secondsB:0.0} s)"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: {ex.GetType().Name}: {ex.Message}"); }
        }
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        body.Insert(0, $"{jobs.Count} registry entr{(jobs.Count == 1 ? "y deploys" : "ies deploy")}; {s.pass} equal byte for byte, {left} left to Blender by name\n");
        s.body = body.ToString();
        return s;
    }
}
