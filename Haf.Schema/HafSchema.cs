namespace Haf.Schema
{
    // THE REGISTRY SCHEMA CONTRACT — one number, shared by the editor that WRITES packs and the plugin that READS
    // them, so the two sides can never disagree about what a version means.
    //
    // `docs/Multi-Mod.md` has stated this contract since the pack format shipped — "the HAF schema version this file
    // targets. Currently 1. Evolves ADDITIVELY — new keys are added, old files keep loading" — but until 2026-08-23
    // the number lived ONLY in that sentence and in the example JSON. Nothing in the code knew it. A pack's declared
    // `schemaVersion` was parsed on both paths, printed into `haf_load_report.txt`, and read back by nobody, so a
    // pack could claim any version at all and load identically. A version field with no reader is worse than no
    // field: it tells a pack author they are protected against a skew that is in fact entirely unchecked.
    public static class HafSchema
    {
        // The schema version THIS build implements. Bump it in the same commit that adds a key to HafModelSchema
        // which a pack author is expected to be able to rely on.
        public const int Version = 1;

        // The oldest pack schema this build can still read. ADDITIVE evolution means every older pack loads by
        // construction — a key it never wrote falls through to that field's initializer — so this stays 1 and the
        // lower bound never fires today. It exists as the lever for the day a field's MEANING changes rather than a
        // field merely being added: that break is the one the additive contract does not cover, and on that day this
        // constant moves and the packs below it get told plainly instead of silently misreading.
        public const int MinReadable = 1;

        // 0 = the pack declared no schemaVersion at all: a legacy bare `{ "models": [...] }`, or a hand-written pack.
        // NOT an error — it predates the field, and additive evolution means it still reads correctly.
        public const int Unversioned = 0;
    }
}
