using System;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The load-seam seal (UniRegisterHook.Step) is the structural guarantee that one failed step on the game's
    // AnimationLoad postfix neither propagates into the game's own load path nor stops the remaining steps.
    // These pin that semantic: a refactor that drops the catch — or makes it rethrow — turns them red.
    // (Plugin.Log is null in the test host; the seal's error line uses Log?. so the swallow itself is what's tested.)
    public class LoadSeamTests
    {
        [Fact]
        public void Step_RunsTheAction()
        {
            int n = 0;
            UniRegisterHook.Step("run", () => n++);
            Assert.Equal(1, n);
        }

        [Fact]
        public void Step_SwallowsAThrow_AndLaterStepsStillRun()
        {
            bool laterStepRan = false;
            UniRegisterHook.Step("boom", () => throw new InvalidOperationException("drill"));
            UniRegisterHook.Step("after", () => laterStepRan = true);
            Assert.True(laterStepRan);
        }
    }
}
