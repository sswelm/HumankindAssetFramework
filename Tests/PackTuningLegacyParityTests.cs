using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // EXTRACTION PARITY (2026-08-22) — the oracle this extraction should have shipped with.
    //
    // Lifting the tuning-table parsing out of UniversalInjectPatch into the pure PackTuning.Parse was a refactor of
    // SHIPPED behaviour, and PackTuningTests.cs only pins what the code does NOW — it would pass just as happily over
    // a subtly different parser. Its two sibling extractions (DialConfig, PoseMath) each shipped a verbatim-legacy
    // oracle and each oracle CAUGHT a real divergence. This one shipped none, and it dropped the `sv > 0f` guard: a
    // hand-edited `"scale": 0` then multiplied a shared GPU mesh-table entry by zero, silently, for the session.
    //
    // So this file keeps the ORIGINAL inline loop, copied from the pre-extraction UniversalInjectPatch.cs:307
    // (commit 031d2b8), as the oracle, and asserts the new parser produces the same VALUES over a corpus.
    //
    // Diagnostics are deliberately NOT compared: the new parser NAMES what the old one dropped in silence, which is
    // the point of the change. The one intended divergence — the warning on a rejected scale — is asserted explicitly
    // at the bottom rather than smuggled past the comparison.
    public class PackTuningLegacyParityTests
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- THE ORACLE: the pre-extraction loop, verbatim in shape, guard included ----
        static List<(string match, float scale, int era)> LegacyScaleRules(string packText)
        {
            var outp = new List<(string, float, int)>();
            var arr = Regex.Match(packText ?? "", "\"unitScales\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!arr.Success) return outp;
            foreach (Match rm in Regex.Matches(arr.Groups[1].Value, "\\{[^{}]*\\}", RegexOptions.Singleline))
            {
                var mm = Regex.Match(rm.Value, "\"match\"\\s*:\\s*\"([^\"]*)\"");
                var ms = Regex.Match(rm.Value, "\"scale\"\\s*:\\s*(-?[\\d.eE+]+)");
                if (!mm.Success || !ms.Success) continue;
                var key = mm.Groups[1].Value.Trim();
                if (key.Length == 0) continue;
                var mr = Regex.Match(rm.Value, "\"era\"\\s*:\\s*(-?\\d+)");
                int ruleEra = mr.Success && int.TryParse(mr.Groups[1].Value, out int re) && re > 0 ? re : 0;
                if (float.TryParse(ms.Groups[1].Value, NumberStyles.Float, Inv, out float sv) && sv > 0f)   // THE GUARD
                    outp.Add((key, sv, ruleEra));
            }
            return outp;
        }

        // A corpus mixing healthy packs, the shapes a hand-edit produces, and the malformed input a half-saved file
        // passes through. Every entry is one pack's raw text.
        public static IEnumerable<object[]> Corpus() => new[]
        {
            new object[] { "{\"unitScales\":[{\"match\":\"Tank\",\"scale\":0.6}]}" },
            new object[] { "{\"unitScales\":[{\"match\":\"Tank\",\"scale\":0.6,\"era\":5}]}" },
            new object[] { "{\"unitScales\":[{\"match\":\"A\",\"scale\":1.5},{\"match\":\"B\",\"scale\":2}]}" },
            new object[] { "{\"unitScales\":[{\"match\":\"Zero\",\"scale\":0}]}" },              // the hand-edit that started this
            new object[] { "{\"unitScales\":[{\"match\":\"Neg\",\"scale\":-1}]}" },              // inside-out
            new object[] { "{\"unitScales\":[{\"match\":\"NegSmall\",\"scale\":-0.0001}]}" },
            new object[] { "{\"unitScales\":[{\"match\":\"Tiny\",\"scale\":0.0001}]}" },         // positive: must survive
            new object[] { "{\"unitScales\":[{\"match\":\"Sci\",\"scale\":1e-3}]}" },
            new object[] { "{\"unitScales\":[{\"match\":\"  Padded  \",\"scale\":1.2}]}" },      // trimmed key
            new object[] { "{\"unitScales\":[{\"match\":\"\",\"scale\":1.2}]}" },                // empty key: dropped
            new object[] { "{\"unitScales\":[{\"match\":\"NoScale\"}]}" },                       // no scale: dropped
            new object[] { "{\"unitScales\":[{\"scale\":1.2}]}" },                               // no match: dropped
            new object[] { "{\"unitScales\":[{\"match\":\"Bad\",\"scale\":abc}]}" },             // unparsable
            new object[] { "{\"unitScales\":[{\"match\":\"EraZero\",\"scale\":1.1,\"era\":0}]}" },
            new object[] { "{\"unitScales\":[{\"match\":\"EraNeg\",\"scale\":1.1,\"era\":-3}]}" },
            new object[] { "{\"unitScales\":[]}" },
            new object[] { "{\"models\":[]}" },                                                  // table absent
            new object[] { "" },
            new object[] { "{\"unitScales\":[{\"match\":\"Half\",\"scale\":0.5}" },              // truncated mid-save
        };

        [Theory]
        [MemberData(nameof(Corpus))]
        public void NewParser_MatchesTheLegacyLoop_OnEveryCorpusEntry(string packText)
        {
            var legacy = LegacyScaleRules(packText);
            var got = UniversalInject.PackTuning.Parse(
                new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("pack", packText) });

            Assert.Equal(legacy.Count, got.ScaleRules.Count);
            for (int i = 0; i < legacy.Count; i++)
            {
                Assert.Equal(legacy[i].match, got.ScaleRules[i].match);
                Assert.Equal(legacy[i].scale, got.ScaleRules[i].scale, 5);
                Assert.Equal(legacy[i].era, got.ScaleRules[i].era);
            }
        }

        [Fact]
        public void TheRegression_ScaleZero_IsRejected_LikeTheLegacyLoop()
        {
            // Between the extraction and 2026-08-22 this produced ONE rule with scale 0 — which Inject.cs then
            // multiplied into a shared mesh entry. The oracle above is what would have caught it on day one.
            var got = UniversalInject.PackTuning.Parse(
                new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("enc", "{\"unitScales\":[{\"match\":\"Zero\",\"scale\":0}]}") });
            Assert.Empty(got.ScaleRules);
        }

        [Fact]
        public void RejectingAScale_IsNOTSilent_ItNamesPackKeyAndValue()
        {
            // The one intended divergence from the legacy loop: it dropped these without a word.
            var got = UniversalInject.PackTuning.Parse(
                new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("enc", "{\"unitScales\":[{\"match\":\"Zero\",\"scale\":0}]}") });
            var w = Assert.Single(got.Warnings);
            Assert.Contains("Zero", w);
            Assert.Contains("enc", w);
            Assert.Contains("IGNORED", w);
        }

        [Fact]
        public void NegativeAndNaNAreRejectedToo_PositiveSurvives()
        {
            var got = UniversalInject.PackTuning.Parse(new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("p", "{\"unitScales\":[{\"match\":\"Neg\",\"scale\":-1},{\"match\":\"Ok\",\"scale\":0.25}]}")
            });
            var rule = Assert.Single(got.ScaleRules);
            Assert.Equal("Ok", rule.match);
            Assert.Equal(0.25f, rule.scale, 5);
            Assert.Single(got.Warnings);
        }
    }
}
