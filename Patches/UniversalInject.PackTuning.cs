using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace HumankindAssetFramework
{
    // THE PACK TUNING TABLES — unitScales, eraGrid, formationThresholds — parsed from the RESOLVED packs, in load order.
    //
    // Review finding 2026-08-21 (external agent, verified): these three tables were regex-scraped in loops over the raw
    // DISCOVERY list of files, while the models merged over the RESOLVED pack list. So (1) a pack that resolution rejected
    // — duplicate modId, unmet dependsOn — still resized every matching unit; (2) "later packs win" meant later by
    // FILENAME, not the player's Humankind mod order the models follow; (3) unitScales from two packs matching the same
    // unit composed silently (×0.6 × ×0.6 = ×0.36) under a framework whose rule is "no silent overrides".
    //
    // Now: ONE pure function over (modId, text) pairs in resolved order — the caller hands it exactly the packs the
    // models came from — producing the three tables plus NOTES for every cross-pack interaction, which go to
    // haf_load_report.txt and the log. Policy, documented in Multi-Mod.md:
    //   unitScales          — rules MULTIPLY (by design within a pack); across packs both still apply, but a shared
    //                         `match` key is NAMED with the composed factor so a double shrink is never silent.
    //   eraGrid             — a row (unit era) is owned by the LAST pack in mod order that authors it; named per row.
    //   formationThresholds — the whole table is owned by the LAST pack in mod order that authors it; named.
    // The regexes are the originals, moved verbatim (the fallback-parse shape that survives a hand-edited file).
    internal static partial class UniversalInject
    {
        internal static class PackTuning
        {
            internal sealed class Result
            {
                public readonly List<ScaleRule> ScaleRules = new List<ScaleRule>();
                public readonly Dictionary<int, float[]> EraGridRows = new Dictionary<int, float[]>();
                public readonly List<KeyValuePair<float, string>> FormationBySize = new List<KeyValuePair<float, string>>();   // sorted ascending by threshold
                public readonly List<string> Notes = new List<string>();      // cross-pack interactions — for the load report + a log warning each
                public readonly List<string> Warnings = new List<string>();   // a table that failed to parse (the pack's other tables still load)
            }

            static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
            static string F(float v) => v.ToString("0.###", Inv);

            // `packs` = (modId, raw pack JSON text) in RESOLVED load order — pass exactly what the model merge iterated.
            internal static Result Parse(IList<KeyValuePair<string, string>> packs)
            {
                var r = new Result();
                if (packs == null) return r;

                // ---- unitScales: {match, scale[, era]} — all rules apply (multiply); a match key shared across packs is named ----
                var scaleOwners = new Dictionary<string, List<KeyValuePair<string, float>>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pk in packs)
                {
                    try
                    {
                        var arr = Regex.Match(pk.Value ?? "", "\"unitScales\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                        if (!arr.Success) continue;
                        foreach (Match rm in Regex.Matches(arr.Groups[1].Value, "\\{[^{}]*\\}", RegexOptions.Singleline))
                        {
                            var mm = Regex.Match(rm.Value, "\"match\"\\s*:\\s*\"([^\"]*)\"");
                            var ms = Regex.Match(rm.Value, "\"scale\"\\s*:\\s*(-?[\\d.eE+]+)");
                            if (!mm.Success || !ms.Success) continue;
                            var key = mm.Groups[1].Value.Trim();
                            if (key.Length == 0) continue;
                            var mr = Regex.Match(rm.Value, "\"era\"\\s*:\\s*(-?\\d+)");   // optional: the unit's own era (0/absent = read it off the name)
                            int ruleEra = mr.Success && int.TryParse(mr.Groups[1].Value, out int re) && re > 0 ? re : 0;
                            if (!float.TryParse(ms.Groups[1].Value, NumberStyles.Float, Inv, out float sv)) continue;
                            // A NON-POSITIVE SCALE IS REJECTED — and now SAYS SO (2026-08-22).
                            // The pre-extraction parser had `&& sv > 0f` and this extraction dropped it; nothing
                            // downstream re-guards, because Inject.cs multiplies the value as given. The damage is
                            // quiet and total: `"scale": 0` multiplies a SHARED GPU mesh-table entry by zero, so the
                            // unit — and anything sharing that mesh index — collapses to a point and is culled, then
                            // the recorded probe becomes the zero vector and every later pass computes 0/0 = NaN and
                            // re-applies for the rest of the session. `-1` inverts the mesh through the origin and
                            // renders it inside-out. Both from a plausible hand-edit: 0 reads like "disable this rule".
                            // Restoring the guard silently would still leave the author guessing why nothing happened,
                            // and this framework's rule is that nothing is silently disarmed — so it is a WARNING that
                            // names the pack, the key and the value, and the rule is skipped.
                            if (!(sv > 0f))   // written this way so NaN is rejected too, not just <= 0
                            {
                                r.Warnings.Add($"unitScales '{key}' in '{pk.Key}': scale {F(sv)} is not positive — rule IGNORED " +
                                               "(a scale of 0 would collapse the shared mesh buffer, a negative one would render it inside-out; " +
                                               "remove the rule, or use a small positive value)");
                                continue;
                            }
                            r.ScaleRules.Add(new ScaleRule { match = key, scale = sv, era = ruleEra });
                            if (!scaleOwners.TryGetValue(key, out var owners)) scaleOwners[key] = owners = new List<KeyValuePair<string, float>>();
                            owners.Add(new KeyValuePair<string, float>(pk.Key, sv));
                        }
                    }
                    catch (Exception ex) { r.Warnings.Add($"unitScales parse in '{pk.Key}': {ex.Message}"); }
                }
                foreach (var kv in scaleOwners)
                {
                    var distinctPacks = kv.Value.Select(o => o.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (distinctPacks.Count < 2) continue;
                    float product = 1f; foreach (var o in kv.Value) product *= o.Value;
                    r.Notes.Add($"unitScales '{kv.Key}': {string.Join(" and ", kv.Value.Select(o => $"'{o.Key}' x{F(o.Value)}"))} BOTH apply (rules multiply) -> x{F(product)} — make the intent explicit or drop one");
                }

                // ---- eraGrid: rows of {unitEra, scales[]} — a row belongs to the LAST pack (mod order) that authors it ----
                var rowOwner = new Dictionary<int, string>();
                foreach (var pk in packs)
                {
                    try
                    {
                        var arr = Regex.Match(pk.Value ?? "", "\"eraGrid\"\\s*:\\s*\\[(.*)\\]", RegexOptions.Singleline);
                        if (!arr.Success) continue;
                        foreach (Match rm in Regex.Matches(arr.Groups[1].Value, "\\{[^{}]*\"scales\"\\s*:\\s*\\[[^\\]]*\\][^{}]*\\}", RegexOptions.Singleline))
                        {
                            var me = Regex.Match(rm.Value, "\"unitEra\"\\s*:\\s*(\\d+)");
                            var sa = Regex.Match(rm.Value, "\"scales\"\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.Singleline);
                            if (!me.Success || !sa.Success || !int.TryParse(me.Groups[1].Value, out int uEra)) continue;
                            var cells = sa.Groups[1].Value.Split(',')
                                .Select(t => float.TryParse(t.Trim(), NumberStyles.Float, Inv, out float cv) ? cv : 1f)
                                .ToArray();
                            if (cells.Length == 0) continue;
                            if (rowOwner.TryGetValue(uEra, out var prev) && !string.Equals(prev, pk.Key, StringComparison.OrdinalIgnoreCase))
                                r.Notes.Add($"eraGrid unit era {uEra}: '{pk.Key}' overrides '{prev}' (later in mod order wins)");
                            r.EraGridRows[uEra] = cells; rowOwner[uEra] = pk.Key;
                        }
                    }
                    catch (Exception ex) { r.Warnings.Add($"eraGrid parse in '{pk.Key}': {ex.Message}"); }
                }

                // ---- formationThresholds: {threshold, formation} rows — the WHOLE table belongs to the LAST pack that authors one ----
                string tableOwner = null;
                foreach (var pk in packs)
                {
                    try
                    {
                        var arr4 = Regex.Match(pk.Value ?? "", "\"formationThresholds\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                        if (!arr4.Success) continue;
                        var rows = new List<KeyValuePair<float, string>>();
                        foreach (Match rm in Regex.Matches(arr4.Groups[1].Value, "\\{[^{}]*\\}", RegexOptions.Singleline))
                        {
                            var th = Regex.Match(rm.Value, "\"threshold\"\\s*:\\s*([0-9.eE+-]+)");
                            var fm = Regex.Match(rm.Value, "\"formation\"\\s*:\\s*\"([^\"]+)\"");
                            if (th.Success && fm.Success && float.TryParse(th.Groups[1].Value, NumberStyles.Float, Inv, out float tv))
                                rows.Add(new KeyValuePair<float, string>(tv, fm.Groups[1].Value));
                        }
                        if (rows.Count == 0) continue;
                        if (tableOwner != null && !string.Equals(tableOwner, pk.Key, StringComparison.OrdinalIgnoreCase))
                            r.Notes.Add($"formationThresholds: '{pk.Key}' replaces '{tableOwner}' (later in mod order wins)");
                        r.FormationBySize.Clear(); r.FormationBySize.AddRange(rows); tableOwner = pk.Key;
                    }
                    catch (Exception ex) { r.Warnings.Add($"formationThresholds parse in '{pk.Key}': {ex.Message}"); }
                }
                r.FormationBySize.Sort((a, b) => a.Key.CompareTo(b.Key));
                return r;
            }
        }
    }
}
