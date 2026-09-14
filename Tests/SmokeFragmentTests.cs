using System.Collections.Generic;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The smoke's "descriptor repoint held" fact (2026-09-14): every fragment we APPENDED to a unit's gpu descriptor
    // (hand prop, multi-mesh chunks) must still be drawn by the descriptor's live block — judged by CONTENT (encoded
    // mesh ids), never by position. The first in-game run judged by position and flagged two healthy units: their
    // 0+0 descriptors had been registered by the game AFTER our append, block moved, our entries inside.
    public class SmokeFragmentTests
    {
        static SmokeFactsWrapper Facts() => new SmokeFactsWrapper();
        class SmokeFactsWrapper { public UniversalInject.SmokeFacts F = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 }; }

        [Fact]
        public void Appended_ids_present_in_the_live_block_count_as_held_wherever_the_block_moved()
        {
            var f = Facts().F;
            // the log's own case: we appended 7 chunks at 197+7; the game then registered 204+8 = body + our 7
            var appended = new List<uint> { 11, 12, 13, 14, 15, 16, 17 };
            var live = new List<uint> { 1, 11, 12, 13, 14, 15, 16, 17 };
            UniversalInject.GatherFragmentFact("TorpedoBoatDestroyers", appended, live, f);
            Assert.Empty(f.FragmentIssues);
            Assert.Equal(1, f.FragmentsChecked);
            var v = UniversalInject.SmokeVerdict(f);
            Assert.True(v.Pass);
            Assert.Contains("1 descriptor repoint(s) held", v.Summary);
        }

        [Fact]
        public void A_missing_appended_id_fails_the_smoke_and_names_it()
        {
            var f = Facts().F;
            UniversalInject.GatherFragmentFact("Bremen", new List<uint> { 0x10, 0x20, 0x30 }, new List<uint> { 1, 0x10, 0x30 }, f);
            Assert.Single(f.FragmentIssues);
            Assert.Equal(0, f.FragmentsChecked);
            var v = UniversalInject.SmokeVerdict(f);
            Assert.False(v.Pass);
            Assert.Contains("descriptor repoint(s) undone", v.Summary);
            Assert.Contains("no longer draws 1 of 3 appended fragment(s) [0x00000020]", v.Summary);
        }

        [Fact]
        public void A_block_reverted_to_the_original_body_only_fails()
        {
            var f = Facts().F;
            UniversalInject.GatherFragmentFact("DroneSquadFPV", new List<uint> { 489993472u }, new List<uint> { 1 }, f);   // the M60 prop gone, body alone
            Assert.Single(f.FragmentIssues);
            Assert.Contains("block holds 1 entry(ies)", f.FragmentIssues[0]);
        }

        [Fact]
        public void An_unreadable_descriptor_is_a_note_not_a_failure()
        {
            var f = Facts().F;
            UniversalInject.GatherFragmentFact("Bremen", new List<uint> { 5 }, null, f);
            Assert.Empty(f.FragmentIssues);
            Assert.Single(f.FragmentNotes);
            var v = UniversalInject.SmokeVerdict(f);
            Assert.True(v.Pass);
            Assert.Contains("not verifiable", v.Summary);
        }

        [Fact]
        public void An_entry_that_appended_nothing_is_not_judged()
        {
            var f = Facts().F;
            UniversalInject.GatherFragmentFact("Bremen", new List<uint>(), new List<uint> { 1 }, f);
            UniversalInject.GatherFragmentFact("Bremen", null, new List<uint> { 1 }, f);
            Assert.Empty(f.FragmentIssues); Assert.Empty(f.FragmentNotes); Assert.Equal(0, f.FragmentsChecked);
        }
    }
}
