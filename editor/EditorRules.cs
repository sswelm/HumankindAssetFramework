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
using System.Linq;

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
    // THE PLACEMENT RULE — one definition, both bake paths (2026-09-20; user: "switching between static and
    // animated should give the same result in both facing and offset"). Given the model's box AFTER the bake's
    // rotation, this is where the model has to move: its footprint centred on the origin, its lowest point on the
    // ground. UniversalBaker applies it to the static mesh; rig_anim.py applies the identical arithmetic to the
    // rigged mesh and its bone rests (its "PLACEMENT" block) — the two were written apart and drifted apart, which
    // is exactly the failure this file exists to catch: an off-centre animated bake looks perfectly fine until you
    // bake the same model the other way and it jumps (the steam frigate, 0.97 x 1.84 game units at size 5).
    // SELF-CORRECTING by construction: feed it the box of an already-placed model and every result is 0.
    public static void Placement(double minX, double maxX, double minY, double maxY, double minZ,
                                 out double sway, out double fore, out double raise)
    {
        sway = -(minX + maxX) / 2.0;   // x: sway, centred
        fore = -(minY + maxY) / 2.0;   // y: fore/aft, centred
        raise = -minZ;                 // z: keel/tyre contact to the ground
    }

    // ---- DISTRICT bake outputs (2026-09-21 review) -------------------------------------------------------------
    // The district path writes these four assets into Assets/Resources and, like the unit paths, DELETES them before
    // its fallible steps (DistrictBaker.BakeFxMesh deletes _DistrictMesh and _FxMesh up front, so CreateAsset cannot
    // keep a stale serialized ref). Nothing restored them: a district re-bake that threw left the previous building
    // gone while haf_districts.json still pointed at its guids.
    //
    // They are deliberately NOT added to UniversalBaker.OutputSuffixes, even though that array is described as "the
    // rollback whitelist". It also drives SweepAllOutputs, which DELETES, and which the UNIT bake paths and the
    // Factory's Remove both call — and a unit and a district may legitimately share a resourceName. The sweep
    // already warns about exactly that layering. Folding these in would make baking or removing a unit destroy a
    // same-named district's assets: a worse bug than the one being fixed.
    //
    // CityMapSelector_<name> is also the reason this is a list of BASENAMES rather than suffixes: it is a PREFIX,
    // which a `name + suffix` array cannot express at all.
    public static readonly string[] DistrictOutputSuffixes = { "_DistrictMesh.asset", "_FxMesh.asset", "_Element.asset" };

    /// <summary>Every file a district bake of `name` writes under Assets/Resources, as basenames (no directory).</summary>
    // An empty/whitespace name returns NOTHING rather than the bare suffixes: these basenames are fed to a delete
    // loop, and "" would turn "_FxMesh.asset" and "CityMapSelector_.asset" into real deletion targets.
    public static List<string> DistrictOutputBasenames(string name)
    {
        var outp = new List<string>();
        string n = (name ?? "").Trim();
        if (n.Length == 0) return outp;
        foreach (var s in DistrictOutputSuffixes) outp.Add(n + s);
        outp.Add("CityMapSelector_" + n + ".asset");
        return outp;
    }

    /// <summary>Everything ONE district bake can disturb: the unit outputs its base bake re-creates, plus its own.</summary>
    // THE SCOPE FIX (PR #77 review). A district bake is not only its four assets: step 1 runs the unit baker, which
    // sweeps and re-mints the SHARED outputs for the same resourceName — the atlases among them — and a district
    // entry references those by guid (atlasGuid / normalAtlasGuid / roughAtlasGuid). Backing up only the district's
    // own four therefore restored the previous BUILDING while leaving the registry's atlas guids dangling: the old
    // model back in place, untextured. The rollback set is the union, and the caller passes the unit suffixes in
    // rather than this kernel naming them, so there is exactly one declaration of that list (UniversalBaker's).
    public static List<string> DistrictBakeBasenames(string name, IEnumerable<string> unitSuffixes)
    {
        var outp = DistrictOutputBasenames(name);
        if (outp.Count == 0) return outp;   // blank name: nothing, as above
        string n = (name ?? "").Trim();
        var seen = new HashSet<string>(outp, StringComparer.OrdinalIgnoreCase);
        foreach (var s in unitSuffixes ?? new string[0])
        {
            if (string.IsNullOrEmpty(s)) continue;
            string bn = n + s;
            if (seen.Add(bn)) outp.Add(bn);   // de-duped: the two lists are disjoint today, and stay correct if not
        }
        return outp;
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
    // THE PARENT NAMES OF A CUT PART (2026-09-25): the Cutter names what it makes after what it cut - X_Part_001
    // (Split, Tear), X_CutA / X_CutB (the plane cut) - so a part's ancestors are found by peeling those tails, one
    // at a time, nearest first: Material2_3_Part_001 -> Material2_3. A role kept under any of them is the part's to
    // inherit. A bare _<number> tail is NOT ancestry (review of PR #85, third round): the Cutter's unique renaming
    // makes one, but so does an author who names two parts Hull and Hull_2, and a suffix alone cannot tell them
    // apart - Hull_2 would have taken Hull's role, an Ignore among them, and vanished from the output.
    public static IEnumerable<string> ParentNames(string name)
    {
        if (string.IsNullOrEmpty(name)) yield break;
        string cur = name;
        for (int guard = 0; guard < 16; guard++)
        {
            string next = null;   // the specific tails before the bare number: a greedy "(.*)_\d+" would peel "_001" off "_Part_001"
            foreach (string tail in new[] { @"^(.+)_Part_\d{3}$", @"^(.+)_Cut[AB]$" })
            {
                var m1 = System.Text.RegularExpressions.Regex.Match(cur, tail);
                if (m1.Success) { next = m1.Groups[1].Value; break; }
            }
            if (string.IsNullOrEmpty(next) || next == cur) yield break;
            yield return next; cur = next;
        }
    }

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
    // PER-PART PLACEMENT (2026-09-20, user: "address the floating objects"). The Lab hands vehicle_rig.py one line
    // per placed part — name|ox,oy,oz|sx,sy,sz — through the tagged parttx=@file argument. The NAME may contain the
    // separator (a Sketchfab name can be anything), so BOTH sides split from the RIGHT: the last two fields are the
    // numeric triples and whatever is left is the name — the rule TryParsePartLine learned the hard way. Invariant
    // culture on both sides: a comma decimal would silently corrupt a triple on a German machine.
    // ---- SECOND-MODEL SCALE, PER AXIS (2026-09-21; user: "what I meant by scale is Scale X, Scale Y, Scale Z") ----
    // The second model had ONE uniform scale — enough to reconcile units (a cm file next to a metre one), not enough
    // to fit a part borrowed from another ship: a paddle wheel cut from one hull has to match the new hull's beam AND
    // its freeboard, and those rarely differ by the same factor. Offset and Rotation were already per axis.
    //
    // The scale field of the merge2= argument: "sx,sy,sz". A component that is not a positive finite number becomes 1
    // HERE as well as at the script boundary — zero collapses the model to a plane and a negative one mirrors it,
    // which reverses every triangle's winding and the bake then renders it inside out. Five decimals, invariant:
    // "0.###" once rounded a sub-0.0005 unit factor to a literal 0, and a Dutch locale writes 0,5.
    public static string Merge2ScaleField(float sx, float sy, float sz)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string F(float v) => (v > 0f && !float.IsInfinity(v) && !float.IsNaN(v) ? v : 1f).ToString("0.#####", inv);
        return F(sx) + "," + F(sy) + "," + F(sz);
    }

    // RECIPE MIGRATION. Recipes written before this change carry the single `model2Scale` and no per-axis key, so the
    // per-axis value deserializes to its initializer (1,1,1) and the old number must not be lost: the effective scale
    // is legacy x per-axis, component-wise. A new recipe writes legacy = 1, so the product is simply the per-axis
    // value; an old recipe has per-axis = 1, so the product is the old uniform number on all three axes. Anything
    // non-positive on either side counts as 1 (a hand-edited 0 must not collapse the model).
    public static float[] Model2ScaleOnLoad(float legacyUniform, float sx, float sy, float sz)
    {
        float P(float v) => v > 0f && !float.IsInfinity(v) && !float.IsNaN(v) ? v : 1f;
        float u = P(legacyUniform);
        return new[] { u * P(sx), u * P(sy), u * P(sz) };
    }

    public static string PartPlacementLine(string name, float ox, float oy, float oz, float sx, float sy, float sz)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string F(float v) => v.ToString("0.####", inv);
        return name + "|" + F(ox) + "," + F(oy) + "," + F(oz) + "|" + F(sx) + "," + F(sy) + "," + F(sz);
    }

    public static bool TryParsePartPlacementLine(string line, out string name, out float[] offset, out float[] scale)
    {
        name = null; offset = null; scale = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        int cut2 = line.LastIndexOf('|'); if (cut2 <= 0) return false;
        int cut1 = line.LastIndexOf('|', cut2 - 1); if (cut1 <= 0) return false;
        if (!Triple(line.Substring(cut1 + 1, cut2 - cut1 - 1), out offset) || !Triple(line.Substring(cut2 + 1), out scale)) return false;
        name = line.Substring(0, cut1);
        return name.Length > 0;
    }

    static bool Triple(string s, out float[] v)
    {
        v = null; var f = s.Split(',');
        if (f.Length != 3) return false;
        var r = new float[3];
        for (int i = 0; i < 3; i++)
            if (!float.TryParse(f[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r[i])) return false;
        v = r; return true;
    }

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

    // SIDECARS WRITTEN BEFORE THE UNIQUE RENAMING (review of PR #85, P1): a v2 line names its part as the file did -
    // "B|2|Deck" - and against the renamed rows (node 2 is now Deck_3) the resolver cannot match index 2, falls back
    // to the one row still called Deck, and the B line overwrites A at node 0 while node 2 loses its mark. So before
    // resolving, every line whose index still carries the name the FILE gives it is rewritten to the row's unique
    // name; a line whose name matches neither is left for the resolver to refuse. v1 lines (no index) and comments
    // pass through. `parts`: (node index, the file's name, the unique name) per row.
    // A name the FILE gives to several nodes is settled by the line's index or not at all (review of PR #85, third
    // round): a name-only legacy line "A|Deck", or "B|9|Deck" with node 9 not a Deck, was refused as ambiguous
    // against the file's names and would be taken by the one row still called Deck after the renaming. Such lines
    // are dropped here with a reason in `refused`; a name the file gives to one node passes through untouched.
    public static string[] MigrateSidecarNames(IEnumerable<string> lines, IList<(int index, string fileName, string name)> parts, List<string> refused = null)
    {
        var fileNameAt = new Dictionary<int, string>(); var nameAt = new Dictionary<int, string>(); var fileCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in parts) { fileNameAt[p.index] = p.fileName; nameAt[p.index] = p.name; if (p.fileName != null) fileCount[p.fileName] = fileCount.TryGetValue(p.fileName, out int c) ? c + 1 : 1; }
        bool Shared(string name) => name != null && fileCount.TryGetValue(name, out int c) && c > 1;
        string Refuse(string name, string where) { refused?.Add("'" + name + "' names " + fileCount[name] + " parts in the file and " + where + " — mark them by hand"); return null; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var outLines = new List<string>(); bool v2 = false;
        bool Renamed(int index, string name, out string unique)
        {
            unique = null;
            return fileNameAt.TryGetValue(index, out string fileName) && nameAt.TryGetValue(index, out unique) && fileName == name && unique != null && unique != name;
        }
        foreach (string raw in lines ?? new string[0])
        {
            string line = raw?.Trim() ?? "";
            if (line.StartsWith("#", StringComparison.Ordinal)) { if (line == SidecarHeader) v2 = true; outLines.Add(raw); continue; }
            int firstBar = line.IndexOf('|');
            if (firstBar <= 0) { outLines.Add(raw); continue; }
            string letter = line.Substring(0, firstBar), remainder = line.Substring(firstBar + 1);
            if (v2)
            {
                // "<letter>|<index>|<name>"
                int secondBar = remainder.IndexOf('|');
                string name = secondBar > 0 ? remainder.Substring(secondBar + 1) : remainder.Trim();   // no second bar: a name-only line, which a shared name makes ambiguous
                int index = -1; bool hasIndex = secondBar > 0 && int.TryParse(remainder.Substring(0, secondBar).Trim(), System.Globalization.NumberStyles.Integer, inv, out index);
                if (hasIndex && Renamed(index, name, out string unique)) { outLines.Add(letter + "|" + remainder.Substring(0, secondBar) + "|" + unique); continue; }
                if (Shared(name) && !(hasIndex && fileNameAt.TryGetValue(index, out string at) && at == name)) { Refuse(name, hasIndex ? "node " + index.ToString(inv) + " is not one of them" : "no node index is given"); continue; }
            }
            else
            {
                // the legacy layout, "<letter>|<name>|<index>" (review of PR #85, second round: these passed through and
                // the legacy resolver then found the now unique name at the wrong node); "<letter>|<name>" has no index to go by
                int lastBar = remainder.LastIndexOf('|');
                int index = -1; bool hasIndex = lastBar > 0 && int.TryParse(remainder.Substring(lastBar + 1).Trim(), System.Globalization.NumberStyles.Integer, inv, out index);
                string name = hasIndex ? remainder.Substring(0, lastBar).Trim() : remainder.Trim();
                if (hasIndex && Renamed(index, name, out string unique))
                {
                    // BOTH readings fit (review of PR #85, fourth round): "A|Hull|3" beside two Hulls AND a part literally
                    // named "Hull|3" - the resolver has always refused that line as ambiguous, and it is left exactly as
                    // it is for the resolver to do so; settling the first reading here would hand it to node 3
                    if (fileCount.ContainsKey(remainder.Trim())) { outLines.Add(raw); continue; }
                    outLines.Add(letter + "|" + unique + "|" + remainder.Substring(lastBar + 1)); continue;
                }
                if (Shared(name) && !(hasIndex && fileNameAt.TryGetValue(index, out string at) && at == name)) { Refuse(name, hasIndex ? "node " + index.ToString(inv) + " is not one of them" : "no node index is given"); continue; }
            }
            outLines.Add(raw);
        }
        return outLines.ToArray();
    }

    // GROUP NAMES (2026-09-25, user: "when you have selected a group, it should be possible to name the group in an
    // additional field, which also gets visible in the combo list"): a fuse group's name rides in the groupings
    // sidecar as "#name|F|deck" - a comment to every reader before this one, so an old window ignores it - and names
    // the fused shell: Fused_F_deck instead of Fused_F_<first part>.
    public static string GroupNameLine(string letter, string name) => "#name|" + letter + "|" + (name ?? "").Trim();
    public static Dictionary<string, string> ParseGroupNames(IEnumerable<string> lines)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in lines ?? new string[0])
        {
            string line = raw?.Trim() ?? "";
            if (!line.StartsWith("#name|", StringComparison.Ordinal)) continue;
            string rest = line.Substring("#name|".Length); int bar = rest.IndexOf('|');
            if (bar != 1) continue;
            string letter = rest.Substring(0, 1), name = rest.Substring(bar + 1).Trim();
            if (letter[0] < 'A' || letter[0] > 'Z' || name.Length == 0) continue;
            names[letter] = name;
        }
        return names;
    }
    // the fused shell's name: the group's name when it has one (spaces to underscores, no '|'), else the first part's
    public static string ShellName(string letter, string groupName, string firstPart)
    {
        string g = (groupName ?? "").Trim().Replace('|', '_');
        g = System.Text.RegularExpressions.Regex.Replace(g, @"\s+", "_");
        return "Fused_" + letter + "_" + (g.Length > 0 ? g : firstPart);
    }

    // the "#name|letter|name" lines for the letters a set of sidecar lines carries (review of PR #87: the fused
    // output's sidecar wrote the letters alone, and a named group lost its name when the fused file was reopened)
    public static List<string> GroupNameLinesFor(IEnumerable<string> lines, Func<string, string> nameOf)
    {
        var letters = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string raw in lines ?? new string[0])
        {
            string line = raw?.Trim() ?? ""; int bar = line.IndexOf('|');
            if (line.StartsWith("#", StringComparison.Ordinal) || bar != 1) continue;
            letters.Add(line.Substring(0, 1));
        }
        var outLines = new List<string>();
        foreach (string l in letters) { string nm = nameOf?.Invoke(l); if (!string.IsNullOrWhiteSpace(nm)) outLines.Add(GroupNameLine(l, nm)); }
        return outLines;
    }

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

    // THE SCROLL THAT KEEPS A ROW IN VIEW (2026-09-25, user: "when you have selected a part while a filter is active,
    // and then disable the filter, I expect the selected part to remain selected and in the window"): the highlight did
    // survive a filter change, but the list kept its old scroll offset over a differently ordered list, so the
    // highlighted row landed anywhere. The ↑/↓ keys already scrolled by the rows' measured rects; the same arithmetic,
    // pure: a row above the view scrolls to sit a margin below the top, one below the view a margin above the bottom,
    // one already in view leaves the scroll alone. Rect and scroll are in list-content space.
    public static float RevealScroll(float rowMin, float rowMax, float scrollY, float viewHeight, float margin = 8f)
    {
        if (rowMin < scrollY + margin) return Math.Max(0f, rowMin - margin);
        if (rowMax > scrollY + viewHeight - margin) return Math.Max(0f, rowMax - viewHeight + margin);
        return scrollY;
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

// BAKE TESTS' GOLDEN RULES (2026-09-25, user: "could you add fuse and split and extra generate test to the bake test"):
// the pure half of the Model Workshop and Vehicle Lab rows in Tools > HAF > Bake Tests - what a rig run's snapshot is,
// how two snapshots compare, which recipes are the representatives - so the part that DECIDES is unit-tested and the
// Unity-facing part (WorkshopGateTest, VehicleLabGateTest) only runs and reports.
public static class BakeGoldenRules
{
    /// The deterministic lines of a Vehicle Lab rig run: every line the script prints starting "VEHICLE" except the
    /// timing lines, with the output path cut off the RIG DONE line (the Bake Tests write to a throwaway path).
    /// Trailing whitespace and CR dropped. Never null.
    public static string[] LabSnapshotLines(string stdout)
    {
        var keep = new List<string>();
        foreach (string raw in (stdout ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (!line.StartsWith("VEHICLE", StringComparison.Ordinal) || line.StartsWith("VEHICLE timing:", StringComparison.Ordinal)) continue;
            if (line.StartsWith("VEHICLE RIG DONE", StringComparison.Ordinal))
            {
                int arrow = line.LastIndexOf(" -> ", StringComparison.Ordinal);
                if (arrow > 0) line = line.Substring(0, arrow);
            }
            keep.Add(line);
        }
        return keep.ToArray();
    }

    /// The bone total from "VEHICLE armature: N bones total ...", or -1 when no such line is there.
    public static int ArmatureBones(IEnumerable<string> lines)
    {
        foreach (string line in lines ?? new string[0])
        {
            var m = System.Text.RegularExpressions.Regex.Match(line ?? "", @"^VEHICLE armature: (\d+) bones");
            if (m.Success && int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n)) return n;
        }
        return -1;
    }

    /// null when the two snapshots are the same lines (trailing whitespace, CR and empty lines ignored); otherwise one
    /// sentence naming the FIRST difference - its line number, the expected and the actual text - and the line-count
    /// delta when the two differ in length.
    public static string Diff(IEnumerable<string> golden, IEnumerable<string> got)
    {
        var g = Norm(golden); var t = Norm(got);
        string counts = g.Count != t.Count ? $" ({g.Count} golden line(s), {t.Count} now)" : "";
        int n = Math.Min(g.Count, t.Count);
        for (int i = 0; i < n; i++)
            if (g[i] != t[i]) return $"line {i + 1}: expected '{g[i]}' but got '{t[i]}'" + counts;
        if (g.Count > t.Count) return $"line {n + 1}: expected '{g[n]}' but the run ended" + counts;
        if (t.Count > g.Count) return $"line {n + 1}: unexpected '{t[n]}'" + counts;
        return null;
    }
    static List<string> Norm(IEnumerable<string> lines) =>
        (lines ?? new string[0]).Select(l => (l ?? "").TrimEnd('\r', ' ', '\t')).Where(l => l.Length > 0).ToList();

    /// The Workshop row's fuse group: the parts (index, triangles) that FIT a triangle budget, largest first - a part
    /// over the remaining budget is skipped and the smaller ones still fill it, so the group has several parts
    /// whenever several fit (review of PR #90: taking the first part regardless made a single-part fuse of any hull
    /// over the budget, and could take far longer than advertised). When no part fits at all, the smallest one alone.
    /// A multi-part group whenever ANY two parts fit together (second review: 100/90/60 at a budget of 150 picked the
    /// 100 alone while 90+60 was a two-part fuse inside the budget): when the largest-first fill ends with one part,
    /// the largest pair that fits seeds the group and the rest fill in.
    public static List<int> FuseSelection(IEnumerable<KeyValuePair<int, int>> partsByIndexAndTriangles, int budget)
    {
        var sorted = (partsByIndexAndTriangles ?? new KeyValuePair<int, int>[0]).OrderByDescending(p => p.Value).ThenBy(p => p.Key).ToList();
        List<int> Fill(IList<KeyValuePair<int, int>> seed)
        {
            var picked = seed.Select(p => p.Key).ToList(); long sum = seed.Sum(p => (long)p.Value);
            foreach (var p in sorted)
                if (!picked.Contains(p.Key) && sum + p.Value <= budget) { picked.Add(p.Key); sum += p.Value; }
            return picked;
        }
        var result = Fill(new KeyValuePair<int, int>[0]);
        if (result.Count < 2 && sorted.Count > 1)
            for (int i = 0; i < sorted.Count && result.Count < 2; i++)
                for (int j = i + 1; j < sorted.Count; j++)
                    if ((long)sorted[i].Value + sorted[j].Value <= budget) { result = Fill(new[] { sorted[i], sorted[j] }); break; }
        if (result.Count == 0 && sorted.Count > 0) result.Add(sorted[sorted.Count - 1].Key);   // nothing fits: the smallest alone
        return result;
    }

    /// The Vehicle Lab rows' REPRESENTATIVE recipes: for each feature, in this order - oars, a gun, wheels, sails, a
    /// rotor (main or tail), tracks - the first recipe (in the given order) marking it that is not already picked.
    /// `rolesOf` gives the role names a recipe marks (VehicleLabWindow.ReadRecipe).
    public static List<string> Representatives(IEnumerable<string> recipes, Func<string, ISet<string>> rolesOf)
    {
        var order = (recipes ?? new string[0]).ToList();
        var roles = order.ToDictionary(r => r, r => rolesOf(r) ?? new HashSet<string>());
        var picked = new List<string>();
        foreach (var feature in new[] { new[] { "Oar" }, new[] { "Gun" }, new[] { "Wheel" }, new[] { "Sail" }, new[] { "Rotor", "TailRotor" }, new[] { "Caterpillar" } })
        {
            string hit = order.FirstOrDefault(r => !picked.Contains(r) && feature.Any(f => roles[r].Contains(f)));
            if (hit != null) picked.Add(hit);
        }
        return picked;
    }
}

// ANIMATION LAB RULES (2026-09-26, user: "I have no idea if the elevation axis is the correct one, so in the preview
// could you add a slider that allows me to raise the turret from min to max"): the pure half of the Lab's elevation
// preview - which bone the runtime will elevate, and by how much - so the preview cannot disagree with the game on
// the part that can be reasoned about.
public static class AnimationLabRules
{
    /// The bone the runtime elevates: the Turret bone when one is set, else the Gun (muzzle) bone. NOT trimmed, and
    /// tested with IsNullOrEmpty, exactly as ApplyGunElevation reads them (review of PR #92: trimming here made a name
    /// with a stray space preview as working and fail in the game with "gun bone not found" - the very failure this
    /// preview exists to catch).
    public static string ElevationBoneName(string turretBone, string muzzleBone) =>
        !string.IsNullOrEmpty(turretBone) ? turretBone : (muzzleBone ?? "");

    /// True when a configured bone name only matches once something trims it: the game will not find it.
    public static bool NameNeedsTrimming(string configured) =>
        !string.IsNullOrEmpty(configured) && configured != configured.Trim();

    /// The runtime takes the FIRST bone whose name contains the configured one, case-insensitive, walking the
    /// skeleton's bone array - which the conversion sorts by name, and the bake's b###_ prefix makes that the rig's
    /// own order (Patches/UniversalInject.Pose.cs, ApplyGunElevation). The preview must land on the SAME bone, so it
    /// walks the rig's names in ordinal order and takes the first hit. It must NOT prefer an exact match: with
    /// "b005_GunShield" before "b012_Gun", preferring the exact name turned the barrel here and the shield in the
    /// game, which is exactly backwards for a preview meant to prove the configuration (review of PR #92).
    public static string PickElevationBone(IEnumerable<string> boneNames, string configured)
    {
        if (string.IsNullOrEmpty(configured)) return null;
        return (boneNames ?? new string[0]).Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault(n => n.IndexOf(configured, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// The angle the runtime writes for a fraction of the configured max: NEGATED, as ApplyGunElevation does (a
    /// positive rotation about the gun bone's pitch axis points the muzzle down in the engine's frame, so a positive
    /// max must apply a negative angle to raise it; a negative max flips, as in the game).
    public static float ElevationAngle(float gunElevMax, float fraction) => -gunElevMax * Math.Min(1f, Math.Max(0f, fraction));

    /// THE GUN'S SPAN, as the RIGGER measured it — parsed from the "VEHICLE GUNSPAN ..." line vehicle_rig.py prints at
    /// Generate and the Vehicle Lab keeps beside the output GLB as &lt;glb&gt;.gun.txt (2026-09-26, review of PR #92; the
    /// editor used to re-derive it and got a different set of vertices and a different rule at the 0.5 default).
    /// Bone-local SOURCE units: the breech and the muzzle relative to the bone's head — so the head, and therefore the
    /// rig's own pivot, is the origin — plus the whole assembly's extent along that direction, which is what lets the
    /// editor recover the bake's scale by measuring the same extent on the baked rig. False when the text is not
    /// that line, or carries a field it cannot read.
    public static bool ParseGunSpan(string text, out string bone, out double[] breech, out double[] muzzle, out double extent)
    {
        bone = null; breech = null; muzzle = null; extent = 0;
        foreach (string raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("VEHICLE GUNSPAN", StringComparison.Ordinal)) continue;
            string b = SpanField(line, "bone="), br = SpanField(line, "breech="), mz = SpanField(line, "muzzle="), ex = SpanField(line, "extent=");
            if (string.IsNullOrEmpty(b) || !SpanTriple(br, out breech) || !SpanTriple(mz, out muzzle)
                || !SpanNumber(ex, out extent) || extent <= 0 || SpanLength(breech, muzzle) <= 1e-9)
            { bone = null; breech = null; muzzle = null; extent = 0; return false; }
            bone = b;
            return true;
        }
        return false;
    }
    static string SpanField(string line, string key)
    {
        int i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        int start = i + key.Length, end = line.IndexOf(' ', start);
        return end < 0 ? line.Substring(start) : line.Substring(start, end - start);
    }
    static bool SpanNumber(string s, out double v) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)
        && !double.IsNaN(v) && !double.IsInfinity(v);
    static double SpanLength(double[] a, double[] b) =>
        Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
    static bool SpanTriple(string s, out double[] v)
    {
        v = null;
        var parts = (s ?? "").Split(',');
        if (parts.Length != 3) return false;
        var o = new double[3];
        for (int i = 0; i < 3; i++) if (!SpanNumber(parts[i], out o[i])) return false;
        v = o; return true;
    }

    /// Where a point sits along breech->muzzle, as the fraction the Vehicle Lab's dial speaks: 0 at the breech, 1 at
    /// the muzzle (the projection onto the segment, unclamped so an origin outside the span reads as such).
    public static double PivotFraction(double[] point, double[] breech, double[] muzzle)
    {
        double dx = muzzle[0] - breech[0], dy = muzzle[1] - breech[1], dz = muzzle[2] - breech[2];
        double len2 = dx * dx + dy * dy + dz * dz;
        if (len2 <= 1e-18) return 0.5;
        return ((point[0] - breech[0]) * dx + (point[1] - breech[1]) * dy + (point[2] - breech[2]) * dz) / len2;
    }

    /// The point at a fraction of breech->muzzle.
    public static double[] PivotPoint(double[] breech, double[] muzzle, double fraction) =>
        new[] { breech[0] + (muzzle[0] - breech[0]) * fraction, breech[1] + (muzzle[1] - breech[1]) * fraction, breech[2] + (muzzle[2] - breech[2]) * fraction };
}
