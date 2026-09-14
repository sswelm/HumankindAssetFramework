namespace HumankindAssetFramework
{
    // District decisions with no game dependency, extracted so the plugin test suite can lock them (coverage tier,
    // 2026-09-14). `EffectiveDensityBoost` reads the mesh's triangle count off a live FxMesh, so its auto-size branch
    // never executed in a unit test — and its one review-caught bug (the ≤1 opt-out being overridden) had to be found
    // by reading. Callers gather the facts, this decides.
    internal static class DistrictRules
    {
        internal const int VertsPerPpcUnit = 255;   // the engine's per-mesh row: 255 × PrimitivePerParticleCount primitives

        // The density boost to apply for a district mesh: the configured boost, raised to what the mesh actually
        // needs (ceil(tris / (255 · ppc))) when that is larger. `configBoost` ≤ 1 is the DOCUMENTED opt-out and
        // always wins (review P2, 2026-09-12: a disable the user set by hand must beat any heuristic). Unknown
        // triangle count or PPC (≤ 0) means "no evidence" — keep the config.
        internal static int NeededBoost(int ppc, long tris, int configBoost)
        {
            if (configBoost <= 1) return configBoost;
            if (ppc <= 0 || tris <= 0) return configBoost;
            long per = (long)VertsPerPpcUnit * ppc;
            int needed = (int)((tris + per - 1) / per);
            return needed > configBoost ? needed : configBoost;
        }
    }
}
