using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json.Linq;             // provided by the game (mod.io); robust registry parse where JsonUtility no-ops in the game runtime

namespace HumankindAssetFramework
{
    // All reflection member access funnels through here (Phase-2 co-location, 2026-08-02). GetMember / SetMember are
    // the ONE reader/writer the whole plugin uses; FormationOverride.Mem and FireProbe.Member are thin forwarders to
    // GetMember. Kept together so the caching/lookup strategy lives in one place and can't drift.
    internal static partial class UniversalInject
    {
        // Memoize the Property/Field resolution per (type,name). OnPawnAdded resolves ~a dozen members per pawn-add on the
        // game's hot path; caching the lookup (null included) turns those from member scans into dict hits. Semantics are
        // identical to the old inline AccessTools calls (property-first, CanWrite for writes, field fallback). Main-thread only.
        // ONE combined member cache (2026-08-01): GetMember/SetMember run per-pawn-per-frame on the game's hot path, and
        // every Amplitude member we touch is a FIELD — so the old property-THEN-field pair paid a wasted CachedProp dict
        // lookup on every call. Resolve property-or-field in a single dict hit (null cached too). Main-thread only.
        // `fieldCache`/`CachedField` stays for the polls' direct static-field lookups.
        // NOT main-thread only (review 2026-08-21). The "Main-thread only" note above was wrong: the SIM-thread hooks
        // read through here too — FireProbe.Member (ArtilleryStrikeStarted), OnBattleStarted's group/contender walk,
        // FacingPersist.OnSave/OnLoad — and the members they touch (StrikerUnit, AttackerGroup, Contenders, ...) are
        // touched by NO main-thread path, so their first use is a guaranteed INSERT racing the per-pawn-per-frame reads.
        // A plain Dictionary resized under a concurrent reader corrupts its bucket chain (FindEntry spins forever — a
        // hard freeze, no exception, no log line). ConcurrentDictionary: same null-caching semantics, lock-free reads
        // on the hot path, and a non-capturing factory so the miss path allocates no closure.
        [ProcessLived("reflection cache")] static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), MemberInfo> memberCache = new System.Collections.Concurrent.ConcurrentDictionary<(Type, string), MemberInfo>();
        [ProcessLived("reflection cache")] static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), FieldInfo> fieldCache = new System.Collections.Concurrent.ConcurrentDictionary<(Type, string), FieldInfo>();
        static readonly Func<(Type, string), MemberInfo> resolveMember = k => (MemberInfo)AccessTools.Property(k.Item1, k.Item2) ?? AccessTools.Field(k.Item1, k.Item2);
        static readonly Func<(Type, string), FieldInfo> resolveField = k => AccessTools.Field(k.Item1, k.Item2);
        static MemberInfo CachedMember(Type t, string name) => memberCache.GetOrAdd((t, name), resolveMember);
        static FieldInfo CachedField(Type t, string name) => fieldCache.GetOrAdd((t, name), resolveField);

        internal static object GetMember(object o, string name)
        {
            if (o == null) return null;
            var m = CachedMember(o.GetType(), name);
            try { if (m is PropertyInfo p) return p.GetValue(o); if (m is FieldInfo f) return f.GetValue(o); } catch { }
            return null;
        }

        internal static void SetMember(object o, string name, object val)
        {
            var m = CachedMember(o.GetType(), name);
            try { if (m is PropertyInfo p) { if (p.CanWrite) p.SetValue(o, val); } else if (m is FieldInfo f) f.SetValue(o, val); } catch { }
        }

        internal static object MakeGuid(int a, int b, int c, int d)
        { var gt = GameBinding.Guid; if (gt == null) return null; var g = Activator.CreateInstance(gt);
          gt.GetField("a", BF)?.SetValue(g, a); gt.GetField("b", BF)?.SetValue(g, b); gt.GetField("c", BF)?.SetValue(g, c); gt.GetField("d", BF)?.SetValue(g, d); return g; }
    }
}
