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
            public readonly Type Type;      // resolved via a GameBinding accessor (so the NAME lives only there) — null if the game type is missing
            public readonly string Name;    // display name for the report
            public readonly string[] Members;
            public Dep(Type type, string name, params string[] members) { Type = type; Name = name; Members = members ?? new string[0]; }
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
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())   // full name: scan loaded assemblies (Amplitude.* in-game)
                try { t = asm.GetType(name, false); if (t != null) return t; } catch { }
            if (name.IndexOf('.') < 0)   // SIMPLE (namespace-less) name — match by Type.Name, like AccessTools.TypeByName (the game uses a few short-name-only types)
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    try { foreach (var ty in asm.GetTypes()) if (ty.Name == name) return ty; }
                    catch (ReflectionTypeLoadException rtle) { foreach (var ty in rtle.Types) if (ty != null && ty.Name == name) return ty; }   // partial load: use what loaded
                    catch { }
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
        internal static Type Cached(string name, params string[] fallbacks)
        {
            if (_typeCache.TryGetValue(name, out var c) && c != null) return c;
            var t = ResolveType(name);
            if (t == null && fallbacks != null)
                foreach (var f in fallbacks) { t = ResolveType(f); if (t != null) break; }
            if (t != null) _typeCache[name] = t;
            return t;
        }

        // ---- audio ----
        internal static Type PresentationSubPawn => Cached("Amplitude.Mercury.Presentation.PresentationSubPawn");
        internal static Type AudioEmitter        => Cached("Amplitude.Wwise.Components.AudioEmitter");
        internal static Type AudioManager        => Cached("Amplitude.Wwise.Audio.AudioManager");
        internal static Type AkSoundEngine       => Cached("Amplitude.Wwise.Interop.AkSoundEngine", "AkSoundEngine");
        internal static Type AudioEventHandle    => Cached("Amplitude.Wwise.AudioEventHandle", "AudioEventHandle");
        // ---- combat / fight ----
        internal static Type PawnRangedFightSequence  => Cached("Amplitude.Mercury.Presentation.PawnRangedFightSequence");
        internal static Type PawnActionMeleeStartFight => Cached("Amplitude.Mercury.Presentation.PawnActionMeleeStartFight");
        internal static Type RotationPawnStateMachine  => Cached("Amplitude.Mercury.Presentation.RotationPawnStateMachine");
        internal static Type PawnActionRangedStartAttack => Cached("Amplitude.Mercury.Presentation.PawnActionRangedStartAttack");
        internal static Type AttackAnimationStateMachine => Cached("Amplitude.Mercury.Presentation.AttackAnimationStateMachine");
        internal static Type AbstractAnimationStateMachine => Cached("Amplitude.Mercury.Presentation.AbstractAnimationStateMachine");
        internal static Type PresentationArtilleryStrikeController => Cached("Amplitude.Mercury.Presentation.PresentationArtilleryStrikeController");
        internal static Type PresentationArtilleryStrike => Cached("Amplitude.Mercury.Presentation.PresentationArtilleryStrike");
        internal static Type UnitActionRangedFightSequence => Cached("Amplitude.Mercury.Presentation.UnitActionRangedFightSequence");
        internal static Type WorldPosition          => Cached("Amplitude.Mercury.WorldPosition");
        internal static Type WorldPositionExtensions => Cached("WorldPositionExtensions", "Amplitude.Mercury.WorldPositionExtensions");
        internal static Type UnitActionFaceEnemy      => Cached("Amplitude.Mercury.Presentation.UnitActionFaceEnemy");
        internal static Type MecanimEventInterpreter  => Cached("Amplitude.Mercury.Animation.MecanimEventInterpreter");
        internal static Type AlterationFireProjectile => Cached("Amplitude.Mercury.Animation.AlterationFireProjectile");
        // ---- presentation core ----
        internal static Type Presentation        => Cached("Amplitude.Mercury.Presentation.Presentation");
        internal static Type PresentationPawn    => Cached("Amplitude.Mercury.Presentation.PresentationPawn", "PresentationPawn");
        internal static Type PresentationUnit    => Cached("Amplitude.Mercury.Presentation.PresentationUnit", "PresentationUnit");
        internal static Type PresentationUnitHolder => Cached("Amplitude.Mercury.Presentation.PresentationUnitHolder");
        internal static Type PresentationDistrict => Cached("Amplitude.Mercury.Presentation.PresentationDistrict");
        internal static Type PresentationEntityFactoryController => Cached("Amplitude.Mercury.Presentation.PresentationEntityFactoryController", "PresentationEntityFactoryController");   // the army-walk root; read as a STATIC field off Presentation by respawn/facing/class-scan/census
        // ---- animation / pawn ----
        internal static Type PawnManager         => Cached("Amplitude.Mercury.Animation.PawnManager");
        internal static Type AnimationManager    => Cached("Amplitude.Mercury.Animation.AnimationManager");
        internal static Type PresentationPawnDefinitionAddOn => Cached("Amplitude.Mercury.Animation.PresentationPawnDefinitionAddOn");
        internal static Type ClipCollection      => Cached("Amplitude.Mercury.Animation.ClipCollection");
        internal static Type MeshCollection      => Cached("Amplitude.Mercury.Animation.MeshCollection");
        // ---- data / assets / graphics ----
        internal static Type AssetDatabase       => Cached("Amplitude.Framework.Asset.AssetDatabase");
        internal static Type Guid                => Cached("Amplitude.Framework.Guid");
        internal static Type FxEvolverMaterial   => Cached("Amplitude.Graphics.Fx.FxEvolverMaterial");
        internal static Type FxMesh              => Cached("Amplitude.Graphics.Fx.FxMesh");
        internal static Type ContentLayer        => Cached("Amplitude.Graphics.Fx.FxComponentMeshContentManager+ContentLayer");
        internal static Type PresentationPawnDefinition => Cached("Amplitude.Mercury.Data.World.PresentationPawnDefinition");
        internal static Type ProjectileAsset     => Cached("Amplitude.Mercury.Data.World.ProjectileAsset");
        // ---- district scoped-visual (mesh footprint + strategic footprint) — the newest + most reflection-heavy subsystem ----
        internal static Type FxEvolverMaterialLevelBuildElement  => Cached("Amplitude.Mercury.Terrain.Fx.FxEvolverMaterialLevelBuildElement");
        internal static Type FxEvolverMaterialLevelBuildSelector => Cached("Amplitude.Mercury.Terrain.Fx.FxEvolverMaterialLevelBuildSelector");
        internal static Type FxEvolverMaterialLevelBuildEmitter  => Cached("Amplitude.Mercury.Terrain.Fx.FxEvolverMaterialLevelBuildEmitter");   // our scoped selectors ARE emitters; levelBuildItems lives here
        internal static Type RenderFeatureSelector => Cached("Amplitude.Mercury.Fx.RenderFeatureSelector");
        internal static Type RenderFeatureProvider => Cached("Amplitude.Mercury.Fx.RenderFeatureProvider");
        internal static Type FxOutputLayer         => Cached("Amplitude.Graphics.Fx.FxOutputLayer");
        // ---- formation ----
        internal static Type PresentationFormationDefinition => Cached("Amplitude.Mercury.Data.PresentationFormationDefinition", "Amplitude.Mercury.Data.World.PresentationFormationDefinition", "PresentationFormationDefinition");
        internal static Type FormationHelper     => Cached("Amplitude.Mercury.Data.World.FormationHelper", "FormationHelper");
        internal static Type EntityFactoryControllerSettings => Cached("PresentationEntityFactoryControllerSettings");
        internal static Type GameObjectPoolController        => Cached("PresentationGameObjectPoolController");
        // ---- world state ----
        internal static Type Sandbox             => Cached("Amplitude.Mercury.Sandbox.Sandbox");

        // Resolve each dep. Unit-testable against known .NET types (the game types simply aren't present in a test host,
        // which exercises the "missing type" path).
        internal static List<DepResult> Validate(IEnumerable<Dep> deps)
        {
            var results = new List<DepResult>();
            foreach (var d in deps ?? Enumerable.Empty<Dep>())
            {
                var r = new DepResult { Type = d.Name, TypeFound = d.Type != null, MissingMembers = new List<string>() };
                if (d.Type != null)
                    foreach (var m in d.Members)
                        if (!MemberExists(d.Type, m)) r.MissingMembers.Add(m);
                results.Add(r);
            }
            return results;
        }

        // Validate the catalog and log a one-line OK, or a per-item WARNING naming exactly what's missing.
        // A4 — the game version the catalog was last VERIFIED against. Update after re-verifying on a new game build.
        // "" = not pinned. Gives the report context: a NOT FOUND on a matching version is a real regression; on a
        // different version it's most likely just an untested game update.
        internal const string VerifiedGameVersion = "1.30";   // verified 2026-08-02; update after re-checking on a new build

        static string VersionNote()
        {
            string ver = null;
            try { ver = UnityEngine.Application.version; } catch { }
            if (string.IsNullOrEmpty(ver)) return "";
            if (string.IsNullOrEmpty(VerifiedGameVersion)) return $" [game {ver}]";
            return ver == VerifiedGameVersion
                ? $" [game {ver}, verified]"
                : $" [game {ver} — UNTESTED; catalog verified against {VerifiedGameVersion}, so warnings are likely this update]";
        }

        // Last startup-report result, surfaced in the F8 window so a player SEES a break instead of it hiding in the log.
        // null = all resolved (or not yet run); non-null = a one-line "what's missing" summary. `HealthMissing` is the count.
        internal static string HealthSummary;
        internal static int HealthMissing;
        internal static readonly List<string> HealthDetail = new List<string>();

        internal static void ValidateAndLog(IEnumerable<Dep> deps)
        {
            try
            {
                var results = Validate(deps);
                int typesMissing = results.Count(r => !r.TypeFound);
                int membersMissing = results.Where(r => r.TypeFound).Sum(r => r.MissingMembers.Count);
                HealthDetail.Clear();
                if (typesMissing == 0 && membersMissing == 0)
                {
                    HealthSummary = null; HealthMissing = 0;
                    Plugin.Log.LogInfo($"[GameBinding] OK — {results.Count} game type(s) + their members all resolved.{VersionNote()}");
                    return;
                }
                HealthMissing = typesMissing + membersMissing;
                HealthSummary = $"{typesMissing} type(s) + {membersMissing} member(s) NOT FOUND (game update?) — features using them may misbehave.{VersionNote()}";
                Plugin.Log.LogWarning($"[GameBinding] {HealthSummary}");
                foreach (var r in results)
                {
                    if (!r.TypeFound) { HealthDetail.Add($"MISSING TYPE: {r.Type}"); Plugin.Log.LogWarning($"[GameBinding]   MISSING TYPE: {r.Type}"); }
                    else if (r.MissingMembers.Count > 0) { HealthDetail.Add($"{r.Type}: {string.Join(", ", r.MissingMembers)}"); Plugin.Log.LogWarning($"[GameBinding]   {r.Type}: missing member(s) {string.Join(", ", r.MissingMembers)}"); }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[GameBinding] validate: " + ex); }
        }

        // The catalog validates the ACCESSORS above (the one place each game-type name lives — A3). Every accessor is
        // listed so the startup report covers all of them; members are attributed to the type they're actually read off
        // (A1's PresentationUnit mistake was the lesson). A "missing" here is a real game-API change.
        internal static readonly Dep[] Catalog =
        {
            // audio
            new Dep(PresentationSubPawn, nameof(PresentationSubPawn), "AudioEmitter", "Transform", "PresentationPawnDescription"),
            new Dep(AudioEmitter, nameof(AudioEmitter), "AudioEntityGUID", "PostEvent"),
            new Dep(AudioManager, nameof(AudioManager), "PostEvent"),
            new Dep(AkSoundEngine, nameof(AkSoundEngine), "PostEvent", "StopAll"),
            // combat / fight
            new Dep(PawnRangedFightSequence, nameof(PawnRangedFightSequence)),
            new Dep(PawnActionMeleeStartFight, nameof(PawnActionMeleeStartFight)),
            new Dep(RotationPawnStateMachine, nameof(RotationPawnStateMachine), "StartDirectionToLook", "StepTurning"),
            new Dep(PawnActionRangedStartAttack, nameof(PawnActionRangedStartAttack), "OnReadyToStart", "isReadyToStart"),
            new Dep(AttackAnimationStateMachine, nameof(AttackAnimationStateMachine), "TeleportToSimpleAttack"),
            new Dep(AbstractAnimationStateMachine, nameof(AbstractAnimationStateMachine), "StepWaitingDelay", "ownerPawn"),
            new Dep(PresentationArtilleryStrikeController, nameof(PresentationArtilleryStrikeController), "ScheduleArtilleryStrikeProjectileLaunch", "ScheduleArtilleryStrikeHit"),
            new Dep(PresentationArtilleryStrike, nameof(PresentationArtilleryStrike), "TriggerArtilleryStrikeVisuals", "TriggerArtilleryStrikeFX", "PrepareArtilleryStrikeFX", "projectileData", "TargetTileIndex", "AttackerArmyGUID"),
            new Dep(UnitActionRangedFightSequence, nameof(UnitActionRangedFightSequence), "AddPawnRangedFightSequence"),
            new Dep(WorldPosition, nameof(WorldPosition)),
            new Dep(WorldPositionExtensions, nameof(WorldPositionExtensions), "ToVector3"),
            new Dep(UnitActionFaceEnemy, nameof(UnitActionFaceEnemy), "StartUnitAction", "actionScope", "AttackerBattleUnit"),
            new Dep(MecanimEventInterpreter, nameof(MecanimEventInterpreter)),
            new Dep(AlterationFireProjectile, nameof(AlterationFireProjectile)),
            // presentation core
            new Dep(Presentation, nameof(Presentation), "PresentationEntityFactoryController"),   // the STATIC army-walk root — read by respawn / facing / class-scan / census; a rename silently no-ops all four (was uncatalogued: critical-review #5)
            new Dep(PresentationEntityFactoryController, nameof(PresentationEntityFactoryController), "PresentationArmyEntities"),   // the next hop off that root — the army array every walk enumerates
            new Dep(PresentationPawn, nameof(PresentationPawn)),
            new Dep(PresentationUnit, nameof(PresentationUnit), "UnitDefinition", "GUID", "Pawns", "Formation"),
            new Dep(PresentationUnitHolder, nameof(PresentationUnitHolder)),
            new Dep(PresentationDistrict, nameof(PresentationDistrict), "presentationLevelBuildComponent", "ApplyGroundMaterialDefinition", "ConstructibleDefinitionName"),
            // animation / pawn
            new Dep(PawnManager, nameof(PawnManager), "Load", "AddPawnEntry", "gpuPawnDescriptorEntries"),
            new Dep(AnimationManager, nameof(AnimationManager), "Instance", "skeletonBufferSize", "FxComponentRenderer", "FxComponentMeshContentManager", "FXMeshLayerIndex"),
            new Dep(PresentationPawnDefinitionAddOn, nameof(PresentationPawnDefinitionAddOn), "FragmentEntries", "Skeleton", "MeshCollection"),
            new Dep(ClipCollection, nameof(ClipCollection)),
            new Dep(MeshCollection, nameof(MeshCollection)),
            // data / assets / graphics
            new Dep(AssetDatabase, nameof(AssetDatabase)),
            new Dep(Guid, nameof(Guid), "a", "b", "c", "d"),
            new Dep(FxEvolverMaterial, nameof(FxEvolverMaterial)),
            new Dep(FxMesh, nameof(FxMesh)),
            new Dep(ContentLayer, nameof(ContentLayer)),
            new Dep(PresentationPawnDefinition, nameof(PresentationPawnDefinition)),
            new Dep(ProjectileAsset, nameof(ProjectileAsset)),
            // district scoped-visual — the mesh strategic footprint (render-feature gate, B&W, flatten) + composed foliage
            new Dep(FxEvolverMaterialLevelBuildElement, nameof(FxEvolverMaterialLevelBuildElement), "renderFeatureSelector", "size", "outputLayer", "fxMesh", "WriteToGPUData"),
            new Dep(FxEvolverMaterialLevelBuildEmitter, nameof(FxEvolverMaterialLevelBuildEmitter), "levelBuildItems"),   // the scoped selector we walk for building elements + decals
            new Dep(FxEvolverMaterialLevelBuildSelector, nameof(FxEvolverMaterialLevelBuildSelector), "fxMaterialCacheEntries"),   // nested sub-selectors (pizza compose) — traversed via cache entries
            new Dep(RenderFeatureSelector, nameof(RenderFeatureSelector), "SelectionFlags0"),
            new Dep(RenderFeatureProvider, nameof(RenderFeatureProvider), "ComputeRenderState"),
            new Dep(FxOutputLayer, nameof(FxOutputLayer), "primitivePerParticleCount", "RenderOutputs"),
            // formation (EntityFactoryControllerSettings / GameObjectPoolController resolve by SIMPLE name — see ResolveType)
            new Dep(PresentationFormationDefinition, nameof(PresentationFormationDefinition)),
            new Dep(FormationHelper, nameof(FormationHelper)),
            new Dep(EntityFactoryControllerSettings, nameof(EntityFactoryControllerSettings)),
            new Dep(GameObjectPoolController, nameof(GameObjectPoolController)),
            // world
            new Dep(Sandbox, nameof(Sandbox), "MajorEmpires", "NumberOfMajorEmpires", "Timeline"),
            // NOTE: AudioEventHandle has an accessor but is NOT in this startup catalog — it's a genuine LATE-LOADER (the
            // Wwise event-handle type loads after the menu), so it can't resolve at this report's Awake time and would
            // false-positive. Its accessor re-resolves on first use (the audio catalog dump / audition) in a loaded game.
        };
    }
}
