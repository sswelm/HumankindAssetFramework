// VehicleRigArgs.cs - the Vehicle Lab's rig command line, built in ONE place (step (a) of the vehicle_rig port, 2026-10-14;
// docs/Review-Backlog.md item (5)). VehicleLabWindow.Vehicleize fills VehicleRigArgs from its fields and hands Blender
// RigArgLine's text; tools/vehiclerig-drill/VehicleRigJobs.cs fills it from a saved recipe's JSON (RigArgsFromRecipe, the
// window's load normalisations repeated here) and writes the drill's jobs from RigArgv's tokens - so the drill runs
// vehicle_rig.py on exactly the arguments a Generate runs it on. The Lab's gate rows hold the two fillings to the same
// line on every saved recipe (VehicleLabWindow.GenerateHeadless), the unit tests hold the template to the script's
// argv table (argv[1..75] positional, then the tagged merge2= bright= flagfold= defaultreduce= shroudreduce= gunreduce=
// windowreduce= parttx= arguments, each quoted as one token).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using Newtonsoft.Json.Linq;

/// <summary>Every value the rig command line is made of, as the window holds it after a recipe load.</summary>
public sealed class VehicleRigArgs
{
    public bool fast;                                   // rigfast (the source-skeleton fast path) or rig
    public string srcFile = "", outGlb = "", previewFbx = "";
    public int frames = 15, axisChoice, minVerts = 1; public float degrees = -360f;
    public int treadAdvCells = 3; public float treadCellsPerLink = 4f; public bool tracksStatic, spinEnabled = true;
    public bool waveEnabled; public float rockDegrees; public int rockFrames = 120, rockAxisChoice; public float rockHeading, rockPitchDeg = 2.4f;
    public int rockPitchCycles = 1, rockRollCycles = 1; public float rockPitchPhase = 90f;
    public float modelRotX, modelRotY, modelRotZ;
    public int tailAxisChoice; public float tailYawAdj, tailPitchAdj;
    public float trailSpreadDeg = 35f; public int trailFrames = 12;
    public float gunPivot = 0.5f, gunDeployElev; public float recoilDist; public int recoilFrames = 16, recoilLead;
    public bool doubleSided, fixInsideOut;
    public float oarSweepDeg = 24f, oarDipDeg = 18f; public int oarFrames = 24; public float oarBladeRollDeg, oarLiftDeg, oarRakeDeg, oarPivotPct = 30f, oarLengthPct = 100f;
    public float riggingReducePct = 75f, structureReducePct = 50f, bodyReducePct, oarReducePct, sailReducePct, rudderReducePct, wheelReducePct, flipReducePct, preserveReducePct, detailReducePct, shroudReducePct, gunReducePct, turretReducePct, windowReducePct, defaultReducePct;
    public bool sailFoldIdle; public int sailFoldFrames = 12; public float sailFoldAngleDeg = 270f; public bool sailFoldReverse; public float sailFoldSag;
    public bool flagFoldOn; public float flagFoldDeg; public int flagFoldFrames = 12;
    public string srcFile2 = ""; public float model2OffX, model2OffY, model2OffZ, model2RotX, model2RotY, model2RotZ, model2ScaleX = 1f, model2ScaleY = 1f, model2ScaleZ = 1f;
    public float model1Bright = 1f, model2Bright = 1f;
    /// <summary>The names files by role key: wheels, turrets, ignore, tracks, guns, rotors, tailrotors, trails, muzzles, cradles,
    /// oars, sails, rigging, structure, bodies, flags, rudders, preserve, flip, detail, default, shroud, window - the "@path"
    /// arguments. The window writes them under its preview folder, the jobs writer under the drill's work folder.</summary>
    public Dictionary<string, string> namesFiles = new Dictionary<string, string>(StringComparer.Ordinal);
    public string partTxFile;                           // the placement lines' file, or null when no part is placed
}

public static class VehicleRigArgLine
{
    public static readonly string[] AxisOptions = { "AUTO", "X", "Y", "Z" };
    public static readonly string[] NamesRoles = { "wheels", "turrets", "ignore", "tracks", "guns", "rotors", "tailrotors", "trails", "muzzles", "cradles", "oars", "sails", "rigging",
                                                   "structure", "bodies", "flags", "rudders", "preserve", "flip", "detail", "default", "shroud", "window" };

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string F1(float v) => v.ToString("0.#", Inv);
    static string F2(float v) => v.ToString("0.##", Inv);
    static string F3(float v) => v.ToString("0.###", Inv);
    static string Names(VehicleRigArgs a, string role) => "@" + (a.namesFiles.TryGetValue(role, out var p) ? p : throw new ArgumentException("no names file for the role '" + role + "'"));

    /// <summary>The argv tokens after `--`, in the script's order: [0] the mode, [1..75] the positional block, then the tagged
    /// arguments. `quoted` says which the window wraps in quotes on its command line (every path, "@file", the rotation and
    /// the tagged arguments; never a number or a word).</summary>
    public static List<(string value, bool quoted)> RigArgv(VehicleRigArgs a)
    {
        var t = new List<(string, bool)>();
        void U(string v) => t.Add((v, false));
        void Q(string v) => t.Add((v, true));
        U(a.fast ? "rigfast" : "rig"); Q(a.srcFile); Q(a.outGlb); Q(a.previewFbx); Q(Names(a, "wheels")); Q(Names(a, "turrets"));
        U(a.axisChoice == 0 ? "AUTO" : AxisOptions[a.axisChoice]); U(a.frames.ToString(Inv)); U(F1(a.spinEnabled ? a.degrees : 0f));
        Q(Names(a, "ignore")); Q(Names(a, "tracks")); Q(Names(a, "guns"));
        U(a.treadAdvCells.ToString(Inv)); U("1"); U("1"); U(F2(a.treadCellsPerLink)); U(a.tracksStatic || !a.spinEnabled ? "1" : "0");
        U(F2(a.waveEnabled ? a.rockDegrees : 0f)); U(a.rockFrames.ToString(Inv)); U(a.rockAxisChoice == 1 ? "X" : a.rockAxisChoice == 2 ? "Y" : "AUTO");
        U(F2(a.rockHeading)); U(F2(a.waveEnabled ? a.rockPitchDeg : 0f)); U(a.rockPitchCycles.ToString(Inv));
        Q(F2(a.modelRotX) + "," + F2(a.modelRotY) + "," + F2(a.modelRotZ)); U(F2(a.rockPitchPhase)); U(a.rockRollCycles.ToString(Inv));
        Q(Names(a, "rotors")); Q(Names(a, "tailrotors")); U(a.tailAxisChoice == 0 ? "AUTO" : AxisOptions[a.tailAxisChoice]); U(F2(a.tailYawAdj)); U(F2(a.tailPitchAdj));
        Q(Names(a, "trails")); U(F2(a.trailSpreadDeg)); U(a.trailFrames.ToString(Inv)); U(F3(a.gunPivot)); U(F2(a.gunDeployElev));
        Q(Names(a, "muzzles")); Q(Names(a, "cradles")); U(F3(a.recoilDist)); U(a.recoilFrames.ToString(Inv)); U(a.recoilLead.ToString(Inv)); U(a.doubleSided ? "1" : "0");
        Q(Names(a, "oars")); U(F2(a.oarSweepDeg)); U(F2(a.oarDipDeg)); U(a.oarFrames.ToString(Inv)); U(a.fixInsideOut ? "1" : "0"); U(F2(a.oarBladeRollDeg));
        Q(Names(a, "sails")); Q(Names(a, "rigging")); U(F1(a.riggingReducePct)); Q(Names(a, "structure")); U(F1(a.structureReducePct)); Q(Names(a, "bodies")); U(F1(a.bodyReducePct));
        Q(Names(a, "flags")); U(F2(a.oarLiftDeg)); U(F2(a.oarRakeDeg)); U(F1(a.oarPivotPct)); U(F1(a.oarLengthPct));
        Q(Names(a, "rudders")); U(F1(a.oarReducePct)); U(F1(a.sailReducePct)); Q(Names(a, "preserve")); U(F1(a.rudderReducePct)); U(F1(a.wheelReducePct));
        Q(Names(a, "flip")); U(F1(a.flipReducePct)); U(F1(a.preserveReducePct)); Q(Names(a, "detail")); U(F1(a.detailReducePct));
        U(a.sailFoldIdle ? "1" : "0"); U(Math.Max(1, a.sailFoldFrames).ToString(Inv)); U(F1(a.sailFoldAngleDeg)); U(a.sailFoldReverse ? "1" : "0"); U(F2(a.sailFoldSag));
        // the tagged arguments, in the window's order; each absent on its own terms
        string merge2 = Merge2Text(a.srcFile2, a.model2OffX, a.model2OffY, a.model2OffZ, a.model2RotX, a.model2RotY, a.model2RotZ, a.model2ScaleX, a.model2ScaleY, a.model2ScaleZ);
        if (merge2 != null) Q("merge2=" + merge2);
        string bright = BrightText(a.srcFile2, a.model1Bright, a.model2Bright);
        if (bright != null) Q("bright=" + bright);
        if (a.flagFoldOn) Q("flagfold=" + F1(a.flagFoldDeg) + "|" + Math.Max(1, a.flagFoldFrames).ToString(Inv));
        if (a.defaultReducePct > 0f) Q("defaultreduce=" + Names(a, "default") + "|" + F1(a.defaultReducePct));
        Q("shroudreduce=" + Names(a, "shroud") + "|" + F1(a.shroudReducePct));
        if (a.gunReducePct > 0f) Q("gunreduce=" + F1(a.gunReducePct));
        if (a.turretReducePct > 0f) Q("turretreduce=" + F1(a.turretReducePct));
        Q("windowreduce=" + Names(a, "window") + "|" + F1(a.windowReducePct));
        if (a.partTxFile != null) Q("parttx=@" + a.partTxFile);
        return t;
    }

    /// <summary>The command line after the script: what the window hands Blender.</summary>
    public static string RigArgLine(VehicleRigArgs a) => string.Join(" ", RigArgv(a).Select(x => x.quoted ? "\"" + x.value + "\"" : x.value));

    /// <summary>The merge2= text, the characters both the probe and the rig parse (five decimals, the per-axis scale); null without a second model.</summary>
    public static string Merge2Text(string srcFile2, float ox, float oy, float oz, float rx, float ry, float rz, float sx, float sy, float sz)
    {
        if (string.IsNullOrWhiteSpace(srcFile2)) return null;
        string F(float v) => v.ToString("0.#####", Inv);
        return $"{srcFile2.Trim().Replace('\\', '/')}|{F(ox)},{F(oy)},{F(oz)}|{F(rx)},{F(ry)},{F(rz)}|{VehicleLabRules.Merge2ScaleField(sx, sy, sz)}";
    }

    /// <summary>The bright= text; null when both factors are 1 (an untouched recipe sends nothing).</summary>
    public static string BrightText(string srcFile2, float model1Bright, float model2Bright)
    {
        bool b1 = Math.Abs(model1Bright - 1f) > 0.005f;
        bool b2 = !string.IsNullOrWhiteSpace(srcFile2) && Math.Abs(model2Bright - 1f) > 0.005f;
        if (!b1 && !b2) return null;
        return F3(model1Bright) + "|" + F3(b2 ? model2Bright : 1f);
    }

    // ---- a saved recipe's JSON, read as the window loads it (JsonUtility: an absent key keeps the field initializer;
    //      then LoadRecipeFromPath's guards) ----

    public sealed class RecipePart { public string name = ""; public int role; public float offX, offY, offZ, scaleX = 1f, scaleY = 1f, scaleZ = 1f; }

    public sealed class Recipe
    {
        public VehicleRigArgs args = new VehicleRigArgs();
        public List<RecipePart> parts = new List<RecipePart>(), boneParts = new List<RecipePart>();
        public bool useSourceRig;
        public bool FastPath => useSourceRig && boneParts.Count > 0;
        public List<RecipePart> ActiveParts => FastPath ? boneParts : parts;
    }

    /// <summary>Body 0, Wheel 1, Turret 2, Ignore 3, Default 4, Edgecase 5, Caterpillar 6, Gun 7, Rotor 8, TailRotor 9, Trail 10, Muzzle 11,
    /// Cradle 12, Oar 13, Sail 14, Rigging 15, Structure 16, Flag 17, Rudder 18, Preserve 19, Flip 20, Detail 21, Shroud 22, Window 23
    /// - the window's Role enum, whose ints the saved recipes hold.</summary>
    public static readonly Dictionary<string, int> RoleOfNamesFile = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        { "wheels", 1 }, { "turrets", 2 }, { "ignore", 3 }, { "tracks", 6 }, { "guns", 7 }, { "rotors", 8 }, { "tailrotors", 9 }, { "trails", 10 }, { "muzzles", 11 },
        { "cradles", 12 }, { "oars", 13 }, { "sails", 14 }, { "rigging", 15 }, { "structure", 16 }, { "bodies", 0 }, { "flags", 17 }, { "rudders", 18 },
        { "preserve", 19 }, { "flip", 20 }, { "detail", 21 }, { "default", 4 }, { "shroud", 22 }, { "window", 23 },
    };

    public static bool IsPlaced(RecipePart p) => p.offX * p.offX + p.offY * p.offY + p.offZ * p.offZ > 1e-10f ||
        (p.scaleX - 1f) * (p.scaleX - 1f) + (p.scaleY - 1f) * (p.scaleY - 1f) + (p.scaleZ - 1f) * (p.scaleZ - 1f) > 1e-10f;

    static float Flt(JObject o, string key, float dflt) => o[key] is JValue v && v.Type != JTokenType.Null ? Convert.ToSingle(v.Value, Inv) : dflt;
    static int Int(JObject o, string key, int dflt) => o[key] is JValue v && v.Type != JTokenType.Null ? Convert.ToInt32(v.Value, Inv) : dflt;
    static bool Bool(JObject o, string key, bool dflt) => o[key] is JValue v && v.Type == JTokenType.Boolean ? (bool)v.Value : dflt;
    static string Str(JObject o, string key, string dflt) => o[key] is JValue v && v.Type == JTokenType.String ? (string)v.Value : dflt;
    static float[] Vec(JObject o, string key, float dx, float dy, float dz)
    {
        if (!(o[key] is JObject v)) return new[] { dx, dy, dz };
        return new[] { Flt(v, "x", dx), Flt(v, "y", dy), Flt(v, "z", dz) };
    }

    public static Recipe RigArgsFromRecipe(string json)
    {
        var o = JObject.Parse(json);
        if (!(o["parts"] is JArray)) throw new InvalidDataException("not a Vehicle Lab recipe: parts must be an array");
        var r = new Recipe(); var a = r.args;
        a.srcFile = Str(o, "srcFile", ""); a.outGlb = Str(o, "outGlb", "");
        a.frames = Int(o, "frames", 0); a.axisChoice = Int(o, "axisChoice", 0); a.minVerts = Int(o, "minVerts", 0); a.degrees = Flt(o, "degrees", 0f);
        int tac = Int(o, "treadAdvCells", 3); a.treadAdvCells = tac > 0 ? tac : 3;
        float tcl = Flt(o, "treadCellsPerLink", 4f); a.treadCellsPerLink = tcl > 0f ? tcl : 4f;
        var mr = Vec(o, "modelRot", 0, 0, 0); a.modelRotX = mr[0]; a.modelRotY = mr[1]; a.modelRotZ = mr[2];
        a.tracksStatic = Bool(o, "tracksStatic", false); a.spinEnabled = Bool(o, "spinEnabled", true); a.doubleSided = Bool(o, "doubleSided", false); a.fixInsideOut = Bool(o, "fixInsideOut", false);
        a.oarSweepDeg = Flt(o, "oarSweepDeg", 24f); a.oarDipDeg = Flt(o, "oarDipDeg", 18f); a.oarFrames = Int(o, "oarFrames", 24);
        a.oarBladeRollDeg = Flt(o, "oarBladeRollDeg", 0f); a.oarLiftDeg = Flt(o, "oarLiftDeg", 0f); a.oarRakeDeg = Flt(o, "oarRakeDeg", 0f);
        a.oarPivotPct = Flt(o, "oarPivotPct", 30f); a.oarLengthPct = Flt(o, "oarLengthPct", 100f);
        a.riggingReducePct = Flt(o, "riggingReducePct", 75f); a.structureReducePct = Flt(o, "structureReducePct", 50f); a.bodyReducePct = Flt(o, "bodyReducePct", 0f);
        a.oarReducePct = Flt(o, "oarReducePct", 0f); a.sailReducePct = Flt(o, "sailReducePct", 0f); a.rudderReducePct = Flt(o, "rudderReducePct", 0f); a.wheelReducePct = Flt(o, "wheelReducePct", 0f);
        a.flipReducePct = Flt(o, "flipReducePct", 0f); a.defaultReducePct = Flt(o, "defaultReducePct", 0f); a.shroudReducePct = Flt(o, "shroudReducePct", 0f); a.gunReducePct = Flt(o, "gunReducePct", 0f); a.turretReducePct = Flt(o, "turretReducePct", 0f);
        a.windowReducePct = Flt(o, "windowReducePct", 0f); a.preserveReducePct = Flt(o, "preserveReducePct", 0f); a.detailReducePct = Flt(o, "detailReducePct", 0f);
        a.sailFoldIdle = Bool(o, "sailFoldIdle", false);
        int sff = Int(o, "sailFoldFrames", 12); a.sailFoldFrames = sff <= 0 ? 12 : sff;
        float sfa = Flt(o, "sailFoldAngleDeg", 270f); a.sailFoldAngleDeg = sfa <= 0f ? 270f : sfa;
        a.sailFoldReverse = Bool(o, "sailFoldReverse", false); a.sailFoldSag = Math.Min(1f, Math.Max(0f, Flt(o, "sailFoldSag", 0f)));
        a.flagFoldOn = Bool(o, "flagFoldOn", false); a.flagFoldDeg = Math.Min(175f, Math.Max(-175f, Flt(o, "flagFoldDeg", 0f)));
        int fff = Int(o, "flagFoldFrames", 12); a.flagFoldFrames = fff <= 0 ? 12 : fff;
        a.srcFile2 = (Str(o, "srcFile2", "") ?? "").Trim();
        var off = Vec(o, "model2Off", 0, 0, 0); a.model2OffX = off[0]; a.model2OffY = off[1]; a.model2OffZ = off[2];
        var rot = Vec(o, "model2Rot", 0, 0, 0); a.model2RotX = rot[0]; a.model2RotY = rot[1]; a.model2RotZ = rot[2];
        var sxyz = Vec(o, "model2ScaleXYZ", 1, 1, 1);
        var m2s = VehicleLabRules.Model2ScaleOnLoad(Flt(o, "model2Scale", 1f), sxyz[0], sxyz[1], sxyz[2]); a.model2ScaleX = m2s[0]; a.model2ScaleY = m2s[1]; a.model2ScaleZ = m2s[2];
        float b1 = Flt(o, "model1Bright", 1f), b2 = Flt(o, "model2Bright", 1f); a.model1Bright = b1 <= 0f ? 1f : b1; a.model2Bright = b2 <= 0f ? 1f : b2;
        a.tailAxisChoice = Int(o, "tailAxisChoice", 0); a.tailYawAdj = Flt(o, "tailYawAdj", 0f); a.tailPitchAdj = Flt(o, "tailPitchAdj", 0f);
        a.trailSpreadDeg = Flt(o, "trailSpreadDeg", 35f); a.trailFrames = Int(o, "trailFrames", 12); a.gunPivot = Flt(o, "gunPivot", 0.5f); a.gunDeployElev = Flt(o, "gunDeployElev", 0f);
        a.recoilDist = Flt(o, "recoilDist", 0f); a.recoilFrames = Int(o, "recoilFrames", 16); a.recoilLead = Int(o, "recoilLead", 0);
        a.waveEnabled = Bool(o, "waveEnabled", false); a.rockDegrees = Flt(o, "rockDegrees", 0f); a.rockAxisChoice = Int(o, "rockAxisChoice", 0); a.rockHeading = Flt(o, "rockHeading", 0f);
        a.rockPitchDeg = Flt(o, "rockPitchDeg", 2.4f); a.rockPitchPhase = Flt(o, "rockPitchPhase", 90f);
        int rf = Int(o, "rockFrames", 120); a.rockFrames = rf > 0 ? rf : 120;
        int rrc = Int(o, "rockRollCycles", 1); a.rockRollCycles = rrc > 0 ? rrc : 1;
        int rpc = Int(o, "rockPitchCycles", 1); a.rockPitchCycles = rpc > 0 ? rpc : 1;
        r.parts = Parts(o["parts"]); r.boneParts = Parts(o["boneParts"]);
        r.useSourceRig = Bool(o, "useSourceRig", false) && r.boneParts.Count > 0;
        a.fast = r.FastPath;
        return r;
    }

    static List<RecipePart> Parts(JToken t)
    {
        var list = new List<RecipePart>();
        if (t == null || t.Type == JTokenType.Null) return list;
        if (!(t is JArray arr)) throw new InvalidDataException("recipe parts must be an array");
        foreach (var item in arr)
        {
            if (!(item is JObject p)) throw new InvalidDataException("a recipe part must be an object");
            var off = Vec(p, "offset", 0, 0, 0); var sc = Vec(p, "scale", 1, 1, 1);
            list.Add(new RecipePart { name = Str(p, "name", "") ?? "", role = Int(p, "role", 0), offX = off[0], offY = off[1], offZ = off[2], scaleX = sc[0], scaleY = sc[1], scaleZ = sc[2] });
        }
        return list;
    }

    /// <summary>The names of a role's parts among the active list, in the recipe's order - a names file's lines.</summary>
    public static string[] NamesOf(Recipe r, string namesRole) => r.ActiveParts.Where(p => p.role == RoleOfNamesFile[namesRole]).Select(p => p.name).ToArray();

    /// <summary>The placement lines (VehicleLabRules.PartPlacementLine) of every placed part, in the recipe's order; empty when none is placed.</summary>
    public static string[] PlacementLines(Recipe r) => r.parts.Where(IsPlaced).Select(p => VehicleLabRules.PartPlacementLine(p.name, p.offX, p.offY, p.offZ, p.scaleX, p.scaleY, p.scaleZ)).ToArray();
}
