// RigAnimDrill.cs - the C# side of tools/riganim_drill.sh (replacing rig_anim.py, step (a): the stage dump).
//   riganim.exe <jobs file> <dump>...     exit 0 when every job's dump is complete and consistent
// A dump is what tools/riganim-drill/blender_riganim_dump.py printed: rig_anim.py run on each registry job, stage by
// stage, the scene after each stage. This step holds the EVIDENCE, not yet a rule: every job named by the jobs file has
// one block, each block's stages run 1..k without a gap, a block that stopped short names the stage and why (the script's
// exit or death), every row has its fields and they parse, and the rows agree with each other (a mesh row names an object,
// a weight row a group of that mesh, a pose bone a bone, an fcurve its action, an action its fcurve count). The census
// (COVER rows) says which of the script's branches the registry's recipes take, by the script's own log lines - the
// stage ports (b)..(e) read this dump through RigAnimDump and hold their rules against it; a branch at zero there is a
// fixture to write first.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public sealed class RigAnimDump
{
    public sealed class Stage
    {
        public int N;
        public string[] Scene;                       // frame_start, frame_end, frame_current, pose_position
        public List<string[]> Objs = new List<string[]>();
        public List<string[]> Meshes = new List<string[]>();
        public List<string[]> Groups = new List<string[]>();
        public List<string[]> Bones = new List<string[]>();
        public List<string[]> PoseBones = new List<string[]>();
        public List<string[]> Actions = new List<string[]>();
        public List<string[]> FCurves = new List<string[]>();
        public List<string[]> Keys = new List<string[]>();
        public string[] Image;
        public List<string> Logs = new List<string>();
    }

    public string Key;
    public string[] Argv;                             // the job's fields: input, argv[2]..argv[14]
    public SortedDictionary<int, Stage> Stages = new SortedDictionary<int, Stage>();
    public int StoppedAt;                             // 0 when all four stages ran; else the stage that exited or died
    public string Exit;                               // the exit code, or null
    public string Died;                               // the exception, or null
    public List<string> LogsOfStop = new List<string>();
    public List<string> ExportLogs = new List<string>();     // XLOG: the export step's RIGANIM lines (step (f))
    public string ExportDied;                                 // XDIES, or null
    public List<string[]> ExportRows = new List<string[]>();  // X: the GLB read back (glb_rows.py), the X stripped
    public IEnumerable<string> AllLogs => Stages.Values.SelectMany(s => s.Logs).Concat(LogsOfStop);
    public Stage Last => Stages.Count == 0 ? null : Stages[Stages.Keys.Max()];

    static readonly Dictionary<string, int> Width = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        { "JOB", 2 }, { "STAGE", 2 }, { "LOG", 3 }, { "SCENE", 6 }, { "OBJ", 42 }, { "MESH", 19 }, { "VG", 6 }, { "BONE", 23 },
        { "PB", 33 }, { "ACT", 7 }, { "FC", 8 }, { "K", 15 }, { "IMG", 9 }, { "EXIT", 3 }, { "DIES", 3 }, { "DONE", 2 }, { "FAIL", 3 },
    };

    static bool IsHex(string s, int n) => s.Length == n && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    static bool IsInt(string s) => long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    static void Floats(string[] t, int from, int count, string what)
    {
        for (int i = from; i < from + count; i++) if (!IsHex(t[i], 8)) throw new InvalidDataException($"{what} row has a value that is not float32 bits: '{t[i]}'");
    }

    /// <summary>One job's block (JOB .. DONE), checked for shape and consistency; throws InvalidDataException otherwise.</summary>
    static string FcKey(string[] row) => row[2] + "\u0001" + row[3] + "\u0001" + row[4];

    static byte[] LittleEndianBits(string hex)
    {
        var bytes = new byte[4];
        for (int i = 0; i < 4; i++) bytes[i] = Convert.ToByte(hex.Substring((3 - i) * 2, 2), 16);
        return bytes;
    }

    static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    public static RigAnimDump Parse(List<string> block, Dictionary<string, string[]> jobs, bool requireKeys = false)
    {
        var all = block.Select(l => l.Split('\t')).ToList();
        var rows = all.Where(t => t[0] != "X" && t[0] != "XLOG" && t[0] != "XDIES").ToList();
        if (rows[0][0] != "JOB" || rows[0].Length != 2 || rows[0][1].Length == 0) throw new InvalidDataException("the dump has an invalid JOB row");
        var d = new RigAnimDump { Key = rows[0][1] };
        // the export's rows (step (f)): after stage 4 only, the GLB rows after the logs and only without a death
        var exportKinds = new HashSet<string>(new[] { "GLB", "SCENE", "NODE", "SKIN", "IBM", "MESH", "PRIM", "MAT", "IMG", "TEX", "SAMP", "ANIM", "CHAN" }, StringComparer.Ordinal);
        bool exportStarted = false, stage4Seen = false, doneSeen = false; string exportFramingProblem = null;
        foreach (var t in all)
        {
            bool isExport = t[0] == "X" || t[0] == "XLOG" || t[0] == "XDIES";
            if (isExport)
            {
                if (doneSeen) throw new InvalidDataException("the export row follows DONE");
                if (!stage4Seen) throw new InvalidDataException("the export row is before stage 4");
                exportStarted = true;
            }
            else
            {
                if (exportStarted && t[0] != "DONE") exportFramingProblem = "a stage row follows the export";
                if (t[0] == "STAGE" && t.Length > 1 && t[1] == "4") stage4Seen = true;
                if (t[0] == "DONE") doneSeen = true;
            }
            if (t[0] == "XLOG") { if (t.Length != 2 || !t[1].StartsWith("RIGANIM", StringComparison.Ordinal)) throw new InvalidDataException("an XLOG row that is not a RIGANIM line"); d.ExportLogs.Add(t[1]); }
            else if (t[0] == "XDIES") { if (t.Length != 2 || t[1].Length == 0 || d.ExportDied != null) throw new InvalidDataException("an invalid or repeated XDIES row"); d.ExportDied = t[1]; }
            else if (t[0] == "X") { if (t.Length < 2 || d.ExportDied != null) throw new InvalidDataException("an X row after the export died, or an empty one"); if (!exportKinds.Contains(t[1])) throw new InvalidDataException("the export has an unknown row kind: " + t[1]); d.ExportRows.Add(t.Skip(1).ToArray()); }
        }
        if (d.ExportDied != null && d.ExportRows.Count > 0) throw new InvalidDataException("the export both died and has GLB rows");
        if (!jobs.TryGetValue(d.Key, out d.Argv)) throw new InvalidDataException("the dump has a job the jobs file does not");
        var fail = rows.FirstOrDefault(t => t[0] == "FAIL");
        if (fail != null) throw new InvalidDataException("Blender could not run it: " + fail.Last());
        foreach (var t in rows)
        {
            if (!Width.TryGetValue(t[0], out int w)) throw new InvalidDataException($"the dump has an unknown row kind '{t[0]}'");
            if (t.Length != w) throw new InvalidDataException($"{t[0]} row has {t.Length} fields, {w} expected");
        }
        var done = rows.Where(t => t[0] == "DONE").ToList();
        if (done.Count == 0) throw new InvalidDataException("the dump has no DONE row");
        if (done.Count != 1 || done[0][1] != d.Key || rows[rows.Count - 1] != done[0]) throw new InvalidDataException("the dump must end with exactly one matching DONE row");
        if (rows.Skip(1).Any(t => t[0] == "JOB")) throw new InvalidDataException("the dump repeats a JOB row inside a block");
        // the stages: 1..k in order, each announced once, every row of a stage after its STAGE row
        var stageRows = rows.Where(t => t[0] == "STAGE").Select(t => int.Parse(t[1], CultureInfo.InvariantCulture)).ToList();
        for (int i = 0; i < stageRows.Count; i++) if (stageRows[i] != i + 1) throw new InvalidDataException($"the stages are not 1..k in order: STAGE {stageRows[i]} at position {i + 1}");
        if (stageRows.Count > 4) throw new InvalidDataException("more than four stages");
        int k = stageRows.Count;
        var exits = rows.Where(t => t[0] == "EXIT").ToList(); var dies = rows.Where(t => t[0] == "DIES").ToList();
        if (exits.Count + dies.Count > 1) throw new InvalidDataException("the dump claims more than one stop (EXIT or DIES)");
        if (k == 4 && exits.Count + dies.Count > 0) throw new InvalidDataException("the dump claims a stop after all four stages ran");
        if (k < 4 && exits.Count + dies.Count == 0) throw new InvalidDataException($"the dump has {k} stage(s) and no EXIT or DIES row for the next");
        if (exits.Count == 1)
        {
            if (int.Parse(exits[0][1], CultureInfo.InvariantCulture) != k + 1) throw new InvalidDataException("the EXIT row names a stage other than the one after the last dumped");
            if (exits[0][2] != "1") throw new InvalidDataException($"the script exits only with code 1; the dump says '{exits[0][2]}'");
            d.StoppedAt = k + 1; d.Exit = exits[0][2];
        }
        if (dies.Count == 1)
        {
            if (int.Parse(dies[0][1], CultureInfo.InvariantCulture) != k + 1) throw new InvalidDataException("the DIES row names a stage other than the one after the last dumped");
            if (dies[0][2].Length == 0) throw new InvalidDataException("the DIES row names no exception");
            d.StoppedAt = k + 1; d.Died = dies[0][2];
        }
        // Report the existing envelope/stop errors first; export framing supplements those guarantees.
        if (exportFramingProblem != null) throw new InvalidDataException(exportFramingProblem);
        for (int n = 1; n <= k; n++) d.Stages[n] = new Stage { N = n };
        int current = 0;   // the stage whose rows we are in: STAGE n opens n; LOG rows of stage n come before STAGE n
        foreach (var t in rows)
        {
            if (t[0] == "JOB" || t[0] == "DONE" || t[0] == "FAIL") continue;
            if (t[0] == "STAGE") { current = int.Parse(t[1], CultureInfo.InvariantCulture); continue; }
            int n = int.Parse(t[1], CultureInfo.InvariantCulture);
            if (t[0] == "LOG" || t[0] == "EXIT" || t[0] == "DIES")
            {
                if (n != current + 1 || n > k + 1) throw new InvalidDataException($"a {t[0]} row of stage {n} is out of place (after stage {current})");
                if (t[0] == "LOG")
                {
                    if (!t[2].StartsWith("RIGANIM", StringComparison.Ordinal)) throw new InvalidDataException("a LOG row that is not a RIGANIM line");
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
                    if (!IsInt(t[4]) || !IsInt(t[5]) || !IsHex(t[6], 64) || !IsHex(t[7], 64)) throw new InvalidDataException("FC row has an invalid index, count or hash");
                    s.FCurves.Add(t); break;
                case "K": Floats(t, 5, 6, "K"); s.Keys.Add(t); break;
                case "IMG":
                    if (n != 4) throw new InvalidDataException("an IMG row outside stage 4");
                    if (s.Image != null) throw new InvalidDataException("stage 4 has two IMG rows");
                    if (!IsInt(t[3]) || !IsHex(t[4], 64) || !IsInt(t[5]) || !IsInt(t[6]) || !IsInt(t[7]) || !IsHex(t[8], 64)) throw new InvalidDataException("IMG row has an invalid size or hash");
                    s.Image = t; break;
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
            foreach (var f in s.FCurves) if (!fcKeys.Add(FcKey(f))) throw new InvalidDataException($"stage {s.N}: two FC rows for '{f[3]}'[{f[4]}] of '{f[2]}'");
            foreach (var o in s.Objs) if (o[8] != "-" && !actNames.Contains(o[8])) throw new InvalidDataException($"stage {s.N}: object '{o[2]}' holds an action the stage does not list");
            if (requireKeys || s.Keys.Count > 0)
            {
                var keyRows = s.Keys.GroupBy(FcKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
                foreach (var id in keyRows.Keys) if (!fcKeys.Contains(id)) throw new InvalidDataException($"stage {s.N}: K row names no FC row");
                foreach (var f in s.FCurves)
                {
                    keyRows.TryGetValue(FcKey(f), out var rowsForCurve);
                    int have = rowsForCurve == null ? 0 : rowsForCurve.Count;
                    if (have != int.Parse(f[5], CultureInfo.InvariantCulture)) throw new InvalidDataException($"stage {s.N}: fcurve '{f[3]}'[{f[4]}] says {f[5]} keys, {have} K rows");
                    var co = new List<byte>(); var leftHandles = new List<byte>(); var rightHandles = new List<byte>(); var meta = new List<string>();
                    if (rowsForCurve != null) foreach (var krow in rowsForCurve)
                    {
                        co.AddRange(LittleEndianBits(krow[5])); co.AddRange(LittleEndianBits(krow[6]));
                        for (int i = 7; i <= 8; i++) leftHandles.AddRange(LittleEndianBits(krow[i]));
                        for (int i = 9; i <= 10; i++) rightHandles.AddRange(LittleEndianBits(krow[i]));
                        meta.Add(krow[11] + "/" + krow[12] + "/" + krow[13] + "/" + krow[14]);
                    }
                    var handles = new List<byte>(leftHandles.Count + rightHandles.Count);
                    handles.AddRange(leftHandles); handles.AddRange(rightHandles);
                    handles.AddRange(Encoding.UTF8.GetBytes(string.Join("|", meta)));
                    if (Hash(co.ToArray()) != f[6] || Hash(handles.ToArray()) != f[7])
                        throw new InvalidDataException($"stage {s.N}: K rows do not match FC hashes for '{f[3]}'[{f[4]}]");
                }
            }
            if (s.N == 4)
            {
                bool wrote = s.Logs.Any(l => l.StartsWith("RIGANIM albedo ->", StringComparison.Ordinal));
                if (wrote != (s.Image != null)) throw new InvalidDataException(wrote ? "stage 4 says an albedo was written and has no IMG row" : "stage 4 has an IMG row and no albedo line");
            }
        }
        return d;
    }
}

static class RigAnimDrill
{
    // the script's branches, by its own log lines (and two by the dump's shape)
    static readonly (string key, Func<RigAnimDump, bool> test)[] Cover =
    {
        ("the conversion path ON", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM conversion path: ON"))),
        ("the conversion path off (legacy)", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM conversion path: off"))),
        ("the contract marker: V2 clean units (a deploy conversion)", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM contract: V2"))),
        ("state roles", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM state roles:"))),
        ("a previous run's role clip cleared", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM cleared the previous run's"))),
        ("donor sockets requested", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM donor sockets requested:"))),
        ("object-level animation cleared", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM cleared object-level animation"))),
        ("localNodeAnim: mesh worlds sampled from the round trip", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM localNodeAnim: sampled"))),
        ("staticParts kept weightless", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM staticParts:"))),
        ("bone-parent -> skin: meshes bound", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM bone-parent->skin:"))),
        ("wrapper empties flattened", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM flattened"))),
        ("localNodeAnim v2: mesh bones keyed", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM localNodeAnim v2:"))),
        ("the bone cap: zero-weight leaves removed", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM bone-cap:"))),
        ("the bone-index wall: a finished rig past the budget (a warning)", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM WARNING:") && l.Contains("past the 126-bone budget"))),
        ("a clip sliced", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM sliced"))),
        ("a reversed slice", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM sliced") && l.EndsWith(", reversed)"))),
        ("a held stance padded (the epsilon nudge)", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM held-stance pad:"))),
        ("the primary with state clips", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM action '") && l.Contains(" state clip(s)"))),
        ("the primary alone", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM action '") && !l.Contains(" state clip(s)"))),
        ("the bone filter", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM bone-filter"))),
        ("the rest fold and rebake", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM rest-normalized + rebaked"))),
        ("a single-frame clip padded in the rebake", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM single-frame clip"))),
        ("translation-animated bones, kept", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM TRANSLATION-animated bone(s)") && l.Contains("KEPT"))),
        ("translation-animated bones, stripped", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM TRANSLATION-animated bone(s)") && l.Contains("stripped below"))),
        ("the rest fold failed before the rewrite (a warning)", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM WARN: rest-fold failed"))),
        ("legacy keepTranslations: paths kept", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM legacy keepTranslations:") && l.Contains("kept:"))),
        ("legacy keepTranslations: nothing kept", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM legacy keepTranslations: no attack/move"))),
        ("legacy keepTranslations: paths rejected as garbage", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM legacy keepTranslations:") && l.Contains("REJECTED"))),
        ("bone-location fcurves stripped", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM stripped") && l.Contains("bone-LOCATION"))),
        ("kept curves delta-rebased and amplified", d => d.AllLogs.Any(l => l.Contains("delta-rebased + amplified"))),
        ("the albedo written", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM albedo ->"))),
        ("no albedo image", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM no albedo image"))),
        ("the weld done", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM weld:") && !l.Contains("SKIPPED"))),
        ("the weld skipped (seams)", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM weld: SKIPPED"))),
        ("the material slots kept", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM keeping"))),
        ("the materials collapsed to one", d => d.Stages.ContainsKey(4) && !d.AllLogs.Any(l => l.StartsWith("RIGANIM keeping"))),
        ("the decimate skipped", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM decimate SKIPPED"))),
        ("the decimate done", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM decimate ") && !l.Contains("SKIPPED"))),
        ("the armature modifier re-bound after the join", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM re-bound"))),
        ("no-op root bones collapsed", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM collapsed"))),
        ("bones renamed with topological prefixes", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM bones renamed"))),
        ("the A###_ prefix (sockets)", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM bones renamed") && l.Contains("prefix A###_"))),
        ("dormant fcurve paths patched", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM patched"))),
        ("a socket created", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM socket '"))),
        ("the rotation and scale folded into the data", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM rig rotation+scale APPLIED"))),
        ("a non-zero rotation", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM rig rotation: ") && !l.Contains("x=0.0 y=0.0 z=0.0"))),
        ("object-level fcurves stripped for the fold", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM stripped") && l.Contains("OBJECT-level"))),
        ("the placement moved the model", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM placement: world centre"))),
        ("the placement found it centred", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM placement: already"))),
        ("the grounding warning", d => d.AllLogs.Any(l => l.StartsWith("RIGANIM WARNING: grounding"))),
        ("the script's exit (a RIGANIM ERROR)", d => d.Exit != null),
        ("the script died", d => d.Died != null),
        ("all four stages ran", d => d.Stages.Count == 4),
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        bool requireKeys = false; int stages = 0;   // --stages N: compare the C# model of stages 1..N (N = 1 or 2) with the dump
        int offset = 0;
        const string usage = "usage: riganim.exe [--keys] [--stages 1|2|3|4] [--export] <jobs file> <dump>...";
        while (offset < args.Length && args[offset].StartsWith("--", StringComparison.Ordinal))
        {
            if (args[offset] == "--keys") requireKeys = true;
            else if (args[offset] == "--export") { Stage1Drill.Export = true; Stage1Drill.InProcess = true; stages = 4; }   // step (f): the GLB export after stage 4, against the X rows; step (g): the Factory's wrapper on each
            else if (args[offset] == "--stages" && offset + 1 < args.Length && int.TryParse(args[offset + 1], NumberStyles.None, CultureInfo.InvariantCulture, out stages) && stages >= 1 && stages <= 4) offset++;
            else { Console.WriteLine(usage); return 2; }
            offset++;
        }
        if (args.Length < offset + 2) { Console.WriteLine(usage); return 2; }
        var jobs = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var leftByName = new List<string>();
        foreach (var raw in File.ReadAllLines(args[offset]))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var t = line.Split('\t');
            if (t[0] == "LEFT" && t.Length == 3) { leftByName.Add(t[1] + ": " + t[2]); continue; }
            if (t[0] != "JOB" || t.Length != 16 || t[1].Length == 0 || jobs.ContainsKey(t[1])) { Console.WriteLine("FAIL jobs: an invalid or duplicate job definition"); return 1; }
            jobs.Add(t[1], t.Skip(2).ToArray());
        }
        if (jobs.Count == 0) { Console.WriteLine("FAIL jobs: no rig_anim jobs to read"); return 1; }
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
        long stageRows = 0, logs = 0, objs = 0, meshes = 0, groups = 0, bones = 0, fcurves = 0, keys = 0; int stopped = 0, full = 0;
        foreach (var block in blocks)
        {
            string key = block[0].Split('\t').Length > 1 ? block[0].Split('\t')[1] : "?";
            try
            {
                if (!seen.Add(key)) throw new InvalidDataException("the dump repeats a job");
                var d = RigAnimDump.Parse(block, jobs, requireKeys);
                foreach (var c in Cover) if (c.test(d)) cover[c.key]++;
                stageRows += d.Stages.Count; logs += d.AllLogs.Count();
                foreach (var s in d.Stages.Values) { objs += s.Objs.Count; meshes += s.Meshes.Count; groups += s.Groups.Count; bones += s.Bones.Count; fcurves += s.FCurves.Count; keys += s.Keys.Count; }
                // EXIT:<stage>: keys name the expected script exit stage. A different exit is a broken fixture too.
                // Every other job must run through all four stages: a registry recipe that stops is a broken bake.
                bool expectStop = key.StartsWith("EXIT:", StringComparison.Ordinal);
                int expectedStage = 0;
                if (expectStop)
                {
                    int colon = key.IndexOf(':', 5);
                    if (colon < 0 || !int.TryParse(key.Substring(5, colon - 5), NumberStyles.None, CultureInfo.InvariantCulture, out expectedStage) || expectedStage < 1 || expectedStage > 4)
                        throw new InvalidDataException("EXIT: key must name its expected stage (EXIT:<stage>:<name>)");
                }
                if (d.StoppedAt > 0)
                {
                    stopped++;
                    string how = d.Exit != null ? "exits " + d.Exit + ": " + (d.LogsOfStop.LastOrDefault(l => l.StartsWith("RIGANIM ERROR")) ?? "(no RIGANIM ERROR line)") : "dies: " + d.Died;
                    if (expectStop && d.Exit != null && d.StoppedAt == expectedStage && d.LogsOfStop.Any(l => l.StartsWith("RIGANIM ERROR", StringComparison.Ordinal)))
                        Console.WriteLine($"OK {key}: stops at stage {d.StoppedAt} as expected; {how}");
                    else if (key.StartsWith("LEFT:", StringComparison.Ordinal))
                        Console.WriteLine($"OK {key}: left to Blender by name, and Blender's own run stopped at stage {d.StoppedAt}; {how} (the model's decline is checked)");
                    else { fails++; Console.WriteLine($"FAIL {key}: stopped at stage {d.StoppedAt}" + (expectStop ? $", expected to stop at stage {expectedStage} with the script's RIGANIM ERROR; " : "; ") + how); }
                }
                else if (expectStop) { fails++; Console.WriteLine($"FAIL {key}: expected to stop (its key says EXIT:), ran through all four stages"); }
                else
                {
                    full++;
                    var last = d.Last;
                    Console.WriteLine($"OK {key}: 4 stages, {d.AllLogs.Count()} log lines; at the end {last.Objs.Count} objects, {last.Meshes.Count} mesh(es), {last.Bones.Count} bones, {last.Actions.Count} action(s), {last.FCurves.Count} fcurves, frames {last.Scene[0]}..{last.Scene[1]}");
                }
                if (stages > 0 && !Stage1Drill.Compare(d, key, stages)) fails++;
            }
            catch (InvalidDataException e) { fails++; Console.WriteLine($"FAIL {key}: {e.Message}"); }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {key}: {e.GetType().Name}: {e.Message}"); }
        }
        foreach (var k in jobs.Keys.Where(k => !seen.Contains(k))) { fails++; Console.WriteLine($"FAIL {k}: the jobs file names it, the dump has no block for it"); }
        foreach (var l in leftByName) Console.WriteLine("LEFT " + l);
        foreach (var kv in cover) Console.WriteLine($"COVER {kv.Value} {kv.Key}");
        if (stages > 0) Stage1Drill.Summary(stages);
        Console.WriteLine($"TOTAL jobs {blocks.Count} full {full} stopped {stopped} left {leftByName.Count} stages {stageRows} logs {logs} objects {objs} meshes {meshes} groups {groups} bones {bones} fcurves {fcurves} keys {keys} fails {fails}");
        if (fails > 0) { Console.WriteLine($"FAIL — {fails} problem(s)"); return 1; }
        Console.WriteLine($"PASS — rig_anim stage dump: {blocks.Count} jobs dumped by Blender, {full} through all four stages, {stopped} stopped by the script as it says; {leftByName.Count} recipe(s) left by name" + (stages > 0 ? $"; stages 1 to {stages} modelled: {Stage1Drill.Equal} of {Stage1Drill.Compared} jobs equal row for row ({Stage1Drill.RowsEqual} rows), {Stage1Drill.Left} left by name, {Stage1Drill.Deferred} deferred to a later step" : ""));
        return 0;
    }
}

/// <summary>Step (b): the stage 1 model (BlenderRigAnim.RunStage1) against the dump, every row of stage 1 as a string -
/// the dump's own encoding, float32 bits and SHA-256 hashes - so that equal means equal to the bit. A job the model
/// leaves to Blender is a FAIL here: every dumped recipe and fixture is one the C# must take.</summary>
static class Stage1Drill
{
    public static int Compared, Equal, Left, Deferred; public static long RowsEqual;
    static readonly SortedDictionary<string, int> Census = new SortedDictionary<string, int>(StringComparer.Ordinal);

    static float FromHex(string h) => BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(h, 16)), 0);
    static string Show(string f) => f.Length == 8 && f.All(c => Uri.IsHexDigit(c)) ? f + " (" + FromHex(f).ToString("R", CultureInfo.InvariantCulture) + ")" : f.Length == 64 ? "a hash " + f.Substring(0, 12) + "…" : "'" + f + "'";

    /// <summary>The first difference between two row lists, or null: a row's identity is its leading `idFields` after the stage number.</summary>
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

    /// <summary>An FC hash that differs, explained by the K rows when the dump has them: the first key that differs.</summary>
    static string ExplainKeys(string[] fcMine, List<string[]> keysMine, RigAnimDump.Stage s)
    {
        if (s.Keys.Count == 0) return " (RIGANIM_KEYS=1 shows the key)";
        string Id(string[] k) => k[2] + "\u0001" + k[3] + "\u0001" + k[4];
        var mine = keysMine.Where(k => Id(k) == Id(fcMine)).ToList(); var theirs = s.Keys.Where(k => Id(k) == Id(fcMine)).ToList();
        string d = Diff("K", mine, theirs, 3);
        return d == null ? " (every K row equal: the hash itself differs)" : "; " + d;
    }

    /// <summary>The model's rows of stage `n` against the dump's: every kind, as strings.</summary>
    static void CompareRows(int n, BlenderRigAnim.Rows rows, List<string> logs, RigAnimDump.Stage s, List<string> problems)
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
        if ((x = Diff("FC", rows.FCurves, s.FCurves, 3)) != null)
        {
            int at = -1; for (int i = 0; i < Math.Min(rows.FCurves.Count, s.FCurves.Count) && at < 0; i++) if (!rows.FCurves[i].SequenceEqual(s.FCurves[i])) at = i;
            problems.Add(N + x + (at >= 0 && rows.FCurves[at].Take(5).SequenceEqual(s.FCurves[at].Take(5)) ? ExplainKeys(rows.FCurves[at], rows.Keys, s) : ""));
        }
        if (s.Keys.Count > 0 && (x = Diff("K", rows.Keys, s.Keys, 3)) != null) problems.Add(N + x);
    }

    /// <summary>RIGANIM_MODEL_ROWS=&lt;file&gt; writes the model's rendered rows of a stage (for the job RIGANIM_MODEL_JOB, or every
    /// job) in the dump's own format, to be diffed against the dump when a row differs.</summary>
    static void DumpModelRows(string key, int n, BlenderRigAnim.Rows rows, List<string> logs)
    {
        string path = Environment.GetEnvironmentVariable("RIGANIM_MODEL_ROWS"); string only = Environment.GetEnvironmentVariable("RIGANIM_MODEL_JOB");
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

    static long RowCount(RigAnimDump.Stage s) => 1 + s.Objs.Count + s.Meshes.Count + s.Groups.Count + s.Bones.Count + s.PoseBones.Count + s.Actions.Count + s.FCurves.Count + s.Keys.Count;
    static string Describe(RigAnimDump.Stage s) => $"{s.Objs.Count} objects, {s.Meshes.Count} meshes, {s.Groups.Count} vertex groups, {s.Bones.Count} bones, {s.Actions.Count} actions, {s.FCurves.Count} fcurves" + (s.Keys.Count > 0 ? $", {s.Keys.Count} keys" : "");

    /// <summary>Stages 1..`stages` of the model against the dump. A LEFT: job must be declined by the model at one of
    /// them (its earlier stages compared all the same); any other decline is a FAIL.</summary>
    public static bool Export;                                   // --export: BlenderRigExport against the dump's X rows after stage 4
    public static bool InProcess; public static int InProcessEqual;   // --export also runs BlenderRigAnimInProcess on every exported job (step (g))
    public static int Exported; public static long ExportRows;
    public static readonly SortedDictionary<string, long> ExportCover = new SortedDictionary<string, long>(StringComparer.Ordinal);

    public static bool Compare(RigAnimDump d, string key, int stages)
    {
        Compared++;
        var argv = new List<string> { d.Argv[0], "riganim_drill_output.fbx" }; argv.AddRange(d.Argv.Skip(1));
        // argv[5] is the dump script's albedo PATH when the job's flag is 1 (blender_riganim_dump.py builds it): the dump's IMG
        // row or its albedo line gives it back; a job that wrote none keeps a placeholder (the name never shows)
        if (argv.Count > 5 && argv[5] == "0") argv[5] = "";   // no albedo asked (reuseExtracted)
        else if (argv.Count > 5 && argv[5] == "1")
        {
            string path = d.Stages.ContainsKey(4) && d.Stages[4].Image != null ? d.Stages[4].Image[2] : null;
            if (path == null) { var line = d.AllLogs.FirstOrDefault(l => l.StartsWith("RIGANIM albedo -> ", StringComparison.Ordinal)); if (line != null) { int at = line.LastIndexOf(" (", StringComparison.Ordinal); path = at > 0 ? line.Substring(18, at - 18) : null; } }
            argv[5] = path ?? "riganim_drill_albedo.png";
        }
        var problems = new List<string>();
        BlenderRigAnim.Stage1 model; BlenderRigAnim.Args args;
        try
        {
            var m = GlbReader.Read(d.Argv[0]);
            args = BlenderRigAnim.ParseArgs(argv);
            model = BlenderRigAnim.RunStage1(m, args);
        }
        catch (Exception e) { Console.WriteLine($"FAIL {key}: stage 1 model: {e.GetType().Name}: {e.Message}"); return false; }
        // a LEFT: key names a job the C# leaves to Blender by name: the model must say so, and a model that takes it is wrong too
        bool left = key.StartsWith("LEFT:", StringComparison.Ordinal);
        bool exportLeft = key.StartsWith("EXPORTLEFT:", StringComparison.Ordinal);   // step (f): left at the export alone, the four stages equal
        if (model.Fallback != null)
        {
            foreach (var note in model.Notes) Census[note] = (Census.TryGetValue(note, out int c) ? c : 0) + 1;
            if (!left) { Console.WriteLine($"FAIL {key}: stage 1 model leaves this file to Blender: {model.Fallback}"); return false; }
            Left++; Console.WriteLine($"STAGE1 {key}: left to Blender by name, as the model says: {model.Fallback}"); return true;
        }
        var rows = model.Exits ? null : BlenderRigAnim.Render(model, 1);
        if (model.Exits)
        {
            if (d.StoppedAt != 1 || d.Exit == null) problems.Add("stage 1 model: the script exits in stage 1 here, " + (d.StoppedAt == 0 ? "Blender ran it through" : $"Blender stopped at stage {d.StoppedAt}"));
            else if (!d.LogsOfStop.SequenceEqual(model.Logs)) problems.Add("stage 1 model: the stop's RIGANIM lines differ: here [" + string.Join(" | ", model.Logs) + "], Blender [" + string.Join(" | ", d.LogsOfStop) + "]");
        }
        else if (!d.Stages.TryGetValue(1, out var s)) problems.Add("stage 1 model: the scene runs on here, Blender stopped in stage 1: " + (d.Exit != null ? string.Join(" | ", d.LogsOfStop) : d.Died));
        else
        {
            CompareRows(1, rows, model.Logs, s, problems);
            if (problems.Count == 0) { RowsEqual += RowCount(s); Console.WriteLine($"STAGE1 {key}: equal - {Describe(s)}"); }
        }
        if (problems.Count == 0 && model.Exits) { RowsEqual += model.Logs.Count; Console.WriteLine($"STAGE1 {key}: equal - the script's exit in stage 1, {model.Logs.Count} RIGANIM lines"); }
        // stage 2 on the scene stage 1 left (the script's exit ends the run; its RIGANIM lines are the stop's, compared above)
        if (problems.Count == 0 && stages >= 2 && !model.Exits)
        {
            try { BlenderRigAnim.RunStage2(model, args); }
            catch (Exception e) { problems.Add($"stage 2 model: {e.GetType().Name}: {e.Message}"); }
            if (problems.Count == 0 && model.Fallback != null)
            {
                foreach (var note in model.Notes) Census[note] = (Census.TryGetValue(note, out int c) ? c : 0) + 1;
                if (!left) { Console.WriteLine($"FAIL {key}: stage 2 model leaves this file to Blender: {model.Fallback}"); return false; }
                Left++; Console.WriteLine($"STAGE2 {key}: left to Blender by name, as the model says: {model.Fallback}"); return true;
            }
            RigAnimDump.Stage s2 = null;
            if (problems.Count == 0 && !d.Stages.TryGetValue(2, out s2)) problems.Add("stage 2 model: the scene runs on here, Blender stopped in stage 2: " + (d.Exit != null ? string.Join(" | ", d.LogsOfStop) : d.Died));
            else if (problems.Count == 0)
            {
                var rows2 = BlenderRigAnim.Render(model, 2);
                CompareRows(2, rows2, model.Logs2, s2, problems);
                if (problems.Count == 0) { RowsEqual += RowCount(s2); Console.WriteLine($"STAGE2 {key}: equal - {Describe(s2)}"); }
            }
        }
        // stage 3 on the scene stage 2 left: a job the model defers by name (the rest fold, the bone cap: step (d2)) is
        // counted apart, its stages 1 and 2 compared all the same
        if (problems.Count == 0 && stages >= 3 && !model.Exits)
        {
            try { BlenderRigAnim.RunStage3(model, args); }
            catch (Exception e) { problems.Add($"stage 3 model: {e.GetType().Name}: {e.Message}"); }
            if (problems.Count == 0 && model.Fallback != null)
            {
                foreach (var note in model.Notes) Census[note] = (Census.TryGetValue(note, out int c) ? c : 0) + 1;
                if (!left) { Console.WriteLine($"FAIL {key}: stage 3 model leaves this file to Blender: {model.Fallback}"); return false; }
                Left++; Console.WriteLine($"STAGE3 {key}: left to Blender by name, as the model says: {model.Fallback}"); return true;
            }
            if (problems.Count == 0 && model.Deferred != null)
            {
                foreach (var note in model.Notes) Census[note] = (Census.TryGetValue(note, out int c) ? c : 0) + 1;
                if (left) { Console.WriteLine($"FAIL {key}: the model defers a file its key says is left to Blender"); return false; }
                Deferred++; Console.WriteLine($"STAGE3 {key}: deferred - {model.Deferred}"); return true;
            }
            if (problems.Count == 0 && model.Exits)
            {
                if (d.StoppedAt != 3 || d.Exit == null) problems.Add("stage 3 model: the script exits in stage 3 here, " + (d.StoppedAt == 0 ? "Blender ran it through" : $"Blender stopped at stage {d.StoppedAt}"));
                else if (!d.LogsOfStop.SequenceEqual(model.Logs3)) problems.Add("stage 3 model: the stop's RIGANIM lines differ: here [" + string.Join(" | ", model.Logs3) + "], Blender [" + string.Join(" | ", d.LogsOfStop) + "]");
                if (problems.Count == 0) { RowsEqual += model.Logs3.Count; Console.WriteLine($"STAGE3 {key}: equal - the script's exit in stage 3, {model.Logs3.Count} RIGANIM lines"); }
            }
            else if (problems.Count == 0)
            {
                RigAnimDump.Stage s3 = null;
                if (!d.Stages.TryGetValue(3, out s3)) problems.Add("stage 3 model: the scene runs on here, Blender stopped in stage 3: " + (d.Exit != null ? string.Join(" | ", d.LogsOfStop) : d.Died));
                else
                {
                    var rows3 = BlenderRigAnim.Render(model, 3);
                    DumpModelRows(key, 3, rows3, model.Logs3);
                    CompareRows(3, rows3, model.Logs3, s3, problems);
                    if (problems.Count == 0) { RowsEqual += RowCount(s3); Console.WriteLine($"STAGE3 {key}: equal - {Describe(s3)}"); }
                }
            }
        }
        // stage 4 on the scene stage 3 left
        if (problems.Count == 0 && stages >= 4 && !model.Exits && model.Deferred == null)
        {
            try { BlenderRigAnim.RunStage4(model, args); }
            catch (Exception e) { problems.Add($"stage 4 model: {e.GetType().Name}: {e.Message}"); }
            if (problems.Count == 0 && model.Fallback != null)
            {
                foreach (var note in model.Notes) Census[note] = (Census.TryGetValue(note, out int c) ? c : 0) + 1;
                if (!left) { Console.WriteLine($"FAIL {key}: stage 4 model leaves this file to Blender: {model.Fallback}"); return false; }
                Left++; Console.WriteLine($"STAGE4 {key}: left to Blender by name, as the model says: {model.Fallback}"); return true;
            }
            if (problems.Count == 0 && model.Exits)
            {
                if (d.StoppedAt != 4 || d.Exit == null) problems.Add("stage 4 model: the script exits in stage 4 here, " + (d.StoppedAt == 0 ? "Blender ran it through" : $"Blender stopped at stage {d.StoppedAt}"));
                else if (!d.LogsOfStop.SequenceEqual(model.Logs4)) problems.Add("stage 4 model: the stop's RIGANIM lines differ: here [" + string.Join(" | ", model.Logs4) + "], Blender [" + string.Join(" | ", d.LogsOfStop) + "]");
                if (problems.Count == 0) { RowsEqual += model.Logs4.Count; Console.WriteLine($"STAGE4 {key}: equal - the script's exit in stage 4, {model.Logs4.Count} RIGANIM lines"); }
            }
            else if (problems.Count == 0)
            {
                RigAnimDump.Stage s4 = null;
                if (!d.Stages.TryGetValue(4, out s4)) problems.Add("stage 4 model: the scene runs on here, Blender stopped in stage 4: " + (d.Exit != null ? string.Join(" | ", d.LogsOfStop) : d.Died));
                else
                {
                    var rows4 = BlenderRigAnim.Render(model, 4);
                    DumpModelRows(key, 4, rows4, model.Logs4);
                    CompareRows(4, rows4, model.Logs4, s4, problems);
                    // the IMG row: the path, the width, the height and the channels; the file's bytes and the pixels are the encoder's
                    if ((rows4.Image == null) != (s4.Image == null)) problems.Add("stage 4 IMG: " + (rows4.Image == null ? "no albedo here, one in Blender" : "an albedo here, none in Blender"));
                    else if (rows4.Image != null)
                        foreach (int fld in new[] { 2, 5, 6, 7 })
                            if (rows4.Image[fld] != s4.Image[fld]) { problems.Add($"stage 4 IMG field {fld}: here '{rows4.Image[fld]}', Blender '{s4.Image[fld]}'"); break; }
                    if (problems.Count == 0) { RowsEqual += RowCount(s4) + (s4.Image != null ? 1 : 0); Console.WriteLine($"STAGE4 {key}: equal - {Describe(s4)}" + (s4.Image != null ? $", the albedo {s4.Image[5]}x{s4.Image[6]}" : "")); }
                    // ---- the export (step (f)): the GLB Blender's exporter writes of this scene, structure first
                    if (problems.Count == 0 && Export)
                    {
                        BlenderDeployExport.Result x = null;
                        try { x = BlenderRigExport.Build(model, args); }
                        catch (Exception e) { problems.Add($"the export model: {e.GetType().Name}: {e.Message}"); }
                        if (problems.Count == 0 && d.ExportDied != null)
                        {
                            if (x.Fallback == null) problems.Add("the export: Blender's exporter died (" + d.ExportDied + ") and the model went on");
                            else if (!left) problems.Add("the export is left by name (" + x.Fallback + "), which the key does not say; Blender's exporter died: " + d.ExportDied);
                            else { Left++; Console.WriteLine($"EXPORT {key}: left to Blender by name, as the model says: {x.Fallback} (Blender's exporter died: {d.ExportDied})"); return true; }
                        }
                        else if (problems.Count == 0 && x.Fallback != null)
                        {
                            if (!left && !exportLeft) problems.Add("the export is left by name, which the key does not say: " + x.Fallback);
                            else { Left++; Console.WriteLine($"EXPORT {key}: left to Blender by name, as the model says: {x.Fallback}"); return true; }
                        }
                        else if (problems.Count == 0 && exportLeft) problems.Add("the jobs file expects the export left to Blender (EXPORTLEFT:), and it is made here (take the mark away once it holds)");
                        else if (problems.Count == 0)
                        {
                            if (d.ExportRows.Count == 0) problems.Add("the export: the dump has no X rows for it");
                            else
                            {
                                try
                                {
                                    List<string> mineOut = null; string rowsFile = Environment.GetEnvironmentVariable("RIGANIM_EXPORT_ROWS");
                                    if (!string.IsNullOrEmpty(rowsFile) && Environment.GetEnvironmentVariable("RIGANIM_MODEL_JOB") == key) mineOut = new List<string>();
                                    // RIGANIM_GLB_DIR=<the dump's work dir>: the file Blender wrote (<key>/<key>.glb, the colons underscored), for the first differing byte
                                    byte[] theirGlb = null; string glbDir = Environment.GetEnvironmentVariable("RIGANIM_GLB_DIR");
                                    if (!string.IsNullOrEmpty(glbDir)) { string safe = key.Replace(':', '_'); string gp = Path.Combine(glbDir, safe, safe + ".glb"); if (File.Exists(gp)) theirGlb = File.ReadAllBytes(gp); }
                                    GlbRowsCompare.Compare(problems, "the export's RIGANIM lines", x.Log, d.ExportLogs.Where(l => !l.StartsWith("RIGANIM glb wrote ", StringComparison.Ordinal)).ToList());
                                    var xp = new List<string>();   // every difference named as the export's (the drill's tampers look for the word)
                                    var st = GlbRowsCompare.Compare(x, d.ExportRows, xp, ExportCover, name => BlenderRigExport.ChannelCount(model, name), true, true, theirGlb, mineOut);
                                    foreach (var xpr in xp) problems.Add("the export: " + xpr);
                                    if (mineOut != null) { mineOut.AddRange(BlenderRigExport.DebugBoneRows(model)); File.WriteAllLines(rowsFile, mineOut); }
                                    if (problems.Count == 0)
                                    {
                                        void Cover(string k, long n = 1) { if (n > 0) ExportCover[k] = (ExportCover.TryGetValue(k, out long c) ? c : 0) + n; }
                                        Cover("a job's export compared"); Cover("the GLB byte-identical", st.GlbEqual ? 1 : 0);
                                        Cover("a skinned mesh node", x.Nodes.Count(n => n.Skin >= 0)); Cover("a mesh node without a skin (no vertex groups)", x.Nodes.Count(n => n.Mesh >= 0 && n.Skin < 0));
                                        Cover("the neutral bone (a vertex without a bone)", x.NeutralBone ? 1 : 0); Cover("a mesh of several primitives", x.Meshes.Count(me => me.Primitives.Count > 1));
                                        Cover("joints as shorts (256 bones or more)", x.Meshes.Sum(me => me.Primitives.Count(p => p.Joints != null && p.Joints.Max() >= 256)));
                                        Cover("indices as unsigned ints (65535 vertices or more)", x.Meshes.Sum(me => me.Primitives.Count(p => p.Indices.Length > 0 && p.Indices.Max() >= 65535)));
                                        Cover("a second UV layer", x.Meshes.Sum(me => me.Primitives.Count(p => p.Uv.Count > 1))); Cover("a colour set", x.Meshes.Sum(me => me.Primitives.Count(p => p.Colors.Count > 0)));
                                        Cover("an image (the albedo among them)", st.Images); Cover("several animations", x.Animations.Count > 1 ? 1 : 0); Cover("no animation", x.Animations.Count == 0 ? 1 : 0);
                                        var ch = x.ChannelRows; Cover("a channel reduced to its first and last sample", ch.Reduced); Cover("a channel with every sample", ch.Full); Cover("a channel of one sample", ch.Single);
                                        Cover("a STEP channel", ch.Stepped); Cover("a LINEAR channel", ch.Linear); Cover("a bone holding the pose the script left", ch.HeldBones);
                                        // step (g): the Factory's wrapper over the same stages and export (BlenderRigAnimInProcess) writes this job's file - its
                                        // bytes the dump's GLB row's when they were compared -, names the primary's action and each role's
                                        if (InProcess)
                                        {
                                            string tmpGlb = Path.Combine(Path.GetTempPath(), "riganim_inproc_" + Guid.NewGuid().ToString("N") + ".glb");
                                            try
                                            {
                                                var o = BlenderRigAnimInProcess.Run(d.Argv[0], tmpGlb, argv.Skip(2).ToArray());
                                                if (!o.Written) problems.Add("the in-process wrapper did not write the file: " + (o.ScriptExit ? "the script's exit" : o.UseBlender));
                                                else
                                                {
                                                    if (st.GlbEqual)
                                                    {
                                                        var glbRow = d.ExportRows.First(r => r[0] == "GLB");
                                                        string sha; using (var h = SHA256.Create()) sha = BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(tmpGlb))).Replace("-", "").ToLowerInvariant();
                                                        if (sha != glbRow[4]) problems.Add("the in-process wrapper's file differs from the dump's GLB (sha " + sha + " against " + glbRow[4] + ")");
                                                    }
                                                    else
                                                    {
                                                        // a JPEG albedo: the file carries the source's JPEG where Blender's has its PNG re-encode - it must read back whole
                                                        try
                                                        {
                                                            var back = GlbReader.Read(tmpGlb);
                                                            if (back.Images.Count != x.MaterialRows.Images.Count) problems.Add("the in-process wrapper's file reads back with " + back.Images.Count + " image(s), the export has " + x.MaterialRows.Images.Count);
                                                            else if (back.Images.Any(im => im.Bytes == null || im.Bytes.Length == 0)) problems.Add("the in-process wrapper's file has an image without bytes");
                                                            else if (back.Animations.Count != x.Animations.Count) problems.Add("the in-process wrapper's file reads back with " + back.Animations.Count + " animation(s), the export has " + x.Animations.Count);
                                                            else
                                                            {
                                                                var expectedImages = x.MaterialRows.Images.ToArray();
                                                                try
                                                                {
                                                                    for (int ii = 0; ii < expectedImages.Length; ii++)
                                                                        if (expectedImages[ii].bytes == null)
                                                                        {
                                                                            var im = expectedImages[ii]; var srcIm = model.Model.Images[im.source];
                                                                            x.MaterialRows.Images[ii] = (im.name, BlenderRigAnimInProcess.ImageMime(srcIm.Bytes), srcIm.Bytes, im.source);
                                                                        }
                                                                    var expectedFile = BlenderGlbWriter.Build(x);
                                                                    if (expectedFile.Fallback != null || !File.ReadAllBytes(tmpGlb).SequenceEqual(expectedFile.Glb)) problems.Add("the in-process wrapper's JPEG file differs from the oracle-checked export with its source image bytes");
                                                                    else Cover("the in-process wrapper's file read back (a JPEG albedo: the source's bytes, not Blender's PNG)");
                                                                }
                                                                finally { for (int ii = 0; ii < expectedImages.Length; ii++) x.MaterialRows.Images[ii] = expectedImages[ii]; }
                                                            }
                                                        }
                                                        catch (Exception rx) { problems.Add("the in-process wrapper's file does not read back: " + rx.GetType().Name + ": " + rx.Message); }
                                                    }
                                                    if (o.PrimaryAction != (model.PrimaryAction ?? "")) problems.Add("the in-process wrapper names the primary '" + o.PrimaryAction + "', the model '" + model.PrimaryAction + "'");
                                                    if (!o.RoleActions.SequenceEqual(model.RoleActions)) problems.Add("the in-process wrapper names " + o.RoleActions.Count + " role(s), the model " + model.RoleActions.Count);
                                                    if (!BlenderRigAnimInProcess.ParseRolesSidecar(BlenderRigAnimInProcess.RolesSidecar(o.PrimaryAction, o.RoleActions), out string pr, out var rl) || pr != o.PrimaryAction || !rl.SequenceEqual(o.RoleActions)) problems.Add("the roles sidecar does not read back");
                                                    if ((o.AlbedoBytes != null) != (model.Albedo.path != null)) problems.Add("the in-process wrapper's albedo " + (o.AlbedoBytes != null ? "written" : "not written") + ", the model's " + (model.Albedo.path != null ? "written" : "not written"));
                                                    if (problems.Count == 0) { InProcessEqual++; Cover("the in-process wrapper's file (step (g))"); }
                                                }
                                            }
                                            finally { try { if (File.Exists(tmpGlb)) File.Delete(tmpGlb); } catch { } }
                                        }
                                        if (problems.Count == 0) { Exported++; ExportRows += d.ExportRows.Count; Console.WriteLine($"EXPORT {key}: equal - {st.Nodes} nodes, {st.Joints} joints, {x.Skins.Count} skin(s), {st.Meshes} mesh(es) with {st.Prims} primitives, {st.Materials} materials, {st.Images} images, {st.Anims} animations with {st.Channels} channels" + (st.GlbEqual ? $", the GLB's {st.GlbBytes} bytes" : "")); }
                                    }
                                }
                                catch (InvalidDataException e) { problems.Add("the export's dump: " + e.Message); }
                            }
                        }
                    }
                }
            }
        }
        foreach (var note in model.Notes) Census[note] = (Census.TryGetValue(note, out int c) ? c : 0) + 1;
        if (problems.Count == 0 && left) { Console.WriteLine($"FAIL {key}: the model takes a file its key says is left to Blender (through stage {Math.Min(stages, model.Exits ? 1 : stages)})"); return false; }
        if (problems.Count == 0) { Equal++; return true; }
        foreach (var p in problems) Console.WriteLine($"FAIL {key}: {p}");
        return false;
    }

    public static void Summary(int stages)
    {
        foreach (var kv in Census) Console.WriteLine($"MODEL {kv.Value} {kv.Key}");
        if (Export) { foreach (var kv in ExportCover) Console.WriteLine($"XCOVER {kv.Value} {kv.Key}"); Console.WriteLine($"EXPORTS jobs {Exported} rows {ExportRows} inprocess {InProcessEqual}"); }
        Console.WriteLine($"STAGES {stages} jobs {Compared} equal {Equal} left {Left} rows {RowsEqual} deferred {Deferred}");
    }
}
