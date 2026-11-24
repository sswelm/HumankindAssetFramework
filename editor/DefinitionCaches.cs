// Lists read out of the game's definition databases (the pawn names of the Pick lists, which pawn is a boat, the
// district names) are scanned once and kept, because the first scan loads every container asset. Until 2026-11-24
// they were kept for the whole editor session: a pawn definition made in the Unity editor reached the Model
// Factory's Pick list only after a restart, and a pawn whose profile was changed to Boat kept its old answer.
//
// A kept list is valid for ONE generation. The generation moves
//   - when Unity imports, deletes or moves a container asset (saving an edited database is an import), and
//   - on every Pick click, so a definition that is added but not saved yet shows as well.
// A consumer stores the generation it scanned under and scans again when the number has moved.
//
// The pure half (the counter and the path rule) compiles without Unity for the unit tests; the asset hook is editor-only.
using System;

static class DefinitionCaches
{
    // The words the scans hand to AssetDatabase.FindAssets, which matches them against asset names. The scans and
    // the path rule below read the same constants, so the two cannot drift apart.
    internal const string PawnContainers = "PresentationPawnDefinition";
    internal const string DistrictContainers = "ConstructibleCommonExtensionDefinition";

    internal static int Generation { get; private set; }
    internal static void Invalidate() { unchecked { Generation++; } }

    // Could this asset path be one of the containers a scan reads? An .asset with one of the words ANYWHERE in its
    // path, case-insensitively. That is wider than the scans on purpose (a folder of that name counts too): a path
    // taken wrongly costs one more scan, a path missed is a stale list.
    internal static bool IsContainer(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return false;
        if (!assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return false;
        return assetPath.IndexOf(PawnContainers, StringComparison.OrdinalIgnoreCase) >= 0
            || assetPath.IndexOf(DistrictContainers, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // One asset-database pass (imported, deleted, moved to, moved from): moves the generation when any path in it is
    // a container. Returns whether it did.
    internal static bool OnAssetsChanged(params string[][] pathLists)
    {
        if (pathLists == null) return false;
        foreach (var list in pathLists)
        {
            if (list == null) continue;
            foreach (var path in list)
                if (IsContainer(path)) { Invalidate(); return true; }
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
