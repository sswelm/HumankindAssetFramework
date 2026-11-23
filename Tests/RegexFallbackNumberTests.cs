using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // HOW A NUMBER IS SPELLED (2026-11-23). The regex readers - the fallback of ParseModels for a pack that will not
    // parse as JSON, the district fallback, and the pack tuning tables - each took a float with a character class of
    // their own. Two were wrong in the same quiet way: `-?[\d.eE+]+` has no `-` inside, so 1.5e-2 stopped at the `e`,
    // the parse of "1.5e" failed, and the dial came back as its DEFAULT (a scale rule was dropped); `-?[0-9.]+`, on
    // eleven dials, has no exponent at all, so 1.5e+2 read 1.5. Found by the review of PR #313, whose fraction test
    // passes because a plain decimal was always read right. Every reader now takes UniversalInject.JsonNumber.
    //
    // The oracle is not the code under test: the expected value is the runtime's own invariant parse of the spelling,
    // and the JSON parser's reading of the same entry.
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

        // THE WHOLE TOKEN OR NOTHING. A token that only starts like a number is not read as the digits it starts with:
        // the dial keeps its default (scale 1, turnRate 0). The two old patterns disagreed here - one gave the default,
        // the other, on turnRate's eleven, read 2.5 out of 2.5em.
        [Theory]
        [InlineData("2.5e")]        // an exponent with no digits
        [InlineData("2.5e+")]
        [InlineData("2.5em")]
        [InlineData("2.5.e3x")]
        [InlineData("1.2.3")]       // a whole token, and not a number: float.TryParse says no
        public void ATokenThatIsNotWhollyANumber_LeavesTheDialAtItsDefault(string spelling)
        {
            var e = Assert.Single(UniversalInject.ParseModels(
                "{ \"modId\": \"m\",, \"models\": [ { \"pawnDescription\": \"P\", \"scale\": " + spelling + ", \"turnRate\": " + spelling + ", \"brightness\": 2.5 } ] }"));
            Assert.Equal(1f, e.scale);
            Assert.Equal(0f, e.turnRate);
            Assert.Equal(2.5f, e.brightness);   // the entry itself was read
        }

        // A FALLBACK THAT INVENTS A DIFFERENT DEFAULT changes behaviour on the one day it runs. Every float dial left out
        // of the entry must come back from the fallback as the JSON parser leaves it (the schema's initializer).
        // idleAltInterval did not: 25 through the parser, 0 through the fallback, which switched the idle-alt
        // one-shots off for every entry of a pack that needed the fallback (the reviews of 10-12 to 11-23 carried it;
        // the Review-Backlog had marked it fixed on the strength of the parser's path alone).
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

        // The pack tuning table's scale, which is read by pattern on the MAIN path (there is no JSON parse of it).
        [Theory]
        [InlineData("2.5e-1", 0.25f)]    // the rule was dropped
        [InlineData("2.5E-1", 0.25f)]
        [InlineData("1e-3", 0.001f)]
        [InlineData("1.5e+1", 15f)]
        [InlineData("0.25", 0.25f)]
        public void AUnitScaleRule_ReadsItsScaleInAnySpelling(string spelling, float want)
        {
            var got = UniversalInject.PackTuning.Parse(new List<KeyValuePair<string, string>>
                { new KeyValuePair<string, string>("p", "{\"unitScales\":[{\"match\":\"Tank\",\"scale\":" + spelling + "}]}") });
            Assert.Equal(want, Assert.Single(got.ScaleRules).scale);
            Assert.Empty(got.Warnings);
        }
    }
}
