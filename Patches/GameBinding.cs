using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HumankindAssetFramework
{
    // Startup compatibility check for the game types/members HAF binds to by name via reflection. That binding is
    // inherently fragile — a game update can rename any of them and the feature then silently misbehaves. This does NOT
    // remove the reflection (which buys graceful per-hook degradation); it makes a break LOUD and LOCALISED: at load it
    // resolves a catalog of the types/members HAF depends on and logs exactly which (if any) are missing, so an API
    // change shows as a clear startup warning instead of a confusing runtime bug. The reflection equivalent of the
    // honest patched-count and the schema-parity guard — "make drift loud."
    //
    // Deliberately plain System.Reflection (no HarmonyLib/AccessTools): it needs no MonoMod, so the resolution logic is
    // unit-testable in a plain test host. Resolution matches how the rest of HAF reads members: a type by assembly scan,
    // a member (field/property/method, any access) by walking the base-type chain.
    //
    // Cautious first slice (A1): the catalog is small + confident, and this only ADDS a report — it doesn't yet reroute
    // the scattered GetMember-by-name calls through a cached binding. Grow the catalog over time.
    internal static class GameBinding
    {
        internal sealed class Dep
        {
            public readonly string Type;
            public readonly string[] Members;
            public Dep(string type, params string[] members) { Type = type; Members = members ?? new string[0]; }
        }

        internal struct DepResult { public string Type; public bool TypeFound; public List<string> MissingMembers; }

        // DeclaredOnly + a manual base-chain walk so an inherited NON-public member still counts (plain GetMember with
        // NonPublic only sees the level it's declared on).
        const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        static Type ResolveType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var t = Type.GetType(name, false);           // resolves mscorlib/qualified names directly
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())   // scan loaded assemblies (Amplitude.* in-game)
                try { t = asm.GetType(name, false); if (t != null) return t; } catch { }
            return null;
        }

        static bool MemberExists(Type t, string m)
        {
            if (string.IsNullOrEmpty(m)) return false;
            for (var cur = t; cur != null; cur = cur.BaseType)
                if (cur.GetMember(m, MemberFlags).Length > 0) return true;
            return false;
        }

        // ---- A3: cached type handles — the ONE place each game type NAME lives. Call sites use GameBinding.<Type> instead
        // of a scattered TypeByName("…"), so a rename is fixed here, not hunted across the codebase. Resolved once and
        // cached (non-null only, so a type not yet loaded at first touch re-resolves next call). `fallback` mirrors the
        // game's own short-name fallback used for a couple of these. Rolling this out one subsystem at a time; AUDIO first.
        static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();
        internal static Type Cached(string name, string fallback = null)
        {
            if (_typeCache.TryGetValue(name, out var c) && c != null) return c;
            var t = ResolveType(name) ?? (fallback != null ? ResolveType(fallback) : null);
            if (t != null) _typeCache[name] = t;
            return t;
        }

        internal static Type PresentationSubPawn => Cached("Amplitude.Mercury.Presentation.PresentationSubPawn");
        internal static Type AudioEmitter        => Cached("Amplitude.Wwise.Components.AudioEmitter");
        internal static Type AudioManager        => Cached("Amplitude.Wwise.Audio.AudioManager");
        internal static Type AkSoundEngine       => Cached("Amplitude.Wwise.Interop.AkSoundEngine", "AkSoundEngine");
        internal static Type AudioEventHandle    => Cached("Amplitude.Wwise.AudioEventHandle", "AudioEventHandle");

        // Resolve each dep. Unit-testable against known .NET types (the game types simply aren't present in a test host,
        // which exercises the "missing type" path).
        internal static List<DepResult> Validate(IEnumerable<Dep> deps)
        {
            var results = new List<DepResult>();
            foreach (var d in deps ?? Enumerable.Empty<Dep>())
            {
                var t = ResolveType(d.Type);
                var r = new DepResult { Type = d.Type, TypeFound = t != null, MissingMembers = new List<string>() };
                if (t != null)
                    foreach (var m in d.Members)
                        if (!MemberExists(t, m)) r.MissingMembers.Add(m);
                results.Add(r);
            }
            return results;
        }

        // Validate the catalog and log a one-line OK, or a per-item WARNING naming exactly what's missing.
        internal static void ValidateAndLog(IEnumerable<Dep> deps)
        {
            try
            {
                var results = Validate(deps);
                int typesMissing = results.Count(r => !r.TypeFound);
                int membersMissing = results.Where(r => r.TypeFound).Sum(r => r.MissingMembers.Count);
                if (typesMissing == 0 && membersMissing == 0)
                {
                    Plugin.Log.LogInfo($"[GameBinding] OK — {results.Count} game type(s) + their members all resolved.");
                    return;
                }
                Plugin.Log.LogWarning($"[GameBinding] {typesMissing} type(s) + {membersMissing} member(s) NOT FOUND (game update?) — features using them may misbehave:");
                foreach (var r in results)
                {
                    if (!r.TypeFound) Plugin.Log.LogWarning($"[GameBinding]   MISSING TYPE: {r.Type}");
                    else if (r.MissingMembers.Count > 0) Plugin.Log.LogWarning($"[GameBinding]   {r.Type}: missing member(s) {string.Join(", ", r.MissingMembers)}");
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[GameBinding] validate: " + ex); }
        }

        // The catalog — the game types/members HAF binds to on its hot paths. Each member here is read by a feature that
        // works today, so a "missing" is a real game-API change (or a catalog typo — the report catches both). Members are
        // attributed to the type they're actually read off (A1's PresentationUnit mistake was the lesson). A2 growth.
        internal static readonly Dep[] Catalog =
        {
            // ---- audio (engine sound / silence / audition) ----
            new Dep("Amplitude.Mercury.Presentation.PresentationSubPawn", "AudioEmitter", "Transform", "PresentationPawnDescription"),
            new Dep("Amplitude.Wwise.Components.AudioEmitter", "AudioEntityGUID", "PostEvent"),
            new Dep("Amplitude.Wwise.Audio.AudioEntityGUID", "IsValid", "guid"),
            new Dep("Amplitude.Wwise.Audio.AudioManager", "PostEvent"),
            new Dep("Amplitude.Wwise.Interop.AkSoundEngine", "PostEvent", "StopAll"),
            // ---- pawn registration / pose / injection ----
            new Dep("Amplitude.Mercury.Animation.PawnManager", "Load", "AddPawnEntry", "gpuPawnDescriptorEntries"),
            new Dep("Amplitude.Mercury.Animation.AnimationManager", "Instance", "skeletonBufferSize", "FxComponentRenderer", "FxComponentMeshContentManager", "FXMeshLayerIndex"),
            new Dep("Amplitude.Mercury.Animation.PresentationPawnDefinitionAddOn", "FragmentEntries", "Skeleton", "MeshCollection"),
            new Dep("Amplitude.Mercury.Animation.ClipCollection"),
            new Dep("Amplitude.Mercury.Animation.MeshCollection"),
            // ---- unit / combat ----
            new Dep("Amplitude.Mercury.Presentation.PresentationUnit", "UnitDefinition", "GUID", "Pawns", "Formation"),
            new Dep("Amplitude.Mercury.Presentation.PawnRangedFightSequence"),
            new Dep("Amplitude.Mercury.Presentation.UnitActionFaceEnemy", "StartUnitAction", "actionScope", "AttackerBattleUnit"),
            // ---- era / world state ----
            new Dep("Amplitude.Mercury.Sandbox.Sandbox", "MajorEmpires", "NumberOfMajorEmpires", "Timeline"),
            // ---- misc ----
            new Dep("Amplitude.Framework.Guid", "a", "b", "c", "d"),
        };
    }
}
