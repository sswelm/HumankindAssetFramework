using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // THE DISTRICT REGISTRY'S BLAST RADIUS — the twin of the pack `modId` crash.
    //
    // `haf_districts.json` was parsed by one `JObject.Parse` inside one try, with the per-entry loop inside it too:
    // a SINGLE malformed character left `distModels` empty and every custom district silently gone behind one
    // LogError, and a single bad ENTRY aborted the loop and took every entry after it too. The model registry has
    // had a field-by-field regex fallback for exactly this since it shipped; its twin never got one.
    //
    // NOTE ON WHAT THESE CAN ASSERT: every GUID field routes through MakeGuid, which needs the live game, so
    // outside it every guid is null. That is why the accept/reject gate (`Usable`) is deliberately separate from
    // the two extractors. Tests compare what each path extracts and check that missing values stay in their own entry.
    public class DistrictRegistryParseTests
    {
        public DistrictRegistryParseTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
        }

        // Two entries, every key the Factory writes, in the Factory's own order.
        const string TwoDistricts = @"{
          ""districts"": [
            { ""district"": ""Extension_A"", ""fxMeshGuid"": ""1,2,3,4"", ""atlasGuid"": ""5,6,7,8"",
              ""normalAtlasGuid"": ""9,10,11,12"", ""roughAtlasGuid"": """", ""footprintDonor"": """",
              ""selectorGuid"": ""13,14,15,16"", ""groundMaterial"": ""Prairie"", ""hexSculpt"": ""Sculpt_A"",
              ""isolate"": true, ""footprintMesh"": true, ""footprintMeshBW"": true, ""footprintMeshFlat"": false,
              ""footprintMeshHideDecal"": true, ""footprintMeshFlatHeight"": 0.02 },
            { ""district"": ""Extension_B"", ""fxMeshGuid"": ""21,22,23,24"", ""atlasGuid"": """",
              ""normalAtlasGuid"": """", ""roughAtlasGuid"": """", ""footprintDonor"": ""31,32,33,34"",
              ""selectorGuid"": """", ""groundMaterial"": """", ""hexSculpt"": """",
              ""isolate"": false, ""footprintMesh"": false, ""footprintMeshBW"": false, ""footprintMeshFlat"": true,
              ""footprintMeshHideDecal"": false, ""footprintMeshFlatHeight"": 0.5 }
          ]
        }";

        // ---- THE PARITY ORACLE: the fallback must recover what the object parse extracts ----

        // Both extractors should agree on the values from a well-formed document.
        [Fact]
        public void RegexRecovery_MatchesTheObjectParse_FieldForField()
        {
            var json = DistrictInject.JsonDistricts(TwoDistricts);
            var rex = DistrictInject.RegexDistricts(TwoDistricts);

            Assert.Equal(json.Count, rex.Count);
            for (int i = 0; i < json.Count; i++)
            {
                Assert.Equal(json[i].district, rex[i].district);
                Assert.Equal(json[i].groundMaterial, rex[i].groundMaterial);
                Assert.Equal(json[i].hexSculpt, rex[i].hexSculpt);
                Assert.Equal(json[i].isolate, rex[i].isolate);
                Assert.Equal(json[i].footprintMesh, rex[i].footprintMesh);
                Assert.Equal(json[i].footprintMeshBW, rex[i].footprintMeshBW);
                Assert.Equal(json[i].footprintMeshFlat, rex[i].footprintMeshFlat);
                Assert.Equal(json[i].footprintMeshHideDecal, rex[i].footprintMeshHideDecal);
                Assert.Equal(json[i].footprintMeshFlatHeight, rex[i].footprintMeshFlatHeight, 4);
            }
        }

        // Alignment specifically: entry B's distinct values must land on entry B, not bleed onto A.
        [Fact]
        public void RegexRecovery_KeepsEntriesInOrderAndDoesNotCrossFields()
        {
            var rex = DistrictInject.RegexDistricts(TwoDistricts);
            Assert.Equal(new[] { "Extension_A", "Extension_B" }, rex.Select(e => e.district).ToArray());
            Assert.True(rex[0].isolate);
            Assert.False(rex[1].isolate);
            Assert.Equal("Prairie", rex[0].groundMaterial);
            Assert.Equal("", rex[1].groundMaterial);
            Assert.Equal(0.02f, rex[0].footprintMeshFlatHeight, 4);
            Assert.Equal(0.5f, rex[1].footprintMeshFlatHeight, 4);
        }

        // ---- THE BUG: a broken document used to yield ZERO districts ----

        // Asserts on ParseDistrictsRAW, not ParseDistricts: the public entry filters through Usable(), and outside
        // the game every guid is null, so it returns empty no matter what and the assertion proves nothing. The
        // first version of this test made exactly that mistake — deleting the whole regex fallback left the suite
        // green. Raw is where the primary-or-fallback decision is observable.
        // The malformation is a LITERAL, not a Replace() on the valid one. The first version built it with
        // .Replace("}\n          ]", ...) — which silently matched NOTHING, because a verbatim string in a CRLF
        // file contains \r\n. The "broken" fixture was byte-identical to the valid one, so the test ran the object
        // path and passed while the regex fallback could be deleted wholesale without failing anything.
        const string BrokenDoc =
            @"{ ""districts"": [ { ""district"": ""Extension_A"", ""fxMeshGuid"": ""1,2,3,4"", ""isolate"": true }, " +
            @"{ ""district"": ""Extension_B"", ""fxMeshGuid"": ""21,22,23,24"", ""isolate"": false } ] ";   // no closing brace

        [Fact]
        public void TheBrokenFixture_IsReallyUnparseable()
        {
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(BrokenDoc));
        }

        [Fact]
        public void MalformedJson_FallsBackAndStillRecoversEveryEntry()
        {
            Assert.Equal(new[] { "Extension_A", "Extension_B" },
                         DistrictInject.ParseDistrictsRaw(BrokenDoc).Select(e => e.district).ToArray());   // 0 before the fallback existed
        }

        [Fact]
        public void RegexRecovery_MissingFieldsStayAtTheirDefaultsAndLaterEntriesKeepTheirValues()
        {
            const string doc = @"{ ""districts"": [
                { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"" },
                { ""footprintMeshFlatHeight"": 5e-1, ""district"": ""B"", ""footprintMesh"": true, ""groundMaterial"": ""Stone }"" },
                { ""district"": ""C"", ""footprintMeshFlatHeight"": 0.75";
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));
            var districts = DistrictInject.ParseDistrictsRaw(doc);
            Assert.Equal(new[] { "A", "B", "C" }, districts.Select(d => d.district));
            Assert.Equal(new[] { 0.17f, 0.5f, 0.75f }, districts.Select(d => d.footprintMeshFlatHeight));
            Assert.Equal(new[] { false, true, false }, districts.Select(d => d.footprintMesh));
            Assert.Equal(new[] { "", "Stone }", "" }, districts.Select(d => d.groundMaterial));
        }

        // WHEN THE BRACES CANNOT BE TRUSTED. One object per district is found by counting braces outside strings, so a
        // closing quote or brace missing in ONE district swallowed every district after it, and a damaged wrapper left
        // nothing to count: 1 of 3 and 0 of 3 districts recovered (measured 2026-11-24), where the reader before it
        // named all three. Each row is one such damage; all three districts must come back, each with its own values.
        public static IEnumerable<object[]> DamagedStructures()
        {
            const string a = @"{ ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"", ""hexSculpt"": ""Raised"", ""footprintMeshFlatHeight"": 0.3 }";
            const string b = @"{ ""district"": ""B"", ""fxMeshGuid"": ""1,2,3,4"", ""footprintMeshFlatHeight"": 0.5 }";   // no hexSculpt: it must stay empty
            const string c = @"{ ""district"": ""C"", ""fxMeshGuid"": ""1,2,3,4"", ""hexSculpt"": ""Sunk"", ""footprintMeshFlatHeight"": 0.7 }";
            yield return new object[] { "a closing quote missing in the first district",
                @"{ ""districts"": [ { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"", ""groundMaterial"": ""Grass, ""hexSculpt"": ""Raised"", ""footprintMeshFlatHeight"": 0.3 }, " + b + ", " + c + " ] }" };
            yield return new object[] { "a closing brace missing on the first district",
                @"{ ""districts"": [ { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"", ""hexSculpt"": ""Raised"", ""footprintMeshFlatHeight"": 0.3, " + b + ", " + c + " ] }" };
            yield return new object[] { "the wrapper lost its colon", @"{ ""districts"" [ " + a + ", " + b + ", " + c + " ] }" };
            yield return new object[] { "the wrapper lost its bracket", @"{ ""districts"": " + a + ", " + b + ", " + c + " }" };
            yield return new object[] { "the wrapper key is misspelled, and a comma is missing", @"{ ""Districts"": [ " + a + " " + b + ", " + c + " ] }" };
            yield return new object[] { "no wrapper at all", "[ " + a + ",, " + b + ", " + c + " ]" };
        }

        [Theory]
        [MemberData(nameof(DamagedStructures))]
        public void RegexRecovery_ADamagedStructure_StillYieldsEveryDistrictWithItsOwnValues(string damage, string doc)
        {
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));
            var districts = DistrictInject.ParseDistrictsRaw(doc);
            Assert.True(new[] { "A", "B", "C" }.SequenceEqual(districts.Select(d => d.district)), damage + ": " + string.Join(",", districts.Select(d => d.district)));
            Assert.Equal(new[] { 0.3f, 0.5f, 0.7f }, districts.Select(d => d.footprintMeshFlatHeight));
            Assert.Equal(new[] { "Raised", "", "Sunk" }, districts.Select(d => d.hexSculpt));
        }

        // The structure intact, the entry texts are the objects themselves (keys in any order are the entry's own).
        [Fact]
        public void EntryTexts_OfAnIntactArray_AreItsObjects()
        {
            var texts = DistrictInject.DistrictEntryTexts(@"{ ""districts"": [ { ""hexSculpt"": ""Raised"", ""district"": ""A"" },, { ""district"": ""B"" } ] }");
            Assert.Equal(new[] { @"{ ""hexSculpt"": ""Raised"", ""district"": ""A"" }", @"{ ""district"": ""B"" }" }, texts);
        }

        [Fact]
        public void RegexRecovery_ReadsOnlyTheDistrictsArray()
        {
            const string doc = @"{ ""notes"": { ""district"": ""example"", ""footprintMeshFlatHeight"": 99 },
                ""districts"": [ { ""district"": ""Actual"", ""footprintMeshFlatHeight"": 0.25 } ]";
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));
            var entry = Assert.Single(DistrictInject.ParseDistrictsRaw(doc));
            Assert.Equal("Actual", entry.district);
            Assert.Equal(0.25f, entry.footprintMeshFlatHeight);
        }

        // ...and a WELL-FORMED document must still go through the object path, not quietly rely on the fallback.
        [Fact]
        public void WellFormedJson_UsesTheObjectPath()
        {
            var raw = DistrictInject.ParseDistrictsRaw(TwoDistricts);
            Assert.Equal(2, raw.Count);
            Assert.Equal("Prairie", raw[0].groundMaterial);
        }

        [Fact]
        public void MalformedJson_DoesNotThrow()
        {
            var ex = Record.Exception(() => DistrictInject.ParseDistricts(@"{ ""districts"": [ { ""district"": ""A"" ,,, }"));
            Assert.Null(ex);
        }

        // ---- one bad ENTRY must not sink its neighbours ----

        [Fact]
        public void OneUnparseableEntry_DoesNotDropTheOthers()
        {
            // a non-numeric flat height is a FormatException on the (float?) cast — pinned by the test below, so
            // this fixture cannot quietly stop being malformed the way the last one did
            const string mixed = @"{ ""districts"": [
                { ""district"": ""Good1"", ""fxMeshGuid"": ""1,2,3,4"" },
                { ""district"": ""Bad"",   ""fxMeshGuid"": ""1,2,3,4"", ""footprintMeshFlatHeight"": ""not-a-number"" },
                { ""district"": ""Good2"", ""fxMeshGuid"": ""1,2,3,4"" } ] }";
            // RAW again — and the assertion is that the loop RAN ON: exactly the two good entries survive, in
            // order, with the bad one dropped. Without the per-entry try the whole document throws and falls to
            // the regex path, which happily recovers all THREE names (its `(true|false)` match just misses the bad
            // isolate) — so "count == 2 and no Bad" is what separates the two behaviours.
            var raw = DistrictInject.ParseDistrictsRaw(mixed);
            Assert.Equal(new[] { "Good1", "Good2" }, raw.Select(e => e.district).ToArray());
            Assert.DoesNotContain(raw, e => e.district == "Bad");
        }

        // Pins that the "bad entry" above really is bad — otherwise the test above degrades into asserting that a
        // perfectly valid document parses, which is what happened to the malformed-JSON fixture.
        [Fact]
        public void TheBadEntry_ReallyThrowsOnBuild()
        {
            var tok = Newtonsoft.Json.Linq.JObject.Parse(@"{ ""footprintMeshFlatHeight"": ""not-a-number"" }");
            Assert.ThrowsAny<System.Exception>(() => DistrictInject.BuildDistrict(tok));
        }

        // ---- shape edge cases ----

        [Fact]
        public void NoDistrictsArray_IsEmptyNotAnException()
        {
            Assert.Empty(DistrictInject.ParseDistricts(@"{ ""somethingElse"": [] }"));
        }

        [Fact]
        public void EmptyDocument_IsEmptyNotAnException()
        {
            Assert.Empty(DistrictInject.ParseDistricts(""));
        }

        [Fact]
        public void RegexRecovery_OfAnEmptyDocumentIsEmpty()
        {
            Assert.Empty(DistrictInject.RegexDistricts(""));
        }

        // The accept/reject rule is unchanged: a nameless entry is still rejected, by BOTH paths, via one gate.
        [Fact]
        public void Usable_RejectsANamelessEntry()
        {
            var raw = new List<DistrictInject.DistrictModel> { new DistrictInject.DistrictModel { district = "" } };
            Assert.Empty(DistrictInject.Usable(raw));
        }
    }
}
