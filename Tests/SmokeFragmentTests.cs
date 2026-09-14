using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The smoke's "descriptor repoint held" fact (2026-09-14): for every entry that appended fragments after
    // registration, the live gpu descriptor must still point at the block the repoint wrote. Pure kernel + verdict.
    public class SmokeFragmentTests
    {
        [Fact]
        public void A_block_that_still_matches_is_counted_as_held()
        {
            var f = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
            UniversalInject.GatherFragmentFact("Bremen", 20, 5, 20, 5, f);
            Assert.Empty(f.FragmentIssues);
            Assert.Equal(1, f.FragmentsChecked);
            var v = UniversalInject.SmokeVerdict(f);
            Assert.True(v.Pass);
            Assert.Contains("1 descriptor repoint(s) held", v.Summary);
        }

        [Fact]
        public void A_moved_or_shrunk_block_fails_the_smoke_and_names_both_values()
        {
            var f = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
            UniversalInject.GatherFragmentFact("Bremen", 20, 5, 10, 3, f);      // the game re-packed: back on the original block
            Assert.Single(f.FragmentIssues);
            Assert.Equal(0, f.FragmentsChecked);
            var v = UniversalInject.SmokeVerdict(f);
            Assert.False(v.Pass);
            Assert.Contains("descriptor repoint(s) undone", v.Summary);
            Assert.Contains("now 10+3, repointed to 20+5", v.Summary);
        }

        [Fact]
        public void An_unreadable_descriptor_is_a_note_not_a_failure()
        {
            var f = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
            UniversalInject.GatherFragmentFact("Bremen", 20, 5, -1, -1, f);
            Assert.Empty(f.FragmentIssues);
            Assert.Single(f.FragmentNotes);
            var v = UniversalInject.SmokeVerdict(f);
            Assert.True(v.Pass);
            Assert.Contains("not verifiable", v.Summary);
        }

        [Fact]
        public void An_entry_that_never_repointed_post_registration_is_not_judged()
        {
            var f = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
            UniversalInject.GatherFragmentFact("Bremen", -1, -1, 3, 9, f);
            Assert.Empty(f.FragmentIssues); Assert.Empty(f.FragmentNotes); Assert.Equal(0, f.FragmentsChecked);
        }
    }
}
