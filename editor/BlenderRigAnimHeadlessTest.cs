// BlenderRigAnimHeadlessTest.cs - the rig slim in C# (BlenderRigAnimInProcess, the Factory's GLB rig route) in UNITY'S OWN
// RUNTIME, as a Bake Tests row (step (g) of the rig_anim port, 2026-10-14). The push gate holds the port to rig_anim.py
// outside Unity (tools/riganim_drill.sh: the four stages row for row and the exported GLB byte for byte, on the registry's
// rigs and the fixtures) - in a .NET Framework process. What only Unity can show is that the same code writes the same
// file under Unity's Mono (which evaluates an uncast float product in double), on the registry's own animated rigs with
// the Factory's own arguments. For every animated registry entry whose source is a .glb/.gltf (a deployConvert entry's
// CONVERTED file, when its cache holds one), the argv the Factory would hand both routes (UniversalBaker.RigAnimArgv)
// runs the port into one GLB and Blender's Tools/rig_anim_glb.py (rig_anim.py's own source cut before its FBX export,
// then the one glTF export the drill's dump runs too) into another, and the two must be equal byte for byte. A fallback
// the port NAMES is a SKIP that says why (that is the fallback working); a port that CRASHES is a FAIL here even though
// the Factory would quietly take the FBX route - a crash is not a named shape. The script's own exits must be the
// script's on both sides. This row is the acceptance the backlog asks for before the GLB route becomes the default; the
// albedo is judged as the Factory writes it (a PNG source byte for byte; a JPEG one is Blender's encoder's and only its
// size is held).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class BlenderRigAnimHeadlessTest
{
    const int InProcessLimitMs = 600000;   // the Factory allows 180 s per rig; the row is generous, a stall is a FAIL either way

    public static BakeTestSection RunSection()
    {
        var s = new BakeTestSection { title = "rig slim in C#: the in-process rig_anim against Blender's GLB export, byte for byte, in Unity" };
        var body = new System.Text.StringBuilder();
        var entries = ModelRegistry.Load();
        if (ModelRegistry.LastLoadFailed) { s.skip++; s.body = "SKIP: the model registry " + ModelRegistry.LastLoadProblem; return s; }
        if (!UniversalBaker.BlenderAvailable()) { s.skip++; s.body = "SKIP: Blender is not installed - there is nothing to hold the in-process rig slim to"; return s; }
        foreach (var tool in new[] { "rig_anim.py", "rig_anim_glb.py" })
            if (!File.Exists(HafPackageContext.ToolPath(tool))) { s.skip++; s.body = "SKIP: Tools/" + tool + " is not there - nothing to hold the in-process rig slim to"; return s; }
        var jobs = entries.Where(e => e.animated).ToList();
        if (jobs.Count == 0) { s.skip++; s.body = "SKIP: no registry entry is animated - the port has nothing of yours to be judged on"; return s; }
        string root = Path.Combine(Path.GetTempPath(), "haf_riganim_test");
        int left = 0, exits = 0, notGlb = 0, byteEqual = 0, imageAside = 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            var e = jobs[i];
            BakeTestRunnerWindow.Progress.Step($"{e.resourceName} ({i + 1}/{jobs.Count}) - in process, then Blender", (float)i / Math.Max(1, jobs.Count));
            BakeTestRunnerWindow.Progress.ThrowIfCancelled();
            if (string.IsNullOrWhiteSpace(e.modelFile) || !File.Exists(e.modelFile)) { s.skip++; body.AppendLine($"SKIP: {e.resourceName}: {e.modelFile} is not on this machine"); continue; }
            try
            {
                var cfg = ModelFactoryWindow.ConfigFor(e);
                string src = e.modelFile;
                if (cfg.deployConvert)
                {
                    // the Factory slims the CONVERTED rig; this row does not convert (the deploy row judges that), it takes the cache
                    string conv = UniversalBaker.DeployConvertedPath(e.resourceName);
                    if (!File.Exists(conv)) { s.skip++; body.AppendLine($"SKIP: {e.resourceName}: deploys, and its converted rig is not cached ({conv}) - bake it once, or run the deploy row first"); continue; }
                    src = conv;
                }
                string ext = Path.GetExtension(src).ToLowerInvariant();
                if (ext != ".glb" && ext != ".gltf") { s.skip++; notGlb++; body.AppendLine($"SKIP: {e.resourceName}: {Path.GetFileName(src)} is not a glTF file - the port reads .glb/.gltf only, the FBX route stays Blender's"); continue; }
                string dirA = Path.Combine(root, "cs"), dirB = Path.Combine(root, "blender");
                foreach (var d in new[] { dirA, dirB }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }
                string glbA = Path.Combine(dirA, e.resourceName + "_anim.glb"), glbB = Path.Combine(dirB, e.resourceName + "_anim.glb");
                string albedoA = Path.Combine(dirA, e.resourceName + "_albedo.png"), albedoB = Path.Combine(dirB, e.resourceName + "_albedo.png");
                int target = cfg.targetTris > 0 ? cfg.targetTris : 0;
                bool keepMats = cfg.materialMode != MaterialMode.Single;
                // both sides get the SAME albedo argument (the script writes the PNG beside its output; the path is a file name the
                // port hands back and the Factory writes) - Blender's under dirB, the port's where the Factory would put it
                string[] argvA = UniversalBaker.RigAnimArgv(cfg, target, albedoA, keepMats);
                string[] argvB = UniversalBaker.RigAnimArgv(cfg, target, albedoB, keepMats);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                void Checkpoint()
                {
                    try { BakeTestRunnerWindow.Progress.Heartbeat(); } catch { }
                    BakeTestRunnerWindow.Progress.ThrowIfCancelled();
                    if (sw.ElapsedMilliseconds >= InProcessLimitMs) throw new TimeoutException($"the in-process rig slim exceeded {InProcessLimitMs / 1000} seconds");
                }
                var inProc = BlenderRigAnimInProcess.Run(src, glbA, argvA, Checkpoint);
                double secondsA = sw.Elapsed.TotalSeconds;
                if (inProc.UseBlender != null && inProc.UseBlender.StartsWith("the C# rig run failed", StringComparison.Ordinal))
                { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the port crashed - {inProc.UseBlender} (the Factory would take the FBX route; a crash is not a named shape)"); continue; }
                if (inProc.UseBlender != null) { s.skip++; left++; body.AppendLine($"SKIP: {e.resourceName}: left to Blender - {inProc.UseBlender}"); continue; }
                if (inProc.Written && inProc.AlbedoBytes != null && !UniversalBaker.WriteAlbedoPng(inProc, out string albErr))
                { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the port's albedo could not be written - {albErr}"); continue; }
                BakeTestRunnerWindow.Progress.Step($"{e.resourceName} ({i + 1}/{jobs.Count}) - Blender's rig_anim_glb.py", (float)(i + 0.5) / Math.Max(1, jobs.Count));
                sw.Restart();
                var blender = UniversalBaker.RunRigAnimGlbViaBlender(src, glbB, argvB);
                double secondsB = sw.Elapsed.TotalSeconds;
                if (inProc.ScriptExit)
                {
                    // the script stops itself (no armature, no clip of that name, a socket without its bone): Blender must stop the same way
                    string why = inProc.Log.LastOrDefault(l => l.Contains("RIGANIM ERROR")) ?? inProc.Log.LastOrDefault() ?? "(no RIGANIM line)";
                    if (!ScriptExitMatches(inProc.Log, blender, File.Exists(glbB), out string exitError))
                    { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the port stopped before writing ({why}); {exitError}"); continue; }
                    s.pass++; exits++; body.AppendLine($"PASS: {e.resourceName}: both stop before writing - {why}"); continue;
                }
                if (!blender.Written) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: Blender's rig_anim_glb.py failed on a file the port wrote - {blender.Error}"); continue; }
                byte[] a = File.ReadAllBytes(glbA), b = File.ReadAllBytes(glbB);
                string bytesNote = "";
                if (!a.SequenceEqual(b))
                {
                    string reason = $"{a.Length} bytes in process, {b.Length} from Blender";
                    bool jpegAlbedo = inProc.AlbedoMime == "image/jpeg" && inProc.AlbedoBytes != null;
                    try
                    {
                        HafModel ma = GlbReader.Read(glbA), mb = GlbReader.Read(glbB);
                        if (jpegAlbedo)
                        {
                            // Blender's file carries its PNG re-encode of the JPEG albedo, the port's the source JPEG: the models must agree with the image bytes aside
                            reason = HafModelDiff.FirstDifference(ma, mb, inProc.SourceImageFallbacks);
                            if (reason == null) bytesNote = " with the albedo's bytes aside (a JPEG source: Blender re-encodes it, the port keeps it)";
                        }
                        else reason = HafModelDiff.FirstDifference(ma, mb) ?? reason;
                    }
                    catch (Exception dx) { reason += " (" + dx.GetType().Name + " reading them back)"; }
                    if (reason != null) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the written files differ - {reason}"); continue; }
                }
                // the albedo: the script wrote its PNG at argv[5] when asked; the port's must be there too, a PNG source byte for byte
                string albedoNote = "";
                if (argvA[3].Length > 0)
                {
                    bool hasA = File.Exists(albedoA), hasB = File.Exists(albedoB);
                    if (hasA != hasB) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the albedo is {(hasA ? "written" : "not written")} in process and {(hasB ? "written" : "not written")} by Blender"); continue; }
                    if (hasA)
                    {
                        if (inProc.AlbedoMime == "image/png" && !File.ReadAllBytes(albedoA).SequenceEqual(File.ReadAllBytes(albedoB)))
                        { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: the albedo PNG differs from Blender's ({new FileInfo(albedoA).Length} bytes in process, {new FileInfo(albedoB).Length} from Blender)"); continue; }
                        var texB = new Texture2D(2, 2); bool okB = texB.LoadImage(File.ReadAllBytes(albedoB));
                        if (!okB || texB.width != inProc.AlbedoWidth || texB.height != inProc.AlbedoHeight)
                        { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: Blender's albedo is {(okB ? texB.width + "x" + texB.height : "unreadable")}, the port says {inProc.AlbedoWidth}x{inProc.AlbedoHeight}"); UnityEngine.Object.DestroyImmediate(texB); continue; }
                        UnityEngine.Object.DestroyImmediate(texB);
                        albedoNote = inProc.AlbedoMime == "image/png" ? ", the albedo PNG byte for byte" : $", the albedo ({inProc.AlbedoMime}) re-encoded at Blender's {inProc.AlbedoWidth}x{inProc.AlbedoHeight}";
                    }
                }
                s.pass++;
                if (bytesNote.Length == 0) byteEqual++; else imageAside++;
                int roles = inProc.RoleActions.Count;
                body.AppendLine(FormattableString.Invariant($"PASS: {e.resourceName}: {a.Length:N0} bytes{(bytesNote.Length > 0 ? bytesNote : " equal")} (primary '{inProc.PrimaryAction}'{(roles > 0 ? ", " + roles + " role clip(s)" : "")}{albedoNote}; in process {secondsA:0.0} s, Blender {secondsB:0.0} s)"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { s.fail++; body.AppendLine($"FAIL: {e.resourceName}: {ex.GetType().Name}: {ex.Message}"); }
        }
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        body.Insert(0, $"{jobs.Count} animated registry entr{(jobs.Count == 1 ? "y" : "ies")}; {byteEqual} equal byte for byte, {imageAside} equal with re-encoded image payloads aside, {exits} matching script exits, {left} left to Blender by name, {notGlb} not glTF sources\n");
        s.body = body.ToString();
        return s;
    }

    /// <summary>The port stopped as the script does (sys.exit(1) after a RIGANIM ERROR line): Blender must have exited 1 with the same
    /// final RIGANIM ERROR line and no file, and without a Python traceback.</summary>
    internal static bool ScriptExitMatches(IEnumerable<string> portLog, BakerRules.DeployBlenderRun run, bool outputExists, out string error)
    {
        error = null;
        if (!run.Completed) { error = "Blender did not complete: " + run.Error; return false; }
        if (run.ExitCode != 1) { error = "Blender exited " + run.ExitCode + ", expected the script's SystemExit(1)"; return false; }
        if (run.Written || outputExists) { error = "Blender wrote a file after the port stopped"; return false; }
        if ((run.Stdout + "\n" + run.Stderr).Contains("Traceback (most recent call last):"))
        { error = "Blender raised a Python exception instead of the script's own exit"; return false; }
        string[] expected = portLog.Select(l => l.Trim()).Where(l => l.StartsWith("RIGANIM ERROR", StringComparison.Ordinal)).ToArray();
        string[] actual = run.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("RIGANIM ERROR", StringComparison.Ordinal)).ToArray();
        if (expected.Length == 0) { error = "the port's script exit has no RIGANIM ERROR diagnostic"; return false; }
        if (!actual.SequenceEqual(expected)) { error = "Blender did not report the port's script-exit diagnostic: " + expected.Last() + (actual.Length > 0 ? " (Blender said: " + actual.Last() + ")" : " (Blender said nothing)"); return false; }
        return true;
    }
}
