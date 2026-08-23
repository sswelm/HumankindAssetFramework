using System.Collections.Generic;
using System.Linq;
using Haf.Schema;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // PACK WRAPPER RULES (2026-08-23). Until now PackValidator had ~30 rules for entry CONTENT — bones, files, pawns,
    // ranges — and NOT ONE for the wrapper: modId / schemaVersion / dependsOn / loadAfter / overrides. A pack whose
    // wrapper was wrong failed soft at runtime (named, graceful) but was never caught at AUTHORING time, which is the
    // only place the author can still fix it.
    //
    // Every rule mirrors behaviour verified in UniversalInjectPatch, not invented policy — see the block comment on
    // PackValidator.ValidatePack for the map from rule to runtime site. These tests therefore double as the record of
    // WHY each rule exists; if the runtime changes, a test here should change with it.
    public class PackWrapperValidationTests
    {
        static PackValidator.PackWrapper W(string modId = "mypack", int schema = 1,
                                           IList<string> dependsOn = null, IList<string> loadAfter = null,
                                           IList<PackValidator.PackOverrideRef> overrides = null)
            => new PackValidator.PackWrapper
            {
                ModId = modId, SchemaVersion = schema,
                DependsOn = dependsOn, LoadAfter = loadAfter, Overrides = overrides,
            };

        static PackValidator.PackOverrideRef Ov(string modId, string pawn)
            => new PackValidator.PackOverrideRef { ModId = modId, Pawn = pawn };

        static List<ValidationIssue> On(string field, List<ValidationIssue> issues)
            => issues.Where(i => i.Field == field).ToList();

        // ---- the happy path must be SILENT, or every rule below is noise ----

        [Fact]
        public void AWellFormedWrapperProducesNothing()
        {
            var issues = PackValidator.ValidatePack(W(
                modId: "mypack", schema: HafSchema.Version,
                dependsOn: new[] { "enc" },
                loadAfter: new[] { "otherpack" },
                overrides: new[] { Ov("otherpack", "Era6_Common_TankDestroyers_01") }));
            Assert.Empty(issues);
        }

        [Fact]
        public void ANullWrapperIsSafe() => Assert.Empty(PackValidator.ValidatePack(null));

        [Fact]
        public void AMinimalLegacyWrapperIsAccepted()
        {
            // schemaVersion 0 = absent = legacy bare pack. Legal: additive evolution, docs/Multi-Mod.md.
            Assert.Empty(PackValidator.ValidatePack(W(modId: "legacy", schema: 0)));
        }

        // ---- modId: the identity other packs write their dependsOn and overrides against ----

        [Fact]
        public void AnEmptyModIdIsFlagged()
        {
            Assert.NotEmpty(On("modId", PackValidator.ValidatePack(W(modId: ""))));
            Assert.NotEmpty(On("modId", PackValidator.ValidatePack(W(modId: null))));
            Assert.NotEmpty(On("modId", PackValidator.ValidatePack(W(modId: "   "))));
        }

        // The runtime TRIMS on load, so a dependsOn written with the untrimmed spelling would silently not match.
        [Fact]
        public void ModIdWithSurroundingWhitespaceIsFlagged()
        {
            var issues = On("modId", PackValidator.ValidatePack(W(modId: " mypack ")));
            Assert.Single(issues);
            Assert.Contains("whitespace", issues[0].Message);
        }

        // `unnamed-pack:` is ParsePack's last-resort placeholder for a pack it could not identify — never a name to author.
        [Fact]
        public void TheRuntimesPlaceholderIdIsFlagged()
        {
            Assert.NotEmpty(On("modId", PackValidator.ValidatePack(W(modId: "unnamed-pack:mine.json"))));
        }

        // ---- schemaVersion: advisory, never a gate ----

        [Fact]
        public void AFutureSchemaIsFlaggedAsSilentlyStrippedKeys()
        {
            var issues = On("schemaVersion", PackValidator.ValidatePack(W(schema: HafSchema.Version + 1)));
            Assert.Single(issues);
            Assert.Equal(ValidationSeverity.Warning, issues[0].Severity);   // NEVER an error — fail-soft stands
        }

        [Fact]
        public void ANegativeSchemaIsFlagged()
            => Assert.NotEmpty(On("schemaVersion", PackValidator.ValidatePack(W(schema: -1))));

        [Fact]
        public void TheCurrentSchemaIsSilent()
            => Assert.Empty(On("schemaVersion", PackValidator.ValidatePack(W(schema: HafSchema.Version))));

        // ---- dependsOn / loadAfter ----

        // Verified in ResolvePacks: a dependsOn that cannot be satisfied means the pack is SKIPPED outright, so
        // depending on yourself is fatal to your own pack — the one wrapper mistake that earns an Error.
        [Fact]
        public void DependingOnYourselfIsAnError()
        {
            var issues = On("dependsOn", PackValidator.ValidatePack(W(modId: "mypack", dependsOn: new[] { "mypack" })));
            Assert.Single(issues);
            Assert.Equal(ValidationSeverity.Error, issues[0].Severity);
        }

        // loadAfter is only an ordering hint, so the same shape is merely useless, not fatal.
        [Fact]
        public void LoadAfterYourselfIsOnlyAWarning()
        {
            var issues = On("loadAfter", PackValidator.ValidatePack(W(modId: "mypack", loadAfter: new[] { "mypack" })));
            Assert.Single(issues);
            Assert.Equal(ValidationSeverity.Warning, issues[0].Severity);
        }

        [Fact]
        public void SelfReferenceIsCaughtRegardlessOfCasing()
            => Assert.NotEmpty(On("dependsOn", PackValidator.ValidatePack(W(modId: "MyPack", dependsOn: new[] { "mypack" }))));

        [Theory]
        [InlineData("dependsOn")]
        [InlineData("loadAfter")]
        public void EmptyAndDuplicateEntriesAreFlagged(string field)
        {
            var list = new[] { "other", "", "other" };
            var w = field == "dependsOn" ? W(dependsOn: list) : W(loadAfter: list);
            var issues = On(field, PackValidator.ValidatePack(w));
            Assert.Equal(2, issues.Count);   // one empty, one duplicate
        }

        // ---- overrides ----

        // Verified in ParsePack: `if (om.Length > 0 && op.Length > 0)` — anything else never reaches the pack, so the
        // author's intent vanishes with NO runtime message naming it. That silence is the whole reason for this rule.
        [Fact]
        public void AnOverrideWithABlankFieldIsFlaggedAsSilentlyDropped()
        {
            var blankMod = On("overrides", PackValidator.ValidatePack(W(overrides: new[] { Ov("", "SomePawn_01") })));
            var blankPawn = On("overrides", PackValidator.ValidatePack(W(overrides: new[] { Ov("other", "") })));
            Assert.Contains("DROPPED", blankMod.Single().Message);
            Assert.Contains("DROPPED", blankPawn.Single().Message);
        }

        [Fact]
        public void OverridingYourOwnPackIsFlagged()
            => Assert.NotEmpty(On("overrides", PackValidator.ValidatePack(
                   W(modId: "mypack", overrides: new[] { Ov("mypack", "SomePawn_01") }))));

        [Fact]
        public void ARepeatedOverrideLineIsFlagged()
        {
            var issues = On("overrides", PackValidator.ValidatePack(W(
                loadAfter: new[] { "other" },
                overrides: new[] { Ov("other", "P_01"), Ov("other", "P_01") })));
            Assert.Single(issues);
            Assert.Contains("repeats", issues[0].Message);
        }

        // THE RULE WORTH HAVING. An override replaces a pawn ALREADY CLAIMED by an earlier-loaded pack, so the
        // overriding pack must load AFTER its target. With no declared constraint the order is whatever the game's
        // module order happens to be — and if this pack lands first, the target's entry arrives later, finds the pawn
        // owned by us, has no matching override of its own, and is dropped as an undeclared CONFLICT. The override
        // then silently does the OPPOSITE of what the author wrote.
        [Fact]
        public void AnOverrideWithNoOrderingConstraintIsFlagged()
        {
            var issues = On("overrides", PackValidator.ValidatePack(W(overrides: new[] { Ov("other", "P_01") })));
            Assert.Single(issues);
            Assert.Contains("loadAfter", issues[0].Message);
        }

        [Theory]
        [InlineData(true)]    // dependsOn also implies ordering
        [InlineData(false)]   // loadAfter is the lighter way to say it
        public void AnOverrideBackedByAnOrderingConstraintIsSilent(bool viaDependsOn)
        {
            var w = viaDependsOn
                ? W(dependsOn: new[] { "other" }, overrides: new[] { Ov("other", "P_01") })
                : W(loadAfter: new[] { "other" }, overrides: new[] { Ov("other", "P_01") });
            Assert.Empty(On("overrides", PackValidator.ValidatePack(w)));
        }

        [Fact]
        public void TheOrderingConstraintIsMatchedCaseAndWhitespaceInsensitively()
        {
            var w = W(loadAfter: new[] { "  Other  " }, overrides: new[] { Ov("other", "P_01") });
            Assert.Empty(On("overrides", PackValidator.ValidatePack(w)));
        }

        // ---- the fail-soft contract: the validator EXPLAINS, it never blocks ----

        // Only ONE wrapper mistake is fatal to the pack at runtime (an unsatisfiable dependsOn => SKIPPED), so only
        // that one may be an Error. If a future rule starts raising Errors for advisory problems, this fails.
        [Fact]
        public void OnlyAnUnsatisfiableSelfDependencyIsAnError()
        {
            var everythingWrong = PackValidator.ValidatePack(W(
                modId: " ", schema: HafSchema.Version + 5,
                dependsOn: new[] { "", "dup", "dup" },
                loadAfter: new[] { "" },
                overrides: new[] { Ov("", ""), Ov("nowhere", "P_01") }));
            Assert.NotEmpty(everythingWrong);
            Assert.All(everythingWrong, i => Assert.Equal(ValidationSeverity.Warning, i.Severity));
        }
    }
}
