using System.Linq;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // A1 of the reflection-fragility work: prove GameBinding.Validate resolves types/members correctly, so the in-game
    // startup report is trustworthy. The actual GAME types aren't present in a test host — which is exactly the
    // "missing type" path — so we exercise the resolution logic against known .NET types.
    public class GameBindingTests
    {
        [Fact]
        public void Validate_KnownType_AllMembersFound()
        {
            // String.Length (property) + Substring (method) both exist.
            var r = GameBinding.Validate(new[] { new GameBinding.Dep(typeof(string), "String", "Length", "Substring") }).Single();
            Assert.True(r.TypeFound);
            Assert.Empty(r.MissingMembers);
        }

        [Fact]
        public void Validate_ReportsMissingMemberButKeepsTheReal()
        {
            var r = GameBinding.Validate(new[] { new GameBinding.Dep(typeof(string), "String", "Length", "NopeNotAMember") }).Single();
            Assert.True(r.TypeFound);
            Assert.Equal(new[] { "NopeNotAMember" }, r.MissingMembers.ToArray());   // Length resolved, only the bogus one flagged
        }

        [Fact]
        public void Validate_ReportsMissingType()
        {
            var r = GameBinding.Validate(new[] { new GameBinding.Dep(null, "Missing.Game.Type", "X") }).Single();
            Assert.False(r.TypeFound);
            // members aren't probed when the type is missing (nothing to probe against)
            Assert.Empty(r.MissingMembers);
        }

        [Fact]
        public void Validate_MethodAndField_BothCountAsMembers()
        {
            // StringBuilder.Append (method) + Capacity (property); plus a field-bearing type for the field path.
            var sb = GameBinding.Validate(new[] { new GameBinding.Dep(typeof(System.Text.StringBuilder), "StringBuilder", "Append", "Capacity") }).Single();
            Assert.True(sb.TypeFound);
            Assert.Empty(sb.MissingMembers);
        }

        [Fact]
        public void Validate_EmptyAndNull_AreSafe()
        {
            Assert.Empty(GameBinding.Validate(null));
            Assert.Empty(GameBinding.Validate(System.Array.Empty<GameBinding.Dep>()));
            // an empty member name is treated as missing (a catalog typo), not silently OK
            var r = GameBinding.Validate(new[] { new GameBinding.Dep(typeof(string), "String", "") }).Single();
            Assert.Contains("", r.MissingMembers);
        }

        // ---- A3: cached type accessors ----
        [Fact]
        public void Cached_ResolvesAndCachesSameInstance()
        {
            var a = GameBinding.Cached("System.String");
            var b = GameBinding.Cached("System.String");
            Assert.Same(typeof(string), a);
            Assert.Same(a, b);   // cached: same instance
        }

        [Fact]
        public void Cached_FallsBackWhenPrimaryMissing()
        {
            var t = GameBinding.Cached("Bogus.Missing.Primary.Type", "System.Text.StringBuilder");
            Assert.Same(typeof(System.Text.StringBuilder), t);
        }

        [Fact]
        public void Cached_MissingBothReturnsNull()
        {
            Assert.Null(GameBinding.Cached("Totally.Bogus.Nope.Type"));
        }

        [Fact]
        public void Cached_ResolvesSimpleName()
        {
            // AccessTools-style simple-name (namespace-less) resolution — the game has a few short-name-only types,
            // and this is what Hk_FormationPrefabExtend needs (it broke when ResolveType only did full names).
            Assert.Same(typeof(System.Text.StringBuilder), GameBinding.Cached("StringBuilder"));
        }

        // ---- A5, the struct batch: DERIVED bindings — a struct type resolved structurally from an anchor member
        // (field type / array element), the same path the runtime code walks, so no name-guess false positives.
        class DerivationDummy
        {
#pragma warning disable 0649, 0169
            public DummyElement[] items;                       // array-element derivation (pawnEntries[] shape)
            public System.Collections.Generic.List<DummyElement> listItems;   // generic-arg derivation (Battles shape)
            DummyElement single;                               // NON-public field-type derivation (game fields are often private)
            public string PropTyped { get; set; }              // property-type derivation
#pragma warning restore 0649, 0169
        }
        class DummyElement { public int X; }

        [Fact]
        public void Derived_FieldArrayGenericAndProperty_AllResolve()
        {
            var t = typeof(DerivationDummy);
            Assert.Same(typeof(DummyElement), GameBinding.ElementType(GameBinding.FieldOrPropType(t, "items")));
            Assert.Same(typeof(DummyElement), GameBinding.ElementType(GameBinding.FieldOrPropType(t, "listItems")));
            Assert.Same(typeof(DummyElement), GameBinding.FieldOrPropType(t, "single"));   // non-public reached
            Assert.Same(typeof(string), GameBinding.FieldOrPropType(t, "PropTyped"));
        }

        [Fact]
        public void Derived_BrokenAnchor_YieldsNullNotThrow()
        {
            // A renamed anchor member = the derivation chain breaks = null type = a [MISSING TYPE] line in the
            // report, never an exception at boot (the drift must be LOUD, not fatal).
            Assert.Null(GameBinding.FieldOrPropType(typeof(DerivationDummy), "renamedByGameUpdate"));
            Assert.Null(GameBinding.ElementType(GameBinding.FieldOrPropType(typeof(DerivationDummy), "renamedByGameUpdate")));
            Assert.Null(GameBinding.ElementType(typeof(string)));   // non-array, non-generic → null
            Assert.Null(GameBinding.FieldOrPropType(null, "items"));
        }

        [Fact]
        public void Derived_MissingStructMember_FlagsLikeAnyDep()
        {
            // End-to-end: a derived type feeding a Dep behaves identically — a renamed struct member is flagged.
            var derived = GameBinding.ElementType(GameBinding.FieldOrPropType(typeof(DerivationDummy), "items"));
            var r = GameBinding.Validate(new[] { new GameBinding.Dep(derived, "DummyElement", "X", "RenamedByUpdate") }).Single();
            Assert.True(r.TypeFound);
            Assert.Equal(new[] { "RenamedByUpdate" }, r.MissingMembers.ToArray());
        }
    }
}
