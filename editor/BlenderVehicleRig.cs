// BlenderVehicleRig.cs - vehicle_rig.py's rig path in C#, stage by stage (step (b) of the vehicle_rig port, 2026-10-14;
// docs/Review-Backlog.md item (5)), held to Blender by tools/vehiclerig_drill.sh: every row of every stage's dump. Stage 1 is
// the scene the script's reduction tiers start from: the import (BlenderRigAnim.ImportScene, the glTF importer's scene as
// the rig_anim port models it), the importer's bone-shape placeholders purged by signature, the second-model merge, the
// per-source brightness, the flatten (every mesh a root with its world transform, its skinning and modifiers cleared, the
// helpers dropped), the single-mesh loose split, the per-part placements, the ignored parts deleted, the orientation, and
// transform_apply(location, rotation, scale) into every mesh. What is named as Blender's here (Fallback) the Lab's Generate
// runs through Blender as before: the source-skeleton fast path (rigfast) and a .fbx/.obj/.blend source (until the native
// readers); every other decline is a FAIL in the drill. Rules read from the script and from Blender's sources
// (5.1): `o.matrix_world = m` writes the properties through BKE_object_apply_mat4 (VehicleProbe.ApplyMat4's round trip),
// the next view_layer.update() rebuilds the world from them; `o.parent = None` keeps the local transform (the script copies
// the world matrix back); transform_apply = BKE_mesh_transform with the object's whole matrix (math::transform_point:
// ((x c0 + y c1) + z c2) + loc in float32, and NOTHING within 1e-6 of the identity - measured on the bell, whose root carries a 1.0000001 scale) and the
// properties reset; a multi-user mesh is copied first (`o.data = o.data.copy()`: the mesh pool's next free name).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public static partial class BlenderVehicleRig
{
    public sealed class Args
    {
        public string Mode = "rig", Input = "", OutGlb = "", PreviewFbx = "";
        public double[] ModelRot = { 0.0, 0.0, 0.0 };
        public Dictionary<string, List<string>> Names = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // by role key (the jobs writer's)
        public Dictionary<string, double> Reduce = new Dictionary<string, double>(StringComparer.Ordinal);   // the reduce dial per role key, min(95, max(0, float)): 0 when absent
        public HashSet<string> Marked = new HashSet<string>(StringComparer.Ordinal);   // every role list's names (the artifact purge spares them)
        public HashSet<string> Ignore = new HashSet<string>(StringComparer.Ordinal);
        public string Merge2;                       // the merge2= text, or null
        public double Bright1 = 1.0, Bright2 = 1.0;
        public List<string> PartTx;                 // the parttx= lines, or null
        public bool TracksStatic;                   // argv[16] "1": the tread loops rigid, no link bones (the line at argument time)
        public readonly List<string> Logs = new List<string>();   // the VEHICLE lines the rig path prints at argument time (a bad orientation, static tracks), after the merge
        public string Problem;                      // the script's own exit on its arguments before the import (bright=, flagfold=: a VEHICLE ERROR), or null
        public string PreDie;                       // an exception before the import (int(float(inf)) of a flagfold frame count), or null
        public string TagError;                     // the first malformed reduce tag's VEHICLE ERROR after the merge (defaultreduce, shroudreduce, gunreduce, turretreduce, windowreduce), or null
        public string NamesDie;                     // a positional names file the script cannot open: the FileNotFoundError it dies of after the merge, or null
        public string Unsupported;                  // an argument the C# does not read as Python does (a names file that is not UTF-8): left to Blender by name
        public string FoldDie;                      // a sail-fold or oar dial (argv 72, 73, 75, 56-59, read unconditionally) Python cannot parse: the exception, or null
        public string DialsDie;                     // a dial (argv[7..25]) Python cannot parse: the exception it dies of before the orientation, or null
        public string AxisArg = "";                 // argv[6].upper(): the wheels' axle override (X, Y, Z) or anything else = AUTO (the thinnest extent)
        public string TailAxisArg = "AUTO";         // argv[28].upper() when not blank: the tail rotor's own axle override (X, Y, Z), else AUTO
        public double TailYaw, TailPitch;           // float(argv[29]), float(argv[30]) or 0: the tail axle's trim in degrees (yaw about the vertical, then pitch)
        public bool RockOn;                         // the WAVE ROCK is on: a RootHull bone under Root carries everything that would skin to Root
        public double GunPivot = 0.5;               // min(1, max(0, float(argv[34]))) or 0.5: where along the tube (breech 0 .. muzzle 1) the Gun bone's head sits
        public double RecoilDist;                   // min(1, max(0, float(argv[38]))) or 0: recoil on (the RecoilArm + Barrel split) when above 0
        public bool SailFold;                       // argv[71].strip() == "1": the Sail bone gets the SailF1..F3 fold chain, the canvas skins by height band
        public bool FlagFoldOn;                     // a flagfold= tag is present: the Flag bone hinges at the flag's top (else it strikes below the keel)
        public double FlagFoldDeg;                  // max(-175, min(175, float(deg))) or 0 when blank (a NaN clamps to 175)
        public System.Numerics.BigInteger FlagFoldFrames = 12;   // max(1, int(float(frames))) or 12 when blank - Python's exact int
        public double OarRoll;                      // float(argv[47]) or 0: the oars' blade roll in degrees (applied above 0.01)
        public double OarPivot = 30.0;              // min(95, max(0, float(argv[58]))) or 30: the oarlock along the oar's beam extent, percent
        public double OarLength = 100.0;            // min(300, max(25, float(argv[59]))) or 100: the oar stretched about its oarlock, percent
        public System.Numerics.BigInteger Frames = 15;   // max(2, int(argv[7])): the Spin clip's frames - Python's exact int
        public double Degrees;                      // float(argv[8]): the largest wheel's turn over the clip
        public double RoadSpeedMul = 1.0, IdlerSpeedMul = 1.0;   // max(0.1, min(4.0, float(argv[13] / [14]))) or 1
        public double RockDeg, RockPitchDeg = 2.4, RockHeading, RockPitchPhase = 90.0;   // argv 17, 21 (max(0, min(45, ...))), 20, 24
        public System.Numerics.BigInteger RockFrames, RockPitchCycles = 1, RockRollCycles = 1;   // argv 18 (or the frames), 22, 25
        public string RockAxisArg = "AUTO";         // argv[19].strip().upper() when not blank
    }

    static readonly (string role, int index)[] NamesArgv =
    {
        ("wheels", 4), ("turrets", 5), ("ignore", 9), ("tracks", 10), ("guns", 11), ("rotors", 26), ("tailrotors", 27), ("trails", 31), ("muzzles", 36),
        ("cradles", 37), ("oars", 42), ("sails", 48), ("rigging", 49), ("structure", 51), ("bodies", 53), ("flags", 55), ("rudders", 60), ("preserve", 63),
        ("flip", 66), ("detail", 69),
    };

    /// <summary>namelist(arg): "@path" = `open(path, encoding="utf-8")` iterated by line (universal newlines: \n, \r, \r\n), each line
    /// str.strip()ped, the empty ones dropped - a UTF-8 BOM is NOT a newline or a space, so it stays on the first name; else the
    /// ';'-separated names kept as written, the blank ones dropped. Throws what open() would raise, as .NET's exceptions.</summary>
    static List<string> NameList(string arg)
    {
        if (arg.StartsWith("@", StringComparison.Ordinal))
        {
            var bytes = System.IO.File.ReadAllBytes(arg.Substring(1));
            string text = new System.Text.UTF8Encoding(false, true).GetString(bytes);   // strict: invalid UTF-8 throws; a BOM decodes to U+FEFF
            return System.Text.RegularExpressions.Regex.Split(text, "\r\n|\r|\n").Select(VehicleProbe.Input.PyStrip).Where(l => l.Length > 0).ToList();
        }
        return arg.Split(';').Where(PyNonBlank).ToList();
    }

    static bool NamesFileException(Exception e) => e is System.IO.IOException || e is ArgumentException || e is UnauthorizedAccessException;

    // The same open()/decode errors apply to positional names and names inside reduce tags. The callers decide
    // whether Python lets the exception escape, prints it in a tagged-argument error, or needs a named fallback.
    static string NamesFileError(Exception e, string arg, out string unsupported)
    {
        unsupported = null; string path = arg.Substring(1);
        if (e is UnauthorizedAccessException) return "PermissionError: [Errno 13] Permission denied: " + PyStrRepr(path);
        if (e is System.Text.DecoderFallbackException)
            unsupported = "a names file that is not UTF-8 (Python's UnicodeDecodeError is not modelled)";
        else if (e is ArgumentException && path.Length > 0)
            unsupported = "a names file path .NET refuses (Python's OSError is not modelled)";
        else return "FileNotFoundError: " + PyFileNotFound(path);
        return null;
    }

    /// <summary>The script's reading of its argv (argv[0] = the mode onward): the role lists (@file), the orientation, the tagged
    /// arguments; the exits it takes on a malformed tagged argument.</summary>
    public static Args ParseArgs(IList<string> argv)
    {
        var a = new Args { Mode = argv[0], Input = argv[1], OutGlb = argv.Count > 2 ? argv[2] : "", PreviewFbx = argv.Count > 3 ? argv[3] : "" };
        string At(int i) => i < argv.Count ? argv[i] : "";
        int namesFailureIndex = int.MaxValue;   // positional names and reduce dials are interleaved in the script
        int unsupportedAt = int.MaxValue;       // a names file the C# leaves to Blender: only when nothing earlier in argv died
        foreach (var (role, index) in NamesArgv)
        {
            List<string> list;
            try
            {
                list = index < argv.Count && PyNonBlank(At(index)) ? NameList(At(index)) : new List<string>();
                if (index == 4 || index == 5) list = NameList(At(index));   // wheel_names, turret_names: read unconditionally
            }
            catch (Exception e) when (NamesFileException(e))
            {
                // open() of the path: a missing file or an empty path is FileNotFoundError, a directory (or a denied file) PermissionError;
                // a file that is not UTF-8 is Python's UnicodeDecodeError, whose text (the byte, its offset in the reader's chunk) is not
                // modelled - the job is left to Blender by name
                list = new List<string>();
                string death = NamesFileError(e, At(index), out string unsupported);
                if (unsupported != null && a.Unsupported == null) { a.Unsupported = unsupported; unsupportedAt = index; }
                if (death != null && index < namesFailureIndex) { namesFailureIndex = index; a.NamesDie = death; }
            }   // namelist() opens it in the rig path: the script dies there
            a.Names[role] = list;
        }
        a.Ignore = new HashSet<string>(a.Names["ignore"], StringComparer.Ordinal);
        // the positional reduce dials, read among the names (vehicle_rig.py 871-922): min(95, max(0, float(x))) when present and
        // non-blank, else 0; float() of a non-number dies there, like a missing names file
        // the other dials read among the names (vehicle_rig.py 802-860), in argv order: float() of 29, 30, 32, 34, 35, 38, 43, 44, 47 and
        // int(float()) of 33, 39, 40, 45 when present and non-blank - a value Python cannot parse dies where it is read
        foreach (var (idx, integral) in new[] { (29, false), (30, false), (32, false), (33, true), (34, false), (35, false), (38, false), (39, true), (40, true), (43, false), (44, false), (45, true), (47, false) })
        {
            if (!(argv.Count > idx && PyNonBlank(At(idx)))) continue;
            string death = null;
            if (!TryPyFloat(At(idx), out double dv)) death = "ValueError: could not convert string to float: " + PyStrRepr(At(idx));
            else if (integral && (double.IsNaN(dv) || double.IsInfinity(dv))) death = double.IsNaN(dv) ? "ValueError: cannot convert float NaN to integer" : "OverflowError: cannot convert float infinity to integer";
            if (death != null) { if (idx < namesFailureIndex) { namesFailureIndex = idx; a.NamesDie = death; } continue; }
            if (idx == 29) a.TailYaw = dv;
            if (idx == 30) a.TailPitch = dv;
            if (idx == 47) a.OarRoll = dv;
            if (idx == 34) a.GunPivot = PyMin(1.0, PyMax(0.0, dv));
            if (idx == 38) a.RecoilDist = PyMin(1.0, PyMax(0.0, dv));
        }
        foreach (var (role, idx) in new[] { ("rigging", 50), ("structure", 52), ("bodies", 54), ("oars", 61), ("sails", 62), ("rudders", 64), ("wheels", 65), ("flip", 67), ("preserve", 68), ("detail", 70) })
        {
            a.Reduce[role] = 0.0;
            if (!(argv.Count > idx && PyNonBlank(At(idx)))) continue;
            if (!TryPyFloat(At(idx), out double v)) { if (idx < namesFailureIndex) { namesFailureIndex = idx; a.NamesDie = "ValueError: could not convert string to float: " + PyStrRepr(At(idx)); } continue; }
            a.Reduce[role] = PyClamp95(v);
        }
        a.TracksStatic = argv.Count > 16 && At(16).Trim() == "1";
        // the reduce tags in the script's order - defaultreduce, shroudreduce, gunreduce, turretreduce, windowreduce -, the names before the LAST
        // '|', the percent after it; the first malformed one is the script's VEHICLE ERROR after the merge (its exit)
        foreach (var tag in new[] { "defaultreduce", "shroudreduce", "gunreduce", "turretreduce", "windowreduce" })
        {
            string role = tag == "shroudreduce" ? "shroud" : tag == "windowreduce" ? "window" : tag == "defaultreduce" ? "default" : null;
            if (role != null) a.Names[role] = new List<string>();
            // Python never opens a later tagged file after a positional error or the first malformed tag.
            if (a.NamesDie != null || a.Unsupported != null || a.TagError != null) continue;
            string arg = argv.FirstOrDefault(x => x.StartsWith(tag + "=", StringComparison.Ordinal));
            if (arg == null) continue;
            string spec = arg.Substring(tag.Length + 1); int bar = spec.LastIndexOf('|');
            string namesText = bar >= 0 ? spec.Substring(0, bar) : null, err = null;
            try
            {
                if (tag == "gunreduce" || tag == "turretreduce")   // percent-only: the parts travel positionally (argv[11], argv[5])
                {
                    double pct = VehicleProbe.Input.PyFloat(spec);
                    if (double.IsNaN(pct) || double.IsInfinity(pct)) err = "not a finite percent: " + PyStrRepr(arg);
                    else a.Reduce[tag == "gunreduce" ? "guns" : "turrets"] = PyClamp95(pct);
                }
                else if (tag == "windowreduce")
                {
                    if (bar < 0) err = "no '|' between the names and the percent: " + PyStrRepr(arg);
                    else
                    {
                        double pct = VehicleProbe.Input.PyFloat(spec.Substring(bar + 1));
                        if (double.IsNaN(pct) || double.IsInfinity(pct)) err = "not a finite percent: " + PyStrRepr(arg);
                        else { a.Reduce[role] = PyClamp95(pct); if (PyNonBlank(namesText)) a.Names[role] = NameList(namesText); }
                    }
                }
                else
                {
                    if (bar < 0) err = "not enough values to unpack (expected 2, got 1)";   // `names, pct = spec.rsplit("|", 1)`
                    else
                    {
                        if (PyNonBlank(namesText)) a.Names[role] = NameList(namesText);
                        a.Reduce[role] = PyClamp95(VehicleProbe.Input.PyFloat(spec.Substring(bar + 1)));   // min(95, max(0, float(pct))): inf and nan pass here
                    }
                }
            }
            // float() errors quote the offending percent with Python's repr(), including its quote choice and Unicode
            // escapes. The shared probe parser's legacy error formatter is not the script's exact representation.
            catch (FormatException) { err = "could not convert string to float: " + PyStrRepr(tag == "gunreduce" || tag == "turretreduce" ? spec : spec.Substring(bar + 1)); }
            catch (Exception e) when (NamesFileException(e))
            {
                string death = NamesFileError(e, namesText, out string unsupported);
                if (unsupported != null) a.Unsupported = unsupported;
                else err = death.Substring(death.IndexOf(": ", StringComparison.Ordinal) + 2);   // str(exception), without its type
            }
            if (err != null && a.TagError == null)
                a.TagError = tag == "gunreduce" || tag == "turretreduce" || tag == "windowreduce" ? "VEHICLE ERROR: malformed " + tag + " argument: " + err
                           : "VEHICLE ERROR: malformed " + tag + " argument: " + arg + " (" + err + ")";
        }
        // _role_marked: every role list but ignore (the tagged shroud/window lists too); static tracks are emptied before it is built
        foreach (var kv in a.Names) if (kv.Key != "ignore" && kv.Key != "default" && !(kv.Key == "tracks" && a.TracksStatic)) a.Marked.UnionWith(kv.Value);
        string m2 = argv.FirstOrDefault(x => x.StartsWith("merge2=", StringComparison.Ordinal));
        if (m2 != null) a.Merge2 = m2.Substring(7);
        // the exits before the import, in the script's order: bright=, then flagfold=
        string br = argv.FirstOrDefault(x => x.StartsWith("bright=", StringComparison.Ordinal));
        if (br != null)
        {
            var f = br.Substring(7).Split('|');
            if (f.Length != 2 || (PyNonBlank(f[0]) && !TryPyFloat(f[0], out a.Bright1)) || (PyNonBlank(f[1]) && !TryPyFloat(f[1], out a.Bright2)))
            { a.Logs.Add("VEHICLE ERROR: malformed bright argument: " + br); a.Problem = "malformed bright argument"; return a; }
        }
        string ff = argv.FirstOrDefault(x => x.StartsWith("flagfold=", StringComparison.Ordinal));
        if (ff != null)
        {
            // `deg, frames = spec.split("|")`, float(deg) when non-blank, int(float(frames)) when non-blank: a ValueError is the exit, int() of
            // an infinite float an OverflowError the script does not catch (it dies before the import)
            var f = ff.Substring(9).Split('|'); bool ok = f.Length == 2;
            if (ok && PyNonBlank(f[0])) ok = TryPyFloat(f[0], out _);
            if (ok && PyNonBlank(f[1]))
            {
                ok = TryPyFloat(f[1], out double fr) && !double.IsNaN(fr);
                if (ok && double.IsInfinity(fr)) { a.PreDie = "OverflowError: cannot convert float infinity to integer"; return a; }
            }
            if (!ok) { a.Logs.Add("VEHICLE ERROR: malformed flagfold argument: " + ff); a.Problem = "malformed flagfold argument"; return a; }
            a.FlagFoldOn = true;
            if (PyNonBlank(f[0]) && TryPyFloat(f[0], out double fd)) a.FlagFoldDeg = PyMax(-175.0, PyMin(175.0, fd));
            if (PyNonBlank(f[1]) && TryPyFloat(f[1], out double ffr)) { var tr = new System.Numerics.BigInteger(Math.Truncate(ffr)); a.FlagFoldFrames = tr < 1 ? 1 : tr; }
        }
        string tx = argv.FirstOrDefault(x => x.StartsWith("parttx=", StringComparison.Ordinal));
        if (tx != null)
        {
            string spec = tx.Substring(7);
            a.PartTx = spec.StartsWith("@", StringComparison.Ordinal) ? System.IO.File.ReadAllLines(spec.Substring(1)).ToList() : spec.Split(';').ToList();
        }
        // the dials the rig path reads before the orientation: int(argv[7]) and float(argv[8]) unconditionally, the rest when present and
        // non-blank; a value Python cannot parse - or int(float()) of inf or nan - is an uncaught exception: the script dies there
        if (!TryPyInt(At(7))) a.DialsDie = "ValueError: invalid literal for int() with base 10: " + PyStrRepr(At(7));
        else if (!TryPyFloat(At(8), out _)) a.DialsDie = "ValueError: could not convert string to float: " + PyStrRepr(At(8));
        else
            foreach (var (idx, integral) in new[] { (12, true), (13, false), (14, false), (15, false), (17, false), (18, true), (20, false), (21, false), (22, true), (25, true), (24, false) })
            {
                if (!(argv.Count > idx && PyNonBlank(At(idx)))) continue;
                if (!TryPyFloat(At(idx), out double v)) { a.DialsDie = "ValueError: could not convert string to float: " + PyStrRepr(At(idx)); break; }
                if (integral && (double.IsNaN(v) || double.IsInfinity(v))) { a.DialsDie = (double.IsNaN(v) ? "ValueError: cannot convert float NaN to integer" : "OverflowError: cannot convert float infinity to integer"); break; }
            }
        // the stage 3 readings (when the dials parse): axis_arg = argv[6].upper(); rock_on (vehicle_rig.py 1085-1107) - rock_deg =
        // max(0, min(45, float(argv[17]))) or 0, rock_frames = max(0, int(float(argv[18]))) or 0, the pitch max(0, min(45,
        // float(argv[21]))) or 2.4 when blank; a frame count of 0 or less takes `frames` (max(2, int(argv[7])) >= 2) once either
        // amplitude is positive; rock_on = (rock_deg > 0 or pitch > 0) and rock_frames >= 2. Python's min/max keep their FIRST
        // argument unless the second compares past it, so a NaN amplitude clamps to 45.
        if (a.Unsupported != null && unsupportedAt > namesFailureIndex) a.Unsupported = null;   // Python died earlier, at a name or dial
        a.AxisArg = PyUpper(At(6));
        a.TailAxisArg = argv.Count > 28 && PyNonBlank(At(28)) ? PyUpper(At(28)) : "AUTO";
        a.SailFold = argv.Count > 71 && VehicleProbe.Input.PyStrip(At(71)) == "1";
        // the sail-fold and oar dials, read unconditionally after the reduce tags and before the dials (vehicle_rig.py 1010-1017):
        // int(float(argv[72])), float(argv[73]), float(argv[75]), float(argv[56..59]) when present and non-blank
        foreach (var (idx, integral) in new[] { (72, true), (73, false), (75, false), (56, false), (57, false), (58, false), (59, false) })
        {
            if (!(argv.Count > idx && PyNonBlank(At(idx)))) continue;
            if (!TryPyFloat(At(idx), out double v)) { a.FoldDie = "ValueError: could not convert string to float: " + PyStrRepr(At(idx)); break; }
            if (integral && (double.IsNaN(v) || double.IsInfinity(v))) { a.FoldDie = double.IsNaN(v) ? "ValueError: cannot convert float NaN to integer" : "OverflowError: cannot convert float infinity to integer"; break; }
            if (idx == 58) a.OarPivot = PyMin(95.0, PyMax(0.0, v));
            if (idx == 59) a.OarLength = PyMin(300.0, PyMax(25.0, v));
        }
        if (a.DialsDie == null)
        {
            double Amp(int i, double dflt) => argv.Count > i && PyNonBlank(At(i)) && TryPyFloat(At(i), out double v) ? PyMax(0.0, PyMin(45.0, v)) : dflt;
            double rockDeg = Amp(17, 0.0), pitch = Amp(21, 2.4);
            double rockFrames = 0.0;
            if (argv.Count > 18 && PyNonBlank(At(18)) && TryPyFloat(At(18), out double rf)) rockFrames = Math.Max(0.0, Math.Truncate(rf));
            bool any = rockDeg > 0.0 || pitch > 0.0;
            if (any && rockFrames <= 0.0) rockFrames = 2.0;   // `frames`, at least 2
            a.RockOn = any && rockFrames >= 2.0;
            // the values stage 4 reads (vehicle_rig.py 1068-1121), each parsed as above
            string fs = VehicleProbe.Input.PyNumberAscii(At(7)).Replace("_", "");
            a.Frames = System.Numerics.BigInteger.Max(2, System.Numerics.BigInteger.Parse(fs.TrimStart('+'), CultureInfo.InvariantCulture));
            a.Degrees = VehicleProbe.Input.PyFloat(At(8));
            double Clamp(int i, double lo, double hi, double dflt) => argv.Count > i && PyNonBlank(At(i)) && TryPyFloat(At(i), out double v) ? PyMax(lo, PyMin(hi, v)) : dflt;
            a.RoadSpeedMul = Clamp(13, 0.1, 4.0, 1.0); a.IdlerSpeedMul = Clamp(14, 0.1, 4.0, 1.0);
            a.RockDeg = rockDeg; a.RockPitchDeg = pitch;
            System.Numerics.BigInteger IntOf(int i, System.Numerics.BigInteger dflt) => argv.Count > i && PyNonBlank(At(i)) && TryPyFloat(At(i), out double v) ? new System.Numerics.BigInteger(Math.Truncate(v)) : dflt;
            a.RockFrames = System.Numerics.BigInteger.Max(0, IntOf(18, 0));
            if (a.RockDeg > 0.0 && a.RockFrames <= 0) a.RockFrames = a.Frames;
            a.RockPitchCycles = System.Numerics.BigInteger.Max(1, IntOf(22, 1)); a.RockRollCycles = System.Numerics.BigInteger.Max(1, IntOf(25, 1));
            if (a.RockPitchDeg > 0.0 && a.RockFrames <= 0) a.RockFrames = a.Frames;
            a.RockOn = (a.RockDeg > 0.0 || a.RockPitchDeg > 0.0) && a.RockFrames >= 2;
            a.RockHeading = argv.Count > 20 && PyNonBlank(At(20)) && TryPyFloat(At(20), out double hd) ? hd : 0.0;
            a.RockPitchPhase = argv.Count > 24 && PyNonBlank(At(24)) && TryPyFloat(At(24), out double ph) ? ph : 90.0;
            a.RockAxisArg = argv.Count > 19 && PyNonBlank(At(19)) ? PyUpper(VehicleProbe.Input.PyStrip(At(19))) : "AUTO";
        }
        // MODEL ORIENTATION (argv[23] "rx,ry,rz" degrees): [float(v) for v in split(",")] padded with zeros and cut to three - two
        // values are read, a fourth is dropped; only a value float() refuses is the WARN
        if (argv.Count > 23 && PyNonBlank(At(23)))
        {
            var parts = At(23).Split(','); var vals = new double[3]; bool ok = true;
            for (int i = 0; ok && i < parts.Length; i++) { ok = TryPyFloat(parts[i], out double v); if (ok && i < 3) vals[i] = v; }
            if (ok) a.ModelRot = vals;
            else a.Logs.Add("VEHICLE WARN: bad orientation arg '" + At(23) + "' — ignoring");
        }
        if (a.TracksStatic && a.Names["tracks"].Count > 0) a.Logs.Add("VEHICLE tracks STATIC (isolation): " + a.Names["tracks"].Count + " tread part(s) rigged rigid to Root, no link bones");
        return a;
    }

    static bool TryPyFloat(string s, out double v)
    {
        try { v = VehicleProbe.Input.PyFloat(s); return true; } catch (FormatException) { v = 0; return false; }
    }

    /// <summary>min(95.0, max(0.0, v)) as Python evaluates it: max keeps 0.0 unless v is greater (so nan gives 0), min keeps 95 unless v is less.</summary>
    /// <summary>Python's max(a, b) / min(a, b) on two floats: the first argument unless the second compares past it.</summary>
    static double PyMax(double a, double b) => b > a ? b : a;
    static double PyMin(double a, double b) => b < a ? b : a;
    /// <summary>str.upper() for the axle override; only "X", "Y" and "Z" are told apart, and only x, y and z upper-case to those.</summary>
    static string PyUpper(string s) => s.ToUpperInvariant();
    static double PyClamp95(double v) { double m = v > 0.0 ? v : 0.0; return m < 95.0 ? m : 95.0; }

    /// <summary>int(str): whitespace around, a sign, digits with single underscores between them - nothing else.</summary>
    internal static bool TryPyInt(string t)
    {
        // int(text): the same Unicode digit and space transform as float(), one optional sign, digits with underscores only between them
        string s = VehicleProbe.Input.PyNumberAscii(t);
        if (s == null) return false;
        if (s.StartsWith("+", StringComparison.Ordinal) || s.StartsWith("-", StringComparison.Ordinal)) s = s.Substring(1);
        return s.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(s, "^[0-9]+(_[0-9]+)*$");
    }

    /// <summary>`argv[i].strip()` is non-empty (str.strip(): every str.isspace() character).</summary>
    static bool PyNonBlank(string t) => VehicleProbe.Input.PyStrip(t).Length > 0;

    /// <summary>Python's "%.Nf": the exact binary value rounded half to even, a negative zero kept.</summary>
    static string Py(double v, int digits) => BlenderRigAnim.PyFixed(v, digits);

    /// <summary>str(FileNotFoundError) for open(path): "[Errno 2] No such file or directory: 'path'".</summary>
    static string PyFileNotFound(string path) => "[Errno 2] No such file or directory: " + PyStrRepr(path);

    /// <summary>is_icosphere_artifact: the importer's bone-shape placeholder by signature - the name, an icosphere's vertex count,
    /// a sphere's near-equal local extents (bound_box: the mesh's float32 position extremes).</summary>
    internal static bool IsIcosphereArtifact(string name, BlenderRigAnim.MeshData d)
    {
        if (!(name.StartsWith("Icosphere", StringComparison.Ordinal) || name.StartsWith("B_Icosphere", StringComparison.Ordinal))) return false;
        if (d.Vertices != 12 && d.Vertices != 42 && d.Vertices != 162 && d.Vertices != 642) return false;
        LocalBox(d, out var mn, out var mx);
        float ex = mx[0] - mn[0], ey = mx[1] - mn[1], ez = mx[2] - mn[2];
        float max = Math.Max(ex, Math.Max(ey, ez)), min = Math.Min(ex, Math.Min(ey, ez));
        return max > 0 && min > 0.8f * max;
    }

    static void LocalBox(BlenderRigAnim.MeshData d, out float[] mn, out float[] mx)
    {
        mn = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity }; mx = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        for (int v = 0; v < d.Vertices; v++) for (int k = 0; k < 3; k++) { float x = d.Positions[3 * v + k]; if (x < mn[k]) mn[k] = x; if (x > mx[k]) mx[k] = x; }
        if (d.Vertices == 0) { mn = new float[3]; mx = new float[3]; }
    }

    static void Note(BlenderRigAnim.Stage1 s, string what) { if (!s.Notes.Contains(what)) s.Notes.Add(what); }

    /// <summary>Stage 1 of vehicle_rig.py's rig path on a glTF model: the scene its reduction tiers start from, or Fallback.</summary>
    /// <summary>The source is one the C# reads (.glb/.gltf): else the reason it is Blender's, before any read.</summary>
    public static string SourceFallback(Args a)
    {
        string ext = System.IO.Path.GetExtension(a.Input).ToLowerInvariant();
        return ext == ".glb" || ext == ".gltf" ? null : "a " + ext + " source: Blender's own importer, until the native reader";
    }

    public static BlenderRigAnim.Stage1 RunStage1(HafModel m, Args a) => RunStage1(m, a, null);

    public static BlenderRigAnim.Stage1 RunStage1(HafModel m, Args a, Func<string, HafModel> loadSecond)
    {
        var s = new BlenderRigAnim.Stage1 { Model = m };
        if (a.Problem != null) { s.Logs.AddRange(a.Logs); s.Exits = true; return s; }
        if (SourceFallback(a) != null) { s.Fallback = SourceFallback(a); return s; }
        if (a.Mode == "rigfast") { s.Fallback = "the source-skeleton fast path (rigfast): spun on the artist skeleton, not modelled"; return s; }
        if (a.Mode != "rig") { s.Fallback = "the mode '" + a.Mode + "' is not the rig path"; return s; }
        if (a.PreDie != null) { s.Dies = a.PreDie; return s; }
        // the import, as the importer leaves it (no material-less drop, no armature required, the animation kept); the argument-time
        // lines (a.Logs) come later: the rig path prints them after the merge
        BlenderRigAnim.ImportScene(m, s, new List<string>(), dropMaterialLess: false, requireArmature: false, convertRig: false);
        if (s.Fallback != null) return s;
        var names = s.Names;
        // ---- imp(): the importer's bone-shape placeholders (one 42-vertex icosphere per armature, hidden, no vertex groups: the
        //      signature holds by construction) purged with their lines, then any mesh of that signature without vertex groups
        foreach (var shape in names.BoneShapes) { names.ObjectPool.Remove(shape); s.Logs.Add("VEHICLE purged glTF importer bone-shape artifact: " + shape); Note(s, "a bone-shape placeholder purged at import"); }
        foreach (var o in s.Objects.ToList())
        {
            if (o.Type != "MESH" || o.Mesh.Groups.Count > 0 || !IsIcosphereArtifact(o.Name, o.Mesh)) continue;
            s.Logs.Add("VEHICLE purged glTF importer bone-shape artifact: " + o.Name);
            RemoveObject(s, o); Note(s, "a bone-shape placeholder purged at import");
        }
        // convert_renderables: a glTF holds no curve, surface, text or metaball object
        // ---- apply_brightness on the first model: the base-colour edits leave this stage's rows as they are (a flat colour's value,
        //      a tint's value and an image's pixels are the export's), the line's counts come from each material's chain
        if (!Brightness(s, m, names, s.Objects, a.Bright1, "")) return s;
        // the modifier bake: no glTF carries a modifier
        // ---- the second model merged in (merge2=): imported into the same scene, its parts prefixed B_, each mesh's target world
        //      matrix baked into its vertices, its helpers dropped
        if (a.Merge2 != null)
        {
            // before the second import: the four '|' fields and the file; the offset, rotation and scale are read AFTER the import
            var f4 = a.Merge2.Split('|');
            if (f4.Length != 4) { s.Logs.Add("VEHICLE ERROR: malformed merge2 argument: merge2=" + a.Merge2); s.Exits = true; return s; }
            string path2 = f4[0];
            if (!System.IO.File.Exists(path2)) { s.Logs.Add("VEHICLE ERROR: second model not found: " + path2); s.Exits = true; return s; }
            string ext2 = System.IO.Path.GetExtension(path2).ToLowerInvariant();
            if (ext2 != ".glb" && ext2 != ".gltf") { s.Fallback = "a second model that is not glTF (" + ext2 + "): Blender's own importer"; return s; }
            if (loadSecond == null) { s.Fallback = "a second model (merge2): no loader given"; return s; }
            var m2 = loadSecond(path2);
            var s2 = new BlenderRigAnim.Stage1 { Model = m2 };
            BlenderRigAnim.ImportScene(m2, s2, new List<string>(), dropMaterialLess: false, requireArmature: false, convertRig: false, seed: names, existingActions: s.Actions.Select(x => x.Name));   // posed at frame 1 like the first: its first clip is the active action when it is imported
            if (s2.Fallback != null) { s.Fallback = "the second model: " + s2.Fallback; return s; }
            foreach (var n in s2.Notes) Note(s, "second model: " + n);
            // the pools continue from the second import's (its names unique against the first model's)
            names = s2.Names; s.Names = names;
            var newObjs = s2.Objects.ToList();
            if (!Brightness(s, m2, s2.Names, newObjs, a.Bright2, " (second model)")) return s;
            // its bone-shape placeholders purged (second model)
            foreach (var shape in s2.Names.BoneShapes) { s.Logs.Add("VEHICLE purged glTF importer bone-shape artifact (second model): " + shape); names.ObjectPool.Remove(shape); }
            foreach (var o in newObjs.ToList())
                if (o.Type == "MESH" && o.Mesh.Groups.Count == 0 && IsIcosphereArtifact(o.Name, o.Mesh)) { s.Logs.Add("VEHICLE purged glTF importer bone-shape artifact (second model): " + o.Name); newObjs.Remove(o); RemoveObject(s2, o); names.ObjectPool.Remove(o.Name); }
            // the offset, rotation and scale, after the import's lines: float() of an offset or rotation component is not caught in the
            // script (it dies there), a bad scale field is its exit
            if (!VehicleProbe.Input.TryParseMerge2(a.Merge2, out _, out var off2, out var rot2, out var scl2, out var bad2, out string err2))
            {
                if (err2.StartsWith("second model: ", StringComparison.Ordinal)) { s.Dies = "ValueError: " + err2.Substring(14); return s; }
                s.Logs.Add("VEHICLE ERROR: " + err2); s.Exits = true; return s;
            }
            if (bad2.Count > 0) s.Logs.Add("VEHICLE WARN: second-model scale component(s) " + string.Join(", ", bad2.Select(x => "'" + x + "'")) + " not positive - using 1.0 there");
            var T2 = VehicleProbe.Merge2Matrix(off2, rot2, scl2);
            // the targets from the import's world matrices (view_layer.update() first: every object evaluated)
            var target = new Dictionary<BlenderRigAnim.SceneObject, float[]>();
            foreach (var o in newObjs) if (o.Type == "MESH" && o.Mesh.Vertices > 0) target[o] = VehicleProbe.MatMulMathutils(T2, VehicleProbe.ToRowMajor(o.World));
            // the rename: every object of the second import, in bpy.data.objects order (by name), to the pool's B_ name
            var created = newObjs.Select(o => o.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var old in created) { names.ObjectPool.Remove(old); renamed[old] = names.ObjectPool.Unique("B_" + old); }
            foreach (var o in newObjs) { o.Name = renamed[o.Name]; }
            // the slot identifiers stay as the import made them (OB<old name>): a rename of the object does not rename its slot
            // FLATTEN B: a shared mesh copied, the target baked into the vertices (Mesh.transform), a mirrored one's faces flipped,
            // the object at the identity and a root
            var users2 = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var o in target.Keys) users2[o.DataName] = (users2.TryGetValue(o.DataName, out int c) ? c : 0) + 1;
            var meshes2 = newObjs.Where(target.ContainsKey).ToList();
            foreach (var o in meshes2)
            {
                if (users2[o.DataName] > 1) { users2[o.DataName]--; string copy = names.MeshPool.Unique(o.DataName); o.Mesh = CloneMesh(o.Mesh); o.DataName = copy; users2[copy] = 1; Note(s, "second model: a shared mesh datablock copied"); }
                o.Parent = null; o.ParentBone = null; o.ParentBoneNode = -1; o.ParentInverse = null;
                var it = target[o];
                BlenderRigAnim.TransformPoints(VehicleProbe.ToColumnMajor(it), o.Mesh.Positions, o.Mesh.Vertices);
                if (VehicleProbe.Determinant4(it) < 0f) { o.Mesh.Faces = FlipFaces(o.Mesh); Note(s, "second model: a mirrored node's faces flipped"); }
                o.Props = new[] { new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } }; o.Local = VehicleProbe.ObjectMatrix(o.Props[0], o.Props[1], o.Props[2]); o.World = (float[])o.Local.Clone(); o.Dirty = false;
            }
            var helpers2 = newObjs.Where(o => !target.ContainsKey(o)).ToList();
            foreach (var h in helpers2) { RemoveObject(s2, h); names.ObjectPool.Remove(h.Name); }
            if (helpers2.Count > 0) s.Logs.Add("VEHICLE MERGE: " + helpers2.Count + " helper object(s) of the second model dropped (empties/armatures — placement baked into the meshes)");
            // the alignment numbers: B's world box after the bake
            float[] bmn = null, bmx = null;
            foreach (var o in meshes2)
            {
                LocalBox(o.Mesh, out var lmn, out var lmx);
                VehicleProbe.WorldBox(VehicleProbe.ToRowMajor(o.World), lmn, lmx, out var c2, out var d2);
                var lo = new[] { (float)(c2[0] - (float)(d2[0] / 2f)), (float)(c2[1] - (float)(d2[1] / 2f)), (float)(c2[2] - (float)(d2[2] / 2f)) };
                var hi = new[] { (float)(c2[0] + (float)(d2[0] / 2f)), (float)(c2[1] + (float)(d2[1] / 2f)), (float)(c2[2] + (float)(d2[2] / 2f)) };
                if (bmn == null) { bmn = lo; bmx = hi; } else for (int i = 0; i < 3; i++) { bmn[i] = Math.Min(bmn[i], lo[i]); bmx[i] = Math.Max(bmx[i], hi[i]); }
            }
            var f2 = a.Merge2.Split('|');
            s.Logs.Add(string.Format(CultureInfo.InvariantCulture, "VEHICLE MERGE: second model '{0}' -> {1} part(s) prefixed B_ | offset ({2}) rot ({3}) scale ({4}, {5}, {6})",
                path2.Replace('\\', '/').Split('/').Last(), meshes2.Count, f2[1], f2[2], VehicleProbe.G5(scl2[0]), VehicleProbe.G5(scl2[1]), VehicleProbe.G5(scl2[2])));
            if (bmn != null) s.Logs.Add("VEHICLE MERGE: B bbox min (" + Py(bmn[0], 2) + ", " + Py(bmn[1], 2) + ", " + Py(bmn[2], 2) + ") max (" + Py(bmx[0], 2) + ", " + Py(bmx[1], 2) + ", " + Py(bmx[2], 2) + ")");
            // the second import's objects join the scene after the first's; its actions join bpy.data.actions (sorted by name)
            foreach (var o2 in s2.Objects) if (o2.Collection > 0) o2.Collection += 100;   // the second file's Orphan Nodes: linked after the first's
            s.Objects.AddRange(s2.Objects);
            BlenderRigAnim.SortByCollection(s.Objects);
            s.Actions.AddRange(s2.Actions);
            BlenderExportTree.StableSort(s.Actions, (x, y) => BlenderDeploy.IdNameCmp(x.Name, y.Name));
            Note(s, "a second model merged");
        }
        // ---- between the merge and the rig path: the positional names files (namelist() dies of a missing one), the reduce tags
        //      (the first malformed one is the script's exit), the dials (an unparsable one is a death), then the argument-time lines
        if (a.Unsupported != null) { s.Fallback = a.Unsupported; return s; }
        if (a.NamesDie != null) { s.Dies = a.NamesDie; return s; }
        if (a.TagError != null) { s.Logs.Add(a.TagError); s.Exits = true; return s; }
        if (a.FoldDie != null) { s.Dies = a.FoldDie; return s; }
        if (a.DialsDie != null) { s.Dies = a.DialsDie; return s; }
        s.Logs.AddRange(a.Logs);
        // ---- the flatten: every mesh a root keeping its world transform, skinning and modifiers cleared; the helpers dropped
        var meshes = s.Objects.Where(o => o.Type == "MESH" && o.Mesh.Vertices > 0).ToList();
        foreach (var o in meshes)
        {
            if (IsIcosphereArtifact(o.Name, o.Mesh) && !a.Marked.Contains(o.Name))
            {
                s.Logs.Add("VEHICLE flatten: purged glTF importer bone-shape artifact: " + o.Name);
                RemoveObject(s, o); Note(s, "a bone-shape placeholder purged at the flatten"); continue;
            }
            var world = (float[])o.World.Clone();
            if (o.Mesh.Groups.Count > 0) { o.Mesh.Groups.Clear(); o.Mesh.DefNr = null; o.Mesh.DefWeight = null; Note(s, "a skinned source's vertex groups cleared"); }
            if (o.Mesh.ArmatureModifier != null) { o.Mesh.ArmatureModifier = null; Note(s, "an armature modifier removed"); }
            // `_fo.parent = None` keeps the local transform; `_fo.matrix_world = _fmw` writes the world back as properties
            o.Parent = null; o.ParentBone = null; o.ParentBoneNode = -1; o.ParentInverse = null;
            SetWorld(o, world);
        }
        var helpers = s.Objects.Where(o => o.Type != "MESH" || o.Mesh.Vertices == 0).ToList();
        foreach (var h in helpers) RemoveObject(s, h);
        if (helpers.Count > 0) { s.Logs.Add("VEHICLE flatten: " + helpers.Count + " non-mesh helper(s) dropped (empties/armatures/lights — placement baked into the meshes)"); Note(s, "helpers dropped at the flatten"); }
        s.Armature = null;
        Update(s);
        // ---- the per-source split: a source of ONE mesh object is separated into its loose parts
        var objs = s.Objects.Where(o => o.Type == "MESH" && o.Mesh.Vertices > 0).ToList();
        foreach (string prefix in new[] { "", "B_" })
        {
            var own = objs.Where(o => o.Name.StartsWith("B_", StringComparison.Ordinal) == (prefix == "B_")).ToList();
            if (own.Count != 1) continue;
            // view_layer.objects.active = the one object (unguarded): an object of the Orphan Nodes collection (a node in no scene) is
            // not in the view layer, and the assignment raises - the script dies here
            // (Blender's operator error ends in a newline; the dump writes it as a backslash and an n)
            if (own[0].Collection > 0) { s.Dies = "RuntimeError: Error: ViewLayer 'ViewLayer' does not contain object '" + own[0].Name + "'\\n"; Note(s, "the one part a node in no scene: the split's death"); return s; }
            SplitLoose(s, own[0], names);
            objs = s.Objects.Where(o => o.Type == "MESH" && o.Mesh.Vertices > 0).ToList();
            Note(s, "a single mesh object split into its loose parts");
        }
        // ---- the per-part placements, after the split, before the orientation
        if (a.PartTx != null)
        {
            var input = new VehicleProbe.Input(); input.AddPlacementLines(a.PartTx);
            foreach (var w in input.Warnings) s.Logs.Add("VEHICLE WARN: " + w);
            if (input.Placements.Count > 0)
            {
                Note(s, "per-part placement");
                var byName = objs.ToDictionary(o => o.Name, o => o, StringComparer.Ordinal);
                foreach (var pl in input.Placements)
                {
                    if (!byName.TryGetValue(pl.Name, out var o)) { s.Logs.Add("VEHICLE WARN: placement for '" + pl.Name + "' skipped — no such part after the split (re-Probe, then place it again)"); continue; }
                    // the part and its direct children detached keeping their world: `rel.parent = None; rel.matrix_world = mw` re-derives
                    // the properties from the world even for a root (a quaternion round trip, not a no-op), then view_layer.update()
                    foreach (var rel in new[] { o }.Concat(objs.Where(c => c.Parent == o)).ToList()) { var mw = rel.World; rel.Parent = null; rel.ParentBone = null; rel.ParentBoneNode = -1; SetWorld(rel, mw); }
                    Update(s);
                    LocalBox(o.Mesh, out var lmn, out var lmx);
                    var it = VehicleProbe.ToRowMajor(o.World);
                    VehicleProbe.WorldBox(it, lmn, lmx, out var c0, out var s0);
                    var T = VehicleProbe.MatMulMathutils(VehicleProbe.MatMulMathutils(VehicleProbe.MatMulMathutils(
                        VehicleProbe.MatTranslation(pl.Offset[0], pl.Offset[1], pl.Offset[2]), VehicleProbe.MatTranslation(c0[0], c0[1], c0[2])),
                        VehicleProbe.MatDiagonal(pl.Scale[0], pl.Scale[1], pl.Scale[2])), VehicleProbe.MatTranslation(-c0[0], -c0[1], -c0[2]));
                    SetWorld(o, VehicleProbe.ToColumnMajor(VehicleProbe.MatMulMathutils(T, it)));
                    Update(s);
                    VehicleProbe.WorldBox(VehicleProbe.ToRowMajor(o.World), lmn, lmx, out var c1, out var s1);
                    string F3(double v) => Py(v, 3);
                    s.Logs.Add("VEHICLE PLACED (rig): " + pl.Name + " | offset (" + string.Join(",", pl.Offset.Select(F3)) + ") scale (" + string.Join(",", pl.Scale.Select(F3))
                        + ") | centre (" + F3(c0[0]) + "," + F3(c0[1]) + "," + F3(c0[2]) + ") -> (" + F3(c1[0]) + "," + F3(c1[1]) + "," + F3(c1[2])
                        + ") | size (" + F3(s0[0]) + "," + F3(s0[1]) + "," + F3(s0[2]) + ") -> (" + F3(s1[0]) + "," + F3(s1[1]) + "," + F3(s1[2]) + ")");
                }
            }
        }
        // ---- the ignored parts, deleted
        if (a.Ignore.Count > 0)
        {
            var rem = objs.Where(o => a.Ignore.Contains(o.Name)).ToList();
            foreach (var o in rem) RemoveObject(s, o);
            objs = objs.Where(o => !a.Ignore.Contains(o.Name)).ToList();
            s.Logs.Add("VEHICLE ignored: " + rem.Count + " part(s) deleted from the output");
            if (rem.Count > 0) Note(s, "parts ignored (deleted)");
        }
        // ---- the orientation: Rx @ Ry @ Rz onto every root's matrix_world
        if (a.ModelRot.Any(v => Math.Abs(v) > 1e-6))
        {
            var orient = VehicleProbe.ProbeOrientMatrix(a.ModelRot);
            foreach (var o in s.Objects) if (o.Parent == null) SetWorld(o, VehicleProbe.ToColumnMajor(VehicleProbe.MatMulMathutils(orient, VehicleProbe.ToRowMajor(o.World))));
            Update(s);
            s.Logs.Add("VEHICLE orientation: straightened by (" + Py(a.ModelRot[0], 1) + ", " + Py(a.ModelRot[1], 1) + ", " + Py(a.ModelRot[2], 1) + ") deg before rigging");
            Note(s, "the orientation straightened");
        }
        // _apply_transforms makes `objs[0]` active: with every part Ignored the list is empty and the IndexError lands in _guard - its
        // ERROR line (the traceback goes to stderr), exit 1
        if (objs.Count == 0) { s.Logs.Add("VEHICLE ERROR: rig step crashed:"); s.Exits = true; Note(s, "every part ignored: the script's crash"); return s; }
        // ... and selects every part: an object of the importer's Orphan Nodes collection (a node in no scene) is not in the view
        // layer, so select_set raises ("cannot be selected because it is not in View Layer") - the same crash line, exit 1
        if (objs.Any(o => o.Collection > 0)) { s.Logs.Add("VEHICLE ERROR: rig step crashed:"); s.Exits = true; Note(s, "a part of a node in no scene: select_set's crash"); return s; }
        // ---- transform_apply(location, rotation, scale): a multi-user mesh copied first, the whole matrix into the data
        var dataUsers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in objs) dataUsers[o.DataName] = (dataUsers.TryGetValue(o.DataName, out int c) ? c : 0) + 1;
        foreach (var o in objs)
        {
            if (dataUsers[o.DataName] > 1)
            {
                dataUsers[o.DataName]--;
                string copy = names.MeshPool.Unique(o.DataName);
                o.Mesh = CloneMesh(o.Mesh); o.DataName = copy; dataUsers[copy] = 1;
                Note(s, "a shared mesh datablock copied for transform_apply");
            }
            var it = VehicleProbe.ToRowMajor(o.World);
            string dbg = Environment.GetEnvironmentVariable("VEHICLERIG_DEBUG_POS");
            if (dbg == o.Name)
            {
                Console.WriteLine("DEBUG " + o.Name + " world (row-major) " + string.Join(" ", it.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
                for (int v = 0; v < Math.Min(3, o.Mesh.Vertices); v++) Console.WriteLine("DEBUG before v" + v + " " + string.Join(" ", Enumerable.Range(0, 3).Select(k => o.Mesh.Positions[3 * v + k].ToString("R", CultureInfo.InvariantCulture))));
            }
            // BKE_mesh_transform -> transform_positions: every vertex through the whole matrix, UNLESS the matrix is within 1e-6 of the
            // identity, when nothing is touched (measured on the bell: a 1.0000001 scale and a 7.5e-8 rotation on its root)
            BlenderRigAnim.TransformPoints(o.World, o.Mesh.Positions, o.Mesh.Vertices);
            if (dbg == o.Name) for (int v = 0; v < Math.Min(3, o.Mesh.Vertices); v++) Console.WriteLine("DEBUG after v" + v + " " + string.Join(" ", Enumerable.Range(0, 3).Select(k => o.Mesh.Positions[3 * v + k].ToString("R", CultureInfo.InvariantCulture))));
            // a mirrored object (a negative determinant): the faces flipped, each corner's data with its corner
            if (VehicleProbe.Determinant4(it) < 0f)
            {
                o.Mesh.Faces = FlipFaces(o.Mesh);
                Note(s, "a mirrored object's faces flipped at transform_apply");
            }
            o.Props = new[] { new float[3], new[] { 1f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f } };
            o.Local = VehicleProbe.ObjectMatrix(o.Props[0], o.Props[1], o.Props[2]); o.World = (float[])o.Local.Clone(); o.Dirty = false;
        }
        return s;
    }

    /// <summary>mesh_flip_faces on triangles: corners 1 and 2 swap, every per-corner array (the UV layers, the custom normals)
    /// permuted with them.</summary>
    static int[] FlipFaces(BlenderRigAnim.MeshData d)
    {
        var tris = d.Faces; var r = (int[])tris.Clone();
        var uvs = new List<float[]>(); if (d.Uv != null) uvs.Add(d.Uv); foreach (var u in d.UvAll) if (!uvs.Contains(u)) uvs.Add(u);
        for (int f = 0; f < tris.Length / 3; f++)
        {
            int a = f * 3 + 1, b = f * 3 + 2;
            r[a] = tris[b]; r[b] = tris[a];
            foreach (var u in uvs) for (int k = 0; k < 2; k++) { float t = u[2 * a + k]; u[2 * a + k] = u[2 * b + k]; u[2 * b + k] = t; }
            foreach (var color in d.Colors) if (!color.point)
                for (int k = 0; k < 4; k++) { byte t = color.bytes[4 * a + k]; color.bytes[4 * a + k] = color.bytes[4 * b + k]; color.bytes[4 * b + k] = t; }
            if (d.CustomNormal != null) for (int k = 0; k < 2; k++) { short t = d.CustomNormal[2 * a + k]; d.CustomNormal[2 * a + k] = d.CustomNormal[2 * b + k]; d.CustomNormal[2 * b + k] = t; }
        }
        return r;
    }

    /// <summary>separate(type='LOOSE') on the one mesh object: BM_mesh_calc_edge_groups_as_arrays over the mesh's own edges (the
    /// importer's order), the first group staying with the object in its imported vertex order, every other group a NEW object
    /// (a copy asking the pools for its names) with the walk's vertex order - the probe's BlenderIslands rule on this mesh.</summary>
    static void SplitLoose(BlenderRigAnim.Stage1 s, BlenderRigAnim.SceneObject o, BlenderNames.Result names)
    {
        var d = o.Mesh; int n = d.Vertices;
        var disk = new List<int>[n]; var edges = new List<(int a, int b)>();
        if (d.Edges != null)
            for (int i = 0; i + 1 < d.Edges.Length; i += 2)
            {
                int id = edges.Count; edges.Add((d.Edges[i], d.Edges[i + 1]));
                (disk[d.Edges[i]] ?? (disk[d.Edges[i]] = new List<int>())).Add(id);
                (disk[d.Edges[i + 1]] ?? (disk[d.Edges[i + 1]] = new List<int>())).Add(id);
            }
        // each edge's faces in creation order (the mesh's face order); the walk reads them in radial order (RadialOrder)
        var facesOfEdge = new List<int>[edges.Count]; var edgeId = new Dictionary<long, int>();
        for (int i = 0; i < edges.Count; i++) { long key = (long)Math.Min(edges[i].a, edges[i].b) << 32 | (uint)Math.Max(edges[i].a, edges[i].b); if (!edgeId.ContainsKey(key)) edgeId[key] = i; }
        for (int f = 0; f < d.Faces.Length / 3; f++)
            for (int k = 0; k < 3; k++)
            {
                int a = d.Faces[3 * f + k], b = d.Faces[3 * f + (k + 1) % 3];
                long key = (long)Math.Min(a, b) << 32 | (uint)Math.Max(a, b);
                if (!edgeId.TryGetValue(key, out int e)) continue;
                (facesOfEdge[e] ?? (facesOfEdge[e] = new List<int>())).Add(f);
            }
        var tagged = new bool[n]; var edgeTagged = new bool[edges.Count]; var faceTagged = new bool[d.Faces.Length / 3];
        var groups = new List<List<int>>(); var groupFaces = new List<List<int>>(); var groupEdges = new List<List<int>>(); var stack = new Stack<int>();
        for (int seed = 0; seed < n; seed++)
        {
            if (tagged[seed]) continue;
            var verts = new List<int>(); var faces = new List<int>(); var edgeList = new List<int>(); tagged[seed] = true; verts.Add(seed);
            if (disk[seed] != null)
            {
                int v = seed;
                while (true)
                {
                    if (disk[v] != null)
                        foreach (int e in disk[v])
                        {
                            if (edgeTagged[e]) continue;
                            edgeTagged[e] = true; edgeList.Add(e);
                            if (facesOfEdge[e] != null) foreach (int f in RadialOrder(facesOfEdge[e])) if (!faceTagged[f]) { faceTagged[f] = true; faces.Add(f); }
                            int other = edges[e].a == v ? edges[e].b : edges[e].a;
                            if (!tagged[other]) { tagged[other] = true; verts.Add(other); stack.Push(other); }
                        }
                    if (stack.Count == 0) break;
                    v = stack.Pop();
                }
            }
            groups.Add(verts); groupFaces.Add(faces); groupEdges.Add(edgeList);
        }
        o.Mesh.EdgeSharp = null;   // the edit-mode round trip (separate LOOSE) writes sharp_edge back only when an edge is marked: none is
        if (groups.Count <= 1) return;
        groups[0].Sort();
        var customVectors = d.CustomNormal == null ? null : VehicleProbe.DecodeBMeshCustomNormals(d.Positions, d.Faces, d.FaceSharp ?? new bool[d.Faces.Length / 3], d.CustomNormal);
        int at = s.Objects.IndexOf(o);
        var made = new List<BlenderRigAnim.SceneObject>();
        for (int g = 0; g < groups.Count; g++)
        {
            // the staying group keeps the mesh's own face and edge order; a copied group gets the walk's discovery order
            var sub = SubMesh(d, groups[g], g == 0 ? null : groupFaces[g], g == 0 ? null : groupEdges[g].Select(e => (edges[e].a, edges[e].b)).ToList());
            if (g == 0) { o.Mesh = sub; continue; }
            if (customVectors != null)
            {
                var vectors = new float[3 * sub.Faces.Length]; int atCorner = 0;
                foreach (int f in groupFaces[g]) for (int k = 0; k < 3; k++)
                { Array.Copy(customVectors, 3 * (3 * f + k), vectors, 3 * atCorner, 3); atCorner++; }
                sub.CustomNormal = VehicleProbe.EncodeBMeshCustomNormals(sub.Positions, sub.Faces, sub.FaceSharp ?? new bool[sub.Faces.Length / 3], vectors);
            }
            var copy = new BlenderRigAnim.SceneObject
            {
                Name = names.ObjectPool.Unique(o.Name), Type = "MESH", DataName = names.MeshPool.Unique(o.DataName), Mesh = sub, Source = o.Source, Collection = o.Collection,
                Props = new[] { (float[])o.Props[0].Clone(), (float[])o.Props[1].Clone(), (float[])o.Props[2].Clone() }, Local = (float[])o.Local.Clone(), World = (float[])o.World.Clone(),
            };
            made.Add(copy);
        }
        s.Objects.AddRange(made);   // linked after every object of the original's collection (separate links the copies to its collections)
        BlenderRigAnim.SortByCollection(s.Objects);
        string dbg = Environment.GetEnvironmentVariable("VEHICLERIG_DEBUG_FACES");
        if (!string.IsNullOrEmpty(dbg))
            foreach (var x in new[] { o }.Concat(made))
                if (x.Name == dbg)
                {
                    var md = x.Mesh;
                    Console.WriteLine("DEBUG " + x.Name + " verts " + md.Vertices + " polys " + md.Faces.Length / 3 + " edges " + (md.Edges == null ? -1 : md.Edges.Length / 2));
                    Console.WriteLine("DEBUG polys: " + string.Join(" ", Enumerable.Range(0, Math.Min(8, md.Faces.Length / 3)).Select(f => "[" + md.Faces[3 * f] + "," + md.Faces[3 * f + 1] + "," + md.Faces[3 * f + 2] + "]")));
                    if (md.Edges != null) Console.WriteLine("DEBUG edges: " + string.Join(" ", Enumerable.Range(0, Math.Min(8, md.Edges.Length / 2)).Select(e => "[" + md.Edges[2 * e] + "," + md.Edges[2 * e + 1] + "]")));
                }
    }

    /// <summary>BM_FACES_OF_EDGE: the edge's radial cycle from `e->l`. bmesh_radial_loop_append inserts each new loop after the
    /// current `e->l` and points `e->l` at it, so the walk sees the LAST face created first, then the others in creation order
    /// (measured on the organ gun's split: [0,1,4] before [0,2,1] on their shared edge).</summary>
    internal static IEnumerable<int> RadialOrder(List<int> created)
    {
        if (created.Count <= 1) return created;
        return new[] { created[created.Count - 1] }.Concat(created.Take(created.Count - 1));
    }

    /// <summary>The mesh of one loose part: its vertices in the given order, the faces whose corners are all in it (the original face
    /// order), every per-corner and per-face array with them, the edges within it in the original edge order.</summary>
    static BlenderRigAnim.MeshData SubMesh(BlenderRigAnim.MeshData d, List<int> verts, List<int> faceOrder, List<(int a, int b)> edgeOrder)
    {
        var map = new Dictionary<int, int>(); for (int i = 0; i < verts.Count; i++) map[verts[i]] = i;
        var sub = new BlenderRigAnim.MeshData { Vertices = verts.Count, Positions = new float[3 * verts.Count], Materials = new List<string>(d.Materials), Groups = new List<string>(), ActiveColor = d.ActiveColor };
        for (int i = 0; i < verts.Count; i++) for (int k = 0; k < 3; k++) sub.Positions[3 * i + k] = d.Positions[3 * verts[i] + k];
        var faces = new List<int>(); var faceMat = new List<int>(); var sharp = new List<bool>(); var corners = new List<int>();
        IEnumerable<int> order = faceOrder ?? Enumerable.Range(0, d.Faces.Length / 3);
        foreach (int f in order)
        {
            int a = d.Faces[3 * f], b = d.Faces[3 * f + 1], c = d.Faces[3 * f + 2];
            if (!map.ContainsKey(a) || !map.ContainsKey(b) || !map.ContainsKey(c)) continue;
            faces.Add(map[a]); faces.Add(map[b]); faces.Add(map[c]); faceMat.Add(d.FaceMaterial[f]); if (d.FaceSharp != null) sharp.Add(d.FaceSharp[f]);
            corners.Add(3 * f); corners.Add(3 * f + 1); corners.Add(3 * f + 2);
        }
        sub.Faces = faces.ToArray(); sub.FaceMaterial = faceMat.ToArray(); sub.FaceSharp = d.FaceSharp == null ? null : sharp.ToArray();
        foreach (var color in d.Colors)
        {
            var indices = color.point ? verts : corners;
            var bytes = new byte[4 * indices.Count];
            for (int i = 0; i < indices.Count; i++) Array.Copy(color.bytes, 4 * indices[i], bytes, 4 * i, 4);
            sub.Colors.Add((color.name, color.point, bytes));
        }
        float[] Corners2(float[] src) { if (src == null) return null; var r = new float[2 * corners.Count]; for (int i = 0; i < corners.Count; i++) { r[2 * i] = src[2 * corners[i]]; r[2 * i + 1] = src[2 * corners[i] + 1]; } return r; }
        sub.Uv = Corners2(d.Uv); sub.UvAll = d.UvAll.Select(Corners2).ToList();
        if (d.Uv != null && d.UvAll.Count > 0 && ReferenceEquals(d.Uv, d.UvAll[0])) sub.Uv = sub.UvAll[0];
        if (d.CustomNormal != null) { sub.CustomNormal = new short[2 * corners.Count]; for (int i = 0; i < corners.Count; i++) { sub.CustomNormal[2 * i] = d.CustomNormal[2 * corners[i]]; sub.CustomNormal[2 * i + 1] = d.CustomNormal[2 * corners[i] + 1]; } }
        if (edgeOrder != null) sub.Edges = edgeOrder.SelectMany(e => new[] { map[e.a], map[e.b] }).ToArray();
        else if (d.Edges != null)
        {
            var e = new List<int>();
            for (int i = 0; i + 1 < d.Edges.Length; i += 2) if (map.ContainsKey(d.Edges[i]) && map.ContainsKey(d.Edges[i + 1])) { e.Add(map[d.Edges[i]]); e.Add(map[d.Edges[i + 1]]); }
            sub.Edges = e.ToArray();
        }
        return sub;
    }

    /// <summary>apply_brightness(scope, factor, tag): the factor clamped to [0.2, 4], nothing under 1e-3 from 1; the scope's materials
    /// (its meshes' slots) each adjusted through the importer's Base Color chain - a flat colour's value scaled, a directly linked
    /// texture's pixels by the whole factor, a MULTIPLY tint (baseColorFactor with a texture) absorbing what it can and the pixels
    /// taking the residual; the line counts the images whose pixels change (by image and residual) and the materials adjusted.
    /// A chain the script rejects (vertex colours, an extension's diffuse) is left to Blender here.</summary>
    static bool Brightness(BlenderRigAnim.Stage1 s, HafModel model, BlenderNames.Result names, List<BlenderRigAnim.SceneObject> scope, double factor0, string tag)
    {
        double factor = Math.Min(4.0, Math.Max(0.2, factor0));
        if (Math.Abs(factor - 1.0) < 1e-3) return true;
        var mats = new List<string>();
        foreach (var o in scope) if (o.Type == "MESH") foreach (var mn in o.Mesh.Materials) if (mn != null && !mats.Contains(mn)) mats.Add(mn);
        var matOf = new Dictionary<string, (int mat, bool vc)>(StringComparer.Ordinal);
        foreach (var kv in names.MaterialOf) if (!matOf.ContainsKey(kv.Value)) matOf[kv.Value] = (kv.Key.material, kv.Key.vertexColor);
        var jobs = new HashSet<string>(StringComparer.Ordinal); int edited = 0, warn = 0;
        foreach (var mn in mats)
        {
            if (!matOf.TryGetValue(mn, out var id)) { s.Fallback = "brightness: the material '" + mn + "' is not one the importer made of the file"; return false; }
            if (id.vc) { s.Fallback = "brightness: a material with vertex colours (the importer's Color Attribute chain) is not modelled"; return false; }
            if (id.mat < 0 || id.mat >= model.Materials.Count) { s.Fallback = "brightness: a material without a glTF material"; return false; }
            var mat = model.Materials[id.mat];
            if (mat.Name != null && model.ExtensionsUsed.Contains("KHR_materials_pbrSpecularGlossiness")) { s.Fallback = "brightness: a specular-glossiness material's chain is not modelled"; return false; }
            if (mat.BaseColorTexture < 0) continue;   // a flat colour: Base Color's value scaled, no image
            if (mat.BaseColorTexture >= model.Textures.Count) { s.Fallback = "brightness: a texture the file does not have"; return false; }
            int image = model.Textures[mat.BaseColorTexture].Source;
            if (image < 0 || image >= model.Images.Count) { s.Fallback = "brightness: a texture without an image (the importer's node has none: the script's WARN chain) is not modelled"; return false; }
            var bc = mat.BaseColorFactor ?? new double[] { 1, 1, 1, 1 };
            double[] resid;
            if (bc[0] == 1.0 && bc[1] == 1.0 && bc[2] == 1.0) resid = new[] { factor, factor, factor };   // the texture linked straight in: the whole factor on the pixels
            else
            {
                // the importer's MULTIPLY mix of the tint and the texture: the tint rises first, the pixels take the residual (per channel)
                resid = new double[3];
                for (int c = 0; c < 3; c++) { double t = (float)bc[c]; double want = t * factor; double nw = Math.Min(1.0, want); resid[c] = nw <= 0.0 ? 1.0 : want / nw; }
            }
            if (resid.All(r => Math.Abs(r - 1.0) < 1e-3)) continue;   // the tint absorbed everything: no image copied
            string key = image + "|" + string.Join(",", resid.Select(r => Math.Round(r, 4).ToString("R", CultureInfo.InvariantCulture)));
            if (jobs.Add(key)) edited++;
            Note(s, "brightness: an image's pixels edited (the export carries Blender's re-encode)");
        }
        s.Logs.Add("VEHICLE brightness x" + Py(factor, 2) + tag + ": " + edited + " base-color image(s) copied+adjusted, " + (mats.Count - warn) + " material(s) adjusted" + (warn > 0 ? ", " + warn + " SKIPPED (unsupported chains, see warnings)" : ""));
        Note(s, "the brightness factor");
        return true;
    }

    static BlenderRigAnim.MeshData CloneMesh(BlenderRigAnim.MeshData d) => new BlenderRigAnim.MeshData
    {
        Vertices = d.Vertices, Faces = (int[])d.Faces.Clone(), Positions = (float[])d.Positions.Clone(), FaceMaterial = (int[])d.FaceMaterial.Clone(), Uv = d.Uv == null ? null : (float[])d.Uv.Clone(),
        Materials = new List<string>(d.Materials), Groups = new List<string>(d.Groups), DefNr = d.DefNr, DefWeight = d.DefWeight, ArmatureModifier = d.ArmatureModifier,
        Edges = d.Edges == null ? null : (int[])d.Edges.Clone(), EdgeSharp = d.EdgeSharp == null ? null : (bool[])d.EdgeSharp.Clone(), FaceSharp = d.FaceSharp == null ? null : (bool[])d.FaceSharp.Clone(), CustomNormal = d.CustomNormal == null ? null : (short[])d.CustomNormal.Clone(),
        UvAll = d.UvAll.Select(u => (float[])u.Clone()).ToList(), Colors = d.Colors.Select(c => (c.name, c.point, (byte[])c.bytes.Clone())).ToList(), ActiveColor = d.ActiveColor,
    };

    /// <summary>`o.matrix_world = m`: the properties through BKE_object_apply_mat4 (the object is a root here), the world
    /// left as the last evaluation had it until the next update.</summary>
    static void SetWorld(BlenderRigAnim.SceneObject o, float[] worldColumnMajor)
    {
        BlenderRigAnim.ApplyMat4(worldColumnMajor, null, out var loc, out var quat, out var scale);
        o.Props = new[] { loc, quat, scale }; o.Local = VehicleProbe.ObjectMatrix(loc, quat, scale); o.ParentInverse = null; o.EvalStale = false; o.Dirty = true;
    }

    /// <summary>view_layer.update() on a scene of roots: every tagged object's world is its own matrix.</summary>
    static void Update(BlenderRigAnim.Stage1 s)
    {
        foreach (var o in s.Objects) if (o.Dirty) { o.World = (float[])o.Local.Clone(); o.Dirty = false; }
    }

    static void RemoveObject(BlenderRigAnim.Stage1 s, BlenderRigAnim.SceneObject o)
    {
        s.Objects.Remove(o);
        s.Names.ObjectPool.Remove(o.Name);   // bpy.data.objects.remove frees the object name; orphan mesh datablocks remain reserved
        foreach (var c in s.Objects) if (c.Parent == o) { c.Parent = null; c.Dirty = true; }   // a removed parent leaves its children roots (their world kept until the next update)
        foreach (var act in s.Actions) foreach (var slot in act.Slots) if (slot.Object == o) slot.Object = null;
    }
}
