// VehicleRigJobs.cs - the vehicle_rig.py JOBS of the Vehicle Lab's saved recipes (tools/vehiclerig_drill.sh, step (a) of the
// vehicle_rig port, 2026-10-14): each the argument vector a Generate hands Blender for that recipe, built by the SAME
// builder the window uses (editor/VehicleRigArgs.cs: VehicleRigArgLine.RigArgv on VehicleRigArgLine.RigArgsFromRecipe),
// with the names files and the placement file written under the work folder as the window writes them under its
// preview folder - so the drill runs the script on exactly what a Generate runs it on. The Lab's gate rows hold the
// window's fields to this reader on every saved recipe (VehicleLabWindow.GenerateHeadless).
//
//   VehicleRigJobs.exe <recipes dir> <work dir> [--max-mb N] [--all]
//
// One TAB-separated line per recipe:
//   JOB <key> <argv[0]> <argv[1]> ... <argv[k]>      the script's arguments after `--`; argv[2] (the output GLB) and argv[3]
//                                                     (the preview FBX) are the dump's to replace: the key's own files
//   FAST <key> <argv...>                              the same for a recipe on the source-skeleton fast path (rigfast)
//   LEFT <key> <reason>                               a recipe over the size limit (FULL=1 / --all takes them)
//   UNAVAILABLE <key> <reason>                        the source model is not on this machine (exit 3)
//   INVALID <key> <reason>                            the recipe cannot be read (exit 1)
// A key is the recipe file's stem; a name may hold TAB-free characters only (a TAB in a part name is INVALID).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

static class VehicleRigJobs
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length < 2) { Console.WriteLine("usage: VehicleRigJobs.exe <recipes dir> <work dir> [--max-mb N] [--all]"); return 2; }
        string recipesDir = args[0], work = args[1]; double maxMb = 40; bool all = false;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--all") all = true;
            else if (args[i] == "--max-mb" && i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out maxMb)) i++;
            else { Console.WriteLine("usage: VehicleRigJobs.exe <recipes dir> <work dir> [--max-mb N] [--all]"); return 2; }
        }
        if (!Directory.Exists(recipesDir)) { Console.WriteLine("INVALID\t-\tno recipes directory at " + recipesDir); return 1; }
        Directory.CreateDirectory(work);
        int unavailable = 0, invalid = 0;
        foreach (var file in Directory.GetFiles(recipesDir, "*.json").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            string key = Path.GetFileNameWithoutExtension(file);
            try
            {
                var r = VehicleRigArgLine.RigArgsFromRecipe(File.ReadAllText(file));
                var a = r.args;
                if (string.IsNullOrWhiteSpace(a.srcFile)) { Console.WriteLine($"INVALID\t{key}\tno source model"); invalid++; continue; }
                a.srcFile = a.srcFile.Trim().Replace('\\', '/');
                if (!File.Exists(a.srcFile)) { Console.WriteLine($"UNAVAILABLE\t{key}\tsource missing: {a.srcFile}"); unavailable++; continue; }
                if (a.srcFile2.Length > 0 && !File.Exists(a.srcFile2)) { Console.WriteLine($"UNAVAILABLE\t{key}\tsecond model missing: {a.srcFile2}"); unavailable++; continue; }
                double mb = new FileInfo(a.srcFile).Length / 1048576.0 + (a.srcFile2.Length > 0 ? new FileInfo(a.srcFile2).Length / 1048576.0 : 0);
                if (!all && mb > maxMb) { Console.WriteLine($"LEFT\t{key}\tover {maxMb:0} MB ({mb:0.0} MB; FULL=1 takes it)"); continue; }
                // the names files, as the window writes them (one name per line, the recipe's order), under <work>/<key>/
                string dir = Path.Combine(work, key); Directory.CreateDirectory(dir);
                foreach (var role in VehicleRigArgLine.NamesRoles)
                {
                    var names = VehicleRigArgLine.NamesOf(r, role);
                    if (names.Any(n => n.IndexOf('\t') >= 0 || n.IndexOf('\n') >= 0)) throw new InvalidDataException("a part name holds a tab or a newline");
                    string path = Path.Combine(dir, key + "_" + role + ".txt").Replace('\\', '/');
                    if (role == "default" && a.defaultReducePct <= 0f) { if (File.Exists(path)) File.Delete(path); a.namesFiles[role] = path; continue; }   // the window writes it only when the dial asks
                    File.WriteAllLines(path, names, new UTF8Encoding(false));
                    a.namesFiles[role] = path;
                }
                var placement = VehicleRigArgLine.PlacementLines(r);
                string tx = Path.Combine(dir, key + "_parttx.txt").Replace('\\', '/');
                if (placement.Length > 0) { File.WriteAllLines(tx, placement, new UTF8Encoding(false)); a.partTxFile = tx; }
                else { if (File.Exists(tx)) File.Delete(tx); a.partTxFile = null; }
                a.outGlb = Path.Combine(dir, key + "_Spin.glb").Replace('\\', '/'); a.previewFbx = "";   // the dump's to replace; no preview FBX in the drill
                var argv = VehicleRigArgLine.RigArgv(a).Select(x => x.value).ToList();
                if (argv.Any(v => v.IndexOf('\t') >= 0 || v.IndexOf('\n') >= 0)) throw new InvalidDataException("an argument holds a tab or a newline");
                Console.WriteLine((a.fast ? "FAST\t" : "JOB\t") + key + "\t" + string.Join("\t", argv));
            }
            catch (Exception e) { Console.WriteLine($"INVALID\t{key}\t{e.GetType().Name}: {e.Message}"); invalid++; }
        }
        return invalid > 0 ? 1 : unavailable > 0 ? 3 : 0;
    }
}
