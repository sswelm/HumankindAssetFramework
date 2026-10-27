// BlenderRigAnimInProcess.cs - rig_anim.py in this process (step (g) of the rig_anim port, 2026-10-14; docs/Review-Backlog.md
// item (4)): the script's four stages (BlenderRigAnim, steps (b) to (e)) and the GLB its stage 4 scene exports to
// (BlenderRigExport + BlenderGlbWriter, step (f)) - every one held to Blender by tools/riganim_drill.sh - as the Factory's
// animated path runs them on its GLB route. What any step names as Blender's (a Fallback, a Deferred branch) is Blender's:
// the Factory runs the script then, on its FBX route, as before. The script's own exits (no armature, no clip, no mesh,
// a socket without a parent) are the script's outcome, not a fallback: Blender would stop the same way.
// What the Factory gets back beside the file: the RIGANIM lines, the primary action's name and each state role's (the
// GLB carries every kept action as one animation, named after it; the FBX route wrote one file per role), and the
// albedo image the script would have saved at argv[5] - its source bytes and type (a PNG is written as it is; a JPEG the
// caller encodes as a PNG, as Blender's image.save does) with its size. Inside the GLB, Blender's file carries that saved
// PNG (the albedo renamed to the file's stem): a PNG source's bytes are its own and the file is Blender's byte for byte; a
// JPEG source's PNG is Blender's encoder's and is not reproduced - the file carries the SOURCE JPEG under the same name,
// typed image/jpeg (the Factory's import reads either), and the Bake Tests row holds the two models with the image bytes
// aside. The drill marks such a job "the GLB's bytes not compared" and reads the written file back instead.
// Alpha-reading source JPEGs also retain their encoding; the native acceptance row requires exact RGBA pixels
// and strict non-image JSON/buffer parity for those conversions, rather than ignoring their image bytes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public static class BlenderRigAnimInProcess
{
    /// <summary>Bump when the in-process run's written file changes: the Factory's slim cache key carries it, so a cached
    /// GLB made by an older port is made again.</summary>
    public const string CacheRevision = "2026-10-27.pose-reset.1"; // re-slim cached rigs with the post-rebake pose-channel reset

    public sealed class Outcome
    {
        public bool Written;                 // the GLB is there
        public bool ScriptExit;              // the script stops itself (sys.exit(1) after its RIGANIM ERROR line): no file, no Blender run either
        public string UseBlender;            // why the script has to run (a shape the port names, an input the reader refuses, a failure here)
        public readonly List<string> Log = new List<string>();   // the RIGANIM lines, in the script's order
        public string PrimaryAction = "";    // the action the primary clip ended as (the glTF animation's name)
        public readonly List<(string role, string action)> RoleActions = new List<(string, string)>();   // each state role's action, in the script's order
        public readonly HashSet<int> RetainedJpegAlphaImages = new HashSet<int>(); // PNG/JPEG pairs require exact pixel and non-image byte checks
        public readonly HashSet<int> SourceImageFallbacks = new HashSet<int>();   // only these exported images differ from Blender's PNG encoding
        public byte[] AlbedoBytes; public string AlbedoMime = ""; public string AlbedoPath; public int AlbedoWidth, AlbedoHeight;   // the albedo the script would save (null bytes: none written)
    }

    /// <summary>rig_anim.py's argv[2..14] as the Factory formats them for the script (UniversalBaker.RigAnimViaBlender's
    /// order): target, bone prefixes, clip, albedo path, keepmats, rotation "x,y,z", convertRig, state roles, auto-ground,
    /// sockets, keepTranslations, static parts, localNodeAnim.</summary>
    public static string[] Argv(int targetTris, string bonePrefixes, string clipName, string albedoOut, bool keepMaterials, string rotation, bool convertRig,
                                string stateRoles, bool autoGround, string socketBones, bool keepTranslations, string staticParts, bool localNodeAnim)
        => new[] { Math.Max(0, targetTris).ToString(CultureInfo.InvariantCulture), bonePrefixes ?? "", clipName ?? "", albedoOut ?? "", keepMaterials ? "1" : "0",
                   rotation ?? "0,0,0", convertRig ? "1" : "0", stateRoles ?? "", autoGround ? "1" : "0", socketBones ?? "", keepTranslations ? "1" : "0", staticParts ?? "", localNodeAnim ? "1" : "0" };

    /// <summary>The run of `src` into `outGlb` with the script's arguments (argv[2:]). `checkpoint`, when given, runs
    /// between the stages: a cancel (OperationCanceledException) passes through, a deadline (any other exception) leaves
    /// the model to Blender.</summary>
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
            var a = BlenderRigAnim.ParseArgs(new[] { src, outGlb }.Concat(argv).ToList());
            o.Log.AddRange(a.Logs);
            if (a.Problem != null) { o.Log.Add(a.Problem); o.ScriptExit = true; return o; }   // the script dies on its arguments (int() of the target)
            var s = BlenderRigAnim.RunStage1(m, a);
            bool Stop(string stage)
            {
                if (s.Fallback != null) { o.UseBlender = stage + ": " + s.Fallback; return true; }
                if (s.Deferred != null) { o.UseBlender = stage + ": " + s.Deferred; return true; }
                if (s.Exits) { o.ScriptExit = true; return true; }
                return false;
            }
            o.Log.AddRange(s.Logs.Skip(a.Logs.Count));
            if (Stop("stage 1")) return o;
            Tick();
            BlenderRigAnim.RunStage2(s, a); o.Log.AddRange(s.Logs2);
            if (Stop("stage 2")) return o;
            Tick();
            BlenderRigAnim.RunStage3(s, a); o.Log.AddRange(s.Logs3);
            if (Stop("stage 3")) return o;
            Tick();
            BlenderRigAnim.RunStage4(s, a); o.Log.AddRange(s.Logs4);
            if (Stop("stage 4")) return o;
            Tick();
            o.PrimaryAction = s.PrimaryAction ?? "";
            o.RoleActions.AddRange(s.RoleActions);
            // the albedo: the script saves the image at argv[5] as a PNG (its own encoder); the source's bytes and type go
            // to the caller, who writes a PNG source as it is and encodes a JPEG one
            if (s.Albedo.path != null && s.AlbedoImage >= 0 && s.AlbedoImage < m.Images.Count)
            {
                var im = m.Images[s.AlbedoImage];
                if (im.Bytes == null) { o.UseBlender = "the albedo image's bytes are not at hand (" + im.Uri + ")"; return o; }
                o.AlbedoBytes = im.Bytes; o.AlbedoPath = s.Albedo.path; o.AlbedoWidth = s.Albedo.w; o.AlbedoHeight = s.Albedo.h;
                o.AlbedoMime = im.Bytes.Length >= 4 && im.Bytes[0] == 0x89 && im.Bytes[1] == 0x50 ? "image/png" : im.Bytes.Length >= 2 && im.Bytes[0] == 0xFF && im.Bytes[1] == 0xD8 ? "image/jpeg" : (im.MimeType ?? "");
            }
            var x = BlenderRigExport.Build(s, a, preserveJpegAlpha: true);
            if (x.Fallback != null) { o.UseBlender = "the export: " + x.Fallback; return o; }
            o.RetainedJpegAlphaImages.UnionWith(x.MaterialRows.RetainedJpegAlphaImages);
            o.Log.AddRange(x.Log);
            // an image whose bytes the export does not know (a JPEG albedo: Blender's PNG re-encode) carries the SOURCE's bytes and type
            for (int i = 0; i < x.MaterialRows.Images.Count; i++)
            {
                var im = x.MaterialRows.Images[i];
                if (im.bytes != null) continue;
                var srcImage = im.source >= 0 && im.source < m.Images.Count ? m.Images[im.source] : null;
                if (srcImage == null || srcImage.Bytes == null) { o.UseBlender = "the file: image '" + im.name + "' has no bytes to write"; return o; }
                string mime = ImageMime(srcImage.Bytes);
                if (mime == null) { o.UseBlender = "the file: image '" + im.name + "' has no known PNG/JPEG encoding"; return o; }
                x.MaterialRows.Images[i] = (im.name, mime, srcImage.Bytes, im.source);
                o.SourceImageFallbacks.Add(i);
                o.Log.Add("RIGANIM glb: image '" + im.name + "' carries the source's " + x.MaterialRows.Images[i].mime + " bytes (Blender's PNG re-encode is not reproduced)");
            }
            Tick();
            var w = BlenderGlbWriter.Build(x);
            if (w.Fallback != null) { o.UseBlender = "the file: " + w.Fallback; return o; }
            // a temporary file, then an atomic replace: never half a file where the import looks
            string tmp = outGlb + ".tmp";
            // the output's folder may not exist yet (the acceptance row bakes under throwaway names; the FBX route creates its
            // folder on its own side): the port crashed on UniversalTanks with DirectoryNotFound and the bake fell back (2026-10-27)
            string outDir = Path.GetDirectoryName(outGlb);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            File.WriteAllBytes(tmp, w.Glb);
            if (File.Exists(outGlb)) File.Replace(tmp, outGlb, null); else File.Move(tmp, outGlb);
            o.Log.Add("RIGANIM wrote " + outGlb + " ('" + o.PrimaryAction + "', " + x.Animations.Count.ToString(CultureInfo.InvariantCulture) + " animation(s))");
            o.Written = true;
            return o;
        }
        catch (OperationCanceledException) { throw; }   // a bake-test cancel passes through every wrapper
        catch (Exception e)
        {
            try { if (File.Exists(outGlb + ".tmp")) File.Delete(outGlb + ".tmp"); } catch { }
            o.Written = false; o.ScriptExit = false;
            o.UseBlender = "the C# rig run failed (" + e.GetType().Name + ": " + e.Message + ")";
            return o;
        }
    }

    internal static string ImageMime(byte[] bytes)
        => bytes != null && bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 ? "image/png"
         : bytes != null && bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF ? "image/jpeg" : null;

    /// <summary>The roles sidecar the Factory keeps beside the GLB: JSON with the primary action and every role/action pair - a later bake with 'Reuse extracted' reads the clip names from it.</summary>
    public static string RolesSidecar(string primary, IEnumerable<(string role, string action)> roles)
    {
        return new Newtonsoft.Json.Linq.JObject
        {
            ["primary"] = primary ?? "",
            ["roles"] = new Newtonsoft.Json.Linq.JArray(roles.Select(r => new Newtonsoft.Json.Linq.JObject { ["role"] = r.role, ["action"] = r.action }))
        }.ToString(Newtonsoft.Json.Formatting.None) + "\n";
    }

    /// <summary>The sidecar read back; false when it is not in the form RolesSidecar writes.</summary>
    public static bool ParseRolesSidecar(string text, out string primary, out List<(string role, string action)> roles)
    {
        primary = null; roles = new List<(string, string)>();
        if (text == null) return false;
        if (text.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                var j = Newtonsoft.Json.Linq.JObject.Parse(text);
                if (j["primary"]?.Type != Newtonsoft.Json.Linq.JTokenType.String || !(j["roles"] is Newtonsoft.Json.Linq.JArray array)) return false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in array)
                {
                    if (!(row is Newtonsoft.Json.Linq.JObject item) || item["role"]?.Type != Newtonsoft.Json.Linq.JTokenType.String || item["action"]?.Type != Newtonsoft.Json.Linq.JTokenType.String) return false;
                    string role = (string)item["role"];
                    if (role.Length == 0 || !seen.Add(role)) return false;
                    roles.Add((role, (string)item["action"]));
                }
                primary = (string)j["primary"]; return true;
            }
            catch (Newtonsoft.Json.JsonException) { return false; }
        }
        // Read caches written by the first GLB route version as well.
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0) return false;
            string k = line.Substring(0, tab), v = line.Substring(tab + 1);
            if (k == "primary") { if (primary != null) return false; primary = v; }
            else roles.Add((k, v));
        }
        return primary != null;
    }
}
