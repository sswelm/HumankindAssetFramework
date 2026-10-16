// VehicleRigDrill.cs - the C# side of tools/vehiclerig_drill.sh (replacing vehicle_rig.py, step (a): the stage dump).
//   vehiclerig.exe [--keys] <jobs file> <dump>...     exit 0 when every job's dump is complete and consistent
// A dump is what tools/vehiclerig-drill/blender_vehiclerig_dump.py printed: vehicle_rig.py run on each recipe job, stage by
// stage, the scene after each of its five stages and the GLB it exported read back (the X rows). This step holds the
// EVIDENCE, not yet a rule: every job named by the jobs file has one block, each block's stages run 1..k without a gap, a
// block that stopped short names the stage and why (the script's exit or death), every row has its fields and they parse,
// the rows agree with each other (a mesh row names an object, a weight row a group of that mesh, a pose bone a bone, an
// fcurve its action, an action its fcurve count), and a job that ran to its end - all five stages, or the fast path's exit 0
// - has its GLB rows. The census (COVER rows) says which of the script's branches the recipes take, by its own log lines;
// a branch at zero there is a fixture to write before its port. The parser is the rig_anim drill's (RigAnimDrill.cs), with
// five stages, VEHICLE lines, the fast path's exit 0, no albedo row, and the fcurve's SLOT in its identity (FC and K rows carry
// it after the action: a slotted action repeats a data path per slot - the dugout canoe's 'Take 001' does).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

public sealed class VehicleRigDump
{
    public sealed class Stage
    {
        public int N;
        public string[] Scene;
        public List<string[]> Objs = new List<string[]>(), Meshes = new List<string[]>(), Groups = new List<string[]>(), Bones = new List<string[]>(),
                              PoseBones = new List<string[]>(), Actions = new List<string[]>(), FCurves = new List<string[]>(), Keys = new List<string[]>();
        public List<string> Logs = new List<string>();
    }

    public const int StageCount = 5;
    public string Key;
    public bool Fast;                                 // the jobs file's FAST line: the source-skeleton path, exits 0 in stage 1 after its own export
    public string[] Argv;                             // the job's fields: argv[0] (the mode) onward
    public SortedDictionary<int, Stage> Stages = new SortedDictionary<int, Stage>();
    public int StoppedAt; public string Exit; public string Died;
    public List<string> LogsOfStop = new List<string>();
    public List<string[]> ExportRows = new List<string[]>();   // the GLB read back, the X stripped
    public string ExportDied;
    public IEnumerable<string> AllLogs => Stages.Values.SelectMany(s => s.Logs).Concat(LogsOfStop);
    public Stage Last => Stages.Count == 0 ? null : Stages[Stages.Keys.Max()];
    public bool Completed => Stages.Count == StageCount || (Fast && Exit == "0" && StoppedAt == 1);

    static readonly Dictionary<string, int> Width = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        { "JOB", 2 }, { "STAGE", 2 }, { "LOG", 3 }, { "SCENE", 6 }, { "OBJ", 42 }, { "MESH", 19 }, { "VG", 6 }, { "BONE", 23 },
        { "PB", 33 }, { "ACT", 7 }, { "FC", 9 }, { "K", 16 }, { "EXIT", 3 }, { "DIES", 3 }, { "DONE", 2 }, { "FAIL", 3 }, { "EVIDENCE", 4 },
    };
    static readonly HashSet<string> ExportKinds = new HashSet<string>(new[] { "GLB", "SCENE", "NODE", "SKIN", "IBM", "MESH", "PRIM", "MAT", "IMG", "TEX", "SAMP", "ANIM", "CHAN" }, StringComparer.Ordinal);

    static bool IsHex(string s, int n) => s.Length == n && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    static bool IsInt(string s) => long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    static void Floats(string[] t, int from, int count, string what)
    {
        for (int i = from; i < from + count; i++) if (!IsHex(t[i], 8)) throw new InvalidDataException($"{what} row has a value that is not float32 bits: '{t[i]}'");
    }
    static string FcKey(string[] row) => row[2] + "\u0001" + row[3] + "\u0001" + row[4] + "\u0001" + row[5];   // action, slot, data_path, index
    static byte[] LittleEndianBits(string hex) { var b = new byte[4]; for (int i = 0; i < 4; i++) b[i] = Convert.ToByte(hex.Substring((3 - i) * 2, 2), 16); return b; }
    static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

    public static VehicleRigDump Parse(List<string> block, Dictionary<string, (bool fast, string[] argv)> jobs, bool requireKeys)
    {
        var all = block.Select(l => l.Split('\t')).ToList();
        var rows = all.Where(t => t[0] != "X" && t[0] != "XDIES").ToList();
        if (rows[0][0] != "JOB" || rows[0].Length != 2 || rows[0][1].Length == 0) throw new InvalidDataException("the dump has an invalid JOB row");
        var d = new VehicleRigDump { Key = rows[0][1] };
        if (!jobs.TryGetValue(d.Key, out var job)) throw new InvalidDataException("the dump has a job the jobs file does not");
        d.Fast = job.fast; d.Argv = job.argv;
        var fail = rows.FirstOrDefault(t => t[0] == "FAIL");
        if (fail != null) throw new InvalidDataException("Blender could not run it: " + fail.Last());
        // the export rows: after the last stage row (or the stop), before DONE
        bool exportStarted = false, doneSeen = false;
        foreach (var t in all)
        {
            bool isExport = t[0] == "X" || t[0] == "XDIES";
            if (isExport)
            {
                if (doneSeen) throw new InvalidDataException("an export row follows DONE");
                exportStarted = true;
                if (t[0] == "XDIES") { if (t.Length != 2 || t[1].Length == 0 || d.ExportDied != null) throw new InvalidDataException("an invalid or repeated XDIES row"); d.ExportDied = t[1]; }
                else { if (t.Length < 2 || d.ExportDied != null) throw new InvalidDataException("an X row after the export died, or an empty one"); if (!ExportKinds.Contains(t[1])) throw new InvalidDataException("the export has an unknown row kind '" + t[1] + "'"); d.ExportRows.Add(t.Skip(1).ToArray()); }
            }
            else
            {
                if (exportStarted && t[0] != "DONE" && t[0] != "EVIDENCE") throw new InvalidDataException("a stage row follows the export rows");
                if (t[0] == "DONE") doneSeen = true;
            }
        }
        if (d.ExportDied != null && d.ExportRows.Count > 0) throw new InvalidDataException("the export both died and has GLB rows");
        foreach (var t in rows)
        {
            if (!Width.TryGetValue(t[0], out int w)) throw new InvalidDataException($"the dump has an unknown row kind '{t[0]}'");
            if (t.Length != w) throw new InvalidDataException($"{t[0]} row has {t.Length} fields, {w} expected");
        }
        var done = rows.Where(t => t[0] == "DONE").ToList();
        if (done.Count == 0) throw new InvalidDataException("the dump has no DONE row");
        if (done.Count != 1 || done[0][1] != d.Key || rows[rows.Count - 1] != done[0]) throw new InvalidDataException("the dump must end with exactly one matching DONE row");
        if (rows.Skip(1).Any(t => t[0] == "JOB")) throw new InvalidDataException("the dump repeats a JOB row inside a block");
        var stageRows = rows.Where(t => t[0] == "STAGE").Select(t => int.Parse(t[1], CultureInfo.InvariantCulture)).ToList();
        for (int i = 0; i < stageRows.Count; i++) if (stageRows[i] != i + 1) throw new InvalidDataException($"the stages are not 1..k in order: STAGE {stageRows[i]} at position {i + 1}");
        if (stageRows.Count > StageCount) throw new InvalidDataException("more than five stages");
        int k = stageRows.Count;
        var exits = rows.Where(t => t[0] == "EXIT").ToList(); var dies = rows.Where(t => t[0] == "DIES").ToList();
        if (exits.Count + dies.Count > 1) throw new InvalidDataException("the dump claims more than one stop (EXIT or DIES)");
        if (k == StageCount && exits.Count + dies.Count > 0) throw new InvalidDataException("the dump claims a stop after all five stages ran");
        if (k < StageCount && exits.Count + dies.Count == 0) throw new InvalidDataException($"the dump has {k} stage(s) and no EXIT or DIES row for the next");
        if (exits.Count == 1)
        {
            if (int.Parse(exits[0][1], CultureInfo.InvariantCulture) != k + 1) throw new InvalidDataException("the EXIT row names a stage other than the one after the last dumped");
            if (exits[0][2] == "0") { if (!d.Fast || k != 0) throw new InvalidDataException("an exit 0 belongs to the fast path alone, in stage 1"); }
            else if (exits[0][2] != "1") throw new InvalidDataException($"the script exits with 0 (the fast path) or 1 (an error); the dump says '{exits[0][2]}'");
            d.StoppedAt = k + 1; d.Exit = exits[0][2];
        }
        if (dies.Count == 1)
        {
            if (int.Parse(dies[0][1], CultureInfo.InvariantCulture) != k + 1) throw new InvalidDataException("the DIES row names a stage other than the one after the last dumped");
            if (dies[0][2].Length == 0) throw new InvalidDataException("the DIES row names no exception");
            d.StoppedAt = k + 1; d.Died = dies[0][2];
        }
        for (int n = 1; n <= k; n++) d.Stages[n] = new Stage { N = n };
        int current = 0;
        foreach (var t in rows)
        {
            if (t[0] == "JOB" || t[0] == "DONE" || t[0] == "FAIL" || t[0] == "EVIDENCE") continue;
            if (t[0] == "STAGE") { current = int.Parse(t[1], CultureInfo.InvariantCulture); continue; }
            int n = int.Parse(t[1], CultureInfo.InvariantCulture);
            if (t[0] == "LOG" || t[0] == "EXIT" || t[0] == "DIES")
            {
                if (n != current + 1 || n > k + 1) throw new InvalidDataException($"a {t[0]} row of stage {n} is out of place (after stage {current})");
                if (t[0] == "LOG")
                {
                    if (!t[2].StartsWith("VEHICLE", StringComparison.Ordinal)) throw new InvalidDataException("a LOG row that is not a VEHICLE line");
                    if (t[2].StartsWith("VEHICLE timing:", StringComparison.Ordinal)) throw new InvalidDataException("a LOG row that is a timing line (not evidence)");
                    if (n <= k) d.Stages[n].Logs.Add(t[2]); else d.LogsOfStop.Add(t[2]);
                }
                continue;
            }
            if (n != current) throw new InvalidDataException($"a {t[0]} row of stage {n} is not under its STAGE row (under {current})");
            var s = d.Stages[n];
            switch (t[0])
            {
                case "SCENE":
                    if (s.Scene != null) throw new InvalidDataException($"stage {n} has two SCENE rows");
                    if (!IsInt(t[2]) || !IsInt(t[3]) || !IsInt(t[4])) throw new InvalidDataException("SCENE row has a frame that is not an integer");
                    s.Scene = t.Skip(2).ToArray(); break;
                case "OBJ": Floats(t, 10, 32, "OBJ"); s.Objs.Add(t); break;
                case "MESH":
                    if (!IsInt(t[3]) || !IsInt(t[4]) || !IsInt(t[5])) throw new InvalidDataException("MESH row has a count that is not an integer");
                    for (int i = 6; i <= 9; i++) if (!IsHex(t[i], 64)) throw new InvalidDataException("MESH row has a hash that is not SHA-256");
                    if (t[10] != "-" && !IsHex(t[10], 64)) throw new InvalidDataException("MESH row has a UV hash that is not SHA-256");
                    if (!IsHex(t[15], 64)) throw new InvalidDataException("MESH row has an edge hash that is not SHA-256");
                    for (int i = 16; i <= 17; i++) if (t[i] != "-" && !IsHex(t[i], 64)) throw new InvalidDataException("MESH row has a sharp-edge or custom-normal hash that is not SHA-256");
                    if (t[18] != "-" && !t[18].Split(';').All(c => { var p = c.Split(':'); return p.Length >= 3 && IsHex(p[p.Length - 1], 64); })) throw new InvalidDataException("MESH row has a colour hash that is not SHA-256");
                    s.Meshes.Add(t); break;
                case "VG":
                    if (!IsInt(t[4]) || !IsHex(t[5], 64)) throw new InvalidDataException("VG row has an invalid count or hash");
                    s.Groups.Add(t); break;
                case "BONE":
                    if (!IsInt(t[2]) || (t[5] != "0" && t[5] != "1")) throw new InvalidDataException("BONE row has an invalid index or deform flag");
                    Floats(t, 6, 17, "BONE"); s.Bones.Add(t); break;
                case "PB": Floats(t, 4, 29, "PB"); s.PoseBones.Add(t); break;
                case "ACT":
                    Floats(t, 3, 2, "ACT");
                    if (!IsInt(t[5]) || !IsInt(t[6])) throw new InvalidDataException("ACT row has a count that is not an integer");
                    s.Actions.Add(t); break;
                case "FC":
                    if (!IsInt(t[5]) || !IsInt(t[6]) || !IsHex(t[7], 64) || !IsHex(t[8], 64)) throw new InvalidDataException("FC row has an invalid index, count or hash");
                    s.FCurves.Add(t); break;
                case "K": Floats(t, 6, 6, "K"); s.Keys.Add(t); break;
                default: throw new InvalidDataException($"the dump has an unknown row kind '{t[0]}'");
            }
        }
        foreach (var s in d.Stages.Values)
        {
            if (s.Scene == null) throw new InvalidDataException($"stage {s.N} has no SCENE row");
            if (s.Objs.Count == 0) throw new InvalidDataException($"stage {s.N} has no OBJ row");
            var objNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in s.Objs) if (!objNames.Add(o[2])) throw new InvalidDataException($"stage {s.N} has two objects named '{o[2]}'");
            foreach (var o in s.Objs) if (o[4] != "-" && !objNames.Contains(o[4])) throw new InvalidDataException($"stage {s.N}: object '{o[2]}' has a parent the stage does not list");
            var meshObjs = new HashSet<string>(s.Objs.Where(o => o[3] == "MESH").Select(o => o[2]), StringComparer.Ordinal);
            var meshNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in s.Meshes)
            {
                if (!meshObjs.Contains(m[2])) throw new InvalidDataException($"stage {s.N}: MESH row '{m[2]}' names no mesh object of the stage");
                if (!meshNames.Add(m[2])) throw new InvalidDataException($"stage {s.N}: two MESH rows for '{m[2]}'");
            }
            if (meshNames.Count != meshObjs.Count) throw new InvalidDataException($"stage {s.N}: {meshObjs.Count} mesh objects, {meshNames.Count} MESH rows");
            var groupsOf = s.Meshes.ToDictionary(m => m[2], m => m[12] == "-" ? new string[0] : m[12].Split(';'), StringComparer.Ordinal);
            var seenGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in s.Groups)
            {
                if (!groupsOf.TryGetValue(g[2], out var gs) || !gs.Contains(g[3])) throw new InvalidDataException($"stage {s.N}: VG row '{g[2]}'/'{g[3]}' names a group its mesh does not list");
                if (!seenGroups.Add(g[2] + "\u0001" + g[3])) throw new InvalidDataException($"stage {s.N}: two VG rows for '{g[2]}'/'{g[3]}'");
            }
            int expectGroups = groupsOf.Values.Sum(gs => gs.Length);
            if (s.Groups.Count != expectGroups) throw new InvalidDataException($"stage {s.N}: {expectGroups} vertex groups listed, {s.Groups.Count} VG rows");
            var armatures = s.Objs.Where(o => o[3] == "ARMATURE").ToList();
            if (armatures.Count == 0 && (s.Bones.Count > 0 || s.PoseBones.Count > 0)) throw new InvalidDataException($"stage {s.N}: bone rows without an armature object");
            if (armatures.Count > 0 && s.Bones.Count == 0) throw new InvalidDataException($"stage {s.N}: an armature without a BONE row");
            var boneNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < s.Bones.Count; i++)
            {
                if (int.Parse(s.Bones[i][2], CultureInfo.InvariantCulture) != i) throw new InvalidDataException($"stage {s.N}: BONE rows are not indexed 0..n-1 in order");
                if (!boneNames.Add(s.Bones[i][3])) throw new InvalidDataException($"stage {s.N}: two bones named '{s.Bones[i][3]}'");
            }
            foreach (var b in s.Bones) if (b[4] != "-" && !boneNames.Contains(b[4])) throw new InvalidDataException($"stage {s.N}: bone '{b[3]}' has a parent the stage does not list");
            var pbNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in s.PoseBones) if (!boneNames.Contains(p[2]) || !pbNames.Add(p[2])) throw new InvalidDataException($"stage {s.N}: PB row '{p[2]}' names no bone, or names one twice");
            if (pbNames.Count != boneNames.Count) throw new InvalidDataException($"stage {s.N}: {boneNames.Count} bones, {pbNames.Count} pose bones");
            var actNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in s.Actions) if (!actNames.Add(a[2])) throw new InvalidDataException($"stage {s.N}: two actions named '{a[2]}'");
            var fcOf = s.FCurves.GroupBy(f => f[2], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var f in s.FCurves) if (!actNames.Contains(f[2])) throw new InvalidDataException($"stage {s.N}: FC row names an action the stage does not list: '{f[2]}'");
            foreach (var a in s.Actions)
            {
                fcOf.TryGetValue(a[2], out int have);
                if (have != int.Parse(a[5], CultureInfo.InvariantCulture)) throw new InvalidDataException($"stage {s.N}: action '{a[2]}' says {a[5]} fcurves, {have} FC rows");
            }
            var fcKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in s.FCurves) if (!fcKeys.Add(FcKey(f))) throw new InvalidDataException($"stage {s.N}: two FC rows for '{f[4]}'[{f[5]}] of '{f[2]}' slot '{f[3]}'");
            foreach (var o in s.Objs) if (o[8] != "-" && !actNames.Contains(o[8])) throw new InvalidDataException($"stage {s.N}: object '{o[2]}' holds an action the stage does not list");
            if (requireKeys || s.Keys.Count > 0)
            {
                var keyRows = s.Keys.GroupBy(FcKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
                foreach (var id in keyRows.Keys) if (!fcKeys.Contains(id)) throw new InvalidDataException($"stage {s.N}: K row names no FC row");
                foreach (var f in s.FCurves)
                {
                    keyRows.TryGetValue(FcKey(f), out var rowsForCurve);
                    int have = rowsForCurve == null ? 0 : rowsForCurve.Count;
                    if (have != int.Parse(f[6], CultureInfo.InvariantCulture)) throw new InvalidDataException($"stage {s.N}: fcurve '{f[4]}'[{f[5]}] says {f[6]} keys, {have} K rows");
                    var co = new List<byte>(); var left = new List<byte>(); var right = new List<byte>(); var meta = new List<string>();
                    if (rowsForCurve != null) foreach (var krow in rowsForCurve)
                    {
                        co.AddRange(LittleEndianBits(krow[6])); co.AddRange(LittleEndianBits(krow[7]));
                        for (int i = 8; i <= 9; i++) left.AddRange(LittleEndianBits(krow[i]));
                        for (int i = 10; i <= 11; i++) right.AddRange(LittleEndianBits(krow[i]));
                        meta.Add(krow[12] + "/" + krow[13] + "/" + krow[14] + "/" + krow[15]);
                    }
                    var handles = new List<byte>(left.Count + right.Count); handles.AddRange(left); handles.AddRange(right); handles.AddRange(Encoding.UTF8.GetBytes(string.Join("|", meta)));
                    if (Hash(co.ToArray()) != f[7] || Hash(handles.ToArray()) != f[8]) throw new InvalidDataException($"stage {s.N}: K rows do not match FC hashes for '{f[4]}'[{f[5]}]");
                }
            }
        }
        // the GLB: a run to the end (five stages, or the fast path's exit 0) read its file back; a stop did not
        if (d.Completed)
        {
            if (d.ExportDied != null) throw new InvalidDataException("the export: " + d.ExportDied);
            if (d.ExportRows.Count == 0 || d.ExportRows[0][0] != "GLB") throw new InvalidDataException("the export: no GLB rows for a run that completed");
            ValidateExport(d.ExportRows);
        }
        else if (d.ExportRows.Count > 0 || d.ExportDied != null) throw new InvalidDataException("export rows on a run that stopped");
        var seals = all.Where(t => t[0] == "EVIDENCE").ToList();
        if (seals.Count != 1 || all.Count < 3 || all[all.Count - 2] != seals[0] || seals[0][1] != d.Key)
            throw new InvalidDataException("the dump must end with one matching EVIDENCE row before DONE");
        var seal = seals[0];
        string recorded = string.Join("\n", all.Take(all.Count - 2).Select(t => string.Join("\t", t))) + "\n";
        if (!IsInt(seal[2]) || long.Parse(seal[2], CultureInfo.InvariantCulture) != all.Count - 2 || !IsHex(seal[3], 64) || Hash(Encoding.UTF8.GetBytes(recorded)) != seal[3])
            throw new InvalidDataException("the evidence row count or checksum differs from the recorded job");
        return d;
    }

    // Validate the GLB row grammar and references as well as the complete-job checksum. The checksum prevents lost
    // unreferenced rows; these checks still reject malformed evidence when its envelope is otherwise consistent.
    static void ValidateExport(List<string[]> rows)
    {
        int Number(string text) { if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) throw new InvalidDataException("the export has an invalid non-negative integer"); return n; }
        void Need(bool ok, string why) { if (!ok) throw new InvalidDataException("the export: " + why); }
        var indexed = new HashSet<string>(new[] { "NODE", "SKIN", "MESH", "MAT", "IMG", "TEX", "SAMP", "ANIM" });
        var tables = indexed.ToDictionary(k => k, k => rows.Where(r => r[0] == k).ToList());
        void Ref(string text, string kind, bool optional = false) { if (optional && text == "-") return; Need(Number(text) < tables[kind].Count, "invalid " + kind + " reference"); }
        int[] Indices(string text) => text == "-" || text.Length == 0 ? new int[0] : text.Split(',').Select(Number).ToArray();
        foreach (var r in rows)
        {
            int expected = r[0] == "GLB" ? 5 : r[0] == "SCENE" ? 3 : r[0] == "SKIN" ? 5 : r[0] == "IBM" ? 19 :
                r[0] == "MESH" ? 4 : r[0] == "MAT" || r[0] == "SAMP" ? 3 : r[0] == "IMG" || r[0] == "TEX" || r[0] == "ANIM" ? 5 : r[0] == "CHAN" ? 12 : 0;
            if (expected > 0) Need(r.Length == expected, r[0] + " row has an invalid field count");
            if (r[0] == "NODE")
            {
                Need(r.Length >= 9, "NODE row has an invalid field count");
                int at = 6;
                foreach (int count in new[] { 3, 4, 3 })
                {
                    Need(at < r.Length, "NODE row has an invalid field count");
                    if (r[at] == "-") at++; else { Need(at + count <= r.Length, "NODE row has an invalid field count"); Floats(r, at, count, "export NODE"); at += count; }
                }
                Need(at == r.Length, "NODE row has an invalid field count");
            }
            if (r[0] == "PRIM") Need(r.Length >= 8, "PRIM row has an invalid field count");
        }
        foreach (var table in tables) for (int i = 0; i < table.Value.Count; i++) Need(Number(table.Value[i][1]) == i, table.Key + " indices are not contiguous or unique");
        Need(rows.Count(r => r[0] == "GLB") == 1, "must have exactly one GLB header");
        var header = rows[0]; int bytes = Number(header[1]), json = Number(header[2]), bin = Number(header[3]);
        Need(json > 0 && json % 4 == 0 && bin % 4 == 0 && ((long)bytes == 28L + json + bin || bin == 0 && (long)bytes == 20L + json) && IsHex(header[4], 64), "invalid GLB size or hash");
        Need(rows.Any(r => r[0] == "SCENE"), "missing SCENE rows");
        foreach (var r in rows)
        {
            switch (r[0])
            {
                case "SCENE": foreach (int i in Indices(r[2])) Ref(i.ToString(CultureInfo.InvariantCulture), "NODE"); break;
                case "NODE": foreach (int i in Indices(r[3])) Ref(i.ToString(CultureInfo.InvariantCulture), "NODE"); Ref(r[4], "MESH", true); Ref(r[5], "SKIN", true); break;
                case "SKIN":
                    var joints = Indices(r[3]); Need(joints.Length > 0 && joints.Distinct().Count() == joints.Length, "invalid SKIN joints");
                    foreach (int i in joints) Ref(i.ToString(CultureInfo.InvariantCulture), "NODE"); Ref(r[4], "NODE", true); break;
                case "IBM": Ref(r[1], "SKIN"); Floats(r, 3, 16, "export IBM"); break;
                case "MESH": Number(r[3]); break;
                case "PRIM":
                    Ref(r[1], "MESH"); Number(r[2]); Ref(r[3], "MAT", true); Need(Number(r[4]) <= 6, "invalid primitive mode");
                    int indices = Number(r[5]); Need(indices == 0 && r[6] == "-" || IsHex(r[6], 64), "invalid index hash");
                    var attributes = new HashSet<string>();
                    for (int i = 7; i < r.Length; i++)
                    {
                        var a = r[i].Split(':'); Need(a.Length == 4 && attributes.Add(a[0]), "invalid or duplicate primitive attribute"); Number(a[1]);
                        Need(System.Text.RegularExpressions.Regex.IsMatch(a[2], @"^(SCALAR|VEC[234]|MAT[234])/(5120|5121|5122|5123|5125|5126)n?$"), "invalid attribute type"); Need(IsHex(a[3], 64), "invalid attribute hash");
                    }
                    Need(attributes.Contains("POSITION"), "primitive has no POSITION attribute"); break;
                case "MAT": case "SAMP": JObject.Parse(r[2]); break;
                case "IMG": Need(IsHex(r[4], 64), "invalid image hash"); break;
                case "TEX": Ref(r[2], "IMG", true); Ref(r[3], "SAMP", true); JObject.Parse(r[4]); break;
                case "ANIM": Number(r[3]); Number(r[4]); break;
                case "CHAN":
                    Ref(r[1], "ANIM"); Number(r[2]); Ref(r[3], "NODE", true); Need(new[] { "translation", "rotation", "scale", "weights" }.Contains(r[4]), "invalid animation path");
                    Need(Number(r[5]) < Number(tables["ANIM"][Number(r[1])][4]), "invalid animation sampler"); Need(new[] { "LINEAR", "STEP", "CUBICSPLINE" }.Contains(r[6]), "invalid interpolation");
                    Need(Number(r[7]) > 0, "empty channel inputs"); Floats(r, 8, 2, "export CHAN"); Need(IsHex(r[10], 64) && IsHex(r[11], 64), "invalid channel hash"); break;
            }
        }
        void Children(string parentKind, string childKind, int countField)
        {
            foreach (var parent in tables[parentKind])
            {
                var children = rows.Where(r => r[0] == childKind && r[1] == parent[1]).ToList();
                Need(children.Count == Number(parent[countField]), "missing or duplicate " + childKind + " rows");
                for (int i = 0; i < children.Count; i++) Need(Number(children[i][2]) == i, childKind + " indices are not contiguous or unique");
            }
        }
        Children("MESH", "PRIM", 3); Children("ANIM", "CHAN", 3);
        foreach (var skin in tables["SKIN"])
        {
            var ibms = rows.Where(r => r[0] == "IBM" && r[1] == skin[1]).ToList();
            if (ibms.Count == 0) continue;   // inverseBindMatrices may be absent in a GLB; the job checksum detects their loss
            Need(ibms.Count == Indices(skin[3]).Length, "missing or duplicate IBM rows");
            for (int i = 0; i < ibms.Count; i++) Need(Number(ibms[i][2]) == i, "IBM indices are not contiguous or unique");
        }
    }
}

static class VehicleRigDrill
{
    // the script's branches, by its own log lines (and a few by the dump's shape)
    static readonly (string key, Func<VehicleRigDump, bool> test)[] Cover =
    {
        ("the fast path (rigfast: the source skeleton)", d => d.Fast),
        ("the mesh path (rig)", d => !d.Fast),
        ("a second model merged", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE MERGE:"))),
        ("per-part placement", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE PLACED ("))),
        ("the brightness factor", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE brightness x"))),
        ("modifiers baked", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE modifiers baked:"))),
        ("the flatten", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE flatten:"))),
        ("the orientation straightened", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE orientation: straightened"))),
        ("parts ignored (deleted)", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE ignored:"))),
        ("a reduction tier run", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE ") && l.Contains(" -> ") && l.Contains(" verts (welded from"))),
        ("the RIGGING tier", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE RIGGING:"))),
        ("the PRESERVE role", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE PRESERVE:"))),
        ("the RUDDER role", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE RUDDER:"))),
        ("the FLIP role", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE FLIP:"))),
        ("the SAIL bone", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE SAIL:"))),
        ("the SAIL fold bands", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE SAIL fold bands"))),
        ("the FLAG bone", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE FLAG:"))),
        ("the FLAG clip", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE FLAG clip:"))),
        ("the oar bank (ROWING)", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE ROWING"))),
        ("the tread (tracks)", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE tread"))),
        ("the tread conveyor", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE tread conveyor"))),
        ("tracks STATIC (isolation)", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE tracks STATIC"))),
        ("the idler", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE idler"))),
        ("the rotor hub", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE rotor hub:"))),
        ("the gun pivot", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE gun pivot"))),
        ("the gun span (GUNSPAN)", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE GUNSPAN"))),
        ("the muzzle", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE MUZZLE"))),
        ("the recoil clip", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE RECOIL clip:"))),
        ("the recoil arm", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE RECOIL ARM:"))),
        ("the trails (Deploy)", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE TRAIL") || l.StartsWith("VEHICLE trail "))),
        ("hybrid bones", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE hybrid bones:"))),
        ("the armature built", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE armature:"))),
        ("the join per bone", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE join:"))),
        ("rolling-contact wheel speeds", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE rolling-contact"))),
        ("the wave rock", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE wave rock:"))),
        ("a leftover source clip purged", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE purged leftover source clip"))),
        ("the inside-out fix", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE inside-out fix:"))),
        ("a FLIP applied at export", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE FLIP applied:"))),
        ("double-sided at export", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE double-sided"))),
        ("a WARN line", d => d.AllLogs.Any(l => l.StartsWith("VEHICLE WARN:"))),
        ("the script's exit (a VEHICLE ERROR)", d => d.Exit == "1"),
        ("the script died", d => d.Died != null),
        ("all five stages ran", d => d.Stages.Count == 5),
        ("the GLB read back", d => d.ExportRows.Count > 0),
        ("an animation in the GLB", d => d.ExportRows.Any(r => r[0] == "ANIM")),
        ("more than one animation in the GLB", d => d.ExportRows.Count(r => r[0] == "ANIM") > 1),
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        bool requireKeys = false; int offset = 0; int stages = 0;   // --stages N: the C# model of stages 1..N (editor/BlenderVehicleRig.cs) against the dump
        const string usage = "usage: vehiclerig.exe [--keys] [--stages 1|2|3] <jobs file> <dump>...";
        while (offset < args.Length && args[offset].StartsWith("--", StringComparison.Ordinal))
        {
            if (args[offset] == "--keys") requireKeys = true;
            else if (args[offset] == "--stages" && offset + 1 < args.Length && int.TryParse(args[offset + 1], NumberStyles.None, CultureInfo.InvariantCulture, out stages) && stages >= 1 && stages <= 3) offset++;
            else { Console.WriteLine(usage); return 2; }
            offset++;
        }
        if (args.Length < offset + 2) { Console.WriteLine(usage); return 2; }
        var jobs = new Dictionary<string, (bool fast, string[] argv)>(StringComparer.Ordinal);
        var leftByName = new List<string>();
        foreach (var raw in File.ReadAllLines(args[offset]))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var t = line.Split('\t');
            if (t[0] == "LEFT" && t.Length == 3) { leftByName.Add(t[1] + ": " + t[2]); continue; }
            if ((t[0] != "JOB" && t[0] != "FAST") || t.Length < 78 || t[1].Length == 0 || jobs.ContainsKey(t[1])) { Console.WriteLine("FAIL jobs: an invalid or duplicate job definition"); return 1; }
            if (t[2] != (t[0] == "FAST" ? "rigfast" : "rig")) { Console.WriteLine("FAIL jobs: a FAST line must run rigfast, a JOB line rig"); return 1; }
            jobs.Add(t[1], (t[0] == "FAST", t.Skip(2).ToArray()));
        }
        if (jobs.Count == 0) { Console.WriteLine("FAIL jobs: no vehicle_rig jobs to read"); return 1; }
        var blocks = new List<List<string>>();
        int fails = 0;
        foreach (var dump in args.Skip(offset + 1))
            foreach (var raw in File.ReadLines(dump))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line.StartsWith("JOB\t", StringComparison.Ordinal)) blocks.Add(new List<string>());
                if (blocks.Count > 0) blocks[blocks.Count - 1].Add(line);
                else { fails++; Console.WriteLine("FAIL dump: a row before the first JOB"); }
            }
        var cover = new SortedDictionary<string, long>(StringComparer.Ordinal); foreach (var c in Cover) cover[c.key] = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long stageRows = 0, logs = 0, objs = 0, meshes = 0, groups = 0, bones = 0, fcurves = 0, keys = 0, glbRows = 0; int stopped = 0, full = 0, fast = 0;
        foreach (var block in blocks)
        {
            string key = block[0].Split('\t').Length > 1 ? block[0].Split('\t')[1] : "?";
            try
            {
                if (!seen.Add(key)) throw new InvalidDataException("the dump repeats a job");
                var d = VehicleRigDump.Parse(block, jobs, requireKeys);
                foreach (var c in Cover) if (c.test(d)) cover[c.key]++;
                stageRows += d.Stages.Count; logs += d.AllLogs.Count(); glbRows += d.ExportRows.Count;
                foreach (var s in d.Stages.Values) { objs += s.Objs.Count; meshes += s.Meshes.Count; groups += s.Groups.Count; bones += s.Bones.Count; fcurves += s.FCurves.Count; keys += s.Keys.Count; }
                // EXIT:<stage>: keys name the expected script exit stage (a fixture); every other job must run to its end
                // DIES:<stage>: keys name the expected stage of the script's own death (an uncaught exception: a fixture)
                bool expectStop = key.StartsWith("EXIT:", StringComparison.Ordinal), expectDie = key.StartsWith("DIES:", StringComparison.Ordinal);
                int expectedStage = 0;
                if (expectStop || expectDie)
                {
                    int colon = key.IndexOf(':', 5);
                    if (colon < 0 || !int.TryParse(key.Substring(5, colon - 5), NumberStyles.None, CultureInfo.InvariantCulture, out expectedStage) || expectedStage < 1 || expectedStage > VehicleRigDump.StageCount)
                        throw new InvalidDataException((expectStop ? "EXIT:" : "DIES:") + " key must name its expected stage (EXIT:<stage>:<name>, DIES:<stage>:<name>)");
                }
                if (d.Fast && d.Completed)
                {
                    if (expectStop || expectDie) { fails++; Console.WriteLine($"FAIL {key}: expected to stop (its key says EXIT: or DIES:), ran the fast path to its export"); }
                    else { fast++; Console.WriteLine($"OK {key}: the fast path (rigfast) exported, {d.AllLogs.Count()} log lines, {d.ExportRows.Count} GLB rows"); }
                }
                else if (d.StoppedAt > 0)
                {
                    stopped++;
                    string how = d.Exit != null ? "exits " + d.Exit + ": " + (d.LogsOfStop.LastOrDefault(l => l.StartsWith("VEHICLE ERROR")) ?? "(no VEHICLE ERROR line)") : "dies: " + d.Died;
                    if (expectStop && d.Exit == "1" && d.StoppedAt == expectedStage && d.LogsOfStop.Any(l => l.StartsWith("VEHICLE ERROR", StringComparison.Ordinal)))
                        Console.WriteLine($"OK {key}: stops at stage {d.StoppedAt} as expected; {how}");
                    else if (expectDie && d.Died != null && d.StoppedAt == expectedStage)
                        Console.WriteLine($"OK {key}: dies in stage {d.StoppedAt} as expected; {how}");
                    else { fails++; Console.WriteLine($"FAIL {key}: stopped at stage {d.StoppedAt}" + (expectStop ? $", expected to stop at stage {expectedStage} with the script's VEHICLE ERROR; " : expectDie ? $", expected to die in stage {expectedStage}; " : "; ") + how); }
                }
                else if (expectStop || expectDie) { fails++; Console.WriteLine($"FAIL {key}: expected to stop (its key says EXIT: or DIES:), ran through all five stages"); }
                else
                {
                    full++;
                    var last = d.Last;
                    Console.WriteLine($"OK {key}: 5 stages, {d.AllLogs.Count()} log lines; at the end {last.Objs.Count} objects, {last.Meshes.Count} mesh(es), {last.Bones.Count} bones, {last.Actions.Count} action(s), {last.FCurves.Count} fcurves; {d.ExportRows.Count} GLB rows");
                }
                if (stages > 0 && !StageDrill.Compare(d, key, stages)) fails++;
            }
            catch (InvalidDataException e) { fails++; Console.WriteLine($"FAIL {key}: {e.Message}"); }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {key}: {e.GetType().Name}: {e.Message}"); }
        }
        foreach (var k in jobs.Keys.Where(k => !seen.Contains(k))) { fails++; Console.WriteLine($"FAIL {k}: the jobs file names it, the dump has no block for it"); }
        foreach (var l in leftByName) Console.WriteLine("LEFT " + l);
        foreach (var kv in cover) Console.WriteLine($"COVER {kv.Value} {kv.Key}");
        if (stages > 0) StageDrill.Summary(stages);
        Console.WriteLine($"TOTAL jobs {blocks.Count} full {full} fast {fast} stopped {stopped} left {leftByName.Count} stages {stageRows} logs {logs} objects {objs} meshes {meshes} groups {groups} bones {bones} fcurves {fcurves} keys {keys} glbrows {glbRows}");
        if (fails > 0) { Console.WriteLine($"FAIL — {fails} problem(s)"); return 1; }
        Console.WriteLine($"PASS — vehicle_rig stage dump: {blocks.Count} jobs dumped by Blender, {full} through all five stages, {fast} on the fast path, {stopped} stopped by the script as it says; {leftByName.Count} recipe(s) left by name" + (leftByName.Count > 0 ? " (" + string.Join("; ", leftByName) + ")" : ""));
        return 0;
    }
}

/// <summary>Step (b): the stage 1 model (BlenderVehicleRig.RunStage1) against the dump, every row of stage 1 as a string - the
/// dump's own encoding, float32 bits and SHA-256 hashes - so that equal means equal to the bit. A job the model leaves to
/// Blender by name is counted LEFT and said; the census (MODEL rows) names the branches the jobs took and the reasons left.</summary>
static class StageDrill
{
    public static int Compared, Equal, Left, Left3; public static long RowsEqual;
    static readonly SortedDictionary<string, int> Census = new SortedDictionary<string, int>(StringComparer.Ordinal);

    static float FromHex(string h) => BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(h, 16)), 0);
    static string Show(string f) => f.Length == 8 && f.All(c => Uri.IsHexDigit(c)) ? f + " (" + FromHex(f).ToString("R", CultureInfo.InvariantCulture) + ")" : f.Length == 64 ? "a hash " + f.Substring(0, 12) + "…" : "'" + f + "'";

    static string Diff(string kind, List<string[]> mine, List<string[]> theirs, int idFields)
    {
        int n = Math.Min(mine.Count, theirs.Count);
        for (int i = 0; i < n; i++)
        {
            var a = mine[i]; var b = theirs[i];
            string id = string.Join("/", b.Skip(2).Take(idFields));
            if (a.Length != b.Length) return $"{kind} row {i} ({id}): {a.Length} fields here, {b.Length} in Blender";
            for (int j = 0; j < a.Length; j++)
                if (a[j] != b[j]) return $"{kind} row {i} ({string.Join("/", a.Skip(2).Take(idFields))} here, {id} in Blender) field {j}: here {Show(a[j])}, Blender {Show(b[j])}";
        }
        if (mine.Count != theirs.Count) return $"{kind}: {mine.Count} rows here, {theirs.Count} in Blender" + (mine.Count > n ? $"; the first extra here: {string.Join("/", mine[n].Skip(2).Take(idFields))}" : $"; the first extra in Blender: {string.Join("/", theirs[n].Skip(2).Take(idFields))}");
        return null;
    }

    static string ExplainKeys(string[] fcMine, List<string[]> keysMine, VehicleRigDump.Stage s)
    {
        if (s.Keys.Count == 0) return " (VEHICLERIG_KEYS=1 shows the key)";
        string Id(string[] k) => k[2] + "\u0001" + k[3] + "\u0001" + k[4] + "\u0001" + k[5];
        var mine = keysMine.Where(k => Id(k) == Id(fcMine)).ToList(); var theirs = s.Keys.Where(k => Id(k) == Id(fcMine)).ToList();
        string d = Diff("K", mine, theirs, 4);
        return d == null ? " (every K row equal: the hash itself differs)" : "; " + d;
    }

    static void CompareRows(int n, BlenderRigAnim.Rows rows, List<string> logs, VehicleRigDump.Stage s, List<string> problems)
    {
        string N = "stage " + n + " ";
        if (!s.Logs.SequenceEqual(logs)) problems.Add(N + "LOG lines differ: here [" + string.Join(" | ", logs) + "], Blender [" + string.Join(" | ", s.Logs) + "]");
        if (!s.Scene.SequenceEqual(rows.Scene.Skip(2))) problems.Add(N + "SCENE differs: here [" + string.Join(" ", rows.Scene.Skip(2)) + "], Blender [" + string.Join(" ", s.Scene) + "]");
        string x;
        if ((x = Diff("OBJ", rows.Objs, s.Objs, 1)) != null) problems.Add(N + x);
        if ((x = Diff("MESH", rows.Meshes, s.Meshes, 1)) != null) problems.Add(N + x);
        if ((x = Diff("VG", rows.Groups, s.Groups, 2)) != null) problems.Add(N + x);
        if ((x = Diff("BONE", rows.Bones, s.Bones, 2)) != null) problems.Add(N + x);
        if ((x = Diff("PB", rows.PoseBones, s.PoseBones, 1)) != null) problems.Add(N + x);
        if ((x = Diff("ACT", rows.Actions, s.Actions, 1)) != null) problems.Add(N + x);
        if ((x = Diff("FC", rows.FCurves, s.FCurves, 4)) != null)
        {
            int at = -1; for (int i = 0; i < Math.Min(rows.FCurves.Count, s.FCurves.Count) && at < 0; i++) if (!rows.FCurves[i].SequenceEqual(s.FCurves[i])) at = i;
            problems.Add(N + x + (at >= 0 && rows.FCurves[at].Take(6).SequenceEqual(s.FCurves[at].Take(6)) ? ExplainKeys(rows.FCurves[at], rows.Keys, s) : ""));
        }
        if (s.Keys.Count > 0 && (x = Diff("K", rows.Keys, s.Keys, 4)) != null) problems.Add(N + x);
    }

    /// <summary>VEHICLERIG_MODEL_ROWS=&lt;file&gt; writes the model's rendered rows of a stage (for the job VEHICLERIG_MODEL_JOB, or every
    /// job) in the dump's own format, to be diffed against the dump when a row differs.</summary>
    static void DumpModelRows(string key, int n, BlenderRigAnim.Rows rows, List<string> logs)
    {
        string path = Environment.GetEnvironmentVariable("VEHICLERIG_MODEL_ROWS"); string only = Environment.GetEnvironmentVariable("VEHICLERIG_MODEL_JOB");
        if (string.IsNullOrEmpty(path) || (!string.IsNullOrEmpty(only) && only != key)) return;
        var sb = new StringBuilder();
        sb.Append("JOB").Append('\t').Append(key).Append('\n');
        foreach (var l in logs) sb.Append("LOG").Append('\t').Append(n).Append('\t').Append(l).Append('\n');
        sb.Append("STAGE").Append('\t').Append(n).Append('\n');
        sb.Append(string.Join("\t", rows.Scene)).Append('\n');
        foreach (var list in new[] { rows.Objs, rows.Meshes, rows.Groups, rows.Bones, rows.PoseBones, rows.Actions, rows.FCurves, rows.Keys })
            foreach (var r in list) sb.Append(string.Join("\t", r)).Append('\n');
        File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static long RowCount(VehicleRigDump.Stage s) => 1 + s.Objs.Count + s.Meshes.Count + s.Groups.Count + s.Bones.Count + s.PoseBones.Count + s.Actions.Count + s.FCurves.Count + s.Keys.Count;
    static string Describe(VehicleRigDump.Stage s) => $"{s.Objs.Count} objects, {s.Meshes.Count} meshes, {s.Groups.Count} vertex groups, {s.Bones.Count} bones, {s.Actions.Count} actions, {s.FCurves.Count} fcurves" + (s.Keys.Count > 0 ? $", {s.Keys.Count} keys" : "");

    public static bool Compare(VehicleRigDump d, string key, int stages)
    {
        Compared++;
        var problems = new List<string>();
        BlenderRigAnim.Stage1 model; BlenderVehicleRig.Args args;
        try
        {
            args = BlenderVehicleRig.ParseArgs(d.Argv);
            var m = BlenderVehicleRig.SourceFallback(args) == null ? GlbReader.Read(args.Input) : null;   // a .fbx/.obj/.blend source: not read, the model declines it by name
            model = BlenderVehicleRig.RunStage1(m, args, GlbReader.Read);
        }
        catch (Exception e) { Console.WriteLine($"FAIL {key}: stage 1 model: {e.GetType().Name}: {e.Message}"); return false; }
        foreach (var note in model.Notes) Census[note] = (Census.TryGetValue(note, out int c) ? c : 0) + 1;
        if (model.Fallback != null)
        {
            Census["LEFT: " + model.Fallback] = (Census.TryGetValue("LEFT: " + model.Fallback, out int c) ? c : 0) + 1;
            // left by NAME: a source the C# does not read (until the native readers); every other decline is a model that is wrong
            bool named = BlenderVehicleRig.SourceFallback(args) != null || model.Fallback.StartsWith("the source-skeleton fast path", StringComparison.Ordinal);
            if (!named) { Console.WriteLine($"FAIL {key}: stage 1 model leaves this job to Blender: {model.Fallback}"); return false; }
            Left++; Console.WriteLine($"STAGE1 {key}: left to Blender by name: {model.Fallback}"); return true;
        }
        var rows = model.Exits || model.Dies != null ? null : BlenderRigAnim.Render(model, 1, fcSlot: true);
        if (rows != null) DumpModelRows(key, 1, rows, model.Logs);
        if (model.Dies != null)
        {
            // the model dies here (an uncaught exception in the script): Blender must have died in stage 1 after the same lines
            Census["the script's death: " + model.Dies.Split(':')[0]] = (Census.TryGetValue("the script's death: " + model.Dies.Split(':')[0], out int cd) ? cd : 0) + 1;
            if (d.StoppedAt != 1 || d.Died == null) problems.Add("stage 1 model: the script dies in stage 1 here (" + model.Dies + "), " + (d.StoppedAt == 0 ? "Blender ran it through" : d.Died != null ? $"Blender died in stage {d.StoppedAt}" : $"Blender exited {d.Exit} in stage {d.StoppedAt}"));
            else if (d.Died != model.Dies) problems.Add("stage 1 model: the death differs: here '" + model.Dies + "', Blender '" + d.Died + "'");
            else if (!d.LogsOfStop.SequenceEqual(model.Logs)) problems.Add("stage 1 model: the lines before the death differ: here [" + string.Join(" | ", model.Logs) + "], Blender [" + string.Join(" | ", d.LogsOfStop) + "]");
            if (problems.Count == 0) { RowsEqual += model.Logs.Count; Equal++; Console.WriteLine($"STAGE1 {key}: equal - the script dies in stage 1 ({d.Died}), {model.Logs.Count} VEHICLE lines before it"); }
        }
        else if (model.Exits)
        {
            if (d.StoppedAt != 1 || d.Exit != "1") problems.Add("stage 1 model: the script exits in stage 1 here, " + (d.StoppedAt == 0 ? "Blender ran it through" : $"Blender stopped at stage {d.StoppedAt}" + (d.Died != null ? " by dying" : "")));
            else if (!d.LogsOfStop.SequenceEqual(model.Logs)) problems.Add("stage 1 model: the stop's VEHICLE lines differ: here [" + string.Join(" | ", model.Logs) + "], Blender [" + string.Join(" | ", d.LogsOfStop) + "]");
        }
        else if (!d.Stages.TryGetValue(1, out var s)) problems.Add("stage 1 model: the scene runs on here, Blender stopped in stage 1: " + (d.Exit != null ? string.Join(" | ", d.LogsOfStop) : d.Died));
        else
        {
            CompareRows(1, rows, model.Logs, s, problems);
            if (problems.Count == 0) { RowsEqual += RowCount(s); Equal++; Console.WriteLine($"STAGE1 {key}: equal - {Describe(s)}"); }
        }
        if (problems.Count == 0 && model.Exits) { RowsEqual += model.Logs.Count; Equal++; Console.WriteLine($"STAGE1 {key}: equal - the script's exit in stage 1, {model.Logs.Count} VEHICLE lines"); }
        // stage 2 on the scene stage 1 left (step (c)): the reduction tiers; any decline is a FAIL
        if (problems.Count == 0 && stages >= 2 && !model.Exits && model.Dies == null)
        {
            int notes0 = model.Notes.Count;
            try { BlenderVehicleRig.RunStage2(model, args); }
            catch (Exception e) { problems.Add($"stage 2 model: {e.GetType().Name}: {e.Message}"); }
            for (int i = notes0; i < model.Notes.Count; i++) Census[model.Notes[i]] = (Census.TryGetValue(model.Notes[i], out int c2) ? c2 : 0) + 1;
            if (problems.Count == 0 && model.Fallback != null)
            {
                Census["LEFT: " + model.Fallback] = (Census.TryGetValue("LEFT: " + model.Fallback, out int c) ? c : 0) + 1;
                // part 2 ported the dissolve: no stage 2 decline is named any more
                Console.WriteLine($"FAIL {key}: stage 2 model leaves this job to Blender: {model.Fallback}"); return false;
            }
            if (problems.Count == 0)
            {
                if (model.Dies != null)
                {
                    if (d.StoppedAt != 2 || d.Died != model.Dies) problems.Add("stage 2 model: the death differs: here '" + model.Dies + "', Blender '" + d.Died + "' in stage " + d.StoppedAt);
                    else if (!d.LogsOfStop.SequenceEqual(model.Logs2)) problems.Add("stage 2 model: the lines before the death differ");
                    else { RowsEqual += model.Logs2.Count; Console.WriteLine($"STAGE2 {key}: equal - the script dies in stage 2 ({d.Died}), {model.Logs2.Count} VEHICLE lines before it"); }
                }
                else if (!d.Stages.TryGetValue(2, out var s2)) problems.Add("stage 2 model: the scene runs on here, Blender stopped in stage 2: " + (d.Exit != null ? string.Join(" | ", d.LogsOfStop) : d.Died));
                else
                {
                    var rows2 = BlenderRigAnim.Render(model, 2, fcSlot: true);
                    DumpModelRows(key, 2, rows2, model.Logs2);
                    CompareRows(2, rows2, model.Logs2, s2, problems);
                    if (problems.Count == 0) { RowsEqual += RowCount(s2); Console.WriteLine($"STAGE2 {key}: equal - {Describe(s2)}"); }
                }
            }
        }
        // stage 3 on the scene stage 2 left (step (d)): the rig; a decline naming a branch a later part ports ("stage 3: the ... is not
        // modelled yet") is LEFT, any other decline a FAIL
        if (problems.Count == 0 && stages >= 3 && !model.Exits && model.Dies == null)
        {
            int notes0 = model.Notes.Count;
            try { BlenderVehicleRig.RunStage3(model, args); }
            catch (Exception e) { problems.Add($"stage 3 model: {e.GetType().Name}: {e.Message}"); }
            for (int i = notes0; i < model.Notes.Count; i++) Census[model.Notes[i]] = (Census.TryGetValue(model.Notes[i], out int c3) ? c3 : 0) + 1;
            if (problems.Count == 0 && model.Fallback != null)
            {
                Census["LEFT: " + model.Fallback] = (Census.TryGetValue("LEFT: " + model.Fallback, out int c) ? c : 0) + 1;
                bool named3 = model.Fallback.StartsWith("stage 3: the ", StringComparison.Ordinal) && model.Fallback.EndsWith(" is not modelled yet", StringComparison.Ordinal);
                if (!named3) { Console.WriteLine($"FAIL {key}: stage 3 model leaves this job to Blender: {model.Fallback}"); return false; }
                Equal--; Left++; Left3++; Console.WriteLine($"STAGE3 {key}: left to Blender by name: {model.Fallback}"); return true;
            }
            if (problems.Count == 0)
            {
                if (model.Exits)
                {
                    if (d.StoppedAt != 3 || d.Exit != "1") problems.Add("stage 3 model: the script exits in stage 3 here, " + (d.StoppedAt == 0 ? "Blender ran it through" : $"Blender stopped at stage {d.StoppedAt}" + (d.Died != null ? " by dying" : "")));
                    else if (!d.LogsOfStop.SequenceEqual(model.Logs3)) problems.Add("stage 3 model: the stop's VEHICLE lines differ: here [" + string.Join(" | ", model.Logs3) + "], Blender [" + string.Join(" | ", d.LogsOfStop) + "]");
                    else { RowsEqual += model.Logs3.Count; Console.WriteLine($"STAGE3 {key}: equal - the script's exit in stage 3, {model.Logs3.Count} VEHICLE lines"); }
                }
                else if (!d.Stages.TryGetValue(3, out var s3)) problems.Add("stage 3 model: the scene runs on here, Blender stopped in stage 3: " + (d.Exit != null ? string.Join(" | ", d.LogsOfStop) : d.Died));
                else
                {
                    var rows3 = BlenderRigAnim.Render(model, 3, fcSlot: true);
                    DumpModelRows(key, 3, rows3, model.Logs3);
                    CompareRows(3, rows3, model.Logs3, s3, problems);
                    if (problems.Count == 0) { RowsEqual += RowCount(s3); Console.WriteLine($"STAGE3 {key}: equal - {Describe(s3)}"); }
                }
            }
        }
        if (problems.Count > 0) { foreach (var p in problems) Console.WriteLine($"FAIL {key}: {p}"); return false; }
        return true;
    }

    public static void Summary(int stages)
    {
        foreach (var kv in Census) Console.WriteLine($"MODEL {kv.Value} {kv.Key}");
        Console.WriteLine($"STAGES {stages} jobs {Compared} equal {Equal} left {Left} rows {RowsEqual} left3 {Left3}");
    }
}
