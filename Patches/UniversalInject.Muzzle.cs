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
        // TURRETIZE: aim a vehicle turret at the target by hijacking the game's OWN aim layer. The engine streams a
        // HEADING angle (AxisIndex 1 = up) into a BoneRotation slot whose SkeletonBoneIndex is the INVALID sentinel on
        // our injected models (so it hits nothing). We repoint that slot at OUR turret bone — the engine's aim yaw then
        // rotates the turret, no per-frame trig. Wheel-spin junk (axis 0) is still zeroed (our wheels are baked). The
        // turret bone index is resolved once from the skeleton's BoneInfos (same substring match as the hand prop).
        static bool turretLogged;
        static void TurretizeAimLayer(object entry, ModelEntry e)
        {
            if (e.turretBoneIdx == -2)   // resolve once
            {
                e.turretBoneIdx = -1;
                if (e.skeleton != null && GetMember(e.skeleton, "BoneInfos") is Array bones)
                    for (int i = 0; i < bones.Length; i++)
                    {
                        var n = GetMember(bones.GetValue(i), "Name")?.ToString() ?? "";
                        if (n.IndexOf(e.turretBone, StringComparison.OrdinalIgnoreCase) >= 0) { e.turretBoneIdx = i; break; }
                    }
                Plugin.Diag($"[Turret] '{e.resourceName}' turret bone '{e.turretBone}' -> skeleton index {e.turretBoneIdx}");
            }
            if (e.turretBoneIdx < 0) { SanitizeAimLayer(entry); return; }   // no such bone — fall back to the safe path
            for (int i = 0; i < 4; i++)
            {
                var br = GetMember(entry, BoneRotationNames[i]);
                if (br == null) continue;
                long axis, boneIdx;
                try { axis = Convert.ToInt64(GetMember(br, "AxisIndex")); boneIdx = Convert.ToInt64(GetMember(br, "SkeletonBoneIndex")); } catch { continue; }
                bool invalid = boneIdx < 0 || boneIdx >= 100000;           // the 0xFFFFFFFF sentinel (aim meant for us)
                if (!invalid)
                {
                    // DONOR-INDEXED JUNK (user diagnosis 2026-07-26: "the engine applies extra modifiers"): a
                    // VALID index here was computed for the DONOR skeleton (wheel-spin/aim channels on donor
                    // wheel bones 0..33) — on OUR replaced skeleton that index lands on an arbitrary bone
                    // (a tread link! a shuttle!) and the streamed angle continuously rotates it: the band
                    // pushed off the wheels, the idle micro-twitch, and the phantom "bone ceiling" (rig size
                    // shifts which bone is the victim). Unless it happens to BE our turret, zero it.
                    if (boneIdx != e.turretBoneIdx)
                    {
                        float aj; try { aj = Convert.ToSingle(GetMember(br, "Angle")); } catch { continue; }
                        if (aj != 0f) { SetMember(br, "Angle", 0f); SetMember(entry, BoneRotationNames[i], br); }
                    }
                    continue;
                }
                if (axis == 1)                                             // HEADING channel -> aim our turret
                {
                    SetMember(br, "SkeletonBoneIndex", (uint)e.turretBoneIdx);
                    if (e.turretAxis >= 0)                                  // override the rotation axis (yaw for a turret, pitch for an artillery barrel)
                    {
                        object curAx = GetMember(br, "AxisIndex"); Type axT = curAx?.GetType();
                        object newAx = (axT != null && axT.IsEnum) ? Enum.ToObject(axT, e.turretAxis)
                                     : (axT != null) ? Convert.ChangeType((long)e.turretAxis, axT)
                                     : (object)(uint)e.turretAxis;
                        SetMember(br, "AxisIndex", newAx);
                    }
                    SetMember(entry, BoneRotationNames[i], br);
                    if (!turretLogged) { turretLogged = true; float ang = 0f; try { ang = Convert.ToSingle(GetMember(br, "Angle")); } catch { }
                        Plugin.Diag($"[Turret] '{e.resourceName}' slot {i} heading angle {ang:0.#}° -> bone {e.turretBoneIdx}, axis {(e.turretAxis >= 0 ? e.turretAxis.ToString() : "keep")}"); }
                }
                else                                                       // wheel-spin (axis 0) etc. -> zero
                {
                    float a; try { a = Convert.ToSingle(GetMember(br, "Angle")); } catch { continue; }
                    if (a != 0f) { SetMember(br, "Angle", 0f); SetMember(entry, BoneRotationNames[i], br); }
                }
            }
        }

        // ---- MUZZLE-RELOCATE (2026-07-24) — anchor the weapon muzzle-flash on OUR unit ----
        // The donor's fire clip fires the muzzle via a FireProjectile mecanim event; AlterationFireProjectile.StartEvent does
        //   startPosition = SubPawn.GetBoneTRS(mecanimEvent.ParentNameToLaunchVFXPosition).Transform(PositionToLaunchVFX)
        // where that bone name is the DONOR's weapon socket (an AA gun's "Canon"). RepointMatch put OUR skeleton on the addon,
        // so that donor name isn't found -> GetBoneTRS falls back to the pawn ROOT (+ the donor's socket-local offset) and the
        // flash lands off-side. We redirect the lookup: when a SubPawn of ours asks GetBoneTRS for a bone absent from our rig
        // and the entry carries a muzzleBone, hand back OUR muzzle bone's TRS instead (found -> the real path, no re-redirect).
        static MethodBase gocaMethod;   // PresentationPawnDefinitionAddOn.GetOrCreateAddOn(PresentationPawnDefinition)
        static bool gocaResolved, muzzleErrLogged;
        static readonly HashSet<string> muzzleSeen = new HashSet<string>();   // diagnostic: first distinct bone names through the hook
        static readonly Dictionary<Type, MethodBase> boneIdxMethods = new Dictionary<Type, MethodBase>();

        // Full bone name for e.muzzleBone (substring) against OUR skeleton, cached in e.muzzleBoneName. null = unset / not found.
        static string ResolveMuzzleBoneName(ModelEntry e)
        {
            if (e.muzzleBoneName != null) return e.muzzleBoneName.Length == 0 ? null : e.muzzleBoneName;
            e.muzzleBoneName = "";
            if (e.skeleton != null && !string.IsNullOrEmpty(e.muzzleBone) && GetMember(e.skeleton, "BoneInfos") is Array bones)
                for (int i = 0; i < bones.Length; i++)
                {
                    var n = GetMember(bones.GetValue(i), "Name")?.ToString() ?? "";
                    if (n.IndexOf(e.muzzleBone, StringComparison.OrdinalIgnoreCase) >= 0) { e.muzzleBoneName = n; break; }
                }
            Plugin.Diag($"[Muzzle] '{e.resourceName}' muzzle bone '{e.muzzleBone}' -> '{(e.muzzleBoneName.Length == 0 ? "(not found)" : e.muzzleBoneName)}'");
            return e.muzzleBoneName.Length == 0 ? null : e.muzzleBoneName;
        }

        // True if boneName resolves on this Amplitude skeleton (GetBoneIndex >= 0).
        static bool SkelHasBone(object skel, string boneName)
        {
            if (skel == null || string.IsNullOrEmpty(boneName)) return false;
            var t = skel.GetType();
            if (!boneIdxMethods.TryGetValue(t, out var m)) { m = AccessTools.Method(t, "GetBoneIndex", new[] { typeof(string) }); boneIdxMethods[t] = m; }
            if (m == null) return false;
            try { return Convert.ToInt32(m.Invoke(skel, new object[] { boneName })) >= 0; } catch { return false; }
        }

        // The entry (with a muzzleBone) whose skeleton this SubPawn renders on. RepointMatch set addon.Skeleton = e.skeleton,
        // so a reference match is exact and only our repointed pawns qualify.
        static ModelEntry MuzzleEntryForSubPawn(object subPawn)
        {
            // NAME MATCH first — proven live by the [Muzzle] diagnostic: the fire path asked for the donor socket
            // ('Canon_Up_left') on sub-pawn "'64727-32-0'- Era5_Common_ArmouredCar_01" with entry=none — the AddOn
            // skeleton reference walk below does NOT round-trip for the firing sub-pawn, but its GameObject name
            // carries the pawn description (the same match the audio poll relies on).
            string goName = (subPawn as UnityEngine.Component)?.gameObject?.name;
            if (!string.IsNullOrEmpty(goName))
                for (int i = 0; i < entries.Count; i++)
                {
                    var en = entries[i];
                    if (!string.IsNullOrEmpty(en.muzzleBone) && !string.IsNullOrEmpty(en.pawnDescription)
                        && goName.IndexOf(en.pawnDescription, StringComparison.OrdinalIgnoreCase) >= 0) return en;
                }
            var pawnDef = GetMember(subPawn, "PresentationPawnDefinition");
            if (pawnDef == null) return null;
            if (!gocaResolved)
            {
                gocaResolved = true;
                var addOnT = AccessTools.TypeByName("Amplitude.Mercury.Animation.PresentationPawnDefinitionAddOn");
                gocaMethod = addOnT != null ? AccessTools.Method(addOnT, "GetOrCreateAddOn") : null;
            }
            if (gocaMethod == null) return null;
            object addOn; try { addOn = gocaMethod.Invoke(null, new[] { pawnDef }); } catch { return null; }
            var skel = GetMember(addOn, "Skeleton");
            if (skel == null) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!string.IsNullOrEmpty(e.muzzleBone) && ReferenceEquals(e.skeleton, skel)) return e;
            }
            return null;
        }

        // Hook body: if this GetBoneTRS(boneName) is a donor socket missing on our rig, answer with OUR muzzle bone's TRS.
        // Returns true if handled (result set, caller skips the original); false to run the original untouched.
        static bool muzzleReentry;   // the native-socket branch re-invokes GetBoneTRS with the SAME name — without this the prefix re-enters itself forever (stack overflow, hard crash to desktop; 2026-07-24 field incident)
        internal static bool MuzzleRedirect(object subPawn, string boneName, MethodBase getBoneTRS, ref object result)
        {
            try
            {
                if (muzzleReentry) return false;   // inner call from our own branch below — run the original untouched
                if (string.IsNullOrEmpty(boneName) || entries == null || entries.Count == 0) return false;
                if (anyMuzzle == null) anyMuzzle = entries.Any(x => !string.IsNullOrEmpty(x.muzzleBone));   // cached early-out (was an entries loop on EVERY GetBoneTRS/VFX lookup during combat); reset on registry publish
                if (!anyMuzzle.Value) return false;
                var e = MuzzleEntryForSubPawn(subPawn);
                // DIAGNOSTIC (temporary, first 12 distinct names): what does the fire path actually ask GetBoneTRS for,
                // and does the sub-pawn->entry match work? Zero [Muzzle] lines while flashes stay off-side = the redirect
                // never engages; this shows whether the CALL is missing or the MATCH is failing.
                if (muzzleSeen.Count < 12 && muzzleSeen.Add(boneName))
                    Plugin.Diag($"[Muzzle] GetBoneTRS('{boneName}') subPawn='{(subPawn as UnityEngine.Component)?.gameObject?.name ?? "?"}' entry={(e?.resourceName ?? "none")}");
                if (e == null) return false;
                if (SkelHasBone(e.skeleton, boneName))
                {
                    // NATIVE SOCKET HIT (post-socketBones rebake): the donor's socket name now EXISTS on our rig, so
                    // the lookup succeeds natively — but AlterationFireProjectile still adds the donor's barrel-length
                    // offset ON TOP, flinging flash AND tracer start off the gun (field result: the smoke [small
                    // offsets] sat on the turret while flash+tracers [the canon offset] flew to the corner /
                    // vanished). Inside StartEvent, hand back the REAL socket TRS pre-compensated so the caller's
                    // own "+offset" lands exactly on the socket. Gated on the entry carrying muzzleBone — on
                    // socketed models that knob doubles as the compensation enable.
                    if (pendingMuzzleActive && boneName == pendingMuzzlePosName)
                    {
                        muzzleReentry = true;
                        try { result = getBoneTRS.Invoke(subPawn, new object[] { boneName }); }
                        finally { muzzleReentry = false; }
                        if (result != null) { CompensateDonorOffset(result, e, boneName, subPawn); return true; }
                    }
                    return false;      // any other real-bone lookup — genuine, leave it
                }
                var mn = ResolveMuzzleBoneName(e);
                if (mn == null) return false;
                muzzleReentry = true;   // belt-and-braces: the mn lookup exits via the found-bone branch today, but that branch is no longer a plain pass-through
                try { result = getBoneTRS.Invoke(subPawn, new object[] { mn }); }
                finally { muzzleReentry = false; }
                // Inside AlterationFireProjectile.StartEvent and this is the POSITION socket: pre-compensate the donor
                // offset on the boxed TRS so the caller's own Transform(offset) lands back on our muzzle bone (v3 above).
                if (pendingMuzzleActive && boneName == pendingMuzzlePosName && result != null)
                    CompensateDonorOffset(result, e, mn, subPawn);
                return true;
            }
            catch (Exception ex) { if (!muzzleErrLogged) { muzzleErrLogged = true; Plugin.Log.LogError("[Muzzle] redirect failed (disabled): " + ex); } return false; }
        }

        // Subtract the donor's socket-local offset from a boxed TRS (Translation -= Rotation * (offset * Scale)) so the
        // caller's own Transform(offset) returns to the bone. Per-shot diagnostic log stays on while this calibrates.
        static void CompensateDonorOffset(object trs, ModelEntry e, string boneLabel, object subPawn)
        {
            if (!trsFieldsResolved)
            {
                trsFieldsResolved = true;
                var tt = trs.GetType();
                trsTranslation = tt.GetField("Translation"); trsRotation = tt.GetField("Rotation"); trsScale = tt.GetField("Scale");
            }
            if (trsTranslation == null || trsRotation == null || trsScale == null) return;
            var tr = (UnityEngine.Vector3)trsTranslation.GetValue(trs);
            var rot = (UnityEngine.Quaternion)trsRotation.GetValue(trs);
            float sc = Convert.ToSingle(trsScale.GetValue(trs));
            // RUNTIME DIAL muzzleOffset "x,y,z" (world units): the empirical fix for a rig whose gun-bone head sits
            // at the base — the pinned origin (flash + tracer) is shifted without any re-bake. Save + relaunch to dial.
            if (!e.muzzleOffsetParsed)
            {
                e.muzzleOffsetParsed = true;
                var p = (e.muzzleOffset ?? "").Split(',');
                if (p.Length == 3
                    && float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ox)
                    && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var oy)
                    && float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var oz))
                    e.muzzleOffsetV = new UnityEngine.Vector3(ox, oy, oz);
            }
            trsTranslation.SetValue(trs, tr - rot * (pendingMuzzleOffset * sc) + e.muzzleOffsetV);
            // Verified recipe (ArmouredCar 2026-07-24): sockets Move_bloc/Canon_Up_left on the gun + dial 0,2.6,0 —
            // flash, smoke and tracer all on the tracking turret. Log ONCE per entry (was per-shot while calibrating).
            if (!e.muzzlePinLogged)
            {
                e.muzzlePinLogged = true;
                var pawnTr = (subPawn as UnityEngine.Component)?.transform;
                Plugin.Diag($"[Muzzle] '{e.resourceName}' fire origin pinned to '{boneLabel}' T={tr.ToString("0.0")} +dial={e.muzzleOffsetV.ToString("0.00")} scale={sc:0.###} donorOff={pendingMuzzleOffset.ToString("0.00")} pawnWorld={(pawnTr != null ? pawnTr.position.ToString("0.0") : "?")}");
            }
        }

        // Clear the procedural AIM layer (PawnEntry.BoneRotation0-3 = SkeletonBoneIndex/AxisIndex/Angle): the game aims an
        // artillery barrel by layering a bone rotation ON TOP of the pose, which twists our barrel (most visible while moving,
        // as the aim swings). Zero the angles so ONLY our baked clip drives the skeleton. No-op for un-aimed models.
        // SANITIZE the bone-rotation layer without killing it: on slots whose SkeletonBoneIndex is INVALID (the
        // 0xFFFFFFFF sentinel some donors emit), wrap the runaway accumulated Angle (1558°…) into 0..360 — the same
        // orientation, but a sane magnitude, so whatever downstream math consumes the raw value stops tearing the rig
        // apart (the Combine soldier's head). Slots with REAL bone indices are left untouched (genuine game layers).
        static bool sanitizeLogged;
        static void SanitizeAimLayer(object entry)
        {
            for (int i = 0; i < 4; i++)
            {
                var br = GetMember(entry, BoneRotationNames[i]);
                if (br == null) continue;
                long boneIdx;
                try { boneIdx = Convert.ToInt64(GetMember(br, "SkeletonBoneIndex")); } catch { continue; }
                if (boneIdx >= 0 && boneIdx < 100000) continue;        // a real bone — the game's own layer, keep it
                // Invalid-bone slots, by axis (observed on the Humvee donor): axis=1 (up) ≈ the HEADING channel — keep
                // it, it turns the pawn. axis=0 (roll) = the donor's WHEEL SPIN, accumulating as the unit travels —
                // on a humanoid rig those land on head/neck bones and wind them up (the ripped-off head). Zero them:
                // our model has no wheels to roll. (Magnitude-wrapping alone was tried first and didn't help — even a
                // sane 250° on the wrong bone tears the rig.)
                long axis;
                try { axis = Convert.ToInt64(GetMember(br, "AxisIndex")); } catch { continue; }
                if (axis == 1) continue;                                // heading — leave alone
                float a;
                try { a = Convert.ToSingle(GetMember(br, "Angle")); } catch { continue; }
                if (a == 0f) continue;
                SetMember(br, "Angle", 0f);
                SetMember(entry, BoneRotationNames[i], br);
                if (!sanitizeLogged) { sanitizeLogged = true; Plugin.Diag($"[Uni] aim-layer sanitize: slot {i} bone={boneIdx} axis={axis} angle {a:0.#} -> 0 (donor wheel-spin on an invalid bone)"); }
            }
        }

        static float lastAimLog;
        static void ClearAimLayer(object entry)
        {
            for (int i = 0; i < 4; i++)
            {
                var br = GetMember(entry, BoneRotationNames[i]);
                if (br == null) continue;
                // DIAGNOSTIC (bombard face-plant, 2026-07-19): log what the game streamed into the aim layer BEFORE
                // we flatten it — angle + which of OUR bones the donor-meant index lands on. Throttled to 1/s.
                try
                {
                    float ang = Convert.ToSingle(GetMember(br, "Angle"));
                    if (ang != 0f && UnityEngine.Time.time - lastAimLog > 1f)
                    {
                        lastAimLog = UnityEngine.Time.time;
                        // dump EVERY field of the BoneRotation struct — the first probe guessed member names
                        // (BoneIndex/Axis) and read nothing, and the index decides whether this runaway angle
                        // lands on a real bone (the diver) or the invalid 0xFFFFFFFF sentinel (a red herring).
                        var t = br.GetType();
                        var dump = string.Join(" ", t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            .Select(fi => fi.Name + "=" + fi.GetValue(br)));
                        Plugin.Diag($"[Aim] streamed slot{i} ({t.Name}): {dump} (cleared)");
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("[Aim] dump: " + ex.Message); }
                SetMember(br, "Angle", 0f);
                SetMember(entry, BoneRotationNames[i], br);
            }
        }

        // Runtime position offset. Static models bake position into the mesh — i.e. in the MODEL'S OWN FRAME, so it
        // turns with the unit. The animated path can't bake it, so it must match that semantic here: rotate the
        // planar (x = sideways, y = fore/aft) part by the pawn's FACING before adding. The old code added constant
        // WORLD axes, which pointed a fixed compass direction — the offset visibly drifted around the model as the
        // unit turned ("works for static, inconsistent in all directions when moving" bug). z (height) stays world-up
        // (Y). Re-applied each frame on the game's fresh world position, so it never accumulates. Logged once.
        static void ApplyPositionOffset(ModelEntry e, object entry)
        {
            if (e.position == UnityEngine.Vector3.zero) return;
            var os = GetMember(entry, "ObjectSpace");                  // boxed TRS
            UnityEngine.Vector3 tr;
            try { tr = (UnityEngine.Vector3)GetMember(os, "Translation"); } catch { return; }   // renamed member -> skip the offset instead of throwing per pawn per frame
            var planar = new UnityEngine.Vector3(e.position.x, 0f, e.position.y);   // registry y (fore/aft) -> local Z
            bool rotated = TryQuaternion(GetMember(os, "Rotation"), out var rot);
            if (rotated) planar = rot * planar;                        // pawn frame; else fall back to world axes
            tr += planar; tr.y += e.position.z;                        // registry z (height) -> world Y (up)
            if (!posLogged) { posLogged = true; Plugin.Diag($"[Uni] {e.resourceName} position offset {e.position} applied in {(rotated ? "PAWN frame (turns with the unit)" : "world axes (Rotation unreadable)")}, z->up Y"); }
            SetMember(os, "Translation", tr);
            SetMember(entry, "ObjectSpace", os);
        }

        // ObjectSpace.Rotation as a UnityEngine.Quaternion: it may BE one, or an Amplitude quaternion type with the
        // same x/y/z/w layout — read the components reflectively in that case. False (identity) if unreadable.
        static bool TryQuaternion(object o, out UnityEngine.Quaternion q)
        {
            if (o is UnityEngine.Quaternion uq) { q = uq; return true; }
            try
            {
                q = new UnityEngine.Quaternion(
                    Convert.ToSingle(GetMember(o, "x")), Convert.ToSingle(GetMember(o, "y")),
                    Convert.ToSingle(GetMember(o, "z")), Convert.ToSingle(GetMember(o, "w")));
                return true;
            }
            catch { q = UnityEngine.Quaternion.identity; return false; }
        }

        // Runtime scale multiplier: fix an animated model baked at the wrong scale WITHOUT a re-bake (the howitzer's 100x FBX
        // unit-conversion oversize -> scale 0.01). Multiplies the pawn's ObjectSpace.Scale each frame.
        static void ApplyScale(ModelEntry e, object entry)
        {
            if (e.scale == 1f || e.scale <= 0f) return;
            var oss = GetMember(entry, "ObjectSpace");
            var scObj = GetMember(oss, "Scale");
            if (scObj is float sf) SetMember(oss, "Scale", sf * e.scale);
            else if (scObj is UnityEngine.Vector3 sv) SetMember(oss, "Scale", sv * e.scale);
            else if (scObj != null) { try { SetMember(oss, "Scale", Convert.ToSingle(scObj) * e.scale); } catch { } }
            SetMember(entry, "ObjectSpace", oss);
            if (!scaleLogged) { scaleLogged = true; Plugin.Diag($"[Uni] {e.resourceName} runtime scale x{e.scale} (ObjectSpace.Scale was {scObj})"); }
        }

        // Diagnostic: dump the pawn's runtime transform once per model — a zero/huge Scale or an off Translation explains a
        // model that's fine in the editor preview but invisible in-game (docs/Firing-On-Attack.md).
        static void LogPoseHookOnce(PawnCtx ctx, ModelEntry e, object pose0)
        {
            if (poseHookSeen == null) poseHookSeen = new HashSet<string>();
            if (!poseHookSeen.Add(e.resourceName)) return;
            var osd = GetMember(ctx.entry, "ObjectSpace");
            Plugin.Diag($"[Uni] pose hook: '{e.resourceName}' -> Pose0 anim {e.animId} (skelId {ctx.skelId} -> {e.skeletonId}, desc {ctx.descId}); " +
                $"ObjectSpace T={GetMember(osd, "Translation")} S={GetMember(osd, "Scale")} R={GetMember(osd, "Rotation")} poseW={GetMember(pose0, "Weight")}");
        }

    }
}
