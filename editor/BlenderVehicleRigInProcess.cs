// vehicle_rig.py's five ported stages and GLB writer behind Generate. Named limits fall back
// to Blender; the script's own exit/death remains a failed Generate. No Unity dependency.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class BlenderVehicleRigInProcess
{
    public static string NormalizeRoute(string route)
    {
        string value = (route ?? "").Trim().ToLowerInvariant();
        return value == "csharp" || value == "blender" ? value : "";
    }
    public static bool UseCSharp(string recipeRoute, string editorDefault)
    {
        string route = NormalizeRoute(recipeRoute);
        return (route.Length > 0 ? route : NormalizeRoute(editorDefault)) == "csharp";
    }

    public sealed class Outcome
    {
        public bool Written, ScriptExit;
        public string ScriptError, UseBlender;
        public readonly List<string> Log = new List<string>();
    }

    sealed class ReaderLimit : Exception { public ReaderLimit(string message) : base(message) { } }
    static HafModel Read(string path)
    {
        try { return GlbReader.Read(path); }
        catch (Exception e) when (e is InvalidDataException || e is NotSupportedException) { throw new ReaderLimit(e.Message); }
    }

    // Exact tokens after --, including mode/input/output/preview. The caller leaves preview empty
    // when using the exported GLB as its preview; an explicit FBX request remains a named limit.
    public static Outcome Run(IReadOnlyList<string> argv, Action checkpoint = null)
    {
        var o = new Outcome(); string temporary = null;
        try
        {
            void Tick() { checkpoint?.Invoke(); }
            Tick();
            var a = BlenderVehicleRig.ParseArgs(argv.ToList());
            string sourceLimit = BlenderVehicleRig.SourceFallback(a);
            if (a.Problem == null && sourceLimit != null) { o.UseBlender = sourceLimit; return o; }
            if (a.Problem == null && a.Mode == "rig" && a.PreDie == null && !File.Exists(a.Input)) { o.UseBlender = "the model file is not there"; return o; }
            HafModel m = a.Problem == null && a.Mode == "rig" && a.PreDie == null ? Read(a.Input) : null;
            Tick();
            var s = BlenderVehicleRig.RunStage1(m, a, Read); o.Log.AddRange(s.Logs);
            bool Stop()
            {
                if (s.Fallback != null || s.Deferred != null) { o.UseBlender = s.Fallback ?? s.Deferred; return true; }
                if (s.Exits || s.Dies != null) { o.ScriptExit = true; o.ScriptError = s.Dies; return true; }
                return false;
            }
            if (Stop()) return o;
            string materialLimit = BlenderVehicleRig.ExportMaterialFallback(s, a, preserveJpegAlpha: true);
            if (materialLimit != null) { o.UseBlender = materialLimit; return o; }
            Tick(); BlenderVehicleRig.RunStage2(s, a); o.Log.AddRange(s.Logs2); if (Stop()) return o;
            Tick(); BlenderVehicleRig.RunStage3(s, a); o.Log.AddRange(s.Logs3); if (Stop()) return o;
            Tick(); BlenderVehicleRig.RunStage4(s, a); o.Log.AddRange(s.Logs4); if (Stop()) return o;
            Tick(); var x = BlenderVehicleRig.RunStage5(s, a, preserveJpegAlpha: true);
            string completed = s.Logs5.LastOrDefault(l => l.StartsWith("VEHICLE RIG DONE:", StringComparison.Ordinal));
            o.Log.AddRange(s.Logs5.Where(l => !l.StartsWith("VEHICLE RIG DONE:", StringComparison.Ordinal))); if (Stop()) return o;
            Tick(); var file = BlenderGlbWriter.Build(x);
            if (file.Fallback != null) { o.UseBlender = "the file: " + file.Fallback; return o; }
            // Do not touch a previous successful result on a decline, error, cancellation or failed write.
            temporary = a.OutGlb + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, file.Glb);
            Tick();
            if (File.Exists(a.OutGlb)) File.Replace(temporary, a.OutGlb, null); else File.Move(temporary, a.OutGlb);
            o.Written = true;
            if (completed != null) o.Log.Add(completed);
            return o;
        }
        catch (OperationCanceledException) { throw; }
        catch (ReaderLimit e) { o.UseBlender = "the C# reader cannot handle this input: " + e.Message; return o; }
        catch (Exception e) { o.UseBlender = "the C# rig run failed (" + e.GetType().Name + ": " + e.Message + ")"; return o; }
        finally { if (temporary != null) try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}
