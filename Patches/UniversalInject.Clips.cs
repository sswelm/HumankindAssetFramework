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
    internal static partial class UniversalInject
    {
        // ---- animated models: register our ClipCollection + override the pawn's pose to play it ----

        static object LoadClipCollection(int a, int b, int c, int d, string tag)
        {
            try
            {
                var guid = MakeGuid(a, b, c, d);
                var ccType = GameBinding.ClipCollection;
                var adb = GameBinding.AssetDatabase;
                if (guid == null || ccType == null || adb == null) return null;
                var load = adb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => (m.Name == "LoadAsset" || m.Name == "TryLoadAsset") && m.IsGenericMethodDefinition && m.GetParameters().Length >= 1);
                var g = load?.MakeGenericMethod(ccType);
                if (g == null) { Plugin.Log.LogError($"[Uni] loadClip '{tag}': Amplitude LoadAsset/TryLoadAsset not resolved (game update?)"); return null; }
                var args = g.GetParameters().Length == 1 ? new[] { guid } : new[] { guid, null };
                var cc = g.Invoke(null, args);
                Plugin.Diag($"[Uni] loaded clipCollection '{tag}': " + ((cc as UnityEngine.Object)?.name ?? "NULL (rebuild mod?)"));
                return cc;
            }
            catch (Exception e) { Plugin.Log.LogError("[Uni] loadClip: " + e); return null; }
        }

        // Append each animated model's ClipCollection to AnimationManager.loadedAnimationClipCollections so Apply's
        // builder bakes its pose data + assigns it an animation id. Idempotent.
        static void InjectClipCollections(object animMgr)
        {
            try
            {
                var field = AccessTools.Field(animMgr.GetType(), "loadedAnimationClipCollections");
                var ccType = GameBinding.ClipCollection;
                if (field == null || ccType == null) { Plugin.Log.LogWarning("[Uni] clipCollection field/type not found"); return; }
                object InjectOne(object coll, int a, int b, int c2, int d2, string tag)
                {
                    if (a == 0 && b == 0 && c2 == 0 && d2 == 0) return coll;
                    if (coll == null) coll = LoadClipCollection(a, b, c2, d2, tag);
                    if (coll == null) return null;
                    try { AccessTools.Method(coll.GetType(), "Load", Type.EmptyTypes)?.Invoke(coll, null); } catch { }
                    var arr = field.GetValue(animMgr) as Array;
                    bool present = false;
                    if (arr != null) foreach (var c in arr) if (ReferenceEquals(c, coll)) { present = true; break; }
                    if (present) return coll;
                    int len = arr?.Length ?? 0;
                    var narr = Array.CreateInstance(ccType, len + 1);
                    if (arr != null) Array.Copy(arr, narr, len);
                    narr.SetValue(coll, len);
                    field.SetValue(animMgr, narr);
                    Plugin.Diag($"[Uni] injected clipCollection '{tag}' at [{len}]");
                    return coll;
                }
                foreach (var e in entries)
                {
                    e.clipColl = InjectOne(e.clipColl, e.ca, e.cb, e.cc, e.cd, e.resourceName);
                    if (e.animStateDriven)
                    {
                        e.moveClipColl = InjectOne(e.moveClipColl, e.mca, e.mcb, e.mcc, e.mcd, e.resourceName + ":move");
                        e.afterClipColl = InjectOne(e.afterClipColl, e.aca, e.acb, e.acc, e.acd, e.resourceName + ":after");
                        e.attackClipColl = InjectOne(e.attackClipColl, e.ata, e.atb, e.atc, e.atd, e.resourceName + ":attack");
                        e.combatClipColl = InjectOne(e.combatClipColl, e.cba, e.cbb, e.cbc, e.cbd, e.resourceName + ":combat");
                        e.preMoveClipColl = InjectOne(e.preMoveClipColl, e.pva, e.pvb, e.pvc, e.pvd, e.resourceName + ":premove");
                        e.idleClipColl = InjectOne(e.idleClipColl, e.iea, e.ieb, e.iec, e.ied, e.resourceName + ":idle");
                        e.idleAltClipColl = InjectOne(e.idleAltClipColl, e.ala, e.alb, e.alc, e.ald, e.resourceName + ":idlealt");
                        e.idleAlt2ClipColl = InjectOne(e.idleAlt2ClipColl, e.a2a, e.a2b, e.a2c, e.a2d, e.resourceName + ":idlealt2");
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[Uni] InjectClipCollections: " + ex); }
        }

        // After Apply built the animation buffer, resolve our clip's animation id via GetAnimationId(clip guid).
        static int ResolveAnimId(object animMgr, ModelEntry e)
        {
            int id = ResolveCollAnimId(animMgr, e.clipColl, e.resourceName, out float d);
            if (id >= 0 && d > 0.001f) e.animDuration = d;
            return id;
        }

        // Same resolution for ANY of an entry's ClipCollections (idle / move / after — Phase 2): index-0 clip guid ->
        // animation id + real duration (the pose hook normalizes Time by it so the clip plays at real speed).
        static int ResolveCollAnimId(object animMgr, object coll, string tag, out float dur)
        {
            dur = 1f;
            try
            {
                var clips = AccessTools.Field(coll.GetType(), "animationClipEntries")?.GetValue(coll) as Array;
                if (clips == null || clips.Length == 0) return -1;
                var clipGuid = GetMember(clips.GetValue(0), "UnityAnimationClip");
                if (clipGuid == null) return -1;
                var getId = AccessTools.Method(animMgr.GetType(), "GetAnimationId", new[] { clipGuid.GetType() });
                int id = getId != null ? Convert.ToInt32(getId.Invoke(animMgr, new[] { clipGuid })) : -1;
                if (id >= 0)   // capture the clip's real duration so the pose hook can normalize Time (play at real speed)
                {
                    var getDur = AccessTools.Method(animMgr.GetType(), "GetAnimationDuration", new[] { typeof(int) });
                    if (getDur != null)
                    {
                        float d = Convert.ToSingle(getDur.Invoke(animMgr, new object[] { id }));
                        if (d > 0.001f) { dur = d; Plugin.Diag($"[Uni] clip '{tag}' animId {id} duration {d:0.###}s"); }
                    }
                }
                return id;
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Uni] ResolveAnimId '{tag}': " + ex.Message); return -1; }
        }

        // RESIZE LAB (2026-07-28): pattern rules from the packs' "unitScales" arrays + the per-session
        // resolution to descriptor ids (session-scoped — descriptor indices change per load).
        struct ScaleRule { public string match; public float scale; public int era; }            // era 0 = derive from the unit's name
        static readonly List<ScaleRule> unitScaleRules = new List<ScaleRule>();
        struct UnitScaleInfo { public float scale; public int homeEra; public int domain; }   // rule product, the unit's own era, and its domain (UnitSpawnType) for the frontier lookup
        static readonly Dictionary<int, UnitScaleInfo> unitScaleByDesc = new Dictionary<int, UnitScaleInfo>();
        static readonly Dictionary<int, string> unitScaleNameByDesc = new Dictionary<int, string>();   // F8 readout only
        // TURN EASE for VANILLA units (docs/Turn-Ease.md): Formation Lab turn links resolved to descriptor ids at
        // addon load (same session-scoped resolution as the Resize rules above); read by the pose hook per pawn.
        static readonly Dictionary<int, float> vanillaTurnByDesc = new Dictionary<int, float>();
        static readonly HashSet<int> vanillaEaseLogged = new HashSet<int>();   // one "easing vanilla desc N" line per type per session
        static readonly Dictionary<string, int> addonDefIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // every addon seen this session: name -> PawnDefinitionId
        internal static readonly HashSet<int> descCensusLogged = new HashSet<int>();   // diag=1 census: one line per rendered descriptor per session

        // CATEGORY TURN EASE (2026-08-06, user design): global defaults per unit TYPE instead of one blanket
        // rate — Human, turretless Land vehicle, Land vehicle WITH a turret, Hover, Ship. Classification is by
        // CHARACTERISTIC, never by name (user rule): the base category derives from
        // PawnAnimationCapabilityProfileType at addon load (Boat=7 ship; InanimateObject=5/Custom=8 land;
        // every organic profile = human); HOVER is the game's own UnitTagAsAbility.Hover (index 32 — the
        // "ignores terrain" flag helicopters and hovercraft carry) read off the sim UnitDefinition by the slow
        // army scan; TURRET is the pawn's extra azimuth rotation transforms. Both refinements are LEARNED once
        // per descriptor via the system's standard position join. FIXED-WING planes and missiles
        // (Plane=14/Missile=15) are EXCLUDED entirely — the engine already flies them on natural curved paths
        // (user rule); only an explicit per-model rate can ease them.
        // Precedence: per-model turnRate > category rate > global `rate`.
        internal const int CatHuman = 0, CatLand = 1, CatTurret = 2, CatHover = 3, CatShip = 4, CatPlane = 5;
        internal const int HoverAbilityIndex = 32;   // UnitTagAsAbility.Hover (verified against Artillery=16, Interceptor=19 usages)
        internal static float catHumanRate, catLandRate, catTurretRate, catHoverRate, catShipRate;
        internal static bool AnyCatRate => catHumanRate > 0f || catLandRate > 0f || catTurretRate > 0f || catHoverRate > 0f || catShipRate > 0f;
        static readonly Dictionary<int, int> vanillaCatByDesc = new Dictionary<int, int>();   // desc -> BASE category (land, not yet hover/turret-refined)
        static readonly Dictionary<int, bool> descTurret = new Dictionary<int, bool>();       // desc -> has azimuth turret (learned)
        static readonly Dictionary<int, bool> descHover = new Dictionary<int, bool>();        // desc -> carries the Hover ability (learned)
        internal struct ClassSample { public UnityEngine.Vector3 pos; public bool turret; public bool hover; }
        internal static readonly List<ClassSample> classSamples = new List<ClassSample>();    // slow-scan output for the position join

        internal static int CategoryFromProfile(int prof)
        {
            switch (prof)
            {
                case 7: return CatShip;                    // Boat
                case 14: case 15: return CatPlane;         // Plane, Missile — EXCLUDED (natural flight paths)
                case 5: case 8: return CatLand;            // InanimateObject (vehicles/guns), Custom — may refine to hover/turret
                default: return prof >= 0 ? CatHuman : -1; // every organic profile (humans, mounts, chariots, animals)
            }
        }

        internal static float CategoryRate(int cat)
        {
            switch (cat)
            {
                case CatHuman: return catHumanRate;
                case CatLand: return catLandRate;
                case CatTurret: return catTurretRate;
                case CatHover: return catHoverRate;
                case CatShip: return catShipRate;
                default: return 0f;                        // CatPlane and unknown: no category easing
            }
        }

        // A land descriptor's LEARNED refinement: hover beats turret beats plain land. Unlearned descriptors
        // use the plain land rate until their first scan sample lands (a few seconds into a session).
        internal static int EffectiveCat(int descId, int baseCat)
        {
            if (baseCat != CatLand) return baseCat;
            if (descHover.TryGetValue(descId, out bool h) && h) return CatHover;
            if (descTurret.TryGetValue(descId, out bool t) && t) return CatTurret;
            return CatLand;
        }

        internal static float CategoryRateForDesc(int descId, int baseCat) => CategoryRate(EffectiveCat(descId, baseCat));

        // Position-join learn: called per land-descriptor pawn while any category rate is active.
        internal static void TryLearnClass(int descId, UnityEngine.Vector3 pos)
        {
            if (descHover.ContainsKey(descId)) return;   // hover + turret always learned together
            for (int i = 0; i < classSamples.Count; i++)
            {
                var d = classSamples[i].pos - pos; d.y = 0f;
                if (d.sqrMagnitude < 2f * 2f)
                {
                    descHover[descId] = classSamples[i].hover;
                    descTurret[descId] = classSamples[i].turret;
                    Plugin.Log.LogInfo($"[TurnEase] desc {descId} classified: {(classSamples[i].hover ? "HOVER (ignores terrain)" : classSamples[i].turret ? "land vehicle WITH turret" : "land vehicle without turret")}");
                    return;
                }
            }
        }

        // Does `name` (a pawn definition or simulation unit name) belong to the turn link whose core is `core`?
        // Plain contains-either-way first; then the CULTURE-VARIANT relaxation: a link on the COMMON family
        // unit also covers the emblematic variants — the census showed the player's howitzers rendering as
        // 'Era5_ZuluKingdom_Common_SiegeHowitzers_01' while the link (naturally) targeted the Common unit.
        // A link whose core names a specific culture has no "_Common_" and stays culture-exact.
        internal static bool TurnLinkMatches(string name, string core)
        {
            if (name.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0 ||
                core.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            int ci = core.IndexOf("_Common_", StringComparison.OrdinalIgnoreCase);
            if (ci < 0) return false;
            string era = core.Substring(0, ci);                        // "Era5" ("" tolerated for odd names)
            string tail = core.Substring(ci + "_Common_".Length);      // "SiegeHowitzers"
            if (tail.Length < 4) return false;                         // too short to be a safe family token
            return name.IndexOf(tail, StringComparison.OrdinalIgnoreCase) >= 0 &&
                   (era.Length == 0 || name.IndexOf(era, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Match the Formation Lab turn links against every addon recorded so far. Called from the addon-load
        // hook (new addon) AND from FormationOverride right after the registry parses (addons that loaded
        // first) — whichever side arrives last completes the mapping.
        internal static void SweepTurnLinks()
        {
            if (FormationOverride.TurnRateByUnit.Count == 0 || addonDefIds.Count == 0) return;
            foreach (var ad in addonDefIds)
            {
                if (vanillaTurnByDesc.ContainsKey(ad.Value)) continue;
                foreach (var kv in FormationOverride.TurnRateByUnit)
                    if (TurnLinkMatches(ad.Key, kv.Key))
                    {
                        vanillaTurnByDesc[ad.Value] = kv.Value;
                        Plugin.Log.LogInfo($"[TurnEase] vanilla '{ad.Key}' -> desc {ad.Value} rate {kv.Value} deg/s (Formation Lab link '{kv.Key}')");
                        break;
                    }
            }
        }
        static readonly Dictionary<int, float[]> eraGridRows = new Dictionary<int, float[]>();  // Global Era Lab: unit era -> modifier per CURRENT era
        static HashSet<string> unitScaleLogged;
        static readonly HashSet<int> vanillaScaledLogged = new HashSet<int>();

        // RESIZE — CLOSED WITH INSTRUCTION-LEVEL PROOF (2026-07-29, the shader dig): the entire render
        // pipeline was disassembled from AssetBundles/InstancingAndFx (AmpliAnimation compute kernels +
        // the Amplitude/ParticleSkinnedMeshRender draw shader, all 128 D3D11 vertex variants):
        //   1. CSAnimateFirstPass writes each bone's animated TRS with Scale HARDCODED to 1.0
        //      (`mov r3.y, l(1.000000)` before the store) — Local.Scale only spreads pose translations;
        //   2. CSAnimateSecondPass composes the chain and emits entry.Scale = 1/IBP.Scale x ObjectSpace.Scale;
        //   3. the draw VS multiplies ONLY the bind-pose translation by entry.Scale (part placement) and
        //      transforms vertex positions by pure rotation+translation — NO instruction anywhere multiplies
        //      geometry by a runtime scale; IBP.Scale is never even read by the draw.
        // Geometry size lives exclusively in the baked vertex buffer. Every runtime transform lever is
        // structurally dead (v1 ObjectSpace, v2 root Local, v3 IBP+BindPose — v3's two legs cancel exactly
        // in the VS: entry.Scale x IBP.T = (1/2s) x 2T).
        // Shader-dump toolchain: tools/ShaderDump (bundle -> AssetsTools.NET -> D3DDisassemble).
        //
        // MESH-SCALE ENGINE v1 (2026-07-29, the same shader read turned into the FIX): since size lives in
        // vertex data, scale the vertex data. The pawn layer's vertex format is VertexDataPosUVNormalTangentBones
        // — Pos is RAW FLOATS at offset 0 (t7 stride 36 in the draw VS; only the static formats quantize
        // positions) — and the layer's CPU WriteContent stays alive and is what every re-upload sends (never
        // released; layer dirty-repaints are self-healing for our writes). Two halves, both shader-proven:
        //   GEOMETRY (once per descriptor): descriptor fragments -> packed field low 24 bits = the mesh's
        //     StartIndex -> match in the layer's FxOneMeshStruct table -> Pos *= s over [StartVertex,
        //     +VertexCount) -> Apply(); uniform scale keeps normals/tangents valid. Mesh + descriptor BBoxes
        //     scaled for culling.
        //   PLACEMENT (per pawn per frame — the pawn buffer is immediate-mode): ObjectSpace.Scale *= s; the
        //     second pass multiplies bone world positions by it and the VS scales bind offsets by it
        //     (entry.Scale = ObjectSpace.Scale / IBP.Scale), so part spacing follows the grown geometry.
        // RATIO-BASED, so the size can CHANGE while the game runs (era anchoring): every mesh remembers the factor
        // currently baked into its vertices, and a new target applies only the DIFFERENCE (target/applied). That
        // makes re-scaling idempotent and reversible instead of compounding. Both maps are keyed by data that
        // outlives a session reset in the same way the buffers do — the Fx content buffers persist across
        // save/load while descriptor ids re-resolve, so clearing the mesh map would double-scale.
        // The mesh record is SELF-VERIFYING rather than assumed: it stores the factor AND the first vertex as we
        // left it. If the engine reloads its Fx content (menu round trip, streaming), that vertex comes back
        // vanilla and the probe no longer matches — we then know the buffer is fresh and re-scale from 1 instead
        // of trusting a stale bookkeeping entry. That closes both failure modes at once: double-scaling a buffer
        // that persisted, and silently under-scaling one that was rebuilt.
        struct MeshScale { public float factor; public UnityEngine.Vector3 probe; }
        static readonly Dictionary<long, MeshScale> meshApplied = new Dictionary<long, MeshScale>();   // (layer<<32)|meshIndex
        static readonly Dictionary<int, float> descApplied = new Dictionary<int, float>();             // descriptor -> current target (session-scoped)

        // ── THE WORLD'S ERA (2026-07-29) ─────────────────────────────────────────────────────────────────────────
        // We want "how advanced is the world", and the obvious API is the wrong one: Timeline.GetGlobalEraIndex()
        // is a THRESHOLD computation over the SUM of all empires' researched techs, so it lags the frontier — a
        // late game showed index 5 (Industrial) while empires were already fielding era-6 ships, which made an
        // ancient hull look wrongly large next to them (user: "this is the end turn ... feels wrong").
        //
        // So the anchor is the MAX era across all major empires: DepartmentOfScience.GetTechnologicalEra() (which
        // applies that empire's TechnologicalEraOffset and clamps), falling back to the public
        // CurrentTechnologicalEraIndex field, and finally to the aggregate Timeline index if the empire walk fails
        // (e.g. before the sandbox exists). Reflection throughout: Sandbox, MajorEmpire and DepartmentOfScience are
        // all internal.
        //
        // Polled rather than event-hooked: the era is derived state, not a notification, and a couple of seconds of
        // lag is invisible. When it moves, the per-frame pawn path picks up the new target and the ratio machinery
        // re-scales the geometry live — the ship changes size in place, no reload.
        // THE ANCHOR IS WHAT HAS BEEN BUILT, PER DOMAIN (user, after two false starts): Humankind advances eras by
        // FAME, not research, so an empire can sit in the last era with no era-6 units to show — and the aggregate
        // Timeline index lags the frontier the other way (a late game read 5 while era-6 ships were sailing). Both
        // make an ancient hull the wrong size next to what is actually on the map. So: walk the live units, read each
        // one's era straight off its definition NAME (user: "it always contains an era"), and take the MAX PER DOMAIN
        // — the moment an era-6 battleship exists, ships are measured against era 6, while land units are unaffected.
        // Comparing like with like is the whole point: a trireme should look small beside a battleship, not beside a
        // tank. Falls back to the overall unit frontier, then to the empires' tech era, then to the Timeline index.
        static int cachedEra = -1;                        // the world era for display / land default
        static readonly int[] domainEra = new int[4];     // 0 Land, 1 Maritime, 2 Air, 3 Missile — max era BUILT
        static int techEra = -1, aggregateEra = -1;       // research-based fallbacks, also shown for comparison
        static float lastEraPoll = -999f;
        static bool eraApiLogged;

    }
}
