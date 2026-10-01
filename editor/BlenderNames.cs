// BlenderNames.cs - the names Blender's glTF importer gives a file's nodes, computed from the model WITHOUT Blender
// (step 3 of replacing Blender: the Vehicle Lab's part rows are keyed by these names, and every saved recipe holds
// them). This is a port of the importer's `compute_vnodes` (Blender 5.1, io_scene_gltf2/blender/imp/vnode.py) and of
// `create_vnode`'s creation order, with Blender's `BLI_uniquename` for the ".001" suffixes - measured against the
// real importer on fixtures that exercise every rule below (Tests/BlenderNamesTests.cs), then on every registry
// file by the probe drill. The rules, as the importer has them:
//   * every node is a "vnode"; the parentless ones hang under a dummy root IN NODE INDEX ORDER (not the scene's
//     order - a dict of vnodes keyed by index); objects are created depth-first from there, children in order;
//   * a skin's armature is the deepest common ancestor of its joints (and skeleton); when that is a joint, its
//     parent; the dummy root may become the armature. Everything between the armature and a joint is a bone, not
//     an object. The armature OBJECT is named after its node, else after the skin ("Armature" when unnamed); its
//     creation also adds the bone-shape "Icosphere" object, which takes a name in the same pool;
//   * a skinned mesh node that is not animated, has no children and is not itself an armature MOVES under its
//     armature (appended to its children, so created after them); otherwise the node stays as an empty with its
//     name and a new "<id>.skinned" vnode, appended to the armature, carries the mesh - named after the MESH;
//   * a mesh on an armature or bone node moves to a new "<id>.mesh" child vnode, named after the mesh;
//   * a mesh object is named after its node, else after the Blender mesh datablock (the glTF mesh's name, else
//     "Mesh_<index>", itself unique among meshes); an empty after its node, else "Node_<index>"; a bone after its
//     node, else "Node_<index>" (unique within its armature);
//   * uniqueness (BLI_uniquename): when a name is taken, split a trailing ".NNN" off, then try NNN+1, NNN+2 ...
//     as ".%03d" until free. Objects of every type share one pool; meshes, armatures and bones have their own.
// Cameras, lights and EXT_mesh_gpu_instancing are not modelled (the reader does not carry them): such a node is an
// empty here, which takes the same name - the only thing that matters for the pool.
using System;
using System.Collections.Generic;
using System.Linq;

public static class BlenderNames
{
    public sealed class Result
    {
        /// <summary>Per glTF node: the name of the Blender OBJECT that carries its MESH (its own object, or the
        /// ".skinned"/".mesh" child the importer makes), or null for a node without a mesh.</summary>
        public string[] MeshObjectOfNode;
        /// <summary>Per glTF node: the name of the Blender object the node itself became (an empty, a mesh object,
        /// an armature), or null when it became a bone.</summary>
        public string[] ObjectOfNode;
        /// <summary>Mesh objects in Blender's creation order (= `scene.objects` order): the node whose mesh each carries.</summary>
        public List<(int node, string name)> MeshObjectsInOrder = new List<(int, string)>();
        /// <summary>Per joint node: its bone name (unique within its armature).</summary>
        public Dictionary<int, string> BoneOfJoint = new Dictionary<int, string>();
        /// <summary>Per skin: the armature object's name.</summary>
        public string[] ArmatureOfSkin;
        /// <summary>Per skin: the node that became the armature, or -1 for the dummy root.</summary>
        public int[] ArmatureNodeOfSkin;
        /// <summary>The bone-shape objects the importer added (one per armature): "Icosphere", "Icosphere.001", ...</summary>
        public List<string> BoneShapes = new List<string>();
        /// <summary>Per glTF node: whether the importer made it a BONE, and then its parent in the importer's tree -
        /// a node index, or -1 for the dummy root (the parent of a chain's first bone is its armature).</summary>
        public bool[] IsBone; public int[] BoneParent;
    }

    enum Kind { Object, Bone, DummyRoot }

    sealed class VNode
    {
        public string Id; public string Name; public string DefaultName; public string Parent;
        public readonly List<string> Children = new List<string>();
        public int MeshNode = -1;              // the glTF node whose mesh this vnode carries, or -1
        public Kind Type = Kind.Object; public bool IsArma; public string ArmaName; public string BoneArma; public int ArmaSkin = -1;
    }

    public static Result Compute(HafModel m)
    {
        var v = new Dictionary<string, VNode>(); var order = new List<string>();   // a Python dict: insertion order
        void Add(VNode n) { v[n.Id] = n; order.Add(n.Id); }
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            var n = new VNode { Id = Key(i), Name = m.Nodes[i].Name.Length > 0 ? m.Nodes[i].Name : null, DefaultName = "Node_" + i, MeshNode = m.Nodes[i].Mesh >= 0 ? i : -1 };
            foreach (var c in m.Nodes[i].Children) n.Children.Add(Key(c));
            Add(n);
        }
        for (int i = 0; i < m.Nodes.Count; i++) foreach (var c in m.Nodes[i].Children) v[Key(c)].Parent = Key(i);
        var root = new VNode { Id = "root", Type = Kind.DummyRoot, DefaultName = "Root" };
        foreach (var id in order.ToList()) if (v[id].Parent == null) { root.Children.Add(id); v[id].Parent = "root"; }
        Add(root);

        // ---- mark_bones_and_armas
        var armaOfSkin = new string[m.Skins.Count];
        for (int si = 0; si < m.Skins.Count; si++)
        {
            var sk = m.Skins[si];
            var descendants = sk.Joints.Select(Key).ToList();
            if (sk.Skeleton >= 0) descendants.Add(Key(sk.Skeleton));
            string arma = DeepestCommonAncestor(v, descendants);
            if (sk.Joints.Contains(Index(arma))) arma = v[arma].Parent;
            if (v[arma].Type != Kind.Bone) { v[arma].Type = Kind.Object; v[arma].IsArma = true; v[arma].ArmaName = sk.Name.Length > 0 ? sk.Name : "Armature"; if (v[arma].ArmaSkin < 0) v[arma].ArmaSkin = si; }
            foreach (var j in sk.Joints)
            {
                string at = Key(j);
                while (at != arma) { v[at].Type = Kind.Bone; v[at].IsArma = false; at = v[at].Parent; }
            }
            armaOfSkin[si] = arma;
        }
        void Visit(string id, string curArma)
        {
            var n = v[id];
            if (n.IsArma) curArma = id; else if (n.Type == Kind.Bone) n.BoneArma = curArma; else curArma = null;
            foreach (var c in n.Children.ToList()) Visit(c, curArma);
        }
        Visit("root", null);

        // ---- move_skinned_meshes
        var animatedNodes = new HashSet<int>(m.Animations.SelectMany(a => a.Channels).Where(c => c.Node >= 0).Select(c => c.Node));
        foreach (var id in order.ToList())
        {
            var n = v[id];
            if (n.MeshNode < 0) continue;
            int skin = m.Nodes[n.MeshNode].Skin;
            if (skin < 0) continue;
            string arma = v[Key(m.Skins[skin].Joints[0])].BoneArma;
            if (arma == null) continue;   // a joint that is not under an armature (the reader accepts such files); the importer would fail
            bool isAnimated = Index(id) >= 0 && animatedNodes.Contains(Index(id));
            bool okToMove = !isAnimated && n.Type == Kind.Object && !n.IsArma && n.Children.Count == 0;
            if (okToMove)
            {
                if (n.Parent != arma) { v[n.Parent].Children.Remove(id); n.Parent = arma; v[arma].Children.Add(id); }   // reparent: a no-op when it is there already (keeps its place)
                continue;
            }
            var moved = new VNode { Id = id + ".skinned", Parent = arma, MeshNode = n.MeshNode };
            Add(moved); v[arma].Children.Add(moved.Id); n.MeshNode = -1;
        }

        // ---- fixup_multitype_nodes
        foreach (var id in order.ToList())
        {
            var n = v[id];
            if (n.MeshNode >= 0 && (n.IsArma || n.Type == Kind.Bone))
            {
                var moved = new VNode { Id = id + ".mesh", Parent = id, MeshNode = n.MeshNode };
                Add(moved); n.Children.Add(moved.Id); n.MeshNode = -1;
            }
        }

        // ---- creation, depth-first: objects, armatures (with their bones and bone shape), mesh datablocks
        var r = new Result { MeshObjectOfNode = new string[m.Nodes.Count], ObjectOfNode = new string[m.Nodes.Count], ArmatureOfSkin = new string[m.Skins.Count], ArmatureNodeOfSkin = new int[m.Skins.Count], IsBone = new bool[m.Nodes.Count], BoneParent = new int[m.Nodes.Count] };
        for (int i = 0; i < m.Nodes.Count; i++) { r.IsBone[i] = v[Key(i)].Type == Kind.Bone; r.BoneParent[i] = Index(v[Key(i)].Parent); }
        var objects = new HashSet<string>(StringComparer.Ordinal); var meshes = new HashSet<string>(StringComparer.Ordinal); var armatures = new HashSet<string>(StringComparer.Ordinal);
        var meshData = new Dictionary<(int mesh, int skin), string>();
        void Create(string id)
        {
            var n = v[id];
            if (n.Type == Kind.Object)
            {
                string name;
                if (n.MeshNode >= 0)
                {
                    int meshIdx = m.Nodes[n.MeshNode].Mesh, skin = m.Nodes[n.MeshNode].Skin;
                    if (!meshData.TryGetValue((meshIdx, skin), out var data))
                    {
                        data = Unique(meshes, m.Meshes[meshIdx].Name.Length > 0 ? m.Meshes[meshIdx].Name : "Mesh_" + meshIdx);
                        meshData[(meshIdx, skin)] = data;
                    }
                    name = Unique(objects, n.Name ?? data);
                    r.MeshObjectOfNode[n.MeshNode] = name;
                    r.MeshObjectsInOrder.Add((n.MeshNode, name));
                }
                else if (n.IsArma)
                {
                    string data = Unique(armatures, n.ArmaName);
                    name = Unique(objects, n.Name ?? data);
                    r.BoneShapes.Add(Unique(objects, "Icosphere"));   // armature_display: the bone-shape object, one per armature
                    for (int si = 0; si < m.Skins.Count; si++) if (armaOfSkin[si] == id) { r.ArmatureOfSkin[si] = name; r.ArmatureNodeOfSkin[si] = Index(id); }
                    // create_bones: every bone under this armature, depth-first, unique within it
                    var bones = new HashSet<string>(StringComparer.Ordinal);
                    void Bones(string bid)
                    {
                        var b = v[bid];
                        if (b.Type == Kind.Bone) { r.BoneOfJoint[Index(bid)] = Unique(bones, b.Name ?? b.DefaultName); foreach (var c in b.Children) Bones(c); }
                    }
                    foreach (var c in n.Children) Bones(c);
                }
                else name = Unique(objects, n.Name ?? n.DefaultName);
                int ni = Index(id);
                if (ni >= 0) r.ObjectOfNode[ni] = name;
            }
            foreach (var c in n.Children) Create(c);
        }
        Create("root");
        return r;
    }

    static string Key(int node) => node.ToString();
    static int Index(string id) => int.TryParse(id, out int i) ? i : -1;

    static string DeepestCommonAncestor(Dictionary<string, VNode> v, List<string> ids)
    {
        List<string> common = null;
        foreach (var id in ids)
        {
            var path = new List<string>(); for (string at = id; at != null; at = v[at].Parent) path.Add(at); path.Reverse();
            if (common == null) common = path;
            else { int k = 0; while (k < common.Count && k < path.Count && common[k] == path[k]) k++; common = common.Take(k).ToList(); }
        }
        return common[common.Count - 1];
    }

    /// <summary>BLI_uniquename: the name as given when free; else its ".NNN" tail split off and NNN+1, NNN+2, ...
    /// tried as ".%03d" until one is free. The chosen name joins the pool.</summary>
    public static string Unique(ISet<string> pool, string name)
    {
        if (pool.Add(name)) return name;
        string left = name; int nr = 0;
        int dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1 && name.Substring(dot + 1).All(char.IsDigit)) { left = name.Substring(0, dot); nr = int.Parse(name.Substring(dot + 1)); }
        for (int k = nr + 1; ; k++)
        {
            string candidate = left + "." + k.ToString("000");
            if (pool.Add(candidate)) return candidate;
        }
    }
}
