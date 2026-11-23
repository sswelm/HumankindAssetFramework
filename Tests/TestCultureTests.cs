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
        static string Expected => TestBootstrap.Culture.Name;

        [Fact]
        public void TheTestThreadRunsUnderTheForcedCulture()
        {
            Assert.NotNull(TestBootstrap.Culture);
            Assert.Equal(Expected, CultureInfo.CurrentCulture.Name);
        }

        // A parse on a worker is as culture-sensitive as one on the test thread (the probe, the fuse and the readers
        // all use workers), so the culture has to be the domain's default, not one thread's setting.
        [Fact]
        public void ANewThreadAThreadPoolWorkerAndAParallelLoopRunUnderItToo()
        {
            string onThread = null;
            var t = new Thread(() => onThread = CultureInfo.CurrentCulture.Name);
            t.Start(); t.Join();
            Assert.Equal(Expected, onThread);

            Assert.Equal(Expected, Task.Run(() => CultureInfo.CurrentCulture.Name).GetAwaiter().GetResult());

            var seen = new string[64];
            Parallel.For(0, seen.Length, i => seen[i] = CultureInfo.CurrentCulture.Name);
            Assert.All(seen, name => Assert.Equal(Expected, name));
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
            Assert.Equal(string.IsNullOrWhiteSpace(asked) ? TestBootstrap.DefaultCultureName : asked.Trim(), Expected);
        }

        // THE GAME-SIDE DIALS THROUGH THE LAST-RESORT PARSER. The regex fallback reads every float dial of the registry
        // with its own TryParse (37 InvariantCulture sites in ParseModels on 2026-11-23), and until this date no test
        // gave any of them a fraction: with the culture dropped from all of them the suite stayed green on nl-NL, and in
        // the game a pack that needs the fallback would read scale 1.5 as its default. Every float field of the shared
        // schema gets its own fraction here and must come back the same from the fallback as from the JSON parser,
        // which is culture-invariant by construction. So does `position`, the one dial that is three numbers (the
        // fallback reads it through a helper of its own, which the float fields never reach).
        [Fact]
        public void EveryFloatDialOfTheSchema_ReadsAFractionTheSameThroughTheRegexFallback()
        {
            var floats = typeof(Haf.Schema.HafModelSchema).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(float)).OrderBy(f => f.Name, System.StringComparer.Ordinal).ToArray();
            Assert.True(floats.Length >= 30, "the schema's float dials were not found: " + floats.Length);

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
