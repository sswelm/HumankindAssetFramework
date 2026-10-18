// BlenderRigExport.cs - the GLB of rig_anim.py's stage 4 scene as Blender's glTF exporter would write it (step (f) of
// the rig_anim port, 2026-10-14; docs/Review-Backlog.md item (4)): the FBX-free route's file. The structure
// BlenderDeployExport makes for the deploy scene is made here from BlenderRigAnim's stage 4 state, and BlenderGlbWriter
// writes the bytes of it. The export runs as tools/riganim-drill/blender_riganim_dump.py's export step does: the scene's
// frame range widened to every action's rounded range, the first frame evaluated (the script's own FBX export pins the
// pose so before each clip), `export_scene.gltf(GLB, animations, frame range, Y up)` as deploy_convert.py calls it.
// Held to Blender by `riganim.exe --export`: the dump's X rows (the file Blender wrote, read back by glb_rows.py).
// Read in the exporter (io_scene_gltf2/blender/exp, Blender 5.1.2), beyond what BlenderDeployExport already holds:
//   * tree.py: the scene's roots in bpy.data.objects order (BlenderDeploy.IdNameCmp), an object's children in that
//     order; an armature's children are its objects, then its root bones (the bone list's order), then the neutral
//     bone; a bone's children are its bone children, then the objects parented to it (parent_type BONE); such an
//     object's matrix_world is re-based on the REST bone when export_rest_position_armature holds (the default):
//     rest @ pose.inverted_safe() @ matrix_world, rest and pose the bone's matrix_local and pose matrix with the
//     armature's world and the Y-up basis change on each side (the pose matrix the scene's current one, after the
//     export's frame_set); its children would carry that delta too (not modelled: named);
//   * exporter.py: a node is numbered after its members - children, mesh, skin -, a skin after its joints (each
//     bone after its bone children), the neutral bone last; a second armature's bones are nodes under it, and a skin
//     is written only when a mesh node uses it - else it is gathered LAST, after the animation export (tree.py
//     get_unused_skins), from the scene the sampler left: left by name (measured on three fixtures: neither the first
//     frame's posed nor its rest world gives those matrices);
//   * nodes.py: a skinned mesh node has no transform; a mesh under an armature without the modifier would have one
//     against the armature's world (named, not reached after stage 4's rebind); a node under a bone decomposes
//     against the bone's re-based world; a joint against its parent joint's, or the armature's for a root bone,
//     without the normalization and the snapping (VehicleProbe.ExporterTrs, joint);
//   * primitive_extract.py __get_bone_data: a vertex's bones are its groups above 0.0001 that name a joint of the
//     skin (a group naming no bone - the root collapse's victim, the cap's - counts for nothing), sorted by weight,
//     the strongest four normalized (BlenderExport.VertexBones); a vertex without one takes the neutral bone;
//   * action.py (ACTIONS mode): one animation per action with a slot of an exported armature whose curves reach the
//     scene's range, sorted by name lower-cased; its channels are three per bone of every armature the action has a
//     slot for (optimize_animation_keep_anim_armature keeps the constant ones) - their samples are part (f3)'s;
//   * the albedo (stage 4's image.save): the image's export name becomes the saved file's stem and its type image/png;
//     a PNG source's bytes stay its own (measured on the fixtures and the registry), a JPEG source's become the PNG
//     Blender's encoder wrote - unknown here (null: the IMG row by name and type, the GLB's bytes without it).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public static class BlenderRigExport
{
    static readonly float[] Basis = { 1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1 };   // row-major items: (x, y, z) -> (x, z, -y)

    sealed class VNode
    {
        public string Name;
        public BlenderRigAnim.SceneObject Object;                    // an object node, or null for a bone
        public int Bone = -1; public BlenderRigAnim.SceneObject Armature;   // a bone: its node id and its armature object
        public bool Neutral;
        public VNode Parent; public readonly List<VNode> Children = new List<VNode>();
        public float[] World;                                        // the exporter's matrix_world, column-major
        public int Index = -1;                                       // the glTF node index once serialized
    }

    static int PyRound(float v) => (int)Math.Round((double)v, MidpointRounding.ToEven);

    /// <summary>The export of the stage 4 scene (RunStage4 ran through). Evaluates the scene at the widened range's first
    /// frame, as the export step does.</summary>
    public static BlenderDeployExport.Result Build(BlenderRigAnim.Stage1 s, BlenderRigAnim.Args a)
        => BuildCore(s, false, null);

    public static BlenderDeployExport.Result BuildVehicle(BlenderRigAnim.Stage1 s, Action<int> evaluateFrame) => BuildCore(s, true, evaluateFrame);

    static BlenderDeployExport.Result BuildCore(BlenderRigAnim.Stage1 s, bool vehicle, Action<int> evaluateFrame)
    {
        var x = new BlenderDeployExport.Result();
        if (s.Fallback != null) { x.Fallback = s.Fallback; return x; }
        if (s.Exits) { x.Fallback = "the script exits before any export"; return x; }
        var m = s.Model; var names = s.Names; var rig = s.Rig;

        // ---- the export step: the scene's range over every action's rounded range, the first frame evaluated
        if (!vehicle && s.Actions.Count > 0)
        {
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var act in s.Actions) { lo = Math.Min(lo, PyRound(act.FrameStart)); hi = Math.Max(hi, PyRound(act.FrameEnd)); }
            s.FrameStart = lo; s.FrameEnd = hi;
        }
        if (vehicle) evaluateFrame(0); else BlenderRigAnim.EvaluateFrame(s, s.FrameStart);
        x.SceneStart = s.FrameStart; x.SceneEnd = s.FrameEnd;
        if (vehicle && s.Actions.Count > 0) { x.SceneStart = s.Actions.Min(act => ActionRange(act).start); x.SceneEnd = s.Actions.Max(act => ActionRange(act).end); }
        if (!vehicle) x.Log.Add("RIGANIM glb: " + s.Actions.Count.ToString(CultureInfo.InvariantCulture) + " action(s), frames " + s.FrameStart.ToString(CultureInfo.InvariantCulture) + ".." + s.FrameEnd.ToString(CultureInfo.InvariantCulture));

        // ---- the vtree: objects in bpy.data.objects order, every armature's bones
        var objs = s.Objects.ToList(); BlenderExportTree.StableSort(objs, (p, q) => BlenderDeploy.IdNameCmp(p.Name, q.Name));
        var byObj = new Dictionary<BlenderRigAnim.SceneObject, VNode>();
        foreach (var o in objs) byObj[o] = new VNode { Name = o.Name, Object = o, World = o.World };
        var boneNodes = new Dictionary<int, VNode>();
        var armOf = new Dictionary<BlenderRigAnim.SceneObject, (VehicleProbe.ArmatureResult rest, List<int> bones)>();
        foreach (var o in objs.Where(o => o.Type == "ARMATURE"))
        {
            bool primary = s.Armature != null && o == s.Armature.Object;
            var rest = primary ? s.Armature.Rest : rig.Armatures[o.Source.GltfNode];
            var bones = primary ? s.Armature.BoneNodes : BlenderRigAnim.DepthFirst(rest.Bones, rest.Parent);
            armOf[o] = (rest, bones);
            foreach (int b in bones) boneNodes[b] = new VNode { Name = names.BoneOfJoint[b], Bone = b, Armature = o };
        }
        // a bone's matrix_world: the armature NODE's world (re-based when the armature hangs from a bone) @ matrix_local @ the basis change
        void BoneWorlds(BlenderRigAnim.SceneObject o)
        {
            var (rest, bones) = armOf[o]; var armRM = VehicleProbe.ToRowMajor(byObj[o].World);
            foreach (int b in bones) boneNodes[b].World = VehicleProbe.ToColumnMajor(VehicleProbe.MatMulMathutils(VehicleProbe.MatMulMathutils(armRM, VehicleProbe.ToRowMajor(rest.ArmMat[b])), Basis));
        }
        foreach (var o in armOf.Keys) if (o.ParentBone == null) BoneWorlds(o);
        // objects under objects (an armature's object children before its bones)
        foreach (var o in objs)
            if (o.Parent != null && o.ParentBone == null) { if (!byObj.TryGetValue(o.Parent, out var p)) { x.Fallback = "'" + o.Name + "': a parent outside the scene"; return x; } p.Children.Add(byObj[o]); byObj[o].Parent = p; }
        // bones under their armature or parent bone
        foreach (var kv in armOf)
        {
            var (rest, bones) = kv.Value; var armNode = byObj[kv.Key];
            foreach (int b in bones)
            {
                int p = rest.Parent.TryGetValue(b, out var pp) ? pp : -1;
                var parentNode = p >= 0 && bones.Contains(p) ? boneNodes[p] : armNode;
                parentNode.Children.Add(boneNodes[b]); boneNodes[b].Parent = parentNode;
            }
        }
        // objects under bones, after the bone's bone children: their world re-based on the rest bone
        foreach (var o in objs)
        {
            if (o.Parent == null || o.ParentBone == null) continue;
            if (!boneNodes.TryGetValue(o.ParentBoneNode, out var bn)) { x.Fallback = "'" + o.Name + "': hung from a bone the export has no node for"; return x; }
            if (objs.Any(c => c.Parent == o)) { x.Fallback = "'" + o.Name + "': an object hung from a bone with children of its own (their delta is not modelled)"; return x; }
            bn.Children.Add(byObj[o]); byObj[o].Parent = bn;
            if (bn.World == null) { x.Fallback = "'" + o.Name + "': hung from a bone of an armature that hangs from a bone itself (the delta's order is not modelled)"; return x; }
            var armRM = VehicleProbe.ToRowMajor(byObj[bn.Armature].World);
            if (!s.Armature.PoseMats.TryGetValue(o.ParentBoneNode, out var poseMat)) { x.Fallback = "'" + o.Name + "': hung from a bone without a pose matrix"; return x; }
            var pose = VehicleProbe.MatMulMathutils(VehicleProbe.MatMulMathutils(armRM, VehicleProbe.ToRowMajor(poseMat)), Basis);
            var rest = VehicleProbe.ToRowMajor(bn.World);
            var delta = VehicleProbe.MatMulMathutils(VehicleProbe.InvertedSafe(pose), VehicleProbe.ToRowMajor(o.World));   // _delta = _pose.inverted_safe() @ matrix_world
            byObj[o].World = VehicleProbe.ToColumnMajor(VehicleProbe.MatMulMathutils(rest, delta));                             // _rest @ _delta
            if (o.Type == "ARMATURE") BoneWorlds(o);
        }

        // ---- the meshes: primitives with the real weights; the neutral bone per armature
        var matKey = new Dictionary<string, (int material, bool vc)>(StringComparer.Ordinal);
        foreach (var kv in names.MaterialOf) if (!matKey.ContainsKey(kv.Value)) matKey[kv.Value] = (kv.Key.material, kv.Key.vertexColor);
        var prims = new Dictionary<BlenderRigAnim.SceneObject, List<BlenderExport.Primitive>>();
        var slotsOf = new Dictionary<BlenderRigAnim.SceneObject, List<(int, bool)>>();
        var armatureOfMesh = new Dictionary<BlenderRigAnim.SceneObject, BlenderRigAnim.SceneObject>();
        var needNeutral = new Dictionary<BlenderRigAnim.SceneObject, bool>();
        foreach (var o in objs.Where(o => o.Type == "MESH"))
        {
            var me = o.Mesh;
            if (me.ArmatureModifier == null) { x.Fallback = "'" + o.Name + "': a mesh without an Armature modifier at the export"; return x; }
            var armObj = objs.FirstOrDefault(q => q.Type == "ARMATURE" && q.Name == me.ArmatureModifier);
            if (armObj == null) { x.Fallback = "'" + o.Name + "': its Armature modifier names no armature of the scene"; return x; }
            if (o.Parent != armObj) { x.Fallback = "'" + o.Name + "': a mesh that is not under its armature at the export"; return x; }
            int nf = me.Faces.Length / 3;
            var r = new BlenderReduce.Result
            {
                VertexCount = me.Vertices, FaceCount = nf, Positions = me.Positions, Faces = me.Faces, Edges = me.Edges ?? new int[0], FaceMaterial = me.FaceMaterial,
                FaceSharp = me.FaceSharp ?? new bool[nf], EdgeSharp = vehicle ? me.EdgeSharp : null, CustomNormal = me.CustomNormal, DefNr = me.DefNr, DefWeight = me.DefWeight, Ratio = 1f,
            };
            foreach (var uv in me.UvAll) r.Uv.Add(uv);
            foreach (var c in me.Colors) r.Colors.Add(c);
            r.ActiveColor = me.ActiveColor;
            foreach (var name in me.Materials)
            {
                if (name == null) { r.Slots.Add((-1, false)); r.SlotAlpha.Add(("OPAQUE", 0.5, 1.0)); continue; }
                if (!matKey.TryGetValue(name, out var key)) { x.Fallback = "'" + o.Name + "': the material '" + name + "' is none the importer made"; return x; }
                r.Slots.Add(key);
                r.SlotAlpha.Add(key.material >= 0 ? (m.Materials[key.material].AlphaMode, m.Materials[key.material].AlphaCutoff, BlenderReduce.MaterialAlphaFactor(m.Materials[key.material])) : ("OPAQUE", 0.5, 1.0));
            }
            if (vehicle && r.Slots.Count == 0) { r.Slots.Add((-1, false)); r.SlotAlpha.Add(("OPAQUE", .5, 1)); }
            var (rest, bones) = armOf[armObj];
            var jointOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < bones.Count; i++) jointOf[names.BoneOfJoint[bones[i]]] = i;
            var groupJoint = me.Groups.Select(g => jointOf.TryGetValue(g, out int j) ? j : -1).ToArray();
            var skin = new BlenderExport.Skin { ObjectWorld = o.World, ArmatureWorld = armObj.World, GroupJoint = groupJoint, JointCount = bones.Count };
            var p = BlenderExport.MeshPrimitives(r, skin);
            prims[o] = p; slotsOf[o] = r.Slots;
            // no vertex groups: the joint attributes are written (every vertex on the would-be neutral index) but the mesh gets
            // no skin (skins.py gathers none without vertex groups) and no neutral bone (measured, fx_s4_rebind)
            if (vehicle && me.Vertices > 0) { BlenderExport.VertexBones(r, skin, out _, out _, out bool missing); if (missing) needNeutral[armObj] = true; }
            if (me.Groups.Count == 0) continue;
            armatureOfMesh[o] = armObj;
            bool neutral = false;
            if (!vehicle && p.Count > 0) neutral = p[0].NeutralBone;
            else if (me.Vertices > 0) BlenderExport.VertexBones(r, skin, out _, out _, out neutral);
            if (neutral) needNeutral[armObj] = true;
        }
        // the neutral joint is added only to a skin a mesh node uses
        foreach (var armObj in needNeutral.Keys.ToList()) if (!prims.Any(kv => armatureOfMesh.TryGetValue(kv.Key, out var ao) && ao == armObj && kv.Value.Count > 0)) needNeutral[armObj] = false;
        var neutralOf = new Dictionary<BlenderRigAnim.SceneObject, VNode>();
        foreach (var kv in armOf)
            if (needNeutral.TryGetValue(kv.Key, out bool nb) && nb)
            {
                var v = new VNode { Name = "neutral_bone", Neutral = true, Armature = kv.Key, Parent = byObj[kv.Key] };
                byObj[kv.Key].Children.Add(v); neutralOf[kv.Key] = v;
            }
        x.NeutralBone = neutralOf.Count > 0;

        // ---- the serializer's walk
        var skinIndexOf = new Dictionary<BlenderRigAnim.SceneObject, int>();
        BlenderDeployExport.Skin MakeSkin(BlenderRigAnim.SceneObject armObj)
        {
            var (rest, bones) = armOf[armObj];
            var sk = new BlenderDeployExport.Skin { Name = armObj.Name };
            var armRM = VehicleProbe.ToRowMajor(armObj.World);
            foreach (int b in bones)
            {
                sk.Joints.Add(boneNodes[b].Index);
                sk.InverseBind.Add(VehicleProbe.ToColumnMajor(VehicleProbe.InvertedSafe(VehicleProbe.MatMulMathutils(Basis, VehicleProbe.MatMulMathutils(armRM, VehicleProbe.ToRowMajor(rest.ArmMat[b]))))));
            }
            if (neutralOf.TryGetValue(armObj, out var nv))
            {
                VehicleProbe.NeutralBone(armObj.World, out _, out _, out _, out var ibm);
                sk.Joints.Add(nv.Index); sk.InverseBind.Add(ibm);
            }
            return sk;
        }
        int MaterialIndex(string blenderName)
        {
            int mi = x.Materials.IndexOf(blenderName);
            if (mi < 0) { mi = x.Materials.Count; x.Materials.Add(blenderName); }
            return mi;
        }
        void Serialize(VNode v)
        {
            if (v.Index >= 0) return;
            foreach (var c in v.Children) Serialize(c);
            var n = new BlenderDeployExport.Node { Name = v.Name, Joint = v.Bone >= 0, Neutral = v.Neutral, World = v.World };
            if (v.Neutral) VehicleProbe.NeutralBone(v.Armature.World, out n.Translation, out n.Rotation, out n.Scale, out _);
            if (v.Object != null && v.Object.Type == "MESH" && prims[v.Object].Count > 0)
            {
                var p = prims[v.Object];
                var mesh = new BlenderDeployExport.Mesh { Name = v.Object.DataName, Primitives = p, Slots = slotsOf[v.Object] };
                foreach (var prim in p) { string matName = prim.MaterialSlot < v.Object.Mesh.Materials.Count ? v.Object.Mesh.Materials[prim.MaterialSlot] : null; mesh.Material.Add(matName == null ? -1 : MaterialIndex(matName)); }
                n.Mesh = x.Meshes.Count; x.Meshes.Add(mesh);
                if (armatureOfMesh.TryGetValue(v.Object, out var armObj))
                {
                    if (!skinIndexOf.TryGetValue(armObj, out int si))
                    {
                        foreach (int b in armOf[armObj].bones) Serialize(boneNodes[b]);
                        if (neutralOf.TryGetValue(armObj, out var nv)) Serialize(nv);
                        si = x.Skins.Count; x.Skins.Add(MakeSkin(armObj)); skinIndexOf[armObj] = si;
                    }
                    n.Skin = si;
                }
            }
            x.Nodes.Add(n); v.Index = x.Nodes.Count - 1;
            foreach (var c in v.Children) x.Nodes[c.Index].Parent = v.Index;
            foreach (var c in v.Children) n.Children.Add(c.Index);
        }
        var roots = objs.Where(o => o.Parent == null).Select(o => byObj[o]).ToList();
        foreach (var r in roots) Serialize(r);
        x.Scenes.Add(("Scene", roots.Select(r => r.Index).ToList()));
        // a skin no mesh node uses (a second armature after the join took its mesh): the exporter gathers it LAST, after the
        // animation export, and its inverse bind matrices read the armature's matrix_world as the sampler left the scene
        // (measured: neither the posed nor the rest world of the export's first frame gives them) - left by name
        foreach (var kv in armOf)
            if (!skinIndexOf.ContainsKey(kv.Key) && !objs.Any(o => o.Type == "MESH" && o.Mesh.ArmatureModifier == kv.Key.Name))
            { x.Fallback = "'" + kv.Key.Name + "': an armature no mesh uses - its skin is written after the animation export, from the scene the sampler leaves (not modelled)"; return x; }

        // ---- the transforms: objects against their parent's exporter world, joints against their parent joint's or the armature's
        foreach (var v in byObj.Values.Concat(boneNodes.Values))
        {
            var n = x.Nodes[v.Index];
            if (v.Object != null)
            {
                if (n.Skin >= 0) continue;   // a skinned mesh node: no transform
                if (v.Parent != null && v.Parent.Object != null && x.Nodes[v.Parent.Index].Skin >= 0) { x.Fallback = "'" + v.Name + "': a child of a skinned mesh node (its transform against the armature is not modelled)"; return x; }
                VehicleProbe.ExporterTrs(v.Parent?.World, v.World, out n.Translation, out n.Rotation, out n.Scale);
            }
            else
            {
                var parentWorld = v.Parent.Bone >= 0 ? v.Parent.World : v.Armature.World;
                VehicleProbe.ExporterTrs(parentWorld, v.World, out n.Translation, out n.Rotation, out n.Scale, joint: true);
            }
        }

        // ---- the animations: every action with a slot of an exported armature whose curves reach the scene's range
        var animated = new List<string>();
        foreach (var act in s.Actions)
        {
            bool armatureSlot = act.Slots.Any(sl => vehicle ? sl.FCurves.Any(fc => fc.Bone != null) : sl.Object != null && sl.Object.Type == "ARMATURE" && byObj.ContainsKey(sl.Object));
            if (!armatureSlot) continue;
            var (start, end) = ActionRange(act);
            if (Math.Max(s.FrameStart, start) <= Math.Min(s.FrameEnd, end)) animated.Add(act.Name);
        }
        BlenderExportTree.StableSort(animated, (p, q) => BlenderRigAnim.PyStringOrder.Compare(BlenderRigAnim.PyLower(p), BlenderRigAnim.PyLower(q)));
        x.Animations.AddRange(animated);

        // ---- the animations' channels as the exporter samples them (BlenderChannelExport's sampler over the rig's Spec)
        x.ChannelRows = BlenderChannelExport.Build(ChannelSpec(s, x, vehicle), x, x.SceneStart, x.SceneEnd);
        if (x.ChannelRows.Fallback != null) { x.Fallback = x.ChannelRows.Fallback; return x; }
        // ---- the materials, textures, samplers and images as the exporter writes them
        // Texture nodes refer to UV layer names on the final mesh. The join may add a layer which a material's
        // original mesh lacked; selecting from the imported meshes would incorrectly clamp that texture to UVMap.
        var materialUvs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in objs.Where(o => o.Type == "MESH"))
            foreach (var name in o.Mesh.Materials.Where(name => name != null))
                materialUvs[name] = o.Mesh.UvAll.Count;
        x.MaterialRows = BlenderMaterialExport.Build(m, names, x.Materials, materialUvs);
        if (x.MaterialRows.Fallback != null) { x.Fallback = x.MaterialRows.Fallback; return x; }
        // the albedo: stage 4 saved the image as a PNG (image.save), so the exporter names it after the file's stem and
        // packs the bytes Blender's encoder wrote - unknown here (null: compared by name and type)
        if (s.AlbedoImage >= 0 && s.Albedo.path != null && s.AlbedoBlenderName != null)
            for (int i = 0; i < x.MaterialRows.Images.Count; i++)
            {
                var im = x.MaterialRows.Images[i];
                if (BlenderRigAnim.ImageName(s, im.source, out _) == s.AlbedoBlenderName)
                    x.MaterialRows.Images[i] = (System.IO.Path.GetFileNameWithoutExtension(s.Albedo.path), "image/png", im.mime == "image/png" ? im.bytes : null, im.source);
            }
        return x;
    }

    /// <summary>The sampler's view of the primary armature: its bones in the vtree's walk (ad.BoneNodes is the bone list's order;
    /// root bones in that order, each followed by its children), every bone in QUATERNION mode (the importer's), the pose the
    /// script left (PoseProps), the actions with a slot of the armature sorted by name lower-cased, the active one, and each
    /// action's curves - a curve per (bone, channel) of its armature slot, evaluated as the depsgraph does (LINEAR and
    /// CONSTANT segments included). An action with curves on any other object (the armature's own object channels, a
    /// second armature) is left by name.</summary>
    static BlenderChannelExport.Spec ChannelSpec(BlenderRigAnim.Stage1 s, BlenderDeployExport.Result x, bool vehicle)
    {
        var ad = s.Armature; var armObj = ad.Object; var names = s.Names;
        var spec = new BlenderChannelExport.Spec { Active = armObj.Action };
        var nodeOfBone = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (int b in ad.BoneNodes) nodeOfBone[names.BoneOfJoint[b]] = b;
        spec.ModeOf = bone => vehicle && ad.RotMode.TryGetValue(nodeOfBone[bone], out var mode) ? mode : "QUATERNION";
        spec.HeldOf = bone =>
        {
            var p = ad.PoseProps[nodeOfBone[bone]];
            var e = vehicle && ad.PoseEuler.TryGetValue(nodeOfBone[bone], out var eul) ? eul : new float[3];
            return new[] { p[0][0], p[0][1], p[0][2], p[1][0], p[1][1], p[1][2], p[1][3], p[2][0], p[2][1], p[2][2], e[0], e[1], e[2] };
        };
        var specOf = new Dictionary<int, BlenderChannelExport.BoneSpec>();
        void Visit(int parent)
        {
            foreach (int b in ad.BoneNodes)
            {
                int p = ad.Rest.Parent.TryGetValue(b, out var pp) && ad.BoneNodes.Contains(pp) ? pp : -1;
                if (p != parent) continue;
                var bs = new BlenderChannelExport.BoneSpec { Name = names.BoneOfJoint[b], MatrixLocal = ad.Rest.ArmMat[b], Offs = ad.Rest.OffsBone[b], Parent = parent >= 0 ? specOf[parent] : null };
                specOf[b] = bs; spec.Walk.Add(bs); Visit(b);
            }
        }
        Visit(-1);
        foreach (var act in s.Actions) if (act.Slots.Any(sl => vehicle ? sl.FCurves.Any(fc => fc.Bone != null) : sl.Object == armObj)) spec.Visited.Add(act.Name);
        BlenderExportTree.StableSort(spec.Visited, (p, q) => BlenderRigAnim.PyStringOrder.Compare(BlenderRigAnim.PyLower(p), BlenderRigAnim.PyLower(q)));
        spec.SourceOf = name =>
        {
            var act = s.Actions.FirstOrDefault(a => a.Name == name);
            if (act == null) return null;
            var src = new BlenderChannelExport.Source { Name = name, Lo = int.MaxValue, Hi = int.MinValue };
            var curves = new Dictionary<(string bone, int channel), List<BlenderRigAnim.KeyData>>();
            foreach (var sl in act.Slots)
            {
                // a slot of an object the scene no longer holds (joined away, flattened) animates nothing; one of a live object does
                if (!vehicle && sl.Object != armObj) { if (sl.FCurves.Count > 0 && sl.Object != null && s.Objects.Contains(sl.Object)) throw new InvalidOperationException("'" + name + "': curves on '" + sl.Object.Name + "' beside the armature's (its object channels, or a second armature, are not modelled)"); continue; }
                foreach (var fc in sl.FCurves)
                {
                    if (fc.Bone == null) throw new InvalidOperationException("'" + name + "': an object-level curve on the armature (" + fc.DataPath + ": the armature node's channels are not modelled)");
                    int c = fc.Prop == "location" ? fc.Index : fc.Prop == "rotation_quaternion" ? 3 + fc.Index : fc.Prop == "scale" ? 7 + fc.Index : fc.Prop == "rotation_euler" ? 10 + fc.Index : -1;
                    if (c < 0) throw new InvalidOperationException("'" + name + "': a curve of " + fc.Prop + " (not modelled)");
                    curves[(fc.Bone, c)] = fc.Keys;
                    foreach (var k in fc.Keys) { int f = (int)k.Frame; if (f < src.Lo) src.Lo = f; if (f > src.Hi) src.Hi = f; src.Any = true; }
                }
            }
            var (start, end) = ActionRange(act); src.Lo = start; src.Hi = end; src.Any = true;   // the exporter's range: int() of frame_range, the single frame's +1 undone
            src.Has = (bone, c) => curves.ContainsKey((bone, c));
            // Vehicle actions can key every bone on every frame. Convert each curve once rather than
            // cloning all its keys on every sample (quadratic allocation on a long rowing/rotor clip).
            var vehicleCurves = vehicle ? curves.ToDictionary(kv => kv.Key, kv => BlenderRigAnim.ToArm(kv.Value)) : null;
            src.Eval = (bone, c, f) => vehicle ? BlenderFCurve.Evaluate(vehicleCurves[(bone, c)], f, constant: true) : BlenderRigAnim.EvaluateCurve(curves[(bone, c)], (float)f);
            src.Keys = (bone, c) => curves.TryGetValue((bone, c), out var keys) ? BlenderRigAnim.ToArm(keys) : null;
            src.DenseCount = (bone, c) => 0;
            return src;
        };
        return spec;
    }

    /// <summary>For the drill's row dump: every armature's bones with their matrix_local (row-major items, hex) and the object's world.</summary>
    public static List<string> DebugBoneRows(BlenderRigAnim.Stage1 s)
    {
        var rows = new List<string>();
        string H(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");
        foreach (var o in s.Objects.Where(o => o.Type == "ARMATURE"))
        {
            bool primary = s.Armature != null && o == s.Armature.Object;
            var rest = primary ? s.Armature.Rest : s.Rig.Armatures[o.Source.GltfNode];
            var bones = primary ? s.Armature.BoneNodes : BlenderRigAnim.DepthFirst(rest.Bones, rest.Parent);
            rows.Add("XW\t" + o.Name + "\t" + string.Join(" ", VehicleProbe.ToRowMajor(o.World).Select(H)));
            foreach (int b in bones) rows.Add("XBM\t" + o.Name + "/" + s.Names.BoneOfJoint[b] + "\t" + string.Join(" ", VehicleProbe.ToRowMajor(rest.ArmMat[b]).Select(H)));
        }
        return rows;
    }

    /// <summary>prepare_actions_range: int() of the action's frame_range - the first and the last key over its curves, (0, 0)
    /// without one -, a single frame's +1 (BKE_action_frame_range_calc) undone by the exporter's own check.</summary>
    internal static (int start, int end) ActionRange(BlenderRigAnim.ActionData act)
    {
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        foreach (var fc in act.FCurves) foreach (var k in fc.Keys) { if (k.Frame < lo) lo = k.Frame; if (k.Frame > hi) hi = k.Frame; }
        if (float.IsInfinity(lo)) return (0, 0);
        int start = (int)act.FrameStart, end = (int)act.FrameEnd;
        if (end - start == 1 && (int)lo == (int)hi) end = start;
        return (start, end);
    }

    /// <summary>The channels an animation carries for the ANIM row: three per bone of every armature the action has a slot
    /// for (keep_anim_armature keeps the constant ones).</summary>
    public static int ChannelCount(BlenderRigAnim.Stage1 s, string animation)
    {
        var act = s.Actions.FirstOrDefault(a => a.Name == animation);
        if (act == null) return 0;
        int bones = 0;
        foreach (var o in act.Slots.Select(sl => sl.Object).Where(o => o != null && o.Type == "ARMATURE").Distinct())
        {
            bool primary = s.Armature != null && o == s.Armature.Object;
            bones += primary ? s.Armature.BoneNodes.Count : s.Rig.Armatures[o.Source.GltfNode].Bones.Count;
        }
        return 3 * bones;
    }
}
