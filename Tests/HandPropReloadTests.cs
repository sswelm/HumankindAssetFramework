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

        static void AppendHandProp(FakeAddon addon, FakeCollection ours, FakeCollection props)
        {
            var item = new FakeFragment(0, props, Prop, new object(), "R_Hand");
            item.Load(ours, null, null, 2);
            var grown = new FakeFragment[addon.FragmentEntries.Length + 1];
            addon.FragmentEntries.CopyTo(grown, 0); grown[grown.Length - 1] = item;
            addon.FragmentEntries = grown;
        }

        [Fact]
        public void The_hand_prop_keeps_its_own_collection_and_its_encoding_when_the_addon_loads_again()
        {
            var (addon, mgr, ours, props, e) = Scene();
            UniversalInject.ReloadFragments(addon, mgr, ours, e);                   // pass 1: the body moves onto our skeleton
            Assert.Equal(0x1D000000u + 123, addon.FragmentEntries[0].EncodedMeshAndVisualParticleCount);
            AppendHandProp(addon, ours, props);
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
            var f = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
            UniversalInject.GatherFragmentFact(e.resourceName, new List<string> { Prop }, UniversalInject.ReadAddonEncsByName(addon), live, f);
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
        public void A_hide_pattern_is_for_donor_fragments_and_does_not_reach_our_own_prop()
        {
            // hideMeshes matches by substring; "Mesh" would catch M60_DistrictMesh. On pass 1 the prop is not in the array
            // yet, so the pattern never applied to it there - and from pass 2 on it used to zero the entry AND the GPU
            // snapshot: the prop would have vanished at the second Load. The body, a donor fragment, is still hidden.
            var (addon, mgr, ours, props, e) = Scene();
            e.hideMeshes = "AllTerrain, DistrictMesh";
            UniversalInject.ReloadFragments(addon, mgr, ours, e);
            AppendHandProp(addon, ours, props);
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
