using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // FastMember builds compiled field accessors for boxed structs (the pose hook's PawnEntry). These pin the contract:
    // a write through the setter lands IN THE BOX (like FieldInfo.SetValue on a box), nested struct paths work without
    // copying the inner struct, numeric leaves convert, and anything unsupported yields null (the reflection fallback).
    public class FastMemberTests
    {
        struct Inner { public float Time; public uint AnimationId; public float Weight; }
        struct Vec { public float x, y, z; }
        struct Outer
        {
            public int SkeletonId; public uint PawnDescriptorId; public float HideFactor;
            public Inner Pose0; public Inner Pose1;
            public Vec Translation;
            private int secret;
            public int Secret => secret;
        }
        class Holder { public Outer Entry; public string Name = "h"; }

        [Fact]
        public void Getter_ReadsLeafAndNestedFields_FromBoxedStruct()
        {
            object box = new Outer { SkeletonId = 7, Pose1 = new Inner { Time = 0.25f, AnimationId = 99 } };
            Assert.Equal(7, FastMember.Getter<int>(typeof(Outer), "SkeletonId")(box));
            Assert.Equal(0.25f, FastMember.Getter<float>(typeof(Outer), "Pose1.Time")(box));
            Assert.Equal(99u, FastMember.Getter<uint>(typeof(Outer), "Pose1.AnimationId")(box));
        }

        [Fact]
        public void Setter_WritesIntoTheBox_NestedPath_NoCopy()
        {
            object box = new Outer();
            FastMember.Setter<uint>(typeof(Outer), "Pose0.AnimationId")(box, 42u);
            FastMember.Setter<float>(typeof(Outer), "Pose0.Time")(box, 0.5f);
            FastMember.Setter<float>(typeof(Outer), "Translation.y")(box, 3f);
            var read = (Outer)box;   // unbox AFTER the writes: they must have landed in the box itself
            Assert.Equal(42u, read.Pose0.AnimationId);
            Assert.Equal(0.5f, read.Pose0.Time);
            Assert.Equal(3f, read.Translation.y);
            Assert.Equal(0u, read.Pose1.AnimationId);   // the sibling slot untouched
        }

        [Fact]
        public void NumericLeaf_ConvertsToRequestedType_BothWays()
        {
            object box = new Outer { PawnDescriptorId = 12345u };
            Assert.Equal(12345, FastMember.Getter<int>(typeof(Outer), "PawnDescriptorId")(box));   // uint field read as int
            FastMember.Setter<int>(typeof(Outer), "PawnDescriptorId")(box, 77);                      // int written into the uint field
            Assert.Equal(77u, ((Outer)box).PawnDescriptorId);
        }

        [Fact]
        public void PrivateField_Reachable()
        {
            object box = new Outer();
            var set = FastMember.Setter<int>(typeof(Outer), "secret");
            Assert.NotNull(set);
            set(box, 5);
            Assert.Equal(5, ((Outer)box).Secret);
        }

        [Fact]
        public void ClassInstance_Works_ThroughReferenceHop()
        {
            var h = new Holder();
            FastMember.Setter<int>(typeof(Holder), "Entry.SkeletonId")(h, 3);
            Assert.Equal(3, h.Entry.SkeletonId);
            Assert.Equal("h", FastMember.Getter<string>(typeof(Holder), "Name")(h));
        }

        [Fact]
        public void Unsupported_YieldsNull_NeverThrows()
        {
            Assert.Null(FastMember.Getter<int>(typeof(Outer), "NoSuchField"));
            Assert.Null(FastMember.Getter<int>(typeof(Outer), "Pose0.Nope"));
            Assert.Null(FastMember.Getter<string>(typeof(Outer), "SkeletonId"));   // int -> string: no conversion
            Assert.Null(FastMember.Setter<Vec>(typeof(Outer), "SkeletonId"));      // struct into int: no
        }

        struct Packed { uint bits; public float HideFactor { get => (bits & 0xFF) / 255f; set => bits = (bits & ~0xFFu) | (uint)(value * 255f); } public uint Bits => bits; }

        // PawnEntry.HideFactor is a PROPERTY packed into a bitfield (tools/typeprobe) — the leaf may be a property, called on the box.
        [Fact]
        public void PropertyLeaf_OnBoxedStruct_GetAndSet()
        {
            object box = new Packed();
            var set = FastMember.Setter<float>(typeof(Packed), "HideFactor"); var get = FastMember.Getter<float>(typeof(Packed), "HideFactor");
            Assert.NotNull(set); Assert.NotNull(get);
            set(box, 1f);
            Assert.Equal(255u, ((Packed)box).Bits);
            Assert.Equal(1f, get(box));
        }

        [Fact]
        public void Accessors_AreCached_SameInstance()
        {
            Assert.Same(FastMember.Getter<int>(typeof(Outer), "SkeletonId"), FastMember.Getter<int>(typeof(Outer), "SkeletonId"));
        }
    }
}
