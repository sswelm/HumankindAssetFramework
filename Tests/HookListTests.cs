using System;
using System.Linq;
using HarmonyLib;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The hook list in Plugin.cs is a hand-list: a [HarmonyPatch] class the plugin ships but never lists is never
    // patched, and nothing says so — the load line counts what was attempted. Forty-one classes were all listed on
    // 2026-10-11, by luck and habit; this pins it. (The project's own signature bug class, per docs/Testing.md: "a
    // hand-maintained list that must stay in step with a type".)
    public class HookListTests
    {
        static Type[] PatchClasses() =>
            typeof(Plugin).Assembly.GetTypes()
                .Where(t => t.IsClass && t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToArray();

        [Fact]
        public void EveryHarmonyPatchClassInThePluginIsInTheHookList()
        {
            var listed = new System.Collections.Generic.HashSet<Type>(Plugin.Hooks);
            var missing = PatchClasses().Where(t => !listed.Contains(t)).Select(t => t.FullName).ToArray();
            Assert.True(missing.Length == 0, "[HarmonyPatch] class(es) not in Plugin.Hooks — they are never patched: " + string.Join(", ", missing));
        }

        [Fact]
        public void EveryListedHookIsAHarmonyPatchClassAndListedOnce()
        {
            var patch = new System.Collections.Generic.HashSet<Type>(PatchClasses());
            var stray = Plugin.Hooks.Where(t => !patch.Contains(t)).Select(t => t.FullName).ToArray();
            Assert.True(stray.Length == 0, "listed but not a [HarmonyPatch] class (Harmony would patch nothing): " + string.Join(", ", stray));
            var dupes = Plugin.Hooks.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key.FullName).ToArray();
            Assert.True(dupes.Length == 0, "listed twice (patched twice): " + string.Join(", ", dupes));
        }

        [Fact]
        public void TheListIsNotEmptyAndMatchesTheAssemblyCount()
        {
            Assert.Equal(PatchClasses().Length, Plugin.Hooks.Length);
            Assert.True(Plugin.Hooks.Length >= 41, "the list shrank below the 41 hooks of 2026-10-11: " + Plugin.Hooks.Length);
        }
    }
}
