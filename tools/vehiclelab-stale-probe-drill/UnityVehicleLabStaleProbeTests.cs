using System;
using System.Collections;
using System.Collections.Generic;
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
// Scenarios 5-7 are the PR review's (2026-11-27): a stop must leave the session as it was, two marked members fused into
// one row are not both "carried", and a new row's guessed role is not generated unseen.
public static class UnityVehicleLabStaleProbeTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static FieldInfo Field(string name) => typeof(VehicleLabWindow).GetField(name, Private);
    static object Call(VehicleLabWindow w, string name, params object[] args) => typeof(VehicleLabWindow).GetMethod(name, Private).Invoke(w, args);

    // the glTF with every node and mesh called `from` renamed to `to` (both: the probe may name a part after either)
    static byte[] Renamed(byte[] glb, params (string from, string to)[] renames) => Edited(glb, false, renames);

    // stillSkin: drop the clips, so a skinned fixture is probed in-process (an animated skin goes to Blender's evaluated pose)
    static byte[] Edited(byte[] glb, bool stillSkin, params (string from, string to)[] renames)
    {
        int jsonLen = BitConverter.ToInt32(glb, 12);
        var json = JObject.Parse(Encoding.UTF8.GetString(glb, 20, jsonLen));
        if (stillSkin) json.Remove("animations");
        foreach (string list in new[] { "nodes", "meshes" })
            foreach (var o in (json[list] as JArray ?? new JArray()).OfType<JObject>())
                foreach (var (from, to) in renames)
                    if ((string)o["name"] == from) o["name"] = to;
        byte[] j = Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None));
        int pad = (4 - j.Length % 4) % 4;
        byte[] rest = new byte[glb.Length - 20 - jsonLen];
        Array.Copy(glb, 20 + jsonLen, rest, 0, rest.Length);
        // stillSkin also gives each BLENDED vertex wholly to its last weighted joint (the arm): the generator's skin blends half
        // its vertices 50/50, and the probe calls a source rigged only when 90 % of the vertices have one joint above 0.5
        // (VehicleProbe, RigBones). A vertex already above 0.5 keeps its joint, so both bones carry weights.
        if (stillSkin)
            foreach (var prim in json["meshes"].SelectMany(m => m["primitives"]))
            {
                var wa = prim["attributes"]?["WEIGHTS_0"]; if (wa == null) continue;
                var acc = json["accessors"][(int)wa]; var view = json["bufferViews"][(int)acc["bufferView"]];
                if ((int)acc["componentType"] != 5126) continue;   // float weights only
                int at = 8 + ((int?)view["byteOffset"] ?? 0) + ((int?)acc["byteOffset"] ?? 0);   // 8: past the BIN chunk header
                for (int v = 0; v < (int)acc["count"]; v++)
                {
                    var wv = Enumerable.Range(0, 4).Select(k => BitConverter.ToSingle(rest, at + v * 16 + k * 4)).ToArray();
                    if (wv.Max() > 0.5f) continue;
                    int last = Array.FindLastIndex(wv, x => x > 0f); if (last < 0) last = 0;
                    for (int k = 0; k < 4; k++) Array.Copy(BitConverter.GetBytes(k == last ? 1f : 0f), 0, rest, at + v * 16 + k * 4, 4);
                }
            }
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
        VehicleLabWindow w = null;
        try
        {
            byte[] model = File.ReadAllBytes(Arg("-hafStaleModel"));
            int jl = BitConverter.ToInt32(model, 12);
            var meshes = ((JArray)JObject.Parse(Encoding.UTF8.GetString(model, 20, jl))["meshes"]).Select(m => (string)m["name"]).ToList();
            // the Skarv's own pair when present, else the first fused split child: its "before" name is its parent's
            var child = new Regex(@"^(Fused_.+)_Part_\d{3,}$");
            string after = meshes.Contains("Fused_P_Material2_11_Part_001") ? "Fused_P_Material2_11_Part_001" : meshes.First(n => child.IsMatch(n));
            string before = child.Match(after).Groups[1].Value;
            var others = meshes.Where(n => n.StartsWith("Fused_", StringComparison.Ordinal) && n != after).ToList();
            string gone = others[0];                       // renamed out of any group: a marking that cannot land
            string pairA = others[1], pairB = others[2];   // two marked members of one group, fused into one row
            string guessed = others[3];                    // an unmarked part whose new name the probe guesses a role from
            log.AppendLine($"model {Path.GetFileName(Arg("-hafStaleModel"))}: before '{before}' -> after '{after}'; lost '{gone}'; pair '{pairA}' + '{pairB}'; guessed '{guessed}'");

            string proj = Directory.GetParent(Application.dataPath).FullName;
            string dir = Path.Combine(proj, "Logs", "stale"); Directory.CreateDirectory(dir);
            string src = Path.Combine(dir, "boat_fused.glb").Replace('\\', '/');
            string elsewhere = Path.Combine(dir, "other_fused.glb").Replace('\\', '/');
            string outGlb = Path.Combine(dir, "boat_fused_Spin.glb").Replace('\\', '/');
            DateTime clock = DateTime.UtcNow.AddHours(-1);
            void WriteFile(string path, byte[] bytes) { File.WriteAllBytes(path, bytes); clock = clock.AddSeconds(10); File.SetLastWriteTimeUtc(path, clock); }   // a distinct write time per "fuse"
            void WriteSource(byte[] bytes) => WriteFile(src, bytes);

            string Status() => (string)Field("status").GetValue(w);
            string Stamp() => (string)Field("probedSourceStamp").GetValue(w);
            object[] Parts() => ((IEnumerable)Field("parts").GetValue(w)).Cast<object>().ToArray();
            string[] Names() => Parts().Select(p => (string)p.GetType().GetField("name").GetValue(p)).ToArray();
            object PartNamed(string n) => Parts().FirstOrDefault(p => (string)p.GetType().GetField("name").GetValue(p) == n);
            string RoleOf(string n) { var p = PartNamed(n); return p == null ? "(no such part)" : p.GetType().GetField("role").GetValue(p).ToString(); }
            void Mark(string n, string role) { var p = PartNamed(n); var f = p.GetType().GetField("role"); f.SetValue(p, Enum.Parse(f.FieldType, role)); }
            void Place(string n, Vector3 offset) { var p = PartNamed(n); p.GetType().GetField("offset").SetValue(p, offset); }
            int Swaps() => (int)Field("probeSwaps").GetValue(w);
            bool Generate() { if (File.Exists(outGlb)) File.Delete(outGlb); Call(w, "Vehicleize"); return Status().StartsWith("DONE", StringComparison.Ordinal) && File.Exists(outGlb); }
            // a fresh window on `bytes`, probed, with these markings
            void Session(byte[] bytes, params (string name, string role)[] marks)
            {
                if (w != null) { try { Call(w, "DestroyPreview"); } catch { } UnityEngine.Object.DestroyImmediate(w); }
                foreach (var f in Directory.GetFiles(dir, "*.txt")) File.Delete(f);   // no role lists of an earlier scenario's Generate
                w = ScriptableObject.CreateInstance<VehicleLabWindow>();
                Field("headless").SetValue(w, true); Field("headlessDir").SetValue(w, "Logs/stale");
                Field("srcFile").SetValue(w, src); Field("outGlb").SetValue(w, outGlb); Field("generateRoute").SetValue(w, "csharp");
                WriteSource(bytes);
                Call(w, "Probe");
                foreach (var (n, r) in marks) Mark(n, r);
            }
            // the stop's contract: nothing written, the session exactly as it was
            void Untouched(string what, string[] names, string stamp, params (string name, string role)[] roles)
            {
                Check(what + ": nothing written", !File.Exists(outGlb));
                Check(what + ": the parts list is the probed one", Names().SequenceEqual(names), string.Join(", ", Names()));
                Check(what + ": the probe record did not move (the next Generate checks again)", Stamp() == stamp);
                foreach (var (n, r) in roles) Check(what + ": " + n + " is still " + r, RoleOf(n) == r, RoleOf(n));
            }

            // 1. the file Probe read, unchanged: Generate as before, no re-probe
            Session(Renamed(model, (after, before)), (before, "Wheel"));
            Check("probe lists the pre-fuse name " + before, PartNamed(before) != null, Status());
            Check("unchanged source: Generate runs without re-probing", Generate() && !Status().Contains("Re-probed"), Status());

            // 2. the Skarv: the Fuser rewrites the source and the group is now a split child
            WriteSource(model);
            Check("rewritten source: Generate re-probes and succeeds (was: VEHICLE ERROR part '" + before + "' not found)", Generate() && Status().Contains("Re-probed first: the source changed since Probe: the Raw model was rewritten"), Status());
            Check("rewritten source: the Wheel landed on " + after, RoleOf(after) == "Wheel", RoleOf(after));
            Check("rewritten source: the old name is gone from the parts list", PartNamed(before) == null);

            // 3. a marking with nowhere to land: Generate stops and names it; the session is untouched; a second press stops again
            Mark(gone, "Turret");
            var names3 = Names(); string stamp3 = Stamp();
            WriteSource(Renamed(model, (gone, "Zz_Renamed_Part")));
            Check("lost marking: Generate stops", !Generate() && Status().StartsWith("Generate stopped: the source changed since Probe", StringComparison.Ordinal), Status());
            Check("lost marking: the status names it and its role", Status().Contains(gone + " (Turret)"), Status());
            Untouched("lost marking", names3, stamp3, (gone, "Turret"), (after, "Wheel"));
            Check("lost marking: a second press stops again (the review's 0-wheel DONE)", !Generate() && Status().StartsWith("Generate stopped", StringComparison.Ordinal), Status());
            Untouched("lost marking, second press", names3, stamp3, (gone, "Turret"), (after, "Wheel"));
            // the review's scenario A: the right file back - every marking is still there to generate with
            WriteSource(model);
            Check("file back: Generate re-probes and generates", Generate() && Status().Contains("Re-probed first"), Status());
            Check("file back: the Turret survived the stop", RoleOf(gone) == "Turret", RoleOf(gone));
            Check("file back: the Wheel survived the stop", RoleOf(after) == "Wheel", RoleOf(after));

            // 4. re-pointed at another file and back (the review's wording): a stop, then the probed file generates unprobed
            var names4 = Names(); string stamp4 = Stamp();
            WriteFile(elsewhere, Renamed(model, (gone, "Zz_Renamed_Part")));
            Field("srcFile").SetValue(w, elsewhere);
            Check("re-pointed: Generate stops, naming the field", !Generate() && Status().Contains("the Raw model now names another file"), Status());
            Untouched("re-pointed", names4, stamp4, (gone, "Turret"), (after, "Wheel"));
            Field("srcFile").SetValue(w, src);
            Check("pointed back: Generate runs on the probed file without re-probing", Generate() && !Status().Contains("Re-probed"), Status());

            // 5. the review's scenario B: two marked members of one fused group are now ONE row - neither carries
            string zqA1 = "Fused_Zq_A_Part_001", zqA2 = "Fused_Zq_A_Part_002";
            Session(Renamed(model, (pairA, zqA1), (pairB, zqA2)), (after, "Wheel"), (zqA1, "Wheel"), (zqA2, "Turret"));
            var names5 = Names(); string stamp5 = Stamp();
            WriteSource(Renamed(model, (pairA, "Fused_Zq_B"), (pairB, "Other_Mesh_9")));
            Check("two markings onto one row: Generate stops", !Generate() && Status().StartsWith("Generate stopped", StringComparison.Ordinal), Status());
            Check("two markings onto one row: both are named", Status().Contains(zqA1 + " (Wheel)") && Status().Contains(zqA2 + " (Turret)"), Status());
            Untouched("two markings onto one row", names5, stamp5, (zqA1, "Wheel"), (zqA2, "Turret"));
            // a manual Probe takes the file as the user's decision: neither marking lands on the shared row
            Call(w, "Probe");
            Check("two markings onto one row: a manual Probe does not give the row either role", RoleOf("Fused_Zq_B") == "Default", RoleOf("Fused_Zq_B"));

            // 6. the review's scenario C: an unmarked part renamed so the probe guesses a role ("propeller" contains "rope")
            Session(model, (after, "Wheel"));
            var names6 = Names(); string stamp6 = Stamp();
            string guessedNew = "Fused_Xp_propeller_9";
            WriteSource(Renamed(model, (guessed, guessedNew)));
            Check("guessed role: Generate stops", !Generate() && Status().StartsWith("Generate stopped", StringComparison.Ordinal), Status());
            Check("guessed role: the status names the new part and the role it would take", Regex.IsMatch(Status(), Regex.Escape(guessedNew) + @" \((Rigging|Rotor)\)"), Status());
            Untouched("guessed role", names6, stamp6, (after, "Wheel"), (guessed, "Default"));

            // A source rewrite after the read but before the swap leaves the previously accepted session intact.
            Session(Renamed(model, (after, before)), (before, "Wheel"));
            string stamp8 = Stamp();
            WriteSource(model);
            string propeller = "Fused_Xp_propeller_9"; bool rewrote = false;
            Application.LogCallback hook = (msg, st, ty) => { if (!rewrote && msg.Contains("markings carried over by group")) { rewrote = true; WriteSource(Renamed(model, (guessed, propeller))); } };
            Application.logMessageReceived += hook;
            bool gen8; try { gen8 = Generate(); } finally { Application.logMessageReceived -= hook; }
            Check("rewrite before the swap: the hook fired (the fixture migrates)", rewrote);
            Check("rewrite before the swap: Generate stops, saying the source changed again", !gen8 && Status().Contains("changed again"), Status());
            Check("rewrite before the swap: the guessed row is not in the session", PartNamed(propeller) == null && RoleOf(guessed) == "Default", string.Join(", ", Names()));
            Check("rewrite before the swap: the original part and Wheel marking are intact", PartNamed(after) == null && RoleOf(before) == "Wheel", RoleOf(before));
            Check("rewrite before the swap: the probe record is intact", Stamp() == stamp8);
            Check("rewrite before the swap: the next press judges the new file and stops on the rotor guess", !Generate() && Status().Contains(propeller + " (Rotor)"), Status());

            // The second read still redraws a migrated placement. A rewrite during that read may not publish it.
            var offset = new Vector3(0.25f, 0, 0);
            Session(Renamed(model, (after, before)), (before, "Wheel"));
            Place(before, offset);
            WriteSource(model);
            int beforeRedraw = Swaps();
            Check("placement migration: both stable reads complete and Generate runs", Generate() && Swaps() == beforeRedraw + 2, Status());
            var moved = PartNamed(after);
            Check("placement migration: the new name keeps the Wheel and its offset", moved != null && RoleOf(after) == "Wheel" &&
                (Vector3)moved.GetType().GetField("offset").GetValue(moved) == offset, Status());

            Session(Renamed(model, (after, before)), (before, "Wheel"));
            Place(before, offset);
            WriteSource(model);
            beforeRedraw = Swaps();
            int reads = 0;
            Application.LogCallback redrawHook = (msg, st, ty) =>
            {
                if (msg.StartsWith("[VehicleLab] probe (C#, in-process):", StringComparison.Ordinal) && ++reads == 2)
                    WriteSource(Renamed(model, (guessed, propeller)));
            };
            Application.logMessageReceived += redrawHook;
            bool genRedraw;
            try { genRedraw = Generate(); } finally { Application.logMessageReceived -= redrawHook; }
            Check("placement redraw rewrite: the second read ran", reads == 2);
            Check("placement redraw rewrite: Generate stops and no second swap occurs",
                !genRedraw && Status().Contains("changed again") && Swaps() == beforeRedraw + 1, Status());
            Check("placement redraw rewrite: the first accepted Wheel remains", RoleOf(after) == "Wheel" && PartNamed(propeller) == null, Status());

            // 9. review round 2, the fast path: a rigged source (the generator's skinned fixture) with a bone named so the probe
            // guesses Turret, which the user cleared to Default. A merely touched file must not stop every Generate.
            byte[] skinned = Edited(File.ReadAllBytes(Arg("-hafStaleSkinned")), true, ("Arm", "Turret_Arm"));
            Session(skinned);
            object[] Bones() => ((IEnumerable)Field("boneParts").GetValue(w)).Cast<object>().ToArray();
            object BoneNamed(string n) => Bones().FirstOrDefault(b => (string)b.GetType().GetField("name").GetValue(b) == n);
            string BoneRole(string n) { var b = BoneNamed(n); return b == null ? "(no such bone)" : b.GetType().GetField("role").GetValue(b).ToString(); }
            void MarkBone(string n, string role) { var b = BoneNamed(n); var f = b.GetType().GetField("role"); f.SetValue(b, Enum.Parse(f.FieldType, role)); }
            Check("rigged: the probe lists the bones and turns the fast path on", BoneNamed("Root") != null && (bool)Field("useSourceRig").GetValue(w), string.Join(", ", Bones().Select(b => b.GetType().GetField("name").GetValue(b))));
            Check("rigged: the probe guesses Turret_Arm as Turret", BoneRole("Turret_Arm") == "Turret", BoneRole("Turret_Arm"));
            MarkBone("Turret_Arm", "Default"); MarkBone("Root", "Wheel");
            bool gen9a = Generate();
            Check("rigged, unchanged: Generate runs", gen9a, Status());
            File.SetLastWriteTimeUtc(src, clock = clock.AddSeconds(10));
            Check("rigged, touched: Generate re-probes and runs (was: a stop on every Generate)", Generate() && Status().Contains("Re-probed first"), Status());
            Check("rigged, touched: the cleared bone stays Default", BoneRole("Turret_Arm") == "Default", BoneRole("Turret_Arm"));
            Check("rigged, touched: the Wheel bone stays Wheel", BoneRole("Root") == "Wheel", BoneRole("Root"));
            // review round 2: a mesh marking on the fast path is the session's too - its loss stops Generate as well
            var mesh9 = Names().First();
            Mark(mesh9, "Detail");
            var names9 = Names(); string stamp9 = Stamp();
            WriteSource(Renamed(skinned, (mesh9, "Zz_Renamed_Mesh")));
            Check("rigged: a lost MESH marking stops the fast-path Generate", !Generate() && Status().Contains(mesh9 + " (Detail)"), Status());
            Untouched("rigged, lost mesh marking", names9, stamp9, (mesh9, "Detail"));

            // A nested child inherits the nearest marked ancestor only. Its Wheel and Turret ancestors cannot
            // both count as carried when that child is the only survivor of their two markings.
            const string ancestor = "ReviewAncestor", nested = ancestor + "_Part_001", leaf = nested + "_Part_002";
            Session(Renamed(model, (pairA, ancestor), (pairB, nested)), (ancestor, "Wheel"), (nested, "Turret"));
            var nestedNames = Names(); string nestedStamp = Stamp();
            WriteSource(Renamed(model, (pairA, "ReviewReplacement"), (pairB, leaf)));
            Check("nested split: Generate stops and names the farther marking", !Generate() && Status().Contains(ancestor + " (Wheel)"), Status());
            Untouched("nested split", nestedNames, nestedStamp, (ancestor, "Wheel"), (nested, "Turret"));

            string blender = Arg("-hafStaleBlender");
            if (blender.Length > 0)
            {
                string obj = Path.Combine(dir, "preview.obj").Replace('\\', '/');
                const string previewDir = "Assets/StaleBlenderPreview";
                string fbx = Path.Combine(proj, previewDir, "preview_probe.fbx");
                byte[] Obj(string name, int width) => Encoding.UTF8.GetBytes(
                    $"o {name}\nv 0 0 0\nv {width} 0 0\nv 0 1 0\nv 0 0 1\nf 1 3 2\nf 1 2 4\nf 2 3 4\nf 1 4 3\n");
                Session(model);
                Field("findBlender").SetValue(w, new Func<string>(() => blender));
                Field("headlessDir").SetValue(w, previewDir);
                Field("srcFile").SetValue(w, obj);
                WriteFile(obj, Obj("PreviewHull", 1));
                Call(w, "Probe");
                string marked = Names().SingleOrDefault();
                Check("Blender probe: initial preview was written", marked != null && File.Exists(fbx), Status());
                var oldPreview = (GameObject)Field("inst").GetValue(w);
                Check("Blender probe: the preview was imported and instantiated", oldPreview != null);
                byte[] previousPreview = File.ReadAllBytes(fbx);
                Mark(marked, "Detail");
                Place(marked, Vector3.right);
                string oldStamp = Stamp();
                WriteFile(obj, Obj("PreviewReplacement", 3));
                Check("Blender probe: Generate stops when the role and placement would be lost",
                    !Generate() && Status().Contains(marked + " (Detail + placement)"), Status());
                Untouched("Blender probe", new[] { marked }, oldStamp, (marked, "Detail"));
                var placed = PartNamed(marked);
                Check("Blender probe: refusal preserves the marked placement",
                    placed != null && (Vector3)placed.GetType().GetField("offset").GetValue(placed) == Vector3.right);
                Check("Blender probe: refusal preserves the imported preview file", previousPreview.SequenceEqual(File.ReadAllBytes(fbx)));
                Check("Blender probe: refusal preserves the turntable instance", oldPreview != null && ReferenceEquals(oldPreview, Field("inst").GetValue(w)));
                Call(w, "Probe");
                Check("Blender probe: manually accepted probe publishes the new preview", Names().Single() != marked && !previousPreview.SequenceEqual(File.ReadAllBytes(fbx)), Status());
                Check("Blender probe: staged files were cleaned up", Directory.GetFiles(Path.Combine(proj, previewDir), ".haf-probe-*").Length == 0);
            }

            // 7. a recipe-loaded session is not a probe: its parts run as saved, a touched source is not re-probed
            Session(model, (after, "Wheel"));
            string recipe = Path.Combine(dir, "boat.json");
            File.WriteAllText(recipe, JsonUtility.ToJson(Call(w, "BuildRecipe")));
            Call(w, "LoadRecipeFromPath", recipe);
            Check("recipe load clears the probe record", Stamp() == "");
            File.SetLastWriteTimeUtc(src, clock.AddSeconds(30));
            Check("recipe session: a touched source generates as saved, no re-probe", Generate() && !Status().Contains("Re-probed"), Status());
            Call(w, "Probe");
            Check("re-Probe records the source again", Stamp().StartsWith("Raw model\t", StringComparison.Ordinal));
        }
        catch (Exception ex) { failed++; log.AppendLine("FAIL " + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex)); }
        finally { if (w != null) { try { Call(w, "DestroyPreview"); } catch { } UnityEngine.Object.DestroyImmediate(w); } }
        log.AppendLine("TOTAL failed " + failed);
        string output = Arg("-hafStaleOutput");
        if (output != "") File.WriteAllText(output, log.ToString());
        Debug.Log(log.ToString()); EditorApplication.Exit(failed == 0 ? 0 : 1);
    }
}
