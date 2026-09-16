// EditorRules.cs — PURE decision kernels extracted from the Unity-bound editor windows so the plugin test
// suite can compile and lock them (the GlbDisconnectedParts pattern: production compiles this file in the
// Unity editor assembly; Tests/HumankindAssetFramework.Tests.csproj compiles the same source Unity-free).
//
// The rule for what lives here: logic whose WRONGNESS is invisible at use time. The extraction predicate
// below was wrong for a month (review finding 2, 2026-09-07: it demanded a file only multi-material sources
// have, so single-material hand-edits were deleted on every bake) and nothing crashed, logged, or failed —
// the checkbox just didn't do its job. That failure class is exactly what a unit test catches and an eyeball
// doesn't. Keep these kernels free of UnityEngine/UnityEditor types and of file I/O: callers gather the
// facts, the kernel decides.
using System;
using System.Collections.Generic;

/// <summary>Bake-pipeline decisions (UniversalBaker calls these; BakerRulesTests locks them).</summary>
public static class BakerRules
{
    public enum ExtractionAction
    {
        UseExisting = 0,    // extraction on disk matches the current source — skip hygiene and glbconv
        ReExtract = 1,      // stale or missing — delete every derived artifact, run glbconv, restamp
        KeepProtected = 2,  // stale/unstamped BUT keepTexture is on and files exist — keep them, warn (hand-edit protection)
    }

    // The extraction-freshness decision for the animated path's glbconv step (review finding 2, 2026-09-07).
    // `extractedExists` = EITHER extraction shape is on disk (the multi-material MTL or the single
    // `<name>_albedo.png`) — the historic bug was requiring the MTL specifically, which a 1-material source
    // never has, making it permanently "stale": re-extracted every bake, hand-edits deleted, checkbox dead.
    // `stampMatches` = the `.src` stamp exists and equals the current source path + mtime.
    public static ExtractionAction DecideExtraction(bool extractedExists, bool stampMatches, bool keepTexture)
    {
        if (extractedExists && stampMatches) return ExtractionAction.UseExisting;
        if (keepTexture && extractedExists) return ExtractionAction.KeepProtected;
        return ExtractionAction.ReExtract;   // keepTexture cannot protect files that do not exist
    }
}

/// <summary>Natural name ordering — "Object_2" before "Object_10" (Model Workshop part list; NaturalOrderTests).</summary>
public static class NaturalOrder
{
    // "Object_12" -> "Object_": the name with its trailing digits removed. Sort by this first (ordinal,
    // case-insensitive at the call site), then by the trailing number as a NUMBER, then by the full name.
    public static string Prefix(string s)
    {
        int i = s.Length; while (i > 0 && char.IsDigit(s[i - 1])) i--;
        return s.Substring(0, i);
    }

    // "Object_12" -> 12. No trailing digits — or a run too long for long.TryParse (>19 digits) — sorts as -1,
    // i.e. before every numbered sibling of the same prefix.
    public static long Number(string s)
    {
        int i = s.Length; while (i > 0 && char.IsDigit(s[i - 1])) i--;
        return i < s.Length && long.TryParse(s.Substring(i), out long n) ? n : -1;
    }
}

/// <summary>Vehicle Lab decisions over the Blender probe's stdout contract (VehicleLabWindow calls these; VehicleLabRulesTests locks them).</summary>
public static class VehicleLabRules
{
    public sealed class PartRow
    {
        public string Kind;                       // "PART" or "RIGBONE"
        public string Name;
        public int Verts;
        public float[] Center = new float[3];     // probe frame (x, y, z as the script prints them)
        public float[] Size = new float[3];
        public int Vis = -1;                      // escape-ray visibility (1 external / 0 interior), -1 absent
        public string Bone = "";                  // dominant bone (rigged sources), "" absent
        public int Flip = -1;                     // inside-out flip verdict, -1 absent
    }

    // The probe prints POSITIONAL rows:  PART|name|verts|cx,cy,cz|sx,sy,sz|vis|bone|flip  (8 fields, the last three
    // optional — 6th 2026-08-xx, 7th 2026-08-20, 8th 2026-09-13)  and  RIGBONE|name|verts|c|s  (5). Two things went
    // wrong silently before 2026-09-14: a `|` inside a part name shifted every field and the row was dropped, and the
    // parser hard-capped at 8 fields, so a 9th would have emptied the Lab with no log. Now: surplus tokens fold back
    // into the NAME (the only field that can legitimately contain the separator), and a row whose numeric tail then
    // fails to parse is rejected WITH a reason — a new field in the script trips the Lab loudly instead of quietly.
    // Returns false with `reason == null` for lines that are not rows at all (timing lines, blanks): silent skip.
    public static bool TryParsePartLine(string line, out PartRow row, out string reason)
    {
        row = null; reason = null;
        if (string.IsNullOrEmpty(line)) return false;
        var t = line.Trim().Split('|');
        if (t.Length < 2 || (t[0] != "PART" && t[0] != "RIGBONE")) return false;
        bool part = t[0] == "PART";
        int tailMin = 3;                      // verts, centre, size
        int tailMax = part ? 6 : 3;           // + vis, bone, flip
        int given = t.Length - 2;             // tokens after the kind and the first name token
        if (given < tailMin) { reason = $"{t.Length} field(s), expected {2 + tailMin}..{2 + tailMax}"; return false; }
        int nameSpan = given <= tailMax ? 1 : given - tailMax + 1;   // surplus tokens belong to the name
        string name = string.Join("|", t, 1, nameSpan);
        int b = 1 + nameSpan;                 // index of `verts`
        if (!int.TryParse(t[b], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int verts))
        { reason = $"verts '{t[b]}' is not an integer" + (nameSpan > 1 ? " (surplus fields folded into the name — a new column in vehicle_rig.py?)" : ""); return false; }
        var c = t[b + 1].Split(','); var s = t[b + 2].Split(',');
        if (c.Length != 3) { reason = $"centre '{t[b + 1]}' is not x,y,z"; return false; }
        if (s.Length != 3) { reason = $"size '{t[b + 2]}' is not x,y,z"; return false; }
        var r = new PartRow { Kind = t[0], Name = name, Verts = verts };
        for (int i = 0; i < 3; i++) { r.Center[i] = Lenient(c[i]); r.Size[i] = Lenient(s[i]); }
        if (part)
        {
            if (t.Length > b + 3) r.Vis = int.TryParse(t[b + 3], out int vv) ? vv : -1;
            if (t.Length > b + 4) r.Bone = t[b + 4].Trim();
            if (t.Length > b + 5) r.Flip = int.TryParse(t[b + 5], out int fv) ? fv : -1;
        }
        row = r;
        return true;
    }

    // Lenient float: degenerate shards can emit "nan" (python lowercase — .NET rejects it); such a value becomes 0
    // instead of killing the whole probe on one bad line out of thousands.
    public static float Lenient(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;

    // The flat-surface filter's share for one part from the per-renderer sums: the EXACT name first; only when it is
    // absent, the digits-only aliases "<name>.<digits>" (Blender's collision suffixes) merged; -1 when nothing measured
    // (a filter must never hide what it cannot measure). Review P2 (2026-09-13): the old cache stripped ".001" while
    // lookups used the full name, so every suffixed part passed unconditionally — and the strip also ate ".abc".
    public static float FlatShare(string name, IDictionary<string, double> areaByName, IDictionary<string, double> levelByName)
    {
        if (name == null || areaByName == null || levelByName == null) return -1f;
        double a = 0, f = 0;
        if (areaByName.TryGetValue(name, out double ea)) { a = ea; levelByName.TryGetValue(name, out f); }
        else
            foreach (var kv in areaByName)
            {
                if (kv.Key.Length <= name.Length + 1 || kv.Key[name.Length] != '.' || !kv.Key.StartsWith(name, StringComparison.Ordinal)) continue;
                bool digits = true;
                for (int i = name.Length + 1; i < kv.Key.Length; i++) if (!char.IsDigit(kv.Key[i])) { digits = false; break; }
                if (!digits) continue;
                a += kv.Value;
                if (levelByName.TryGetValue(kv.Key, out double lv)) f += lv;
            }
        return a > 0 ? (float)(f / a) : -1f;
    }
}

/// <summary>Model Workshop decisions (WorkshopRulesTests locks them).</summary>
public static class WorkshopRules
{
    // The fuse-groupings sidecar (<source>.glb.fuse.txt). Format v2 (2026-09-16): a header line, then one part per
    // line as "<letter>|<node index>|<name>" — the name is LAST, so every '|' after the second belongs to it and
    // nothing has to be guessed. Two earlier layouts still read: "<letter>|<name>|<node index>" (2026-09-15) and
    // "<letter>|<name>" (before that); both put the name in the middle, and a name that itself ends in "|<number>"
    // cannot be told from a name plus an index — the reader below tries both readings and refuses when both fit
    // a part (review of 6db9a00: "A|Hull|3" after a re-export moved Hull to node 5 while another part was called
    // "Hull|3" landed on the wrong one). Restoring by NAME alone selected every namesake (review of 82088d4), hence
    // the index; a line applies to the row at its index when that row still carries the name, otherwise by name
    // where the name is unique among the rows. Anything else is refused and named, never guessed.
    public const string SidecarHeader = "#fuse-groups v2";

    public static string SidecarLine(string letter, string name, int nodeIndex) =>
        letter + "|" + nodeIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + name;

    /// <param name="rows">(node index, node name) per Workshop row.</param>
    /// <returns>letter by node index; lines that could not be placed are described in <paramref name="refused"/>.</returns>
    public static Dictionary<int, string> ResolveFuseSidecar(IEnumerable<string> lines, IList<KeyValuePair<int, string>> rows, List<string> refused)
    {
        var result = new Dictionary<int, string>();
        var nameAt = new Dictionary<int, string>();
        var countByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<int, string> r in rows)
        {
            nameAt[r.Key] = r.Value;
            if (r.Value == null) continue;
            countByName[r.Value] = countByName.TryGetValue(r.Value, out int c) ? c + 1 : 1;
            indexByName[r.Value] = r.Key;
        }
        if (lines == null) return result;
        int UniqueRow(string name) => name != null && countByName.TryGetValue(name, out int n) && n == 1 ? indexByName[name] : -1;
        int RowAt(int index, string name) => index >= 0 && name != null && nameAt.TryGetValue(index, out string at) && at == name ? index : -1;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        bool v2 = false;
        foreach (string raw in lines)
        {
            string line = raw?.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("#", StringComparison.Ordinal)) { if (line == SidecarHeader) v2 = true; continue; }
            int firstBar = line.IndexOf('|');
            if (firstBar <= 0) continue;
            string letter = line.Substring(0, firstBar).Trim();
            if (letter.Length != 1 || letter[0] < 'A' || letter[0] > 'Z') continue;   // A–Z since 2026-09-16 (was A–H)
            string remainder = line.Substring(firstBar + 1);
            if (remainder.Trim().Length == 0) continue;
            if (v2)
            {
                int secondBar = remainder.IndexOf('|');
                if (secondBar <= 0 || !int.TryParse(remainder.Substring(0, secondBar).Trim(), System.Globalization.NumberStyles.Integer, inv, out int index)) continue;
                string name = remainder.Substring(secondBar + 1).Trim();
                if (name.Length == 0) continue;
                int target = RowAt(index, name); if (target < 0) target = UniqueRow(name);
                if (target >= 0) { result[target] = letter; continue; }
                refused?.Add(countByName.TryGetValue(name, out int n) && n > 1
                    ? "'" + name + "' names " + n + " parts and node " + index.ToString(inv) + " is not one of them — mark them by hand"
                    : "'" + name + "' is not in this file");
                continue;
            }
            // legacy layouts: the name in the middle. Reading 1: "<name>|<index>" split at the last bar; reading 2:
            // the whole remainder is the name. The index settles it when the row there carries the name; otherwise
            // each reading may find a unique part, and two different parts is an ambiguity, not a choice.
            remainder = remainder.Trim();
            int lastBar = remainder.LastIndexOf('|');
            int legacyIndex = -1; string mid = null;
            if (lastBar > 0 && int.TryParse(remainder.Substring(lastBar + 1).Trim(), System.Globalization.NumberStyles.Integer, inv, out int parsed))
            { legacyIndex = parsed; mid = remainder.Substring(0, lastBar).Trim(); }
            int byIndex = RowAt(legacyIndex, mid);
            if (byIndex >= 0 && UniqueRow(remainder) < 0) { result[byIndex] = letter; continue; }
            int byMid = mid != null ? UniqueRow(mid) : -1, byWhole = UniqueRow(remainder);
            var fits = new List<int>(); foreach (int t in new[] { byIndex, byMid, byWhole }) if (t >= 0 && !fits.Contains(t)) fits.Add(t);
            if (fits.Count == 1) { result[fits[0]] = letter; continue; }
            if (fits.Count > 1) { refused?.Add("'" + line + "' fits " + fits.Count + " different parts (an old-format line whose name may end in '|<number>') — mark them by hand"); continue; }
            string shown = mid ?? remainder; int cnt = 0; countByName.TryGetValue(shown, out cnt);
            refused?.Add(cnt > 1
                ? "'" + shown + "' names " + cnt + " parts and node " + (legacyIndex < 0 ? "(none given)" : legacyIndex.ToString(inv)) + " is not one of them — mark them by hand"
                : "'" + shown + "' is not in this file");
        }
        return result;
    }
}
