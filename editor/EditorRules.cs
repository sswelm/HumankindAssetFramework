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

    // TILED MATERIALS (2026-09-17, the Teutonic's decks, hull skin, funnels and masts): a SketchUp-style material
    // repeats a small texture 13 to 1,000 times across a part, relying on texture wrap. An atlas cell cannot wrap,
    // and the fold-into-[0,1) that serves islands parked in one tile smears every triangle that spans several — the
    // deck grain read as a dense hatch. An axis counts as TILED when the material's UV span on it exceeds
    // `TiledSpan` tiles; the cell image is then the texture repeated `repeats` times along that axis (as many as
    // the authored span asks for, capped so each repeat keeps `minRepeatPx` of the texture's own pixels), and the
    // UVs map the part's whole span linearly across the cell — continuous, correctly oriented grain at a coarser
    // repeat ("believable from a distance"). An axis that is not tiled keeps the fold exactly as before.
    public const double TiledSpan = 1.5;

    public static int TileRepeats(double span, int texPixels, int minRepeatPx)
    {
        if (!(span > TiledSpan)) return 1;
        int cap = Math.Max(1, texPixels / Math.Max(1, minRepeatPx));
        return Math.Max(1, Math.Min((int)Math.Round(span), cap));
    }

    // POINT-UV MATERIALS (2026-09-19, the Romanic's deck): a ripped model often paints a part with a textured
    // material whose every face carries the SAME single UV — the texture is used as a colour picker, one texel.
    // Packed as a texture, that point lands on one edge of its atlas cell (the fold maps v = 1 to 0), where the
    // bilinear tap blends the neighbouring cell in and every coarser mip averages the whole image: a tan plank
    // texel read as the image's dark-brown mean (0.61, 0.52, 0.36 for a 0.77, 0.68, 0.52 texel) while the web
    // preview, sampling the wrapped texture at the point, showed the tan. Such a material IS a flat colour — the
    // texel at its point — and packs as an 8 px swatch pinned to its cell centre like any factor-only material.
    // The test is the material's whole UV span fitting inside ONE texel of its own texture on both axes.
    public static bool PointUv(double spanU, double spanV, int texW, int texH)
    {
        if (double.IsNaN(spanU) || double.IsNaN(spanV) || double.IsInfinity(spanU) || double.IsInfinity(spanV)) return false;
        if (spanU < 0 || spanV < 0 || texW < 1 || texH < 1) return false;
        return spanU * texW <= 1.0 && spanV * texH <= 1.0;
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

    // The FUSE REPORT (2026-09-16): the per-group evidence (summary, largest islands, warnings, every stitched
    // candidate's numbers) went into the Workshop's status box as one wall of text — 711 parts in six groups made it
    // unreadable ("some report export would be more useful"). The status keeps one line per group; this file, written
    // beside the output GLB as <output>.fuse-report.txt, holds everything, one item per line, greppable.
    public sealed class FuseGroupReport
    {
        public string Letter; public IList<string> PartNames; public IList<string> Details; public IList<string> Warnings; public bool Changed;
        public IList<string> Islands;   // EVERY island's verdict (Details carries the largest six for the status) — review of 0097bd5
    }

    public static string FuseReport(string sourcePath, string outputPath, double weldPermille, IList<FuseGroupReport> groups)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Model Workshop fuse report\n");
        sb.Append("source: ").Append(sourcePath).Append('\n');
        sb.Append("output: ").Append(outputPath).Append('\n');
        sb.Append("weld: ").Append(weldPermille.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(" permille of the model's length\n");
        sb.Append("groups: ").Append(groups.Count).Append('\n');
        foreach (FuseGroupReport g in groups)
        {
            sb.Append('\n').Append("== group ").Append(g.Letter).Append(" — ").Append(g.PartNames.Count).Append(" part(s)").Append(g.Changed ? "" : " — NOTHING FUSED").Append('\n');
            sb.Append("parts: ").Append(string.Join(", ", g.PartNames)).Append('\n');
            foreach (string w in g.Warnings) sb.Append("WARNING: ").Append(w).Append('\n');
            foreach (string d in g.Details)
            {
                if (g.Islands != null && d.StartsWith("largest islands", StringComparison.Ordinal)) continue;   // the complete list follows instead

                // the "largest islands: a; b; c" and "stitched parts: a; b; c" lines become one item per line
                int colon = d.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0 && (d.StartsWith("largest islands", StringComparison.Ordinal) || d.StartsWith("stitched parts", StringComparison.Ordinal)))
                {
                    sb.Append(d.Substring(0, colon)).Append(":\n");
                    foreach (string item in d.Substring(colon + 2).Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries)) sb.Append("  ").Append(item).Append('\n');
                }
                else sb.Append(d).Append('\n');
            }
            if (g.Islands != null)
            {
                sb.Append("islands (").Append(g.Islands.Count).Append(", largest first):\n");
                foreach (string line in g.Islands) sb.Append("  ").Append(line).Append('\n');
            }
        }
        return sb.ToString();
    }

    // A SPLIT or CUT output keeps every node but the split part's mesh moves into new child nodes (_Part_NNN, _CutA/_CutB),
    // and the parent, meshless, is no longer a row — so its letter resolved to nothing (review of 0097bd5). The letter
    // passes to every mesh-carrying descendant of a marked node; a marked node that still has a mesh keeps its own.
    // `parts`: (node index, parent index or -1) of every mesh-carrying node in the OUTPUT; `letters`: by node index in
    // the source (indices survive a split/cut: nodes are only appended). Returns letters by output node index.
    /// <param name="meshNodes">the nodes to report (mesh-carrying); null = every node in <paramref name="parts"/>. The parent walk uses every entry of <paramref name="parts"/>, meshless ancestors included.</param>
    /// <param name="firstNewNode">the source's node count: a split/cut only APPENDS nodes, so every index at or past it was created by the
    /// operation and inherits the nearest marked ancestor's letter; a node that existed before keeps exactly its own letter, marked or not
    /// (review of 26b4571: a marked hull's unmarked child prop must not join the hull's group because the hull was split).</param>
    public static Dictionary<int, string> TransferLetters(IDictionary<int, string> letters, IEnumerable<KeyValuePair<int, int>> parts, ICollection<int> meshNodes, int firstNewNode)
    {
        var parentOf = new Dictionary<int, int>(); foreach (KeyValuePair<int, int> kv in parts) parentOf[kv.Key] = kv.Value;
        var result = new Dictionary<int, string>();
        foreach (KeyValuePair<int, int> kv in parts)
        {
            if (meshNodes != null && !meshNodes.Contains(kv.Key)) continue;
            if (kv.Key < firstNewNode)
            {   // existed before: its own letter or nothing
                if (letters.TryGetValue(kv.Key, out string own) && !string.IsNullOrEmpty(own)) result[kv.Key] = own;
                continue;
            }
            // created by the operation: walk up through the nodes it created to the FIRST ORIGINAL node — the part that
            // was split or cut — and take its letter or its lack of one. Never further: an unmarked prop split under a
            // marked hull must not hand the hull's letter to its pieces (review of 4caf027).
            int node = kv.Key; var seen = new HashSet<int>();
            while (node >= 0 && seen.Add(node))
            {
                if (node < firstNewNode)
                {
                    if (letters.TryGetValue(node, out string letter) && !string.IsNullOrEmpty(letter)) result[kv.Key] = letter;
                    break;
                }
                node = parentOf.TryGetValue(node, out int up) ? up : -1;
            }
        }
        return result;
    }

    // OUTPUT NAMES THAT CHAIN (2026-09-16, user: "should a cut automatically create a cut postfix?"): a cut's output
    // defaults to <source>_cut.glb, and cutting THAT output again goes to _cut2, _cut3 … instead of refusing (output ==
    // source) or overwriting. Same for _split. Any other name just gets the suffix appended.
    // THE HIGHLIGHT AFTER A ROW LEAVES A FILTERED LIST (2026-09-18, user: "when I change the group so that it disappears
    // from the list, it should select the next item rather than the first"): the row after it, the row before it at the
    // end of the list, nothing when it was alone. The Vehicle Lab's sweep idiom, shared with the Workshop.
    public static int NextHighlight(int idx, int count)
    {
        if (idx < 0 || idx >= count) return -1;
        if (idx + 1 < count) return idx + 1;
        return idx - 1;   // -1 when the list held only this row
    }

    // THE MIRROR OF A PART (2026-09-18, user: "a button to find the mirror item"): the SS Romanic's port fittings are
    // separate nodes under a mirrored chain, and marking one letter per side meant hunting every twin by eye. A twin is
    // the part whose world box is this part's box reflected across the model's centreline: every bound within 3 % of
    // the part's largest dimension. The two sides are often remodelled rather than instanced (Object_6 has 1,262
    // triangles, its twin 1,274), so geometry never enters — only the box. Among several matches (stacked copies of one
    // fitting) the closest box wins, then the closest triangle count. A part that is its own reflection (a keel on the
    // centreline) has no twin: -1 with selfSymmetric set. Unmeasured parts (null boxes) are skipped.
    public static int FindMirror(IList<float[]> mins, IList<float[]> maxs, IList<int> tris, int index, int sideAxis, float centre, out bool selfSymmetric)
    {
        selfSymmetric = false;
        if (index < 0 || index >= mins.Count || mins[index] == null || maxs[index] == null) return -1;
        float[] lo = mins[index], hi = maxs[index];
        float dim = Math.Max(hi[0] - lo[0], Math.Max(hi[1] - lo[1], hi[2] - lo[2]));
        float tol = Math.Max(0.03f * dim, 1e-4f);
        var rlo = (float[])lo.Clone(); var rhi = (float[])hi.Clone();
        rlo[sideAxis] = 2f * centre - hi[sideAxis]; rhi[sideAxis] = 2f * centre - lo[sideAxis];   // the reflected box
        float Score(float[] a, float[] b)
        {
            float worst = 0f;
            for (int c = 0; c < 3; c++) { worst = Math.Max(worst, Math.Abs(a[c] - rlo[c])); worst = Math.Max(worst, Math.Abs(b[c] - rhi[c])); }
            return worst;
        }
        selfSymmetric = Score(lo, hi) <= tol;
        int best = -1; float bestScore = float.PositiveInfinity; int bestTriGap = int.MaxValue;
        for (int i = 0; i < mins.Count; i++)
        {
            if (i == index || mins[i] == null || maxs[i] == null) continue;
            float sc = Score(mins[i], maxs[i]);
            if (sc > tol) continue;
            int gap = Math.Abs(tris[i] - tris[index]);
            bool closer = sc < bestScore - 1e-6f || (Math.Abs(sc - bestScore) <= 1e-6f && gap < bestTriGap);
            if (closer) { best = i; bestScore = sc; bestTriGap = gap; }
        }
        if (best >= 0) selfSymmetric = false;
        return best;
    }

    // The centreline to mirror across, VOTED by the pairs themselves: every two parts with the same box extents and the
    // same position on the other two axes are a candidate mirrored pair, and the midpoint of their side centres is where
    // the centreline would have to be; the cluster of midpoints (within 1 % of the model's side extent) that mirrors the
    // most DISTINCT parts wins. The median of all part centres was the first rule and a review broke it with four parts:
    // a pair at -5/+5, a keel at 0 and one stray fitting at 30.5 gave a median of 2.5 and the exact pair was missed.
    // Counting pairs was the second, and four identical fittings clustered on one side (six pairs among them) outvoted
    // three genuine pairs (three); a part now counts once per cluster however many partners it has there. Nothing to
    // vote (no two parts alike): the median, which a lone keel or a symmetric hull still puts on the centreline.
    public static float MirrorCentre(IList<float[]> mins, IList<float[]> maxs, int sideAxis)
    {
        var idx = new List<int>();
        for (int i = 0; i < mins.Count; i++) if (mins[i] != null && maxs[i] != null) idx.Add(i);
        if (idx.Count == 0) return 0f;
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        foreach (int i in idx) { lo = Math.Min(lo, mins[i][sideAxis]); hi = Math.Max(hi, maxs[i][sideAxis]); }
        float window = Math.Max(0.01f * (hi - lo), 1e-4f);
        var mids = new List<KeyValuePair<float, long>>();   // midpoint, and the pair (i << 32 | j) that voted it
        for (int a = 0; a < idx.Count; a++)
        {
            int i = idx[a]; float[] li = mins[i], hi_ = maxs[i];
            float dim = Math.Max(hi_[0] - li[0], Math.Max(hi_[1] - li[1], hi_[2] - li[2]));
            float tol = Math.Max(0.03f * dim, 1e-4f);
            for (int b = a + 1; b < idx.Count; b++)
            {
                int j = idx[b]; float[] lj = mins[j], hj = maxs[j];
                bool alike = true;
                for (int c = 0; c < 3 && alike; c++)
                {
                    if (Math.Abs((hj[c] - lj[c]) - (hi_[c] - li[c])) > tol) alike = false;                 // same extents
                    else if (c != sideAxis && (Math.Abs(lj[c] - li[c]) > tol || Math.Abs(hj[c] - hi_[c]) > tol)) alike = false;   // same place across the other axes
                }
                if (alike) mids.Add(new KeyValuePair<float, long>(0.25f * (li[sideAxis] + hi_[sideAxis] + lj[sideAxis] + hj[sideAxis]), ((long)i << 32) | (uint)j));
            }
        }
        if (mids.Count == 0)
        {
            var cs = new List<float>(); foreach (int i in idx) cs.Add(0.5f * (mins[i][sideAxis] + maxs[i][sideAxis]));
            cs.Sort();
            return cs.Count % 2 == 1 ? cs[cs.Count / 2] : 0.5f * (cs[cs.Count / 2 - 1] + cs[cs.Count / 2]);
        }
        mids.Sort((x, y) => x.Key.CompareTo(y.Key));
        // slide a window over the sorted midpoints, counting the DISTINCT parts in it (a part enters when its first pair
        // enters and leaves when its last pair leaves); the window mirroring the most parts wins
        var inWindow = new Dictionary<int, int>();
        int bestStart = 0, bestEnd = 0, bestParts = 0, e2 = 0;
        for (int s0 = 0; s0 < mids.Count; s0++)
        {
            while (e2 < mids.Count && mids[e2].Key - mids[s0].Key <= window)
            {
                long pk = mids[e2].Value; int pi = (int)(pk >> 32), pj = (int)(pk & 0xffffffffL);
                inWindow[pi] = inWindow.TryGetValue(pi, out int ci) ? ci + 1 : 1; inWindow[pj] = inWindow.TryGetValue(pj, out int cj) ? cj + 1 : 1;
                e2++;
            }
            if (inWindow.Count > bestParts) { bestParts = inWindow.Count; bestStart = s0; bestEnd = e2; }
            long qk = mids[s0].Value; int qi = (int)(qk >> 32), qj = (int)(qk & 0xffffffffL);
            if (--inWindow[qi] == 0) inWindow.Remove(qi);
            if (--inWindow[qj] == 0) inWindow.Remove(qj);
        }
        return mids[(bestStart + bestEnd) / 2].Key;   // the window's MEDIAN: exact mirrors vote exactly, a near-copy on the same side only nudges a mean
    }

    // THE SIDECAR OF A FUSED OUTPUT (review of PR #63: "the Fuser -> Splitter handoff loses group letters"): every group that
    // fused is now ONE shell, and the shell carries the group's letter under its own node index; a group that produced
    // nothing keeps its parts' lines unchanged (the fuse never renumbers nodes). So a fused output opened in the Splitter
    // still knows its groups: a cut shell's pieces inherit the letter, and the Fuser welds them back if asked.
    public static List<string> FusedOutputSidecarLines(IEnumerable<KeyValuePair<string, IList<KeyValuePair<int, string>>>> groups, IDictionary<string, KeyValuePair<int, string>> fusedNodeByLetter)
    {
        var lines = new List<string>();
        foreach (KeyValuePair<string, IList<KeyValuePair<int, string>>> g in groups)
        {
            if (fusedNodeByLetter.TryGetValue(g.Key, out KeyValuePair<int, string> shell) && shell.Key >= 0) lines.Add(SidecarLine(g.Key, shell.Value, shell.Key));
            else foreach (KeyValuePair<int, string> part in g.Value) if (!string.IsNullOrEmpty(part.Value)) lines.Add(SidecarLine(g.Key, part.Value, part.Key));
        }
        return lines;
    }

    public static string NextOutputName(string baseName, string suffix)
    {
        if (string.IsNullOrEmpty(baseName)) return baseName;
        int at = baseName.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            string tail = baseName.Substring(at + suffix.Length);
            if (tail.Length == 0) return baseName + "2";
            if (int.TryParse(tail, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n))
                return baseName.Substring(0, at) + suffix + (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return baseName + suffix;
    }
}
