using System.Collections.Generic;
using System.Linq;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // MergeModels — the pack merge policy as a pure function (extracted 2026-08-21 for the `disabled` finding:
    // the debug switch was honoured on the no-prior-owner branch only, so a disabled DECLARED override still
    // replaced the owner — dead in exactly the case a modder uses it).
    public class MergeModelsTests
    {
        static UniversalInject.Pack P(string modId, params ModelEntry[] models) =>
            new UniversalInject.Pack { modId = modId, models = models.ToList() };
        static ModelEntry M(string name, string pawn, bool disabled = false) =>
            new ModelEntry { resourceName = name, pawnDescription = pawn, disabled = disabled };

        [Fact]
        public void Disabled_entry_without_owner_is_skipped_so_the_original_unit_renders()
        {
            var r = UniversalInject.MergeModels(new List<UniversalInject.Pack> { P("a", M("Tank", "Pawn_Tank", disabled: true)) });
            Assert.Empty(r.Built); Assert.Empty(r.Notes);
        }

        [Fact]
        public void Disabled_declared_override_leaves_the_owner_in_place_and_is_noted()
        {
            var b = P("b", M("TankB", "Pawn_Tank", disabled: true));
            b.overrides.Add(new UniversalInject.PackOverride { modId = "a", pawn = "Pawn_Tank" });
            var r = UniversalInject.MergeModels(new List<UniversalInject.Pack> { P("a", M("TankA", "Pawn_Tank")), b });
            Assert.Equal("TankA", Assert.Single(r.Built).resourceName);   // the owner keeps the pawn
            Assert.Empty(r.Applied); Assert.Empty(r.Conflicts);
            Assert.Contains("'b' is disabled; 'a' keeps the pawn", Assert.Single(r.Notes));
        }

        [Fact]
        public void Declared_override_replaces_in_place()
        {
            var b = P("b", M("TankB", "Pawn_Tank"));
            b.overrides.Add(new UniversalInject.PackOverride { modId = "a", pawn = "Pawn_Tank" });
            var r = UniversalInject.MergeModels(new List<UniversalInject.Pack> { P("a", M("TankA", "Pawn_Tank"), M("Jeep", "Pawn_Jeep")), b });
            Assert.Equal(new[] { "TankB", "Jeep" }, r.Built.Select(e => e.resourceName).ToArray());   // index preserved
            Assert.Single(r.Applied); Assert.Empty(r.Conflicts);
        }

        [Fact]
        public void Undeclared_clash_keeps_first_loaded_and_is_a_conflict()
        {
            var r = UniversalInject.MergeModels(new List<UniversalInject.Pack> { P("a", M("TankA", "Pawn_Tank")), P("b", M("TankB", "Pawn_Tank")) });
            Assert.Equal("TankA", Assert.Single(r.Built).resourceName);
            Assert.Contains("kept=a dropped=b(TankB)", Assert.Single(r.Conflicts));
        }
    }
}
