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
        // a skinned mesh: the joints of its skin as written (node indices: the armature's bones in creation order, the
        // neutral bone last) and the armature's glTF node (-1 for the dummy root's); null when the node has no skin. The
        // exporter makes ONE skin per armature, so two nodes of one SkinArmature share a skin and two of different ones
        // never do - whatever the joints' names and matrices (review of PR #129: twin armatures)
        public List<int> SkinJoints; public int SkinArmature = -2;
    }

    public sealed class Result
    {
        public List<Node> Nodes = new List<Node>();   // in the written order (index = the file's node index)
        public List<int> SceneRoots = new List<int>();
        public List<int> MeshVisitOrder = new List<int>();   // the mesh nodes in the order the serializer reaches their meshes
        public List<string> Materials = new List<string>();  // the written materials, by Blender name, in first-use order
        public List<string> Notes = new List<string>();      // the bone chain's branches this file took (VehicleProbe.ArmatureResult.Notes), each once
        public int CamerasLeftOut;                           // cameras the exporter's filter dropped
        // the skins of armatures NO kept mesh object is skinned to (tree.py get_unused_skins): written all the same, after
        // every skin a node uses, in the order the armatures enter the exporter's tree - each the armature's glTF node and
        // its joints (node indices, the bones in creation order). A skin of the source no node uses makes one, and so
        // does a strip that takes an armature's meshes and leaves it (measured 2026-10-08, export_strip)
        public List<(int armature, string name, List<int> joints)> UnusedSkins = new List<(int, string, List<int>)>();
        public List<string> Problems = new List<string>();   // shapes Blender writes in a way this tree does not model, named: such a file is Blender's to prep
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
    public static Result Build(HafModel m, BlenderNames.Result names, float[][] bworld, ISet<int> meshNodesWithFaces = null, ISet<int> neutralArmatures = null, Func<int, IEnumerable<string>> materialsOfMesh = null, bool bones = true, ISet<string> stripped = null)
    {
        // prep_model.py's strip removes objects (by name, each with its descendants) before the export: they are not
        // in the tree, and an armature among them takes its bones along (BlenderPrep.Stripped)
        bool Kept(BlenderNames.BlenderObject o) => stripped == null || !stripped.Contains(o.Name);
        neutralArmatures = neutralArmatures ?? new HashSet<int>();
        // the bones of every armature as Blender writes them: matrix_world and inverse bind matrix per joint (VehicleProbe.BlenderArmature)
        var boneNotes = new List<string>();
        var boneWorld = new Dictionary<int, float[]>(); var boneIbm = new Dictionary<int, float[]>(); var armaWorldOf = new Dictionary<int, float[]>();
        if (bones)
            foreach (var a in names.Objects.Where(o => o.Kind == BlenderNames.ObjectKind.Armature && Kept(o)))
            {
                var aw = a.GltfNode >= 0 ? bworld[a.GltfNode] : Identity();
                armaWorldOf[a.GltfNode] = aw;
                var ar = VehicleProbe.BlenderArmature(m, names, a.GltfNode, aw, VehicleProbe.ArmatureScale(m, a.GltfNode));
                foreach (var kv in ar.World) boneWorld[kv.Key] = kv.Value;
                foreach (var kv in ar.InverseBind) boneIbm[kv.Key] = kv.Value;
                foreach (var note in ar.Notes) if (!boneNotes.Contains(note)) boneNotes.Add(note);
            }
        var r = new Result();
        r.Notes.AddRange(boneNotes);
        var objs = stripped == null ? names.Objects : names.Objects.Where(Kept).ToList();
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
        // ---- shapes Blender writes in a way this tree does not model: named, and the file is Blender's to prep
        if (m.ExtensionsUsed.Contains("KHR_lights_punctual")) r.Problems.Add("lights: the file uses KHR_lights_punctual: a light is an object the exporter leaves out, and the reader does not carry which node has one");
        foreach (var o in objs)
            if (o.Kind == BlenderNames.ObjectKind.Mesh && o.Skin >= 0 && o.MeshNode >= 0 && !m.Meshes[m.Nodes[o.MeshNode].Mesh].Primitives.Exists(p => p.Skinned))
                r.Problems.Add($"skin-no-weights: '{o.Name}' has a skin and its mesh no weights: Blender writes its positions through the armature and joints of a bone that is not there");
        MaterialUvProblems(m, r.Problems);
        // ---- the exporter's filter (tree.py recursive_filter, roots in order, depth first): a camera is not exported
        // (prep_model.py leaves export_cameras off); it leaves the tree, and each kept descendant whose parent went is
        // appended to the END of its nearest kept ancestor's children - or of the scene's roots, its transform against
        // that ancestor (measured 2026-10-07, review of PR #129)
        var roots = objs.Where(o => o.Parent == null && o.ParentBone == null).ToList();
        void Filter(BlenderNames.BlenderObject o, BlenderNames.BlenderObject keptParent)
        {
            var kids = objChildren[o.Name].ToList();
            var next = o;
            if (o.Kind == BlenderNames.ObjectKind.Camera)
            {
                if (o.Parent != null) objChildren[o.Parent].Remove(o); else roots.Remove(o);
                next = keptParent;
                r.CamerasLeftOut++;
            }
            else if (o.Parent != keptParent?.Name)
            {
                if (keptParent != null) objChildren[keptParent.Name].Add(o); else roots.Add(o);
                // the order and the parent above are the exporter's (measured); the child's matrix is not: the importer turns
                // a camera (camera_correction) and turns its children back, which VehicleProbe.BlenderWorldMatrices does not
                // model - an ulp on the child's world matrix
                r.Problems.Add($"camera-children: '{o.Name}' is the child of a camera: the importer's camera correction on its matrix is not modelled");
            }
            foreach (var c in kids) Filter(c, next);
        }
        foreach (var o in roots.ToList()) Filter(o, null);
        foreach (var list in boneObjChildren.Values)
            foreach (var o in list.ToList())
            {
                if (o.Kind != BlenderNames.ObjectKind.Camera) { foreach (var c in objChildren[o.Name].ToList()) Filter(c, o); continue; }
                list.Remove(o);
                // its children have no parent bone of their own: get_objects_parented_to_bones would not find a joint for them
                if (objChildren[o.Name].Count > 0) r.Problems.Add($"camera-bone: the camera '{o.Name}' is parented to a bone and has children");
            }
        // bones: per armature node its root bones, per bone its bone children, in creation order (BoneNodesInOrder)
        var boneChildren = new Dictionary<int, List<int>>();   // parent glTF node (or -1 for the dummy root's armature) -> bone nodes
        foreach (int b in names.BoneNodesInOrder)
        {
            int p = names.BoneParent[b];
            if (!boneChildren.TryGetValue(p, out var l)) boneChildren[p] = l = new List<int>();
            l.Add(b);
        }
        // ---- objects parented to bones (nodes.py get_objects_parented_to_bones): per armature, bone by bone depth first,
        // each object is appended to the first node NAMED like its parent bone that a depth-first search from the root
        // joints meets (__find_parent_joint) - the joint, unless an object hung earlier (or a child of one) bears the
        // bone's name and comes first (measured 2026-10-07, review of PR #129). Its children were gathered with it.
        var hung = new Dictionary<object, List<BlenderNames.BlenderObject>>();   // a boxed bone node or a BlenderObject -> the objects appended to it, in order
        foreach (var arma in objs.Where(a => a.Kind == BlenderNames.ObjectKind.Armature))
        {
            if (!boneChildren.TryGetValue(arma.GltfNode, out var rootBones)) continue;
            Sim JointSim(int b) { var s = new Sim { Name = names.BoneOfJoint[b], Key = b }; if (boneChildren.TryGetValue(b, out var k)) foreach (int c in k) s.Kids.Add(JointSim(c)); return s; }
            Sim ObjSim(BlenderNames.BlenderObject o) { var s = new Sim { Name = o.Name, Key = o }; foreach (var c in objChildren[o.Name]) s.Kids.Add(ObjSim(c)); return s; }
            Sim Find(List<Sim> list, string name) { foreach (var s in list) { if (s.Name == name) return s; var f = Find(s.Kids, name); if (f != null) return f; } return null; }
            void AllBones(int b, List<int> into) { into.Add(b); if (boneChildren.TryGetValue(b, out var k)) foreach (int c in k) AllBones(c, into); }
            var rootSims = rootBones.Select(JointSim).ToList();
            var all = new List<int>(); foreach (int b in rootBones) AllBones(b, all);
            foreach (int b in all)
                if (boneObjChildren.TryGetValue((arma.Name, names.BoneOfJoint[b]), out var kids))
                    foreach (var oc in kids)
                    {
                        var target = Find(rootSims, oc.ParentBone);
                        if (target == null) continue;
                        target.Kids.Add(ObjSim(oc));
                        if (!hung.TryGetValue(target.Key, out var l)) hung[target.Key] = l = new List<BlenderNames.BlenderObject>();
                        l.Add(oc);
                    }
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
            if (hung.TryGetValue(b, out var objKids)) foreach (var oc in objKids) node.Children.Add(Visit(oc));
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
            if (hung.TryGetValue(o, out var hungHere)) foreach (var oc in hungHere) node.Children.Add(Visit(oc));   // objects that found THIS object by a bone's name
            // mesh: its materials in slot order, each written once, at first use; then skin: the joints, each after its bone children
            if (node.HasMesh)
            {
                r.MeshVisitOrder.Add(o.MeshNode);
                if (materialsOfMesh != null) foreach (var mat in materialsOfMesh(o.MeshNode)) if (mat != null && !r.Materials.Contains(mat)) r.Materials.Add(mat);
            }
            if (node.HasMesh && o.Kind == BlenderNames.ObjectKind.Mesh && o.Skin >= 0 && o.Skin < m.Skins.Count && m.Meshes[m.Nodes[o.MeshNode].Mesh].Primitives.Exists(p => p.Skinned))
            {
                int armaGltf = names.ArmatureNodeOfSkin[o.Skin];
                var arma = objs.FirstOrDefault(a => a.Kind == BlenderNames.ObjectKind.Armature && a.GltfNode == armaGltf);
                if (arma != null)
                {
                    // the skin's joints are the armature's bones depth-first; the serializer reaches them here first
                    if (boneChildren.TryGetValue(armaGltf, out var roots)) foreach (int b in roots) VisitBone(b, -2, null);
                    if (neutralArmatures.Contains(armaGltf)) VisitNeutral(armaGltf, -2);
                    node.SkinArmature = armaGltf;
                    node.SkinJoints = names.BoneNodesInOrder.Where(b => names.ArmatureNodeOfBone[b] == armaGltf).Select(b => indexOf[b]).ToList();
                    if (neutralArmatures.Contains(armaGltf)) node.SkinJoints.Add(indexOf["neutral:" + armaGltf]);
                }
            }
            int idx = r.Nodes.Count; r.Nodes.Add(node); indexOf[o] = idx;
            foreach (int c in node.Children) { r.Nodes[c].Parent = idx; if ((r.Nodes[c].BoneNode >= 0 || r.Nodes[c].NeutralBone) && r.Nodes[c].ArmatureNode < 0) r.Nodes[c].ArmatureNode = idx; }
            return idx;
        }
        foreach (var o in roots) r.SceneRoots.Add(Visit(o));
        // unused skins: an object uses an armature when it carries the armature modifier under it - every mesh object the
        // importer skinned, with faces or without. The exporter's tree is made parent first, children in their order
        var used = new HashSet<int>(objs.Where(x => x.Kind == BlenderNames.ObjectKind.Mesh && x.Skin >= 0 && x.Skin < m.Skins.Count).Select(x => names.ArmatureNodeOfSkin[x.Skin]));
        var seenArmatures = new HashSet<BlenderNames.BlenderObject>();
        void Unused(BlenderNames.BlenderObject o)
        {
            if (o.Kind == BlenderNames.ObjectKind.Armature && seenArmatures.Add(o) && !used.Contains(o.GltfNode) && indexOf.ContainsKey(o))
            {
                var joints = names.BoneNodesInOrder.Where(b => names.ArmatureNodeOfBone[b] == o.GltfNode && indexOf.ContainsKey(b)).Select(b => indexOf[b]).ToList();
                if (joints.Count > 0) r.UnusedSkins.Add((o.GltfNode, o.Name, joints));
            }
            foreach (var c in objChildren[o.Name]) Unused(c);
        }
        foreach (var o in roots) Unused(o);
        // an armature that hangs from a BONE is reached last here; the exporter's tree has it inside its parent armature's
        // subtree. No file gets that far today (an object under a bone is left to Blender) - set this right with that
        foreach (var o in objs) if (o.Kind == BlenderNames.ObjectKind.Armature && !seenArmatures.Contains(o)) Unused(o);
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

    sealed class Sim { public string Name; public object Key; public readonly List<Sim> Kids = new List<Sim>(); }

    /// <summary>The exporter copies a material per set of UV indices its textures resolve to on the mesh that uses it
    /// (materials.py get_final_material: a texture on TEXCOORD_n reads the layer of that name where the mesh has it, the
    /// active one - the first - where not; every index 0 is the material itself). One material read with two different
    /// sets is written twice under one name (measured 2026-10-07: TEXCOORD_1 on a mesh of two UV sets and on a mesh of
    /// one). Not modelled: named, per material. A texture inside an extension is not interpreted here, so a material
    /// with one on a non-zero set is named as soon as its meshes differ in their number of UV sets.</summary>
    static void MaterialUvProblems(HafModel m, List<string> problems)
    {
        var seen = new Dictionary<(int material, bool colour), string>();
        var named = new HashSet<int>();
        foreach (var node in m.Nodes)
        {
            if (node.Mesh < 0) continue;
            var mesh = m.Meshes[node.Mesh];
            int sets = 0;
            foreach (var p in mesh.Primitives) sets = Math.Max(sets, p.Uv0 == null ? 0 : p.Uv1 == null ? 1 : 2 + (p.UvMore?.Count ?? 0));
            sets = Math.Min(sets, 8);   // the importer's UV_MAX
            foreach (var p in mesh.Primitives)
            {
                if (p.Material < 0 || p.Material >= m.Materials.Count || named.Contains(p.Material)) continue;
                var mt = m.Materials[p.Material];
                int At(int texture, int texCoord) => texture >= 0 && texCoord > 0 && texCoord < sets ? texCoord : 0;
                bool extension = mt.ExtensionsJson != null && System.Text.RegularExpressions.Regex.IsMatch(mt.ExtensionsJson, "\"texCoord\"\\s*:\\s*[1-9]");
                string key = At(mt.BaseColorTexture, mt.BaseColorTexCoord) + "," + At(mt.MetallicRoughnessTexture, mt.MetallicRoughnessTexCoord) + "," + At(mt.NormalTexture, mt.NormalTexCoord) + ","
                    + At(mt.OcclusionTexture, mt.OcclusionTexCoord) + "," + At(mt.EmissiveTexture, mt.EmissiveTexCoord) + (extension ? "," + sets : "");
                var variant = (p.Material, p.Colors != null);
                if (!seen.TryGetValue(variant, out var before)) { seen[variant] = key; continue; }
                if (before == key) continue;
                named.Add(p.Material);
                problems.Add($"material-uv: material {p.Material} '{mt.Name}' reads UV sets [{before}] on one mesh and [{key}] on another: Blender writes it once per set of UV indices");
            }
        }
    }

    static void StableSort<T>(List<T> list, Comparison<T> cmp)
    {
        var indexed = list.Select((x, i) => (x, i)).ToList();
        indexed.Sort((a, b) => { int c = cmp(a.x, b.x); return c != 0 ? c : a.i.CompareTo(b.i); });
        for (int i = 0; i < list.Count; i++) list[i] = indexed[i].x;
    }
}
