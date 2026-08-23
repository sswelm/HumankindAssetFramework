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
    internal static class TestBootstrap
    {
        [ModuleInitializer]
        internal static void Init()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
        }
    }
}
