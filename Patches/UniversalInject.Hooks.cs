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
    [HarmonyPatch]
    internal static class UniRegisterHook
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.AnimationManager;
            return t != null ? AccessTools.Method(t, "AnimationLoad") : null;
        }
        static bool hookLogged;
        // THE LOAD SEAM, SEALED PER STEP (review 2026-09-02): this postfix rides the game's own AnimationLoad — the
        // most critical once-per-session chain in the plugin — and was its LAST unguarded call chain (an AUDITED
        // fact, not an assertion — the previous "only unguarded body" comment below was never verified and was
        // false: all ~40 other patch bodies were checked this date and each opens with try or delegates to a
        // whole-body-sealed callee): one throw
        // inside the re-arm aborted registration, the formation apply AND preflight for the whole session, then
        // propagated into the game's load path. Each step now fails alone and LOUD, and the rest still run — they
        // tolerate a degraded predecessor (registration latches lazily, preflight validates whatever exists). The
        // seal lives HERE, at the hook, so it holds structurally even if a callee's internal guard regresses.
        static void Postfix(object __instance)
        {
            if (!hookLogged) { hookLogged = true; Plugin.Diag("[Uni] UniRegisterHook POSTFIX fired"); }
            Prober.AnimMgr = __instance;
            Step("model re-arm", () => UniversalInject.RearmModelRegistration());
            Step("registration", () => UniversalInject.EnsureRegistered(__instance));
            Step("formation overrides", FormationOverride.OnAnimationLoad);
            Step("preflight", UniversalInject.RunPreflight);   // pre-flight AFTER registration: skeletons + clip ids exist to validate against
        }
        // internal: LoadSeamTests pins the seal semantic (a throwing step must neither propagate nor stop later steps).
        internal static void Step(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Plugin.Log?.LogError($"[Uni] load-seam step '{what}' FAILED — the remaining steps still run, but this session's {what} may be degraded: {e}"); }
        }
    }

    [HarmonyPatch]
    internal static class UniRepointHook
    {
        static MethodBase TargetMethod()
        {
            var addon = GameBinding.PresentationPawnDefinitionAddOn;
            var animMgr = GameBinding.AnimationManager;
            return (addon != null && animMgr != null) ? AccessTools.Method(addon, "Load", new[] { animMgr }) : null;
        }
        // Sealed like UniRegisterHook (both callees also guard internally — this is the structural layer): a throw
        // here would break the game's own PresentationPawnDefinitionAddOn.Load. Fires per definition, so log once.
        static void Postfix(object __instance, object __0)
        {
            try { UniversalInject.RepointMatch(__instance, __0); FormationOverride.MaybeScaleFragments(__instance, __0); }
            catch (Exception e) { Plugin.LogOnceWarning("repoint-hook-seal", "[Uni] repoint hook FAILED (sealed, logged once): " + e); }
        }
    }

    // muzzleBone: the donor's fire clip fires the muzzle flash from ITS weapon socket (e.g. an AA gun's "Canon"); that bone
    // name is absent on our renamed rig, so AlterationFireProjectile falls back to the pawn root + the donor's offset and the
    // flash lands off-side. Redirect the bone lookup to OUR muzzle bone. A PREFIX so we skip the original ONLY on the
    // donor-name miss (no "Unable to find bone …" log spam); every real lookup and every non-muzzle unit runs untouched.
    // Returns a TRS (value type) — read/written as boxed object (the plugin's established boxed-TRS pattern, see ObjectSpace).
    [HarmonyPatch]
    internal static class Hk_MuzzleRelocate
    {
        static MethodBase _getBoneTRS;
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PresentationSubPawn;
            _getBoneTRS = t != null ? AccessTools.Method(t, "GetBoneTRS", new[] { typeof(string) }) : null;
            return _getBoneTRS;
        }
        static bool Prefix(object __instance, string boneName, ref object __result)
            => !UniversalInject.MuzzleRedirect(__instance, boneName, _getBoneTRS, ref __result);
        // battle-turn: while the pawn's strike aim override is live, every TRS handed out is rotated onto the
        // true bearing — muzzle smoke, flash and the shell's fire-time recapture all align with the visible barrel.
        static void Postfix(object __instance, ref object __result)
            => UniversalInject.AimRotateBoneTRS(__instance, __result);
    }

    // Per-pawn-per-frame: after the game writes a PawnEntry, let us override its pose for our animated models.
    [HarmonyPatch]
    internal static class UniPawnPoseHook
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PawnManager;
            return t != null ? AccessTools.Method(t, "AddPawnEntry") : null;
        }
        static void Postfix(object __instance)   // timed per pawn-add (FrameCost): vanilla pawns (the early-out path) and OUR pawns (the full pose path) in separate buckets
        { long t0 = FrameCost.Begin(); UniversalInject.OnPawnAdded(__instance); FrameCost.End(UniversalInject.lastPawnMatched ? FrameCost.PoseOurs : FrameCost.PoseHook, t0); }
    }

    // Live trace of every Wwise PostEvent — see exactly what the game posts on the AUDIBLE vanilla unit's emitter vs
    // ours during a move. Gated behind UniversalInject.AudioTraceOn (+ name filter), toggled from the F8 window, so it's
    // free until enabled. The recipe extracted here is what we reproduce to give our units real movement sound.
    // SILENCE-BY-EVENT-NAME: a Prefix on the SAME service sink the trace watches. Any posted event whose name contains a
    // configured substring (Audio/SilenceAudioEvents) is dropped (return false = skip the original post). No-op while the
    // config is empty (ShouldSilenceEvent fast-returns). The POC building block for era-audio: silence the wrong sound,
    // then (later) post the right one.
    [HarmonyPatch]
    internal static class Hk_SilenceEvents
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.AudioManager;
            return t?.GetMethods().FirstOrDefault(m => m.Name == "PostEvent"
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType.Name == "AudioEventHandle"
                && m.GetParameters()[1].ParameterType.Name == "AudioEntityGUID");
        }
        static bool Prefix(object __0)
        {
            try { if (__0 is UnityEngine.Object eo && eo != null && UniversalInject.ShouldSilenceEvent(eo.name)) return false; }
            catch { }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class Hk_AudioTrace
    {
        // Patch the SERVICE-level sink AudioManager.PostEvent(AudioEventHandle, AudioEntityGUID): both emitter sounds AND
        // service-direct sounds (the unit voice, etc.) funnel through here, so it enumerates the FULL sound palette a unit
        // uses — including whatever path the audible engine actually takes (the emitter-level trace only saw the idle).
        static MethodBase TargetMethod()
        {
            var t = GameBinding.AudioManager;
            return t?.GetMethods().FirstOrDefault(m => m.Name == "PostEvent"
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType.Name == "AudioEventHandle"
                && m.GetParameters()[1].ParameterType.Name == "AudioEntityGUID");
        }
        static void Postfix(object __0)
        {
            // Hot path: this runs on EVERY sound the game posts. Once we're not tracing and the fallback handles are all
            // captured, there is nothing to do — bail before any string work.
            try
            {
                if (!UniversalInject.AudioTraceOn && UniversalInject.StashedEngineHandle != null
                    && UniversalInject.StashedLoudHandle != null && UniversalInject.StashedStopHandle != null) return;
                if (!(__0 is UnityEngine.Object eo) || eo == null) return;
                string en = eo.name;
                // Auto-capture (free) the two engine sounds we replay: the idle loop, and the per-ship move-START one-shot
                // (e.g. 'Play_UNIT_Vehicles_StealthCorvette_Start' — the distinct engine sound the audible boats fire on move,
                // which our units never get because it takes the service path, not the emitter's PostEvent).
                if (UniversalInject.StashedEngineHandle == null && en.IndexOf("ModernBoat_Idle", StringComparison.OrdinalIgnoreCase) >= 0)
                    UniversalInject.StashedEngineHandle = __0;
                if (en.IndexOf("Vehicles", StringComparison.OrdinalIgnoreCase) >= 0 && en.IndexOf("_Start", StringComparison.OrdinalIgnoreCase) >= 0)
                { UniversalInject.StashedLoudHandle = __0; UniversalInject.StashedLoudName = en; }
                if (en.IndexOf("Vehicles", StringComparison.OrdinalIgnoreCase) >= 0 && en.IndexOf("_Stop", StringComparison.OrdinalIgnoreCase) >= 0)
                    UniversalInject.StashedStopHandle = __0;
                if (!UniversalInject.AudioTraceOn) return;
                if (UniversalInject.SeenEvents.Add(en)) Plugin.Diag($"[AudioTrace] NEW event: '{en}'");
            }
            // Sits inside the game's own audio call path — a destroyed handle whose .name throws must never break
            // AudioManager.PostEvent (review 2026-07-19). NOTE: this comment once claimed to mark "the ONLY unguarded
            // patch body" — that was false while UniRegisterHook's load chain ran bare (found 2026-09-02, now sealed).
            catch { }
        }
    }

    // EXPERIMENTAL — the DISTRICT injection axis (docs/District-Visuals.md). A district's on-map building is chosen by a
    // named visual-affinity slot (the district-side analogue of a unit's pawnDescription) resolved to a static FxMesh via
    // FxEvolverMaterial. We patch PresentationDistrict.UpdateLevelBuild (the moment the district builds its asset request
    // and calls SetChannel) and, ONLY for the one district named in config, either swap its affinity to another vanilla
    // one (zero-bake proof) or override the resolved mesh channel with our own baked FxEvolverMaterial GUID (custom model).
    // Scoped by ConstructibleDefinitionName, so the shared affinity every other district borrows is never touched.
    [HarmonyPatch]
    internal static class Hk_DistrictRepoint
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PresentationDistrict;
            // UpdateLevelBuild(HgFxAnchorComponent.EventNameEnum) — the district's own override (fires only for districts).
            return t?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == "UpdateLevelBuild" && m.GetParameters().Length == 1);
        }
        // Prefix runs before the request is built, so the affinity swap feeds FillRequest.
        // __0 = the HgFxAnchorComponent.EventNameEnum arg. We stash the last real value so a forced re-resolve
        // (after the */District/Main cell fill lands) can replay a KNOWN-GOOD call instead of guessing the enum.
        // ConsumePendingDistrictReset FIRST on every district hook: a save-load's Sandbox.Load (possibly off-thread) only
        // FLAGS the district reset; the first district to build in the new world performs it here, on the main thread,
        // before anything binds — so the reset still beats the hooks (the Oracle incident) without a cross-thread Clear().
        static void Prefix(object __instance, object __0) { UniversalInject.ConsumePendingDistrictReset(); DistrictInject.CaptureLevelBuildEvent(__0); DistrictInject.DistrictDiag(__instance); DistrictInject.DistrictAffinitySwap(__instance); }
        // Postfix: after UpdateLevelBuild built the request/material — dumps + the registry-driven apply act here.
        static void Postfix(object __instance) { UniversalInject.ConsumePendingDistrictReset(); DistrictInject.DistrictDumpMaterial(__instance); DistrictInject.DistrictDumpSubMaterials(__instance); DistrictInject.DistrictApplyEntries(__instance); DistrictInject.DistrictGuidOverride(__instance); }
    }

    // EXPERIMENTAL — GROUND MATERIAL under a custom district ("maintained grass field"). PresentationDistrict
    // .UpdateGroundMaterial resolves the terrain paint from (Biome × affinity) and calls ApplyGroundMaterialDefinition;
    // our wonder's affinity has no row → bare sand. Postfix forces a chosen ground-material index for our districts.
    [HarmonyPatch]
    internal static class Hk_DistrictGroundMaterial
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PresentationDistrict;
            return t?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == "UpdateGroundMaterial");
        }
        static void Postfix(object __instance) { UniversalInject.ConsumePendingDistrictReset(); DistrictInject.DistrictApplyGroundMaterial(__instance); }
    }

    // Ground-material index rewrite at the SINGLE choke point. UpdateGroundMaterial isn't the only caller of
    // ApplyGroundMaterialDefinition — deposit tiles re-resolve directly to natural terrain, reverting the postfix. This
    // PREFIX rewrites the index for our registered districts on EVERY call, so the paint holds (no re-assert, no twitch).
    // Also carries the DistrictDebug probe (what native tiles resolve to — the "deadzone" we wanted to match).
    [HarmonyPatch]
    internal static class Hk_GroundApplyProbe
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PresentationDistrict;
            return t?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == "ApplyGroundMaterialDefinition" && m.GetParameters().Length == 1);
        }
        static bool Prefix(object __instance, ref int __0)
        {
            UniversalInject.ConsumePendingDistrictReset();
            DistrictInject.GroundApplyProbe(__instance, __0);            // log the ORIGINAL resolve first (native/deadzone)
            return DistrictInject.GroundApplyOverride(__instance, ref __0); // rewrite+run once, then SKIP redundant calls (false) so the blend settles
        }
    }

    // EXPERIMENTAL — HEXAGON SCULPTING under a custom wonder (the raised platform + strategic-zoom footprint).
    // PresentationDistrict.UpdateHexagonSculpting resolves the sculpt from the ArtificialWonder database; a custom
    // wonder's cell is empty -> flat, no footprint. Postfix forces a chosen index for our districts.
    [HarmonyPatch]
    internal static class Hk_DistrictHexSculpt
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PresentationDistrict;
            return t?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == "UpdateHexagonSculpting");
        }
        static void Postfix(object __instance) { UniversalInject.ConsumePendingDistrictReset(); DistrictInject.DumpNativeHexSculpt(__instance); DistrictInject.DistrictApplyHexSculpt(__instance); }
    }

    // THE SPIKE PLAGUE (2026-07-26, first seen the day the 242-bone tank-destroyer shipped): every VISIBLE pawn
    // gets a per-frame slice of ONE shared animated-bone pool (PawnManager.animatedSkeletonEntry buffers, sized
    // from AnimationManager.skeletonBufferSize = 65,535 entries). High-bone custom skeletons (tread links: 242
    // bones/instance; mech: 240) x instances on a dense late-game map overflow the pool — slices past the end
    // read OTHER pawns' matrices, so random pawns (including VANILLA infantry) stretch into spikes, different
    // ones each frame ("twitching"). Bump the field BEFORE PawnManager.Load() creates the buffers from it; the
    // shader-side bound is set from the buffer's actual size, so both stay consistent.
    [HarmonyPatch]
    internal static class Hk_AnimatedBonePoolHeadroom
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.PawnManager;
            return t?.GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }
        static void Prefix()
        {
            // PawnManager.Load is the UNIVERSAL per-session seam: it fires on EVERY game session — save-load, in-session
            // reload, AND a New Game — unlike AnimationLoad (once per process) and Sandbox.Load (save-deserialize only).
            // Requesting the model re-arm here closes the New-Game gap: a new game after a load otherwise never
            // re-registers our skeletons into the rebuilt manager. Idempotent flag; coalesces with Sandbox.Load.
            UniversalInject.RequestReloadRearm();
            try
            {
                int target = Plugin.SkeletonBoneBudget != null ? Plugin.SkeletonBoneBudget.Value : 0;
                if (target <= 0) return;
                var am = GameBinding.AnimationManager;
                var inst = AccessTools.Property(am, "Instance")?.GetValue(null);
                if (inst == null) { Plugin.Log.LogWarning("[Bones] AnimationManager.Instance not ready — pool not enlarged."); return; }
                var f = AccessTools.Field(am, "skeletonBufferSize");
                if (f == null || f.FieldType != typeof(int)) { Plugin.Log.LogWarning("[Bones] skeletonBufferSize field not found."); return; }
                int cur = (int)f.GetValue(inst);
                if (cur >= target) return;
                f.SetValue(inst, target);
                Plugin.Diag($"[Bones] shared animated-bone pool: {cur} -> {target} entries (per-frame skinning budget for ALL pawns).");
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[Bones] pool headroom: " + ex); }
        }
    }

    // EXPERIMENTAL (opt-in) — enlarge the shared 'Visual' GPU mesh buffer so custom district meshes fit even in a full
    // late-game city. ContentLayer.LoadEncodingVertexAndBuffer() creates the vertex buffer from baseVertexBufferSize; we
    // bump that field for the BIG layer (only, to avoid touching the tiny Emitter/default layers) by DistrictBufferHeadroom.
    [HarmonyPatch]
    internal static class Hk_DistrictBufferHeadroom
    {
        static MethodBase TargetMethod()
        {
            var layer = GameBinding.ContentLayer;
            return layer?.GetMethod("LoadEncodingVertexAndBuffer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }
        static void Prefix(object __instance)
        {
            try
            {
                var t = __instance.GetType();
                var nm = (t.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(__instance)
                          ?? t.GetField("name", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(__instance)) as string ?? "";

                // legacy single-purpose knob: enlarge only the building 'Visual' layer (where district meshes go)
                int extra = Plugin.DistrictBufferHeadroom != null ? Plugin.DistrictBufferHeadroom.Value : 0;
                if (extra > 0 && nm.IndexOf("Visual", StringComparison.OrdinalIgnoreCase) >= 0)
                    BumpIntField(__instance, "baseVertexBufferSize", extra, nm, "[District]");

                // generic per-layer overrides: "<nameSubstr>:verts=+N,idx=+N,meshes=+N,maxtris=N;..."
                // verts/idx/meshes ADD to buffer sizes; maxtris SETS the per-mesh triangle cap (0 = unlimited —
                // otherwise quads beyond it are SILENTLY dropped at encode, leaving holes in detailed models).
                var spec = Plugin.BufferOverrides?.Value?.Trim() ?? "";
                if (spec.Length == 0) return;
                foreach (var part in spec.Split(';'))
                {
                    int colon = part.IndexOf(':');
                    if (colon <= 0) continue;
                    string match = part.Substring(0, colon).Trim();
                    if (match.Length == 0 || nm.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (var kv in part.Substring(colon + 1).Split(','))
                    {
                        var p = kv.Split('=');
                        if (p.Length != 2 || !int.TryParse(p[1].Trim().TrimStart('+'), out int val)) continue;
                        switch (p[0].Trim().ToLowerInvariant())
                        {
                            case "verts":   BumpIntField(__instance, "baseVertexBufferSize", val, nm, "[Buffers]"); break;
                            case "idx":     BumpIntField(__instance, "baseIndexBufferSize", val, nm, "[Buffers]"); break;
                            case "meshes":  BumpIntField(__instance, "maxMeshCount", val, nm, "[Buffers]"); break;
                            case "maxtris": SetIntField(__instance, "maxMeshTriangleCount", val, nm, "[Buffers]"); break;
                        }
                    }
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[Buffers] layer override: " + ex); }
        }

        static void BumpIntField(object o, string field, int extra, string layer, string tag)
        {
            var f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null || f.FieldType != typeof(int)) { Plugin.Log.LogWarning($"{tag} field '{field}' not found on ContentLayer."); return; }
            int cur = (int)f.GetValue(o);
            f.SetValue(o, cur + extra);
            Plugin.Diag($"{tag} '{layer}' {field}: {cur} -> {cur + extra} (+{extra}).");
        }

        static void SetIntField(object o, string field, int val, string layer, string tag)
        {
            var f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null || f.FieldType != typeof(int)) { Plugin.Log.LogWarning($"{tag} field '{field}' not found on ContentLayer."); return; }
            int cur = (int)f.GetValue(o);
            f.SetValue(o, val);
            Plugin.Diag($"{tag} '{layer}' {field}: {cur} -> {val}{(field == "maxMeshTriangleCount" && val == 0 ? " (unlimited)" : "")}.");
        }
    }

    // EXPERIMENTAL (opt-in, [Props]) — pawn PROP/attachment axis. Postfix on AnimationManager.AnimationLoad: the exact
    // moment the game rebuilds its collection list and registers its OWN MeshCollections — we append ours right after,
    // BEFORE any pawn definition resolves its fragments. (An Update-tick registration loses that race: the pawn def then
    // fails its Load, never gets a pawn id, and its units draw as pawn definition 0 — a herd of mammoths, memorably.)
    // AnimationLoad runs per game-session and clears the list, so we re-arm the pending set each time it fires.
    [HarmonyPatch]
    internal static class Hk_PropRegister
    {
        static MethodBase TargetMethod()
        {
            var am = GameBinding.AnimationManager;
            return am?.GetMethod("AnimationLoad", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }
        static void Postfix(object __instance)
        {
            try
            {
                if (Plugin.PropRegisterOn == null || !Plugin.PropRegisterOn.Value) return;
                UniversalInject.RearmPropRegistration();                     // fresh session list -> register everything again
                UniversalInject.RegisterPropCollections(__instance, loud: true);
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[Props] AnimationLoad postfix: " + ex); }
        }
    }

    // EXPERIMENTAL (opt-in, [Projectiles]) — projectile axis. Postfix on AnimationManager.AnimationLoad (same per-session
    // seam as props, after data is up and before combat spawns a projectile): re-point the configured units' Projectile at
    // our baked ProjectileAsset. Re-armed each session so the swap re-asserts if the game reloads its data.
    [HarmonyPatch]
    internal static class Hk_ProjectileOverride
    {
        static MethodBase TargetMethod()
        {
            var am = GameBinding.AnimationManager;
            return am?.GetMethod("AnimationLoad", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }
        static void Postfix()
        {
            try
            {
                var cfg = Plugin.ProjectileOverrides?.Value;
                if (string.IsNullOrWhiteSpace(cfg)) return;
                UniversalInject.RearmProjectileOverrides();
                UniversalInject.ApplyProjectileOverrides(cfg);
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[Projectile] AnimationLoad postfix: " + ex); }
        }
    }

    // F8 WINDOW CLICK-THROUGH FIX (2026-08-17, user report: left-dragging the window panned the map under it). The
    // game's own windows stop map input via the public static UIInteractivityManager.IsMouseCovered — the input
    // pipeline stamps it onto events as Modifiers.MouseCovered and the camera ignores covered drags. SpecificUpdate
    // recomputes the flag every frame from the game's OWN responders (our IMGUI window isn't one), so this postfix
    // ORs in "or over the HAF window" right after that computation: every consumer that respects the game's windows
    // then respects ours — no camera-controller surgery, no input-event swallowing.
    [HarmonyPatch]
    internal static class Hk_MouseCoverExtend
    {
        static MethodBase TargetMethod()
        {
            var t = GameBinding.UIInteractivityManager;
            return t != null ? AccessTools.Method(t, "SpecificUpdate") : null;
        }
        static FieldInfo covered;
        static bool armLogged;
        static void Postfix()
        {
            try
            {
                if (!Plugin.WindowHovered) return;
                if (covered == null) covered = AccessTools.Field(GameBinding.UIInteractivityManager, "IsMouseCovered");
                if (covered == null) return;
                covered.SetValue(null, true);
                if (!armLogged) { armLogged = true; Plugin.Diag("[Uni] Hk_MouseCoverExtend: HAF window hover now counts as mouse-covered (map input suppressed under the window)"); }
            }
            catch { }   // patch body must never break the game's input update
        }
    }
}
