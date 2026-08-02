using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The pure era logic behind the Global Era Lab: parse an era out of a name, and the anchor rule that decides
    // whether a unit is re-scaled for the world's age. The invariant here is a USER RULE — a unit is left at 1.0
    // unless an authored grid cell says otherwise; the defaults never invent a scaling curve.
    public class EraScalingTests
    {
        public EraScalingTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
        }

        [Theory]
        [InlineData("LandUnit_Era4_Common_Musketeers", 4)]
        [InlineData("era7_lowercase", 7)]     // case-insensitive
        [InlineData("Era12_MultiDigit", 12)]
        [InlineData("NoEraToken", -1)]
        [InlineData("", -1)]
        [InlineData(null, -1)]
        public void EraFromName_ExtractsEraOrMinusOne(string name, int expected)
        {
            Assert.Equal(expected, UniversalInject.EraFromName(name));
        }

        // now <= homeEra: the unit's own age or earlier -> nothing has aged yet -> 1.0
        [Theory]
        [InlineData(3, 3)]   // its own era
        [InlineData(3, 2)]   // earlier than the unit
        [InlineData(5, 1)]
        public void EraAnchorFor_OwnAgeOrEarlier_IsUnscaled(int homeEra, int now)
        {
            Assert.Equal(1f, UniversalInject.EraAnchorFor(homeEra, now));
        }

        // A later world era with NO authored grid cell must leave the unit alone (the "never invent a curve" rule).
        [Theory]
        [InlineData(3, 5)]
        [InlineData(1, 6)]
        public void EraAnchorFor_LaterEraButUnauthored_IsUnscaled(int homeEra, int now)
        {
            Assert.Equal(1f, UniversalInject.EraAnchorFor(homeEra, now));
        }

        // Neolithic (0) / unknown eras clamp to the first era, so a (0,0) pair is "own age" -> 1.0, no divide-by-anything.
        [Theory]
        [InlineData(0, 0)]
        [InlineData(-4, -4)]
        [InlineData(-2, 10)]   // homeEra clamps to 1, still unauthored -> 1.0
        public void EraAnchorFor_ClampsNonPositiveEras(int homeEra, int now)
        {
            Assert.Equal(1f, UniversalInject.EraAnchorFor(homeEra, now));
        }
    }
}
