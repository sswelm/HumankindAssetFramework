using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The registry write inside the bake (editor/EditorRules.cs: BakerRules.AfterCommit / CommitError, the pure half of
    // UniversalBaker.Transact). Before 2026-10-31 the rollback copy of a unit's previous assets was discarded the moment
    // the bake succeeded, before the registry entry was written: a save that then failed kept the NEW assets while the
    // registry kept the OLD entry naming the ones just replaced. The three outcomes of a save each have one verdict.
    public class BakeCommitTests
    {
        [Fact]
        public void A_saved_entry_commits_the_bake()
        {
            Assert.Equal(BakerRules.CommitVerdict.Committed, BakerRules.AfterCommit(RegistryRules.SaveOutcome.Saved));
            Assert.Null(BakerRules.CommitError(BakerRules.CommitVerdict.Committed));
        }

        [Fact]
        public void A_refused_save_rolls_the_bake_back()
        {
            // Refused = nothing was written (a lock, a conflict with another editor's version, a guard): the previous
            // assets go back, and the entry on disk keeps naming assets that exist.
            Assert.Equal(BakerRules.CommitVerdict.RolledBack, BakerRules.AfterCommit(RegistryRules.SaveOutcome.Refused));
            Assert.Contains("rolled back", BakerRules.CommitError(BakerRules.CommitVerdict.RolledBack));
            Assert.Contains("previous model is untouched", BakerRules.CommitError(BakerRules.CommitVerdict.RolledBack));
        }

        [Fact]
        public void An_unsettled_save_keeps_the_new_assets_and_says_so()
        {
            // Unknown = the replace could not be settled and the source MAY hold this save: rolling the assets back could
            // strand an entry that names them, so they stay and the user is sent to the source.
            Assert.Equal(BakerRules.CommitVerdict.Unsettled, BakerRules.AfterCommit(RegistryRules.SaveOutcome.Unknown));
            string error = BakerRules.CommitError(BakerRules.CommitVerdict.Unsettled);
            Assert.Contains("COULD NOT BE SETTLED", error);
            Assert.Contains("new assets are kept", error);
            Assert.DoesNotContain("rolled back", error);
        }
    }
}
