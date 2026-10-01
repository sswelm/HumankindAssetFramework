// BlenderNames.cs - the names Blender's glTF importer gives a file's nodes, computed from the model WITHOUT Blender
// (step 3 of replacing Blender: the Vehicle Lab's part rows are keyed by these names, and every saved recipe holds
// them). This is a port of the importer's `compute_vnodes` (Blender 5.1, io_scene_gltf2/blender/imp/vnode.py) and of
// `create_vnode`'s creation order, with Blender's two unique-name rules for the ".001" suffixes - measured against the
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
//   * uniqueness: objects of every type share one pool; meshes, armatures and cameras have their own, and each
//     armature its bones. A taken DATABLOCK name gets its base's smallest free number (NamePool - two objects named
//     B.7 are B.7 and B.001); a taken BONE name counts up from its own tail (UniqueBone - J.7 and J.008). Both rules,
//     with their length limits, are ported from Blender's source further down.
//   * a camera node's object is named after the node, else after the camera datablock (the glTF camera's name, else
//     "Camera", unique among cameras); a node with a mesh AND a camera keeps the mesh and gets a child for the camera.
// Lights (KHR_lights_punctual) and EXT_mesh_gpu_instancing are not modelled - the reader does not carry them: a
// NAMED light node takes the same name as the empty it is here; a nameless one would take its light's name in
// Blender and "Node_<index>" here. No file of the drill's populations has one.
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
        /// <summary>Per skin: the armature object its joints are bones of (two skins may share one), by name.</summary>
        public string[] ArmatureOfSkin;
        /// <summary>Per skin: the node that became that armature, or -1 for the dummy root.</summary>
        public int[] ArmatureNodeOfSkin;
        /// <summary>The armature objects in creation order - Blender's `scene.objects` order, whose first is the one the
        /// Lab's rig report reads: (node or -1 for the dummy root, name).</summary>
        public List<(int node, string name)> ArmaturesInOrder = new List<(int, string)>();
        /// <summary>Per glTF node that is a bone: the node that became its armature, or -1 for the dummy root.</summary>
        public int[] ArmatureNodeOfBone;
        /// <summary>Every object name the import made, of every type (meshes, empties, armatures, cameras, bone shapes):
        /// the pool a later name is made unique in.</summary>
        public NamePool ObjectPool;
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
        public int CameraNode = -1;            // the glTF node whose camera this vnode carries, or -1
        public Kind Type = Kind.Object; public bool IsArma; public string ArmaName; public string BoneArma; public int ArmaSkin = -1;
    }

    public static Result Compute(HafModel m)
    {
        var v = new Dictionary<string, VNode>(); var order = new List<string>();   // a Python dict: insertion order
        void Add(VNode n) { v[n.Id] = n; order.Add(n.Id); }
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            var n = new VNode { Id = Key(i), Name = m.Nodes[i].Name.Length > 0 ? m.Nodes[i].Name : null, DefaultName = "Node_" + i, MeshNode = m.Nodes[i].Mesh >= 0 ? i : -1, CameraNode = m.Nodes[i].Camera >= 0 ? i : -1 };
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
            bool okToMove = !isAnimated && n.Type == Kind.Object && !n.IsArma && n.Children.Count == 0 && n.CameraNode < 0;
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
            bool needsMove = n.IsArma || n.Type == Kind.Bone;
            if (n.MeshNode >= 0)
            {
                if (needsMove) { var moved = new VNode { Id = id + ".mesh", Parent = id, MeshNode = n.MeshNode }; Add(moved); n.Children.Add(moved.Id); n.MeshNode = -1; }
                needsMove = true;   // an object holds one thing: with a mesh on it, a camera moves off
            }
            if (n.CameraNode >= 0 && needsMove) { var moved = new VNode { Id = id + ".camera", Parent = id, CameraNode = n.CameraNode }; Add(moved); n.Children.Add(moved.Id); n.CameraNode = -1; }
        }

        // ---- creation, depth-first: objects, armatures (with their bones and bone shape), mesh datablocks
        var r = new Result { MeshObjectOfNode = new string[m.Nodes.Count], ObjectOfNode = new string[m.Nodes.Count], ArmatureOfSkin = new string[m.Skins.Count], ArmatureNodeOfSkin = new int[m.Skins.Count], IsBone = new bool[m.Nodes.Count], BoneParent = new int[m.Nodes.Count] };
        for (int i = 0; i < m.Nodes.Count; i++) { r.IsBone[i] = v[Key(i)].Type == Kind.Bone; r.BoneParent[i] = Index(v[Key(i)].Parent); }
        var objects = new NamePool(); var meshes = new NamePool(); var armatures = new NamePool(); var cameras = new NamePool();
        var armaName = new Dictionary<string, string>();
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
                        data = meshes.Unique(m.Meshes[meshIdx].Name.Length > 0 ? m.Meshes[meshIdx].Name : "Mesh_" + meshIdx);
                        meshData[(meshIdx, skin)] = data;
                    }
                    name = objects.Unique(n.Name ?? data);
                    r.MeshObjectOfNode[n.MeshNode] = name;
                    r.MeshObjectsInOrder.Add((n.MeshNode, name));
                }
                else if (n.IsArma)
                {
                    string data = armatures.Unique(n.ArmaName);
                    name = objects.Unique(n.Name ?? data);
                    r.BoneShapes.Add(objects.Unique("Icosphere"));   // armature_display: the bone-shape object, one per armature
                    meshes.Unique("Icosphere");   // ... and its mesh DATABLOCK: a glTF mesh named Icosphere, made after it, is Icosphere.001 - and names its nameless node so
                    armaName[id] = name; r.ArmaturesInOrder.Add((Index(id), name));
                    // create_bones: every bone under this armature, depth-first, unique within it
                    var bones = new HashSet<string>(StringComparer.Ordinal);
                    void Bones(string bid)
                    {
                        var b = v[bid];
                        if (b.Type == Kind.Bone) { r.BoneOfJoint[Index(bid)] = UniqueBone(bones, b.Name ?? b.DefaultName); foreach (var c in b.Children) Bones(c); }
                    }
                    foreach (var c in n.Children) Bones(c);
                }
                else if (n.CameraNode >= 0)
                {
                    // BlenderCamera.create: a datablock per camera OBJECT, named after the glTF camera or "Camera"
                    var cam = m.Cameras[m.Nodes[n.CameraNode].Camera];
                    string camName = (GlbReader.ParseObject(cam)["name"]?.ToString() is string cn && cn.Length > 0) ? cn : "Camera";
                    string camData = cameras.Unique(camName);   // ALWAYS made, so always reserved: a named node skipping it left "Lens" free for the next camera, and a mesh node named Lens became Lens.001
                    name = objects.Unique(n.Name ?? camData);
                }
                else name = objects.Unique(n.Name ?? n.DefaultName);
                int ni = Index(id);
                if (ni >= 0) r.ObjectOfNode[ni] = name;
            }
            foreach (var c in n.Children) Create(c);
        }
        Create("root");
        // a skin's armature is the one its joints are bones of (the importer reads it off the first joint) - not always
        // the one the skin itself would have made: a skin whose joints lie inside another skin's chain makes none
        r.ArmatureNodeOfBone = new int[m.Nodes.Count];
        for (int i = 0; i < m.Nodes.Count; i++) r.ArmatureNodeOfBone[i] = v[Key(i)].Type == Kind.Bone && v[Key(i)].BoneArma != null ? Index(v[Key(i)].BoneArma) : -1;
        for (int si = 0; si < m.Skins.Count; si++)
        {
            string arma = m.Skins[si].Joints.Length > 0 ? v[Key(m.Skins[si].Joints[0])].BoneArma : null;
            if (arma == null || !armaName.ContainsKey(arma)) { r.ArmatureNodeOfSkin[si] = -1; continue; }
            r.ArmatureOfSkin[si] = armaName[arma]; r.ArmatureNodeOfSkin[si] = Index(arma);
        }
        r.ObjectPool = objects;
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

    // ---- unique names. Blender has TWO rules, and a name's numeric tail means something else in each (ported from
    // Blender 5.1's own source - blenlib/intern/string_utils.cc, blenkernel/intern/main_namemap.cc - and measured
    // branch by branch on the name_tails fixtures; external review of PR #112 found the first port, one rule for
    // both with char.IsDigit and int.Parse, throwing on "Hull.\u0661" - and the measurement behind that fix found it
    // wrong for every duplicate with a tail of its own: two objects named B.7 are B.7 and B.001, not B.008).

    /// <summary>BLI_string_split_name_number: the base before the LAST dot and the number behind it - when what is
    /// behind it is ASCII digits only and fits an int. Anything else (no dot, nothing behind it, a letter, a digit
    /// of another script such as "\u0661", a number past 2147483647) is no numeric tail: the whole name, number 0.
    /// A name that STARTS with its dot (".5") has an empty base.</summary>
    public static (string left, int number) SplitNumber(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return (name, 0);
        long value = 0;
        for (int i = dot + 1; i < name.Length; i++)
        {
            char c = name[i];
            if (c < '0' || c > '9') return (name, 0);
            if (value <= int.MaxValue) value = value * 10 + (c - '0');   // past an int it stays past it; no overflow of the long
        }
        return value > int.MaxValue ? (name, 0) : (name.Substring(0, dot), (int)value);
    }

    /// <summary>The names of one kind of datablock (objects, meshes, armatures, cameras): Blender's Main name map.
    /// A free name is taken as given. A taken one gets its base's SMALLEST unused number from 1 (exact up to 1023;
    /// past that, one above the highest seen) as ".%03d" - never counting up from the name's own tail. Names hold
    /// 255 bytes of UTF-8; one that would not fit with its number is cut by a character and tried again AS A NAME
    /// (so the second of two 255-byte names is the 254-byte one, without a number).</summary>
    public sealed class NamePool
    {
        public const int MaxBytes = 255;
        const int MaxExact = 1023, MaxNumber = 999999999, None = -1;

        /// <summary>UniqueName_Value: which numbers one base name has in use.</summary>
        sealed class Numbers
        {
            public int? Max; public List<bool> Mask = new List<bool>(); public Dictionary<int, int> Multi;

            public void MarkUsed(int number)
            {
                if (number <= MaxExact)
                {
                    while (Mask.Count <= number) Mask.Add(false);
                    if (Mask[number]) { if (Multi == null) Multi = new Dictionary<int, int>(); Multi[number] = (Multi.TryGetValue(number, out int n) ? n : 1) + 1; }   // "G.1" and "G.001"
                    else Mask[number] = true;
                }
                if (number <= MaxNumber) Max = Max.HasValue ? Math.Max(Max.Value, number) : number;
            }

            public void MarkUnused(int number)
            {
                if (number <= MaxExact)
                {
                    if (Multi != null && Multi.TryGetValue(number, out int n)) { if (n - 1 == 1) Multi.Remove(number); else Multi[number] = n - 1; return; }
                    if (number < Mask.Count) Mask[number] = false;
                }
                if (Max.HasValue && number == Max.Value) { if (number > 0) Max = Max.Value - 1; else Max = null; }
            }

            public int SmallestUnused()
            {
                if (Mask.Count < 2) return 1;
                for (int i = 1; i < Mask.Count; i++) if (!Mask[i]) return i;   // never 0: a second Foo.001 is Foo.002, not Foo
                if (Mask.Count <= MaxExact) return Mask.Count;
                if (Max.HasValue) return Max.Value + 1 <= MaxNumber ? Max.Value + 1 : None;
                return 1;
            }

            public Numbers Clone() => new Numbers { Max = Max, Mask = new List<bool>(Mask), Multi = Multi == null ? null : new Dictionary<int, int>(Multi) };
        }

        readonly HashSet<string> full = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, Numbers> byBase = new Dictionary<string, Numbers>(StringComparer.Ordinal);

        public bool Contains(string name) => full.Contains(name);
        public int Count => full.Count;

        public NamePool Clone()
        {
            var c = new NamePool();
            foreach (var n in full) c.full.Add(n);
            foreach (var kv in byBase) c.byBase[kv.Key] = kv.Value.Clone();
            return c;
        }

        /// <summary>namemap_get_name: the name this datablock gets; it joins the pool.</summary>
        public string Unique(string name)
        {
            name = TruncateUtf8(name, MaxBytes);
            while (true)
            {
                var (left, number) = SplitNumber(name);
                if (!byBase.TryGetValue(left, out var val)) byBase[left] = val = new Numbers();
                if (full.Add(name)) { val.MarkUsed(number); return name; }
                int use = val.SmallestUnused();
                if (!FinalBuild(left, use, ref name)) continue;   // the name was edited: judge it afresh, as a name
                full.Add(name); val.MarkUsed(use);
                return name;
            }
        }

        /// <summary>A removed datablock frees its name, and its number for its base (the Lab's probe purges the
        /// importer's bone shapes before it splits a single mesh into loose parts).</summary>
        public void Remove(string name)
        {
            if (!full.Remove(name)) return;
            var (left, number) = SplitNumber(name);
            if (!byBase.TryGetValue(left, out var val)) return;
            val.MarkUnused(number);
            if (!val.Max.HasValue) byBase.Remove(left);
        }

        // id_name_final_build: base + ".NNN" when there is a number and the result fits; else a new NAME to try -
        // the base less its last character (a long one), or the base + "_001", "_002" ... (a short one out of numbers)
        bool FinalBuild(string left, int number, ref string name)
        {
            if (number != None)
            {
                name = left + "." + number.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
                if (Utf8Length(name) <= MaxBytes) return true;
            }
            name = left;
            bool Usable(string candidate) => !byBase.TryGetValue(candidate, out var v) || (v.Max ?? 0) < MaxNumber;
            while (Utf8Length(name) > 8)
            {
                name = DropLastCharacter(name);
                if (Usable(name)) return false;
            }
            string shortBase = name;
            for (ulong suffix = 1; ; suffix++)
            {
                name = shortBase + "_" + suffix.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
                if (Utf8Length(name) >= MaxBytes + 1 - 12) break;
                if (Usable(name)) return false;
            }
            // Blender's "absolute last defense" (random names, after a thousand million of one base): not ported
            throw new NotSupportedException($"no unique name left for '{left}': every number and every '{shortBase}_NNN' base is used up");
        }
    }

    /// <summary>A BONE's name within its armature - BLI_uniquename_cb, the older rule: 63 bytes of UTF-8; a taken name
    /// has its numeric tail split off and tail+1, tail+2 ... tried as ".%03d" until one is free (two bones named J.7
    /// are J.7 and J.008), the base cut so that the number fits. The chosen name joins the pool.</summary>
    public static string UniqueBone(ISet<string> pool, string name)
    {
        const int maxBytes = 63;
        name = TruncateUtf8(name, maxBytes);
        if (pool.Add(name)) return name;
        var (left, number) = SplitNumber(name);
        while (true)
        {
            number = unchecked(number + 1);   // C's ++ on an int: past 2147483647 Blender names the bone "Bn.-2147483648" (measured)
            string tail = "." + number.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
            string candidate = left.Length == 0 ? tail : TruncateUtf8(left, maxBytes - tail.Length) + tail;
            if (pool.Add(candidate)) return candidate;
        }
    }

    static int Utf8Length(string s) => System.Text.Encoding.UTF8.GetByteCount(s);

    /// <summary>BLI_strncpy_utf8: as many whole characters as fit in maxBytes of UTF-8.</summary>
    static string TruncateUtf8(string s, int maxBytes)
    {
        if (s.Length <= maxBytes / 3) return s;   // a UTF-16 unit is at most 3 bytes of UTF-8
        int bytes = 0, i = 0;
        while (i < s.Length)
        {
            int units = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            int size = units == 2 ? 4 : s[i] < 0x80 ? 1 : s[i] < 0x800 ? 2 : 3;
            if (bytes + size > maxBytes) break;
            bytes += size; i += units;
        }
        return i == s.Length ? s : s.Substring(0, i);
    }

    static string DropLastCharacter(string s)
    {
        if (s.Length == 0) return s;
        int cut = s.Length >= 2 && char.IsLowSurrogate(s[s.Length - 1]) && char.IsHighSurrogate(s[s.Length - 2]) ? 2 : 1;
        return s.Substring(0, s.Length - cut);
    }
}
