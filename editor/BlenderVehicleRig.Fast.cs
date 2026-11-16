// BlenderVehicleRig.Fast.cs - vehicle_rig.py's SOURCE-SKELETON FAST PATH (mode "rigfast", lines 1185-1276) in C# (2026-11-16).
// The source already carries an artist skeleton with its weights: nothing is flattened, cut or rigged. The script
//   - refuses the options that need mesh parts (oars, rotors, the wave rock, Flip, Two-sided, the sail fold: a VEHICLE ERROR and
//     exit 1; a per-part placement is a WARN), a second model before that, a source without an armature;
//   - makes a new "Spin" action on the FIRST armature and keys each named wheel BONE at frame 0 and at the clip's end: the bone
//     goes to XYZ, turning about the one of its own axes that lies closest to the wheels' axle direction, signed so every wheel
//     turns the same world way;
//   - strips every object that is neither a mesh with vertices nor that armature (a kept child of a removed parent keeps its
//     world), prints the bone-wall warning, exports, and exits 0 - inside what the drill calls stage 1.
// The scene before it is RunStage1's: the import, the purge, the brightness, the argument checks.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public static partial class BlenderVehicleRig
{
    const string Dash = "—";

    /// <summary>The fast path on the scene RunStage1 built. Sets s.Exits with the script's lines for a refusal, s.Fallback for what
    /// is not modelled, or s.FastExport with the "VEHICLE RIG DONE: FAST PATH" line last in s.Logs.</summary>
    static BlenderRigAnim.Stage1 RunFast(BlenderRigAnim.Stage1 s, Args a, bool preserveJpegAlpha)
    {
        bool Has(string role) => a.Names.TryGetValue(role, out var l) && l.Count > 0;
        BlenderRigAnim.Stage1 Refuse(string line) { s.Logs.Add(line); s.Exits = true; Note(s, "the fast path refuses an option that needs mesh parts"); return s; }
        if (a.PartTx != null)
            s.Logs.Add("VEHICLE WARN: per-part placement is a mesh-path feature " + Dash + " the source-skeleton fast path spins bones and leaves the meshes as authored; disable the fast path to apply it");
        if (Has("oars")) return Refuse("VEHICLE ERROR: Oar recovery needs mesh parts; disable the source-skeleton fast path and probe the merged oar meshes");
        if (Has("rotors") || Has("tailrotors"))
            return Refuse("VEHICLE ERROR: Rotor / Tail rotor roles are ignored by the source-skeleton fast path " + Dash + " mark the spinning source BONE as Wheel (fast-path bones spin about their own axis), or disable the fast path to rig the mesh parts");
        if (a.RockOn) return Refuse("VEHICLE ERROR: Wave rock is not supported on the source-skeleton fast path " + Dash + " disable the fast path to rig the mesh with a rocking hull");
        if (Has("flip")) return Refuse("VEHICLE ERROR: Flip (winding surgery) is not supported on the source-skeleton fast path " + Dash + " rigfast exports the source geometry as-is; disable the fast path to flip mesh parts");
        if (Has("twosided")) return Refuse("VEHICLE ERROR: Two-sided needs mesh parts " + Dash + " disable the source-skeleton fast path to repair the marked faces");
        if (a.SailFold) return Refuse("VEHICLE ERROR: Fold sail at idle needs the generated Sail fold chain (new bones + band skinning) " + Dash + " the source-skeleton fast path keeps the artist skeleton; disable the fast path or the fold option");

        var arms = s.Objects.Where(o => o.Type == "ARMATURE").ToList();
        if (arms.Count == 0) return Refuse("VEHICLE ERROR: rigfast requested but the source has no armature");
        var ad = s.Armature; var arm = arms[0];
        if (ad == null || ad.Object != arm || arms.Count > 1) { s.Fallback = "the source-skeleton fast path on a source of several armatures is not modelled yet"; return s; }
        if (a.Frames > MaxClipFrames) { s.Fallback = "the source-skeleton fast path's clip of more than 100,000 frames is not modelled yet"; return s; }
        int frames = (int)a.Frames;
        var names = s.Names;
        var boneId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (int b in ad.BoneNodes) if (!boneId.ContainsKey(names.BoneOfJoint[b])) boneId[names.BoneOfJoint[b]] = b;
        var meshes = s.Objects.Where(o => o.Type == "MESH" && o.Mesh != null && o.Mesh.Vertices > 0).ToList();   // mesh_objects()
        float[] refAxis = a.AxisArg == "X" ? new[] { 1f, 0f, 0f } : a.AxisArg == "Z" ? new[] { 0f, 0f, 1f } : new[] { 0f, 1f, 0f };

        // the new action, the armature's active one from here on (the import's own clips stay in the file and are exported too)
        var pool = new BlenderNames.NamePool();
        foreach (var x in s.Actions) pool.Unique(x.Name);
        var spin = new VehicleActionKeys(s, pool, "Spin");
        arm.Action = spin.Data.Name; arm.Slot = null;
        s.FrameStart = 0; s.FrameEnd = frames;
        var spun = new List<(string bone, string axis)>();
        var armRow = VehicleProbe.ToRowMajor(arm.World);
        foreach (string bn in a.Names["wheels"])
        {
            if (!boneId.TryGetValue(bn, out int b))
                return Refuse("VEHICLE ERROR: spin bone '" + bn + "' not found. Bones: [" + string.Join(", ", ad.BoneNodes.Select(n => PyStrRepr(names.BoneOfJoint[n]))) + "]");
            var m = MulMat4Row(armRow, ad.Rest.MatrixLocal[b]);
            int bestI = 0; double bestD = 0.0;
            for (int i = 0; i < 3; i++)
            {
                // (m3 @ unit_i).normalized().dot(ref): the matrix's column i
                var col = new float[3];
                for (int r = 0; r < 3; r++) { double dsum = 0.0; for (int k = 0; k < 3; k++) dsum += (double)(float)(m[4 * r + k] * (k == i ? 1f : 0f)); col[r] = (float)dsum; }
                double dd = Dot(Normalized(col), refAxis);
                if (Math.Abs(dd) > Math.Abs(bestD)) { bestI = i; bestD = dd; }
            }
            double sign = bestD >= 0 ? 1.0 : -1.0;
            // rotation_mode = 'XYZ': the bone's quaternion read as an Euler (BKE_rotMode_change_values), then keyed over
            if (!(ad.RotMode.TryGetValue(b, out var mode) && mode == "XYZ")) ad.PoseEuler[b] = BlenderDeploy.QuatToEulXYZ((float[])ad.PoseProps[b][1].Clone());
            ad.RotMode[b] = "XYZ";
            s.FrameCurrent = 0;
            var zero = new float[3]; ad.PoseEuler[b] = zero; spin.Insert(arm, bn, "rotation_euler", zero, 0);
            var eul = new float[3]; eul[bestI] = (float)(a.Degrees * (Math.PI / 180.0) * sign);
            s.FrameCurrent = frames;
            ad.PoseEuler[b] = eul; spin.Insert(arm, bn, "rotation_euler", eul, frames);
            int at = spun.FindIndex(x => x.bone == bn); string axis = (sign > 0 ? "+" : "-") + "XYZ"[bestI];
            if (at >= 0) spun[at] = (bn, axis); else spun.Add((bn, axis));
        }
        spin.Finish();   // every key LINEAR
        Note(s, "the fast path: the named source bones spun on the artist skeleton");

        // strip the helpers: a kept child of a removed parent keeps its world transform
        var keep = new HashSet<BlenderRigAnim.SceneObject>(meshes) { arm };
        foreach (var o in keep)
            if (o.Parent != null && !keep.Contains(o.Parent))
            {
                if (o.ParentBone != null) { s.Fallback = "the source-skeleton fast path with a kept object hung from a removed armature's bone is not modelled yet"; return s; }
                var mw = (float[])o.World.Clone(); o.Parent = null; o.ParentInverse = null; SetWorld(o, mw); Note(s, "the fast path: a kept object freed from a removed parent");
            }
        foreach (var o in s.Objects.ToList())
            if (!keep.Contains(o)) { s.Objects.Remove(o); names.ObjectPool.Remove(o.Name); Note(s, "the fast path: a helper object removed"); }
        int nb = ad.BoneNodes.Count;
        if (nb > 126)
            s.Logs.Add("VEHICLE *** WARNING: " + nb + " bones " + Dash + " past the 126-bone budget (bone indices break past 127 in-game: the geometry on the last bones collapses or spikes). Lower Tread detail (the bones dial) or mark fewer separate parts.");

        // the export of the scene as it stands
        if (VehicleProbe.Input.PyStrip(a.PreviewFbx).Length > 0) { s.Fallback = "the source-skeleton fast path's FBX preview export is not modelled yet"; return s; }
        if (!PrepareExportMaterials(s, a, out var materials, out var materialNames)) return s;
        s.Model = materials; names.MaterialOf = materialNames.MaterialOf; names.MaterialsInOrder = materialNames.MaterialsInOrder;
        var st = new RigState { Clusters = new List<(double, float[], float[], List<string>, float[], bool, bool)>(), Objs = meshes, Arm = arm };
        foreach (var kv in boneId) st.BoneId[kv.Key] = kv.Value;
        s.VehicleState = st;
        ad.PoseMats = VehiclePoseMats(ad);
        var result = BlenderRigExport.BuildVehicle(s, f => EvaluateExportFrame(s, f), preserveJpegAlpha);
        if (result.Fallback != null) { s.Fallback = "the source-skeleton fast path: the GLB export (" + result.Fallback + ") is not modelled yet"; return s; }
        s.FastExport = result;
        s.Logs.Add("VEHICLE RIG DONE: FAST PATH " + Dash + " " + a.Names["wheels"].Count + " source bone(s) spun {" + string.Join(", ", spun.Select(x => PyStrRepr(x.bone) + ": " + PyStrRepr(x.axis)))
                   + "} on the artist skeleton (" + nb + " bones, weights untouched), Spin 0.." + frames + " " + BlenderRigAnim.PyFixed(a.Degrees, 0) + " deg -> " + a.OutGlb);
        return s;
    }
}
