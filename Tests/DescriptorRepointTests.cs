using System;
using System.Collections.Generic;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The descriptor repoint (hand prop, multi-mesh chunks) is the arithmetic behind "every pawn of the unit renders
    // a foreign mesh" when it is off by one. Until 2026-09-14 it lived inline at two injection sites and could only
    // run with the game up. The kernel is pure over Array + the descriptor struct's two fields, so these drive it
    // with test-defined structs shaped like the game's (StartFragment/FragmentCount; the four gpu fragment fields).
    public class DescriptorRepointTests
    {
        struct Desc { public uint StartFragment; public uint FragmentCount; public int Unrelated; }
        struct GFrag { public uint SkinnedMeshIndex; public uint EncodedMeshAndVisualParticleCountFxMeshIndex; public uint BoneIndex; public uint FxOutputLayerIndex; }

        static object Frag(uint enc) => new GFrag { EncodedMeshAndVisualParticleCountFxMeshIndex = enc };
        static uint Enc(Array a, int i) => ((GFrag)a.GetValue(i)).EncodedMeshAndVisualParticleCountFxMeshIndex;

        // descriptor 2 owns fragments 10..12 (enc 100..102); the manager's tail is 20
        static (Array gfrags, Array descs) Fixture(int length)
        {
            var g = (Array)new GFrag[length];
            for (int i = 0; i < 3; i++) g.SetValue(Frag(100u + (uint)i), 10 + i);
            var d = (Array)new Desc[4];
            d.SetValue(new Desc { StartFragment = 10, FragmentCount = 3, Unrelated = 7 }, 2);
            return (g, d);
        }

        [Fact]
        public void Copies_the_block_to_the_tail_appends_after_it_and_repoints_the_descriptor()
        {
            var (g, d) = Fixture(64);
            Assert.True(DescriptorRepoint.Apply(ref g, d, 2, 20, new[] { Frag(500), Frag(501) }, out var o, out string err), err);

            var desc = (Desc)d.GetValue(2);
            Assert.Equal(20u, desc.StartFragment);
            Assert.Equal(5u, desc.FragmentCount);
            Assert.Equal(7, desc.Unrelated);                 // the rest of the struct survives the boxed write-back
            Assert.Equal(25, o.NewTail);
            Assert.Equal(10, o.OldStart); Assert.Equal(3, o.OldCount);
            Assert.Equal(20, o.NewStart); Assert.Equal(5, o.NewCount);
            Assert.False(o.Grown);
            for (int i = 0; i < 3; i++) Assert.Equal(100u + (uint)i, Enc(g, 20 + i));   // the copy, in order
            Assert.Equal(500u, Enc(g, 23));
            Assert.Equal(501u, Enc(g, 24));
            Assert.Equal(100u, Enc(g, 10));                  // the old block is left in place (the GPU may still read it)
            Assert.Equal(0u, Enc(g, 25));                    // nothing written past the new tail
        }

        [Fact]
        public void Grows_by_need_plus_slack_when_the_array_is_short_and_reports_it()
        {
            var (g, d) = Fixture(22);                        // need = 20 + 3 + 2 = 25 > 22
            var before = g;
            Assert.True(DescriptorRepoint.Apply(ref g, d, 2, 20, new[] { Frag(500), Frag(501) }, out var o, out _));
            Assert.True(o.Grown);
            Assert.NotSame(before, g);
            Assert.Equal(25 + DescriptorRepoint.GrowSlack, g.Length);
            Assert.Equal(102u, Enc(g, 22));                  // the copy landed in the grown array
            Assert.Equal(501u, Enc(g, 24));
            Assert.Equal(25, o.NewTail);
        }

        [Fact]
        public void An_exact_fit_does_not_grow()
        {
            var (g, d) = Fixture(25);
            var before = g;
            Assert.True(DescriptorRepoint.Apply(ref g, d, 2, 20, new[] { Frag(500), Frag(501) }, out var o, out _));
            Assert.False(o.Grown);
            Assert.Same(before, g);
        }

        [Fact]
        public void A_second_repoint_chains_from_the_previous_tail()
        {
            // the shipped sequence: a hand prop (+1) at load, then the multi-mesh chunks (+2) — the second copy must
            // carry the hand prop along, because it copies the descriptor's CURRENT block, not the original one
            var (g, d) = Fixture(64);
            Assert.True(DescriptorRepoint.Apply(ref g, d, 2, 20, new[] { Frag(700) }, out var first, out _));
            Assert.Equal(24, first.NewTail);
            Assert.True(DescriptorRepoint.Apply(ref g, d, 2, first.NewTail, new[] { Frag(500), Frag(501) }, out var second, out _));

            var desc = (Desc)d.GetValue(2);
            Assert.Equal(24u, desc.StartFragment);
            Assert.Equal(6u, desc.FragmentCount);
            Assert.Equal(30, second.NewTail);
            Assert.Equal(new uint[] { 100, 101, 102, 700, 500, 501 }, new[] { Enc(g, 24), Enc(g, 25), Enc(g, 26), Enc(g, 27), Enc(g, 28), Enc(g, 29) });
        }

        [Fact]
        public void Refuses_bad_input_without_touching_anything()
        {
            var (g, d) = Fixture(64);
            var before = g;
            Assert.False(DescriptorRepoint.Apply(ref g, d, 9, 20, new[] { Frag(1) }, out _, out string e1));   // defId outside the table
            Assert.Contains("defId 9", e1);
            Assert.False(DescriptorRepoint.Apply(ref g, d, 2, 20, new object[0], out _, out string e2));       // nothing to append
            Assert.Contains("nothing", e2);
            Assert.False(DescriptorRepoint.Apply(ref g, d, 2, 65, new[] { Frag(1) }, out _, out string e3));    // tail past the array
            Assert.Contains("tail 65", e3);
            d.SetValue(new Desc { StartFragment = 60, FragmentCount = 8 }, 3);                                    // a block that overruns the array
            Assert.False(DescriptorRepoint.Apply(ref g, d, 3, 20, new[] { Frag(1) }, out _, out string e4));
            Assert.Contains("60+8", e4);

            Assert.Same(before, g);
            var desc = (Desc)d.GetValue(2);
            Assert.Equal(10u, desc.StartFragment); Assert.Equal(3u, desc.FragmentCount);
            Assert.Equal(0u, Enc(g, 20));
        }

        [Fact]
        public void TryReadBlock_reports_the_current_block_and_the_unpopulated_case()
        {
            var (_, d) = Fixture(64);
            Assert.True(DescriptorRepoint.TryReadBlock(d, 2, out int s, out int c));
            Assert.Equal(10, s); Assert.Equal(3, c);
            Assert.True(DescriptorRepoint.TryReadBlock(d, 1, out int s1, out int c1));   // allocated, never registered: 0+0
            Assert.Equal(0, s1); Assert.Equal(0, c1);                                    // callers skip the surgical repoint here
            Assert.False(DescriptorRepoint.TryReadBlock(d, 9, out _, out _));
            Assert.False(DescriptorRepoint.TryReadBlock(null, 0, out _, out _));
        }

        [Fact]
        public void Reports_a_descriptor_type_without_the_two_fields()
        {
            var g = (Array)new GFrag[8];
            var d = (Array)new GFrag[4];   // wrong struct in the descriptor slot: no StartFragment / FragmentCount
            Assert.False(DescriptorRepoint.Apply(ref g, d, 1, 0, new[] { Frag(1) }, out _, out string err));
            Assert.Contains("StartFragment", err);
        }
    }
}
