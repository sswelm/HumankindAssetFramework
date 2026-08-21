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
        [ProcessLived("type cache")] static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();
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
        internal static Type LoadingScreen       => Cached("Amplitude.Mercury.LoadingScreen");   // static VisibilityChanged(bool): the end-of-loading seam (load-tier smoke)
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
        // ---- UI / input ----
        internal static Type UIInteractivityManager => Cached("Amplitude.UI.Interactables.UIInteractivityManager");   // F8 click-through fix: its static IsMouseCovered is the game's own "pointer is over UI" flag
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

        // ---- A5, the STRUCT BATCH (2026-08-19): game structs HAF reaches STRUCTURALLY — as array elements and
        // field values off the types above — so their NAMES never appear in code and a name-based catalog entry
        // would be a guess (false-positive risk). Each is DERIVED from its anchor member instead: the same path
        // the runtime code walks. A renamed anchor OR a renamed struct member both surface as one named line in
        // the report. These are the members whose silent drift previously meant torn skinning / dead offsets. ----
        internal static Type FieldOrPropType(Type t, string member)
        {
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                var f = cur.GetField(member, MemberFlags); if (f != null) return f.FieldType;
                var p = cur.GetProperty(member, MemberFlags); if (p != null) return p.PropertyType;
            }
            return null;
        }
        internal static Type ElementType(Type t) =>
            t == null ? null : t.IsArray ? t.GetElementType() : t.IsGenericType ? t.GetGenericArguments().FirstOrDefault() : null;
        static Type CachedDerived(string key, Func<Type> derive)
        {
            if (_typeCache.TryGetValue(key, out var c) && c != null) return c;
            Type t = null; try { t = derive(); } catch { }
            if (t != null) _typeCache[key] = t;
            return t;
        }
        internal static Type PawnEntry          => CachedDerived("PawnEntry",          () => ElementType(FieldOrPropType(PawnManager, "pawnEntries")));
        internal static Type PawnObjectSpace    => CachedDerived("PawnObjectSpace",    () => FieldOrPropType(PawnEntry, "ObjectSpace"));
        internal static Type PawnEntryPose      => CachedDerived("PawnEntryPose",      () => FieldOrPropType(PawnEntry, "Pose0"));
        internal static Type PawnBoneRotation   => CachedDerived("PawnBoneRotation",   () => FieldOrPropType(PawnEntry, "BoneRotation0"));
        internal static Type SkeletonAsset      => CachedDerived("SkeletonAsset",      () => FieldOrPropType(PresentationPawnDefinitionAddOn, "Skeleton"));
        internal static Type SkeletonBoneInfo   => CachedDerived("SkeletonBoneInfo",   () => ElementType(FieldOrPropType(SkeletonAsset, "BoneInfos")));
        internal static Type PresentationArmy   => CachedDerived("PresentationArmy",   () => ElementType(FieldOrPropType(PresentationEntityFactoryController, "PresentationArmyEntities")));
        internal static Type BattleReportController => CachedDerived("BattleReportController", () => FieldOrPropType(Presentation, "PresentationBattleReportController"));
        internal static Type PresentationBattle => CachedDerived("PresentationBattle", () => ElementType(FieldOrPropType(BattleReportController, "Battles")));
        // 2026-08-19 first-launch self-validation: the report flagged three members I had attributed to the WRONG
        // receivers (the A1 lesson, relearned live). Their true homes, derived along the paths the code walks:
        // OutputLayerInstance is on elements of AnimationManager.Content.OutputLayerEntries (the atlas dump +
        // retexture walk), and AttackerGroup/DefenderGroup are on the SIMULATION battle handed to
        // SimulationEvent_BattleStarted.Raise (the war-cry hook) — not the presentation battle.
        internal static Type MethodParamType(Type t, string method, int paramIndex)
        {
            for (var cur = t; cur != null; cur = cur.BaseType)
                foreach (var m in cur.GetMethods(MemberFlags))
                    if (m.Name == method && m.GetParameters().Length > paramIndex) return m.GetParameters()[paramIndex].ParameterType;
            return null;
        }
        internal static Type AnimationContent        => CachedDerived("AnimationContent",        () => FieldOrPropType(AnimationManager, "Content"));
        internal static Type ContentOutputLayerEntry => CachedDerived("ContentOutputLayerEntry", () => ElementType(FieldOrPropType(AnimationContent, "OutputLayerEntries")));
        internal static Type SimulationEventBattleStarted => Cached("Amplitude.Mercury.Simulation.SimulationEvent_BattleStarted", "SimulationEvent_BattleStarted");
        internal static Type SimulationBattle        => CachedDerived("SimulationBattle",        () => MethodParamType(SimulationEventBattleStarted, "Raise", 1));
        internal static Type SimulationBattleGroup   => CachedDerived("SimulationBattleGroup",   () => FieldOrPropType(SimulationBattle, "AttackerGroup"));

        // ---- A6, CLOSING THE CATALOG (2026-08-21): the review measured the net at ~76 of 88 bound member names — and
        // by receiver, ~20 game types HAF reaches with NO accessor at all. Every remaining by-name site now has its
        // receiver here. A5 rule kept: a type whose name never appears in code is DERIVED along the exact path the
        // runtime walks, never guessed. Verified headlessly by tools/bindcheck (which learned to evaluate these chains
        // the same day — it had silently mis-resolved every derived accessor since A5). ----
        // animation / skeleton / fragments / clips
        internal static Type FragmentEntry          => CachedDerived("FragmentEntry",          () => ElementType(FieldOrPropType(PresentationPawnDefinitionAddOn, "FragmentEntries")));   // hide-fragment, hand-prop, retexture, scaled-clone
        internal static Type SkinnedMeshInfo        => CachedDerived("SkinnedMeshInfo",        () => ElementType(FieldOrPropType(SkeletonAsset, "skinnedMeshInfos")));
        internal static Type FxMeshContent          => CachedDerived("FxMeshContent",          () => FieldOrPropType(SkinnedMeshInfo, "FxMeshContent"));   // the scaled-clone vertex rewrite + import-angle probe
        internal static Type FxComponentMeshContentManager => CachedDerived("FxComponentMeshContentManager", () => FieldOrPropType(AnimationManager, "FxComponentMeshContentManager"));
        internal static Type MeshVertexBuffer       => CachedDerived("MeshVertexBuffer",       () => FieldOrPropType(ContentLayer, "vertexBuffer"));   // the GPU vertex slice (crush / scale / prop writes)
        internal static Type ClipEntry              => CachedDerived("ClipEntry",              () => ElementType(FieldOrPropType(ClipCollection, "animationClipEntries")));
        internal static Type PawnDefinitionEntry    => CachedDerived("PawnDefinitionEntry",    () => ElementType(FieldOrPropType(PawnManager, "pawnDefinitions")));   // Resize Lab: descriptor -> unit definition
        internal static Type AnimationVariableNames => Cached("AnimationVariableNames", "Amplitude.Mercury.Presentation.AnimationVariableNames", "Amplitude.Mercury.Animation.AnimationVariableNames");   // battle hold-fire replay
        // unit / formation
        internal static Type PresentationUnitDefinition => CachedDerived("PresentationUnitDefinition", () => FieldOrPropType(PresentationUnit, "PresentationUnitDefinition"));
        internal static Type CoordinationValues     => CachedDerived("CoordinationValues",     () => FieldOrPropType(PresentationUnitDefinition, "CoordinationValues"));   // the dummyOffset struct
        internal static Type PresentationFormation  => CachedDerived("PresentationFormation",  () => FieldOrPropType(PresentationUnit, "Formation"));
        internal static Type FormationDummyData     => CachedDerived("FormationDummyData",     () => ElementType(FieldOrPropType(PresentationFormationDefinition, "Dummies")));   // the nested DummyData struct
        internal static Type PresentationEntityHolder => CachedDerived("PresentationEntityHolder", () => FieldOrPropType(PresentationUnit, "PresentationEntityHolder"));
        internal static Type Formation3D            => CachedDerived("Formation3D",            () => FieldOrPropType(EntityFactoryControllerSettings, "Formation3DPrefab"));   // the dummy-pool prefab we extend
        internal static Type Formation3DDummy       => CachedDerived("Formation3DDummy",       () => ElementType(FieldOrPropType(Formation3D, "Dummies")));
        internal static Type ArmyInfo               => CachedDerived("ArmyInfo",               () => FieldOrPropType(PresentationArmy, "ArmyInfo"));   // facing persistence keys on its SimulationEntityGUID
        internal static Type PresentationSquadron   => CachedDerived("PresentationSquadron",   () => ElementType(FieldOrPropType(PresentationEntityFactoryController, "presentationSquadronEntities")));   // AIR units (the sub-pawn walk)
        internal static Type PresentationAirPatrolController => CachedDerived("PresentationAirPatrolController", () => FieldOrPropType(Presentation, "PresentationAirPatrolController"));   // owns the air formations (a squadron's pawns live there)
        internal static Type PresentationAirFormation => CachedDerived("PresentationAirFormation", () => ElementType(FieldOrPropType(PresentationAirPatrolController, "presentationAirFormations")));
        internal static Type PresentationAirUnit    => CachedDerived("PresentationAirUnit",    () => ElementType(FieldOrPropType(PresentationAirFormation, "airFormationUnits")));
        // world / era (the Resize Lab's era anchor)
        internal static Type MajorEmpire            => CachedDerived("MajorEmpire",            () => ElementType(FieldOrPropType(Sandbox, "MajorEmpires")));
        internal static Type DepartmentOfScience    => CachedDerived("DepartmentOfScience",    () => FieldOrPropType(MajorEmpire, "DepartmentOfScience"));
        internal static Type Timeline               => CachedDerived("Timeline",               () => FieldOrPropType(Sandbox, "Timeline"));
        // district level-build chain + scoped-visual internals
        internal static Type PresentationLevelBuildComponent => CachedDerived("PresentationLevelBuildComponent", () => FieldOrPropType(PresentationDistrict, "presentationLevelBuildComponent"));
        internal static Type LevelBuildChannel      => CachedDerived("LevelBuildChannel",      () => ElementType(FieldOrPropType(PresentationLevelBuildComponent, "channels")));
        internal static Type LevelBuildItem         => CachedDerived("LevelBuildItem",         () => ElementType(FieldOrPropType(FxEvolverMaterialLevelBuildEmitter, "levelBuildItems")));
        internal static Type FxMaterialCache        => CachedDerived("FxMaterialCache",        () => FieldOrPropType(FxEvolverMaterialLevelBuildSelector, "fxMaterialCacheEntries"));
        internal static Type FxMaterialCacheEntry   => CachedDerived("FxMaterialCacheEntry",   () => ElementType(FieldOrPropType(FxMaterialCache, "Entries")));
        // The leaf's FxEvolverDescriptor property is DECLARED as the abstract base; what it returns at runtime (and what the
        // scoped-albedo + flatten code reads materialDataHasChanged / assetContentManagerTexture off) is the concrete
        // level-build-element descriptor singleton — bind the concrete type, base-chain walk finds the members wherever they sit.
        internal static Type FxEvolverDescriptorLevelBuildElement => Cached("Amplitude.Mercury.Terrain.Fx.FxEvolverDescriptorLevelBuildElement", "FxEvolverDescriptorLevelBuildElement");
        internal static Type FxAtlas                => CachedDerived("FxAtlas",                () => ElementType(FieldOrPropType(FxOutputLayer, "atlases")));   // the B&W footprint's private mask atlas
        internal static Type FxAtlasOutputEntry     => CachedDerived("FxAtlasOutputEntry",     () => ElementType(FieldOrPropType(FxAtlas, "outputEntries")));
        internal static Type AssetReferenceRepository => Cached("Amplitude.Mercury.Data.Presentation.AssetReferenceRepository");   // the */District/Main + wonder cell registration

        // ---- A7 (2026-08-21): the types behind the ~80 by-name literals the A6 sweep MISSED. A review measured the
        //      gap mechanically — `bindcheck` proves the catalog resolves, it cannot prove the catalog COVERS the code —
        //      and `tools/check-catalog.sh` now fails the gate on any by-name literal that isn't here. Several of these
        //      sit behind silent catches (FacingAngleOffset, IdleAudioEvent, CurrentTechnologicalEraIndex, BonesCount),
        //      where a game rename degrades a feature with no error at all: exactly what the catalog exists to name.
        internal static Type PresentationPawnDescription => Cached("Amplitude.Mercury.Data.World.PresentationPawnDescription");
        internal static Type PawnRotationTransformInfo => Cached("Amplitude.Mercury.Data.World.PresentationPawnDescription+RotationTransformInfo");
        internal static Type MecanimEvent        => Cached("Amplitude.Mercury.Animation.MecanimEvent");
        internal static Type ClipCurveEntry      => Cached("Amplitude.Mercury.Animation.ClipCurveEntry");
        internal static Type Databases           => Cached("Amplitude.Framework.Databases");
        internal static Type DatatableElementReference => Cached("Amplitude.Framework.DatatableElementReference");
        internal static Type SimulationUnitDefinition => Cached("Amplitude.Mercury.Data.Simulation.UnitDefinition");
        internal static Type BattleContender     => Cached("Amplitude.Mercury.Simulation.BattleContender");
        internal static Type BattleUnit          => Cached("Amplitude.Mercury.Simulation.BattleUnit");
        internal static Type GroundMaterialDefinition => Cached("Amplitude.Mercury.Terrain.GroundMaterialDefinition");
        internal static Type GroundMaterialAuthoringData => Cached("Amplitude.Mercury.Terrain.GroundMaterialAuthoringData");
        internal static Type GroundMaterialTextureData => Cached("Amplitude.Mercury.Terrain.GroundMaterialTextureData");
        internal static Type FxComponentTextureAtlasManager => Cached("Amplitude.Graphics.Fx.FxComponentTextureAtlasManager");
        internal static Type AbstractTextureAtlas => Cached("Amplitude.Graphics.Atlas.AbstractTextureAtlas");
        internal static Type GenericTextureAtlas => Cached("Amplitude.Graphics.Atlas.GenericTextureAtlas`1");
        internal static Type FxTextureAtlasStruct => Cached("Amplitude.Graphics.Fx.FxTextureAtlasStruct");
        internal static Type HgFxOutputLayerAndSubShaderProperty => Cached("Amplitude.Mercury.Fx.HgFx.HgFxOutputLayerAndSubShaderProperty");
        internal static Type FxLevelBuildDecalTextureEntryProperty => Cached("Amplitude.Mercury.Terrain.Fx.FxLevelBuildDecalTextureEntryProperty");
        // derived (A7): the GPU-side entry structs + the boxed handles the runtime reads through
        internal static Type GpuDescriptorEntry  => CachedDerived("GpuDescriptorEntry",  () => ElementType(FieldOrPropType(PawnManager, "gpuPawnDescriptorEntries")));
        internal static Type GpuFragmentEntry    => CachedDerived("GpuFragmentEntry",    () => ElementType(FieldOrPropType(PawnManager, "gpuPawnDescriptorFragmentEntries")));
        internal static Type GpuAnimationEntry   => CachedDerived("GpuAnimationEntry",   () => ElementType(FieldOrPropType(FieldOrPropType(AnimationManager, "gpuAnimationEntryBuffer"), "WriteContent")));
        internal static Type FxOneMeshStruct     => CachedDerived("FxOneMeshStruct",     () => ElementType(FieldOrPropType(ContentLayer, "HxFxOneMeshComputeBufferData")));
        // `vertexBuffer` is DECLARED as the abstract base, so WriteContent (on the generic subclass) is not reachable from it —
        // bindcheck caught that attribution of mine. Bind the runtime types directly: the generic buffer, and the Bones-format
        // vertex struct the resize path is guarded to touch ("only the Bones format stores Pos as raw floats").
        internal static Type ReadWriteBuffer1D   => Cached("Amplitude.Graphics.ReadWriteBuffer1D`1");
        internal static Type MeshVertexRecord    => Cached("Amplitude.Graphics.Fx.FxMeshContent+VertexDataPosUVNormalTangentBones");
        internal static Type AudioEntityGuid     => CachedDerived("AudioEntityGuid",     () => FieldOrPropType(AudioEmitter, "AudioEntityGUID"));
        internal static Type AudioEventHandleRef => CachedDerived("AudioEventHandleRef", () => FieldOrPropType(PresentationPawnDescription, "IdleAudioEvent"));
        internal static Type AtlasEntry          => CachedDerived("AtlasEntry",          () => ElementType(FieldOrPropType(AbstractTextureAtlas, "atlasEntries")));
        internal static Type DatabaseMatrix1D    => CachedDerived("DatabaseMatrix1D",    () => ElementType(FieldOrPropType(AssetReferenceRepository, "databaseMatrices1D")));
        internal static Type DatabaseMatrix2D    => CachedDerived("DatabaseMatrix2D",    () => ElementType(FieldOrPropType(AssetReferenceRepository, "databaseMatrices2D")));
        // ---- runtime module order (pack load order follows the game's own mod order — docs/Multi-Mod.md) ----
        internal static Type FrameworkServices   => Cached("Amplitude.Framework.Services");
        internal static Type RuntimeService      => Cached("Amplitude.Mercury.Runtime.IRuntimeService");

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
        [ProcessLived("rebuilt by Validate")] internal static readonly List<string> HealthDetail = new List<string>();

        static string SafeVersion() { try { return UnityEngine.Application.version ?? "?"; } catch { return "?"; } }

        // Machine-readable sibling of the startup log line: a full PASS/MISSING listing written to
        // BepInEx/config/haf_bindings_report.txt every launch, right next to haf_load_report.txt. A game update — or a
        // headless CI launch on a new build — then yields ONE diffable file that names exactly which bindings broke,
        // instead of hunting the log or waiting for a feature to misbehave in-game. This is the machine-readable half of
        // the reflection-drift net: as more raw Type.GetType/GetMethod sites migrate onto GameBinding accessors + this
        // Catalog, they each show up here for free (the first migration was GetRuntimeModules — FrameworkServices/RuntimeService).
        static void WriteReport(List<DepResult> results, int typesMissing, int membersMissing)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("HAF binding report  (regenerated every launch)");
                sb.AppendLine($"game={SafeVersion()}  verified={(string.IsNullOrEmpty(VerifiedGameVersion) ? "-" : VerifiedGameVersion)}  " +
                              $"resolved={results.Count - typesMissing}/{results.Count} type(s)  missing_types={typesMissing}  missing_members={membersMissing}");
                sb.AppendLine();
                foreach (var r in results)
                {
                    if (!r.TypeFound) sb.AppendLine($"[MISSING TYPE]    {r.Type}");
                    else if (r.MissingMembers.Count > 0) sb.AppendLine($"[MISSING MEMBER]  {r.Type}: {string.Join(", ", r.MissingMembers)}");
                    else sb.AppendLine($"[ok]              {r.Type}");
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "haf_bindings_report.txt"), sb.ToString());
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[GameBinding] binding report write failed: " + ex.Message); }
        }

        internal static void ValidateAndLog(IEnumerable<Dep> deps)
        {
            try
            {
                var results = Validate(deps);
                int typesMissing = results.Count(r => !r.TypeFound);
                int membersMissing = results.Where(r => r.TypeFound).Sum(r => r.MissingMembers.Count);
                HealthDetail.Clear();
                WriteReport(results, typesMissing, membersMissing);   // always write the full machine-readable report, pass or fail
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
            // A7 (2026-08-21) — the by-name surface the A6 sweep missed, now catalogued so bindcheck validates it and
            // tools/check-catalog.sh proves nothing new escapes. Grouped by the type the CALL SITE's receiver actually is.
            new Dep(PresentationPawnDescription, nameof(PresentationPawnDescription), "AudioEntityName", "IdleAudioEvent"),   // idle audio: behind a silent catch
            new Dep(PawnRotationTransformInfo, nameof(PawnRotationTransformInfo), "BoneName"),
            new Dep(MecanimEvent, nameof(MecanimEvent), "ParentNameToLaunchVFXPosition", "PositionToLaunchVFX"),   // muzzle VFX anchor
            new Dep(ClipCurveEntry, nameof(ClipCurveEntry), "EncodingFormat"),
            new Dep(Databases, nameof(Databases), "GetDatabase"),
            new Dep(DatatableElementReference, nameof(DatatableElementReference), "XmlSerializableElementName"),   // formation-by-size reads the unit's own formation through this
            new Dep(SimulationUnitDefinition, nameof(SimulationUnitDefinition), "SpawnType"),   // era-scale domain (land/naval/air)
            new Dep(BattleContender, nameof(BattleContender), "Units"),
            new Dep(BattleUnit, nameof(BattleUnit), "Unit", "TargetUnit"),
            new Dep(GroundMaterialDefinition, nameof(GroundMaterialDefinition), "GroundMaterialAuthoringData"),
            new Dep(GroundMaterialAuthoringData, nameof(GroundMaterialAuthoringData), "GroundMaterialLayer0", "GroundMaterialOneLayer", "Color"),
            new Dep(GroundMaterialTextureData, nameof(GroundMaterialTextureData), "AtlasElement"),
            new Dep(FxComponentTextureAtlasManager, nameof(FxComponentTextureAtlasManager), "AddNullAtlasInfo"),
            new Dep(AbstractTextureAtlas, nameof(AbstractTextureAtlas), "atlasEntries"),
            new Dep(GenericTextureAtlas, nameof(GenericTextureAtlas), "GetElementData"),
            new Dep(FxTextureAtlasStruct, nameof(FxTextureAtlasStruct), "Uvs"),
            new Dep(HgFxOutputLayerAndSubShaderProperty, nameof(HgFxOutputLayerAndSubShaderProperty), "LoadedOutputLayer", "OutputLayerIndex"),
            new Dep(FxLevelBuildDecalTextureEntryProperty, nameof(FxLevelBuildDecalTextureEntryProperty), "maskOption"),
            new Dep(GpuDescriptorEntry, nameof(GpuDescriptorEntry), "StartFragment", "FragmentCount"),
            new Dep(GpuFragmentEntry, nameof(GpuFragmentEntry), "EncodedMeshAndVisualParticleCountFxMeshIndex", "SkinnedMeshIndex", "FxOutputLayerIndex"),
            new Dep(GpuAnimationEntry, nameof(GpuAnimationEntry), "FrameCount", "Format", "StartPoseData"),
            new Dep(FxOneMeshStruct, nameof(FxOneMeshStruct), "StartVertex", "VertexCount", "StartIndex", "PrimitiveCount"),
            new Dep(MeshVertexRecord, nameof(MeshVertexRecord), "Pos"),
            new Dep(ReadWriteBuffer1D, nameof(ReadWriteBuffer1D), "WriteContent"),
            new Dep(AudioEntityGuid, nameof(AudioEntityGuid), "guid", "IsValid"),
            new Dep(AudioEventHandleRef, nameof(AudioEventHandleRef), "Value"),
            new Dep(AtlasEntry, nameof(AtlasEntry), "FullPath", "ShortPath", "Index"),
            new Dep(DatabaseMatrix1D, nameof(DatabaseMatrix1D), "CriteriaNames", "cells"),
            new Dep(DatabaseMatrix2D, nameof(DatabaseMatrix2D), "FirstCriteriaNames", "SecondCriteriaNames"),
            // A7 continued — members on types whose Dep entry above spans several lines (kept as their own rows for clarity)
            new Dep(PresentationDistrict, nameof(PresentationDistrict), "ApplyHexagonSculptingDefinition", "RenderMode", "constructibleDefinitionName"),
            new Dep(ContentLayer, nameof(ContentLayer), "currentIndexIndex", "currentMeshAddedCount", "currentVertexIndex"),
            new Dep(PresentationPawnDefinitionAddOn, nameof(PresentationPawnDefinitionAddOn), "definition"),
            new Dep(PresentationLevelBuildComponent, nameof(PresentationLevelBuildComponent), "FxManager"),
            new Dep(FxEvolverMaterialLevelBuildElement, nameof(FxEvolverMaterialLevelBuildElement), "ResolveDependencies"),
            // audio
            new Dep(PresentationSubPawn, nameof(PresentationSubPawn), "AudioEmitter", "Transform", "PresentationPawnDescription", "GetBoneTRS", "PresentationPawnDefinition", "pawnEntry", "FreeEventHashes"),   // pawnEntry: the cached struct the ghost-rotor source fix repairs
            new Dep(AudioEmitter, nameof(AudioEmitter), "AudioEntityGUID", "PostEvent", "EntityName"),
            new Dep(AudioManager, nameof(AudioManager), "PostEvent"),
            new Dep(LoadingScreen, nameof(LoadingScreen), "VisibilityChanged"),
            new Dep(AkSoundEngine, nameof(AkSoundEngine), "PostEvent", "StopAll"),
            new Dep(UIInteractivityManager, nameof(UIInteractivityManager), "IsMouseCovered", "SpecificUpdate"),
            // combat / fight
            new Dep(PawnRangedFightSequence, nameof(PawnRangedFightSequence), "InitializeCommon", "Shooter", "Targets"),   // the state-driven attack hook's target
            new Dep(PawnActionMeleeStartFight, nameof(PawnActionMeleeStartFight), "StartPairMeleeAttack"),   // the per-swing melee hook's target
            new Dep(RotationPawnStateMachine, nameof(RotationPawnStateMachine), "StartDirectionToLook", "StepTurning", "ownerPawn", "UseRotationAnimation", "rotationStart", "rotationEnd"),
            new Dep(PawnActionRangedStartAttack, nameof(PawnActionRangedStartAttack), "OnReadyToStart", "isReadyToStart", "pawn", "creationTime"),   // creationTime: base PresentationChoreographyAction (base-chain walk)
            new Dep(AttackAnimationStateMachine, nameof(AttackAnimationStateMachine), "TeleportToSimpleAttack"),
            new Dep(AbstractAnimationStateMachine, nameof(AbstractAnimationStateMachine), "StepWaitingDelay", "ownerPawn"),
            new Dep(PresentationArtilleryStrikeController, nameof(PresentationArtilleryStrikeController), "ScheduleArtilleryStrikeProjectileLaunch", "ScheduleArtilleryStrikeHit"),
            new Dep(PresentationArtilleryStrike, nameof(PresentationArtilleryStrike), "TriggerArtilleryStrikeVisuals", "TriggerArtilleryStrikeFX", "PrepareArtilleryStrikeFX", "projectileData", "TargetTileIndex", "AttackerArmyGUID"),
            new Dep(UnitActionRangedFightSequence, nameof(UnitActionRangedFightSequence), "AddPawnRangedFightSequence"),
            new Dep(WorldPosition, nameof(WorldPosition)),
            new Dep(WorldPositionExtensions, nameof(WorldPositionExtensions), "ToVector3"),
            new Dep(UnitActionFaceEnemy, nameof(UnitActionFaceEnemy), "StartUnitAction", "actionScope", "AttackerBattleUnit"),
            new Dep(MecanimEventInterpreter, nameof(MecanimEventInterpreter), "presentationSubPawn"),
            new Dep(AlterationFireProjectile, nameof(AlterationFireProjectile), "StartEvent"),   // the muzzle offset-stash hook's target
            // presentation core
            new Dep(Presentation, nameof(Presentation), "PresentationEntityFactoryController", "PresentationBattleReportController", "PresentationAirPatrolController"),   // the STATIC army-walk root — read by respawn / facing / class-scan / census; a rename silently no-ops all four (was uncatalogued: critical-review #5)
            new Dep(PresentationEntityFactoryController, nameof(PresentationEntityFactoryController), "PresentationArmyEntities", "presentationSquadronEntities"),   // the next hops off that root — the army array every walk enumerates, and the AIR units (squadrons) beside it
            new Dep(PresentationPawn, nameof(PresentationPawn), "Transform", "PresentationUnit",
                "SubPawns", "SubPawnCount", "IsTurning", "PlayAnimationState", "TriggerDeath", "InstantiatePawn", "rotationTransformInfos", "Dummy"),   // + battle hold-fire replay, death cue, pawn-spawn hook, turret census
            new Dep(PresentationUnit, nameof(PresentationUnit), "UnitDefinition", "GUID", "Pawns", "Formation",
                "PresentationUnitDefinition", "IsLoaded", "IsNaval", "IsAnyPawnMoving",
                "UpdatePawns", "FormationAngle", "PresentationEntityHolder", "InstantiatePawns"),   // + re-form / respawn, facing persistence, health-ratio spawn count, pawns hook
            new Dep(PresentationUnitHolder, nameof(PresentationUnitHolder), "PresentationUnit", "audioEmitter", "playRumbleAudioEvent"),
            new Dep(PresentationDistrict, nameof(PresentationDistrict), "presentationLevelBuildComponent", "ApplyGroundMaterialDefinition", "ConstructibleDefinitionName",
                "UpdateLevelBuild", "UpdateGroundMaterial", "UpdateHexagonSculpting", "mainLevelBuildComponantLayer", "visualAffinityName", "initialVisualAffinityName", "hexagonSculptingDefinitionIndex"),
            // animation / pawn
            new Dep(PawnManager, nameof(PawnManager), "Load", "AddPawnEntry", "gpuPawnDescriptorEntries",
                "Instance", "gpuPawnDescriptorFragmentEntries", "descriptorBufferDirty", "persistentFragmentEntryCount", "pawnDefinitions", "pawnEntries", "pawnCount"),
            new Dep(AnimationManager, nameof(AnimationManager), "Instance", "skeletonBufferSize", "FxComponentRenderer", "FxComponentMeshContentManager", "FXMeshLayerIndex",
                "AnimationLoad", "RegisterMeshCollection", "Apply", "loadedAnimationClipCollections", "GetAnimationId", "GetAnimationDuration", "GetPoseTRS",
                "gpuAnimationEntryBuffer", "gpuSkeletonEntriesBuffer", "gpuSkeletonBoneEntiesBuffer", "meshCollections"),
            new Dep(PresentationPawnDefinitionAddOn, nameof(PresentationPawnDefinitionAddOn), "FragmentEntries", "Skeleton", "MeshCollection",
                "Load", "Definition", "GetOrCreateAddOn", "PawnDefinitionId"),   // PawnDefinitionId: the descriptor seed that arms the wrong-skeleton net + the Resize/turn-ease maps (6 sites)
            new Dep(ClipCollection, nameof(ClipCollection), "animationClipEntries", "animationClipCurveEntries"),
            new Dep(MeshCollection, nameof(MeshCollection)),
            // data / assets / graphics
            new Dep(AssetDatabase, nameof(AssetDatabase)),
            new Dep(Guid, nameof(Guid), "a", "b", "c", "d", "Null"),
            new Dep(FxEvolverMaterial, nameof(FxEvolverMaterial), "NextDoublonAvoidanceIndex", "TryLoad"),
            new Dep(FxMesh, nameof(FxMesh), "Mesh", "importAngles"),   // importAngles: the hand-prop import-angle stamp (bindcheck re-homed it here from FxEvolverMaterial)
            new Dep(ContentLayer, nameof(ContentLayer),
                "LoadEncodingVertexAndBuffer", "baseVertexBufferSize", "baseIndexBufferSize", "maxMeshCount", "maxMeshTriangleCount",
                "vertexBuffer", "HxFxOneMeshComputeBufferData", "hxFxOneMeshComputeBuffer", "FindGuidAssociatedToIndex"),
            new Dep(PresentationPawnDefinition, nameof(PresentationPawnDefinition), "Projectile", "AnimationCapabilityProfile", "SubPawnDefinitions"),
            new Dep(ProjectileAsset, nameof(ProjectileAsset)),
            // district scoped-visual — the mesh strategic footprint (render-feature gate, B&W, flatten) + composed foliage
            new Dep(FxEvolverMaterialLevelBuildElement, nameof(FxEvolverMaterialLevelBuildElement), "renderFeatureSelector", "size", "outputLayer", "fxMesh", "WriteToGPUData",
                "meshIndex", "outputLayerIndex", "materialIndex", "textureIndex", "loadingStatus", "Load", "LoadIFN", "Name", "FxEvolverDescriptor", "OnEditionChange"),
            new Dep(FxEvolverMaterialLevelBuildEmitter, nameof(FxEvolverMaterialLevelBuildEmitter), "levelBuildItems"),   // the scoped selector we walk for building elements + decals
            new Dep(FxEvolverMaterialLevelBuildSelector, nameof(FxEvolverMaterialLevelBuildSelector), "fxMaterialCacheEntries",   // nested sub-selectors (pizza compose) — traversed via cache entries
                "pairs", "defaultMaterial", "invalidNameMaterial", "deferredName", "deferredTable"),
            new Dep(RenderFeatureSelector, nameof(RenderFeatureSelector), "SelectionFlags0", "FadingOptions"),
            new Dep(RenderFeatureProvider, nameof(RenderFeatureProvider), "ComputeRenderState"),
            new Dep(FxOutputLayer, nameof(FxOutputLayer), "primitivePerParticleCount", "RenderOutputs", "renderOutputs", "atlases", "Atlas", "atlas", "LayerIndex"),
            // formation (EntityFactoryControllerSettings / GameObjectPoolController resolve by SIMPLE name — see ResolveType)
            // ColumnsCountPerRow0/5 are the ENDPOINTS of the six per-row grids the builder writes (like Pose0/Pose8).
            new Dep(PresentationFormationDefinition, nameof(PresentationFormationDefinition), "Dummies", "ColumnsCountPerRow0", "ColumnsCountPerRow5", "Initialize", "LowSpecFormationDefinition"),
            new Dep(FormationHelper, nameof(FormationHelper), "InitializeFormation3DForDefinition"),
            new Dep(EntityFactoryControllerSettings, nameof(EntityFactoryControllerSettings), "Instance", "Formation3DPrefab"),
            new Dep(GameObjectPoolController, nameof(GameObjectPoolController), "DoStart"),
            // world
            new Dep(Sandbox, nameof(Sandbox), "MajorEmpires", "NumberOfMajorEmpires", "Timeline"),
            // runtime module order — the ordered active-mod list HAF sorts packs by (docs/Multi-Mod.md). First reflection
            // site migrated onto the catalog (was a raw Type.GetType in GetRuntimeModulesRaw); a rename now shows here.
            new Dep(FrameworkServices, nameof(FrameworkServices), "GetService"),
            new Dep(RuntimeService, nameof(RuntimeService), "GetRuntimeModules"),
            // THE STRUCT BATCH (A5, 2026-08-19) — derived types (see the accessors): the GPU-facing pawn structs the
            // pose seam writes every frame, the skeleton/bone structs the preflight + injection read, and the
            // army/battle walk the state sampler enumerates. A [MISSING TYPE] on a derived entry means its ANCHOR
            // member was renamed (the derivation chain broke); a [MISSING MEMBER] means the struct itself changed.
            // Pose0/Pose8 + BoneRotation0/BoneRotation3 are the ENDPOINTS of the slot ranges the code indexes.
            new Dep(PawnEntry, nameof(PawnEntry), "ObjectSpace", "HideFactor", "SkeletonId", "PawnDescriptorId", "Pose0", "Pose8", "BoneRotation0", "BoneRotation3"),
            new Dep(PawnObjectSpace, nameof(PawnObjectSpace), "Translation", "Rotation", "Scale"),
            new Dep(PawnEntryPose, nameof(PawnEntryPose), "AnimationId", "Time", "Weight"),
            new Dep(PawnBoneRotation, nameof(PawnBoneRotation), "Angle", "SkeletonBoneIndex", "AxisIndex"),
            new Dep(SkeletonAsset, nameof(SkeletonAsset), "BoneInfos", "SkeletonId", "skinnedMeshInfos", "GetFxMeshIndex", "GetBoneIndex", "LoadIFN", "loadingStatus", "BBoxMin", "BBoxMax"),
            new Dep(SkeletonBoneInfo, nameof(SkeletonBoneInfo), "Name", "Local", "BindPose", "ParentIndex"),
            new Dep(PresentationArmy, nameof(PresentationArmy), "PresentationUnit", "ArmyInfo", "IsLockedByBattle"),
            new Dep(BattleReportController, nameof(BattleReportController), "Battles"),
            new Dep(PresentationBattle, nameof(PresentationBattle), "AllUnits"),
            // the launch-flagged members, re-homed on their TRUE receivers (both derived — see the accessors)
            new Dep(ContentOutputLayerEntry, nameof(ContentOutputLayerEntry), "OutputLayerInstance"),
            new Dep(SimulationEventBattleStarted, nameof(SimulationEventBattleStarted), "Raise"),
            new Dep(SimulationBattle, nameof(SimulationBattle), "AttackerGroup", "DefenderGroup"),
            new Dep(SimulationBattleGroup, nameof(SimulationBattleGroup), "Contenders", "Contenders"),
            // A6 — CLOSING THE CATALOG (2026-08-21): the by-name sites that were still outside the drift net. Each member
            // is attributed to the receiver the code actually reads it off (derived receivers follow the walked path).
            new Dep(FragmentEntry, nameof(FragmentEntry), "meshCollection", "meshName", "boneName", "SlotIndex", "BoneIndex", "EncodedMeshAndVisualParticleCount", "FxOutputLayer", "fxOutputLayer", "Load"),
            new Dep(SkinnedMeshInfo, nameof(SkinnedMeshInfo), "MeshName", "FxMeshContent", "MeshIndex"),
            new Dep(FxMeshContent, nameof(FxMeshContent), "verticesBytes", "vertexCount", "verticesBytesCrc", "bboxMin", "bboxMax", "Guid", "ImportAngles"),
            new Dep(FxComponentMeshContentManager, nameof(FxComponentMeshContentManager), "layers", "Layers"),
            new Dep(MeshVertexBuffer, nameof(MeshVertexBuffer), "Apply", "Size"),   // WriteContent lives on the RUNTIME buffer subclass, not the declared field type — unreachable statically, read null-tolerant via GetMember
            new Dep(ClipEntry, nameof(ClipEntry), "UnityAnimationClip", "FrameCount", "BonesCount", "CurveIndex", "Looping"),
            new Dep(PawnDefinitionEntry, nameof(PawnDefinitionEntry), "PresentationUnitDefinition"),
            new Dep(AnimationVariableNames, nameof(AnimationVariableNames), "SimpleAttackState"),
            new Dep(PresentationUnitDefinition, nameof(PresentationUnitDefinition), "CoordinationValues", "PresentationFormationDefinition"),
            new Dep(CoordinationValues, nameof(CoordinationValues), "DummyOffsetPosition"),
            new Dep(PresentationFormation, nameof(PresentationFormation), "DummyCount"),
            new Dep(FormationDummyData, nameof(FormationDummyData), "Position", "CoordinatePerDirection"),
            new Dep(PresentationEntityHolder, nameof(PresentationEntityHolder), "GetHealthRatio"),
            new Dep(Formation3D, nameof(Formation3D), "Dummies"),
            new Dep(Formation3DDummy, nameof(Formation3DDummy), "Transform", "GameObject"),
            new Dep(ArmyInfo, nameof(ArmyInfo), "SimulationEntityGUID"),
            new Dep(PresentationSquadron, nameof(PresentationSquadron), "PresentationUnit"),
            new Dep(PresentationAirPatrolController, nameof(PresentationAirPatrolController), "presentationAirFormations"),
            new Dep(PresentationAirFormation, nameof(PresentationAirFormation), "airFormationUnits"),
            new Dep(PresentationAirUnit, nameof(PresentationAirUnit), "PresentationUnit", "MainPawn"),
            new Dep(MajorEmpire, nameof(MajorEmpire), "DepartmentOfScience"),
            new Dep(DepartmentOfScience, nameof(DepartmentOfScience), "GetTechnologicalEra", "CurrentTechnologicalEraIndex"),
            new Dep(Timeline, nameof(Timeline), "GetGlobalEraIndex"),
            new Dep(PresentationLevelBuildComponent, nameof(PresentationLevelBuildComponent), "channels", "RefreshChannel"),
            new Dep(LevelBuildChannel, nameof(LevelBuildChannel), "evolverMaterial", "EvolverMaterialGuid"),
            new Dep(LevelBuildItem, nameof(LevelBuildItem), "loadedEvolverMaterial"),
            new Dep(FxMaterialCache, nameof(FxMaterialCache), "Entries"),
            new Dep(FxMaterialCacheEntry, nameof(FxMaterialCacheEntry), "FxMaterial"),
            new Dep(FxEvolverDescriptorLevelBuildElement, nameof(FxEvolverDescriptorLevelBuildElement), "assetContentManagerTexture", "materialDataHasChanged", "EvolverMaterials", "AssetContentManagerMesh", "evolverMaterials", "GetInstance"),
            // (assetContentManagerTexture's AddNullAtlasInfo is on the RUNTIME manager subclass — called null-tolerant off GetType(), not catalogued)
            new Dep(FxAtlas, nameof(FxAtlas), "atlasEntries", "elementData", "outputEntries", "owner"),
            new Dep(FxAtlasOutputEntry, nameof(FxAtlasOutputEntry), "unityTextureRef"),
            new Dep(AssetReferenceRepository, nameof(AssetReferenceRepository), "Instance", "Loaded", "databaseMatrices1D"),
            // Deliberately NOT catalogued (diagnostics only, DistrictDebug-gated, every read null-tolerant): the RepoDump
            // matrix/criteria walk, the ground-material colour dump, Prober's Databases.GetDatabase. A rename there costs
            // a debug dump, not a feature.
            // NOTE: AudioEventHandle has an accessor but is NOT in this startup catalog — it's a genuine LATE-LOADER (the
            // Wwise event-handle type loads after the menu), so it can't resolve at this report's Awake time and would
            // false-positive. Its accessor re-resolves on first use (the audio catalog dump / audition) in a loaded game.
        };
    }
}
