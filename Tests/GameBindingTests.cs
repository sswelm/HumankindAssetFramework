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
            var r = GameBinding.Validate(new[] { new GameBinding.Dep("System.String", "Length", "Substring") }).Single();
            Assert.True(r.TypeFound);
            Assert.Empty(r.MissingMembers);
        }

        [Fact]
        public void Validate_ReportsMissingMemberButKeepsTheReal()
        {
            var r = GameBinding.Validate(new[] { new GameBinding.Dep("System.String", "Length", "NopeNotAMember") }).Single();
            Assert.True(r.TypeFound);
            Assert.Equal(new[] { "NopeNotAMember" }, r.MissingMembers.ToArray());   // Length resolved, only the bogus one flagged
        }

        [Fact]
        public void Validate_ReportsMissingType()
        {
            var r = GameBinding.Validate(new[] { new GameBinding.Dep("Totally.Nonexistent.GameType", "X") }).Single();
            Assert.False(r.TypeFound);
            // members aren't probed when the type is missing (nothing to probe against)
            Assert.Empty(r.MissingMembers);
        }

        [Fact]
        public void Validate_MethodAndField_BothCountAsMembers()
        {
            // StringBuilder.Append (method) + Capacity (property); plus a field-bearing type for the field path.
            var sb = GameBinding.Validate(new[] { new GameBinding.Dep("System.Text.StringBuilder", "Append", "Capacity") }).Single();
            Assert.True(sb.TypeFound);
            Assert.Empty(sb.MissingMembers);
        }

        [Fact]
        public void Validate_EmptyAndNull_AreSafe()
        {
            Assert.Empty(GameBinding.Validate(null));
            Assert.Empty(GameBinding.Validate(System.Array.Empty<GameBinding.Dep>()));
            // an empty member name is treated as missing (a catalog typo), not silently OK
            var r = GameBinding.Validate(new[] { new GameBinding.Dep("System.String", "") }).Single();
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
    }
}
