// BlenderExportTree.cs - the node list of the file Blender's glTF exporter writes (step 5 of replacing Blender, milestone
// d, the written file, 2026-10-07): which nodes, in which order, under which parent, with which transform - what the
// Factory's converter walks (baker/glbconv: nodes in file order, node.WorldMatrix). Read at Blender 5.1.2:
//   * the exporter's tree (io_scene_gltf2/blender/exp/tree.py): the scene's objects without a parent in the order the
//     scene holds them - creation order for an import -, each object's children in the order `bpy.data.objects` lists
//     them - by name, BLI_strcasecmp: byte by byte after an ASCII tolower, signed, so a non-ASCII byte sorts first -, an
//     armature's object children before its root bones (`pose.bones` without a parent, creation order), a bone's bone
//     children in creation order. An object parented to a BONE (nodes.py get_objects_parented_to_bones) hangs from that
//     bone's joint after the joint's bone children, in name order with its siblings; its transform, and its subtree's, are
//     the bone part's (the dug-out canoe has eleven). When a mesh of the armature has a vertex without a bone, the exporter adds a joint "neutral_bone"
//     after the armature's bones (primitive_extract.py need_neutral_bone; the layout says which meshes).
//   * a skinned mesh hangs from its armature with no transform of its own (the importer moves it there: its
//     matrix_world is the armature's); a mesh of lines or points alone is written as a node without a mesh.
//   * the index order (exporter.py __traverse): depth-first from the scene's root list; a node's members in alphabetical
//     order - children, then mesh (materials, textures, images in first-use order), then skin (its joints, each with
//     its bone children first) -, and the node itself appended AFTER its members: children before parents, a joint
//     reached through a skin before the mesh that uses it, scene roots last.
//   * a node's transform (nodes.py __gather_trans_rot_scale): a root decomposes its matrix_world; a child decomposes
//     parent.matrix_world.inverted_safe() @ matrix_world (mathutils, float32); the quaternion normalized; (x, z, -y) for
//     the translation, (w, x, z, -y) for the rotation, (x, z, y) for the scale; each component within 2e-6 of its
//     identity value snapped to it; a property that is the identity is left out. A bone's matrix_world is the armature's
//     @ bone.matrix_local @ the Z-up to Y-up basis change (VehicleProbe.BlenderArmature), and a joint's transform
//     (joints.py) is decomposed WITHOUT the normalization and the snapping: its float noise is written (ExporterTrs).
// Proof: tools/prep-drill (every node Blender wrote, by index: name, parent, transform bits).
using System;
using System.Collections.Generic;
using System.Linq;

public static class BlenderExportTree
{
    public sealed class Node
    {
        public string Name;
        public int Parent = -1;                     // index in Nodes, or -1 for a scene root
        public bool NeutralBone;                    // the exporter's extra joint
        public bool HasMesh;                        // written with a mesh: a mesh object whose mesh has triangles
        public BlenderNames.BlenderObject Object;   // the Blender object, or null for a bone
        public int BoneNode = -1;                   // a bone's glTF node
        public int ArmatureNode = -1;               // for a bone: the index (in Nodes) of its armature's node
        public float[] World;                       // matrix_world, column-major float32 (null for a bone until the bone part lands)
        public float[] Translation, Rotation, Scale;   // as written: null when left out
        public float[] InverseBind;                 // a joint's inverse bind matrix as written (16 floats, column by column), the neutral bone's too
        public bool TransformKnown;                 // false for a bone, and under one: its transform is the next part's
        public List<int> Children = new List<int>();
    }

    public sealed class Result
    {
        public List<Node> Nodes = new List<Node>();   // in the written order (index = the file's node index)
        public List<int> SceneRoots = new List<int>();
        public List<int> MeshVisitOrder = new List<int>();   // the mesh nodes in the order the serializer reaches their meshes
        public List<string> Materials = new List<string>();  // the written materials, by Blender name, in first-use order
        public List<string> Problems = new List<string>();   // shapes not modelled, named
    }

    /// <summary>BLI_strcasecmp over the UTF-8 bytes: each byte as a signed char after tolower (ASCII only).</summary>
    public static int StrCaseCmp(string a, string b)
    {
        var x = System.Text.Encoding.UTF8.GetBytes(a); var y = System.Text.Encoding.UTF8.GetBytes(b);
        for (int i = 0; ; i++)
        {
            int c1 = i < x.Length ? Lower((sbyte)x[i]) : 0, c2 = i < y.Length ? Lower((sbyte)y[i]) : 0;
            if (c1 < c2) return -1;
            if (c1 > c2) return 1;
            if (c1 == 0) return 0;
        }
    }

    static int Lower(sbyte c) => c >= 'A' && c <= 'Z' ? c + 32 : c;

    /// <summary>The tree and the written node order for a model, given every glTF node's matrix_world as Blender holds it
    /// (VehicleProbe.BlenderWorldMatrices, column-major float32), the mesh nodes whose mesh has a triangle (the others are
    /// written without a mesh) and the armature glTF nodes (-1 for the dummy root's) a mesh of which needs the neutral
    /// bone.</summary>
    public static Result Build(HafModel m, BlenderNames.Result names, float[][] bworld, ISet<int> meshNodesWithFaces = null, ISet<int> neutralArmatures = null, Func<int, IEnumerable<string>> materialsOfMesh = null, bool bones = true)
    {
        neutralArmatures = neutralArmatures ?? new HashSet<int>();
        // the bones of every armature as Blender writes them: matrix_world and inverse bind matrix per joint (VehicleProbe.BlenderArmature)
        var boneWorld = new Dictionary<int, float[]>(); var boneIbm = new Dictionary<int, float[]>(); var armaWorldOf = new Dictionary<int, float[]>();
        if (bones)
            foreach (var a in names.Objects.Where(o => o.Kind == BlenderNames.ObjectKind.Armature))
            {
                var aw = a.GltfNode >= 0 ? bworld[a.GltfNode] : Identity();
                armaWorldOf[a.GltfNode] = aw;
                var ar = VehicleProbe.BlenderArmature(m, names, a.GltfNode, aw, VehicleProbe.ArmatureScale(m, a.GltfNode));
                foreach (var kv in ar.World) boneWorld[kv.Key] = kv.Value;
                foreach (var kv in ar.InverseBind) boneIbm[kv.Key] = kv.Value;
            }
        var r = new Result();
        var objs = names.Objects;
        var byName = objs.ToDictionary(o => o.Name, o => o, StringComparer.Ordinal);
        // the vtree: an object's children by name (a stable sort keeps creation order among names equal ignoring case)
        var objChildren = new Dictionary<string, List<BlenderNames.BlenderObject>>(StringComparer.Ordinal);
        foreach (var o in objs) objChildren[o.Name] = new List<BlenderNames.BlenderObject>();
        var boneObjChildren = new Dictionary<(string armature, string bone), List<BlenderNames.BlenderObject>>();   // objects parented to a bone, by name order
        foreach (var o in objs)
        {
            if (o.ParentBone != null)
            {
                var key = (o.Parent, o.ParentBone);
                if (!boneObjChildren.TryGetValue(key, out var l)) boneObjChildren[key] = l = new List<BlenderNames.BlenderObject>();
                l.Add(o);
                continue;
            }
            if (o.Parent != null) objChildren[o.Parent].Add(o);
        }
        foreach (var list in objChildren.Values) StableSort(list, (a, b) => StrCaseCmp(a.Name, b.Name));
        foreach (var list in boneObjChildren.Values) StableSort(list, (a, b) => StrCaseCmp(a.Name, b.Name));
        var armatureNameOfBone = new Dictionary<int, string>();   // bone glTF node -> its armature object's name
        foreach (var o in objs) if (o.Kind == BlenderNames.ObjectKind.Armature) foreach (int b in names.BoneNodesInOrder) if (names.ArmatureNodeOfBone[b] == o.GltfNode) armatureNameOfBone[b] = o.Name;
        // bones: per armature node its root bones, per bone its bone children, in creation order (BoneNodesInOrder)
        var boneChildren = new Dictionary<int, List<int>>();   // parent glTF node (or -1 for the dummy root's armature) -> bone nodes
        foreach (int b in names.BoneNodesInOrder)
        {
            int p = names.BoneParent[b];
            if (!boneChildren.TryGetValue(p, out var l)) boneChildren[p] = l = new List<int>();
            l.Add(b);
        }
        // the serializer's walk: members first (children, mesh, skin), the node itself after them
        var indexOf = new Dictionary<object, int>();   // BlenderObject or boxed bone node -> index in r.Nodes
        var nodeOf = new Dictionary<object, Node>();
        float[] WorldOf(BlenderNames.BlenderObject o)
        {
            // a skinned mesh hangs from its armature with the identity (moved there by the importer), and so does a vnode the
            // importer made (".skinned", ".mesh", ".camera"): the parent's world. The dummy root's armature sits at the origin.
            bool ownTransform = o.GltfNode >= 0 && !(o.Kind == BlenderNames.ObjectKind.Mesh && o.Skin >= 0);
            if (ownTransform) return bworld[o.GltfNode];
            return o.Parent != null && byName.TryGetValue(o.Parent, out var p) ? WorldOf(p) : Identity();
        }
        int VisitNeutral(int armaGltf, int armatureIndex)
        {
            object key = "neutral:" + armaGltf;
            if (indexOf.TryGetValue(key, out int done)) return done;
            var node = new Node { Name = "neutral_bone", NeutralBone = true, ArmatureNode = armatureIndex, TransformKnown = bones };
            if (bones && armaWorldOf.TryGetValue(armaGltf, out var aw2)) VehicleProbe.NeutralBone(aw2, out node.Translation, out node.Rotation, out node.Scale, out node.InverseBind);
            int idx = r.Nodes.Count; r.Nodes.Add(node); indexOf[key] = idx;
            return idx;
        }
        int VisitBone(int b, int armatureIndex, Node parentNode)
        {
            if (indexOf.TryGetValue(b, out int done)) return done;
            var node = new Node { Name = names.BoneOfJoint[b], BoneNode = b, ArmatureNode = armatureIndex, TransformKnown = boneWorld.ContainsKey(b), World = boneWorld.TryGetValue(b, out var bw) ? bw : null, InverseBind = boneIbm.TryGetValue(b, out var bi) ? bi : null };
            nodeOf[b] = node;
            if (boneChildren.TryGetValue(b, out var kids)) foreach (int c in kids) node.Children.Add(VisitBone(c, armatureIndex, node));
            if (armatureNameOfBone.TryGetValue(b, out var an) && boneObjChildren.TryGetValue((an, names.BoneOfJoint[b]), out var objKids)) foreach (var oc in objKids) node.Children.Add(Visit(oc));
            int idx = r.Nodes.Count; r.Nodes.Add(node); indexOf[b] = idx;
            foreach (int c in node.Children) r.Nodes[c].Parent = idx;
            return idx;
        }
        int Visit(BlenderNames.BlenderObject o)
        {
            if (indexOf.TryGetValue(o, out int done)) return done;
            var node = new Node { Name = o.Name, Object = o, World = WorldOf(o), TransformKnown = true, HasMesh = o.MeshNode >= 0 && (meshNodesWithFaces == null || meshNodesWithFaces.Contains(o.MeshNode)) };
            nodeOf[o] = node;
            // children: the object children, then (an armature) the root bones
            foreach (var c in objChildren[o.Name]) node.Children.Add(Visit(c));
            if (o.Kind == BlenderNames.ObjectKind.Armature)
            {
                // the root bones' subtrees are members too: indexed before the armature (the canoe, whose skin no mesh uses,
                // reaches its joints only here); a joint already reached through a skin keeps its index
                int armaGltf = o.GltfNode;   // -1 for the dummy root's armature: its bones' parent is -1 too
                if (boneChildren.TryGetValue(armaGltf, out var roots)) foreach (int b in roots) node.Children.Add(VisitBone(b, -2, null));
                if (neutralArmatures.Contains(armaGltf)) node.Children.Add(VisitNeutral(armaGltf, -2));   // the neutral bone, last
            }
            // mesh: its materials in slot order, each written once, at first use; then skin: the joints, each after its bone children
            if (node.HasMesh)
            {
                r.MeshVisitOrder.Add(o.MeshNode);
                if (materialsOfMesh != null) foreach (var mat in materialsOfMesh(o.MeshNode)) if (mat != null && !r.Materials.Contains(mat)) r.Materials.Add(mat);
            }
            if (o.Kind == BlenderNames.ObjectKind.Mesh && o.Skin >= 0 && o.Skin < m.Skins.Count && names.ArmatureNodeOfSkin[o.Skin] >= -1)
            {
                int armaGltf = names.ArmatureNodeOfSkin[o.Skin];
                var arma = objs.FirstOrDefault(a => a.Kind == BlenderNames.ObjectKind.Armature && a.GltfNode == armaGltf);
                if (arma != null)
                {
                    // the skin's joints are the armature's bones depth-first; the serializer reaches them here first
                    if (boneChildren.TryGetValue(armaGltf, out var roots)) foreach (int b in roots) VisitBone(b, -2, null);
                    if (neutralArmatures.Contains(armaGltf)) VisitNeutral(armaGltf, -2);
                }
            }
            int idx = r.Nodes.Count; r.Nodes.Add(node); indexOf[o] = idx;
            foreach (int c in node.Children) { r.Nodes[c].Parent = idx; if ((r.Nodes[c].BoneNode >= 0 || r.Nodes[c].NeutralBone) && r.Nodes[c].ArmatureNode < 0) r.Nodes[c].ArmatureNode = idx; }
            return idx;
        }
        foreach (var o in objs) if (o.Parent == null && o.ParentBone == null) r.SceneRoots.Add(Visit(o));
        // every bone's armature index (bones reached through a skin got -2 until their armature was indexed)
        foreach (var n in r.Nodes) if (n.BoneNode >= 0 || n.NeutralBone) { int a = n.Parent; while (a >= 0 && r.Nodes[a].BoneNode >= 0) a = r.Nodes[a].Parent; n.ArmatureNode = a; }
        // transforms: objects against their parent's world, joints against their parent joint's or the armature's
        foreach (var n in r.Nodes)
        {
            if (n.NeutralBone) continue;   // set when made
            float[] parentWorld = n.Parent >= 0 ? r.Nodes[n.Parent].World : null;
            if (n.Object != null)
            {
                bool underBone = false; for (int a = n.Parent; a >= 0; a = r.Nodes[a].Parent) if (r.Nodes[a].BoneNode >= 0) { underBone = true; break; }
                if (underBone) { n.TransformKnown = false; continue; }   // parented to a bone, or below such an object: not modelled yet
            }
            else if (n.World == null || (n.Parent >= 0 && parentWorld == null)) { n.TransformKnown = false; continue; }
            VehicleProbe.ExporterTrs(parentWorld, n.World, out n.Translation, out n.Rotation, out n.Scale, joint: n.BoneNode >= 0);
        }
        return r;
    }

    static float[] Identity() { var m = new float[16]; m[0] = m[5] = m[10] = m[15] = 1f; return m; }

    static void StableSort<T>(List<T> list, Comparison<T> cmp)
    {
        var indexed = list.Select((x, i) => (x, i)).ToList();
        indexed.Sort((a, b) => { int c = cmp(a.x, b.x); return c != 0 ? c : a.i.CompareTo(b.i); });
        for (int i = 0; i < list.Count; i++) list[i] = indexed[i].x;
    }
}
