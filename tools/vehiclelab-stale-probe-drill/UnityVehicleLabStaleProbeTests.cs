using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Run in the temporary project made by vehiclelab_stale_probe_unity_drill.sh: the real Vehicle Lab window, a real fused
// model, and the sequence that failed on 2026-11-26 - Probe, mark, the Fuser rewrites the source (renaming a fused
// group), Generate. The model's fused meshes are renamed in its glTF JSON to play the "before" and "after" files.
public static class UnityVehicleLabStaleProbeTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static FieldInfo Field(string name) => typeof(VehicleLabWindow).GetField(name, Private);
    static object Call(VehicleLabWindow w, string name, params object[] args) => typeof(VehicleLabWindow).GetMethod(name, Private).Invoke(w, args);

    // the glTF with every node and mesh called `from` renamed to `to` (both: the probe may name a part after either)
    static byte[] Renamed(byte[] glb, params (string from, string to)[] renames)
    {
        int jsonLen = BitConverter.ToInt32(glb, 12);
        var json = JObject.Parse(Encoding.UTF8.GetString(glb, 20, jsonLen));
        foreach (string list in new[] { "nodes", "meshes" })
            foreach (var o in (json[list] as JArray ?? new JArray()).OfType<JObject>())
                foreach (var (from, to) in renames)
                    if ((string)o["name"] == from) o["name"] = to;
        byte[] j = Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None));
        int pad = (4 - j.Length % 4) % 4;
        byte[] rest = new byte[glb.Length - 20 - jsonLen];
        Array.Copy(glb, 20 + jsonLen, rest, 0, rest.Length);
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(0x46546C67); bw.Write(2); bw.Write(12 + 8 + j.Length + pad + rest.Length);
            bw.Write(j.Length + pad); bw.Write(0x4E4F534A); bw.Write(j); for (int i = 0; i < pad; i++) bw.Write((byte)0x20);
            bw.Write(rest);
            return ms.ToArray();
        }
    }

    public static void Run()
    {
        var log = new StringBuilder(); int failed = 0;
        void Check(string name, bool ok, string detail = "") { log.AppendLine((ok ? "PASS " : "FAIL ") + name + (ok || detail == "" ? "" : "\n     " + detail.Replace("\n", "\n     "))); if (!ok) failed++; }
        var args = Environment.GetCommandLineArgs();
        string Arg(string key) { int at = Array.IndexOf(args, key); return at >= 0 && at + 1 < args.Length ? args[at + 1] : ""; }
        var w = ScriptableObject.CreateInstance<VehicleLabWindow>();
        try
        {
            byte[] model = File.ReadAllBytes(Arg("-hafStaleModel"));
            int jl = BitConverter.ToInt32(model, 12);
            var meshes = ((JArray)JObject.Parse(Encoding.UTF8.GetString(model, 20, jl))["meshes"]).Select(m => (string)m["name"]).ToList();
            // the Skarv's own pair when present, else the first fused split child: its "before" name is its parent's
            var child = new Regex(@"^(Fused_.+)_Part_\d{3,}$");
            string after = meshes.Contains("Fused_P_Material2_11_Part_001") ? "Fused_P_Material2_11_Part_001" : meshes.First(n => child.IsMatch(n));
            string before = child.Match(after).Groups[1].Value;
            string gone = meshes.First(n => n.StartsWith("Fused_", StringComparison.Ordinal) && n != after);   // renamed out of any group: a marking that cannot land
            log.AppendLine($"model {Path.GetFileName(Arg("-hafStaleModel"))}: before '{before}' -> after '{after}'; lost '{gone}'");

            string proj = Directory.GetParent(Application.dataPath).FullName;
            string dir = Path.Combine(proj, "Logs", "stale"); Directory.CreateDirectory(dir);
            string src = Path.Combine(dir, "boat_fused.glb").Replace('\\', '/');
            string outGlb = Path.Combine(dir, "boat_fused_Spin.glb").Replace('\\', '/');
            DateTime clock = DateTime.UtcNow.AddHours(-1);
            void WriteSource(byte[] bytes) { File.WriteAllBytes(src, bytes); clock = clock.AddSeconds(10); File.SetLastWriteTimeUtc(src, clock); }   // a distinct write time per "fuse"

            Field("headless").SetValue(w, true); Field("headlessDir").SetValue(w, "Logs/stale");
            Field("srcFile").SetValue(w, src); Field("outGlb").SetValue(w, outGlb); Field("generateRoute").SetValue(w, "csharp");
            string Status() => (string)Field("status").GetValue(w);
            object[] Parts() => ((IEnumerable)Field("parts").GetValue(w)).Cast<object>().ToArray();
            object PartNamed(string n) => Parts().FirstOrDefault(p => (string)p.GetType().GetField("name").GetValue(p) == n);
            string RoleOf(string n) { var p = PartNamed(n); return p == null ? "(no such part)" : p.GetType().GetField("role").GetValue(p).ToString(); }
            void Mark(string n, string role) { var p = PartNamed(n); var f = p.GetType().GetField("role"); f.SetValue(p, Enum.Parse(f.FieldType, role)); }
            bool Generate() { if (File.Exists(outGlb)) File.Delete(outGlb); Call(w, "Vehicleize"); return Status().StartsWith("DONE", StringComparison.Ordinal) && File.Exists(outGlb); }

            // 1. the file Probe read, unchanged: Generate as before, no re-probe
            WriteSource(Renamed(model, (after, before)));
            Call(w, "Probe");
            Check("probe lists the pre-fuse name " + before, PartNamed(before) != null, Status());
            Mark(before, "Wheel");
            Check("unchanged source: Generate runs without re-probing", Generate() && !Status().Contains("Re-probed"), Status());

            // 2. the Skarv: the Fuser rewrites the source and the group is now a split child
            WriteSource(model);
            bool ok2 = Generate();
            Check("rewritten source: Generate re-probes and succeeds (was: VEHICLE ERROR part '" + before + "' not found)", ok2 && Status().Contains("Re-probed first: the source changed since Probe: the Raw model was rewritten"), Status());
            Check("rewritten source: the Wheel landed on " + after, RoleOf(after) == "Wheel", RoleOf(after));
            Check("rewritten source: the old name is gone from the parts list", PartNamed(before) == null);

            // 3. a marking with nowhere to land: Generate stops, names it, writes nothing; the next press generates
            Mark(gone, "Turret");
            WriteSource(Renamed(model, (gone, "Zz_Renamed_Part")));
            bool ok3 = Generate();
            Check("lost marking: Generate stops before writing", !ok3 && !File.Exists(outGlb) && Status().StartsWith("Generate stopped: the source changed since Probe", StringComparison.Ordinal), Status());
            Check("lost marking: the status names it and its role", Status().Contains(gone + " (Turret)"), Status());
            Check("lost marking: the Wheel still carried", RoleOf(after) == "Wheel", RoleOf(after));
            Check("lost marking: the second press generates (the parts list now matches the file)", Generate() && !Status().Contains("Re-probed"), Status());

            // 4. a recipe-loaded session is not a probe: its parts run as saved, a touched source is not re-probed
            string recipe = Path.Combine(dir, "boat.json");
            File.WriteAllText(recipe, JsonUtility.ToJson(Call(w, "BuildRecipe")));
            Call(w, "LoadRecipeFromPath", recipe);
            Check("recipe load clears the probe record", (string)Field("probedSourceStamp").GetValue(w) == "");
            File.SetLastWriteTimeUtc(src, clock.AddSeconds(30));
            Check("recipe session: a touched source generates as saved, no re-probe", Generate() && !Status().Contains("Re-probed"), Status());

            // 5. a re-Probe of the recipe session arms the check again
            Call(w, "Probe");
            Check("re-Probe records the source again", ((string)Field("probedSourceStamp").GetValue(w)).StartsWith("Raw model\t", StringComparison.Ordinal));
        }
        catch (Exception ex) { failed++; log.AppendLine("FAIL " + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex)); }
        finally { try { Call(w, "DestroyPreview"); } catch { } UnityEngine.Object.DestroyImmediate(w); }
        log.AppendLine("TOTAL failed " + failed);
        string output = Arg("-hafStaleOutput");
        if (output != "") File.WriteAllText(output, log.ToString());
        Debug.Log(log.ToString()); EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
