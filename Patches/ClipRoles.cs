using System;

namespace HumankindAssetFramework
{
    // THE CLIP-ROLE TABLE (Cut A, 2026-08-21). A model can carry up to nine animation roles — the primary clip plus the
    // eight state-driven ones. Until today each role was a hand-expanded FIELD FAMILY on ModelEntry (4 guid ints +
    // collection + animId + duration, ×9 ≈ 63 fields), and every "all roles" site — load, resolve, re-arm, preflight,
    // smoke, AnyStateRole — was a hand-written list of nine that had to be edited in lockstep. That shape produced two
    // shipped bugs (AnyStateRole gating on moveAnimId alone: critical-review #8; the `alc` component dropped from the
    // smoke test's wiring: the reason the 36-int test existed). Now: one enum, one binding class, one table per entry,
    // and every "all roles" site is a loop over ClipRoles.All. A tenth role is one enum value + one name/tag/key.
    //
    // Order matters and is pinned by tests: Primary is index 0; the JSON keys are the PACK CONTRACT the editor writes.
    public enum ClipRole { Primary = 0, Move, After, Attack, Combat, PreMove, IdleOverride, IdleAlt, IdleAlt2 }

    internal sealed class ClipBinding
    {
        public int a, b, c, d;          // Amplitude guid components of the baked ClipCollection; 0,0,0,0 = not authored
        public object coll;             // the loaded ClipCollection asset (InjectClipCollections)
        public int animId = -1;         // resolved animation id after AnimationManager.Apply (-1 = unresolved / dead)
        public float dur = 1f;          // clip duration in seconds (pose Time is normalized by it)
        public bool Authored => (a | b | c | d) != 0;
        public void Set(int a, int b, int c, int d) { this.a = a; this.b = b; this.c = c; this.d = d; }
    }

    internal static class ClipRoles
    {
        public const int Count = 9;
        public static readonly ClipRole[] All =
            { ClipRole.Primary, ClipRole.Move, ClipRole.After, ClipRole.Attack, ClipRole.Combat, ClipRole.PreMove, ClipRole.IdleOverride, ClipRole.IdleAlt, ClipRole.IdleAlt2 };
        // report / validator names (the smoke's "dead clip role" line, the preflight's Field), the log tag suffix the
        // collection is injected under, and the pack JSON key — all indexed by (int)role.
        static readonly string[] names    = { "primary", "move",     "after",     "attack",     "combat",     "preMove",     "idleOverride", "idleAlt",     "idleAlt2" };
        static readonly string[] tags     = { "",        ":move",    ":after",    ":attack",    ":combat",    ":premove",    ":idle",        ":idlealt",    ":idlealt2" };
        static readonly string[] jsonKeys = { "clip",    "clipMove", "clipAfter", "clipAttack", "clipCombat", "clipPreMove", "clipIdle",     "clipIdleAlt", "clipIdleAlt2" };
        public static string Name(ClipRole r) => names[(int)r];
        public static string Tag(ClipRole r) => tags[(int)r];
        public static string JsonKey(ClipRole r) => jsonKeys[(int)r];
        public static bool IsState(ClipRole r) => r != ClipRole.Primary;   // the eight state-driven roles (loaded only when animStateDriven)
        public static ClipBinding[] NewTable() { var t = new ClipBinding[Count]; for (int i = 0; i < Count; i++) t[i] = new ClipBinding(); return t; }
    }
}
