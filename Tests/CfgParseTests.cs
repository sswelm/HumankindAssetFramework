using Xunit;

namespace HumankindAssetFramework.Tests
{
    // Plugin.ParseFloat — the ONE way config text becomes a number (policy comment at its definition).
    //
    // Why these exist: the shape it replaced was `float x = 0.17f; float.TryParse(cfg, …, out x);`, which reads as
    // "0.17 unless overridden" and means "0 unless it parses" — `out` is definitely-assigned, so a failed parse
    // overwrites the initializer. Four sites carried it; two were live. The failure is silent, produces a plausible
    // number, and throws nothing, so no guard in the plugin could see it. These tests pin the property the old shape
    // violated: **a failed parse returns the caller's fallback, never zero.**
    public class CfgParseTests
    {
        // THE REGRESSION. Each of these inputs used to yield 0f at the four call sites.
        [Theory]
        [InlineData(null)]        // config entry not bound yet (the CfgFloat null path lands here)
        [InlineData("")]          // key present, value cleared by hand — the likeliest real case
        [InlineData("   ")]
        [InlineData("abc")]
        [InlineData("0,17")]      // comma-decimal locale: HAF parses invariant ON PURPOSE, so this must FALL BACK, not read 17
        [InlineData("0.17f")]
        [InlineData("1/2")]
        public void UnparseableText_ReturnsFallback_NotZero(string text)
        {
            Assert.Equal(0.17f, Plugin.ParseFloat(text, 0.17f), 4);
            Assert.Equal(3.0f, Plugin.ParseFloat(text, 3.0f), 4);   // the mask-size site's fallback
        }

        // The fallback is honoured EVEN WHEN IT IS ZERO — i.e. the result is the caller's number, not a
        // "looks unset so substitute something" heuristic. This is what makes the rotation site safe by rule
        // rather than by the coincidence that its fallback happened to equal the failure value.
        [Fact]
        public void ZeroFallback_IsHonoured_NotTreatedAsUnset()
        {
            Assert.Equal(0f, Plugin.ParseFloat("nonsense", 0f), 4);
        }

        [Theory]
        [InlineData("0.17", 0.17f)]
        [InlineData("0.02", 0.02f)]
        [InlineData("1", 1f)]
        [InlineData("1.0", 1f)]
        [InlineData("-45", -45f)]     // the rotation site takes negatives
        [InlineData("  0.5  ", 0.5f)] // TryParse trims by default; pinned so a future "tighten the parse" doesn't break live cfgs
        [InlineData("1e-2", 0.01f)]
        public void ValidText_Parses(string text, float expected)
        {
            Assert.Equal(expected, Plugin.ParseFloat(text, 999f), 4);
        }

        // An explicitly-authored 0 is a REAL value and must survive the parse. It is then a RANGE question, which is a
        // different concern handled at the call site (the mask-size site's `if (fpSize <= 0f)`), deliberately not here
        // — folding range into the parse would make "the user typed 0" indistinguishable from "the text was garbage",
        // which is the exact conflation the whole fix is about.
        [Fact]
        public void ExplicitZero_Parses_AndIsNotConfusedWithFailure()
        {
            Assert.Equal(0f, Plugin.ParseFloat("0", 0.17f), 4);
            Assert.Equal(0.17f, Plugin.ParseFloat("", 0.17f), 4);
        }

        // Round-trip with the WRITE side of the same policy: anything Plugin.Inv() prints must read back. A drill in
        // 2026-08-20 found the dial echo printing `1,5` on a Dutch machine — the exact spelling its own parser
        // rejects. DialConfig pinned that for dial files; this pins it for the config path.
        [Theory]
        [InlineData(0.17f)]
        [InlineData(0.02f)]
        [InlineData(-45f)]
        [InlineData(1e-2f)]
        public void InvFormatted_RoundTrips(float v)
        {
            string printed = Plugin.Inv($"{v}");
            Assert.Equal(v, Plugin.ParseFloat(printed, float.NaN), 4);
        }

        // CfgFloat's null path — the only line of the pair that isn't pure. A null ConfigEntry (never bound, or a
        // [Debug] key absent from this build) must answer the caller's default, not throw and not zero.
        [Fact]
        public void CfgFloat_NullEntry_ReturnsFallback()
        {
            Assert.Equal(0.17f, Plugin.CfgFloat(null, 0.17f), 4);
        }

        // ---- CfgBool: the five district footprint keys, typed bool since 2026-08-23 ----
        // They were ConfigEntry<string> compared with `== "true"`, which answers FALSE to every value that is not
        // that exact token — blank, "True", "1", "yes", " true". BepInEx now does the parsing, so the only decision
        // left in our code is the null case, and it is the one with teeth: TWO of the five default to TRUE, so a
        // `?? false` would silently INVERT them in the window before Bind runs.
        [Fact]
        public void CfgBool_NullEntry_ReturnsFallback_IncludingTrue()
        {
            Assert.False(Plugin.CfgBool(null, false));
            Assert.True(Plugin.CfgBool(null, true));   // DistrictFootprintMeshHideDecal defaults true — must not invert
        }
    }
}
