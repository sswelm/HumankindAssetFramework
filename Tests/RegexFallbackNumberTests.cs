using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // HOW A NUMBER IS SPELLED (2026-11-23). Three regex readers - the fallback of ParseModels for a pack whose entries
    // cannot be read as JSON objects, the district fallback, and the unit-scale rules of the pack tuning tables - each
    // took a float with a character class of their own. Two classes were wrong in the same quiet way: `-?[\d.eE+]+` has
    // no `-` inside, so 1.5e-2 stopped at the `e`, the parse of "1.5e" failed, and the dial came back as its DEFAULT (a
    // scale rule was dropped); `-?[0-9.]+`, on eleven dials, has no exponent at all, so 1.5e+2 read 1.5. Found by the
    // review of PR #313, whose fraction test passes because a plain decimal was always read right. These three readers
    // now take UniversalInject.JsonNumber. (The tuning table's `threshold` keeps a pattern of its own, which reads
    // exponents; the integer dials keep theirs.)
    //
    // The oracle is not the code under test: the expected value is the runtime's own invariant parse of the spelling,
    // and, wherever the document is JSON, the JSON parser's reading of the same entry.
    public class RegexFallbackNumberTests
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Every float a pack can set on a model: the fields of ModelEntry that the plugin's own key list
        /// (registryConfigKeys) admits - the shared schema's and the plugin-only rotorSpinSpeed.</summary>
        static FieldInfo[] FloatDials()
        {
            var keysField = typeof(UniversalInject).GetField("registryConfigKeys", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(keysField != null, "UniversalInject.registryConfigKeys was not found (renamed? these tests take their fields from it)");
            var keys = (HashSet<string>)keysField.GetValue(null);
            var floats = typeof(ModelEntry).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(float) && keys.Contains(f.Name)).OrderBy(f => f.Name, System.StringComparer.Ordinal).ToArray();
            Assert.True(floats.Length >= 30, "the float dials were not found: " + floats.Length);
            Assert.Contains(floats, f => f.Name == "rotorSpinSpeed");
            return floats;
        }

        static (ModelEntry viaObject, ModelEntry viaRegex) BothPaths(string body)
        {
            string wellFormed = "{ \"modId\": \"m\", \"models\": [ { " + body + " } ] }";
            string broken = "{ \"modId\": \"m\",, \"models\": [ { " + body + " } ] }";   // a doubled comma: the regex path
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(broken));
            return (Assert.Single(UniversalInject.ParseModels(wellFormed)), Assert.Single(UniversalInject.ParseModels(broken)));
        }

        // Each spelling is given to EVERY float dial and to position's three numbers at once. The first three rows were
        // always read right; the rest are the spellings a writer may choose for the same values.
        [Theory]
        [InlineData("0.375")]
        [InlineData("-0.375")]
        [InlineData("3")]
        [InlineData("1.5e-2")]                 // came back as the default
        [InlineData("1.5E-2")]
        [InlineData("-1.5e-2")]
        [InlineData("1e-05")]
        [InlineData("9.999999747378752e-6")]   // how a float of 0.00001 is written as a double
        [InlineData("1.5e+2")]                 // read 1.5 on the eleven dials without an exponent in their pattern
        [InlineData("1.5e2")]
        [InlineData("-2.5E+1")]
        [InlineData("5E-1")]
        [InlineData(".5")]                     // not JSON by the letter; the JSON parser takes both
        [InlineData("5.")]
        public void EveryFloatDial_ReadsTheSpelling_AsTheJsonParserDoes(string spelling)
        {
            float want = float.Parse(spelling, NumberStyles.Float, Inv);
            var floats = FloatDials();
            var body = new StringBuilder("\"resourceName\": \"Dials\", \"pawnDescription\": \"PAWN_A\"");
            foreach (var f in floats) body.Append(", \"").Append(f.Name).Append("\": ").Append(spelling);
            body.Append(", \"position\": { \"x\": ").Append(spelling).Append(", \"y\": ").Append(spelling).Append(", \"z\": ").Append(spelling).Append(" }");

            var (viaObject, viaRegex) = BothPaths(body.ToString());
            foreach (var f in floats)
            {
                Assert.True(want == (float)f.GetValue(viaObject), f.Name + " = " + spelling + " through the JSON parser: " + ((float)f.GetValue(viaObject)).ToString("R", Inv));
                Assert.True(want == (float)f.GetValue(viaRegex), f.Name + " = " + spelling + " through the regex fallback: " + ((float)f.GetValue(viaRegex)).ToString("R", Inv) + ", wanted " + want.ToString("R", Inv));
            }
            Assert.Equal(new[] { want, want, want }, new[] { viaObject.position.x, viaObject.position.y, viaObject.position.z });
            Assert.Equal(new[] { want, want, want }, new[] { viaRegex.position.x, viaRegex.position.y, viaRegex.position.z });
        }

        // WHAT A HAND TYPES. These spellings are not JSON, so each of them is what sends its file to the fallback - which
        // must then read it. Both old patterns, or one of them, read each of these; the first version of the one pattern
        // refused them all (a lookahead for "a whole number or nothing") and every dial fell to its default (the review of
        // PR #315). The JSON parser has no say here: it refuses the document.
        [Theory]
        [InlineData("+1.5", 1.5f)]       // a leading plus
        [InlineData("+1.5e-2", 0.015f)]
        [InlineData("0.5f", 0.5f)]       // a C-style suffix
        [InlineData("2.5em", 2.5f)]      // a unit after the number
        [InlineData("2.5e", 2.5f)]       // an exponent that was never finished
        [InlineData("2.5e+", 2.5f)]
        public void ASpellingThatIsNotJson_IsReadAsTheNumberItStartsWith(string spelling, float want)
        {
            var floats = FloatDials();
            var body = new StringBuilder("\"pawnDescription\": \"P\"");
            foreach (var f in floats) body.Append(", \"").Append(f.Name).Append("\": ").Append(spelling);
            body.Append(", \"position\": { \"x\": ").Append(spelling).Append(", \"y\": ").Append(spelling).Append(", \"z\": ").Append(spelling).Append(" }");
            string doc = "{ \"modId\": \"m\", \"models\": [ { " + body + " } ] }";   // no doubled comma: the spelling itself breaks it
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));

            var e = Assert.Single(UniversalInject.ParseModels(doc));
            foreach (var f in floats)
                Assert.True(want == (float)f.GetValue(e), f.Name + " = " + spelling + " through the regex fallback: " + ((float)f.GetValue(e)).ToString("R", Inv) + ", wanted " + want.ToString("R", Inv));
            Assert.Equal(new[] { want, want, want }, new[] { e.position.x, e.position.y, e.position.z });
        }

        // What float.TryParse refuses stays a default: the pattern only chooses the token.
        [Theory]
        [InlineData("1.2.3")]        // matched whole, and not a number
        [InlineData("abc")]          // nothing to match
        [InlineData("--1")]
        [InlineData("1e39")]         // past a float: the runtime refuses it here, where the JSON parser reads Infinity
        public void WhatIsNotANumber_LeavesTheDialAtItsDefault(string spelling)
        {
            var e = Assert.Single(UniversalInject.ParseModels(
                "{ \"modId\": \"m\",, \"models\": [ { \"pawnDescription\": \"P\", \"scale\": " + spelling + ", \"turnRate\": " + spelling + ", \"brightness\": 2.5 } ] }"));
            Assert.Equal(1f, e.scale);
            Assert.Equal(0f, e.turnRate);
            Assert.Equal(2.5f, e.brightness);   // the entry itself was read
        }

        // A NUMBER IN QUOTES. The JSON parser takes "0.375" for a float field; the fallback never matched one, so the dial
        // fell to its default - and for idleAltInterval, whose fallback default was 0 until this date, a file's "0" came
        // out right by accident. With the default corrected to 25 that accident would have turned the one-shots ON against
        // the file (the review of PR #315), so the fallback now reads the quoted number itself.
        [Theory]
        [InlineData("0.375")]
        [InlineData("-0.375")]
        [InlineData("1.5e-2")]
        [InlineData("0")]
        public void ANumberInQuotes_IsReadOnBothPaths(string spelling)
        {
            float want = float.Parse(spelling, NumberStyles.Float, Inv);
            var floats = FloatDials();
            var body = new StringBuilder("\"resourceName\": \"Dials\", \"pawnDescription\": \"PAWN_A\"");
            foreach (var f in floats) body.Append(", \"").Append(f.Name).Append("\": \"").Append(spelling).Append("\"");
            var (viaObject, viaRegex) = BothPaths(body.ToString());
            foreach (var f in floats)
            {
                Assert.True(want == (float)f.GetValue(viaObject), f.Name + " = \"" + spelling + "\" through the JSON parser: " + ((float)f.GetValue(viaObject)).ToString("R", Inv));
                Assert.True(want == (float)f.GetValue(viaRegex), f.Name + " = \"" + spelling + "\" through the regex fallback: " + ((float)f.GetValue(viaRegex)).ToString("R", Inv));
            }
        }

        // The key present and its value not a number at all: the dial's default, as for every other dial. For
        // idleAltInterval that is the schema's 25 (the one-shots on), where the fallback gave 0 until this date; the
        // documented way to switch them off is a number, 0.
        [Theory]
        [InlineData("\"off\"")]
        [InlineData("false")]
        [InlineData("null")]
        public void IdleAltInterval_WithAValueThatIsNotANumber_IsTheSchemaDefault(string value)
        {
            var e = Assert.Single(UniversalInject.ParseModels(
                "{ \"modId\": \"m\",, \"models\": [ { \"pawnDescription\": \"P\", \"idleAltInterval\": " + value + ", \"brightness\": 2.5 } ] }"));
            Assert.Equal(new ModelEntry().idleAltInterval, e.idleAltInterval);
            Assert.Equal(2.5f, e.brightness);
        }

        // attackRepeats: the one INTEGER among the forty captures that changed pattern (it was read with the first float
        // pattern, and still is parsed as an int). Held here because no float test reaches it: its capture and its
        // default could both be changed with every other test green (the review of PR #315). A whole number in any of
        // these spellings reads; what the JSON parser also takes is compared with it.
        [Theory]
        [InlineData("3", true)]
        [InlineData("\"3\"", true)]
        [InlineData("+3", false)]        // not JSON
        [InlineData("3x", false)]
        public void AttackRepeats_ReadsAWholeNumber(string spelling, bool isJson)
        {
            string body = "\"resourceName\": \"R\", \"pawnDescription\": \"P\", \"attackRepeats\": " + spelling;
            var viaRegex = Assert.Single(UniversalInject.ParseModels("{ \"modId\": \"m\",, \"models\": [ { " + body + " } ] }"));
            Assert.Equal(3, viaRegex.attackRepeats);
            if (isJson) Assert.Equal(3, Assert.Single(UniversalInject.ParseModels("{ \"models\": [ { " + body + " } ] }")).attackRepeats);
        }

        // Left out, it is the schema's default on both paths. (Written as a float - 3.0, 2e0 - the fallback gives this
        // default too, where the JSON parser converts: an integer dial's limit, in the Review-Backlog, not held here.)
        [Fact]
        public void AttackRepeats_LeftOut_DefaultsTheSameThroughTheRegexFallback()
        {
            var (viaObject, viaRegex) = BothPaths("\"resourceName\": \"R\", \"pawnDescription\": \"P\"");
            Assert.Equal(new ModelEntry().attackRepeats, viaObject.attackRepeats);
            Assert.Equal(new ModelEntry().attackRepeats, viaRegex.attackRepeats);
        }

        // One number of position that is not a number is 0, and the other two are still read.
        [Fact]
        public void Position_WithOneNumberThatIsNot_KeepsTheOtherTwo()
        {
            var e = Assert.Single(UniversalInject.ParseModels(
                "{ \"modId\": \"m\",, \"models\": [ { \"pawnDescription\": \"P\", \"position\": { \"x\": 1.2.3, \"y\": 2.5, \"z\": 3.5 } } ] }"));
            Assert.Equal(new[] { 0f, 2.5f, 3.5f }, new[] { e.position.x, e.position.y, e.position.z });
        }

        // THE FALLBACK IS ALSO REACHED BY A DOCUMENT THAT PARSES: when no entry can be mapped (here the only entry holds a
        // value the mapper refuses), the whole file goes to the regex reader. The exponent defect bit there too.
        [Fact]
        public void AParseableDocumentWhoseOnlyEntryCannotBeMapped_TakesTheFallback_AndItsExponentsAreRead()
        {
            string doc = "{ \"models\": [ { \"resourceName\": \"R\", \"pawnDescription\": \"P\", \"hideSubPawns\": \"perhaps\", \"scale\": 1.5e-2, \"turnRate\": 1.5e+2 } ] }";
            Newtonsoft.Json.Linq.JObject.Parse(doc);   // it IS JSON
            var e = Assert.Single(UniversalInject.ParseModels(doc));
            Assert.Equal(0.015f, e.scale);
            Assert.Equal(150f, e.turnRate);
        }

        // A FALLBACK THAT INVENTS A DIFFERENT DEFAULT changes behaviour on the one day it runs. Every float dial left out
        // of the entry must come back from the fallback as the JSON parser leaves it (the schema's initializer).
        // idleAltInterval did not: 25 through the parser, 0 through the fallback, which switched the idle-alt
        // one-shots off for every entry of a pack that needed the fallback (reviews carried it from 2026-10-12 on; the
        // Review-Backlog had marked it fixed on the strength of the parser's path alone).
        [Fact]
        public void AFloatDialLeftOut_DefaultsTheSameThroughTheRegexFallback()
        {
            var (viaObject, viaRegex) = BothPaths("\"resourceName\": \"Dials\", \"pawnDescription\": \"PAWN_A\"");
            var fresh = new ModelEntry();
            foreach (var f in FloatDials())
            {
                Assert.True((float)f.GetValue(fresh) == (float)f.GetValue(viaObject), f.Name + " left out, through the JSON parser: " + f.GetValue(viaObject));
                Assert.True((float)f.GetValue(fresh) == (float)f.GetValue(viaRegex), f.Name + " left out, through the regex fallback: " + ((float)f.GetValue(viaRegex)).ToString("R", Inv) + ", the schema's default is " + ((float)f.GetValue(fresh)).ToString("R", Inv));
            }
            Assert.Equal(25f, viaRegex.idleAltInterval);
        }

        // The district fallback's one float.
        [Theory]
        [InlineData("2.5e-1", 0.25f)]    // came back as the default, 0.17
        [InlineData("2.5E-1", 0.25f)]
        [InlineData("0.25", 0.25f)]
        [InlineData("2.5e+1", 25f)]
        public void TheDistrictFallback_ReadsTheFlatHeightInAnySpelling(string spelling, float want)
        {
            string doc = "{ \"districts\": [ { \"district\": \"D1\", \"fxMeshGuid\": \"1,2,3,4\", \"footprintMeshFlatHeight\": " + spelling + " } ] }";
            Assert.Equal(want, Assert.Single(DistrictInject.RegexDistricts(doc)).footprintMeshFlatHeight);
            Assert.Equal(want, Assert.Single(DistrictInject.JsonDistricts(doc)).footprintMeshFlatHeight);
        }

        // THE DISTRICT READER PAIRS VALUES BY POSITION, so a height it cannot read must still take its place. With the
        // first version of the one pattern an unreadable first height vanished from the matches and every later district
        // took its neighbour's: 0.5, 0.7 and the default where the file says x, 0.5 and 0.7 (the review of PR #315).
        // Each first value is given with what it reads as; the two after it must stay their own in every row.
        [Theory]
        [InlineData("0.3", 0.3f)]
        [InlineData("+0.3", 0.3f)]         // not JSON: this token is what sends the file to the fallback
        [InlineData("0.3f", 0.3f)]
        [InlineData("3e-1", 0.3f)]
        [InlineData("\"0.3\"", 0.3f)]
        [InlineData("tall", 0.17f)]        // not a number: the default, in ITS place
        [InlineData("null", 0.17f)]
        [InlineData("\"tall\"", 0.17f)]
        public void TheDistrictFallback_KeepsEveryLaterHeightInItsPlace(string first, float want)
        {
            string doc = "{ \"districts\": [ "
                + "{ \"district\": \"A\", \"fxMeshGuid\": \"1,2,3,4\", \"footprintMeshFlatHeight\": " + first + " }, "
                + "{ \"district\": \"B\", \"fxMeshGuid\": \"1,2,3,4\", \"footprintMeshFlatHeight\": 0.5 }, "
                + "{ \"district\": \"C\", \"fxMeshGuid\": \"1,2,3,4\", \"footprintMeshFlatHeight\": 0.7 } ] }";
            var got = DistrictInject.RegexDistricts(doc);
            Assert.Equal(new[] { "A", "B", "C" }, got.Select(d => d.district).ToArray());
            Assert.Equal(new[] { want, 0.5f, 0.7f }, got.Select(d => d.footprintMeshFlatHeight).ToArray());
        }

        // The pack tuning table's scale, which is read by pattern on the MAIN path (there is no JSON parse of it). The
        // last three rows are spellings the old pattern read and the first version of the one pattern dropped, rule and
        // all, without a warning.
        [Theory]
        [InlineData("2.5e-1", 0.25f)]    // the rule was dropped
        [InlineData("2.5E-1", 0.25f)]
        [InlineData("1e-3", 0.001f)]
        [InlineData("1.5e+1", 15f)]
        [InlineData("0.25", 0.25f)]
        [InlineData("+2", 2f)]
        [InlineData("2.5f", 2.5f)]
        [InlineData("\"2.5\"", 2.5f)]
        public void AUnitScaleRule_ReadsItsScaleInAnySpelling(string spelling, float want)
        {
            var got = UniversalInject.PackTuning.Parse(new List<KeyValuePair<string, string>>
                { new KeyValuePair<string, string>("p", "{\"unitScales\":[{\"match\":\"Tank\",\"scale\":" + spelling + "}]}") });
            Assert.Equal(want, Assert.Single(got.ScaleRules).scale);
            Assert.Empty(got.Warnings);
        }
    }
}
