using System.Collections.Generic;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The smoke's "descriptor repoint held" fact (2026-09-14): every fragment we APPENDED to a unit's addon (hand
    // prop, multi-mesh chunks) must still be drawn by the descriptor's live block. Identity is the fragment's mesh
    // NAME, resolved to its CURRENT encoded id on the addon — never position (the game's registration moves the
    // block; the first in-game run flagged two healthy units) and never a remembered encoding (data-scale clones
    // re-encode every fragment in the same Load hook; review of PR #53).
    public class SmokeFragmentTests
    {
        static UniversalInject.SmokeFacts Facts() => new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
        static Dictionary<string, uint> Addon(params (string name, uint enc)[] rows)
        {
            var d = new Dictionary<string, uint>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) d[r.name] = r.enc;
            return d;
        }

        [Fact]
        public void Names_whose_current_ids_are_in_the_live_block_count_as_held_wherever_the_block_moved()
        {
            var f = Facts();
            // the log's own case: 7 chunks appended at 197+7; the game then registered 204+8 = body + our 7
            var names = new List<string> { "Bremen_ModelMesh_B", "Bremen_ModelMesh_C", "Bremen_ModelMesh_D" };
            var addon = Addon(("Bremen_ModelMesh", 1), ("Bremen_ModelMesh_B", 11), ("Bremen_ModelMesh_C", 12), ("Bremen_ModelMesh_D", 13));
            var live = new List<uint> { 1, 11, 12, 13 };
            UniversalInject.GatherFragmentFact("TorpedoBoatDestroyers", names, addon, live, f);
            Assert.Empty(f.FragmentIssues);
            Assert.Equal(1, f.FragmentsChecked);
            var v = UniversalInject.SmokeVerdict(f);
            Assert.True(v.Pass);
            Assert.Contains("1 descriptor repoint(s) held", v.Summary);
        }

        [Fact]
        public void A_data_scale_re_encode_is_not_a_loss_when_the_new_ids_are_drawn()
        {
            // MaybeScaleFragments rebuilt every entry onto scaled clones: same names, NEW encodings, block re-registered
            var f = Facts();
            var names = new List<string> { "M60_DistrictMesh" };
            var addon = Addon(("Body", 0x9001), ("M60_DistrictMesh", 0x9002));   // the prop's id used to be 489993472
            var live = new List<uint> { 0x9001, 0x9002 };
            UniversalInject.GatherFragmentFact("DroneSquadFPV", names, addon, live, f);
            Assert.Empty(f.FragmentIssues);
            Assert.Equal(1, f.FragmentsChecked);
        }

        [Fact]
        public void A_name_whose_current_id_is_not_in_the_block_fails_and_names_it()
        {
            var f = Facts();
            var addon = Addon(("Body", 1), ("Bremen_ModelMesh_B", 0x10), ("Bremen_ModelMesh_C", 0x20));
            UniversalInject.GatherFragmentFact("Bremen", new List<string> { "Bremen_ModelMesh_B", "Bremen_ModelMesh_C" }, addon, new List<uint> { 1, 0x10 }, f);
            Assert.Single(f.FragmentIssues);
            var v = UniversalInject.SmokeVerdict(f);
            Assert.False(v.Pass);
            Assert.Contains("descriptor repoint(s) undone", v.Summary);
            Assert.Contains("no longer draws 1 of 2 appended fragment(s): 'Bremen_ModelMesh_C' (0x00000020 not in the descriptor block)", v.Summary);
        }

        [Fact]
        public void A_dead_entry_on_the_addon_fails_and_says_it_is_dead()
        {
            // a vanilla ReloadFragments rebuilt FragmentEntries from the definition and our prop entry is dead: the
            // NAME is still listed, its encoded id reads 0. Distinct from the name being absent entirely (below) —
            // the two point at different causes, and one message for both cost two wrong diagnoses of one report.
            var f = Facts();
            var addon = Addon(("Body", 1), ("M60_DistrictMesh", 0));
            UniversalInject.GatherFragmentFact("DroneSquadFPV", new List<string> { "M60_DistrictMesh" }, addon, new List<uint> { 1 }, f);
            Assert.Single(f.FragmentIssues);
            Assert.Contains("'M60_DistrictMesh' (addon entry present but its encoded id reads 0 — unloaded since the append)", f.FragmentIssues[0]);
            Assert.Contains("block holds 1 entry(ies)", f.FragmentIssues[0]);
        }

        [Fact]
        public void A_name_missing_from_the_addon_fails_and_says_how_many_entries_it_looked_through()
        {
            // The OTHER half of what used to be one message: the name is not on the addon at all. Both injection
            // paths append to addon.FragmentEntries before any descriptor work, so a name that is simply absent
            // means we are reading a different addon than the one we appended to — a stale reference, not a loss of
            // geometry. The count comes with it, because "not among 2 entries" and "not among 40" read differently.
            var f = Facts();
            var addon = Addon(("Body", 1), ("SomethingElse", 2));
            UniversalInject.GatherFragmentFact("DroneSquadFPV", new List<string> { "M60_DistrictMesh" }, addon, new List<uint> { 1 }, f);
            Assert.Single(f.FragmentIssues);
            Assert.Contains("'M60_DistrictMesh' (not among the addon's 2 fragment entry(ies)", f.FragmentIssues[0]);
            Assert.Contains("stale addon", f.FragmentIssues[0]);
        }

        [Fact]
        public void Unreadable_sources_are_notes_not_failures()
        {
            var f = Facts();
            UniversalInject.GatherFragmentFact("Bremen", new List<string> { "X" }, null, new List<uint> { 1 }, f);
            UniversalInject.GatherFragmentFact("Bremen", new List<string> { "X" }, Addon(("X", 5)), null, f);
            Assert.Empty(f.FragmentIssues);
            Assert.Equal(2, f.FragmentNotes.Count);
            var v = UniversalInject.SmokeVerdict(f);
            Assert.True(v.Pass);
            Assert.Contains("not verifiable", v.Summary);
        }

        [Fact]
        public void An_entry_that_appended_nothing_is_not_judged()
        {
            var f = Facts();
            UniversalInject.GatherFragmentFact("Bremen", new List<string>(), Addon(("Body", 1)), new List<uint> { 1 }, f);
            UniversalInject.GatherFragmentFact("Bremen", null, null, null, f);
            Assert.Empty(f.FragmentIssues); Assert.Empty(f.FragmentNotes); Assert.Equal(0, f.FragmentsChecked);
        }
    }
}
