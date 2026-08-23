using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The registry-vs-config precedence for the strategic footprint (decision 2026-08-23: keep BOTH sources, make
    // the rule explicit and logged). The rule under test, in one sentence: an entry that turns footprintMesh ON
    // claims the district and supplies all five values; otherwise the global config governs all five.
    //
    // These exist because the config branch was, on the shipped pack, unreachable — every district entry set
    // footprintMesh=true, so the else was never executed in-game and a dead-default bug lived in it unnoticed. A
    // branch the game cannot reach is exactly the branch a unit test must.
    public class FootprintPrecedenceTests
    {
        static FootprintValues Entry() => new FootprintValues
        { mesh = true, bw = true, flat = true, hideDecal = false, flatHeight = 0.5f };

        static FootprintValues Global() => new FootprintValues
        { mesh = false, bw = false, flat = false, hideDecal = true, flatHeight = 0.17f };

        [Fact]
        public void EntryWithMeshOn_Wins_AndSuppliesEveryValue()
        {
            var d = FootprintPrecedence.Resolve(true, Entry(), Global(), "Reactor");

            Assert.Equal(FootprintSource.Entry, d.Source);
            // ALL five come from the entry — including the ones that happen to disagree with the config.
            Assert.True(d.Values.mesh);
            Assert.True(d.Values.bw);
            Assert.True(d.Values.flat);
            Assert.False(d.Values.hideDecal);          // config says true; the entry wins
            Assert.Equal(0.5f, d.Values.flatHeight, 4); // config says 0.17; the entry wins
            Assert.Contains("IGNORED", d.Reason);
        }

        [Fact]
        public void EntryWithMeshOff_YieldsToConfig_ForEveryValue()
        {
            var e = Entry();
            e.mesh = false;                 // the entry exists but does not claim the district
            e.flatHeight = 0.9f;            // and must NOT leak its other values through

            var d = FootprintPrecedence.Resolve(true, e, Global(), "Reactor");

            Assert.Equal(FootprintSource.GlobalConfig, d.Source);
            Assert.False(d.Values.mesh);
            Assert.True(d.Values.hideDecal);            // the config's value, not the entry's false
            Assert.Equal(0.17f, d.Values.flatHeight, 4); // not 0.9 — no partial merge
            Assert.Contains("footprintMesh=false", d.Reason);
        }

        [Fact]
        public void NoEntryAtAll_YieldsToConfig_AndSaysSo()
        {
            var d = FootprintPrecedence.Resolve(false, default(FootprintValues), Global(), "Oracle");

            Assert.Equal(FootprintSource.GlobalConfig, d.Source);
            Assert.Equal(0.17f, d.Values.flatHeight, 4);
            Assert.Contains("no registry entry", d.Reason);
        }

        // A 0 (or negative) authored height means "never written", NOT "paper-flat": the live-tuning path clamps to
        // [0.02, 1], so 0 is a value the rest of the system calls illegal. This is the entry-side twin of the
        // dead-default parse bug — the same wrong number arriving by a different road.
        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        public void EntryWithUnwrittenHeight_FallsBackToDefault_NotZero(float authored)
        {
            var e = Entry();
            e.flatHeight = authored;

            var d = FootprintPrecedence.Resolve(true, e, Global(), "Reactor");

            Assert.Equal(FootprintSource.Entry, d.Source);   // it still claims the district
            Assert.Equal(FootprintPrecedence.DefaultFlatHeight, d.Values.flatHeight, 4);
        }

        [Fact]
        public void AuthoredHeight_SurvivesWhenPositive()
        {
            var e = Entry();
            e.flatHeight = 0.02f;   // the low end of the legal range — must NOT be mistaken for unset
            Assert.Equal(0.02f, FootprintPrecedence.Resolve(true, e, Global(), "R").Values.flatHeight, 4);
        }

        // Every outcome names the district and the winning source, because the whole point of the decision was that
        // an operator whose config edit "did nothing" must be able to find out why.
        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        public void Reason_AlwaysNamesTheDistrict(bool present, bool meshOn)
        {
            var e = Entry(); e.mesh = meshOn;
            var d = FootprintPrecedence.Resolve(present, e, Global(), "BreederReactor");
            Assert.Contains("BreederReactor", d.Reason);
            Assert.False(string.IsNullOrWhiteSpace(d.Reason));
        }
    }
}
