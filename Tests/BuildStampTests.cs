using System.Text.RegularExpressions;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // BUILD IDENTITY — the guard for the csproj stanza that stamps the build date into the assembly.
    //
    // Written 2026-08-23 after an in-game F8 smoke PASS and a 43-test bake-suite PASS were both read as
    // verification of two fixes that were not in the deployed DLL — it was the previous day's build, and no output
    // anywhere named which build had produced it. `Plugin.BuildStamp` degrades to the DLL's file time, and then to
    // "unknown", so losing the csproj stanza would NOT break the build or any other test: it would just quietly
    // reintroduce the ambiguity. This is the only thing that notices.
    public class BuildStampTests
    {
        [Fact]
        public void BuildStamp_IsCompiledIn_NotAFallback()
        {
            var s = Plugin.BuildStamp;
            Assert.False(s == "unknown", "no build stamp at all — the csproj HafBuildStamp stanza is gone");
            Assert.DoesNotContain("file time", s);   // the fallback fired: the attribute is missing from the assembly
            Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC$"), s);
        }

        // The one-liner every report and the F8 panel print. It has to carry BOTH numbers: the version alone cannot
        // distinguish two builds of the same version, which is exactly the case that went wrong.
        [Fact]
        public void VersionLine_CarriesVersionAndBuildStamp()
        {
            var line = Plugin.VersionLine;
            Assert.Contains(Plugin.PluginVersion, line);
            Assert.Contains(Plugin.BuildStamp, line);
            Assert.StartsWith("HAF ", line);
        }

        // The stamp is read once and cached; a per-call re-read would be a file hit on a path some callers reach
        // often (the load report is rewritten every load).
        [Fact]
        public void BuildStamp_IsStableAcrossCalls()
        {
            Assert.Same(Plugin.BuildStamp, Plugin.BuildStamp);
        }
    }
}
