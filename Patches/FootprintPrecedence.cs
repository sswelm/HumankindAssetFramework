namespace HumankindAssetFramework
{
    // WHO DECIDES THE STRATEGIC FOOTPRINT — the per-district registry entry, or the global config?
    //
    // Both sources are kept deliberately (decision 2026-08-23): the registry is what a pack AUTHORS and ships, the
    // global config is what an operator TUNES live in the F8 window without a re-bake. Keeping both means there is a
    // precedence question, and until now the answer lived only in a comment on DistrictModel and in the shape of an
    // if/else buried in ResolveScopedFootprint. Nothing said which source had won for a given district, so an author
    // whose config edit "did nothing" had no way to discover that their own registry entry was overriding it — and
    // the config branch turned out to be unexercised by the shipped pack entirely, which is how a dead-default bug
    // sat in it unnoticed (see Review-Backlog, 2026-08-23).
    //
    // THE RULE, in one place: **an entry that turns footprintMesh ON claims the district and supplies ALL five
    // values; otherwise the global config governs all five.**
    //
    // It is deliberately ALL-OR-NOTHING rather than per-field. `footprintMesh` is a plain `bool`, so "the author
    // left this unset" and "the author set it to false" are the same value — a per-field merge would have to invent
    // the difference, and would silently treat every un-authored `false` as an override. Making that work means
    // nullable fields in Haf.Schema and a matching editor change; until someone needs it, one rule that can be
    // stated in a sentence beats a merge whose result nobody can predict.
    //
    // Pure by design (Decisions: "move the DECISION out of the method that does the I/O") — no config reads, no
    // engine access, no logging. The caller supplies both candidate sets and reports what comes back.
    // Unit-tested in Tests/FootprintPrecedenceTests.cs, both branches.
    internal enum FootprintSource
    {
        Entry,          // a registry entry with footprintMesh=true claimed this district
        GlobalConfig,   // no entry, or an entry that leaves footprintMesh off
    }

    internal struct FootprintValues
    {
        public bool mesh, bw, flat, hideDecal;
        public float flatHeight;
    }

    internal struct FootprintDecision
    {
        public FootprintValues Values;
        public FootprintSource Source;
        public string Reason;      // human-readable, for the one-shot log line and the load report
    }

    internal static class FootprintPrecedence
    {
        // The per-entry flatten height falls back to this when the entry authors a non-positive one. A 0 here is not
        // a legal height (SetFlatHeight clamps live edits to [0.02, 1]), so a 0 in the registry means "unwritten",
        // not "paper-flat".
        internal const float DefaultFlatHeight = 0.17f;

        // entryPresent: a registry entry exists for this district at all (its values are in `entry`).
        // The caller reads both candidate sets; this only decides between them.
        internal static FootprintDecision Resolve(bool entryPresent, FootprintValues entry, FootprintValues global, string district)
        {
            if (entryPresent && entry.mesh)
            {
                var v = entry;
                v.mesh = true;
                // A non-positive authored height means the field was never written — fall to the default rather than
                // to 0, which the live-tuning path defines as out of range.
                if (!(v.flatHeight > 0f)) v.flatHeight = DefaultFlatHeight;
                return new FootprintDecision
                {
                    Values = v,
                    Source = FootprintSource.Entry,
                    Reason = "registry entry for '" + district + "' sets footprintMesh=true — the entry supplies all "
                           + "five footprint values and the global DistrictFootprintMesh* config is IGNORED for this district",
                };
            }

            return new FootprintDecision
            {
                Values = global,
                Source = FootprintSource.GlobalConfig,
                Reason = entryPresent
                    ? "registry entry for '" + district + "' leaves footprintMesh=false — the global "
                      + "DistrictFootprintMesh* config governs this district"
                    : "no registry entry for '" + district + "' — the global DistrictFootprintMesh* config governs it",
            };
        }
    }
}
