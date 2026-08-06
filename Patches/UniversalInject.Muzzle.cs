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
                var addOnT = GameBinding.PresentationPawnDefinitionAddOn;
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
        // TRUE-BEARING FX ROTATION (smoke fix 2026-08-06): while a strike's aim override is active for this
        // subpawn, rotate EVERY bone TRS it hands out from the transform's hex-quantized yaw onto the aim.
        // The transform skeleton never turns with the eased GPU model, so anything spawned off bone lookups —
        // the mecanim muzzle SMOKE, and the shell recapture via PrepareArtilleryStrikeFX — sat at the stale
        // angle. One seam rotates them all consistently (pivot = the subpawn root, ≈ the pawn root).
        internal static void AimRotateBoneTRS(object subPawn, object trs)
        {
            try
            {
                if (trs == null || aimOverrides.Count == 0) return;
                var st = (subPawn as UnityEngine.Component)?.transform;
                if (st == null || !TryAimAt(st.position, out float aim)) return;
                if (!trsFieldsResolved)
                {
                    trsFieldsResolved = true;
                    var tt = trs.GetType();
                    trsTranslation = tt.GetField("Translation"); trsRotation = tt.GetField("Rotation"); trsScale = tt.GetField("Scale");
                }
                if (trsTranslation == null || trsRotation == null) return;
                float delta = UnityEngine.Mathf.DeltaAngle(st.eulerAngles.y, aim);
                if (UnityEngine.Mathf.Abs(delta) < 0.5f) return;
                var rot = UnityEngine.Quaternion.Euler(0f, delta, 0f);
                var t = (UnityEngine.Vector3)trsTranslation.GetValue(trs);
                var r = (UnityEngine.Quaternion)trsRotation.GetValue(trs);
                trsTranslation.SetValue(trs, st.position + rot * (t - st.position));
                trsRotation.SetValue(trs, rot * r);
            }
            catch { }
        }

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

        // MOVE TILT (2026-08-04, user request — "helicopter-ness" after the ghost-rotor sprite was silenced): pitch the
        // model nose-down while it MOVES (the classic forward-flight attitude), ease back level at rest. Movement =
        // frame-to-frame planar delta of the pawn's own Translation (the settle-immune signal). Applied as a LOCAL
        // pitch on top of the game's facing each frame (never accumulates — recomputed from the game's fresh rotation).
        // Per-entry state is fine: tilt targets single-pawn hero units (multi-pawn units share one tilt, acceptable).
        static void ApplyMoveTilt(ModelEntry e, object entry)
        {
            if (e.moveTilt == 0f) return;
            var os = GetMember(entry, "ObjectSpace");
            UnityEngine.Vector3 tr;
            try { tr = (UnityEngine.Vector3)GetMember(os, "Translation"); } catch { return; }
            float now = UnityEngine.Time.time;
            float dt = UnityEngine.Mathf.Clamp(now - e.tiltLastTime, 0f, 0.1f);
            var dp = tr - e.tiltLastPos; dp.y = 0f;
            bool moving = e.tiltLastTime > 0f && dt > 0f && dp.magnitude / UnityEngine.Mathf.Max(dt, 1e-4f) > 0.5f;   // > 0.5 world units/s
            e.tiltLastPos = tr; e.tiltLastTime = now;
            e.tiltCur = UnityEngine.Mathf.MoveTowards(e.tiltCur, moving ? e.moveTilt : 0f, 60f * dt);   // ease 60 deg/s
            if (UnityEngine.Mathf.Abs(e.tiltCur) < 0.01f) return;
            var ro = GetMember(os, "Rotation");
            if (!TryQuaternion(ro, out var rot)) return;
            var tilted = rot * UnityEngine.Quaternion.Euler(e.tiltCur, 0f, 0f);   // local pitch; negative registry value = nose-up if the axis reads inverted
            if (ro is UnityEngine.Quaternion) SetMember(os, "Rotation", tilted);
            else
            {   // Amplitude quaternion struct: write components onto the boxed value, then put it back
                SetMember(ro, "x", tilted.x); SetMember(ro, "y", tilted.y); SetMember(ro, "z", tilted.z); SetMember(ro, "w", tilted.w);
                SetMember(os, "Rotation", ro);
            }
            SetMember(entry, "ObjectSpace", os);
        }

        // TURN EASE (spike/turn-ease 2026-08-04, "make it more fluent"): the engine writes a pawn's FACING as an
        // instant SNAP when a move order changes heading (spotted by shakee on the Comanche video). Smooth it:
        // keep a per-pawn eased yaw, advance it toward the game's fresh target yaw at a capped rate, and BANK a
        // few degrees into the turn. Live-tunable via BepInEx/config/enc_turnease.txt (`rate=180` deg/s,
        // `bank=6` deg, `snap=120` deg) polled ~1/s like the rotor trim; rate 0 or no file = fully off. Runs
        // BEFORE ApplyMoveTilt so the nose-down composes on top of the eased heading. State is position-matched
        // (nearest within 4u — the deploy poll's approximation) so multiple units smooth independently; a
        // stacked squadron shares one state harmlessly (same spot, same heading). Every yaw angle eases, 180s
        // included; teleports/battle placement snap naturally by MISSING the position match (fresh state = target yaw).
        class TurnState { public UnityEngine.Vector3 pos; public float yaw; public float targetYaw; public float bank; public float lastT; }
        static readonly List<TurnState> turnStates = new List<TurnState>();
        internal static float turnRate = 0f, turnBank = 0f;   // file-driven while spiking

        // TRUE-BEARING AIM (2026-08-05, "turn to the exact angle needed to realistically shoot"): a vanilla map
        // bombard flips the unit to a HEX-QUANTIZED angle (GetHexagonAngleToPosition = one of six directions, up
        // to 30 deg off the target) — invisible at snap speed, glaring on an eased turn. While a strike is in
        // progress an override registered here (position-matched, like everything in this system) replaces the
        // ease TARGET with the real bearing to the target tile; the barrel lays ON the target, and after the
        // override expires the unit eases back to the game's quantized facing (the crew re-laying the gun).
        // releaseAt is THE strike's one shared clock (sync fix 2026-08-05): the attack pose teleport, the shot
        // sound/smoke and the shell schedule all fire off this single timestamp — mixing a dynamic release
        // (aligned-within-8°) with static scheduler delays desynced the bang from the recoil by ~0.25 s.
        class AimOverride { public UnityEngine.Vector3 pos; public float yaw; public float until; public float releaseAt; }
        static readonly List<AimOverride> aimOverrides = new List<AimOverride>();
        internal static void SetAimOverride(UnityEngine.Vector3 pos, float yaw, float duration, float releaseAt = 0f)
        {
            float now = UnityEngine.Time.time;
            for (int i = aimOverrides.Count - 1; i >= 0; i--) if (now > aimOverrides[i].until) aimOverrides.RemoveAt(i);
            foreach (var o in aimOverrides)
            { var d = o.pos - pos; d.y = 0f; if (d.sqrMagnitude < 4f) { o.pos = pos; o.yaw = yaw; o.until = now + duration; o.releaseAt = releaseAt; return; } }
            aimOverrides.Add(new AimOverride { pos = pos, yaw = yaw, until = now + duration, releaseAt = releaseAt });
        }
        // The strike's shared release time for the pawn nearest `pos` (false = no armed strike there).
        internal static bool TryAimRelease(UnityEngine.Vector3 pos, out float releaseAt)
        {
            releaseAt = 0f; float now = UnityEngine.Time.time; float best = 16f; bool found = false;
            for (int i = 0; i < aimOverrides.Count; i++)
            {
                if (now > aimOverrides[i].until || aimOverrides[i].releaseAt <= 0f) continue;
                var d = aimOverrides[i].pos - pos; d.y = 0f;
                if (d.sqrMagnitude < best) { best = d.sqrMagnitude; releaseAt = aimOverrides[i].releaseAt; found = true; }
            }
            return found;
        }
        static bool TryAimAt(UnityEngine.Vector3 pos, out float yaw)
        {
            yaw = 0f; float now = UnityEngine.Time.time; float best = 16f; bool found = false;
            for (int i = 0; i < aimOverrides.Count; i++)
            {
                if (now > aimOverrides[i].until) continue;
                var d = aimOverrides[i].pos - pos; d.y = 0f;
                if (d.sqrMagnitude < best) { best = d.sqrMagnitude; yaw = aimOverrides[i].yaw; found = true; }
            }
            return found;
        }

        // How far (deg) the pawn nearest `pos` still has to turn (eased yaw vs the game's target). 0 = aligned,
        // no state, or easing off. Main-thread only (pose hook + Plugin.Update), like every turnStates consumer.
        // Battle-turn spike: lets the fire-clip arm HOLD the recoil until the barrel actually faces the enemy.
        internal static float TurnMisalignAt(UnityEngine.Vector3 pos)
        {
            float now = UnityEngine.Time.time; TurnState st = null; float best = 16f;
            for (int i = turnStates.Count - 1; i >= 0; i--)
            {
                if (now - turnStates[i].lastT > 10f) continue;   // stale — pruning is ApplyTurnEase's job
                var d = turnStates[i].pos - pos; d.y = 0f;
                if (d.sqrMagnitude < best) { best = d.sqrMagnitude; st = turnStates[i]; }
            }
            return st == null ? 0f : UnityEngine.Mathf.Abs(UnityEngine.Mathf.DeltaAngle(st.yaw, st.targetYaw));
        }

        // Seconds until the pawn nearest `pos` finishes its eased turn — OUR entries only (vanilla pawns don't
        // ease, so they keep vanilla attack pacing). Feeds the AttackFSM delay patch so the attack animation and
        // its FireProjectile mecanim event (the shell!) wait for the barrel to face the target.
        // The eased yaw of the pawn nearest `pos`, if a live turn-ease state exists there. Lets the artillery
        // hold compute remaining-turn against a target read from the (already flipped) pawn Transform — no
        // dependence on TurnState.targetYaw, which only refreshes on the NEXT pose frame (the v3/v4 race).
        internal static bool TryTurnYawAt(UnityEngine.Vector3 pos, out float yaw)
        {
            yaw = 0f;
            float now = UnityEngine.Time.time; TurnState st = null; float best = 16f;
            for (int i = turnStates.Count - 1; i >= 0; i--)
            {
                if (now - turnStates[i].lastT > 10f) continue;
                var d = turnStates[i].pos - pos; d.y = 0f;
                if (d.sqrMagnitude < best) { best = d.sqrMagnitude; st = turnStates[i]; }
            }
            if (st == null) return false;
            yaw = st.yaw; return true;
        }

        // The turn-ease rate an attacking pawn is governed by: our entry's (dial override first), else a
        // Formation Lab per-unit link — the VANILLA route (unitDef here is the simulation name, e.g.
        // "LandUnit_Era5_Common_LineInfantry (…)"; the link stores the presentation name inside it, so
        // case-insensitive contains bridges the two). 0 = no easing, vanilla pacing.
        static float rateTraceAt;
        internal static float TurnRateForUnitDef(string unitDef)
        {
            var e = FindEntryForUnitDefinition(unitDef);
            if (e != null)   // our entry: per-model > category default > global rate (docs/Turn-Ease.md precedence)
            {
                if (e.turnRate > 0f) return e.turnRate;
                if (e.profCat == CatPlane) return 0f;              // planes stay excluded even from fallbacks
                float cr = CategoryRateForDesc(e.descId, e.profCat);
                if (cr > 0f) return cr;
                if (turnRate > 0f) return turnRate;
                // FALL THROUGH (regression fix): an entry with no per-model rate and an unset/stale
                // profCat/descId used to DEAD-END here at 0 while the pose side happily eased the rendered
                // pawn via its own desc-keyed category — the howitzer turned but the strike chain never armed.
                // The name-based category fallback below answers for it instead.
            }
            if (string.IsNullOrEmpty(unitDef)) return 0f;
            foreach (var kv in FormationOverride.TurnRateByUnit)
                if (TurnLinkMatches(unitDef, kv.Key)) return kv.Value;   // incl. the culture-variant relaxation
            if (AnyCatRate)
            {
                // vanilla category fallback: find the unit's MAIN pawn definition (variant-suffix-stripped
                // addon name contained in the simulation unit-def string) and use its category rate.
                foreach (var ad in addonDefIds)
                {
                    var nm = ad.Key;
                    int us = nm.LastIndexOf('_');
                    if (us > 0 && us + 1 < nm.Length)
                    {
                        bool digits = true;
                        for (int i = us + 1; i < nm.Length; i++) if (!char.IsDigit(nm[i])) { digits = false; break; }
                        if (digits) nm = nm.Substring(0, us);
                    }
                    if (nm.Length >= 6 && unitDef.IndexOf(nm, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        vanillaCatByDesc.TryGetValue(ad.Value, out int cat))
                        return CategoryRateForDesc(ad.Value, cat);
                }
            }
            // resolution trace (throttled): everything above came up empty — name the state so a dead strike
            // chain is diagnosable from one log line instead of another instrumented round-trip
            if (UnityEngine.Time.time > rateTraceAt)
            {
                rateTraceAt = UnityEngine.Time.time + 1f;
                Plugin.Log.LogInfo($"[TurnEase] rate 0 for '{unitDef}': entry={(e != null ? $"{e.resourceName} (turnRate={e.turnRate}, profCat={e.profCat}, descId={e.descId})" : "-")}, links={FormationOverride.TurnRateByUnit.Count}, catRates={(AnyCatRate ? "on" : "off")}, addons={addonDefIds.Count}");
            }
            return 0f;
        }

        // Like TurnHoldSeconds, but the target is the pawn's TRANSFORM yaw — valid the same frame a bombard's
        // FlipPawnsGrid(Teleport) snaps it, when TurnState.targetYaw hasn't refreshed yet (next pose frame).
        internal static float TurnHoldTransformSeconds(object pawn)
        {
            var unit = GetMember(pawn, "PresentationUnit");
            float rate = TurnRateForUnitDef(GetMember(unit, "UnitDefinition")?.ToString() ?? "");
            if (rate <= 0f || !(GetMember(pawn, "Transform") is UnityEngine.Transform tr)) return 0f;
            if (!TryTurnYawAt(tr.position, out float eased)) return 0f;
            // an active true-bearing override IS the aim the shot should wait for; the flipped transform
            // (hex-quantized) is the fallback when no strike override exists
            float target = TryAimAt(tr.position, out float ay) ? ay : tr.eulerAngles.y;
            float miss = UnityEngine.Mathf.Abs(UnityEngine.Mathf.DeltaAngle(eased, target));
            return miss >= 8f ? UnityEngine.Mathf.Min(miss / rate + 0.2f, 3f) : 0f;
        }

        static float holdLogAt;
        internal static float TurnHoldSeconds(object pawn, UnityEngine.Vector3 pos)
        {
            var unit = GetMember(pawn, "PresentationUnit");
            string unitDef = GetMember(unit, "UnitDefinition")?.ToString() ?? "";
            float rate = TurnRateForUnitDef(unitDef);
            if (rate <= 0f) return 0f;
            float miss = TurnMisalignAt(pos);
            float hold = miss >= 8f ? UnityEngine.Mathf.Min(miss / rate + 0.15f, 3f) : 0f;
            // eased units attack rarely — a throttled trace here is the ground truth for WHY an attack
            // did or didn't wait (rate resolution, measured misalignment, resulting hold)
            if (UnityEngine.Time.time > holdLogAt)
            { holdLogAt = UnityEngine.Time.time + 0.5f; Plugin.Log.LogInfo($"[BattleTurn] hold check '{unitDef}': rate={rate} misalign={miss:F0}deg -> hold {hold:F2}s"); }
            return hold;
        }

        static void ApplyTurnEase(ModelEntry e, object entry)
        {
            // PRECEDENCE (2026-08-06, user design): per-model Factory value > CATEGORY default (human/land/
            // turret/hover/ship, from the dial) > global `rate`. The old file-overrides-model rule surprised
            // the user ("the howitzer turned despite not being configured") — an explicit per-model value is
            // now always authoritative, and the dial provides type-level DEFAULTS instead of a blanket
            // override. PLANES (CatPlane) never ease from category OR global — the engine flies fixed-wing
            // aircraft on natural curved paths (user rule); only their own per-model rate can opt them in.
            if (e.profCat == CatLand && AnyCatRate && e.descId >= 0 &&
                GetMember(entry, "ObjectSpace") is object eos &&
                GetMember(eos, "Translation") is UnityEngine.Vector3 epos)
                TryLearnClass(e.descId, epos);   // our land-profile models refine to hover/turret too (the Comanche IS a Hover unit)
            float catRate = CategoryRateForDesc(e.descId, e.profCat);
            float rate = e.turnRate > 0f ? e.turnRate
                       : e.profCat == CatPlane ? 0f
                       : catRate > 0f ? catRate
                       : turnRate;
            // bank: per-model wins; then the CATEGORY bank (hoverbank/shipbank — a chopper banks, a ship
            // heels, a truck does neither); the legacy file `bank` covers models eased per-model/global-rate.
            float catBank = CategoryBank(EffectiveCat(e.descId, e.profCat));
            float bank = e.turnBank != 0f ? e.turnBank
                       : catBank != 0f ? catBank
                       : e.turnRate > 0f || turnRate > 0f ? turnBank
                       : 0f;
            ApplyTurnEaseCore(rate, bank, entry);
        }

        // Core easing, rate/bank already resolved — shared by our model entries and VANILLA pawns whose unit
        // carries a Formation Lab turn-ease link (rate from vanillaTurnByDesc, bank always 0 for those).
        internal static void ApplyTurnEaseCore(float rate, float bank, object entry)
        {
            if (rate <= 0f) return;
            var os = GetMember(entry, "ObjectSpace");
            UnityEngine.Vector3 tr;
            try { tr = (UnityEngine.Vector3)GetMember(os, "Translation"); } catch { return; }
            var ro = GetMember(os, "Rotation");
            if (!TryQuaternion(ro, out var rot)) return;
            // true-bearing aim: an active strike override replaces the game's (hex-quantized) yaw as the target
            bool aimed = TryAimAt(tr, out float aimYaw);
            float target = aimed ? aimYaw : rot.eulerAngles.y;
            float now = UnityEngine.Time.time;
            TurnState st = null; float best = 16f;   // nearest live state within 4 world units
            for (int i = turnStates.Count - 1; i >= 0; i--)
            {
                if (now - turnStates[i].lastT > 10f) { turnStates.RemoveAt(i); continue; }
                var d = turnStates[i].pos - tr; d.y = 0f;
                if (d.sqrMagnitude < best) { best = d.sqrMagnitude; st = turnStates[i]; }
            }
            if (st == null) { st = new TurnState { pos = tr, yaw = target, lastT = now }; turnStates.Add(st); }
            float dt = UnityEngine.Mathf.Clamp(now - st.lastT, 0f, 0.1f);
            st.pos = tr; st.lastT = now; st.targetYaw = target;   // published for TurnMisalignAt (fire-clip hold)
            float diff = UnityEngine.Mathf.DeltaAngle(st.yaw, target);
            // NO yaw-size guard (user verdict: every angle eases, incl. full 180s). Teleports/battle placement
            // still snap NATURALLY: a pawn that jumps >4u misses its position-matched state and the fresh state
            // starts AT the target yaw. The old `snap` threshold was redundant and ate legitimate 180-turns.
            st.yaw = UnityEngine.Mathf.MoveTowardsAngle(st.yaw, target, rate * dt);
            float wantBank = UnityEngine.Mathf.Clamp(diff / 45f, -1f, 1f) * bank;   // bank ~ how hard we're turning
            st.bank = UnityEngine.Mathf.MoveTowards(st.bank, wantBank, (UnityEngine.Mathf.Abs(bank) * 3f + 30f) * dt);
            if (!aimed && UnityEngine.Mathf.Abs(UnityEngine.Mathf.DeltaAngle(st.yaw, target)) < 0.01f && UnityEngine.Mathf.Abs(st.bank) < 0.05f)
                return;   // converged on the game's own value — leave it (while AIMED we must keep writing: the game re-writes the quantized yaw every frame)
            var eased = UnityEngine.Quaternion.Euler(rot.eulerAngles.x, st.yaw, st.bank);   // keep the game's pitch; z = our bank
            if (ro is UnityEngine.Quaternion) SetMember(os, "Rotation", eased);
            else
            {
                SetMember(ro, "x", eased.x); SetMember(ro, "y", eased.y); SetMember(ro, "z", eased.z); SetMember(ro, "w", eased.w);
                SetMember(os, "Rotation", ro);
            }
            SetMember(entry, "ObjectSpace", os);
        }

        // TERRAIN HUG (spike/terrain-hug 2026-08-04): the engine already flies air units at a terrain-RELATIVE
        // altitude (user-verified), but that altitude ignores BUILDINGS — hence the registry position.z lift that
        // clears a city skyline. Flying that high everywhere wastes the terrain-following: this drops the unit
        // back down (`drop`, negative) whenever no district is under/ahead of it, and eases the lift back in as
        // it approaches one. Districts are enumerated from live PresentationDistrict components (cached ~3s —
        // they're static) and matched on the horizontal plane only. The probe point LEADS the unit along its own
        // movement vector (`lookahead`), so it climbs BEFORE the buildings, like a pilot, instead of reacting.
        // Runs AFTER ApplyPositionOffset: position.z stays the city-clearing height, this subtracts over open
        // ground. Live-tuned via BepInEx/config/enc_hugterrain.txt; drop 0 or no file = off.
        internal static float hugDrop = 0f, hugRadius = 0f, hugLookahead = 3f, hugEase = 4f, hugCliff = 1f;
        internal static readonly List<string> hugOnly = new List<string>();   // name whitelist (empty = all)
        internal static readonly List<string> hugSkip = new List<string>();   // name blacklist (farms, exploitations)
        // BUILT-IN default blacklist: these district kinds are FLAT (cultivated tiles — fields, vineyards, mines —
        // and rubble), so they must never make an aircraft climb. It's a property of Humankind's data, not a user
        // preference, so it applies whenever the dial file specifies no filter of its own — deleting the file
        // can't resurrect the "cruises high over farmland" bug.
        static readonly List<string> hugSkipDefault = new List<string> { "Exploitation", "Ruin" };
        static readonly List<UnityEngine.Vector3> districtPts = new List<UnityEngine.Vector3>();
        static float districtNextScan, tileSpacing;
        class HugState { public UnityEngine.Vector3 pos; public UnityEngine.Vector3 dir; public float cur; public float lastT; }
        static readonly List<HugState> hugStates = new List<HugState>();

        internal static void RearmDistrictScan() { districtNextScan = 0f; hugScanLogged = false; }

        // Ground height under a world point via a downward physics raycast (float.MinValue = nothing hit).
        // One-shot log of the first hit so we know WHAT we're standing on — and, if nothing ever hits, that the
        // terrain is collider-less and cliff anticipation needs the tile-elevation route instead.
        static bool cliffProbeLogged;
        static float GroundHeight(UnityEngine.Vector3 p)
        {
            try
            {
                // RaycastAll + take the LOWEST hit, skipping units: the first version used a plain Raycast and
                // measured the helicopter's OWN army collider ("hit 'PresentationArmy #64685'"), i.e. it compared
                // unit heights instead of ground and the cliff climb was noise. Armies/units sit on layer 10 and
                // are named Presentation*; the ground is whatever remains underneath them.
                var hits = UnityEngine.Physics.RaycastAll(new UnityEngine.Vector3(p.x, p.y + 80f, p.z),
                                                          UnityEngine.Vector3.down, 400f);
                float best = float.MinValue; string bestName = null; int bestLayer = -1;
                for (int i = 0; i < hits.Length; i++)
                {
                    var col = hits[i].collider;
                    if (col == null) continue;
                    var nm = col.name ?? "";
                    if (col.gameObject.layer == 10 || nm.StartsWith("Presentation", StringComparison.Ordinal)) continue;
                    if (best == float.MinValue || hits[i].point.y < best)   // lowest = the ground, not rooftops
                    { best = hits[i].point.y; bestName = nm; bestLayer = col.gameObject.layer; }
                }
                if (!cliffProbeLogged)
                {
                    cliffProbeLogged = true;
                    var all = string.Join(", ", hits.Select(h => $"{h.collider?.name}(L{h.collider?.gameObject.layer},y{h.point.y:0.#})").Take(6));
                    Plugin.Log.LogInfo(best > float.MinValue
                        ? $"[Hug] cliff probe: ground '{bestName}' (layer {bestLayer}) at y={best:0.##}, unit y={p.y:0.##} | all hits: {all}"
                        : $"[Hug] cliff probe: NO ground collider (only units/none) — cliff anticipation inert. Hits: {(hits.Length == 0 ? "none" : all)}");
                }
                return best;
            }
            catch { }
            return float.MinValue;
        }

        static void RescanDistricts()
        {
            float now = UnityEngine.Time.time;
            if (now < districtNextScan) return;
            districtNextScan = now + 3f;   // districts are static; a rescan every few seconds is plenty
            try
            {
                var dt = GameBinding.PresentationDistrict;
                if (dt == null) return;
                districtPts.Clear();
                var names = new List<string>();
                foreach (var o in UnityEngine.Object.FindObjectsOfType(dt))
                {
                    if (!(o is UnityEngine.Component c) || c == null) continue;
                    // NOT every PresentationDistrict is a BUILDING: Humankind renders cultivated tiles
                    // (farms, vineyards, mines) as districts too, and lifting over farmland defeats the
                    // whole feature. Filter by name — `only=` (whitelist) or `skip=` (blacklist), both
                    // live-tunable substrings, so the classification is dialed without a rebuild.
                    // The GameObject is always "PresentationDistrict(Clone)" — useless. The real identity is the
                    // private `constructibleDefinitionName` (e.g. Extension_Base_CityCenter, an Exploitation_*
                    // for a cultivated tile), which is exactly the built-vs-farmed distinction we need.
                    string nm = GetMember(c, "constructibleDefinitionName")?.ToString();
                    if (string.IsNullOrEmpty(nm)) nm = c.gameObject.name ?? "";
                    if (!hugScanLogged && names.Count < 40) names.Add(nm);
                    var skip = hugSkip.Count > 0 || hugOnly.Count > 0 ? hugSkip : hugSkipDefault;
                    if (hugOnly.Count > 0 && !hugOnly.Any(s => nm.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    if (skip.Count > 0 && skip.Any(s => nm.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    districtPts.Add(c.transform.position);
                }
                if (names.Count > 0)
                    Plugin.Log.LogInfo("[Hug] district names seen: " + string.Join(" | ", names.Distinct().Take(40)));
                // TILE SCALE, measured not guessed: the median nearest-neighbour distance between districts IS
                // the tile spacing (adjacent districts sit one tile apart). `radius` then means "this tile only"
                // instead of an arbitrary world distance — the difference between climbing OVER the buildings and
                // climbing for the whole neighbourhood around them.
                var nn = new List<float>();
                for (int i = 0; i < districtPts.Count; i++)
                {
                    float b = float.MaxValue;
                    for (int j = 0; j < districtPts.Count; j++)
                    {
                        if (i == j) continue;
                        float dx = districtPts[i].x - districtPts[j].x, dz = districtPts[i].z - districtPts[j].z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < b) b = d2;
                    }
                    if (b < float.MaxValue) nn.Add(UnityEngine.Mathf.Sqrt(b));
                }
                if (nn.Count > 0) { nn.Sort(); tileSpacing = nn[nn.Count / 2]; }
                if (!hugScanLogged)
                {
                    hugScanLogged = true;
                    Plugin.Log.LogInfo($"[Hug] district scan: {districtPts.Count} PresentationDistrict(s), tile spacing ~{tileSpacing:0.##} " +
                                       $"=> auto radius {tileSpacing * 0.55f:0.##} (radius<=0 in the dial file uses this)");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Hug] district scan: " + ex.Message); }
        }
        static bool hugScanLogged, hugWasOver;

        static void ApplyTerrainHug(ModelEntry e, object entry)
        {
            // PER-MODEL first (the Factory's "Terrain hug — drop"), with the live dial file as an OVERRIDE for
            // in-game tuning: a non-zero enc_hugterrain.txt `drop` wins so a session can be dialed by feel, and
            // clearing it (drop=0/no file) falls back to whatever each model shipped with.
            float drop = hugDrop != 0f ? hugDrop : e.hugDrop;
            float lookahead = hugDrop != 0f ? hugLookahead : e.hugLookahead;
            if (drop == 0f) return;
            RescanDistricts();
            var os = GetMember(entry, "ObjectSpace");
            UnityEngine.Vector3 tr;
            try { tr = (UnityEngine.Vector3)GetMember(os, "Translation"); } catch { return; }
            float now = UnityEngine.Time.time;
            HugState st = null; float best = 16f;
            for (int i = hugStates.Count - 1; i >= 0; i--)
            {
                if (now - hugStates[i].lastT > 10f) { hugStates.RemoveAt(i); continue; }
                var d = hugStates[i].pos - tr; d.y = 0f;
                if (d.sqrMagnitude < best) { best = d.sqrMagnitude; st = hugStates[i]; }
            }
            if (st == null) { st = new HugState { pos = tr, cur = 0f, lastT = now }; hugStates.Add(st); }
            float dt = UnityEngine.Mathf.Clamp(now - st.lastT, 0f, 0.1f);
            var step = tr - st.pos; step.y = 0f;
            if (step.sqrMagnitude > 1e-6f) st.dir = UnityEngine.Vector3.Lerp(st.dir, step.normalized, 0.25f);   // smoothed heading
            st.pos = tr; st.lastT = now;
            // probe AHEAD of the unit so the climb anticipates the skyline
            var probe = tr + st.dir * lookahead;
            // radius <= 0 => AUTO: a bit over half the measured tile spacing, i.e. "this district's own tile".
            // A wider radius lifts the unit for every field and forest NEXT to the city (observed: cruising high
            // over farmland with one building two tiles away).
            float rad = hugRadius > 0f ? hugRadius : (tileSpacing > 0.01f ? tileSpacing * 0.55f : 3f);
            bool overDistrict = false;
            float r2 = rad * rad, nearest2 = float.MaxValue;
            for (int i = 0; i < districtPts.Count; i++)
            {
                float dx = districtPts[i].x - probe.x, dz = districtPts[i].z - probe.z;
                float d2 = dx * dx + dz * dz;
                if (d2 < nearest2) nearest2 = d2;
                if (d2 < r2) { overDistrict = true; break; }
            }
            // calibration aid: log only when the verdict FLIPS (not per frame), with the distance that decided it
            if (overDistrict != hugWasOver)
            {
                hugWasOver = overDistrict;
                Plugin.Log.LogInfo($"[Hug] {(overDistrict ? "OVER district -> climbing" : "open ground -> descending")} " +
                                   $"(nearest district {UnityEngine.Mathf.Sqrt(nearest2):0.##}, radius {rad:0.##}, tile ~{tileSpacing:0.##})");
            }
            // CLIFF ANTICIPATION (user request): the engine's terrain following is tied to the tile the unit is
            // ON, so a step up in the ground arrives at the cell boundary — the aircraft rises INTO the cliff
            // instead of over it. Probe the ground under the same lead point: if it stands higher than the
            // ground here, add that difference NOW so the climb starts before the edge; on arrival the engine's
            // own altitude catches up and the extra decays to zero. Climb-only (a descent handled early would
            // fly us at the LOWER level while still over the high ground). Uses a physics raycast — if the
            // terrain has no collider the probe simply finds nothing and the feature no-ops (logged once).
            float cliff = 0f;
            if (hugCliff > 0f)
            {
                float gHere = GroundHeight(tr), gAhead = GroundHeight(probe);
                if (gHere > float.MinValue && gAhead > float.MinValue && gAhead > gHere)
                    cliff = UnityEngine.Mathf.Min(gAhead - gHere, 12f) * hugCliff;
            }
            // target: 0 near a district (keep the full position.z lift) or `drop` over open ground, plus any
            // cliff pre-climb
            st.cur = UnityEngine.Mathf.MoveTowards(st.cur, (overDistrict ? 0f : drop) + cliff, hugEase * dt);
            if (UnityEngine.Mathf.Abs(st.cur) < 0.001f) return;
            tr.y += st.cur;
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
