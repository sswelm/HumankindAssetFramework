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

        [Fact]
        public void ACommentAndNestedMetadataCannotSupplyTheDistrictRegistry()
        {
            const string doc = @"{
                /* ""districts"": [{ ""district"": ""Comment"" }] */
                ""metadata"": { ""districts"": [{ ""district"": ""Parked"" }] },
                ""districts"": [{ ""district"": ""Active"", ""groundMaterial"": ""Prairie"" }],
                ""bad"": }";
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));
            var district = Assert.Single(DistrictInject.ParseDistrictsRaw(doc));
            Assert.Equal("Active", district.district);
            Assert.Equal("Prairie", district.groundMaterial);
        }

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
            // the reader shared with the model fallback (2026-11-24): the district is the object AROUND its name
            yield return new object[] { "the opening brace missing on the second district",
                @"{ ""districts"": [ " + a + ", " + b.Substring(2) + ", " + c + " ] }" };
            yield return new object[] { "a closing brace missing, and the next district writes its name last",
                @"{ ""districts"": [ { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"", ""hexSculpt"": ""Raised"", ""footprintMeshFlatHeight"": 0.3, { ""footprintMeshFlatHeight"": 0.5, ""fxMeshGuid"": ""1,2,3,4"", ""district"": ""B"" }, " + c + " ] }" };
            yield return new object[] { "a stray ] between two districts", @"{ ""districts"": [ " + a + " ], " + b + ", " + c + " ] }" };
            yield return new object[] { "a closing brace missing, and lookalike objects before and after the array",
                @"{ ""notes"": [ { ""district"": ""example"", ""hexSculpt"": ""X"" } ], ""districts"": [ { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"", ""hexSculpt"": ""Raised"", ""footprintMeshFlatHeight"": 0.3, " + b + ", " + c + @" ], ""parked"": [ { ""district"": ""D"", ""footprintMeshFlatHeight"": 9 } ] }" };
        }

        // NOT MORE THAN THE DOCUMENT SAYS. Without a "districts" array a registry that parses loads no districts
        // ("has no districts array"); a damaged one loads none either, and says so. The readers before this one (the
        // released one, which paired values over the whole text, and the one of earlier on 2026-11-24) recovered
        // districts from each of these documents, read from wherever a "district" key stood - which is also how a
        // parked array comes to life on the day a comma goes missing.
        [Theory]
        [InlineData("the array's key in another case, and a comma missing", @"{ ""Districts"": [ { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"" } { ""district"": ""B"", ""fxMeshGuid"": ""1,2,3,4"" } ] }")]
        [InlineData("a bare array", @"[ { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"" },, { ""district"": ""B"", ""fxMeshGuid"": ""1,2,3,4"" } ]")]
        [InlineData("the array renamed", @"{ ""districts_off"": [ { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"" ] }")]
        [InlineData("not an array: its bracket is gone", @"{ ""districts"": { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"" }, { ""district"": ""B"", ""fxMeshGuid"": ""1,2,3,4"" } }")]
        [InlineData("not an array: null, beside a parked one", @"{ ""districts"": null,, ""parked"": [ { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"" } ] }")]
        public void RegexRecovery_WithoutADistrictsArray_LoadsNoDistricts_AndSaysWhy(string what, string doc)
        {
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));
            var said = new List<string>();
            System.EventHandler<LogEventArgs> listen = (s, e) => { lock (said) said.Add(e.Data?.ToString() ?? ""); };
            Plugin.Log.LogEvent += listen;
            try { Assert.True(DistrictInject.ParseDistrictsRaw(doc).Count == 0, what); }
            finally { Plugin.Log.LogEvent -= listen; }
            lock (said) Assert.Contains(said, line => line.Contains("in a readable \"districts\" array"));
        }

        // A STRAY CLOSING BRACE in the middle of a district. What follows it before the next district can only be this
        // district's own keys; a reader that ended the district at the first brace sent them to their defaults.
        [Fact]
        public void RegexRecovery_AStrayClosingBrace_DoesNotCostTheDistrictItsLaterKeys()
        {
            const string doc = @"{ ""districts"": [ { ""district"": ""D1"", ""fxMeshGuid"": ""1,2,3,4"" }, ""groundMaterial"": ""G1"", ""footprintMeshFlatHeight"": 0.4 }, { ""district"": ""D2"", ""groundMaterial"": ""G2"" } ] }";
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));
            var districts = DistrictInject.ParseDistrictsRaw(doc);
            Assert.Equal(new[] { "D1", "D2" }, districts.Select(d => d.district));
            Assert.Equal(new[] { "G1", "G2" }, districts.Select(d => d.groundMaterial));
            Assert.Equal(new[] { 0.4f, 0.17f }, districts.Select(d => d.footprintMeshFlatHeight));
        }

        // A DISTRICT WITH PARTS. The Factory's districts hold a `parts` array of objects. After a stray `}` the walk
        // closes the district early and takes the first part for the next entry, and the `]` of `parts` for the
        // registry's: a version of this reader that trusted that `]` lost every later district (its second review: 250
        // of 3,497 one-character damages of the live registry).
        [Theory]
        [InlineData("a stray closing brace before the parts", @"""isolate"": true, ""groundMaterial""", @"""isolate"": true }, ""groundMaterial""")]
        [InlineData("a stray closing brace inside the first part", @"[ { ""name"": ""roof""", @"[ { }""name"": ""roof""")]
        [InlineData("a stray closing brace inside the last part", @"{ ""name"": ""wall"", ""tris"": 20 }", @"{ ""name"": ""wall"", }""tris"": 20 }")]
        [InlineData("the district's opening brace lost", null, null)]
        public void RegexRecovery_ADamagedDistrictWithParts_CostsNoLaterDistrict(string what, string from, string to)
        {
            string d1 = @"{ ""district"": ""D1"", ""fxMeshGuid"": ""1,2,3,4"", ""isolate"": true, ""groundMaterial"": ""G1"", ""rotation"": { ""x"": 0.0, ""y"": -90.0, ""z"": -90.0 }, ""parts"": [ { ""name"": ""roof"", ""tris"": 10 }, { ""name"": ""wall"", ""tris"": 20 } ], ""hexSculpt"": ""H1"" }";
            Assert.True(from == null || d1.Contains(from), what);
            d1 = from != null ? d1.Replace(from, to) : d1.Substring(2);
            string doc = @"{ ""districts"": [ " + d1 + @", { ""district"": ""D2"", ""groundMaterial"": ""G2"", ""parts"": [] }, { ""district"": ""D3"", ""groundMaterial"": ""G3"", ""parts"": [ { ""name"": ""roof"" } ] } ] }";
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));
            var districts = DistrictInject.ParseDistrictsRaw(doc);
            Assert.True(new[] { "D1", "D2", "D3" }.SequenceEqual(districts.Select(d => d.district)), what + ": " + string.Join(",", districts.Select(d => d.district)));
            Assert.Equal(new[] { "G1", "G2", "G3" }, districts.Select(d => d.groundMaterial));
            Assert.Equal(new[] { "H1", "", "" }, districts.Select(d => d.hexSculpt));
        }

        // The recovery says that it ran, and what the count found.
        [Fact]
        public void RegexRecovery_OfADamagedStructure_SaysSo()
        {
            const string doc = @"{ ""districts"": [ { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"", { ""district"": ""B"", ""fxMeshGuid"": ""1,2,3,4"" } ] }";
            var said = new List<string>();
            System.EventHandler<LogEventArgs> listen = (s, e) => { lock (said) said.Add(e.Data?.ToString() ?? ""); };
            Plugin.Log.LogEvent += listen;
            try { Assert.Equal(new[] { "A", "B" }, DistrictInject.ParseDistrictsRaw(doc).Select(d => d.district)); }
            finally { Plugin.Log.LogEvent -= listen; }
            lock (said) Assert.Contains(said, line => line.Contains("[District] the structure of the districts array is damaged too (2 object(s) with a \"district\" in the text, 1 readable by their braces)"));
        }

        // ---- the third review (2026-11-24, on the pushed PR): hand edits of a district with parts ----

        // TWO ADJACENT LINES DELETED. The `]` of a district's parts and its `},`: the next district looked like one
        // more part, the count came out even and every later district was lost. The `"parts": [` line and the `{`
        // under it: the first part's brace closed the district, and the `]` of the parts, with nothing before it to
        // close, was taken for the registry's own. An element of an array has a `[` or a comma before it; a `]`
        // followed by `}, {` is an entry's own, and the array goes on.
        [Theory]
        [InlineData("the ] of the parts and the },", @"{ ""name"": ""wall"", ""tris"": 20 } ] }, { ""district"": ""D2""", @"{ ""name"": ""wall"", ""tris"": 20 } { ""district"": ""D2""")]
        [InlineData("the opening of the parts and of the first part", @"""parts"": [ { ""name"": ""roof""", @"""name"": ""roof""")]
        public void RegexRecovery_TwoAdjacentLinesOfADistrictDeleted_CostNoLaterDistrict(string what, string from, string to)
        {
            string doc = @"{ ""districts"": [ { ""district"": ""D1"", ""fxMeshGuid"": ""1,2,3,4"", ""groundMaterial"": ""G1"", ""parts"": [ { ""name"": ""roof"", ""tris"": 10 }, { ""name"": ""wall"", ""tris"": 20 } ] }"
                + @", { ""district"": ""D2"", ""groundMaterial"": ""G2"", ""parts"": [] }, { ""district"": ""D3"", ""groundMaterial"": ""G3"", ""parts"": [ { ""name"": ""roof"" } ] } ] }";
            Assert.True(doc.Contains(from), what);
            doc = doc.Replace(from, to);
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));
            var districts = DistrictInject.ParseDistrictsRaw(doc);
            Assert.True(new[] { "D1", "D2", "D3" }.SequenceEqual(districts.Select(d => d.district)), what + ": " + string.Join(",", districts.Select(d => d.district)));
            Assert.Equal(new[] { "G1", "G2", "G3" }, districts.Select(d => d.groundMaterial));
        }

        // The array's key written twice with the empty one first loads nothing (the first is read, as ever), and a
        // `"district": [` typed for `"districts": [` leaves no array: both must say why, and count the DISTRICTS the
        // file holds - the misspelled array key is not one of them.
        [Theory]
        [InlineData("the key twice, the empty one first", @"{ ""districts"": [],, ""districts"": [ { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"" }, { ""district"": ""B"", ""fxMeshGuid"": ""1,2,3,4"" } ] }")]
        [InlineData("district typed for districts", @"{ ""district"": [ { ""district"": ""A"", ""fxMeshGuid"": ""1,2,3,4"" } { ""district"": ""B"", ""fxMeshGuid"": ""1,2,3,4"" } ] }")]
        public void RegexRecovery_WithNoDistrictInAReadableArray_CountsTheDistrictsItDidNotRead(string what, string doc)
        {
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));
            var said = new List<string>();
            System.EventHandler<LogEventArgs> listen = (s, e) => { lock (said) said.Add(e.Data?.ToString() ?? ""); };
            Plugin.Log.LogEvent += listen;
            try { Assert.True(DistrictInject.ParseDistrictsRaw(doc).Count == 0, what); }
            finally { Plugin.Log.LogEvent -= listen; }
            lock (said) Assert.Contains(said, line => line.Contains("holds 2 \"district\" key(s), none of them in a readable \"districts\" array"));
        }

        // TWO QUOTES LOST IN ONE DISTRICT. Between them the walk reads the text inside out: the `[` of the parts and of
        // the copies stand inside what it takes for strings, the district closes at the first copy's brace, and the
        // `]` of the copies stands there as the registry's own, with no `[` open before it. The walk's end is not
        // trusted once a string ran over a line end, which no JSON string does (the third review's own fix lost the
        // district after it in 64 of 5,819 double damages of the live registry before this was held).
        [Fact]
        public void RegexRecovery_TwoQuotesLostInADistrictWithParts_CostNoLaterDistrict()
        {
            string doc = @"{ ""districts"": [ { ""district"": ""D1"", ""fxMeshGuid"": ""1,2,3,4"", ""modelFile"": D:/m/temple.glb""," + "\n"
                + @" ""parts"": [ { ""copies"": [ { ""x"": 1.5, z"": -1.5 }, { ""x"": -1.5, ""z"": 1.5 }, { ""x"": -1.5, ""z"": -1.5 } ], ""targetTris"": 6000 } ] }, "
                + @"{ ""district"": ""D2"", ""fxMeshGuid"": ""5,6,7,8"", ""groundMaterial"": ""G2"" } ] }";
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));
            var districts = DistrictInject.ParseDistrictsRaw(doc);
            Assert.Equal(new[] { "D1", "D2" }, districts.Select(d => d.district));
            Assert.Equal("G2", districts[1].groundMaterial);
        }

        // A null text is an empty registry, on both ways in.
        [Fact]
        public void EntryTexts_OfNoText_AreNone()
        {
            Assert.Empty(DistrictInject.DistrictEntryTexts(null));
            Assert.Empty(UniversalInject.RegistryEntryTexts(null, "models", "resourceName", out string damage, out int namesWithoutArray, out _));
            Assert.Null(damage);
            Assert.Equal(0, namesWithoutArray);
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
