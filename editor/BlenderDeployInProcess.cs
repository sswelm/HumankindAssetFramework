// BlenderDeployInProcess.cs - deploy_convert.py in this process (2026-10-11, the Factory wiring after parts 1-8c of
// replacing Blender): the decisions and the bake (BlenderDeploy), the role clips (Result.Finish), the export's
// structure, materials and channels (BlenderDeployExport) and the file's bytes (BlenderGlbWriter) - every one held to
// the script bit for bit by tools/deploy_drill.sh. Whatever any step names as Blender's (a Fallback) is Blender's:
// the caller runs the script then, as before. The script's own exits (no animated part, no tube for the recoil) are
// the script's outcome, not a fallback: Blender would stop the same way.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class BlenderDeployInProcess
{
    /// <summary>Bump when the in-process conversion's written file changes: the Factory's cache key carries it, so a
    /// cached deploy_converted.glb made by an older port is converted again.</summary>
    public const string CacheRevision = "2026-10-11.1";

    /// <summary>The longest animation converted here (frames from the bake's first to its last): the bake, the fire
    /// window, the role clips and the export each walk every frame for every bone on the editor thread, where only the
    /// checkpoint between the steps can stop them. The registry's deploys are 25 to 600 frames long.</summary>
    public const int MaxFramesInProcess = 5000;

    public sealed class Outcome
    {
        public bool Written;                 // the file is there, as the script would have written it
        public bool ScriptExit;              // the script stops itself before writing (SystemExit): no file, no Blender run either
        public string UseBlender;            // why the script has to run (a shape the port names, an input the reader refuses, a failure here)
        public readonly List<string> Log = new List<string>();   // the DEPLOY lines, in the script's order
    }

    /// <summary>The script's argv[2:] as the Factory passes them (deploy_convert.py: in out start end strip readyFrame
    /// legScale barrelScale recoilStarts recoilEnds recoilStep recoilMag arcR recoilReturn slamDeg slamSettle
    /// stripExtra wheelBones wheelAxis wheelFrames wheelDegrees), each trimmed, a null as "".</summary>
    public static string[] Argv(string start, string end, string strip, string readyFrame, string legScale, string barrelScale,
                                string recoilStarts, string recoilEnds, string recoilStep, string recoilMag, string arcR, string recoilReturn,
                                string slamDeg, string slamSettle, string stripExtra, string wheelBones, string wheelAxis, string wheelFrames, string wheelDegrees)
    {
        string T(string s) => (s ?? "").Trim();
        return new[] { T(start), T(end), T(strip), T(readyFrame), T(legScale), T(barrelScale), T(recoilStarts), T(recoilEnds), T(recoilStep), T(recoilMag), T(arcR), T(recoilReturn),
                       T(slamDeg), T(slamSettle), T(stripExtra), T(wheelBones), T(wheelAxis), T(wheelFrames), T(wheelDegrees) };
    }

    /// <summary>The conversion of `src` into `outGlb` with the script's arguments (argv[2:]). `checkpoint`, when given,
    /// runs between the steps: a cancel (OperationCanceledException) passes through, a deadline (any other exception)
    /// leaves the model to Blender.</summary>
    public static Outcome Run(string src, string outGlb, string[] argv, Action checkpoint = null)
    {
        var o = new Outcome();
        void Tick() { checkpoint?.Invoke(); }
        if (string.IsNullOrEmpty(src) || !File.Exists(src)) { o.UseBlender = "the model file is not there"; return o; }
        string ext = Path.GetExtension(src).ToLowerInvariant();
        if (ext != ".glb" && ext != ".gltf") { o.UseBlender = "the source is not a .glb/.gltf file"; return o; }
        try
        {
            HafModel m;
            try { m = GlbReader.Read(src); }
            catch (Exception e) when (e is InvalidDataException || e is NotSupportedException) { o.UseBlender = "the C# reader cannot handle this input: " + e.Message; return o; }
            Tick();
            var names = BlenderNames.Compute(m);
            // the decisions, then the whole run through the bake, the fire window, the retarget, the recoil and the bind
            var r = BlenderDeploy.Decide(m, argv, names);
            if (r.Fallback != null) { o.UseBlender = r.Fallback; return o; }
            if (r.Exit) { o.ScriptExit = true; o.Log.AddRange(r.Log.Where(l => l.StartsWith("DEPLOY", StringComparison.Ordinal))); return o; }
            // the work ahead is per bone per frame, on the editor thread, with the checkpoint only between the steps: a
            // long animation is Blender's, whose process the Factory bounds with a timeout (the reviewer's note on PR #153)
            long span = (long)r.FrameMax - r.FrameMin + 1;
            if (span > MaxFramesInProcess) { o.UseBlender = $"an animation of {span} frames (the in-process conversion takes up to {MaxFramesInProcess}; Blender runs under the Factory's timeout)"; return o; }
            Tick();
            var baked = BlenderDeploy.Decide(m, argv, names, true);
            if (baked.Fallback != null) { o.UseBlender = baked.Fallback; return o; }
            Tick();
            void Lines(IEnumerable<string> lines) { o.Log.AddRange(lines.Where(l => l.StartsWith("DEPLOY", StringComparison.Ordinal))); }
            Lines(baked.Log); Lines(baked.BakeLog); Lines(baked.FireLog); Lines(baked.RetargetLog); Lines(baked.RecoilLog);
            if (baked.ExitAtRecoil) { o.ScriptExit = true; return o; }
            Lines(baked.BindLog); Lines(baked.Log7);
            // the role clips (7c), then the export's structure, materials, channels and bytes
            baked.Finish();
            if (baked.RoleFallback != null) { o.UseBlender = "the role clips: " + baked.RoleFallback; return o; }
            Lines(baked.RoleLog);
            Tick();
            var x = BlenderDeployExport.Build(m, names, baked, argv);
            if (x.Fallback != null) { o.UseBlender = "the export: " + x.Fallback; return o; }
            Lines(x.Log);
            Tick();
            var w = BlenderGlbWriter.Build(x);
            if (w.Fallback != null) { o.UseBlender = "the file: " + w.Fallback; return o; }
            // a temporary file, then an atomic replace: never half a file where the slim step looks, and the old file
            // stays until the new one is in place (File.Replace; the reviewer's note on PR #153)
            string tmp = outGlb + ".tmp";
            File.WriteAllBytes(tmp, w.Glb);
            if (File.Exists(outGlb)) File.Replace(tmp, outGlb, null); else File.Move(tmp, outGlb);
            o.Log.Add("DEPLOY wrote: " + outGlb);
            o.Written = true;
            return o;
        }
        catch (OperationCanceledException) { throw; }   // a bake-test cancel passes through every wrapper
        catch (Exception e)
        {
            try { if (File.Exists(outGlb + ".tmp")) File.Delete(outGlb + ".tmp"); } catch { }
            o.Written = false; o.ScriptExit = false;
            o.UseBlender = "the C# conversion failed (" + e.GetType().Name + ": " + e.Message + ")";
            return o;
        }
    }
}
