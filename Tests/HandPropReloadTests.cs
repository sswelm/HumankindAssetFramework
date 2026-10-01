using System.Collections.Generic;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The hand prop across TWO Load passes (2026-10-01). The F8 smoke kept failing on a Drone Squad that was visibly
    // holding its M60:
    //
    //   FAIL (1 descriptor repoint(s) undone: 'DroneSquadFPV' descriptor no longer draws 1 of 1 appended fragment(s):
    //   'M60_DistrictMesh' (no live fragment entry on the addon) - block holds 2 entry(ies))
    //
    // The game calls PresentationPawnDefinitionAddOn.Load more than once per session and our postfix runs on each. For a
    // definition first reached through Load itself it is structural, not incidental: Load finds PawnDefinitionId == -1
    // and calls PawnManager.RegisterPawnDefinition, which assigns the id, calls Load AGAIN (the inner call - our pass
    // 1, the descriptor not yet populated), snapshots the addon into the GPU descriptor and returns into the outer
    // Load, whose postfix is our pass 2. The log shows exactly that: two MATCH lines back to back, the first with
    // "descriptor[86] not populated yet", the second with StartFragment=448 FragmentCount=2.
    // Pass 1 appends the prop (its mesh lives in the PROP's collection) and the registration snapshot draws it. Pass 2
    // ran ReloadFragments over EVERY entry - the prop's included - pointing it at OUR skeleton and calling Load: the
    // skeleton holds no mesh of that name, GetFxMeshIndex answers 0, and FragmentEntry.Load then writes
    // EncodedMeshAndVisualParticleCount = 0 and BoneIndex = 0. InjectHandProp found the name still present and
    // returned. Dead on the addon, alive in the GPU snapshot (the live sync skips a zero): the soldier holds the gun
    // and the smoke, which reads the addon, says it is gone.
    //
    // The fakes below carry the member names ReloadFragments reflects on, and FakeFragment.Load is the game's
    // FragmentEntry.Load as decompiled (Amplitude.Mercury.Animation), less the layer registration. InjectHandProp
    // itself needs mounted asset bundles and cannot run here: step 2 of the test does by hand what it does - construct
    // against the prop's collection, Load, append.
    public class HandPropReloadTests
    {
        sealed class FakeCollection
        {
            public string name;
            public Dictionary<string, uint> meshes = new Dictionary<string, uint>();
            public Dictionary<string, int> bones = new Dictionary<string, int>();
            public uint GetFxMeshIndex(string meshName) => meshes.TryGetValue(meshName, out uint i) ? i : 0u;   // 0 = not in this collection, silently
            public int GetBoneIndex(string boneName) => bones.TryGetValue(boneName, out int i) ? i : 0;
        }

        struct FakeFragment
        {
            public uint EncodedMeshAndVisualParticleCount;
            public uint BoneIndex;
            public int SlotIndex;
            private FakeCollection meshCollection;
            private string meshName;
            private string boneName;
            private object fxOutputLayer;
            public object FxOutputLayer => fxOutputLayer;
            public FakeCollection Collection => meshCollection;

            public FakeFragment(int slotIndex, FakeCollection meshCollection, string meshName, object fxOutputLayer, string boneName)
            {
                SlotIndex = slotIndex; EncodedMeshAndVisualParticleCount = 0u; BoneIndex = 0u;
                this.meshCollection = meshCollection; this.meshName = meshName; this.boneName = boneName; this.fxOutputLayer = fxOutputLayer;
            }

            public bool Load(FakeCollection skeleton, object renderer, object meshContentManager, int meshLayerIndex)
            {
                if (fxOutputLayer == null) return false;
                uint index = 0u;
                if (!string.IsNullOrEmpty(meshName)) index = meshCollection != null ? meshCollection.GetFxMeshIndex(meshName) : skeleton.GetFxMeshIndex(meshName);
                if (index != 0)
                {
                    EncodedMeshAndVisualParticleCount = 0x1D000000u + index;
                    BoneIndex = !string.IsNullOrEmpty(boneName) ? (uint)skeleton.GetBoneIndex(boneName) : 0u;
                }
                else { EncodedMeshAndVisualParticleCount = 0u; BoneIndex = 0u; }
                return true;
            }
        }

        sealed class FakeAddon { public FakeFragment[] FragmentEntries; public int PawnDefinitionId = -1; }
        sealed class FakeAnimationManager { public object FxComponentRenderer = new object(); public object FxComponentMeshContentManager = new object(); public int FXMeshLayerIndex = 2; }

        const string Body = "Unit_Era6_Australia_AllTerrainAPCs_01", Prop = "M60_DistrictMesh";

        static (FakeAddon addon, FakeAnimationManager mgr, FakeCollection ours, FakeCollection props, ModelEntry e) Scene()
        {
            if (Plugin.Log == null) Plugin.Log = new BepInEx.Logging.ManualLogSource("test");
            var donor = new FakeCollection { name = "donor" }; donor.meshes[Body] = 67;
            var ours = new FakeCollection { name = "ours" }; ours.meshes[Body] = 123; ours.bones["R_Hand"] = 41;
            var props = new FakeCollection { name = "M60_Collection" }; props.meshes[Prop] = 77;
            var addon = new FakeAddon { FragmentEntries = new[] { new FakeFragment(0, donor, Body, new object(), "") } };
            addon.FragmentEntries[0].Load(donor, null, null, 2);
            var e = new ModelEntry { resourceName = "DroneSquadFPV", handPropGuid = "1,2,3,4", handPropName = "M60" };
            return (addon, new FakeAnimationManager(), ours, props, e);
        }

        // what InjectHandProp does, by hand: construct against the PROP's collection, Load, append, and record where
        static void AppendHandProp(FakeAddon addon, FakeCollection ours, FakeCollection props, ModelEntry e)
        {
            var item = new FakeFragment(0, props, Prop, new object(), "R_Hand");
            item.Load(ours, null, null, 2);
            var grown = new FakeFragment[addon.FragmentEntries.Length + 1];
            addon.FragmentEntries.CopyTo(grown, 0); grown[grown.Length - 1] = item;
            addon.FragmentEntries = grown;
            UniversalInject.NoteAppended(e, addon, grown.Length - 1, Prop, handProp: true).DefId = 86;   // 86: the descriptor was readable, so the smoke judges it
        }

        // GatherFragmentFacts as the smoke runs it, with the descriptor's live block handed in (the real one reads PawnManager)
        static UniversalInject.SmokeFacts Smoke(ModelEntry e, IDictionary<object, List<uint>> liveByAddon)
        {
            var f = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
            foreach (var set in UniversalInject.AppendedByAddon(e))
                UniversalInject.GatherFragmentFact(e.resourceName, set.names, UniversalInject.ReadAppendedEncs(set.addon, set.records), set.addon != null && liveByAddon.TryGetValue(set.addon, out var live) ? live : null, f);
            return f;
        }
        static UniversalInject.SmokeFacts Smoke(ModelEntry e, FakeAddon addon, List<uint> live) => Smoke(e, new Dictionary<object, List<uint>> { [addon] = live });
        static List<uint> Encs(FakeAddon addon) { var l = new List<uint>(); foreach (var fr in addon.FragmentEntries) l.Add(fr.EncodedMeshAndVisualParticleCount); return l; }

        [Fact]
        public void The_hand_prop_keeps_its_own_collection_and_its_encoding_when_the_addon_loads_again()
        {
            var (addon, mgr, ours, props, e) = Scene();
            UniversalInject.ReloadFragments(addon, mgr, ours, e);                   // pass 1: the body moves onto our skeleton
            Assert.Equal(0x1D000000u + 123, addon.FragmentEntries[0].EncodedMeshAndVisualParticleCount);
            AppendHandProp(addon, ours, props, e);
            Assert.Equal(0x1D000000u + 77, addon.FragmentEntries[1].EncodedMeshAndVisualParticleCount);
            var live = new List<uint> { addon.FragmentEntries[0].EncodedMeshAndVisualParticleCount, addon.FragmentEntries[1].EncodedMeshAndVisualParticleCount };   // the registration snapshot

            UniversalInject.ReloadFragments(addon, mgr, ours, e);                   // pass 2: the game Loads the addon again

            Assert.Same(props, addon.FragmentEntries[1].Collection);                // not re-pointed at our skeleton
            Assert.Equal(0x1D000000u + 77, addon.FragmentEntries[1].EncodedMeshAndVisualParticleCount);
            Assert.Equal(41u, addon.FragmentEntries[1].BoneIndex);
            Assert.Same(ours, addon.FragmentEntries[0].Collection);                 // the body still is
            Assert.Equal(0x1D000000u + 123, addon.FragmentEntries[0].EncodedMeshAndVisualParticleCount);
            Assert.True(e.fragsLogged);                                             // set on ReloadFragments' last line: it ran to its end, nothing was swallowed

            // ... and the smoke, reading that addon as it does in the game, calls the repoint held
            var f = Smoke(e, addon, live);
            Assert.Empty(f.FragmentIssues);
            Assert.Equal(1, f.FragmentsChecked);
        }

        [Fact]
        public void A_donor_attachment_from_another_collection_is_still_moved_onto_our_skeleton()
        {
            // unchanged behaviour, pinned: a DONOR's own attachment (its rifle, from the weapons collection) is re-pointed
            // like every donor fragment - our skeleton has no such mesh, so it stops drawing. Only OUR prop is exempt.
            var (addon, mgr, ours, props, e) = Scene();
            var weapons = new FakeCollection { name = "EQ_Weapons" }; weapons.meshes["Rifle"] = 9;
            var rifle = new FakeFragment(1, weapons, "Rifle", new object(), "R_Hand"); rifle.Load(ours, null, null, 2);
            addon.FragmentEntries = new[] { addon.FragmentEntries[0], rifle };
            UniversalInject.ReloadFragments(addon, mgr, ours, e);
            Assert.Same(ours, addon.FragmentEntries[1].Collection);
            Assert.Equal(0u, addon.FragmentEntries[1].EncodedMeshAndVisualParticleCount);
        }

        [Fact]
        public void A_donor_fragment_that_merely_carries_the_props_name_is_a_donor_fragment()
        {
            // External review of PR #113 (P2): the exemption went by mesh NAME alone, so a donor fragment of that name was
            // skipped too - never moved onto our skeleton, never hidden. It is exempt by IDENTITY now: only the entry
            // InjectHandProp appended to this addon, at the index it recorded.
            var (addon, mgr, ours, props, e) = Scene();
            var weapons = new FakeCollection { name = "EQ_Weapons" }; weapons.meshes[Prop] = 9;
            var namesake = new FakeFragment(1, weapons, Prop, new object(), "R_Hand"); namesake.Load(ours, null, null, 2);
            addon.FragmentEntries = new[] { addon.FragmentEntries[0], namesake };
            UniversalInject.ReloadFragments(addon, mgr, ours, e);                   // pass 1: nothing of ours is on the addon yet
            Assert.Same(ours, addon.FragmentEntries[1].Collection);                 // moved like every donor fragment ...
            Assert.Equal(0u, addon.FragmentEntries[1].EncodedMeshAndVisualParticleCount);   // ... and our skeleton has no such mesh
            // InjectHandProp asks exactly this before it appends: the namesake does not count as "already injected"
            Assert.False(UniversalInject.IsAppendedHandProp(e, addon, 1, Prop));

            AppendHandProp(addon, ours, props, e);                                  // so the configured prop IS added, as entry 2
            UniversalInject.ReloadFragments(addon, mgr, ours, e);                   // pass 2
            Assert.Same(ours, addon.FragmentEntries[1].Collection);                 // the namesake: still a donor fragment
            Assert.Equal(0u, addon.FragmentEntries[1].EncodedMeshAndVisualParticleCount);
            Assert.Same(props, addon.FragmentEntries[2].Collection);                // ours: untouched
            Assert.Equal(0x1D000000u + 77, addon.FragmentEntries[2].EncodedMeshAndVisualParticleCount);
            Assert.True(UniversalInject.IsAppendedHandProp(e, addon, 2, Prop));     // and InjectHandProp now returns: it is there
            // the smoke reads OUR entry of the two
            Assert.Empty(Smoke(e, addon, Encs(addon)).FragmentIssues);
        }

        [Fact]
        public void The_smoke_judges_the_entry_we_appended_not_a_live_namesake()
        {
            // External review of PR #113, second P2: the smoke resolved an appended fragment by NAME, a live entry beating
            // a dead one - so with a live donor namesake on the addon a DEAD prop read as held. It reads the recorded
            // entry now. Here our skeleton does hold a mesh of the prop's name, so the namesake stays alive on it.
            var (addon, mgr, ours, props, e) = Scene();
            ours.meshes[Prop] = 55;
            var namesake = new FakeFragment(1, ours, Prop, new object(), ""); namesake.Load(ours, null, null, 2);
            addon.FragmentEntries = new[] { addon.FragmentEntries[0], namesake };
            UniversalInject.ReloadFragments(addon, mgr, ours, e);
            AppendHandProp(addon, ours, props, e);
            var live = Encs(addon);
            Assert.Empty(Smoke(e, addon, live).FragmentIssues);                     // all three alive: held
            addon.FragmentEntries[2].EncodedMeshAndVisualParticleCount = 0;         // the prop dies; the namesake lives on, and is drawn

            var f = Smoke(e, addon, live);
            Assert.Single(f.FragmentIssues);
            Assert.Contains("encoded id reads 0", f.FragmentIssues[0]);
        }

        [Fact]
        public void The_smoke_judges_every_addon_a_model_entry_serves_each_against_its_own_block()
        {
            // one record per entry (the old gpuAddon) judged only the addon handled LAST, with the names of all
            var (addonA, mgr, ours, props, e) = Scene();
            var addonB = new FakeAddon { FragmentEntries = new[] { addonA.FragmentEntries[0] } };
            UniversalInject.ReloadFragments(addonA, mgr, ours, e); AppendHandProp(addonA, ours, props, e);
            UniversalInject.ReloadFragments(addonB, mgr, ours, e); AppendHandProp(addonB, ours, props, e);
            var live = new Dictionary<object, List<uint>> { [addonA] = Encs(addonA), [addonB] = Encs(addonB) };
            var held = Smoke(e, live);
            Assert.Empty(held.FragmentIssues);
            Assert.Equal(2, held.FragmentsChecked);
            addonA.FragmentEntries[1].EncodedMeshAndVisualParticleCount = 0;        // A's prop dies; B, handled last, is fine
            var f = Smoke(e, live);
            Assert.Single(f.FragmentIssues);
            Assert.Equal(1, f.FragmentsChecked);
        }

        [Fact]
        public void A_record_is_judged_only_where_a_descriptor_was_readable_and_is_replaced_when_we_append_again()
        {
            var (addon, mgr, ours, props, e) = Scene();
            UniversalInject.NoteAppended(e, addon, 1, Prop, handProp: true);        // appended before registration: DefId stays -1
            Assert.Empty(UniversalInject.AppendedByAddon(e));                       // nothing to judge it against - as before
            Assert.Equal(1, UniversalInject.AppendedHandPropIndex(e, addon));       // but ReloadFragments knows it all the same
            UniversalInject.NoteAppended(e, addon, 3, Prop, handProp: true).DefId = 86;   // the array was rebuilt; appended again
            Assert.Single(e.appended);
            Assert.Equal(3, UniversalInject.AppendedHandPropIndex(e, addon));
            UniversalInject.NoteAppended(e, addon, 4, "DroneSquadFPV_ModelMesh_B", handProp: false).DefId = 86;   // a chunk is its own record
            Assert.Equal(2, e.appended.Count);
            Assert.Equal(3, UniversalInject.AppendedHandPropIndex(e, addon));
            var sets = UniversalInject.AppendedByAddon(e);
            Assert.Single(sets); Assert.Equal(new[] { Prop, "DroneSquadFPV_ModelMesh_B" }, sets[0].names.ToArray());
            // what InjectExtraMeshFragments asks before it appends a chunk: recorded for THIS addon, under THIS name
            Assert.Equal(4, UniversalInject.AppendedChunkIndex(e, addon, "DroneSquadFPV_ModelMesh_B"));
            Assert.Equal(-1, UniversalInject.AppendedChunkIndex(e, addon, "DroneSquadFPV_ModelMesh_C"));
            Assert.Equal(-1, UniversalInject.AppendedChunkIndex(e, addon, Prop));               // the prop is not a chunk
            Assert.Equal(-1, UniversalInject.AppendedChunkIndex(e, new FakeAddon(), "DroneSquadFPV_ModelMesh_B"));
        }

        [Fact]
        public void An_entry_that_is_no_longer_where_we_appended_it_is_a_miss_whatever_else_carries_its_name()
        {
            // the game rebuilt the array from the definition: our entry is gone, and here a donor namesake sits elsewhere
            var (addon, mgr, ours, props, e) = Scene();
            UniversalInject.ReloadFragments(addon, mgr, ours, e);
            AppendHandProp(addon, ours, props, e);
            var live = Encs(addon);
            ours.meshes[Prop] = 55;
            var namesake = new FakeFragment(1, ours, Prop, new object(), ""); namesake.Load(ours, null, null, 2);
            addon.FragmentEntries = new[] { namesake, addon.FragmentEntries[0] };   // rebuilt: index 1 now holds the body
            live.Add(namesake.EncodedMeshAndVisualParticleCount);
            var f = Smoke(e, addon, live);
            Assert.Single(f.FragmentIssues);
            Assert.Contains("no longer where we appended it", f.FragmentIssues[0]);
        }

        [Fact]
        public void One_model_entry_serves_several_addons_and_each_keeps_its_own_prop()
        {
            // a registry entry matches every pawn definition its pawnDescription fits (the _01, _02 ... variants of one
            // unit): each has its own addon, each gets the prop, and the record of one must not displace the other's
            var (addonA, mgr, ours, props, e) = Scene();
            var addonB = new FakeAddon { FragmentEntries = new[] { addonA.FragmentEntries[0] } };
            UniversalInject.ReloadFragments(addonA, mgr, ours, e); AppendHandProp(addonA, ours, props, e);
            UniversalInject.ReloadFragments(addonB, mgr, ours, e); AppendHandProp(addonB, ours, props, e);
            UniversalInject.ReloadFragments(addonA, mgr, ours, e);                  // A loads again AFTER B was handled
            UniversalInject.ReloadFragments(addonB, mgr, ours, e);
            Assert.Equal(0x1D000000u + 77, addonA.FragmentEntries[1].EncodedMeshAndVisualParticleCount);
            Assert.Equal(0x1D000000u + 77, addonB.FragmentEntries[1].EncodedMeshAndVisualParticleCount);
            Assert.True(UniversalInject.IsAppendedHandProp(e, addonA, 1, Prop));    // InjectHandProp returns on both: no second copy
            Assert.True(UniversalInject.IsAppendedHandProp(e, addonB, 1, Prop));
        }

        [Fact]
        public void The_identity_is_the_addon_and_the_index_with_the_name_as_guard()
        {
            var (addon, mgr, ours, props, e) = Scene();
            Assert.False(UniversalInject.IsAppendedHandProp(e, addon, 0, Prop));    // nothing appended yet
            AppendHandProp(addon, ours, props, e);
            Assert.True(UniversalInject.IsAppendedHandProp(e, addon, 1, Prop));
            Assert.False(UniversalInject.IsAppendedHandProp(e, addon, 0, Prop));    // another index
            Assert.False(UniversalInject.IsAppendedHandProp(e, addon, 1, Body));    // the game rebuilt the array: another mesh sits there
            Assert.False(UniversalInject.IsAppendedHandProp(e, addon, 1, null));
            Assert.False(UniversalInject.IsAppendedHandProp(e, new FakeAddon(), 1, Prop));   // another addon: a new session's, or another definition's
            Assert.False(UniversalInject.IsAppendedHandProp(null, addon, 1, Prop));
            // a rebuilt array on the SAME addon holds no prop: the recorded index is past its end, so a later Load treats
            // every entry as a donor fragment and InjectHandProp appends again
            addon.FragmentEntries = new[] { addon.FragmentEntries[0] };
            UniversalInject.ReloadFragments(addon, mgr, ours, e);
            Assert.Same(ours, addon.FragmentEntries[0].Collection);
        }

        [Fact]
        public void A_hide_pattern_is_for_donor_fragments_and_does_not_reach_our_own_prop()
        {
            // hideMeshes matches by substring; "Mesh" would catch M60_DistrictMesh. On pass 1 the prop is not in the array
            // yet, so the pattern never applied to it there - and from pass 2 on it used to zero the entry AND the GPU
            // snapshot: the prop would have vanished at the second Load. The body, a donor fragment, is still hidden.
            var (addon, mgr, ours, props, e) = Scene();
            e.hideMeshes = "AllTerrain, DistrictMesh";
            UniversalInject.ReloadFragments(addon, mgr, ours, e);
            AppendHandProp(addon, ours, props, e);
            UniversalInject.ReloadFragments(addon, mgr, ours, e);
            Assert.Equal(0u, addon.FragmentEntries[0].EncodedMeshAndVisualParticleCount);              // the donor body: hidden as asked
            Assert.Equal(0x1D000000u + 77, addon.FragmentEntries[1].EncodedMeshAndVisualParticleCount); // our prop: untouched
        }

        [Fact]
        public void A_model_without_a_hand_prop_has_no_exempt_name()
        {
            Assert.Null(UniversalInject.HandPropMeshName(new ModelEntry { resourceName = "Tank" }));
            Assert.Null(UniversalInject.HandPropMeshName(null));
            Assert.Equal("M60_DistrictMesh", UniversalInject.HandPropMeshName(new ModelEntry { resourceName = "DroneSquadFPV", handPropGuid = "1,2,3,4", handPropName = "M60" }));
            Assert.Equal("SlingerProp_DistrictMesh", UniversalInject.HandPropMeshName(new ModelEntry { resourceName = "Slinger", handPropGuid = "1,2,3,4" }));   // the default prop name
        }
    }
}
