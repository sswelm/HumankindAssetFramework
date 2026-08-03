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
        static HashSet<string> animDiagDone;  // entries already dumped by the one-shot [AnimDiag]

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
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[AnimDiag] " + ex); }
        }

        static bool? anyAnimated;        // cached early-out: skip the per-pawn hook if no model is animated
        static bool? anyMuzzle;          // cached early-out: skip the muzzle-redirect GetBoneTRS hook if no model has a muzzleBone
        static bool? anyFreeze;          // cached early-out: skip the per-pawn hook if no model wants its donor animation frozen
        static bool? anyRescuable;       // any entry repointed onto our skeleton — the rescue must run even for a purely STATIC pack (no pose behaviour)
        static bool rescueLogged, posLogged, poseErrLogged, scaleLogged;
        static HashSet<string> poseHookSeen;   // dump the pose-hook + runtime transform once PER MODEL (so the howitzer logs even if the drone spawns first)
        static readonly HashSet<string> unseededLogged = new HashSet<string>();   // one warning per entry for the disarmed-net state
        static HashSet<int> freezeLogSkels;   // distinct skeleton ids we've logged a freeze for (so a second-instance "twin via descriptor" shows up in the log without spamming)
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
        static ModelEntry HookedEntryFor(int skeletonId)
        {
            if (entries == null || skeletonId < 0) return null;
            foreach (var e in entries) if (Rescuable(e) && e.skeletonId == skeletonId) return e;
            return null;
        }

        // Precomputed member names (perf pass 2026-07-19): "Pose" + i / "BoneRotation" + i built fresh STRINGS per
        // pawn per FRAME on the game's hottest loop — thousands of small allocations a second at scale, pure GC churn.
        static readonly string[] PoseNames = { "Pose0", "Pose1", "Pose2", "Pose3", "Pose4", "Pose5", "Pose6", "Pose7", "Pose8" };
        static readonly string[] BoneRotationNames = { "BoneRotation0", "BoneRotation1", "BoneRotation2", "BoneRotation3" };

        // Per-pawn state read once at the top of the hook and threaded through the behavior handlers. `entry` is the boxed
        // PawnEntry struct — every SetMember mutates that one box, and the handler writes it back via pawnEntries.SetValue.
        struct PawnCtx { public Array pawnEntries; public int idx; public object entry; public int skelId; public int descId; public int pawnCount; }

        // The game just wrote pawnEntries[pawnCount-1]. Match it to one of our models and hand off to the behavior that model
        // wants: FREEZE (pin the donor clip to frame 0) or an ANIMATED pose whose time is driven by loop / fire-once / deploy.
        // Each behavior is its own method below, so adding a new one is a new handler — not another branch on this hot path.
        internal static void OnPawnAdded(object pawnManager)
        {
            try
            {
                if (anyAnimated == null) anyAnimated = entries != null && entries.Any(x => x.ca != 0 || x.cb != 0 || x.cc != 0 || x.cd != 0);
                if (anyFreeze == null) anyFreeze = entries != null && entries.Any(x => x.freezeDonorAnim);
                // anyRescuable keeps a purely STATIC pack in the hook: those entries have no pose behaviour, so the
                // two flags above are both false, yet they still need the wrong-skeleton rescue. Recomputed when an
                // entry is repointed and on session reset — `repointed` flips at runtime, so it cannot be latched.
                if (anyRescuable == null) anyRescuable = entries != null && entries.Any(Rescuable);
                if ((anyAnimated != true && anyFreeze != true && anyRescuable != true && unitScaleByDesc.Count == 0) || !Plugin.UniversalInjectOn.Value) return;
                if (!TryReadLastPawn(pawnManager, out var ctx)) return;
                if (!knownManagers.Contains(pawnManager)) knownManagers.Add(pawnManager);   // every manager, incl. ones only adding vanilla pawns — the sweep needs them all

                // RESIZE LAB: a vanilla pawn (no model entry) whose descriptor has a resolved scale rule gets its
                // ObjectSpace.Scale multiplied ONCE at spawn — the same mechanism the per-entry `scale` field uses.
                if (unitScaleByDesc.Count > 0 && unitScaleByDesc.TryGetValue(ctx.descId, out var vInfo) && HookedEntryFor(ctx.skelId) == null)
                    ApplyVanillaScale(ctx, vInfo);   // MESH-SCALE engine: verts x s (on change) + ObjectSpace.Scale (per frame)

                // Match this pawn to one of our entries (animated OR freeze-static) by OUR baked skeleton id (the correctly
                // skinned pawn), else by the descriptor learned from that first correct pawn. The game spawns a unit's LATER
                // instances on a different vanilla skeleton; without the descriptor fallback only the first instance is
                // handled and the rest slip through (animating / rocking on the donor's rig).
                var e = HookedEntryFor(ctx.skelId);
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

                ForceOurSkeleton(ctx, e);
                SweepForStrays(ctx, e);   // stale same-descriptor slots the game no longer rewrites (the ghost-donor fix)

                // FREEZE (static): no clip of our own — pin the donor pose to frame 0 and stop. ANIMATED: play our clip on Pose0.
                // NEITHER: a purely static repointed model. It reaches here only since the rescue was widened past
                // `Hooked`; it wants the skeleton force and nothing else, so persist the entry and leave the pose
                // alone. Sending it down the animated path would write Pose0 with animId -1. The explicit write-back
                // matters because ForceOurSkeleton only mutates the boxed struct — the pose handlers are what
                // normally store it, and this branch runs neither.
                if (e.freezeDonorAnim && e.animId < 0) ApplyFreeze(ctx, e);
                else if (e.animId >= 0) ApplyAnimatedPose(ctx, e);
                else ctx.pawnEntries.SetValue(ctx.entry, ctx.idx);
            }
            // one-shot log: a bare catch here hid member renames after a game update (models just stopped animating, no clue why).
            catch (Exception ex) { InjectionErrors++; if (!poseErrLogged) { poseErrLogged = true; Plugin.Log.LogError("[Uni] OnPawnAdded (pose hook disabled this pawn): " + ex); } }
        }

        // Read the just-written PawnEntry (pawnCount-1) + its skeleton/descriptor ids, or false if there's nothing to act on.
        static bool TryReadLastPawn(object pawnManager, out PawnCtx ctx)
        {
            ctx = default;
            var pawnEntries = GetMember(pawnManager, "pawnEntries") as Array;
            if (pawnEntries == null) return false;
            int pawnCount = Convert.ToInt32(GetMember(pawnManager, "pawnCount"));
            if (pawnCount <= 0 || pawnCount > pawnEntries.Length) return false;
            int idx = pawnCount - 1;
            var entry = pawnEntries.GetValue(idx);                     // boxed PawnEntry (struct)
            ctx = new PawnCtx
            {
                pawnEntries = pawnEntries, idx = idx, entry = entry,
                skelId = Convert.ToInt32(GetMember(entry, "SkeletonId")),
                descId = Convert.ToInt32(GetMember(entry, "PawnDescriptorId")),
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
        static readonly Dictionary<string, float> sweepLast = new Dictionary<string, float>();
        static readonly HashSet<string> sweepScanLogged = new HashSet<string>();
        static int sweepFixLogged;
        // EVERY pawn manager the hook has ever seen (reference-identity; a handful — the map's plus per-battle ones).
        // The hook fires per-manager as pawns are ADDED, so a manager whose buffer was written once and never re-added
        // (a stale PresentationUnit from the load/respawn path) would never be swept via ctx alone — its stale slots
        // keep rendering donor visuals forever. Sweeping every known manager closes that hole. Cleared on session reset.
        internal static readonly List<object> knownManagers = new List<object>();
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

        // FORCE our skeleton so this pawn skins by OUR rig. A LATER instance the game spawned on a vanilla skeleton would
        // otherwise draw mis-skinned (animated) or WARP when we pin a foreign skeleton's frame 0 (freeze — the vertical,
        // "shape-shifting" airship). Shared by both paths, so every instance ends up on our skeleton.
        static void ForceOurSkeleton(PawnCtx ctx, ModelEntry e)
        {
            if (ctx.skelId == e.skeletonId) return;
            SetMember(ctx.entry, "SkeletonId", e.skeletonId);
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
        static readonly Dictionary<string, float> pawnLiveLast = new Dictionary<string, float>();

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
            var pose0 = GetMember(entry, "Pose0");                     // boxed PawnEntryPose (struct)
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
            if (e.animStateDriven && e.moveAnimId >= 0)
            {
                StatePose(e, entry, out bool moving, out bool inAfter, out float afterT, out bool inAttack, out float attackT, out bool inCombat, out bool inPreMove, out float preMoveT, out bool inIdleAlt, out float idleAltT, out int idleAltId);
                if (inAttack)
                {
                    // ATTACK wins over every other state: the pawn just fired (ranged-fight hook armed a window at
                    // its position) — one clamped 0->1 pass of the attack clip, holding the last frame until it elapses.
                    SetMember(pose0, "AnimationId", (uint)e.attackAnimId);
                    SetMember(pose0, "Time", attackT);
                }
                else if (inPreMove)
                {
                    // PRE-MOVEMENT: the unit just STARTED moving — one clamped pass (e.g. the howitzer folding),
                    // then the Move loop takes over when the window elapses.
                    SetMember(pose0, "AnimationId", (uint)e.preMoveAnimId);
                    SetMember(pose0, "Time", preMoveT);
                }
                else if (moving)
                {
                    float md = e.moveDur > 0.001f ? e.moveDur : 1f;
                    SetMember(pose0, "AnimationId", (uint)e.moveAnimId);
                    SetMember(pose0, "Time", UnityEngine.Time.time / md + phase);
                }
                else if (inAfter)
                {
                    SetMember(pose0, "AnimationId", (uint)e.afterAnimId);
                    SetMember(pose0, "Time", afterT);
                }
                else if (inCombat && e.combatAnimId >= 0)
                {
                    // COMBAT-IDLE: the army is locked in a battle — hold the combat stance instead of the relaxed idle.
                    // (A single-frame stance clip renders fine: FrameCount 1 pins the sampler to frame 0 at any Time.)
                    float cd = e.combatDur > 0.001f ? e.combatDur : 1f;
                    SetMember(pose0, "AnimationId", (uint)e.combatAnimId);
                    SetMember(pose0, "Time", UnityEngine.Time.time / cd + phase);
                }
                else if (inIdleAlt && idleAltId >= 0)
                {
                    // IDLE-ALT: the occasional flavor one-shot (howl/eat) — one clamped 0->1 pass at the performer
                    // pawn, then back to the normal idle. Never fires outside plain idle (StatePose gates it).
                    SetMember(pose0, "AnimationId", (uint)idleAltId);
                    SetMember(pose0, "Time", idleAltT);
                }
                else if (e.idleAnimId >= 0)
                {
                    // IDLE OVERRIDE role (bake-only stance fix, 2026-07-19): a stance baked as the PRIMARY clip
                    // encodes ~identity against the skeleton's reference pose (the primary defines that reference)
                    // and renders as REST in-game — the "forgot to deploy" trap. The stance therefore bakes as a
                    // ROLE (real deltas against the full primary clip's reference) and idle plays it here; the
                    // primary (e.animId) stays the full reference clip and is what plays when no override is set.
                    float idleDur = e.idleDur > 0.001f ? e.idleDur : 1f;
                    SetMember(pose0, "AnimationId", (uint)e.idleAnimId);
                    SetMember(pose0, "Time", UnityEngine.Time.time / idleDur + phase);
                }
                else
                {
                    float idleDur = e.animDuration > 0.001f ? e.animDuration : 1f;
                    SetMember(pose0, "AnimationId", (uint)e.animId);
                    SetMember(pose0, "Time", UnityEngine.Time.time / idleDur + phase);
                }
                SetMember(pose0, "Weight", 1f);
                SetMember(entry, "Pose0", pose0);
            }
            else
            {
                float dur = e.animDuration > 0.001f ? e.animDuration : 1f;
                SetMember(pose0, "AnimationId", (uint)e.animId);
                SetMember(pose0, "Weight", 1f);
                SetMember(pose0, "Time", ComputePoseTime(e, entry, dur, phase));
                SetMember(entry, "Pose0", pose0);
            }
            for (int i = 1; i < 9; i++)
            {
                var pose = GetMember(entry, PoseNames[i]);
                if (pose == null) continue;
                SetMember(pose, "Weight", 0f);
                SetMember(entry, PoseNames[i], pose);
            }
            // The AIM layer is cleared only for the ARTILLERY behaviors (it twists the howitzer's barrel as the game
            // aims). For other animated models the game's bone-rotation layer stays — it carries the pawn's FACING
            // (clearing it froze the soldier to one compass direction) — but on some donors it arrives with an
            // INVALID bone index (0xFFFFFFFF) and RUNAWAY angles (1558°…): those magnitudes deform the rig (the
            // soldier's ripped-off head). SanitizeAimLayer wraps such angles into 0..360 — same orientation, sane
            // magnitude — instead of zeroing (which would kill facing).
            if (((e.fireOnAttack || e.deployOnStop) && !e.animStateDriven) || e.clearAimLayer) ClearAimLayer(entry);   // legacy artillery rule, OR the explicit per-model knob (a STATE-DRIVEN howitzer still needs the donor's aim/wheel junk cleared; characters keep the layer for facing)
            else if (!string.IsNullOrEmpty(e.turretBone)) TurretizeAimLayer(entry, e);   // retarget the game's aim/heading angle onto OUR turret bone (a vehicle turret tracks the target)
            else SanitizeAimLayer(entry);
            ApplyPositionOffset(e, entry);
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
            LogPoseHookOnce(ctx, e, pose0);
        }

        // This pawn's animation phase, held STEADY across LOD rebuilds. Identified by position (the entry has no
        // stable id and its array slot is reshuffled whenever zoom changes the LOD), then followed frame to frame
        // so a sailing pawn keeps the phase it was given. New pawns take the next value on the golden-ratio
        // sequence, which spreads arrivals evenly instead of clustering them the way count/total would.
        static float PhaseFor(ModelEntry e, object entry)
        {
            if (e.animPhaseSpread <= 0.0001f) return 0f;
            var os = GetMember(entry, "ObjectSpace");
            if (os == null) return 0f;
            UnityEngine.Vector3 pos;
            try { pos = (UnityEngine.Vector3)GetMember(os, "Translation"); } catch { return 0f; }
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
            var os = GetMember(entry, "ObjectSpace");
            if (os == null) return;
            UnityEngine.Vector3 pos;
            try { pos = (UnityEngine.Vector3)GetMember(os, "Translation"); } catch { return; }
            if (e.attackAnimId >= 0)
            {
                float atd = e.attackDur > 0.001f ? e.attackDur : 1f;
                // attackRepeats: the window spans N passes of the clip; Time is fed UNCLAMPED (dt/clipDur) and the
                // sampler's Repeat(Time,1) wraps it, so the clip replays each pass — sustained fire from a
                // single-pop source clip. repeats=1 degenerates to the original clamped one-shot.
                int rep = e.attackRepeats > 0 ? e.attackRepeats : 1;
                float win = atd * rep;
                float nowT = UnityEngine.Time.time;
                lock (e.activeFires)
                    for (int i = 0; i < e.activeFires.Count; i++)
                    {
                        float dtF = nowT - e.activeFires[i].startTime;
                        if (dtF < 0f || dtF >= win) continue;
                        if ((e.activeFires[i].pos - pos).sqrMagnitude < 4f * 4f)
                        { inAttack = true; attackT = UnityEngine.Mathf.Min(dtF / atd, rep - 0.001f); break; }
                    }
            }
            float stoppedAt = -1f, moveStartedAt = -1f; bool matched = false;
            lock (e.stateSamples)
            {
                // Proximity-weighted MAJORITY over the samples in the 4u radius, NOT the single nearest. Samples are
                // pooled per model TYPE (there is no per-unit id — the pawn entry's array slot reshuffles on LOD), so
                // two SAME-TYPE units within the radius (one moving, one idle) could have a pawn match the NEIGHBOUR's
                // nearest sample and play the wrong clip. Weighting by proximity (w = R^2 - d^2) lets a pawn deep in
                // its own formation be carried by its mates instead of flipped by a single closer neighbour sample.
                // IDENTICAL to the old nearest-sample pick whenever the in-radius samples AGREE (the common case:
                // one non-zero side, whose nearest sample IS the overall nearest).
                const float R2 = 4f * 4f;   // same 4u match radius class as the fire/deploy hooks
                float wMove = 0f, wIdle = 0f, dMove = float.MaxValue, dIdle = float.MaxValue;
                StateSample sMove = default, sIdle = default;
                for (int i = 0; i < e.stateSamples.Count; i++)
                {
                    var s = e.stateSamples[i];
                    float d = (s.pos - pos).sqrMagnitude;
                    if (d >= R2) continue;
                    float w = R2 - d;   // proximity weight (0 at the radius edge, heaviest at the pawn's own position)
                    if (s.moving) { wMove += w; if (d < dMove) { dMove = d; sMove = s; } }
                    else          { wIdle += w; if (d < dIdle) { dIdle = d; sIdle = s; } }
                }
                if (wMove > 0f || wIdle > 0f)
                {
                    matched = true;
                    // representative = the winning state's NEAREST sample (carries that unit's stoppedAt/combat); tie -> nearest overall
                    var pick = wMove > wIdle ? sMove : wIdle > wMove ? sIdle : (dMove <= dIdle ? sMove : sIdle);
                    moving = pick.moving; stoppedAt = pick.stoppedAt; moveStartedAt = pick.moveStartedAt; inCombat = pick.combat;
                }
            }
            if (!matched)
            {
                moving = false;
                return;
            }
            // BAKE-ONLY pacing (user decision 2026-07-19): the runtime plays every clip at its authored length —
            // pacing belongs in the DATA. A fold that outlasts a map move is authored shorter via a slice STEP
            // (deploy[179..0/3] = every 3rd frame = 3x faster), not scaled here.
            if (!moving && e.afterAnimId >= 0 && stoppedAt > 0f)
            {
                float ad = e.afterDur > 0.001f ? e.afterDur : 1f;
                float dt = UnityEngine.Time.time - stoppedAt;
                if (dt >= 0f && dt < ad) { inAfter = true; afterT = UnityEngine.Mathf.Min(dt / ad, 0.999f); }   // one pass, hold the last frame until it elapses
            }
            // PRE-MOVEMENT one-shot: just STARTED moving (e.g. a howitzer folding its legs) — plays once, then the Move loop
            if (moving && e.preMoveAnimId >= 0 && moveStartedAt > 0f)
            {
                float pd = e.preMoveDur > 0.001f ? e.preMoveDur : 1f;
                float dtp = UnityEngine.Time.time - moveStartedAt;
                if (dtp >= 0f && dtp < pd) { inPreMove = true; preMoveT = UnityEngine.Mathf.Min(dtp / pd, 0.999f); }
            }
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
            var osD = GetMember(entry, "ObjectSpace");
            UnityEngine.Vector3 dpos;
            try { dpos = (UnityEngine.Vector3)GetMember(osD, "Translation"); } catch { return e.deployPoseTime; }   // member renamed by a game update -> degrade to the default pose instead of throwing per pawn per frame
            float poseTime = e.deployPoseTime;
            float bestSqD = 3f * 3f;
            lock (e.deploySamples)
                for (int i = 0; i < e.deploySamples.Count; i++)
                {
                    float d = (e.deploySamples[i].pos - dpos).sqrMagnitude;
                    if (d < bestSqD) { bestSqD = d; poseTime = e.deploySamples[i].poseTime; }
                }
            // RECOIL-ON-FIRE overlay: when this howitzer is DEPLOYED (held near deployPoseTime) AND it just fired, sweep the
            // pose time up through the recoil tail once. The clip's tail after deployPoseTime is the extracted kickback.
            if (e.fireOnAttack && poseTime >= e.deployPoseTime * 0.9f) poseTime = RecoilOverlay(e, dpos, dur, poseTime);
            return poseTime;
        }

        // Sweep the pose time up through the RECOIL TAIL [deployPoseTime, ~1) once from this pawn's nearest active fire, then
        // fall back to the deployed hold. Same per-instance fire match as fire-once (nearest active fire by render position).
        static float RecoilOverlay(ModelEntry e, UnityEngine.Vector3 dpos, float dur, float poseTime)
        {
            float bestSqF = 4f * 4f, bestStartF = -1f;
            lock (e.activeFires)
                for (int i = 0; i < e.activeFires.Count; i++)
                {
                    float d = (e.activeFires[i].pos - dpos).sqrMagnitude;
                    if (d < bestSqF) { bestSqF = d; bestStartF = e.activeFires[i].startTime; }
                }
            if (bestStartF < 0f) return poseTime;
            const float recoilMax = 0.999f;                            // stay below 1.0 (Mathf.Repeat wraps 1.0 -> frame 0 = folded)
            float rspd = e.recoilSpeed > 0f ? e.recoilSpeed : 1f;
            float recoilDur = dur * (recoilMax - e.deployPoseTime) / rspd;   // tail duration at authored speed, sped up by recoilSpeed
            float elapsedF = UnityEngine.Time.time - bestStartF;
            if (recoilDur > 0.0001f && elapsedF < recoilDur)
            {
                poseTime = e.deployPoseTime + (elapsedF / recoilDur) * (recoilMax - e.deployPoseTime);
                if (recoilLogStart != bestStartF)
                { recoilLogStart = bestStartF; Plugin.Diag($"[Deploy-Fire] '{e.resourceName}' RECOIL sweep (dur={recoilDur:0.00}s, poseTime {e.deployPoseTime:0.00}->{recoilMax}, matchDist={UnityEngine.Mathf.Sqrt(bestSqF):0.0}u)"); }
            }
            return poseTime;
        }

        // FIRE-ONCE, PER-INSTANCE: rest at frame 0 unless THIS pawn is the one that bombarded. Match this pawn to the nearest
        // active fire by ObjectSpace position (both Unity render coords) and play one 0->1 pass from that fire's start time.
        // Only the firer animates — every other howitzer of the type stays at rest.
        static float FireOncePoseTime(ModelEntry e, object entry, float dur)
        {
            float poseTime = 0f;
            var osT = GetMember(entry, "ObjectSpace");
            UnityEngine.Vector3 tpos;
            try { tpos = (UnityEngine.Vector3)GetMember(osT, "Translation"); } catch { return 0f; }   // renamed member -> rest at frame 0 instead of throwing per pawn per frame
            float bestSq = float.MaxValue, bestStart = -1f;
            lock (e.activeFires)
            {
                for (int i = 0; i < e.activeFires.Count; i++)
                {
                    float d = (e.activeFires[i].pos - tpos).sqrMagnitude;
                    if (d < bestSq) { bestSq = d; bestStart = e.activeFires[i].startTime; }
                }
            }
            const float matchRadiusSq = 4f * 4f;                       // a pawn within 4u of a fire is the firer (tiles are spaced wider)
            if (bestStart >= 0f && bestSq <= matchRadiusSq)
            {
                float elapsed = UnityEngine.Time.time - bestStart;
                poseTime = elapsed >= dur ? 0f : elapsed / dur;        // one pass, then rest (Update prunes finished fires)
            }
            return poseTime;
        }

    }
}
