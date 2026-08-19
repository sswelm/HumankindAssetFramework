using System.Linq;
using Haf.Schema;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The pack pre-flight validator's PURE rule core (Haf.Schema.PackValidator) — one rule set consumed by the
    // editor "Validate pack" button and the plugin's boot-time pass. These pin: a default entry is clean, every
    // rule class fires on its trigger, tri-state NULL context answers SKIP checks (never guess), and only
    // missing-identity is an ERROR (fail-soft: everything else warns, nothing blocks a load).
    public class PackValidatorTests
    {
        // Configurable stub: null = "can't check" (the default), matching a host with no lookup for that class.
        class Ctx : IValidationContext
        {
            public bool? Pawn = null, Sound = null, Skin = null, Bone = null;
            public bool? PawnExists(string p) => Pawn;
            public bool? SoundFileExists(string f) => Sound;
            public bool? SkinFileExists(string f) => Skin;
            public bool? BoneExists(string b) => Bone;
        }

        static HafModelSchema Valid() => new HafModelSchema { resourceName = "Tank", pawnDescription = "Era5_Common_Tanks_01" };

        [Fact]
        public void DefaultEntry_WithIdentity_IsClean()
        {
            Assert.Empty(PackValidator.ValidateEntry(Valid(), new Ctx()));
        }

        [Fact]
        public void MissingIdentity_IsError_TheOnlyErrorClass()
        {
            var issues = PackValidator.ValidateEntry(new HafModelSchema(), new Ctx());
            Assert.Equal(2, issues.Count(i => i.Severity == ValidationSeverity.Error));
            Assert.Contains(issues, i => i.Field == "resourceName");
            Assert.Contains(issues, i => i.Field == "pawnDescription");
        }

        [Fact]
        public void UnknownPawn_Warns_UnknownableSkips()
        {
            var e = Valid();
            Assert.Contains(PackValidator.ValidateEntry(e, new Ctx { Pawn = false }), i => i.Field == "pawnDescription" && i.Severity == ValidationSeverity.Warning);
            Assert.Empty(PackValidator.ValidateEntry(e, new Ctx { Pawn = null }));   // host can't check -> no guess
            Assert.Empty(PackValidator.ValidateEntry(e, new Ctx { Pawn = true }));
        }

        [Fact]
        public void MissingSoundFile_Warns_AndNamesTheSearchOrder()
        {
            var e = Valid(); e.soundIdleFile = "growl.wav";
            var i = Assert.Single(PackValidator.ValidateEntry(e, new Ctx { Sound = false }));
            Assert.Equal("soundIdleFile", i.Field);
            Assert.Contains("haf_sounds", i.Message);
        }

        [Fact]
        public void NonWavSound_Warns_EvenWhenTheFileExists()
        {
            var e = Valid(); e.soundFile = "engine.mp3";
            var i = Assert.Single(PackValidator.ValidateEntry(e, new Ctx { Sound = true }));
            Assert.Contains("not a .wav", i.Message);
        }

        [Fact]
        public void BoneTypo_Warns_TheClassicSilentFailure()
        {
            var e = Valid(); e.muzzleBone = "Turrret";
            var i = Assert.Single(PackValidator.ValidateEntry(e, new Ctx { Bone = false }));
            Assert.Equal("muzzleBone", i.Field);
            Assert.Contains("silently", i.Message);
        }

        [Theory]
        [InlineData("1,2", false)]
        [InlineData("a,b,c", false)]
        [InlineData("1.5, -2, 0.25", true)]
        [InlineData("", true)]
        public void TripleFormat_IsChecked(string v, bool ok)
        {
            var e = Valid(); e.muzzleOffset = v;
            var issues = PackValidator.ValidateEntry(e, new Ctx());
            Assert.Equal(ok, issues.All(i => i.Field != "muzzleOffset"));
        }

        [Fact]
        public void HandPropGuid_MustBeFourInts_AndNameWithoutGuidWarns()
        {
            var e = Valid(); e.handPropGuid = "1,2,3";
            Assert.Contains(PackValidator.ValidateEntry(e, new Ctx()), i => i.Field == "handPropGuid");
            var e2 = Valid(); e2.handPropName = "M60";
            Assert.Contains(PackValidator.ValidateEntry(e2, new Ctx()), i => i.Field == "handPropName" && i.Message.Contains("no prop will attach"));
        }

        [Theory]
        [InlineData(0f, false)]
        [InlineData(-1f, false)]
        [InlineData(0.001f, true)]
        [InlineData(1f, true)]
        public void ScaleRange_IsChecked(float v, bool ok)
        {
            var e = Valid(); e.scale = v;
            Assert.Equal(ok, PackValidator.ValidateEntry(e, new Ctx()).All(i => i.Field != "scale"));
        }

        [Fact]
        public void VolumeAndTintAndSpreadRanges_AreChecked()
        {
            var e = Valid(); e.soundVolume = 3f; e.tintR = 300f; e.animPhaseSpread = 1.5f; e.deployPoseTime = -0.1f;
            var fields = PackValidator.ValidateEntry(e, new Ctx()).Select(i => i.Field).ToList();
            Assert.Contains("soundVolume", fields);
            Assert.Contains("tintR", fields);
            Assert.Contains("animPhaseSpread", fields);
            Assert.Contains("deployPoseTime", fields);
        }

        [Fact]
        public void StateDriven_ExclusionsAndTurnBankDependency_Warn()
        {
            var e = Valid(); e.animStateDriven = true; e.fireOnAttack = true; e.deployOnStop = true; e.turnBank = 5f;
            var fields = PackValidator.ValidateEntry(e, new Ctx()).Select(i => i.Field).ToList();
            Assert.Contains("fireOnAttack", fields);
            Assert.Contains("deployOnStop", fields);
            Assert.Contains("turnBank", fields);
        }

        [Fact]
        public void PositiveHugDrop_Warns_ExpectsNegative()
        {
            var e = Valid(); e.hugDrop = 2f;
            Assert.Contains(PackValidator.ValidateEntry(e, new Ctx()), i => i.Field == "hugDrop" && i.Message.Contains("NEGATIVE"));
        }

        [Fact]
        public void CombatZ_SaneRangePasses_ExtremeWarns()
        {
            var ok = Valid(); ok.combatZ = -0.5f;   // the submarine combat dive
            Assert.DoesNotContain(PackValidator.ValidateEntry(ok, new Ctx()), i => i.Field == "combatZ");
            var bad = Valid(); bad.combatZ = -12f;
            Assert.Contains(PackValidator.ValidateEntry(bad, new Ctx()), i => i.Field == "combatZ" && i.Severity == ValidationSeverity.Warning);
        }

        [Fact]
        public void EveryIssue_NamesItsField_AndRendersOneLine()
        {
            var e = new HafModelSchema { soundFile = "x.ogg", scale = -1f };
            foreach (var i in PackValidator.ValidateEntry(e, new Ctx { Sound = false }))
            {
                Assert.False(string.IsNullOrEmpty(i.Field));
                Assert.DoesNotContain("\n", i.ToString());
            }
        }
    }
}
