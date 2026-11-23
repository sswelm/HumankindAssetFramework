using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using BepInEx.Logging;

// net471 has no ModuleInitializerAttribute (it arrived in .NET 5), but the C# compiler only needs the attribute to
// EXIST by this exact name and namespace — declaring it here makes [ModuleInitializer] work on this target.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace HumankindAssetFramework.Tests
{
    // ONE SEED FOR THE WHOLE ASSEMBLY, replacing a per-class habit that had already been forgotten once.
    //
    // The trigger (2026-08-23): CI failed on `MergeModelsTests.Undeclared_clash_keeps_first_loaded_and_is_a_conflict`
    // with a NullReferenceException, while the same commit's neighbours passed and the suite was green locally every
    // run. Cause: `Plugin.Log` is null with no BepInEx host, and FIVE test classes each carried their own
    // `if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");`. MergeModelsTests did not — so it passed
    // only when xunit happened to schedule one of those five first. A flake, and the worst kind: it trains you to
    // re-run CI rather than read it.
    //
    // This is the same move Plugin.Once made against sixteen hand-rolled `static bool xLogged` guards — the failure
    // mode of a per-site convention is forgetting a site, so replace it with one mechanism that cannot be forgotten.
    // A module initializer runs once, before any test in this assembly, whatever order xunit picks.
    //
    // The existing per-class seeds are left in place: they are `if (null)` guarded and therefore no-ops now, and
    // deleting five lines across five files buys nothing but churn.
    //
    // NOTE this is a HARNESS fix, not the whole answer. The product fix landed with it: MergeModels' conflict log
    // now uses `Plugin.Log?.`, because a pure function the unit suite covers must not require a host at all. If a
    // future pure path NREs on a null Log, guard THAT call — do not lean on this seed to hide it.
    //
    // ONE CULTURE FOR THE WHOLE ASSEMBLY TOO (2026-11-23), for the same reason: a per-test habit covers the tests that
    // remembered it. Until this date the suite ran under whatever culture the machine had. The author's machine is
    // nl-NL (decimal comma, '.' groups thousands), so a dropped InvariantCulture failed there; the CI runner is en-US,
    // where a culture-default parse of "1.5" is right by accident. Measured by the review of that day: of 15 production
    // files whose InvariantCulture -> CurrentCulture plant failed the suite on nl-NL, 12 passed it under en-US.
    // Now every test thread runs under DefaultCultureName on every machine, so CI fails where the author's push does.
    // Five tests switch the culture themselves and restore what they found; they are unaffected.
    //
    // HAF_TEST_CULTURE=<name> runs the suite under another culture (tr-TR for the dotless i, en-US to see what a
    // dot-decimal machine hides). The name must be one the runtime LISTS, spelled as it lists it: Windows makes up a
    // culture for any well-formed tag (xx-YY, en-UK: "Unknown Locale", a decimal point), so a mistyped name would
    // otherwise run the whole suite culture-blind and green. Any other name fails the run at once; the sentence
    // below is in the output at dotnet test's default verbosity.
    internal static class TestBootstrap
    {
        /// <summary>The culture every test runs under unless HAF_TEST_CULTURE names another: comma-decimal, the
        /// author's own. TestCultureTests holds both properties.</summary>
        internal const string DefaultCultureName = "nl-NL";

        /// <summary>The culture this run forced (the module initializer sets it before any test).</summary>
        internal static CultureInfo Culture { get; private set; }

        [ModuleInitializer]
        internal static void Init()
        {
            // The culture FIRST: touching Plugin runs its type initializer, and whatever a static of the plugin formats
            // or parses there must already see the forced culture (the second review of PR #313: with the seed first,
            // Plugin's initializer ran under the machine's own culture).
            Culture = Resolve(Environment.GetEnvironmentVariable("HAF_TEST_CULTURE"));
            CultureInfo.DefaultThreadCurrentCulture = Culture;   // every thread of this domain that sets none of its own

            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
        }

        /// <summary>The culture for an asked name (none: the default). Throws for a name the runtime does not list
        /// exactly as written; the instance is the runtime's own read-only one, without the user's regional overrides.</summary>
        internal static CultureInfo Resolve(string asked)
        {
            string name = string.IsNullOrWhiteSpace(asked) ? DefaultCultureName : asked.Trim();
            foreach (var known in CultureInfo.GetCultures(CultureTypes.AllCultures))
                if (known.Name.Length > 0 && string.Equals(known.Name, name, StringComparison.Ordinal))
                    return CultureInfo.GetCultureInfo(name);
            throw new ArgumentException("HAF_TEST_CULTURE='" + name + "' is not a culture this runtime lists (names are spelled like nl-NL, "
                + "tr-TR, en-US). The suite does not run under a made-up culture: unset the variable or correct the name.");
        }
    }
}
