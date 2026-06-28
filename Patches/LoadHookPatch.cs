using HarmonyLib;
using Amplitude.Mercury.Animation;

namespace ENCAccessProof
{
    // Fires when the engine loads + registers its animation content (game start / save load).
    // We cache the AnimationManager and run an access scan to prove we can read the live data.
    [HarmonyPatch(typeof(AnimationManager), "AnimationResolveDependencies")]
    internal static class AnimationManager_Load_Hook
    {
        private static bool scanned;

        // Scan-only now (caches the AnimationManager + fills the window). The REPOINT moved to
        // PreInstantiatePatch so it lands right before the pawn is built (no mid-load re-presentation).
        private static void Postfix(AnimationManager __instance)
        {
            Prober.AnimMgr = __instance;
            if (!scanned && Prober.RuntimeListCount(__instance) > 0) { Prober.RunScan(); scanned = true; }
        }
    }
}
