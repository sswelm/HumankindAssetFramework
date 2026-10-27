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
    /// <summary>The Generate backend when neither the recipe nor the editor preference names one: the C# route (with its C++
    /// kernels) since 2026-10-27 - accepted in-game on the galley and the Saleg's Revenge, every other reason to run Blender is a
    /// named fallback the route takes by itself. Until then "blender".</summary>
    public const string FactoryDefaultRoute = "csharp";
    public static bool UseCSharp(string recipeRoute, string editorDefault)
    {
        string route = NormalizeRoute(recipeRoute);
        return (route.Length > 0 ? route : NormalizeRoute(editorDefault)) == "csharp";
    }

    // The one Blender fallback acceptance EXPECTS: a source the C# does not read (SourceFallback: .fbx/.obj/.blend). Every other
    // reason - a reader refusal, the material preflight, a stage's named limit, a crash - is C# coverage lost, and the acceptance
    // row fails on it until it is fixed or this rule is widened on purpose (review of #188/#191).
    public static bool ExpectedFallback(string source, string reason)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrEmpty(reason)) return false;
        return reason == BlenderVehicleRig.SourceFallback(new BlenderVehicleRig.Args { Input = source });
    }

    public sealed class Outcome
    {
        public bool Written, ScriptExit;
        // Error: a Generate that FAILED in C# and is reported as such - the Lab's deadline, an output the file system refused -
        // not handed to Blender (which would run its own five minutes, to write to the same refused place)
        public string ScriptError, UseBlender, Error;
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
        // the stage times on the log, in the Blender script's own "VEHICLE timing:" form (2026-10-27: the user's C# Generate
        // took 16 s on the Saleg's and the log could not say where; every reader of VEHICLE lines already skips these)
        var watch = System.Diagnostics.Stopwatch.StartNew(); long lap = 0;
        void Lap(string label)
        {
            long now = watch.ElapsedMilliseconds;
            o.Log.Add("VEHICLE timing: " + label.PadRight(12) + " " + ((now - lap) / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5) + "s");
            lap = now;
        }
        try
        {
            void Tick() { checkpoint?.Invoke(); }
            Tick();
            var a = BlenderVehicleRig.ParseArgs(argv.ToList());
            string sourceLimit = BlenderVehicleRig.SourceFallback(a);
            if (a.Problem == null && sourceLimit != null) { o.UseBlender = sourceLimit; return o; }
            if (a.Problem == null && a.Mode == "rig" && a.PreDie == null && !File.Exists(a.Input)) { o.UseBlender = "the model file is not there"; return o; }
            HafModel m = a.Problem == null && a.Mode == "rig" && a.PreDie == null ? Read(a.Input) : null;
            Lap("import");
            Tick();
            var s = BlenderVehicleRig.RunStage1(m, a, Read); o.Log.AddRange(s.Logs); Lap("prep");
            bool Stop()
            {
                if (s.Fallback != null || s.Deferred != null) { o.UseBlender = s.Fallback ?? s.Deferred; return true; }
                if (s.Exits || s.Dies != null) { o.ScriptExit = true; o.ScriptError = s.Dies; return true; }
                return false;
            }
            if (Stop()) return o;
            string materialLimit = BlenderVehicleRig.ExportMaterialFallback(s, a, preserveJpegAlpha: true);
            if (materialLimit != null) { o.UseBlender = materialLimit; return o; }
            Tick(); BlenderVehicleRig.RunStage2(s, a, Tick); o.Log.AddRange(s.Logs2); Lap("reduce"); if (Stop()) return o;
            Tick(); BlenderVehicleRig.RunStage3(s, a); o.Log.AddRange(s.Logs3); Lap("cluster + armature"); if (Stop()) return o;
            Tick(); BlenderVehicleRig.RunStage4(s, a); o.Log.AddRange(s.Logs4); Lap("skinning"); if (Stop()) return o;
            Tick(); var x = BlenderVehicleRig.RunStage5(s, a, preserveJpegAlpha: true);
            string completed = s.Logs5.LastOrDefault(l => l.StartsWith("VEHICLE RIG DONE:", StringComparison.Ordinal));
            o.Log.AddRange(s.Logs5.Where(l => !l.StartsWith("VEHICLE RIG DONE:", StringComparison.Ordinal))); Lap("join + clips"); if (Stop()) return o;
            Tick(); var file = BlenderGlbWriter.Build(x);
            if (file.Fallback != null) { o.UseBlender = "the file: " + file.Fallback; return o; }
            // Do not touch a previous successful result on a decline, error, cancellation or failed write.
            temporary = a.OutGlb + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, file.Glb);
                Tick();
                if (File.Exists(a.OutGlb)) File.Replace(temporary, a.OutGlb, null); else File.Move(temporary, a.OutGlb);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { o.Error = "the output could not be written (" + e.GetType().Name + ": " + e.Message + ")"; return o; }
            o.Written = true;
            Lap("export (glb)");
            if (completed != null) o.Log.Add(completed);
            return o;
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException e) { o.Error = e.Message; return o; }
        catch (ReaderLimit e) { o.UseBlender = "the C# reader cannot handle this input: " + e.Message; return o; }
        catch (Exception e) { o.UseBlender = "the C# rig run failed (" + e.GetType().Name + ": " + e.Message + ")"; return o; }
        finally { if (temporary != null) try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}
