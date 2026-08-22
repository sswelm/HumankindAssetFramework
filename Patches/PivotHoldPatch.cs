using System;
using System.Reflection;
using HarmonyLib;

namespace HumankindAssetFramework
{
    // PIVOT IN PLACE — hold the ARMY's move start (2026-08-22, docs/Turn-Ease.md).
    //
    // The sim feeds PresentationArmy a growing positionHistory; PresentationArmy.UpdateWaitForReadyToMove (every frame
    // from OnUpdate) hands it to the unit as DoMoveAlongTiles when the unit is idle, or extends a running move. A prefix
    // that returns false while the unit is idle DEFERS the whole presentation move — holder and pawns together — for as
    // long as the eased yaw needs to face the next tile (UniversalInject.ShouldHoldArmyMove sets that facing as an aim
    // override). The history keeps growing meanwhile, so on release the unit gets the longer path and rolls through.
    //
    // Why not the pawn's own StartMoveAlongTilesIfPossible (the previous seam)? Holding only the pawns put them ~1.8 s
    // behind the unit holder: the army could no longer extend the path (CanModifyPawnsPath), the first chunk ran to
    // Finalize, and the unit stood 1.5 s at the intermediate tile before the next chunk — measured in the log as
    // "stood 1.5 s, pawn-unit gap 0.0 u". Six position-faking variants before that are in the docs' graveyard.
    [HarmonyPatch] internal static class Hk_PivotMoveHold
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PresentationArmy;
            var m = t != null ? AccessTools.Method(t, "UpdateWaitForReadyToMove") : null;
            if (m != null) Plugin.Log.LogInfo("[Pivot] hooked PresentationArmy.UpdateWaitForReadyToMove (pivot-in-place move-start hold)");
            else Plugin.Log.LogWarning("[Pivot] NOT found: PresentationArmy.UpdateWaitForReadyToMove — pivot in place is inert (units turn while rolling)");
            return m;
        }
        // false = skip the original this frame (OnUpdate calls it again next frame); any failure = vanilla, never a stuck army
        static bool Prefix(object __instance)
        {
            try { return !UniversalInject.ShouldHoldArmyMove(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning("[Pivot] army move prefix: " + e.Message); return true; }
        }
    }
}
