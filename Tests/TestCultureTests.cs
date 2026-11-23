using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // THE SUITE'S CULTURE (2026-11-23). TestBootstrap forces one culture on every test thread; these tests hold that it
    // really is in force where tests run, and that the default one has the property it was chosen for. Without them a
    // bootstrap that stopped working (a runner that sets its own culture, a renamed initializer) would leave the suite
    // green and culture-blind again on a dot-decimal machine - which is the state this replaced.
    public class TestCultureTests
    {
        // THE INSTANCE, not the name: on the author's machine the culture the bootstrap forces and the machine's own
        // are both called nl-NL, so a comparison of names passed with the bootstrap removed (the review of PR #313).
        // The forced one is the runtime's read-only instance; a thread left to the machine holds another object.
        static CultureInfo Expected => TestBootstrap.Culture;

        [Fact]
        public void TheTestThreadRunsUnderTheForcedCulture()
        {
            Assert.NotNull(Expected);
            Assert.Same(Expected, CultureInfo.CurrentCulture);
            Assert.False(CultureInfo.CurrentCulture.UseUserOverride);   // the machine's own carries the user's regional settings
        }

        // A parse on a worker is as culture-sensitive as one on the test thread (the probe, the fuse and the readers
        // all use workers), so the culture has to be the domain's default, not one thread's setting.
        [Fact]
        public void ANewThreadAThreadPoolWorkerAndAParallelLoopRunUnderItToo()
        {
            CultureInfo onThread = null;
            var t = new Thread(() => onThread = CultureInfo.CurrentCulture);
            t.Start(); t.Join();
            Assert.Same(Expected, onThread);

            Assert.Same(Expected, Task.Run(() => CultureInfo.CurrentCulture).GetAwaiter().GetResult());

            var seen = new CultureInfo[64];
            Parallel.For(0, seen.Length, i => seen[i] = CultureInfo.CurrentCulture);
            Assert.All(seen, culture => Assert.Same(Expected, culture));
        }

        // What the default was chosen for, held against the culture's own data and not against this run's setting (so it
        // holds under HAF_TEST_CULTURE too): a decimal comma, and a '.' that is NOT a decimal point. Under such a
        // culture a number written "1.5" does not come back as 1.5 from a culture-default parse - the visible failure
        // a dropped InvariantCulture needs.
        [Fact]
        public void TheDefaultCultureIsCommaDecimal_SoACultureDefaultParseOfADotNumberGoesWrong()
        {
            var nl = CultureInfo.GetCultureInfo(TestBootstrap.DefaultCultureName);
            Assert.Equal(",", nl.NumberFormat.NumberDecimalSeparator);
            Assert.Equal("1,5", 1.5f.ToString(nl));
            Assert.False(float.TryParse("1.5", NumberStyles.Float, nl, out _));
            Assert.Equal(15f, float.Parse("1.5", nl));   // '.' groups thousands: the plausible wrong number
        }

        [Fact]
        public void WithoutTheOverrideTheForcedCultureIsTheDefault()
        {
            string asked = System.Environment.GetEnvironmentVariable("HAF_TEST_CULTURE");
            Assert.Equal(string.IsNullOrWhiteSpace(asked) ? TestBootstrap.DefaultCultureName : asked.Trim(), Expected.Name);
        }

        // Windows answers any well-formed tag with a made-up culture ("Unknown Locale", a decimal point): under such a
        // name the suite ran green and culture-blind (the review of PR #313: xx-YY and en-UK, 2,913 passed).
        [Theory]
        [InlineData("xx-YY")]      // well-formed, made up
        [InlineData("en-UK")]      // the plausible typo of en-GB
        [InlineData("nl_NL")]      // the POSIX spelling
        [InlineData("tr-tr")]      // listed as tr-TR
        [InlineData("klingon")]
        public void ANameTheRuntimeDoesNotListAsWrittenIsRefused(string name)
        {
            var e = Assert.Throws<System.ArgumentException>(() => TestBootstrap.Resolve(name));
            Assert.Contains("'" + name + "'", e.Message);
        }

        [Theory]
        [InlineData(null, "nl-NL")]
        [InlineData("", "nl-NL")]
        [InlineData("  ", "nl-NL")]
        [InlineData("tr-TR", "tr-TR")]
        [InlineData(" en-US ", "en-US")]
        public void AListedNameOrNoneResolves(string asked, string name)
        {
            var culture = TestBootstrap.Resolve(asked);
            Assert.Equal(name, culture.Name);
            Assert.True(culture.IsReadOnly);
            Assert.False(culture.UseUserOverride);
        }

        // THE GAME-SIDE DIALS THROUGH THE LAST-RESORT PARSER. The regex fallback reads every float dial of the registry
        // with a TryParse of its own, and until 2026-11-23 no test gave any of them a fraction: with the culture dropped
        // from all of them the suite stayed green on nl-NL, and in the game a pack that needs the fallback would read
        // scale 1.5 as its default. Every float the registry can set gets its own fraction here and must come back the
        // same from the fallback as from the JSON parser, which is culture-invariant by construction. So does
        // `position`, the one dial that is three numbers (the fallback reads it through a helper of its own).
        //
        // WHICH FLOATS: the fields of ModelEntry that the plugin's own key list (registryConfigKeys) lets a pack set -
        // the shared schema's, and the plugin-only `rotorSpinSpeed`, which the first version of this test missed by
        // reflecting over the schema class alone (the review of PR #313).
        //
        // NOT HELD HERE: how a number is spelled (an exponent, a sign). RegexFallbackNumberTests holds that, on the same
        // fields; the fractions below are plain decimals because the culture is this test's subject.
        [Fact]
        public void EveryFloatDialAPackCanSet_ReadsAFractionTheSameThroughTheRegexFallback()
        {
            var keysField = typeof(UniversalInject).GetField("registryConfigKeys", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(keysField != null, "UniversalInject.registryConfigKeys was not found (renamed? this test takes its fields from it)");
            var keys = (System.Collections.Generic.HashSet<string>)keysField.GetValue(null);
            var floats = typeof(ModelEntry).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(float) && keys.Contains(f.Name)).OrderBy(f => f.Name, System.StringComparer.Ordinal).ToArray();
            Assert.True(floats.Length >= 30, "the float dials were not found: " + floats.Length);
            Assert.Contains(floats, f => f.Name == "scale");            // a schema field
            Assert.Contains(floats, f => f.Name == "rotorSpinSpeed");   // a plugin-only one

            var body = new StringBuilder("\"resourceName\": \"Dials\", \"pawnDescription\": \"PAWN_A\"");
            var want = new float[floats.Length];
            for (int i = 0; i < floats.Length; i++)
            {
                want[i] = 0.125f + 0.25f * i;   // k/8: exact in binary, never whole, distinct per field
                body.Append(", \"").Append(floats[i].Name).Append("\": ").Append(want[i].ToString("R", CultureInfo.InvariantCulture));
            }
            body.Append(", \"position\": { \"x\": 0.5, \"y\": -1.25, \"z\": 2.75 }");
            string wellFormed = "{ \"modId\": \"m\", \"models\": [ { " + body + " } ] }";
            string broken = "{ \"modId\": \"m\",, \"models\": [ { " + body + " } ] }";   // a doubled comma: the regex path
            Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(broken));

            var viaObject = Assert.Single(UniversalInject.ParseModels(wellFormed));
            var viaRegex = Assert.Single(UniversalInject.ParseModels(broken));
            for (int i = 0; i < floats.Length; i++)
            {
                Assert.True(want[i] == (float)floats[i].GetValue(viaObject), floats[i].Name + " through the JSON parser: " + floats[i].GetValue(viaObject));
                Assert.True(want[i] == (float)floats[i].GetValue(viaRegex), floats[i].Name + " through the regex fallback: " + floats[i].GetValue(viaRegex) + ", wanted " + want[i].ToString(CultureInfo.InvariantCulture));
            }
            // component by component: Vector3's == is approximate, and its ToString needs a Unity module the suite has not
            Assert.Equal(new[] { 0.5f, -1.25f, 2.75f }, new[] { viaObject.position.x, viaObject.position.y, viaObject.position.z });
            Assert.Equal(new[] { 0.5f, -1.25f, 2.75f }, new[] { viaRegex.position.x, viaRegex.position.y, viaRegex.position.z });
        }
    }
}
