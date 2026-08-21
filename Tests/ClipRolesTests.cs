using System.Linq;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The clip-role TABLE (Cut A, 2026-08-21) replaced nine hand-expanded field families. These pin what the table
    // promises: its order, its three name spaces, the pack-JSON contract on BOTH parse paths, and the two properties
    // whose hand-written predecessors shipped bugs (AnyStateRole; the per-role re-arm).
    public class ClipRolesTests
    {
        [Fact]
        public void Table_HasNineRoles_PrimaryFirst_AllDistinctNamesTagsKeys()
        {
            Assert.Equal(9, ClipRoles.Count);
            Assert.Equal(9, ClipRoles.All.Length);
            Assert.Equal(ClipRole.Primary, ClipRoles.All[0]);
            Assert.Equal(0, (int)ClipRole.Primary);
            Assert.Equal(ClipRoles.All.Select(r => (int)r), Enumerable.Range(0, 9));   // dense, in order — the table is indexed by it
            Assert.Equal(9, ClipRoles.All.Select(ClipRoles.Name).Distinct().Count());
            Assert.Equal(9, ClipRoles.All.Select(ClipRoles.Tag).Distinct().Count());
            Assert.Equal(9, ClipRoles.All.Select(ClipRoles.JsonKey).Distinct().Count());
            Assert.Equal("", ClipRoles.Tag(ClipRole.Primary));   // the primary collection is injected under the bare resource name
            Assert.False(ClipRoles.IsState(ClipRole.Primary));
            Assert.All(ClipRoles.All.Skip(1), r => Assert.True(ClipRoles.IsState(r)));
        }

        [Fact]
        public void NewTable_IsFreshPerEntry_NotShared()
        {
            var a = new ModelEntry(); var b = new ModelEntry();
            a.Role(ClipRole.Attack).animId = 7;
            Assert.Equal(-1, b.Role(ClipRole.Attack).animId);
            Assert.All(b.Roles, x => { Assert.False(x.Authored); Assert.Null(x.coll); Assert.Equal(-1, x.animId); Assert.Equal(1f, x.dur); });
        }

        [Fact]
        public void NamedAccessors_AreTheTable()
        {
            var e = new ModelEntry();
            e.attackAnimId = 12; e.idleAlt2Dur = 3.5f; e.clipColl = "primary-coll"; e.animDuration = 2f;
            Assert.Equal(12, e.Role(ClipRole.Attack).animId);
            Assert.Equal(3.5f, e.Role(ClipRole.IdleAlt2).dur);
            Assert.Equal("primary-coll", e.Role(ClipRole.Primary).coll);
            Assert.Equal(2f, e.Role(ClipRole.Primary).dur);
            e.Role(ClipRole.Move).animId = 4;
            Assert.Equal(4, e.moveAnimId);
        }

        // critical-review #8: a move-less state-driven model (idle-override + attack) must still count as state-driven.
        [Theory]
        [InlineData(ClipRole.Move)]
        [InlineData(ClipRole.After)]
        [InlineData(ClipRole.Attack)]
        [InlineData(ClipRole.Combat)]
        [InlineData(ClipRole.PreMove)]
        [InlineData(ClipRole.IdleOverride)]
        [InlineData(ClipRole.IdleAlt)]
        [InlineData(ClipRole.IdleAlt2)]
        public void AnyStateRole_TrueForEveryStateRoleAlone_FalseForPrimaryAlone(ClipRole role)
        {
            var e = new ModelEntry();
            Assert.False(e.AnyStateRole);
            e.Role(ClipRole.Primary).animId = 3;
            Assert.False(e.AnyStateRole);   // the primary clip alone is not a state machine
            e.Role(role).animId = 5;
            Assert.True(e.AnyStateRole);
        }

        // THE PACK CONTRACT: each clip* JSON array must land on its role, on the Newtonsoft path AND the regex fallback
        // (a deliberately broken file). This is the test the 36-int reflection guard was standing in for.
        static string Pack(string key, string arr, bool broken) =>
            "{ \"modId\": \"t\", \"schemaVersion\": 1, \"models\": [ { \"resourceName\": \"R\", \"pawnDescription\": \"Unit_01\", " +
            "\"skel\": [9,9,9,9], \"atlas\": [8,8,8,8], \"animStateDriven\": true, \"" + key + "\": " + arr + " } ]" + (broken ? " " : " }");

        [Theory]
        [InlineData(ClipRole.Primary)]
        [InlineData(ClipRole.Move)]
        [InlineData(ClipRole.After)]
        [InlineData(ClipRole.Attack)]
        [InlineData(ClipRole.Combat)]
        [InlineData(ClipRole.PreMove)]
        [InlineData(ClipRole.IdleOverride)]
        [InlineData(ClipRole.IdleAlt)]
        [InlineData(ClipRole.IdleAlt2)]
        public void Parse_EachClipJsonKey_LandsOnItsRole_BothPaths(ClipRole role)
        {
            foreach (var broken in new[] { false, true })
            {
                var list = UniversalInject.ParseModels(Pack(ClipRoles.JsonKey(role), "[1,2,3,4]", broken));
                Assert.Single(list);
                var e = list[0];
                var b = e.Role(role);
                Assert.True(b.Authored, $"{role} not authored on the {(broken ? "regex" : "Newtonsoft")} path");
                Assert.Equal(new[] { 1, 2, 3, 4 }, new[] { b.a, b.b, b.c, b.d });
                foreach (var other in ClipRoles.All.Where(r => r != role))
                    Assert.False(e.Role(other).Authored, $"{other} wrongly authored from key {ClipRoles.JsonKey(role)}");
                Assert.Equal(9, e.sa);   // the skeleton quad still parses beside it
            }
        }
    }
}
