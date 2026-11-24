// Lists read out of the game's definition databases (the pawn names of the Pick lists, which pawn is a boat, the
// district names) are scanned once and kept, because the first scan loads every container asset. Until 2026-11-24
// they were kept for the whole editor session: a pawn definition made in the Unity editor reached the Model
// Factory's Pick list only after a restart, and a pawn whose profile was changed to Boat kept its old answer.
//
// A kept list is valid for ONE generation. The generation moves
//   - when Unity imports, deletes or moves an .asset (saving an edited database is an import), and
//   - on every Pick click, pre-bake check and Validate pack, so a definition that is added but not saved yet is
//     known as well.
// A consumer stores the generation it scanned under and scans again when the number has moved.
//
// The pure half (the counter and the path rule) compiles without Unity for the unit tests; the asset hook is editor-only.
using System;

static class DefinitionCaches
{
    // The words the scans hand to AssetDatabase.FindAssets.
    internal const string PawnContainers = "PresentationPawnDefinition";
    internal const string DistrictContainers = "ConstructibleCommonExtensionDefinition";

    internal static int Generation { get; private set; }
    internal static void Invalidate() { unchecked { Generation++; } }

    // Could this asset hold definitions a scan reads? ANY .asset, whatever its name. FindAssets matches its word
    // against the names of SUB-assets too (measured in the review of 2026-11-24: a file named UnitPresentation_Extra
    // holding an object named PresentationPawnDefinitionCollection is found), so no rule on the file name is safe.
    // The scans filter their hits with this same function, which makes "the hook sees every file a scan reads" true
    // by construction. The price: a bake's own .asset files move the generation too, and the next read scans again.
    internal static bool MayHoldDefinitions(string assetPath) =>
        !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase);

    // One asset-database pass (imported, deleted, moved to, moved from): moves the generation once when any path in
    // it may hold definitions. Returns whether it did.
    internal static bool OnAssetsChanged(params string[][] pathLists)
    {
        if (pathLists == null) return false;
        foreach (var list in pathLists)
        {
            if (list == null) continue;
            foreach (var path in list)
                if (MayHoldDefinitions(path)) { Invalidate(); return true; }
        }
        return false;
    }
}

#if UNITY_EDITOR
// Only the all-assets callback: it takes part in no importer's version, so adding this class reimports nothing.
sealed class DefinitionCacheInvalidator : UnityEditor.AssetPostprocessor
{
    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        DefinitionCaches.OnAssetsChanged(imported, deleted, moved, movedFrom);
    }
}
#endif
