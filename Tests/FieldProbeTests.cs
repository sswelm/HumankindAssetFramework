using System.Reflection;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // THE TWO FIELD PROBES — GF and GFA — and the difference that stops them being merged.
    //
    // `GF` is QUIET (Type.GetField, so a miss emits no HarmonyX warning; probing spams the log) and deliberately
    // UNCACHED. A cache was written for it and measured away — the .NET runtime already keeps a per-type member
    // cache, so Type.GetField is ~20 ns and a (Type,string) dictionary wrapper is ~42 ns. `AccessTools.Field` is
    // the one that needs memoising at 1,524 ns, which is what GFA is.
    //
    // So these do not test caching — there is none to test, and reference identity across calls is the runtime's
    // doing, not ours. What they pin is the RESOLUTION DIFFERENCE that stops the two helpers being merged:
    // Type.GetField cannot see a PRIVATE field inherited from a base type; AccessTools walks the hierarchy and can.
    // A future cleanup that "simplifies" one into the other silently changes which members the district walk can
    // reach, which is the kind of change that shows up as a district that stops rendering.
    public class FieldProbeTests
    {
        class Base { int hiddenPrivate = 7; protected int visibleProtected = 8; public int visiblePublic = 9;
                     public int ReadHidden() => hiddenPrivate; }
        class Derived : Base { public int own = 1; }

        // ---- basic resolution ----

        [Fact]
        public void GF_IsPerTypeAndPerName()
        {
            Assert.NotSame(DistrictInject.GF(typeof(Derived), "own"), DistrictInject.GF(typeof(Derived), "visiblePublic"));
            Assert.Null(DistrictInject.GF(typeof(Base), "own"));   // `own` is Derived's, not Base's
        }

        [Fact]
        public void GF_ToleratesANullType()
        {
            Assert.Null(DistrictInject.GF(null, "own"));
        }

        // ---- the resolution difference: this is why GF and GFA are two helpers ----

        [Fact]
        public void GF_FindsInheritedPublicAndProtectedFields()
        {
            Assert.NotNull(DistrictInject.GF(typeof(Derived), "visiblePublic"));
            Assert.NotNull(DistrictInject.GF(typeof(Derived), "visibleProtected"));
        }

        // THE DIFFERENCE. If this ever starts passing, GF has been widened into GFA and the two are no longer
        // distinguishable — at which point the district walk's reachability changes without anyone deciding to.
        [Fact]
        public void GF_DoesNotSeeAPrivateFieldInheritedFromABase()
        {
            Assert.Null(DistrictInject.GF(typeof(Derived), "hiddenPrivate"));
        }

        [Fact]
        public void GFA_DoesSeeAPrivateFieldInheritedFromABase()
        {
            var f = DistrictInject.GFA(typeof(Derived), "hiddenPrivate");
            Assert.NotNull(f);
            Assert.Equal(7, (int)f.GetValue(new Derived()));   // and it really reads it
        }

        // GFA memoises AccessTools (1,524 ns -> 49 ns measured). Same instance across calls is the observable
        // side of that; unlike GF, here it really is our cache doing it.
        [Fact]
        public void GFA_Caches()
        {
            Assert.Same(DistrictInject.GFA(typeof(Derived), "hiddenPrivate"),
                        DistrictInject.GFA(typeof(Derived), "hiddenPrivate"));
        }
    }
}
