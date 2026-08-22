using System;
using System.Collections.Generic;

namespace HumankindAssetFramework
{
    // THE PAWN-ENTRY FAST PATH (perf pass 2026-08-21). The pose hook runs for every pawn-add, every frame; for OUR pawns
    // it read and wrote ~60 members of the boxed PawnEntry struct through reflection (25-57 µs per pawn, FrameCost).
    // These are the same members through FastMember's compiled accessors, built ONCE from the first PawnEntry the hook
    // sees. EACH accessor has its own reflection fallback (a renamed member costs speed, not function, and the startup
    // binding report still names it); `Ready` is the CORE set (ids, translation, the nine poses) — an optional one
    // (HideFactor — a packed PROPERTY on the game's struct; Rotation) falls back on its own. The shape of PawnEntry was
    // confirmed headlessly (tools/typeprobe): ObjectSpace is a TRS {Vector3 Translation; float Scale; Quaternion
    // Rotation}, PawnEntryPose is {uint AnimationId; float Time; float Weight}, ids are int.
    // Semantics are identical to the reflection path: the accessors write INTO the boxed entry, exactly like SetMember on
    // the box did; the caller still writes the box back into the array.
    internal static partial class UniversalInject
    {
        static class PawnFast
        {
            public static bool Ready;
            static Type builtFor;
            public static Func<object, int> SkelId, DescId;
            public static Action<object, int> SetSkelId;
            public static Func<object, float> HideFactor;
            public static Action<object, float> SetHideFactor;
            public static Func<object, UnityEngine.Vector3> Translation;
            public static Action<object, UnityEngine.Vector3> SetTranslation;
            public static Func<object, float> Scale;                       // ObjectSpace.Scale (a float on this build — tools/typeprobe)
            public static Action<object, float> SetScale;
            public static Func<object, UnityEngine.Quaternion> Rotation;
            public static Action<object, UnityEngine.Quaternion> SetRotation;
            [ProcessLived("compiled accessor table, built once per pawn-entry type")] public static readonly Action<object, uint>[] PoseAnimId = new Action<object, uint>[9];
            [ProcessLived("compiled accessor table, built once per pawn-entry type")] public static readonly Action<object, float>[] PoseTime = new Action<object, float>[9];
            [ProcessLived("compiled accessor table, built once per pawn-entry type")] public static readonly Action<object, float>[] PoseWeight = new Action<object, float>[9];
            [ProcessLived("compiled accessor table, built once per pawn-entry type")] public static readonly Action<object, uint>[] BRBone = new Action<object, uint>[4];    // BoneRotationN.SkeletonBoneIndex (rotor spin/trim, gun elevation)
            [ProcessLived("compiled accessor table, built once per pawn-entry type")] public static readonly Action<object, uint>[] BRAxis = new Action<object, uint>[4];    // BoneRotationN.AxisIndex
            [ProcessLived("compiled accessor table, built once per pawn-entry type")] public static readonly Action<object, float>[] BRAngle = new Action<object, float>[4]; // BoneRotationN.Angle
            public static bool BRReady;

            public static void EnsureInit(object entry)
            {
                if (entry == null) return;
                var t = entry.GetType();
                if (t == builtFor) return;
                builtFor = t; Ready = false;
                try
                {
                    var missing = new List<string>();
                    SkelId = FastMember.Getter<int>(t, "SkeletonId");                         if (SkelId == null) missing.Add("SkeletonId");
                    DescId = FastMember.Getter<int>(t, "PawnDescriptorId");                   if (DescId == null) missing.Add("PawnDescriptorId");
                    SetSkelId = FastMember.Setter<int>(t, "SkeletonId");                      if (SetSkelId == null) missing.Add("set SkeletonId");
                    Translation = FastMember.Getter<UnityEngine.Vector3>(t, "ObjectSpace.Translation");       if (Translation == null) missing.Add("ObjectSpace.Translation");
                    SetTranslation = FastMember.Setter<UnityEngine.Vector3>(t, "ObjectSpace.Translation");    if (SetTranslation == null) missing.Add("set ObjectSpace.Translation");
                    for (int i = 0; i < 9; i++)
                    {
                        PoseAnimId[i] = FastMember.Setter<uint>(t, "Pose" + i + ".AnimationId");   if (PoseAnimId[i] == null) missing.Add("Pose" + i + ".AnimationId");
                        PoseTime[i] = FastMember.Setter<float>(t, "Pose" + i + ".Time");           if (PoseTime[i] == null) missing.Add("Pose" + i + ".Time");
                        PoseWeight[i] = FastMember.Setter<float>(t, "Pose" + i + ".Weight");       if (PoseWeight[i] == null) missing.Add("Pose" + i + ".Weight");
                    }
                    Ready = missing.Count == 0;
                    BRReady = true;
                    for (int i = 0; i < 4; i++)
                    {
                        BRBone[i] = FastMember.Setter<uint>(t, "BoneRotation" + i + ".SkeletonBoneIndex");
                        BRAxis[i] = FastMember.Setter<uint>(t, "BoneRotation" + i + ".AxisIndex");
                        BRAngle[i] = FastMember.Setter<float>(t, "BoneRotation" + i + ".Angle");
                        BRReady &= BRBone[i] != null && BRAxis[i] != null && BRAngle[i] != null;
                    }
                    // OPTIONAL (own fallbacks): the packed HideFactor property, and the rotation (UnityEngine.Quaternion on this build)
                    HideFactor = FastMember.Getter<float>(t, "HideFactor");
                    SetHideFactor = FastMember.Setter<float>(t, "HideFactor");
                    Scale = FastMember.Getter<float>(t, "ObjectSpace.Scale"); SetScale = FastMember.Setter<float>(t, "ObjectSpace.Scale");
                    Rotation = FastMember.Getter<UnityEngine.Quaternion>(t, "ObjectSpace.Rotation");
                    SetRotation = FastMember.Setter<UnityEngine.Quaternion>(t, "ObjectSpace.Rotation");
                    var optional = new List<string>();
                    if (SetHideFactor == null) optional.Add("HideFactor"); if (Rotation == null || SetRotation == null) optional.Add("ObjectSpace.Rotation"); if (!BRReady) optional.Add("BoneRotation slots");
                    Plugin.Log.LogInfo(Ready
                        ? $"[PawnFast] compiled accessors ready for {t.Name} (pose hook on the fast path){(optional.Count > 0 ? "; on reflection: " + string.Join(", ", optional) : "")}"
                        : $"[PawnFast] core accessors unavailable for {t.Name}: {string.Join(", ", missing)} — pose hook stays on reflection (slower, same behaviour)");
                }
                catch (Exception ex) { Ready = false; Plugin.Log.LogWarning("[PawnFast] init failed — reflection path: " + ex.Message); }
            }
        }

        // ---- the wrappers every per-pawn site uses: fast when available, the old reflection otherwise ----
        static bool TryGetTranslation(object entry, out UnityEngine.Vector3 v)
        {
            if (PawnFast.Ready) { v = PawnFast.Translation(entry); return true; }
            v = default;
            var os = GetMember(entry, "ObjectSpace");
            if (os == null) return false;
            if (!(GetMember(os, "Translation") is UnityEngine.Vector3 t)) return false;
            v = t; return true;
        }
        static void SetTranslation(object entry, UnityEngine.Vector3 v)
        {
            if (PawnFast.Ready) { PawnFast.SetTranslation(entry, v); return; }
            var os = GetMember(entry, "ObjectSpace");
            if (os == null) return;
            SetMember(os, "Translation", v);
            SetMember(entry, "ObjectSpace", os);   // boxed struct: the write-back is the write (the combatZ lesson)
        }
        static bool TryGetRotation(object entry, out UnityEngine.Quaternion q)
        {
            if (PawnFast.Rotation != null) { q = PawnFast.Rotation(entry); return true; }
            q = UnityEngine.Quaternion.identity;
            var os = GetMember(entry, "ObjectSpace");
            return os != null && TryQuaternion(GetMember(os, "Rotation"), out q);
        }
        static void SetRotation(object entry, UnityEngine.Quaternion q)
        {
            if (PawnFast.SetRotation != null) { PawnFast.SetRotation(entry, q); return; }
            var os = GetMember(entry, "ObjectSpace");
            if (os == null) return;
            var ro = GetMember(os, "Rotation");
            if (ro is UnityEngine.Quaternion) SetMember(os, "Rotation", q);
            else if (ro != null)
            {   // a non-Unity quaternion struct: write the components onto the boxed value, then put it back
                SetMember(ro, "x", q.x); SetMember(ro, "y", q.y); SetMember(ro, "z", q.z); SetMember(ro, "w", q.w);
                SetMember(os, "Rotation", ro);
            }
            SetMember(entry, "ObjectSpace", os);
        }
        // Pose slot: id + time (+ weight) written straight into the entry's PoseN struct — no intermediate box, no write-back.
        static void SetPose(object entry, int slot, uint animId, float time)
        {
            if (PawnFast.Ready) { PawnFast.PoseAnimId[slot](entry, animId); PawnFast.PoseTime[slot](entry, time); return; }
            var pose = GetMember(entry, PoseNames[slot]); if (pose == null) return;
            SetMember(pose, "AnimationId", animId); SetMember(pose, "Time", time);
            SetMember(entry, PoseNames[slot], pose);
        }
        static void SetPoseWeight(object entry, int slot, float weight)
        {
            if (PawnFast.Ready) { PawnFast.PoseWeight[slot](entry, weight); return; }
            var pose = GetMember(entry, PoseNames[slot]); if (pose == null) return;
            SetMember(pose, "Weight", weight);
            SetMember(entry, PoseNames[slot], pose);
        }
        // An aim-layer slot (BoneRotation0..3): bone index + axis + angle, written straight into the entry.
        static void SetBoneRotation(object entry, int slot, uint boneIndex, uint axis, float angle)
        {
            if (PawnFast.BRReady) { PawnFast.BRBone[slot](entry, boneIndex); PawnFast.BRAxis[slot](entry, axis); PawnFast.BRAngle[slot](entry, angle); return; }
            var br = GetMember(entry, BoneRotationNames[slot]); if (br == null) return;
            SetMember(br, "SkeletonBoneIndex", boneIndex); SetMember(br, "AxisIndex", axis); SetMember(br, "Angle", angle);
            SetMember(entry, BoneRotationNames[slot], br);
        }
        static int ReadSkelId(object entry) => PawnFast.Ready ? PawnFast.SkelId(entry) : Convert.ToInt32(GetMember(entry, "SkeletonId"));
        static int ReadDescId(object entry) => PawnFast.Ready ? PawnFast.DescId(entry) : Convert.ToInt32(GetMember(entry, "PawnDescriptorId"));
        static void WriteSkelId(object entry, int id) { if (PawnFast.Ready) PawnFast.SetSkelId(entry, id); else SetMember(entry, "SkeletonId", id); }
        static void WriteHideFactor(object entry, float f) { if (PawnFast.SetHideFactor != null) PawnFast.SetHideFactor(entry, f); else SetMember(entry, "HideFactor", f); }
    }
}
