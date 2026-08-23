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
        static object animMgrRef;             // AnimationManager instance, captured at registration ([AnimDiag])
        [ProcessLived("diagnostic one-shot dump dedup (lazy)")] static HashSet<string> animDiagDone;  // entries already dumped by the one-shot [AnimDiag]

        // ONE-SHOT GPU-record diagnostic (2026-07-26, the T-62 renders-REST hunt): dump the engine's live
        // per-bone GPUAnimationEntry records (FrameCount/Format/StartPoseData/BBox) for an entry's primary and
        // idle clips, plus the engine's OWN decode of frames 0/1 via its private GetPoseTRS, plus the skeleton's
        // Local rest TRS. Separates "registration wrote garbage records" from "pose data decodes to identity"
        // from "records fine, data fine -> the defect is in pose application".
        static void DumpAnimEntries(ModelEntry e)
        {
            if (animMgrRef == null || e.animId < 0) return;
            if (animDiagDone == null) animDiagDone = new HashSet<string>();
            if (!animDiagDone.Add(e.resourceName)) return;
            try
            {
                var am = animMgrRef;
                Array Content(string name)
                {
                    var buf = AccessTools.Field(am.GetType(), name)?.GetValue(am);
                    return buf == null ? null : GetMember(buf, "WriteContent") as Array;
                }
                var animBuf = Content("gpuAnimationEntryBuffer");
                var skelBuf = Content("gpuSkeletonEntriesBuffer");
                var boneBuf = Content("gpuSkeletonBoneEntiesBuffer");
                if (animBuf == null) { Plugin.Log.LogWarning("[AnimDiag] gpuAnimationEntryBuffer not readable"); return; }
                uint startBone = 0;
                if (skelBuf != null && e.skeletonId >= 0 && e.skeletonId < skelBuf.Length)
                    startBone = Convert.ToUInt32(GetMember(skelBuf.GetValue(e.skeletonId), "StartSkeletonBoneEntry"));
                var getPose = AccessTools.Method(am.GetType(), "GetPoseTRS");
                foreach (var pair in new[] { ("primary", e.animId), ("idle", e.idleAnimId), ("move", e.moveAnimId) })
                {
                    if (pair.Item2 < 0) continue;
                    foreach (int b in new[] { 0, 1, 5, 130 })
                    {
                        int idx = pair.Item2 + b;
                        if (idx >= animBuf.Length) { Plugin.Log.LogWarning($"[AnimDiag] {e.resourceName}:{pair.Item1}+{b} = {idx} beyond buffer {animBuf.Length}"); break; }
                        var ae = animBuf.GetValue(idx);
                        var fc = GetMember(ae, "FrameCount"); var fmt = GetMember(ae, "Format");
                        var spd = GetMember(ae, "StartPoseData");
                        var bmin = GetMember(ae, "BBoxMin"); var bmax = GetMember(ae, "BBoxMax");
                        string decoded = "", local = "";
                        try
                        {
                            if (getPose != null)
                            {
                                var p0 = getPose.Invoke(am, new object[] { spd, fmt, (uint)0, bmin, bmax });
                                decoded = $" | f0 T={GetMember(p0, "Translation")} R={GetMember(p0, "Rotation")} S={GetMember(p0, "Scale")}";
                                uint fcv = Convert.ToUInt32(fc);
                                if (fcv > 2)   // mid-clip frame — f0 is identity by contract and can't discriminate
                                {
                                    var pm = getPose.Invoke(am, new object[] { spd, fmt, fcv / 2, bmin, bmax });
                                    decoded += $" | f{fcv / 2} T={GetMember(pm, "Translation")} R={GetMember(pm, "Rotation")}";
                                }
                            }
                        }
                        catch (Exception px) { decoded = " | decode FAILED: " + px.Message; }
                        try
                        {
                            if (boneBuf != null && startBone + b < boneBuf.Length)
                            {
                                var lb = GetMember(boneBuf.GetValue((int)(startBone + (uint)b)), "Local");
                                local = $" | rest T={GetMember(lb, "Translation")} R={GetMember(lb, "Rotation")} S={GetMember(lb, "Scale")}";
                            }
                        }
                        catch { }
                        Plugin.Diag($"[AnimDiag] {e.resourceName}:{pair.Item1} bone{b}: frames={fc} fmt={fmt} start={spd} bbox={bmin}..{bmax}{local}{decoded}");
                    }
                }
                // WHICH BONES ACTUALLY MOVE (2026-08-22, the towed howitzer's wheels): the fixed bone list above
                // samples four arbitrary indices, so it cannot answer "did the bake keep the roll I authored?".
                // This scans EVERY bone of the skeleton, decodes frame 0 against the middle frame, and NAMES the
                // bones whose rotation actually changes — per role, one-shot per model. A clip the importer
                // flattened prints NONE; a travel clip with a wheel roll prints the wheel bones and their angle.
                // It is what proved the roll reaches the GPU while the unit under suspicion was the wrong one.
                if (getPose != null && GetMember(e.skeleton, "BoneInfos") is Array skelBones)
                {
                    // ATTACK is in this list as of 2026-08-22 (the Vehicle Lab recoil): a barrel that SLIDES is the
                    // one motion this scan could not see, on the one role it did not cover. Rotation-only reporting
                    // could not have answered "did the slide reach the GPU?" either — so translation is reported too.
                    foreach (var pair in new[] { ("primary", e.animId), ("idle", e.idleAnimId), ("move", e.moveAnimId),
                                                 ("premove", e.preMoveAnimId), ("after", e.afterAnimId),
                                                 ("attack", e.attackAnimId) })
                    {
                        if (pair.Item2 < 0) continue;
                        var moved = new List<string>();
                        for (int b = 0; b < skelBones.Length; b++)
                        {
                            int idx = pair.Item2 + b;
                            if (idx < 0 || idx >= animBuf.Length) break;
                            var ae = animBuf.GetValue(idx);
                            uint fcv;
                            try { fcv = Convert.ToUInt32(GetMember(ae, "FrameCount")); } catch { continue; }
                            if (fcv < 2) continue;                                  // a held pose can't move
                            try
                            {
                                var spd2 = GetMember(ae, "StartPoseData"); var fmt2 = GetMember(ae, "Format");
                                var bmin2 = GetMember(ae, "BBoxMin"); var bmax2 = GetMember(ae, "BBoxMax");
                                var p0 = getPose.Invoke(am, new object[] { spd2, fmt2, (uint)0, bmin2, bmax2 });
                                var pm = getPose.Invoke(am, new object[] { spd2, fmt2, fcv / 2, bmin2, bmax2 });
                                if (!TryQuaternion(GetMember(p0, "Rotation"), out var q0) ||
                                    !TryQuaternion(GetMember(pm, "Rotation"), out var qm)) continue;
                                float ang = UnityEngine.Quaternion.Angle(q0, qm);
                                // TRANSLATION, decoded by the ENGINE's own GetPoseTRS — the only thing that settles
                                // whether a kept location curve survives to the GPU. Law 5 says translation is zeroed
                                // for Rotation-encoded curves; a RotationTranslation-encoded bone should report a real
                                // delta here. A bone that only slides has ang ~ 0, so it must not be filtered out by
                                // the rotation test below.
                                string slid = "";
                                if (TryVector3(GetMember(p0, "Translation"), out var t0) &&
                                    TryVector3(GetMember(pm, "Translation"), out var tm))
                                {
                                    float d = (tm - t0).magnitude;
                                    if (d > 0.0005f) slid = $" SLID {d:0.###} ({t0.ToString("0.###")}->{tm.ToString("0.###")})";
                                }
                                if (ang <= 1f && slid.Length == 0) continue;
                                // The bone's REST pivot as the GAME has it, plus the mid-frame quaternion: a wheel that
                                // "spins through the air" is rotating about the wrong POINT (rest far from the wheel's
                                // own centre, e.g. an unscaled 100x pivot) or the wrong AXIS (the quaternion's xyz not
                                // along the axle). Both are unreadable without these numbers (drill 2026-08-22).
                                string rest = "";
                                if (boneBuf != null && startBone + (uint)b < boneBuf.Length &&
                                    GetMember(boneBuf.GetValue((int)(startBone + (uint)b)), "Local") is object lb2)
                                    rest = $" rest T={GetMember(lb2, "Translation")}";
                                moved.Add($"{b}:{GetMember(skelBones.GetValue(b), "Name")}={ang:0}deg{slid}{rest} qMid=({qm.x:0.00},{qm.y:0.00},{qm.z:0.00},{qm.w:0.00})");
                            }
                            catch { }
                        }
                        Plugin.Log.LogInfo($"[AnimDiag] {e.resourceName}:{pair.Item1} bones that MOVE (f0 vs mid): " +
                            (moved.Count == 0 ? "NONE — the baked clip is a held pose"
                                              : string.Join(", ", moved.Take(8)) + (moved.Count > 8 ? $" (+{moved.Count - 8} more)" : "")));
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[AnimDiag] " + ex); }
        }

        static bool? anyAnimated;        // cached early-out: skip the per-pawn hook if no model is animated
        static bool? anyMuzzle;          // cached early-out: skip the muzzle-redirect GetBoneTRS hook if no model has a muzzleBone
        static bool? anyFreeze;          // cached early-out: skip the per-pawn hook if no model wants its donor animation frozen
        static bool? anyRescuable;       // any entry repointed onto our skeleton — the rescue must run even for a purely STATIC pack (no pose behaviour)
        static bool rescueLogged, posLogged, poseErrLogged, scaleLogged;
        [ProcessLived("diagnostic once-per-model dump dedup (lazy)")] static HashSet<string> poseHookSeen;   // dump the pose-hook + runtime transform once PER MODEL (so the howitzer logs even if the drone spawns first)
        [ProcessLived("diagnostic once-per-entry warning dedup")] static readonly HashSet<string> unseededLogged = new HashSet<string>();   // one warning per entry for the disarmed-net state
        [SessionScoped] static HashSet<int> freezeLogSkels;   // distinct skeleton ids we've logged a freeze for (so a second-instance "twin via descriptor" shows up in the log without spamming)
        static float recoilLogStart = -1f;    // diagnostic: log once per fire when the deploy+recoil overlay actually sweeps

        // An entry the per-pawn hook acts on: an ANIMATED model (plays its own baked clip) OR a STATIC model that freezes
        // the donor's animation. Both share the same match (our skeleton id → learned descriptor) + skeleton force; only the
        // pose manipulation differs. Kept as one predicate so the two paths can't drift apart.
        static bool Hooked(ModelEntry x) => x.animId >= 0 || x.freezeDonorAnim;
        // WHO GETS THE WRONG-SKELETON RESCUE (widened 2026-07-31). This used to be `Hooked`, i.e. only models with a
        // pose behaviour — which left every purely STATIC repointed model (8 of the 20 shipped: the cruiser, the
        // hovercraft, the helicopters…) with NO rescue path at all. They are repointed onto our skeleton, so a pawn
        // the game spawns on the DONOR skeleton skins against the wrong bones and draws as spikes, silently, for the
        // whole session — the same failure the descriptor seed fixed for animated models. Rescue is about which RIG
        // a pawn binds to; it has nothing to do with whether we then drive its pose. Gate it on "we repointed this
        // entry onto our own skeleton" instead, and keep the pose decision separate at the dispatch below.
        // Bonus: an ANIMATED entry whose clip fails to resolve (stale GUID after a rebake -> animId -1) used to drop
        // out of `Hooked` and lose the skeleton force too; it keeps it now.
        static bool Rescuable(ModelEntry x) => x.skeletonId >= 0 && x.repointed;
        internal static bool lastPawnMatched;   // set per pawn-add: did this pawn match one of OUR entries? (FrameCost splits vanilla vs ours on it)
        static ModelEntry HookedEntryFor(int skeletonId)
        {
            if (entries == null || skeletonId < 0) return null;
            foreach (var e in entries) if (Rescuable(e) && e.skeletonId == skeletonId) return e;
            return null;
        }

        // Precomputed member names (perf pass 2026-07-19): "Pose" + i / "BoneRotation" + i built fresh STRINGS per
        // pawn per FRAME on the game's hottest loop — thousands of small allocations a second at scale, pure GC churn.
        [ProcessLived("literal member-name table")] static readonly string[] PoseNames = { "Pose0", "Pose1", "Pose2", "Pose3", "Pose4", "Pose5", "Pose6", "Pose7", "Pose8" };
        [ProcessLived("literal member-name table")] static readonly string[] BoneRotationNames = { "BoneRotation0", "BoneRotation1", "BoneRotation2", "BoneRotation3" };

        // Per-pawn state read once at the top of the hook and threaded through the behavior handlers. `entry` is the boxed
        // PawnEntry struct — every SetMember mutates that one box, and the handler writes it back via pawnEntries.SetValue.
        struct PawnCtx { public Array pawnEntries; public int idx; public object entry; public int skelId; public int descId; public int pawnCount; }

        // The game just wrote pawnEntries[pawnCount-1]. Match it to one of our models and hand off to the behavior that model
        // wants: FREEZE (pin the donor clip to frame 0) or an ANIMATED pose whose time is driven by loop / fire-once / deploy.
        // Each behavior is its own method below, so adding a new one is a new handler — not another branch on this hot path.
        internal static void OnPawnAdded(object pawnManager)
        {
            string errSite = "pose";   // refined to the model name once resolved — the catch counts ONCE per site, not per frame
            try
            {
                if (anyAnimated == null) anyAnimated = entries != null && entries.Any(x => x.Role(ClipRole.Primary).Authored);
                if (anyFreeze == null) anyFreeze = entries != null && entries.Any(x => x.freezeDonorAnim);
                // anyRescuable keeps a purely STATIC pack in the hook: those entries have no pose behaviour, so the
                // two flags above are both false, yet they still need the wrong-skeleton rescue. Recomputed when an
                // entry is repointed and on session reset — `repointed` flips at runtime, so it cannot be latched.
                if (anyRescuable == null) anyRescuable = entries != null && entries.Any(Rescuable);
                if ((anyAnimated != true && anyFreeze != true && anyRescuable != true && unitScaleByDesc.Count == 0 && vanillaTurnByDesc.Count == 0 && !AnyCatRate) || !Plugin.UniversalInjectOn.Value) return;
                lastPawnMatched = false;
                pawnMgrRef = pawnManager;   // cached for the live rotor-trim re-apply (PollRotorTrim walks live pawns)
                if (!TryReadLastPawn(pawnManager, out var ctx)) return;
                if (!knownManagers.Contains(pawnManager)) knownManagers.Add(pawnManager);   // every manager, incl. ones only adding vanilla pawns — the sweep needs them all
                var hooked = HookedEntryFor(ctx.skelId);   // resolved ONCE per pawn-add (was three lookups on the vanilla path — perf 2026-08-21)

                // RESIZE LAB: a vanilla pawn (no model entry) whose descriptor has a resolved scale rule gets its
                // ObjectSpace.Scale multiplied ONCE at spawn — the same mechanism the per-entry `scale` field uses.
                if (unitScaleByDesc.Count > 0 && unitScaleByDesc.TryGetValue(ctx.descId, out var vInfo) && hooked == null)
                    ApplyVanillaScale(ctx, vInfo);   // MESH-SCALE engine: verts x s (on change) + ObjectSpace.Scale (per frame)

                // TURN EASE for VANILLA units (docs/Turn-Ease.md): a Formation Lab LINK rate wins; else the
                // TYPE-CATEGORY default (human/land/turret/air/ship — the dial's global defaults). Bank: air
                // category only. Land descriptors learn their turret refinement by position-joining the slow
                // army scan. Same write-back rule as the vanilla scale above: this pawn matches no entry, so
                // nothing downstream persists the mutation for us.
                if ((vanillaTurnByDesc.Count > 0 || AnyCatRate) && hooked == null)
                {
                    float vTurn = 0f; int vCat = -1;
                    if (!vanillaTurnByDesc.TryGetValue(ctx.descId, out vTurn) && AnyCatRate)
                    {
                        // learn from the class scan whenever the desc is UNKNOWN (its pawn definition never
                        // passed the addon hook — the mortar gun) or is land awaiting hover/turret refinement
                        bool known = vanillaCatByDesc.TryGetValue(ctx.descId, out vCat);
                        if ((!known || vCat == CatLand) &&
                            GetMember(ctx.entry, "ObjectSpace") is object vos &&
                            GetMember(vos, "Translation") is UnityEngine.Vector3 vpos)
                        {
                            TryLearnClass(ctx.descId, vpos);
                            if (!known) known = vanillaCatByDesc.TryGetValue(ctx.descId, out vCat);
                        }
                        if (known) vTurn = CategoryRateForDesc(ctx.descId, vCat);
                    }
                    if (vTurn > 0f)
                    {
                        if (vanillaEaseLogged.Add(ctx.descId))
                            Plugin.Log.LogInfo($"[TurnEase] easing vanilla desc {ctx.descId} at {vTurn} deg/s ({(vCat >= 0 ? "category" : "link")}, first pawn seen)");
                        // a LINKED unit (vCat -1) has no category to exclude it: links are Formation Lab picks of
                        // ground/naval units (the Zulu howitzers); a linked helicopter pivoting in place is the
                        // user's own dial choice to make (pivot=0 turns it off globally)
                        int vEff = vCat >= 0 ? EffectiveCat(ctx.descId, vCat) : -1;
                        ApplyTurnEaseCore(vTurn, vCat >= 0 ? CategoryBank(vEff) : 0f, ctx.entry, PivotThresholdForDesc(ctx.descId, vEff));   // a LINKED unit (vEff -1) pivots by default; its own link may still say never
                        ctx.pawnEntries.SetValue(ctx.entry, ctx.idx);
                    }
                    // (the artillery's servant crew — human, rate 0 — needs nothing here: the pivot holds the MOVE
                    // START per unit, so the crew waits with its gun — ShouldHoldMoveStart)
                }

                // Match this pawn to one of our entries (animated OR freeze-static) by OUR baked skeleton id (the correctly
                // skinned pawn), else by the descriptor learned from that first correct pawn. The game spawns a unit's LATER
                // instances on a different vanilla skeleton; without the descriptor fallback only the first instance is
                // handled and the rest slip through (animating / rocking on the donor's rig).
                // RENDER CENSUS (diag=1 in haf_battleturn.txt): one line per DISTINCT descriptor that actually
                // renders, with how this hook classifies it — ends the "which pawn is that on screen?" guessing
                // (the SiegeHowitzers hunt: linked unit, mapped satellites, yet nothing eased).
                if (BattleTurn.diag && descCensusLogged.Add(ctx.descId))
                {
                    string an = null; foreach (var kv in addonDefIds) if (kv.Value == ctx.descId) { an = kv.Key; break; }
                    Plugin.Log.LogInfo($"[Census] desc {ctx.descId} ('{an ?? "?"}') skel {ctx.skelId} entry={(hooked?.resourceName ?? "-")} vanillaTurn={(vanillaTurnByDesc.TryGetValue(ctx.descId, out var cvr) ? cvr.ToString("0") : "-")}");
                }

                var e = hooked; if (e != null) errSite = "pose:" + e.resourceName;
                if (e != null) e.descId = ctx.descId;                  // learn our unit's descriptor from the correct pawn
                else if (ctx.descId >= 0)
                {
                    // plain loop, NOT FirstOrDefault: the lambda captured ctx into a fresh closure allocation for
                    // EVERY pawn the game adds — including every vanilla pawn (perf pass 2026-07-19)
                    var list = entries;
                    if (list != null)
                        for (int i = 0; i < list.Count; i++)
                        { var x = list[i]; if (Rescuable(x) && x.descId >= 0 && x.descId == ctx.descId) { e = x; break; } }
                }
                if (e == null)
                {
                    // Unmatched pawns are overwhelmingly vanilla, so this must stay silent on the hot path — EXCEPT
                    // when one of our entries still has no descriptor. That is the disarmed-safety-net state that
                    // left pawns mis-skinned on the donor rig; the seed at injection should make it impossible, so
                    // if it ever prints, the seed failed and this is the bug to chase. Once per entry.
                    var l2 = entries;
                    if (l2 != null)
                        for (int i = 0; i < l2.Count; i++)
                        {
                            var x = l2[i];
                            // `repointed` is the gate that makes this meaningful: the seed happens in RepointMatch,
                            // which only runs when a unit's AddOn Loads — i.e. when that unit first appears on the
                            // map. An entry whose unit has never been seen legitimately has no descriptor, and
                            // warning about it fired 12 times per launch describing a perfectly healthy state.
                            // Once repointed, though, the seed has run, so descId < 0 really is unreachable.
                            if (Rescuable(x) && x.descId < 0 && unseededLogged.Add(x.resourceName))
                                Plugin.Log.LogWarning($"[Uni] '{x.resourceName}' has NO descriptor yet — its wrong-skeleton net is disarmed, so a pawn spawning now can keep the donor rig (mis-skinned spikes). Expected the injection-time seed to have set it.");
                        }
                    return;
                }

                lastPawnMatched = true; e.lastPoseHookAt = UnityEngine.Time.time;   // from here on this pawn is OURS (FrameCost: PoseOurs bucket)

                // DUPLICATE-PAWN HIDE (2026-08-03, the helicopter "GPU rotor" endgame): gunship-class units spawn a
                // SQUADRON of pawns via the air hardcode — the formation override's 1-dummy layout doesn't reduce them,
                // so 4-5 copies of the model render stacked (the phantom rotors). For hideSubPawns entries keep the
                // FIRST pawn added each frame and hide the rest with the engine's own HideFactor (fog-of-war mechanism).
                if (e.hideSubPawns)
                {
                    // Keep the FIRST pawn per UNIT each frame, hide the squadron duplicates. A gunship unit's stacked
                    // copies share a position; a DIFFERENT unit of the same model sits tiles away — so we key "already
                    // kept" by POSITION, not a per-entry count. (The old per-entry counter kept only one pawn across
                    // ALL units of the type, so a 2nd coexisting unit rendered nothing — critical-review fix 2026-08-16.)
                    int fr = UnityEngine.Time.frameCount;
                    if (e.lastPawnFrame != fr) { e.lastPawnFrame = fr; e.pawnKeptPos.Clear(); }
                    bool havePos = TryGetTranslation(ctx.entry, out var pos);   // PawnFast or reflection
                    bool keptHere = false;
                    if (havePos)
                        for (int k = 0; k < e.pawnKeptPos.Count; k++)
                            if ((e.pawnKeptPos[k] - pos).sqrMagnitude < 0.25f) { keptHere = true; break; }   // within 0.5u = same unit's stack
                    if (keptHere)
                    {
                        // a duplicate of a unit we already kept this frame → hide + bury it. HideFactor hides the mesh
                        // draw, but the ghost overlay samples a pawn slot's data — if it rides a duplicate slot, dropping
                        // that slot 1000u under the world takes the ghost with it. The real mesh is hidden anyway.
                        WriteHideFactor(ctx.entry, 1f);
                        SetTranslation(ctx.entry, new UnityEngine.Vector3(pos.x, pos.y - 1000f, pos.z));
                        ctx.pawnEntries.SetValue(ctx.entry, ctx.idx);
                        return;   // hidden + buried duplicate — no pose work
                    }
                    if (havePos) e.pawnKeptPos.Add(pos);   // first pawn for this unit this frame — remember its spot
                    // the KEPT pawn: un-hide every frame — the cached struct posts HideFactor=1 (the sandwich that
                    // starves the ghost's pre-hook draw); the real post-hook state must render.
                    WriteHideFactor(ctx.entry, 0f);
                }

                ForceOurSkeleton(ctx, e);
                long tS = FrameCost.Begin();
                SweepForStrays(ctx, e);   // stale same-descriptor slots the game no longer rewrites (the ghost-donor fix)
                DumpNearbyPawns(ctx, e);  // ghost census BY POSITION — catches a coincident pawn wearing a DIFFERENT descriptor
                FrameCost.End(FrameCost.PoseSweep, tS);

                // FREEZE (static): no clip of our own — pin the donor pose to frame 0 and stop. ANIMATED: play our clip on Pose0.
                // NEITHER: a purely static repointed model. It reaches here only since the rescue was widened past
                // `Hooked`; it wants the skeleton force and nothing else, so persist the entry and leave the pose
                // alone. Sending it down the animated path would write Pose0 with animId -1. The explicit write-back
                // matters because ForceOurSkeleton only mutates the boxed struct — the pose handlers are what
                // normally store it, and this branch runs neither.
                // USE-DONOR-CLIP (2026-08-04): leave Pose0 exactly as the game wrote it — the DONOR clip plays on our
                // skeleton (the helicopter body hover-bob/pitch restored; its rotor channels land on OUR rotor hubs by
                // bone-index aliasing, so the blades may spin from it too — the Cobra proof: static bakes play the donor
                // clip and move like helicopters). The skeleton force above still applies, so it skins OUR mesh; the
                // explicit write-back persists it. rotorSpinBones then RECLAIMS the hijacked rotor bones: BoneRotation
                // slots override the clip's channel per bone, spun at a constant rate about the dialed axis.
                // Donor-clip path: the donor's clip drives the pose, but the pawn-level adjusters must still run —
                // they live in ApplyAnimatedPose, which this branch bypasses, and without them Position offset
                // (hover height!), moveTilt and runtime scale are silently dead on donor-clip models.
                if (e.useDonorClip) { long tD = FrameCost.Begin(); DumpDonorChannels(ctx.entry, e); ApplyRotorSpin(ctx.entry, e); ApplyRotorTrim(ctx.entry, e); ApplyPositionOffset(e, ctx.entry); ApplyCombatZ(e, ctx.entry); ApplyTerrainHug(e, ctx.entry); ApplyTurnEase(e, ctx.entry); ApplyMoveTilt(e, ctx.entry); ApplyGunElevation(e, ctx.entry); ApplyScale(e, ctx.entry); ctx.pawnEntries.SetValue(ctx.entry, ctx.idx); FrameCost.End(FrameCost.PoseDonor, tD); }
                else
                {
                    // TURN EASE for every non-donor entry too (battle-turn spike): a map attack SNAPS the unit's
                    // facing straight into ObjectSpace.Rotation (the pawn-Transform rotation FSM is a no-op on the
                    // world map — measured 0->0), so ObjectSpace easing is the ONE seam that smooths it — same
                    // mechanism the Comanche flies with. Self-gated: no dial rate AND no per-model rate = no-op.
                    // Runs before the pose handlers; they mutate the same boxed entry and write it back.
                    long tA = FrameCost.Begin();
                    ApplyTurnEase(e, ctx.entry);
                    ApplyGunElevation(e, ctx.entry);   // distance-proportional barrel raise during a bombard (BR slot — independent of the pose)
                    ApplyCombatZ(e, ctx.entry);        // combat height offset — for EVERY non-donor entry (a STATIC submarine dives too); mutates the same boxed entry the branches below write back
                    FrameCost.End(FrameCost.PoseAdjust, tA);
                    long tP = FrameCost.Begin();
                    if (e.freezeDonorAnim && e.animId < 0) ApplyFreeze(ctx, e);
                    else if (e.animId >= 0) ApplyAnimatedPose(ctx, e);
                    else ctx.pawnEntries.SetValue(ctx.entry, ctx.idx);
                    FrameCost.End(FrameCost.PoseAnim, tP);
                }
            }
            // one-shot log: a bare catch here hid member renames after a game update (models just stopped animating, no clue why).
            catch (Exception ex) { NoteInjectionError(errSite); if (!poseErrLogged) { poseErrLogged = true; Plugin.Log.LogError("[Uni] OnPawnAdded (pose hook disabled this pawn): " + ex); } }
        }

        // Read the just-written PawnEntry (pawnCount-1) + its skeleton/descriptor ids, or false if there's nothing to act on.
        static bool TryReadLastPawn(object pawnManager, out PawnCtx ctx)
        {
            ctx = default;
            // Compiled accessors where available — these two run on EVERY pawn-add, before anything knows whether the
            // pawn is ours, so they are the floor cost of the hook. See PawnFast.EnsureMgrInit. The reflection path
            // stays as the fallback, exactly like every other PawnFast site: a renamed member costs speed, not function.
            PawnFast.EnsureMgrInit(pawnManager);
            var pawnEntries = (PawnFast.MgrReady ? PawnFast.MgrEntries(pawnManager) : GetMember(pawnManager, "pawnEntries")) as Array;
            if (pawnEntries == null) return false;
            int pawnCount = PawnFast.MgrReady ? PawnFast.MgrCount(pawnManager) : Convert.ToInt32(GetMember(pawnManager, "pawnCount"));
            if (pawnCount <= 0 || pawnCount > pawnEntries.Length) return false;
            int idx = pawnCount - 1;
            var entry = pawnEntries.GetValue(idx);                     // boxed PawnEntry (struct)
            PawnFast.EnsureInit(entry);                                 // compiled accessors for THIS struct type (once; reflection fallback if unavailable)
            ctx = new PawnCtx
            {
                pawnEntries = pawnEntries, idx = idx, entry = entry,
                skelId = ReadSkelId(entry),
                descId = ReadDescId(entry),
                pawnCount = pawnCount,
            };
            return true;
        }

        // GHOST-DONOR SWEEP (2026-08-03, the StealthHelicopter "GPU rotor"): the hook only ever sees the pawn the game
        // JUST wrote (pawnCount-1), so a STALE slot — left behind by a respawn/reload and never re-added — keeps whatever
        // the game last wrote there (DONOR skeleton + donor clip), yet is still uploaded to the GPU every frame by
        // DoComputation's full-buffer SetData. It renders as a ghost of the donor coincident with the real unit (on the
        // Comanche only the donor's big rotor disc stuck out of our larger model). Every ~2s per entry, walk the WHOLE
        // live array — from inside the hook, i.e. the same thread as the game's writes — and rescue any same-descriptor
        // slot sitting on a foreign skeleton: force our skeleton and put our clip on Pose0 (the donor clip id can't
        // resolve on our rig). A rescued stale slot stays rescued (nobody rewrites it), so the log goes quiet once clean.
        [SessionScoped] static readonly Dictionary<string, float> sweepLast = new Dictionary<string, float>();
        [ProcessLived("diagnostic once-per-name log dedup")] static readonly HashSet<string> sweepScanLogged = new HashSet<string>();
        static int sweepFixLogged;
        // EVERY pawn manager the hook has ever seen (reference-identity; a handful — the map's plus per-battle ones).
        // The hook fires per-manager as pawns are ADDED, so a manager whose buffer was written once and never re-added
        // (a stale PresentationUnit from the load/respawn path) would never be swept via ctx alone — its stale slots
        // keep rendering donor visuals forever. Sweeping every known manager closes that hole. Cleared on session reset.
        [SessionScoped] internal static readonly List<object> knownManagers = new List<object>();
        static void SweepForStrays(PawnCtx ctx, ModelEntry e)
        {
            if (e.descId < 0) return;
            float now = UnityEngine.Time.time;
            if (sweepLast.TryGetValue(e.resourceName, out var last) && now - last < 2f) return;
            sweepLast[e.resourceName] = now;
            for (int m = 0; m < knownManagers.Count; m++)
            {
                var arr = GetMember(knownManagers[m], "pawnEntries") as Array;
                if (arr == null) continue;
                int cnt;
                try { cnt = Convert.ToInt32(GetMember(knownManagers[m], "pawnCount")); } catch { continue; }
                if (cnt <= 0 || cnt > arr.Length) continue;
                int nFixed = 0, nSeen = 0;
                for (int i = 0; i < cnt; i++)
                {
                    var slot = arr.GetValue(i);
                    int d, s;
                    try { d = Convert.ToInt32(GetMember(slot, "PawnDescriptorId")); s = Convert.ToInt32(GetMember(slot, "SkeletonId")); }
                    catch { continue; }
                    if (d != e.descId) continue;
                    nSeen++;
                    if (s == e.skeletonId) continue;
                    SetMember(slot, "SkeletonId", e.skeletonId);
                    var p0 = GetMember(slot, "Pose0");
                    if (p0 != null && e.animId >= 0)
                    {
                        SetMember(p0, "AnimationId", e.animId);
                        SetMember(p0, "Weight", 1f);
                        SetMember(slot, "Pose0", p0);
                    }
                    arr.SetValue(slot, i);
                    nFixed++;
                }
                if (nFixed > 0 && sweepFixLogged < 12)
                { sweepFixLogged++; Plugin.Log.LogInfo($"[Uni][SWEEP] '{e.resourceName}' manager#{m}: rescued {nFixed} stray slot(s) off a foreign skeleton ({nSeen} slot(s) carry desc {e.descId})"); }
                else if (sweepScanLogged.Add(e.resourceName + "#" + m))
                    Plugin.Diag($"[Uni][SWEEP] '{e.resourceName}' manager#{m}: scan — {nSeen} slot(s) carry desc {e.descId}, all on our skeleton {e.skeletonId} ({knownManagers.Count} manager(s) known)");
            }
        }

        // GHOST CENSUS BY POSITION (2026-08-03): every desc-filtered probe came back clean, yet the donor gunship still
        // renders over the StealthHelicopter — so if the ghost IS a pawn, it wears a DIFFERENT PawnDescriptorId (a
        // second pawn definition: an LOD twin, the raw donor def, whatever) and every desc==ours filter is blind to it.
        // For hideSubPawns entries, periodically log EVERY pawn slot within a few world units of our pawn, whatever its
        // descriptor: desc, skel, Pose0 id, position. If a coincident foreign-desc pawn shows up, that's the ghost and
        // its descId is the handle to kill it. If nothing shows, the ghost is provably not a pawn — Fx-layer next.
        static float nearNextAt;
        static bool descTableDumpedLate;
        static void DumpNearbyPawns(PawnCtx ctx, ModelEntry e)
        {
            if (!e.hideSubPawns) return;
            float now = UnityEngine.Time.time;
            if (now < nearNextAt) return;
            nearNextAt = now + 10f;
            // LATE table dump: descriptors registered after our repoint (LODs, lazily-loaded defs) are invisible to the
            // repoint-time dump — re-dump the full table once while the unit is actually on screen (ghost included).
            if (!descTableDumpedLate) { descTableDumpedLate = true; ResetDescTableDump(); DumpDescriptorTable(); ResetFxMeshTableDump(); DumpFxMeshTable(ghostAnimMgr); }
            ScanGhostDescriptors();   // re-scan for late-registered descriptors still drawing the donor mesh (every NEAR tick, ~10s)
            CrushGhostSlice(ghostAnimMgr, e, ghostDonorFxIdx);   // re-crush if the Fx content reloaded (probe-guarded, cheap)
            PollGhostBisect();   // live operator-driven mesh bisect via haf_ghostbisect.txt (no relaunch needed)
            try
            {
                var os0 = GetMember(ctx.entry, "ObjectSpace");
                if (!(GetMember(os0, "Translation") is UnityEngine.Vector3 p0)) return;
                int shown = 0;
                for (int m = 0; m < knownManagers.Count && shown < 24; m++)
                {
                    var arr = GetMember(knownManagers[m], "pawnEntries") as Array;
                    if (arr == null) continue;
                    int cnt;
                    try { cnt = Convert.ToInt32(GetMember(knownManagers[m], "pawnCount")); } catch { continue; }
                    if (cnt <= 0 || cnt > arr.Length) continue;
                    // THE STALE-BUFFER REGION (the ghost's hiding place, 2026-08-03): the GPU uploads the WHOLE
                    // pawnEntries array every frame — but the game (and every census we wrote) only touches
                    // [0..pawnCount). A slot left behind PAST pawnCount by a respawn/rebuild keeps its donor state
                    // forever and still renders. Scan the full array; slots >= pawnCount that carry OUR descriptor
                    // are cleared to default (invisible), which the next full-buffer upload takes to the GPU.
                    int limit = Math.Min(arr.Length, cnt + 64);
                    for (int i = 0; i < limit && shown < 24; i++)
                    {
                        var slot = arr.GetValue(i);
                        int d = -1, s = -1; object animId = null;
                        try { d = Convert.ToInt32(GetMember(slot, "PawnDescriptorId")); s = Convert.ToInt32(GetMember(slot, "SkeletonId")); } catch { }
                        var os = GetMember(slot, "ObjectSpace");
                        bool near = GetMember(os, "Translation") is UnityEngine.Vector3 p && (p - p0).sqrMagnitude <= 9f;
                        bool beyond = i >= cnt;
                        if (!near && !(beyond && d == e.descId)) continue;
                        var pose0 = GetMember(slot, "Pose0");
                        if (pose0 != null) animId = GetMember(pose0, "AnimationId");
                        var pv = GetMember(os, "Translation") is UnityEngine.Vector3 pp ? pp : default;
                        Plugin.Log.LogInfo($"[Uni][NEAR] '{e.resourceName}' mgr#{m} slot[{i}]{(beyond ? " (BEYOND pawnCount=" + cnt + ")" : "")} desc={d} skel={s} pose0={animId} at ({pv.x:0.0},{pv.y:0.0},{pv.z:0.0})" +
                                           (beyond && d == e.descId ? "  <<< STALE GHOST SLOT — CLEARING" : d == e.descId ? "  <ours>" : "  <<< FOREIGN DESC — ghost candidate"));
                        shown++;
                        if (beyond && d == e.descId)
                        {
                            arr.SetValue(Activator.CreateInstance(arr.GetType().GetElementType()), i);   // default struct = renders nothing
                        }
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Uni] DumpNearbyPawns: " + ex.Message); }
        }

        // ROTOR RECLAIM (2026-08-04): the donor clip hijacks our rotor bones by index (tumbling disc). The aim-layer
        // BoneRotation slots override a bone's rotation on top of the playing clip (turretize's proven mechanism) —
        // here driven as a CONSTANT-RATE spinner: each configured bone gets a slot with an ever-advancing angle about
        // its dialed axis. Config "BoneName@axis;BoneName@axis" + rotorSpinSpeed deg/s. Axis is per-model (0/1/2 —
        // the turretAxis lesson: try each). Resolved once against our skeleton's BoneInfos by substring.
        static void ApplyRotorSpin(object entry, ModelEntry e)
        {
            if (string.IsNullOrEmpty(e.rotorSpinBones)) return;
            if (e.rotorIdx == null)
            {
                var specs = e.rotorSpinBones.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                var idx = new List<int>(); var axes = new List<int>();
                foreach (var spec in specs)
                {
                    var at = spec.Split('@');
                    string bn = at[0].Trim();
                    int ax = at.Length > 1 && int.TryParse(at[1], out var a) ? a : 1;
                    int found = -1;
                    if (e.skeleton != null && GetMember(e.skeleton, "BoneInfos") is Array bones)
                        for (int i = 0; i < bones.Length; i++)
                        {
                            var n = GetMember(bones.GetValue(i), "Name")?.ToString() ?? "";
                            if (n.IndexOf(bn, StringComparison.OrdinalIgnoreCase) >= 0) { found = i; break; }
                        }
                    if (found >= 0) { idx.Add(found); axes.Add(ax); }
                    Plugin.Diag($"[RotorSpin] '{e.resourceName}' bone '{bn}' -> index {found} axis {ax}");
                }
                e.rotorIdx = idx.ToArray(); e.rotorAxis = axes.ToArray();
            }
            if (e.rotorIdx.Length == 0) return;
            float angle = (UnityEngine.Time.time * e.rotorSpinSpeed) % 360f;
            for (int i = 0; i < e.rotorIdx.Length && i < 4; i++)
                SetBoneRotation(entry, i, (uint)e.rotorIdx[i], (uint)e.rotorAxis[i], angle);   // fast path (PawnFast) or reflection
        }

        // DONOR-AXIS DIAGNOSTIC (2026-08-04, the canted-fantail question): decode the DONOR clip's four channels
        // straight from the GPU animation records (same GetPoseTRS route as [AnimDiag]) and log each channel's
        // rotation quaternion at several frames. Answers WITH DATA which local axis the donor's tail-rotor channel
        // (ch3, Helix_back) spins about — the bake must orient our canted fan bone so that axis lands on the fan's
        // real axle. One-shot per entry, useDonorClip only.
        [ProcessLived("diagnostic once-per-name dump dedup")] static readonly HashSet<string> donorAxisDumped = new HashSet<string>();
        static void DumpDonorChannels(object entry, ModelEntry e)
        {
            if (animMgrRef == null || !donorAxisDumped.Add(e.resourceName)) return;
            try
            {
                var p0 = GetMember(entry, "Pose0");
                uint animId = Convert.ToUInt32(GetMember(p0, "AnimationId"));
                var am = animMgrRef;
                var buf = AccessTools.Field(am.GetType(), "gpuAnimationEntryBuffer")?.GetValue(am);
                var animBuf = buf == null ? null : GetMember(buf, "WriteContent") as Array;
                var getPose = AccessTools.Method(am.GetType(), "GetPoseTRS");
                if (animBuf == null || getPose == null) { Plugin.Log.LogWarning("[DonorAxis] buffers/GetPoseTRS unavailable"); return; }
                Plugin.Log.LogInfo($"[DonorAxis] {e.resourceName}: donor animId={animId} — decoding channels 0..3");
                for (int ch = 0; ch < 4; ch++)
                {
                    long idx = animId + ch;
                    if (idx < 0 || idx >= animBuf.Length) break;
                    var ae = animBuf.GetValue((int)idx);
                    uint fc = Convert.ToUInt32(GetMember(ae, "FrameCount"));
                    var fmt = GetMember(ae, "Format"); var spd = GetMember(ae, "StartPoseData");
                    var bmin = GetMember(ae, "BBoxMin"); var bmax = GetMember(ae, "BBoxMax");
                    var line = $"[DonorAxis] ch{ch}: frames={fc}";
                    foreach (uint f in new uint[] { 0, 1, fc / 4, fc / 2, 3 * fc / 4 })
                    {
                        if (fc > 0 && f >= fc) continue;
                        var trs = getPose.Invoke(am, new object[] { spd, fmt, f, bmin, bmax });
                        var r = GetMember(trs, "Rotation");
                        line += $" | f{f} T={GetMember(trs, "Translation")} R=({Convert.ToSingle(GetMember(r, "x")):0.###},{Convert.ToSingle(GetMember(r, "y")):0.###},{Convert.ToSingle(GetMember(r, "z")):0.###},{Convert.ToSingle(GetMember(r, "w")):0.###})";
                    }
                    Plugin.Log.LogInfo(line);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[DonorAxis] " + ex.Message); }
        }

        // ROTOR TRIM (2026-08-04, the "slightly wobbling" fix): the airframe is modeled leaning ~10-15 deg forward,
        // so the blade disc is perpendicular to the TILTED mast while the donor clip spins it about true vertical —
        // the disc precesses by the lean angle. A CONSTANT BoneRotation-slot rotation (turretize's mechanism; the
        // spawn-time write is fine because the angle never advances) re-aligns disc and spin axis. Dialed LIVE via
        // BepInEx/config/haf_rotortrim.txt — one line per bone, `BoneSubstring@axis=degrees` (axis 0/1/2), '#'
        // comments — polled ~1/s and re-applied to live pawns, so tuning needs no relaunch.
        // Say out loud whatever the pure dial parser could not understand. Before this, EVERY unrecognised line in
        // a haf_*.txt dial was silently `continue`d — a typo produced a working plugin that quietly ignored the
        // setting. One WARN per problem, naming the file and the line. See Patches/DialConfig.cs.
        internal static void LogDialProblems(string file, List<string> problems)
        {
            for (int i = 0; i < problems.Count; i++) Plugin.Log.LogWarning($"[Dial] {file}: {problems[i]}");
        }

        struct TrimSpec { public string bone; public int axis; public float deg; }
        [ProcessLived("dial-driven list, rebuilt from the dial")] static readonly List<TrimSpec> trims = new List<TrimSpec>();
        static object pawnMgrRef;
        static string trimSig;
        static float trimNextPoll;

        internal static void PollRotorTrim()
        {
            if (UnityEngine.Time.realtimeSinceStartup < trimNextPoll) return;
            trimNextPoll = UnityEngine.Time.realtimeSinceStartup + 1f;
            try
            {
                var path = Path.Combine(Paths.ConfigPath, "haf_rotortrim.txt");
                string txt = File.Exists(path) ? File.ReadAllText(path) : "";
                if (txt == trimSig) return;
                trimSig = txt;
                var problems = new List<string>();
                var dial = RotorTrimDial.Parse(txt, problems);       // PURE parse — Patches/DialConfig.cs, unit-tested
                LogDialProblems("haf_rotortrim.txt", problems);
                trims.Clear();
                foreach (var t in dial.Trims) trims.Add(new TrimSpec { bone = t.Bone, axis = t.Axis, deg = t.Deg });
                int applied = 0;
                if (pawnMgrRef != null && entries != null &&
                    GetMember(pawnMgrRef, "pawnEntries") is Array pe)
                {
                    int pc = Convert.ToInt32(GetMember(pawnMgrRef, "pawnCount"));
                    for (int i = 0; i < pc && i < pe.Length; i++)
                    {
                        var en = pe.GetValue(i);
                        int sk = Convert.ToInt32(GetMember(en, "SkeletonId"));
                        var e = entries.FirstOrDefault(x => x.useDonorClip && x.skeletonId >= 0 && x.skeletonId == sk);
                        if (e == null) continue;
                        ApplyRotorTrim(en, e);
                        pe.SetValue(en, i);
                        applied++;
                    }
                }
                Plugin.Log.LogInfo($"[Trim] reloaded {trims.Count} line(s), re-applied to {applied} live pawn(s)");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Trim] " + ex.Message); }
        }

        // TURN-EASE FILE POLL (spike): haf_turnease.txt in BepInEx/config — `rate=<deg/s>` `bank=<deg>`
        // `snap=<deg>`, '#' comments. Same ~1/s cadence as the rotor trim; missing file or rate=0 disables.
        static string turnSig;
        static float turnNextPoll;
        internal static void PollTurnEase()
        {
            if (UnityEngine.Time.realtimeSinceStartup < turnNextPoll) return;
            turnNextPoll = UnityEngine.Time.realtimeSinceStartup + 1f;
            try
            {
                var path = Path.Combine(Paths.ConfigPath, "haf_turnease.txt");
                string txt = File.Exists(path) ? File.ReadAllText(path) : "";
                if (txt == turnSig) return;
                turnSig = txt;
                var problems = new List<string>();
                var d = TurnEaseDial.Parse(txt, problems);           // PURE parse — Patches/DialConfig.cs, unit-tested
                LogDialProblems("haf_turnease.txt", problems);
                turnRate = d.Rate; turnBank = d.Bank;
                catHumanRate = d.Human; catLandRate = d.Land; catTurretRate = d.Turret; catHoverRate = d.Hover; catShipRate = d.Ship;
                catHoverBank = d.HoverBank;   // legacy files with no `hoverbank` keep inheriting `bank` (resolved in the parse)
                catShipBank = d.ShipBank;
                turnPivot = d.Pivot;          // pivot-in-place threshold (default 90; 0 = off) — ground/naval only
                // (The gun-elevation descent used to be dialled here. It is PER-MODEL now — Animation Lab, beside
                //  the elevation angle it belongs with — so it lives on the entry, not in this global file.)
                // Numbers echoed with DialConfig.Inv so the log spells them the way the dial FILE must (invariant
                // '.') — a comma-decimal locale used to print values the parser would then reject. See Inv().
                Plugin.Log.LogInfo($"[TurnEase] rate={DialConfig.Inv(d.Rate)} bank={DialConfig.Inv(d.Bank)} | categories human={DialConfig.Inv(d.Human)} land={DialConfig.Inv(d.Land)} turret={DialConfig.Inv(d.Turret)} hover={DialConfig.Inv(d.Hover)} ship={DialConfig.Inv(d.Ship)} deg/s, hoverbank={DialConfig.Inv(catHoverBank)} shipbank={DialConfig.Inv(catShipBank)} deg (planes excluded), pivot={DialConfig.Inv(d.Pivot)} deg (ground/naval turn in place first)");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[TurnEase] " + ex.Message); }
        }

        // TERRAIN-HUG FILE POLL (spike): haf_hugterrain.txt — `drop=-2` (how much LOWER over open ground; 0 = off),
        // `radius=6` (district match radius), `lookahead=3` (probe distance ahead), `ease=4` (units/s climb rate).
        static string hugSig;
        static float hugNextPoll;
        internal static void PollTerrainHug()
        {
            if (UnityEngine.Time.realtimeSinceStartup < hugNextPoll) return;
            hugNextPoll = UnityEngine.Time.realtimeSinceStartup + 1f;
            try
            {
                var path = Path.Combine(Paths.ConfigPath, "haf_hugterrain.txt");
                string txt = File.Exists(path) ? File.ReadAllText(path) : "";
                if (txt == hugSig) return;
                hugSig = txt;
                var problems = new List<string>();
                var d = TerrainHugDial.Parse(txt, problems);         // PURE parse — Patches/DialConfig.cs, unit-tested
                LogDialProblems("haf_hugterrain.txt", problems);
                // name filters: which PresentationDistricts count as "a city block" (vs a farm/exploitation)
                hugOnly.Clear(); hugSkip.Clear();
                hugOnly.AddRange(d.Only); hugSkip.AddRange(d.Skip);
                hugDrop = d.Drop; hugRadius = d.Radius; hugLookahead = d.Lookahead; hugEase = d.Ease; hugCliff = d.Cliff;
                RearmDistrictScan();   // filters changed -> the cached district set must be rebuilt
                Plugin.Log.LogInfo($"[Hug] drop={DialConfig.Inv(d.Drop)} radius={DialConfig.Inv(d.Radius)} lookahead={DialConfig.Inv(d.Lookahead)} ease={DialConfig.Inv(d.Ease)} cliff={DialConfig.Inv(d.Cliff)}" +
                                   (hugOnly.Count > 0 ? " only=" + string.Join(",", hugOnly) : "") +
                                   (hugSkip.Count > 0 ? " skip=" + string.Join(",", hugSkip) : ""));
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Hug] " + ex.Message); }
        }

        // GUN ELEVATION (2026-08-06, user spec): during a bombard, raise the gun barrel DISTANCE-proportionally
        // to the model's configured max angle — a short lob barely lifts, a full-range shot elevates fully
        // (full at ~3 tiles). Rides the strike aim override's envelope (up over the turn hold, hold, down as
        // the override expires) and writes BoneRotation slot 3 (spin/trim fill 0-up; a 4-trim dial would
        // collide — documented). Bone = turretBone else muzzleBone, axis per gunElevAxis, resolved once.
        // Range band the elevation ramps across, in TILES: a 1-tile shot commands none of it, an 8-tile shot all of it.
        const float ElevMinTiles = 1f, ElevMaxTiles = 8f;

        static void ApplyGunElevation(ModelEntry e, object entry)
        {
            if (e.gunElevMax == 0f) return;
            // WHY THE ELEVATION DID NOTHING, per exit (2026-08-22). The bone resolve sits BELOW the aim gate, so an
            // absent "[Elev] bone not found" warning does NOT mean the bone resolved — it usually means we returned
            // before ever looking. Each early exit now says so once, so "no effect" names its own cause.
            if (!TryGetTranslation(entry, out var pos))
            { Plugin.DiagOnce("elev-nopos-" + e.resourceName, $"[Elev] '{e.resourceName}': no pawn translation — elevation cannot run"); return; }
            if (!TryAimElevAt(pos, out float dist, out float f, e.gunElevRise, e.gunElevHold, e.gunElevFall))
            { Plugin.DiagOnce("elev-noaim-" + e.resourceName, $"[Elev] '{e.resourceName}': no aim override near the pawn (or its target distance is 0) — elevation only runs during a bombard"); return; }
            Plugin.DiagOnce("elev-live-" + e.resourceName, $"[Elev] '{e.resourceName}': aim found, dist={dist:F1} envelope={f:F2}");
            if (e.gunElevBoneIdx == -2)
            {
                e.gunElevBoneIdx = -1;
                string bn = !string.IsNullOrEmpty(e.turretBone) ? e.turretBone : e.muzzleBone;
                var bones = e.skeleton == null ? null : GetMember(e.skeleton, "BoneInfos") as Array;
                if (!string.IsNullOrEmpty(bn) && bones != null)
                    for (int i = 0; i < bones.Length; i++)
                    {
                        var n = GetMember(bones.GetValue(i), "Name")?.ToString() ?? "";
                        if (n.IndexOf(bn, StringComparison.OrdinalIgnoreCase) >= 0) { e.gunElevBoneIdx = i; break; }
                    }
                if (e.gunElevBoneIdx < 0) Plugin.Log.LogWarning($"[Elev] '{e.resourceName}': gun bone '{bn}' not found — gun elevation off");
            }
            if (e.gunElevBoneIdx < 0) return;
            // RANGE BAND (user spec 2026-08-22): elevation ramps across 1..8 TILES — a point-blank shot sits at the
            // gun's resting elevation and only an 8-tile shot commands the full angle. It used to saturate at 3
            // tiles, so nearly every bombard fired at maximum elevation and the "further = higher" reading was lost.
            float sp = tileSpacing > 0.1f ? tileSpacing : 6.93f;
            float tiles = dist / sp;
            float span = UnityEngine.Mathf.Max(0.01f, ElevMaxTiles - ElevMinTiles);
            // SIGN: NEGATED so that a POSITIVE gunElevMax RAISES the barrel (2026-08-22, in-game).
            // A positive rotation about the gun bone's pitch axis points the muzzle DOWN in the engine's frame — the
            // baked Deploy raise proves it from the other side, keying its elevation as a negative X component
            // (`qMid=(-0.20, 0, 0, 0.98)` for a 22 deg raise). Before this, "Gun elevation — max = 25" lowered the
            // gun, and the two elevation dials a modeller sets together disagreed in sign: the Vehicle Lab's
            // "Gun raise on deploy" elevated while this one depressed. A dial labelled elevation must elevate.
            // A rig whose gun bone genuinely pitches the other way can still dial a NEGATIVE max.
            float angle = -e.gunElevMax * UnityEngine.Mathf.Clamp01((tiles - ElevMinTiles) / span) * f;
            if (UnityEngine.Mathf.Abs(angle) < 0.05f)
            { Plugin.DiagOnce("elev-tiny-" + e.resourceName, $"[Elev] '{e.resourceName}': angle {angle:F3}deg too small to apply (dist={tiles:F1} tiles of the {ElevMinTiles:F0}..{ElevMaxTiles:F0} band, envelope={f:F2})"); return; }
            Plugin.DiagOnce("elev-applied-" + e.resourceName, $"[Elev] '{e.resourceName}': APPLYING {angle:F1}deg to bone[{e.gunElevBoneIdx}] axis {e.gunElevAxis} (BoneRotation slot 3)");
            // THE PEAK is what matters, not the first frame. The envelope ramps from 0 across the turn hold, so the
            // first APPLYING line always reports a near-zero angle and says nothing about whether the elevation ever
            // gets large. If this never logs, the envelope never opens; if it logs a big angle and the gun still does
            // not move, the write is reaching the entry and losing to the clip's own channel for that bone.
            if (UnityEngine.Mathf.Abs(angle) >= 0.5f * UnityEngine.Mathf.Abs(e.gunElevMax))
                Plugin.DiagOnce("elev-peak-" + e.resourceName, $"[Elev] '{e.resourceName}': reached {angle:F1}deg (>= half of gunElevMax {e.gunElevMax:F0}) — envelope={f:F2}, dist={tiles:F1} tiles");
            SetBoneRotation(entry, 3, (uint)e.gunElevBoneIdx, (uint)e.gunElevAxis, angle);
        }

        static void ApplyRotorTrim(object entry, ModelEntry e)
        {
            if (trims.Count == 0) return;
            // RESOLVE ONCE per entry per dial edit (perf pass 2026-08-21): this re-read every bone's Name through reflection
            // (+ a string alloc each) for every trim line, per pawn, per frame — with 6 dial lines and a 60-bone rig that
            // was ~700 reflection ops per helicopter per frame. trimSig changes only when the dial file changes.
            if (!ReferenceEquals(e.rotorTrimSig, trimSig))
            {
                var bones = e.skeleton == null ? null : GetMember(e.skeleton, "BoneInfos") as Array;
                var idx = new List<int>(); var axes = new List<int>(); var degs = new List<float>();
                if (bones != null)
                    foreach (var t in trims)
                    {
                        if (idx.Count >= 4) break;
                        int found = -1;
                        for (int i = 0; i < bones.Length; i++)
                        {
                            var n = GetMember(bones.GetValue(i), "Name")?.ToString() ?? "";
                            if (n.IndexOf(t.bone, StringComparison.OrdinalIgnoreCase) >= 0) { found = i; break; }
                        }
                        if (found >= 0) { idx.Add(found); axes.Add(t.axis); degs.Add(t.deg); }
                    }
                e.rotorTrimIdx = idx.ToArray(); e.rotorTrimAxis = axes.ToArray(); e.rotorTrimDeg = degs.ToArray();
                e.rotorTrimSig = trimSig;
            }
            for (int slot = 0; slot < e.rotorTrimIdx.Length; slot++)
                SetBoneRotation(entry, slot, (uint)e.rotorTrimIdx[slot], (uint)e.rotorTrimAxis[slot], e.rotorTrimDeg[slot]);
        }

        // FORCE our skeleton so this pawn skins by OUR rig. A LATER instance the game spawned on a vanilla skeleton would
        // otherwise draw mis-skinned (animated) or WARP when we pin a foreign skeleton's frame 0 (freeze — the vertical,
        // "shape-shifting" airship). Shared by both paths, so every instance ends up on our skeleton.
        static void ForceOurSkeleton(PawnCtx ctx, ModelEntry e)
        {
            if (ctx.skelId == e.skeletonId) return;
            WriteSkelId(ctx.entry, e.skeletonId);
            if (!rescueLogged) { rescueLogged = true; Plugin.Diag($"[Uni] rescued wrong-skeleton pawn: skelId {ctx.skelId} -> {e.skeletonId} (descId {ctx.descId})"); }
        }

        // FREEZE (static): pin every pose's Time to frame 0 so the donor clip can't advance — the borrowed mesh holds rigid
        // instead of inheriting the donor's hover/drive bob. Weights untouched (keep the pose SHAPE, stop the MOTION); the
        // pawn still glides tile-to-tile (transform-driven, not in the pose). Re-applied every frame (per pawn-add), so it holds.
        static void ApplyFreeze(PawnCtx ctx, ModelEntry e)
        {
            for (int i = 0; i < 9; i++)
            {
                var pose = GetMember(ctx.entry, PoseNames[i]);
                if (pose == null) continue;
                SetMember(pose, "Time", 0f);
                SetMember(ctx.entry, PoseNames[i], pose);
            }
            ctx.pawnEntries.SetValue(ctx.entry, ctx.idx);
            if (freezeLogSkels == null) freezeLogSkels = new HashSet<int>();
            if (freezeLogSkels.Add(ctx.skelId) && freezeLogSkels.Count <= 6)
                Plugin.Diag($"[Uni] freeze: '{e.resourceName}' pinned (skelId {ctx.skelId} -> {e.skeletonId}, descId {ctx.descId})");
        }

        // ANIMATED: play OUR clip on Pose0 (weight 1, advancing time); zero the others (never all-zero => NaN => invisible),
        // clear the aim layer, and apply the runtime position/scale. The pose Time comes from the model's behavior below.
        [SessionScoped] static readonly Dictionary<string, float> pawnLiveLast = new Dictionary<string, float>();

        // [PawnLive] throttled live-pawn dump (2026-07-26, T-62 hunt): read the entry AS THE GAME LEFT IT
        // (before our writes this frame) — Pose slots + BoneRotation layer. Our own log lines only ever showed
        // what WE wrote; this shows what survived the engine's update.
        static void DumpPawnLive(PawnCtx ctx, ModelEntry e)
        {
            float last;
            pawnLiveLast.TryGetValue(e.resourceName, out last);
            if (UnityEngine.Time.time - last < 5f) return;
            pawnLiveLast[e.resourceName] = UnityEngine.Time.time;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"[PawnLive] {e.resourceName} desc={ctx.descId} skel={ctx.skelId}");
                for (int i = 0; i < 3; i++)
                {
                    var p = GetMember(ctx.entry, PoseNames[i]);
                    if (p == null) continue;
                    sb.Append($" | {PoseNames[i]} id={GetMember(p, "AnimationId")} w={GetMember(p, "Weight")} t={Convert.ToSingle(GetMember(p, "Time")):0.###}");
                }
                for (int i = 0; i < 4; i++)
                {
                    var br = GetMember(ctx.entry, BoneRotationNames[i]);
                    if (br == null) continue;
                    long bi = -1, ax = -1; float an = 0f;
                    try { bi = Convert.ToInt64(GetMember(br, "SkeletonBoneIndex")); ax = Convert.ToInt64(GetMember(br, "AxisIndex")); an = Convert.ToSingle(GetMember(br, "Angle")); } catch { }
                    sb.Append($" | BR{i} bone={bi} axis={ax} ang={an:0.#}");
                }
                Plugin.Diag(sb.ToString());
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[PawnLive] " + ex.Message); }
        }

        static void ApplyAnimatedPose(PawnCtx ctx, ModelEntry e)
        {
            var entry = ctx.entry;
            DumpAnimEntries(e);                                        // one-shot [AnimDiag] per entry (no-op after first)
            DumpPawnLive(ctx, e);                                      // throttled [PawnLive] engine-state dump
            // PER-INSTANCE PHASE (2026-07-31): every pawn was fed the SAME Time.time/dur, so a multi-pawn unit
            // moved as one body — twelve canoes rocking in perfect lockstep reads as a single rigid raft. Offset
            // each pawn by a deterministic fraction of its clip. ctx.idx is the pawn's slot in the entries array
            // (the game rewrites every pawn every frame, so it holds steady while the pawn lives); the golden
            // ratio spreads consecutive slots evenly instead of clustering them as idx/count would. Added ONLY to
            // looping poses — one-shots (attack, deploy, fire) are timed from their trigger and must not shift.
            float phase = PhaseFor(e, entry);
            // PawnEntryPose.Time is NORMALIZED (sampler does Mathf.Repeat(Time,1) = one loop). ComputePoseTime divides by the
            // clip duration so it plays at REAL speed and hits every frame; raw Time.time = duration× too fast + frame-skipping.
            // STATE-DRIVEN models switch Pose0's ANIMATIONID by movement state. This is safe: PawnManager's
            // DoComputation() does a FULL pawnEntriesBuffer.SetData(pawnEntries) EVERY FRAME (decompiled), so every
            // field we write — ids included — reaches the GPU each frame; there is no id latching. (The earlier
            // "id switching is ignored" reading was an illusion: the move clip's data was CONSTANT at a near-idle
            // stance at the time. A Pose1 weight-switching attempt rendered the pawn INVISIBLE while moving — the
            // secondary slots misbehave on the GPU pass in some unmapped way — so the state machine deliberately
            // uses only the battle-tested Pose0.)
            if (e.animStateDriven && e.AnyStateRole)   // ANY state role (move/attack/after/combat/preMove/idleAlt/idle), not just move — else a move-less model's attacks arm but never play
            {
                StatePose(e, entry, out bool moving, out bool inAfter, out float afterT, out bool inAttack, out float attackT, out bool inCombat, out bool inPreMove, out float preMoveT, out bool inIdleAlt, out float idleAltT, out int idleAltId);
                if (inAttack)
                {
                    // ATTACK wins over every other state: the pawn just fired (ranged-fight hook armed a window at
                    // its position) — one clamped 0->1 pass of the attack clip, holding the last frame until it elapses.
                    SetPose(entry, 0, (uint)e.attackAnimId, attackT);
                }
                else if (inPreMove)
                {
                    // PRE-MOVEMENT: the unit just STARTED moving — one clamped pass (e.g. the howitzer folding),
                    // then the Move loop takes over when the window elapses.
                    SetPose(entry, 0, (uint)e.preMoveAnimId, preMoveT);
                }
                else if (moving && e.moveAnimId >= 0)   // guard: a move-less state-driven model (idle+attack only) that moves falls through to idle rather than a bad -1 anim id
                {
                    float md = e.moveDur > 0.001f ? e.moveDur : 1f;
                    SetPose(entry, 0, (uint)e.moveAnimId, UnityEngine.Time.time / md + phase);
                }
                else if (inAfter)
                {
                    SetPose(entry, 0, (uint)e.afterAnimId, afterT);
                }
                else if (inCombat && e.combatAnimId >= 0)
                {
                    // COMBAT-IDLE: the army is locked in a battle — hold the combat stance instead of the relaxed idle.
                    // (A single-frame stance clip renders fine: FrameCount 1 pins the sampler to frame 0 at any Time.)
                    float cd = e.combatDur > 0.001f ? e.combatDur : 1f;
                    SetPose(entry, 0, (uint)e.combatAnimId, UnityEngine.Time.time / cd + phase);
                }
                else if (inIdleAlt && idleAltId >= 0)
                {
                    // IDLE-ALT: the occasional flavor one-shot (howl/eat) — one clamped 0->1 pass at the performer
                    // pawn, then back to the normal idle. Never fires outside plain idle (StatePose gates it).
                    SetPose(entry, 0, (uint)idleAltId, idleAltT);
                }
                else if (e.idleAnimId >= 0)
                {
                    // IDLE OVERRIDE role (bake-only stance fix, 2026-07-19): a stance baked as the PRIMARY clip
                    // encodes ~identity against the skeleton's reference pose (the primary defines that reference)
                    // and renders as REST in-game — the "forgot to deploy" trap. The stance therefore bakes as a
                    // ROLE (real deltas against the full primary clip's reference) and idle plays it here; the
                    // primary (e.animId) stays the full reference clip and is what plays when no override is set.
                    float idleDur = e.idleDur > 0.001f ? e.idleDur : 1f;
                    SetPose(entry, 0, (uint)e.idleAnimId, UnityEngine.Time.time / idleDur + phase);
                }
                else
                {
                    float idleDur = e.animDuration > 0.001f ? e.animDuration : 1f;
                    SetPose(entry, 0, (uint)e.animId, UnityEngine.Time.time / idleDur + phase);
                }
                SetPoseWeight(entry, 0, 1f);
            }
            else
            {
                float dur = e.animDuration > 0.001f ? e.animDuration : 1f;
                SetPose(entry, 0, (uint)e.animId, ComputePoseTime(e, entry, dur, phase));
                SetPoseWeight(entry, 0, 1f);
            }
            for (int i = 1; i < 9; i++) SetPoseWeight(entry, i, 0f);   // zero the secondary slots (never all-zero => NaN => invisible)
            // The AIM layer is cleared only for the ARTILLERY behaviors (it twists the howitzer's barrel as the game
            // aims). For other animated models the game's bone-rotation layer stays — it carries the pawn's FACING
            // (clearing it froze the soldier to one compass direction) — but on some donors it arrives with an
            // INVALID bone index (0xFFFFFFFF) and RUNAWAY angles (1558°…): those magnitudes deform the rig (the
            // soldier's ripped-off head). SanitizeAimLayer wraps such angles into 0..360 — same orientation, sane
            // magnitude — instead of zeroing (which would kill facing).
            long tAim = FrameCost.Begin();
            if (((e.fireOnAttack || e.deployOnStop) && !e.animStateDriven) || e.clearAimLayer) ClearAimLayer(entry, e);   // legacy artillery rule, OR the explicit per-model knob (a STATE-DRIVEN howitzer still needs the donor's aim/wheel junk cleared; characters keep the layer for facing)
            else if (!string.IsNullOrEmpty(e.turretBone)) TurretizeAimLayer(entry, e);   // retarget the game's aim/heading angle onto OUR turret bone (a vehicle turret tracks the target)
            else SanitizeAimLayer(entry);
            FrameCost.End(FrameCost.PoseAim, tAim);
            ApplyPositionOffset(e, entry);
            ApplyMoveTilt(e, entry);       // nose-down while moving (helicopter attitude), eased; no-op at moveTilt 0
            ApplyScale(e, entry);
            // NOTE (2026-07-29, shader-proven): ApplyScale above writes ObjectSpace.Scale, which the GPU honours for
            // PLACEMENT ONLY (bone world positions + bind offsets) — it can never grow a mesh. So the per-entry
            // runtime `scale` spreads a multi-fragment model's parts apart without resizing them. Custom models
            // should scale at BAKE time via the Factory's Size field; a full runtime resize needs the mesh-scale
            // engine (ScaleDescriptorMeshes, currently driven by unitScales rules). See docs/Unit-Size.md.
            // PROVEN 2026-07-19 (temp probe, removed): PawnDescriptorId is a per-pawn WRITABLE mesh selector the
            // GPU honors — AssaultInfantry pawns rendered another entry's mesh from a one-int write. This is the
            // foundation for the vanilla-in-combat feature: keep the donor's descriptor alive alongside ours and
            // flip each pawn by combat state.
            ctx.pawnEntries.SetValue(entry, ctx.idx);
            LogPoseHookOnce(ctx, e);
        }

        // This pawn's animation phase, held STEADY across LOD rebuilds. Identified by position (the entry has no
        // stable id and its array slot is reshuffled whenever zoom changes the LOD), then followed frame to frame
        // so a sailing pawn keeps the phase it was given. New pawns take the next value on the golden-ratio
        // sequence, which spreads arrivals evenly instead of clustering them the way count/total would.
        static float PhaseFor(ModelEntry e, object entry)
        {
            if (e.animPhaseSpread <= 0.0001f) return 0f;
            if (!TryGetTranslation(entry, out var pos)) return 0f;
            float now = UnityEngine.Time.time;
            lock (e.phaseTracks)
            {
                // throttled census: how many DISTINCT pawn positions this model actually sees. If a multi-pawn unit
                // reports one position for every pawn, position cannot identify them and the spread has nothing to
                // key on — that is the thing to read first when instances animate in lockstep.
                if (now - e.phaseLogAt > 60f)
                {
                    e.phaseLogAt = now;
                    var seenNow = e.phaseTracks.FindAll(t => now - t.seen < 0.5f);
                    Plugin.Diag($"[Phase] {e.resourceName} tracks={e.phaseTracks.Count} live={seenNow.Count} " +
                        string.Join(" ", seenNow.ConvertAll(t => $"[ph={t.phase:0.##}@({t.pos.x:0.#},{t.pos.z:0.#})]")));
                }
                for (int i = e.phaseTracks.Count - 1; i >= 0; i--)
                    if (now - e.phaseTracks[i].seen > 5f) e.phaseTracks.RemoveAt(i);   // pawn gone (died / unit despawned)
                int best = -1; float bestD = 0.75f * 0.75f;   // < formation spacing, >> per-frame travel
                for (int i = 0; i < e.phaseTracks.Count; i++)
                {
                    if (e.phaseTracks[i].seen == now) continue;   // already claimed by another pawn THIS frame
                    float d = (e.phaseTracks[i].pos - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best >= 0)
                {
                    e.phaseTracks[best].pos = pos;               // follow it
                    e.phaseTracks[best].seen = now;
                    return e.animPhaseSpread * e.phaseTracks[best].phase;
                }
                float ph = (e.phaseTracks.Count * 0.6180339887f) % 1f;
                e.phaseTracks.Add(new ModelEntry.PawnPhase { pos = pos, phase = ph, seen = now });
                Plugin.Diag($"[Phase] {e.resourceName} NEW track #{e.phaseTracks.Count - 1} phase={ph:0.###} at ({pos.x:0.##},{pos.y:0.##},{pos.z:0.##}) spread={e.animPhaseSpread:0.##}");
                return e.animPhaseSpread * ph;
            }
        }

        // The normalized pose time for one animated pawn, per the model's behavior: continuous loop (a spinning prop),
        // fire-once (rest at 0, one pass when this instance bombards), or deploy-on-stop (+ recoil overlay).
        static float ComputePoseTime(ModelEntry e, object entry, float dur, float phase)
        {
            // The phase belongs to the LOOP only. Deploy and fire-once are measured from the moment the unit
            // stopped or fired: shifting them would start the clip part-way through its own one-shot, so a gun
            // would snap to half-deployed. They stay on their trigger's clock.
            if (e.deployOnStop) return DeployPoseTime(e, entry, dur);
            if (e.fireOnAttack) return FireOncePoseTime(e, entry, dur);
            return UnityEngine.Time.time / dur + phase;                // default: continuous loop (a drone's spinning prop)
        }

        // STATE-DRIVEN (Phase 2): resolve this pawn's movement state from the samples ProcessAnimStates published,
        // matched by nearest sample position (the deploy poll's proven approximation). The caller drives the pose
        // WEIGHTS from it — moving -> the MOVE slot; recently stopped + an AFTER clip -> one 0->1 AFTER pass
        // (clamped below 1.0: Repeat(1.0)=frame 0 would snap back); otherwise IDLE. An unmatched pawn idles.
        // ATTACK (highest priority, independent of the movement samples): the ranged-fight hook (Hk_PawnRangedFight)
        // arms a FireInstance at the shooter's render position; the pawn nearest an unexpired one plays the attack
        // clip once — same position-match approximation the fire/deploy behaviors use.
        static void StatePose(ModelEntry e, object entry, out bool moving, out bool inAfter, out float afterT, out bool inAttack, out float attackT, out bool inCombat, out bool inPreMove, out float preMoveT, out bool inIdleAlt, out float idleAltT, out int idleAltId)
        {
            moving = false; inAfter = false; afterT = 0f; inAttack = false; attackT = 0f; inCombat = false; inPreMove = false; preMoveT = 0f; inIdleAlt = false; idleAltT = 0f; idleAltId = -1;
            if (!TryGetTranslation(entry, out var pos)) return;
            if (e.attackAnimId >= 0)
            {
                // PURE decision — Patches/PoseMath.cs, unit-tested. The lock stays here: the list is shared.
                lock (e.activeFires)
                    inAttack = PoseMath.AttackWindow(e.activeFires, pos, UnityEngine.Time.time,
                                                     e.attackDur, e.attackRepeats, out attackT);
                // WHEN THE ATTACK CLIP ACTUALLY STARTS PLAYING, and how far off the gun is at that moment. This is
                // the frame the player sees the kick begin, so it is the only timestamp that can settle "it fires
                // before it has turned" — everything else in this chain is an intention, not an observation.
                if (inAttack && !e.attackOpenLogged)
                {
                    e.attackOpenLogged = true;
                    Plugin.Diag($"[Fire] '{e.resourceName}': ATTACK CLIP STARTS (t={attackT:F2}, dur={e.attackDur:F2}s) " +
                                $"— gun is {TurnMisalignAt(pos):F1}deg off target");
                }
                else if (!inAttack) e.attackOpenLogged = false;   // re-arm for the next shot
            }
            PoseMath.StatePick pick;
            lock (e.stateSamples) pick = PoseMath.PickState(e.stateSamples, pos);
            if (!pick.Matched)
            {
                moving = false;
                return;
            }
            moving = pick.Moving; inCombat = pick.Combat;
            float stoppedAt = pick.StoppedAt, moveStartedAt = pick.MoveStartedAt;
            // BAKE-ONLY pacing (user decision 2026-07-19): the runtime plays every clip at its authored length —
            // pacing belongs in the DATA. A fold that outlasts a map move is authored shorter via a slice STEP
            // (deploy[179..0/3] = every 3rd frame = 3x faster), not scaled here.
            if (!moving && e.afterAnimId >= 0)   // one pass, holding the last frame until it elapses
                inAfter = PoseMath.OneShot(stoppedAt, UnityEngine.Time.time, e.afterDur, out afterT);
            // PRE-MOVEMENT one-shot: just STARTED moving (e.g. a howitzer folding its legs) — plays once, then the Move loop
            if (moving && e.preMoveAnimId >= 0)
                inPreMove = PoseMath.OneShot(moveStartedAt, UnityEngine.Time.time, e.preMoveDur, out preMoveT);
            bool combatIdle = inCombat && e.combatAnimId >= 0 && !moving && !inAfter && !inAttack;
            // IDLE-ALT (2026-07-23, the tiger's howl): an OCCASIONAL flavor one-shot while PLAIN idle — never during
            // move/attack/after/combat. One cadence per ENTRY (unit type): the pawn evaluated at due time becomes the
            // performer (its position is pinned; packmates keep idling — the animation twin of the growl's one-voice
            // radius). With BOTH alt clips baked, each firing picks randomly (howl now, eat later). Cadence = the
            // registry idleAltInterval, jittered 0.6-1.4x like the growl so it never reads as a metronome.
            if (!moving && !inAfter && !inAttack && !combatIdle && e.idleAltInterval > 0.01f && (e.idleAltAnimId >= 0 || e.idleAlt2AnimId >= 0))
            {
                float nowA = UnityEngine.Time.time;
                if (e.idleAltStart >= 0f)   // a firing is running — is THIS pawn the performer, and is it still inside the window?
                {
                    float dtA = nowA - e.idleAltStart;
                    if (dtA >= e.idleAltChosenDur) e.idleAltStart = -1f;
                    else if ((e.idleAltPos - pos).sqrMagnitude < 4f * 4f)
                    { inIdleAlt = true; idleAltT = UnityEngine.Mathf.Min(dtA / e.idleAltChosenDur, 0.999f); idleAltId = e.idleAltChosenId; }
                }
                else if (e.idleAltNextAt <= 0f)
                    e.idleAltNextAt = nowA + e.idleAltInterval * UnityEngine.Random.Range(0.6f, 1.4f);
                else if (nowA >= e.idleAltNextAt)
                {
                    bool both = e.idleAltAnimId >= 0 && e.idleAlt2AnimId >= 0;
                    bool second = e.idleAltAnimId < 0 || (both && UnityEngine.Random.value < 0.5f);
                    e.idleAltChosenId = second ? e.idleAlt2AnimId : e.idleAltAnimId;
                    float d = second ? e.idleAlt2Dur : e.idleAltDur;
                    e.idleAltChosenDur = d > 0.001f ? d : 1f;
                    e.idleAltStart = nowA; e.idleAltPos = pos;
                    e.idleAltNextAt = nowA + e.idleAltInterval * UnityEngine.Random.Range(0.6f, 1.4f);
                    inIdleAlt = true; idleAltT = 0f; idleAltId = e.idleAltChosenId;
                }
            }
        }

        // DEPLOY-ON-STOP (gradual): hold the pose time Plugin.Update ramped for THIS pawn's unit — the deploy clip plays
        // forward (legs spread) after the unit stops and rewinds (fold) while it moves. Match by nearest recorded pawn
        // position (Unity render coords); default deployPoseTime if unmatched (idle -> deployed).
        static float DeployPoseTime(ModelEntry e, object entry, float dur)
        {
            if (!TryGetTranslation(entry, out var dpos)) return e.deployPoseTime;   // member renamed by a game update -> degrade to the default pose instead of throwing per pawn per frame
            float poseTime;
            // PURE decision — Patches/PoseMath.cs, unit-tested (note the 3u radius, tighter than the fire match).
            lock (e.deploySamples) poseTime = PoseMath.NearestDeployPose(e.deploySamples, dpos, e.deployPoseTime);
            // RECOIL-ON-FIRE overlay: when this howitzer is DEPLOYED (held near deployPoseTime) AND it just fired, sweep the
            // pose time up through the recoil tail once. The clip's tail after deployPoseTime is the extracted kickback.
            if (e.fireOnAttack && poseTime >= e.deployPoseTime * 0.9f) poseTime = RecoilOverlay(e, dpos, dur, poseTime);
            return poseTime;
        }

        // Sweep the pose time up through the RECOIL TAIL [deployPoseTime, ~1) once from this pawn's nearest active fire, then
        // fall back to the deployed hold. Same per-instance fire match as fire-once (nearest active fire by render position).
        static float RecoilOverlay(ModelEntry e, UnityEngine.Vector3 dpos, float dur, float poseTime)
        {
            float bestStartF;
            lock (e.activeFires) bestStartF = PoseMath.NearestFireStart(e.activeFires, dpos, PoseMath.FireMatchRadiusSq);
            if (bestStartF < 0f) return poseTime;
            // PURE decision — Patches/PoseMath.cs, unit-tested.
            if (PoseMath.RecoilSweep(UnityEngine.Time.time - bestStartF, dur, e.deployPoseTime, e.recoilSpeed,
                                     out float swept, out float recoilDur))
            {
                poseTime = swept;
                if (recoilLogStart != bestStartF)
                { recoilLogStart = bestStartF; Plugin.Diag($"[Deploy-Fire] '{e.resourceName}' RECOIL sweep (dur={recoilDur:0.00}s, poseTime {e.deployPoseTime:0.00}->{PoseMath.RecoilMax})"); }
            }
            return poseTime;
        }

        // FIRE-ONCE, PER-INSTANCE: rest at frame 0 unless THIS pawn is the one that bombarded. Match this pawn to the nearest
        // active fire by ObjectSpace position (both Unity render coords) and play one 0->1 pass from that fire's start time.
        // Only the firer animates — every other howitzer of the type stays at rest.
        static float FireOncePoseTime(ModelEntry e, object entry, float dur)
        {
            float poseTime = 0f;
            if (!TryGetTranslation(entry, out var tpos)) return 0f;   // renamed member -> rest at frame 0 instead ofthrowing per pawn per frame
            // PURE decision — Patches/PoseMath.cs, unit-tested. A pawn within 4u of a fire is the firer (tiles are
            // spaced wider). This used to seed `best` with float.MaxValue and range-check afterwards, where the
            // recoil overlay seeded it with the radius; the two are equivalent and PoseMathTests pins that.
            float bestStart;
            lock (e.activeFires) bestStart = PoseMath.NearestFireStart(e.activeFires, tpos, PoseMath.FireMatchRadiusSq);
            if (bestStart >= 0f)
                poseTime = PoseMath.FireOncePose(UnityEngine.Time.time - bestStart, dur);   // one pass, then rest
            return poseTime;
        }

    }
}
