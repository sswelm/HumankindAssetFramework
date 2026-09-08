// EditorRules.cs — PURE decision kernels extracted from the Unity-bound editor windows so the plugin test
// suite can compile and lock them (the GlbDisconnectedParts pattern: production compiles this file in the
// Unity editor assembly; Tests/HumankindAssetFramework.Tests.csproj compiles the same source Unity-free).
//
// The rule for what lives here: logic whose WRONGNESS is invisible at use time. The extraction predicate
// below was wrong for a month (review finding 2, 2026-09-07: it demanded a file only multi-material sources
// have, so single-material hand-edits were deleted on every bake) and nothing crashed, logged, or failed —
// the checkbox just didn't do its job. That failure class is exactly what a unit test catches and an eyeball
// doesn't. Keep these kernels free of UnityEngine/UnityEditor types and of file I/O: callers gather the
// facts, the kernel decides.
using System;

/// <summary>Bake-pipeline decisions (UniversalBaker calls these; BakerRulesTests locks them).</summary>
public static class BakerRules
{
    public enum ExtractionAction
    {
        UseExisting = 0,    // extraction on disk matches the current source — skip hygiene and glbconv
        ReExtract = 1,      // stale or missing — delete every derived artifact, run glbconv, restamp
        KeepProtected = 2,  // stale/unstamped BUT keepTexture is on and files exist — keep them, warn (hand-edit protection)
    }

    // The extraction-freshness decision for the animated path's glbconv step (review finding 2, 2026-09-07).
    // `extractedExists` = EITHER extraction shape is on disk (the multi-material MTL or the single
    // `<name>_albedo.png`) — the historic bug was requiring the MTL specifically, which a 1-material source
    // never has, making it permanently "stale": re-extracted every bake, hand-edits deleted, checkbox dead.
    // `stampMatches` = the `.src` stamp exists and equals the current source path + mtime.
    public static ExtractionAction DecideExtraction(bool extractedExists, bool stampMatches, bool keepTexture)
    {
        if (extractedExists && stampMatches) return ExtractionAction.UseExisting;
        if (keepTexture && extractedExists) return ExtractionAction.KeepProtected;
        return ExtractionAction.ReExtract;   // keepTexture cannot protect files that do not exist
    }
}

/// <summary>Natural name ordering — "Object_2" before "Object_10" (Model Workshop part list; NaturalOrderTests).</summary>
public static class NaturalOrder
{
    // "Object_12" -> "Object_": the name with its trailing digits removed. Sort by this first (ordinal,
    // case-insensitive at the call site), then by the trailing number as a NUMBER, then by the full name.
    public static string Prefix(string s)
    {
        int i = s.Length; while (i > 0 && char.IsDigit(s[i - 1])) i--;
        return s.Substring(0, i);
    }

    // "Object_12" -> 12. No trailing digits — or a run too long for long.TryParse (>19 digits) — sorts as -1,
    // i.e. before every numbered sibling of the same prefix.
    public static long Number(string s)
    {
        int i = s.Length; while (i > 0 && char.IsDigit(s[i - 1])) i--;
        return i < s.Length && long.TryParse(s.Substring(i), out long n) ? n : -1;
    }
}
