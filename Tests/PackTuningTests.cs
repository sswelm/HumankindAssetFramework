using System.Collections.Generic;
using System.Linq;
using Xunit;
using HumankindAssetFramework;

// PackTuning.Parse — the three pack tuning tables, parsed from the RESOLVED packs in load order (2026-08-21).
// The bug this guards: the tables used to be scraped from the raw discovery list, so a pack that resolution rejected
// still resized units, and "later wins" meant alphabetical, not mod order.
public class PackTuningTests
{
    static KeyValuePair<string, string> P(string modId, string json) => new KeyValuePair<string, string>(modId, json);

    [Fact]
    public void Only_the_packs_handed_in_contribute()   // a rejected pack is simply not in the list — its rules never exist
    {
        var r = UniversalInject.PackTuning.Parse(new[] { P("a", "{\"unitScales\":[{\"match\":\"Tank\",\"scale\":0.6}]}") });
        Assert.Single(r.ScaleRules);
        Assert.Equal("Tank", r.ScaleRules[0].match);
        Assert.Equal(0.6f, r.ScaleRules[0].scale, 3);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void UnitScales_shared_match_across_packs_is_named_with_the_composed_factor()
    {
        var r = UniversalInject.PackTuning.Parse(new[] {
            P("a", "{\"unitScales\":[{\"match\":\"Tank\",\"scale\":0.6}]}"),
            P("b", "{\"unitScales\":[{\"match\":\"tank\",\"scale\":0.5,\"era\":6}]}") });
        Assert.Equal(2, r.ScaleRules.Count);                      // both still apply — the policy is "named", not "dropped"
        Assert.Equal(6, r.ScaleRules[1].era);
        var n = Assert.Single(r.Notes);
        Assert.Contains("'a' x0.6", n); Assert.Contains("'b' x0.5", n); Assert.Contains("x0.3", n);
    }

    [Fact]
    public void UnitScales_same_pack_twice_is_not_a_cross_pack_note()
    {
        var r = UniversalInject.PackTuning.Parse(new[] { P("a", "{\"unitScales\":[{\"match\":\"Tank\",\"scale\":0.6},{\"match\":\"Tank\",\"scale\":0.9}]}") });
        Assert.Equal(2, r.ScaleRules.Count); Assert.Empty(r.Notes);
    }

    [Fact]
    public void EraGrid_row_is_owned_by_the_later_pack_in_the_order_given_and_named()
    {
        var r = UniversalInject.PackTuning.Parse(new[] {
            P("zeta",  "{\"eraGrid\":[{\"unitEra\":1,\"scales\":[1,1.1]},{\"unitEra\":2,\"scales\":[0.9]}]}"),
            P("alpha", "{\"eraGrid\":[{\"unitEra\":2,\"scales\":[0.5,0.5]}]}") });   // alphabetical order would have made 'zeta' win
        Assert.Equal(new[] { 1f, 1.1f }, r.EraGridRows[1]);
        Assert.Equal(new[] { 0.5f, 0.5f }, r.EraGridRows[2]);
        var n = Assert.Single(r.Notes);
        Assert.Contains("era 2", n); Assert.Contains("'alpha' overrides 'zeta'", n);
    }

    [Fact]
    public void FormationThresholds_whole_table_is_replaced_by_the_later_pack_and_sorted()
    {
        var r = UniversalInject.PackTuning.Parse(new[] {
            P("a", "{\"formationThresholds\":[{\"threshold\":0.5,\"formation\":\"Line\"}]}"),
            P("b", "{\"formationThresholds\":[{\"threshold\":2.0,\"formation\":\"Column\"},{\"threshold\":0.8,\"formation\":\"Wedge\"}]}") });
        Assert.Equal(new[] { "Wedge", "Column" }, r.FormationBySize.Select(t => t.Value).ToArray());
        Assert.Contains("'b' replaces 'a'", Assert.Single(r.Notes));
    }

    [Fact]
    public void Absent_tables_and_null_text_are_harmless()
    {
        var r = UniversalInject.PackTuning.Parse(new[] { P("a", "{}"), P("b", null) });
        Assert.Empty(r.ScaleRules); Assert.Empty(r.EraGridRows); Assert.Empty(r.FormationBySize); Assert.Empty(r.Notes); Assert.Empty(r.Warnings);
    }
}
