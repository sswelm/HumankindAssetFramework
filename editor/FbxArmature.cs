using System;
using System.Collections.Generic;
using System.Linq;

// FbxArmature.cs - the armatures, bones, bind poses and skin weights of an FBX file as Blender 5.1's FBX importer builds
// them, and the joints and skins its glTF exporter writes of them (sub-step 6c of the native FBX reader, 2026-11-02). Read
// at Blender 5.1.2: io_scene_fbx/import_fbx.py (FbxImportHelperNode and the "Objects & Armatures" section of load(), with
// the operator's defaults: ignore_leaf_bones off, force_connect_children off, automatic_bone_orientation off, the primary
// axis Y and the secondary X - no bone correction matrix), the armature kernel (armature_utils.cc: an edit bone from a
// matrix; leaving edit mode; armature.cc: where_is_bone, vec_roll_to_mat3, mat3_vec_to_roll; object.cc: the parent matrix
// of an object parented to a bone) and the exporter (io_scene_gltf2 blender/exp: tree.py, nodes.py, joints.py, skins.py,
// primitive_extract.py __get_bone_data).
// THE IMPORTER, as modelled:
//   * a Model of type LimbNode or Limb is a BONE; a Null or Root Model with a bone child becomes the ARMATURE itself, any
//     other parent of bones (the scene root included) gets a new helper "Armature" inserted between it and the bones
//     (find_armatures); a non-bone node under an armature with bones below it becomes a FAKE BONE, its data and non-bone
//     children moved to a helper of its own without a transform (find_fake_bones); leaf bones are not marked;
//   * bind matrices: every Pose of type BindPose gives its PoseNodes' matrices (the file's column-major doubles, float32)
//     to the helpers; then per bone its Cluster deformers (OO links out of the bone, in file order): the cluster's
//     TransformLink replaces the bind matrix, Transform (times TransformLink) is the mesh's bind matrix and
//     TransformAssociateModel the armature's, remembered per mesh object the cluster reaches through its Skin and Geometry
//     (OO links, both ways as the importer walks them: a cluster, skin or geometry without an outgoing link, or a geometry
//     linked to a non-Model, stops Blender's import - this reader declines the file); make_bind_pose_local turns the
//     global bind matrices local (parent.inverted_safe() @ bind, a bone without one taking its own matrix);
//   * collect_armature_meshes: a mesh any cluster of an armature's bones drives is MOVED under the armature (the last of
//     its children) with matrix = armature.get_world_matrix().inverted_safe() @ mesh.get_world_matrix() (the helpers'
//     chains, no corrections);
//   * find_correction_matrix: the root's children take the global matrix as pre_matrix; bones take no correction (default
//     axes);
//   * build_hierarchy: an armature datablock and object named after the helper (bpy.data.armatures, then objects), its
//     matrix_basis get_matrix() through the Euler round trip; edit bones depth first in children order, named through
//     BLI_uniquename (BlenderNames.UniqueBone): tail (0, 1, 0) * max(0.01, the mean distance of the bone children's bind
//     translations - the parent's size when there are none), then `bone.matrix = parent_bone_matrix @ bind.normalized()`
//     (ED_armature_ebone_from_mat4: head = the translation, tail = head + Y column * the length, roll = mat3_vec_to_roll:
//     vec_roll_to_mat3 of the Y column at roll 0, inverted, times the matrix, to a quaternion (normalize_m3, mat3_to_quat),
//     the twist about Y - atan2f(q.y, q.w) after making w non-negative); leaving edit mode (armature_finalize_restpose,
//     VehicleProbe.RestFromEditBones) gives bone.matrix_local; bone_child_matrix = Translation(-tail);
//   * set_pose_matrix: pose_bone.matrix_basis = bind.inverted_safe() @ matrix - the rest export ignores it, except that an
//     object parented to a posed bone follows the pose (left to Blender by name until the pose evaluation is ported);
//   * link: an object under a bone is parented to the armature with parent_type BONE, parentinv the identity, its
//     pre_matrix = bone_child_matrix @ pre_matrix and its basis get_matrix() again; a skinned mesh gets
//     matrix_parent_inverse = (global @ armature bind [@ armature geom]).inverted_safe() @ (global @ mesh bind [@ mesh
//     geom]) @ basis.inverted_safe() and an armature modifier; the bones' clusters make vertex groups named after the
//     bones, in bone order (depth first), weights set REPLACE and clamped to 0..1 (an index outside the mesh is ignored);
//   * matrix_world: (parent @ parentinv) @ local (BKE_object_where_is_calc); the parent of a bone child is the armature's
//     world @ the bone's pose matrix with its TAIL as translation (ob_parbone; pose = rest here).
// THE EXPORTER, as modelled: an armature node's children are its objects by name, then its root bones in creation order,
// then the neutral bone when a skinned mesh needs one; a joint's children are its bone children in creation order, then the
// objects parented to it by name; a joint's matrix_world is armature.matrix_world @ bone.matrix_local @ the Z-up to Y-up
// basis, its transform decomposed against its parent joint's or the armature's without normalization or snapping
// (VehicleProbe.ExporterTrs joint); an object under a bone is written against the joint, its world first taken as
// rest @ pose.inverted_safe() @ world (the rest-position rule, tree.py), and its descendants moved with it; a skinned
// mesh hangs from its armature with no transform, its positions through its matrix_world, its normals through the
// armature-space transform, its joints and weights per vertex from the vertex groups (BlenderExport.MeshPrimitives with a
// Skin: over 0.0001, by weight, four kept, normalized), a vertex without a bone sent to a "neutral_bone" joint the
// exporter appends; the skin (one per armature, named after the armature object): the bones depth first, the inverse
// bind matrices (basis @ (armature.matrix_world @ bone.matrix_local)).inverted_safe() column by column, the neutral
// bone's (basis @ armature.matrix_world).inverted_safe(); a skin is numbered when the first skinned mesh is traversed
// (its joints first, each after its children), an armature no mesh uses gets its skin after everything (unused skins).
public static class FbxArmature
{
    /// <summary>A bone of an armature as Blender holds it after the import.</summary>
    public sealed class Bone
    {
        public string Name = "";                 // unique within the armature (BLI_uniquename)
        internal FbxImport.Helper Helper;
        public Bone Parent; public readonly List<Bone> Children = new List<Bone>();   // creation order
        public float[] Head, Tail; public float Roll;   // the edit bone, armature space
        public float[] BoneMatrixEdit;           // the `bone_matrix` the children are built against (row-major mathutils items)
        public float[] BoneChildMatrix;          // Translation(-tail): the correction for objects parented to this bone (row-major)
        public float[] MatrixLocal;              // bone.matrix_local (arm_mat), Blender's M[col][row] = column-major
        public float Length;                     // bone.length
        public float[] PoseBasis;                // pose_bone.matrix_basis (row-major): bind.inverted_safe() @ matrix
        public bool Posed;                       // the pose basis is not the identity
        public readonly List<FbxImport.Obj> ObjectChildren = new List<FbxImport.Obj>();   // objects parented to this bone (creation order)
    }

    public sealed class Armature
    {
        public string Name = "";                 // the datablock's name
        public FbxImport.Obj Obj;                // the armature object
        internal FbxImport.Helper Helper;
        public readonly List<Bone> Bones = new List<Bone>();      // creation order (depth first)
        public readonly List<Bone> Roots = new List<Bone>();
        public readonly List<FbxImport.Obj> Meshes = new List<FbxImport.Obj>();   // the meshes its bones' clusters drive (skinned)
        public readonly HashSet<string> BonePool = new HashSet<string>(StringComparer.Ordinal);
    }

    sealed class DeclineException : Exception { public DeclineException(string why) : base(why) { } }

    // ---- the importer's helper-tree passes ------------------------------------------------------------------------------

    /// <summary>find_armatures: a Null/Root with bone children becomes an armature; any other parent gets an "Armature" helper inserted.</summary>
    static void FindArmatures(FbxImport.Helper h)
    {
        bool needs = h.Children.Exists(c => c.IsBone);
        if (needs)
        {
            FbxImport.Helper armature;
            if (h.Model != null && (h.Model.SubType == "Null" || h.Model.SubType == "Root")) { h.IsArmature = true; armature = h; }
            else
            {
                armature = new FbxImport.Helper { Name = "Armature", IsArmature = true };
                foreach (var c in h.Children.ToList()) if (c.IsBone) c.SetParent(armature);
                armature.SetParent(h);
            }
            FindArmatureBones(armature, armature);
        }
        foreach (var c in h.Children) { if (c.IsArmature || c.IsBone) continue; FindArmatures(c); }
    }

    static void FindArmatureBones(FbxImport.Helper h, FbxImport.Helper armature)
    {
        foreach (var c in h.Children) if (c.IsBone) { c.ArmatureHelper = armature; FindArmatureBones(c, armature); }
    }

    static bool FindBoneChildren(FbxImport.Helper h)
    {
        bool has = false;
        foreach (var c in h.Children) has |= FindBoneChildren(c);
        h.HasBoneChildren = has;
        return h.IsBone || has;
    }

    /// <summary>find_fake_bones: inside an armature a non-bone with bones below becomes a bone; a non-Null/Root one moves its
    /// data and its non-bone children to an intermediate helper without a transform.</summary>
    static void FindFakeBones(FbxImport.Helper h, bool inArmature)
    {
        if (inArmature && !h.IsBone && h.HasBoneChildren)
        {
            h.IsBone = true;
            if (!(h.Model != null && (h.Model.SubType == "Null" || h.Model.SubType == "Root")))
            {
                var node = new FbxImport.Helper { Model = h.Model, Data = h.Data, Name = h.Name };
                h.Model = null; h.Data = null;
                foreach (var c in h.Children.ToList()) { if (c.IsBone || c.HasBoneChildren) continue; c.SetParent(node); }
                node.SetParent(h);
            }
        }
        if (h.IsArmature) inArmature = true;
        foreach (var c in h.Children.ToList()) FindFakeBones(c, inArmature);
    }

    /// <summary>array_to_matrix4: 16 doubles column by column, as float32 mathutils items (row-major here).</summary>
    static float[] ArrayToMatrix4(double[] a)
    {
        var m = new float[16];
        for (int col = 0; col < 4; col++) for (int row = 0; row < 4; row++) m[row * 4 + col] = (float)a[col * 4 + row];
        return m;
    }

    static double[] Matrix16(FbxNode n, string child)
    {
        var e = n?.First(child);
        if (e == null || e.Properties.Count == 0) return null;
        var d = FbxScene.Reals(e, child);
        if (d.Length < 16) throw new DeclineException(child + ": fewer than 16 numbers (Blender's importer fails on it)");
        return d;
    }

    /// <summary>The bind poses: every Pose/BindPose's PoseNodes (Node id, Matrix) into the helpers, the last one winning.</summary>
    static void ReadBindPoses(IList<FbxObject> table, Dictionary<long, FbxImport.Helper> helpers)
    {
        foreach (var o in table)
        {
            if (o.Class != "Pose" || o.SubType != "BindPose") continue;
            foreach (var pn in o.Record.All("PoseNode"))
            {
                var nodeElem = pn.First("Node");
                if (nodeElem == null || nodeElem.Properties.Count == 0 || !FbxProps.IsNumber(nodeElem.Properties[0])) throw new DeclineException("a PoseNode without a Node id (Blender's importer fails on it)");
                long id = FbxProps.AsInt(nodeElem.Properties[0]);
                var matrix = Matrix16(pn, "Matrix");
                if (helpers.TryGetValue(id, out var bone) && matrix != null) bone.BindMatrix = ArrayToMatrix4(matrix);   // the root (id 0) included, as Blender has it
            }
        }
    }

    /// <summary>The clusters: per bone, the Deformer/Cluster objects it links into (OO), their matrices and the mesh objects they
    /// reach through their Skin and its Geometry.</summary>
    static void ReadClusters(FbxScene s, IList<FbxObject> table, Dictionary<long, FbxObject> byId, Dictionary<long, FbxImport.Helper> helpers, List<FbxImport.Helper> helperList)
    {
        var forward = new Dictionary<long, List<FbxConnection>>();
        foreach (var c in s.Connections) { if (!forward.TryGetValue(c.SrcId, out var l)) forward[c.SrcId] = l = new List<FbxConnection>(); l.Add(c); }
        List<FbxConnection> Out(long id, string what) { if (!forward.TryGetValue(id, out var l)) throw new DeclineException(what + " without an outgoing connection (Blender's importer fails on it)"); return l; }
        FbxObject Obj(long id) => byId.TryGetValue(id, out var o) ? o : null;
        foreach (var bone in helperList)
        {
            if (!bone.IsBone || bone.Model == null) continue;
            if (!forward.TryGetValue(bone.Model.Id, out var links)) continue;
            foreach (var link in links)
            {
                if (link.Kind != "OO") continue;
                var cluster = Obj(link.DstId);
                if (cluster == null || cluster.Class != "Deformer" || cluster.SubType != "Cluster") continue;
                var txMesh = Matrix16(cluster.Record, "Transform"); var txBone = Matrix16(cluster.Record, "TransformLink"); var txArm = Matrix16(cluster.Record, "TransformAssociateModel");
                var meshMatrix = txMesh != null ? ArrayToMatrix4(txMesh) : FbxImport.Identity4();
                float[] armatureMatrix = txArm != null ? ArrayToMatrix4(txArm) : null;
                if (txBone != null) { var tb = ArrayToMatrix4(txBone); meshMatrix = FbxImport.MatMul(tb, meshMatrix); bone.BindMatrix = tb; }
                var meshes = new HashSet<FbxImport.Helper>();
                foreach (var skinLink in Out(cluster.Id, "a cluster"))
                {
                    if (skinLink.Kind != "OO") continue;
                    var skin = Obj(skinLink.DstId);
                    if (skin == null || skin.Class != "Deformer" || skin.SubType != "Skin") continue;
                    if (!forward.TryGetValue(skin.Id, out var skinLinks)) continue;
                    foreach (var meshLink in skinLinks)
                    {
                        if (meshLink.Kind != "OO") continue;
                        var geom = Obj(meshLink.DstId);
                        if (geom == null || geom.Class != "Geometry" || geom.SubType != "Mesh") continue;
                        foreach (var objLink in Out(geom.Id, "a skinned geometry"))
                        {
                            if (objLink.Kind != "OO") continue;
                            if (!helpers.TryGetValue(objLink.DstId, out var meshNode)) throw new DeclineException("a skinned geometry linked to something that is not a Model (Blender's importer fails on it)");
                            if (meshNode.IsRoot) throw new DeclineException("a skinned geometry linked to the scene root (Blender's importer reparents the root under the armature)");
                            if (bone.ArmatureHelper == null) throw new DeclineException("a cluster on a bone without an armature");
                            meshNode.ArmatureSetup[bone.ArmatureHelper] = (meshMatrix, armatureMatrix);
                            meshes.Add(meshNode);
                        }
                    }
                }
                bone.Clusters.Add((cluster, meshes));
            }
        }
    }

    /// <summary>make_bind_pose_local, from the root with the identity.</summary>
    static void MakeBindPoseLocal(FbxImport.Helper h, float[] parentMatrix)
    {
        float[] bind;
        if (h.BindMatrix != null) bind = FbxImport.MatMul(VehicleProbe.InvertedSafe(parentMatrix), h.BindMatrix);
        else bind = h.Matrix != null ? (float[])h.Matrix.Clone() : null;
        h.BindMatrix = bind;
        if (bind != null) parentMatrix = FbxImport.MatMul(parentMatrix, bind);
        foreach (var c in h.Children) MakeBindPoseLocal(c, parentMatrix);
    }

    static float[] WorldAsParent(FbxImport.Helper h)
    {
        var m = h.Parent != null ? WorldAsParent(h.Parent) : FbxImport.Identity4();
        if (h.MatrixAsParent != null) m = FbxImport.MatMul(m, h.MatrixAsParent);
        return m;
    }
    static float[] World(FbxImport.Helper h)
    {
        var m = h.Parent != null ? WorldAsParent(h.Parent) : FbxImport.Identity4();
        if (h.Matrix != null) m = FbxImport.MatMul(m, h.Matrix);
        return m;
    }

    static void CollectSkeletonMeshes(FbxImport.Helper h, HashSet<FbxImport.Helper> meshes)
    {
        foreach (var (_, m) in h.Clusters) meshes.UnionWith(m);
        foreach (var c in h.Children) if (c.Meshes == null || c.Meshes.Count == 0) CollectSkeletonMeshes(c, meshes);
    }

    /// <summary>collect_armature_meshes: the meshes an armature's bones drive move under it, matrix = armature world inverse @ mesh world.</summary>
    static void CollectArmatureMeshes(FbxImport.Helper h)
    {
        if (h.IsArmature)
        {
            var armInv = VehicleProbe.InvertedSafe(World(h));
            var meshes = new HashSet<FbxImport.Helper>();
            // Python's `for child in self.children` over a list a nested armature shortens while it runs: by index, live
            for (int i = 0; i < h.Children.Count; i++)
            {
                var c = h.Children[i];
                CollectArmatureMeshes(c);
                if (c.Meshes == null || c.Meshes.Count == 0) CollectSkeletonMeshes(c, meshes);
            }
            foreach (var m in meshes.OrderBy(x => x.Order))   // a Python set: the order of iteration is not the insertion order; here every move lands at the END of the armature's children, so only the relative order among several moved meshes could differ (noted)
            {
                m.Matrix = FbxImport.MatMul(armInv, World(m));
                m.SetParent(h);
            }
            h.Meshes = meshes;
        }
        else for (int i = 0; i < h.Children.Count; i++) CollectArmatureMeshes(h.Children[i]);   // live, as Python iterates
    }

    // ---- the kernels ------------------------------------------------------------------------------------------------------

    static float[] Translation(float x, float y, float z) { var m = FbxImport.Identity4(); m[3] = x; m[7] = y; m[11] = z; return m; }

    /// <summary>Matrix.normalized(): normalize_m4 on a copy - each of the three axis columns normalized, its fourth entry divided by the length.</summary>
    static float[] Normalized(float[] it)
    {
        var m = (float[])it.Clone();
        for (int col = 0; col < 3; col++)
        {
            float x = m[0 * 4 + col], y = m[1 * 4 + col], z = m[2 * 4 + col];
            float len = VehicleProbe.NormalizeV3Blender(ref x, ref y, ref z);
            m[0 * 4 + col] = x; m[1 * 4 + col] = y; m[2 * 4 + col] = z;
            if (len != 0f) m[3 * 4 + col] = (float)(m[3 * 4 + col] / len);
        }
        return m;
    }

    /// <summary>mat3_vec_to_roll: the roll of `vec` that brings vec_roll_to_mat3(vec, 0) onto `mat` (both M[col][row], 9 floats).</summary>
    internal static float Mat3VecToRoll(float[] mat, float vx, float vy, float vz) => Mat3VecToRoll(mat, vx, vy, vz, null);

    internal static float Mat3VecToRoll(float[] mat, float vx, float vy, float vz, System.Text.StringBuilder trace)
    {
        float nx = vx, ny = vy, nz = vz; VehicleProbe.NormalizeV3Blender(ref nx, ref ny, ref nz);
        var vecmat = VehicleProbe.VecRollToMat3Normalized(nx, ny, nz, 0f);
        var vecmatinv = VehicleProbe.InvertM3(vecmat);
        var rollmat = VehicleProbe.MulM3(vecmatinv, mat);
        // mat3_to_quat: normalize_m3_m3 (each column), then mat3_normalized_to_quat_with_checks
        var cols = new float[3][];
        for (int c = 0; c < 3; c++) { float x = rollmat[c * 3], y = rollmat[c * 3 + 1], z = rollmat[c * 3 + 2]; VehicleProbe.NormalizeV3Blender(ref x, ref y, ref z); cols[c] = new[] { x, y, z }; }
        var q = VehicleProbe.Mat3NormalizedToQuat(cols);
        if (trace != null)
        {
            string B(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0).ToString("x8");
            trace.Append("nor ").Append(B(nx)).Append(' ').Append(B(ny)).Append(' ').Append(B(nz)).Append('\n');
            trace.Append("vecmat ").Append(string.Join(" ", vecmat.Select(B))).Append('\n');
            trace.Append("vecmatinv ").Append(string.Join(" ", vecmatinv.Select(B))).Append('\n');
            trace.Append("rollmat ").Append(string.Join(" ", rollmat.Select(B))).Append('\n');
            trace.Append("unit ").Append(string.Join(" ", cols.SelectMany(c => c).Select(B))).Append('\n');
            trace.Append("q ").Append(string.Join(" ", q.Select(B))).Append('\n');
        }
        // quat_split_swing_and_twist(q, 1): the canonical quaternion (w >= 0), the half twist about Y = atan2f(y, w), doubled
        if (q[0] < 0f) for (int i = 0; i < 4; i++) q[i] = -q[i];
        return (float)(2.0f * BlenderTrig.Atan2f(q[2], q[0]));
    }

    static float[] Col3(float[] it, int col) => new[] { it[0 * 4 + col], it[1 * 4 + col], it[2 * 4 + col] };

    // ---- the build ----------------------------------------------------------------------------------------------------------

    /// <summary>The whole "Objects & Armatures" section of load() after the helpers and links exist: armatures found, bind poses
    /// and clusters read, meshes collected, corrections set, the hierarchy built and linked, every object's matrix_world.
    /// `decline` names what stops Blender's import.</summary>
    internal static void Build(FbxImport.Scene r, FbxScene s, IList<FbxObject> table, Dictionary<long, FbxObject> byId, Dictionary<long, FbxImport.Helper> helpers, List<FbxImport.Helper> helperList, FbxImport.Helper root,
        Dictionary<FbxObject, FbxImport.Mesh> meshOf, Action<string> decline)
    {
        try { BuildInner(r, s, table, byId, helpers, helperList, root, meshOf); }
        catch (DeclineException e) { decline(e.Message); }
    }

    static void BuildInner(FbxImport.Scene r, FbxScene s, IList<FbxObject> table, Dictionary<long, FbxObject> byId, Dictionary<long, FbxImport.Helper> helpers, List<FbxImport.Helper> helperList, FbxImport.Helper root,
        Dictionary<FbxObject, FbxImport.Mesh> meshOf)
    {
        FindArmatures(root);
        FindBoneChildren(root);
        FindFakeBones(root, false);
        ReadBindPoses(table, helpers);
        ReadClusters(s, table, byId, helpers, helperList);
        MakeBindPoseLocal(root, FbxImport.Identity4());
        CollectArmatureMeshes(root);
        // find_correction_matrix: the root's children take the global matrix; no bone correction under the default axes
        foreach (var h in root.Children) h.PreMatrix = r.GlobalMatrix;
        // ---- build_hierarchy
        var objectPool = new BlenderNames.NamePool(); var armaturePool = new BlenderNames.NamePool();
        FbxImport.Obj NewObject(FbxImport.Helper h, FbxImport.Obj parent, FbxImport.ObjectKind kind)
        {
            var o = new FbxImport.Obj { Model = h.Model, Parent = parent, Name = objectPool.Unique(FbxImport.DefaultName(h.Name, "Object")), Kind = kind };
            if (kind == FbxImport.ObjectKind.Mesh && h.Data != null && meshOf.TryGetValue(h.Data, out var mesh)) o.Mesh = mesh;
            o.Basis = h.GetMatrix(); o.HelperMatrix = h.Matrix; o.HelperBase = h.MatrixAsParent; o.HelperGeom = h.MatrixGeom; o.HelperLclRot = h.LclRot;
            FbxImport.ApplyMat4(o);
            r.Objects.Add(o);
            if (parent != null) parent.Children.Add(o);
            h.Obj = o;
            return o;
        }
        FbxImport.ObjectKind KindOf(FbxImport.Helper h) => h.Data != null && meshOf.ContainsKey(h.Data) ? FbxImport.ObjectKind.Mesh : FbxImport.ObjectKind.Empty;

        void BuildSkeleton(Armature arm, FbxImport.Helper h, Bone parentBone, float[] parentMatrix, double parentBoneSize)
        {
            var bone = new Bone { Helper = h, Parent = parentBone, Name = BlenderNames.UniqueBone(arm.BonePool, FbxImport.DefaultName(h.Name, "Bone")) };   // edit_bones.new(""): "Bone"
            h.Bone = bone; arm.Bones.Add(bone);
            if (parentBone != null) parentBone.Children.Add(bone); else arm.Roots.Add(bone);
            // the mean distance of the bone children's bind translations (Vector.magnitude: the double root of the float dot)
            double size = 0; int count = 0;
            foreach (var c in h.Children) if (c.IsBone) { var t = Col3(c.GetBindMatrix(), 3); size += VehicleProbe.VecLength(t); count++; }
            size = count > 0 ? size / count : parentBoneSize;
            float tailY = (float)Math.Max(0.01, size);
            var boneTail = new[] { 0f, tailY, 0f };
            var boneMatrix = FbxImport.MatMul(parentMatrix, Normalized(h.GetBindMatrix()));
            // ED_armature_ebone_from_mat4 on an edit bone with head (0, 0, 0) and tail boneTail
            var head = Col3(boneMatrix, 3);
            var tail = new[] { (float)(boneTail[0] + head[0]), (float)(boneTail[1] + head[1]), (float)(boneTail[2] + head[2]) };
            float dx = (float)(tail[0] - head[0]), dy = (float)(tail[1] - head[1]), dz = (float)(tail[2] - head[2]);
            float len = VehicleProbe.Sqrtf((float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz)));
            var mat3 = new float[9];   // M[col][row]
            for (int col = 0; col < 3; col++) for (int row = 0; row < 3; row++) mat3[col * 3 + row] = boneMatrix[row * 4 + col];
            float vx = mat3[3], vy = mat3[4], vz = mat3[5];   // mat[1]: the Y column
            bone.Roll = Mat3VecToRoll(mat3, vx, vy, vz);
            bone.Head = head;
            bone.Tail = new[] { (float)(head[0] + (float)(vx * len)), (float)(head[1] + (float)(vy * len)), (float)(head[2] + (float)(vz * len)) };
            bone.BoneMatrixEdit = boneMatrix;
            bone.BoneChildMatrix = Translation(-boneTail[0], -boneTail[1], -boneTail[2]);
            foreach (var c in h.Children) if (c.IsBone) BuildSkeleton(arm, c, bone, boneMatrix, size);
            // (use_connect is decided here in Blender: it changes no matrix the export reads)
        }

        void BuildSkeletonChildren(FbxImport.Helper h, FbxImport.Obj armObj)
        {
            if (h.IsBone) { foreach (var c in h.Children) BuildSkeletonChildren(c, armObj); return; }
            if (h.Model == null) { foreach (var c in h.Children) BuildSkeletonChildren(c, armObj); return; }
            var o = NewObject(h, null, KindOf(h));
            foreach (var c in h.Children) BuildSkeletonChildren(c, armObj);
        }

        void BuildHierarchy(FbxImport.Helper h, FbxImport.Obj parentObj)
        {
            if (h.IsArmature)
            {
                var arm = new Armature { Helper = h, Name = armaturePool.Unique(FbxImport.DefaultName(h.Name, "Armature")) };
                var o = new FbxImport.Obj { Model = h.Model, Parent = parentObj, Name = objectPool.Unique(FbxImport.DefaultName(h.Name, "Object")), Kind = FbxImport.ObjectKind.Armature, Armature = arm };
                arm.Obj = o;
                o.Basis = h.GetMatrix(); o.HelperMatrix = h.Matrix; o.HelperBase = h.MatrixAsParent; o.HelperGeom = h.MatrixGeom; o.HelperLclRot = h.LclRot;
                FbxImport.ApplyMat4(o);
                r.Objects.Add(o); r.Armatures.Add(arm);
                if (parentObj != null) parentObj.Children.Add(o);
                h.Obj = o;
                foreach (var c in h.Children) if (c.IsBone) BuildSkeleton(arm, c, null, FbxImport.Identity4(), 1.0);
                // leaving edit mode: bone.matrix_local for every bone
                var ids = new List<int>(); var kids = new Dictionary<int, List<int>>(); var roots = new List<int>();
                var head = new Dictionary<int, float[]>(); var tail = new Dictionary<int, float[]>(); var roll = new Dictionary<int, float>();
                for (int i = 0; i < arm.Bones.Count; i++)
                {
                    var b = arm.Bones[i]; ids.Add(i); kids[i] = b.Children.Select(c => arm.Bones.IndexOf(c)).ToList();
                    head[i] = (float[])b.Head.Clone(); tail[i] = (float[])b.Tail.Clone(); roll[i] = b.Roll;
                    if (b.Parent == null) roots.Add(i);
                }
                VehicleProbe.RestFromEditBones(ids, kids, roots, head, tail, roll, (i, parent, armMat, offs, length) => { arm.Bones[i].MatrixLocal = armMat; arm.Bones[i].Length = length; });
                // set_pose_matrix: bind.inverted_safe() @ matrix
                foreach (var b in arm.Bones)
                {
                    b.PoseBasis = FbxImport.MatMul(VehicleProbe.InvertedSafe(b.Helper.GetBindMatrix()), b.Helper.GetMatrix());
                    var id = FbxImport.Identity4();
                    for (int i = 0; i < 16; i++) if (b.PoseBasis[i] != id[i]) { b.Posed = true; break; }
                }
                // the bone children: objects created in the skeleton's order
                foreach (var c in h.Children) BuildSkeletonChildren(c, o);
                return;
            }
            if (h.Model != null && !h.IsBone)
            {
                var o = NewObject(h, parentObj, KindOf(h));
                foreach (var c in h.Children) BuildHierarchy(c, o);
                return;
            }
            foreach (var c in h.Children) BuildHierarchy(c, parentObj);
        }
        foreach (var c in root.Children) BuildHierarchy(c, null);

        // ---- link_hierarchy
        void LinkSkeletonChildren(FbxImport.Helper h, Armature arm)
        {
            if (h.IsBone)
            {
                foreach (var c in h.Children)
                {
                    var childObj = c.Obj;
                    if (childObj != null && childObj != arm.Obj)
                    {
                        childObj.Parent = arm.Obj; childObj.ParentBone = h.Bone; arm.Obj.Children.Add(childObj); h.Bone.ObjectChildren.Add(childObj);
                        c.PreMatrix = c.PreMatrix != null ? FbxImport.MatMul(h.Bone.BoneChildMatrix, c.PreMatrix) : h.Bone.BoneChildMatrix;
                        childObj.Basis = c.GetMatrix(); FbxImport.ApplyMat4(childObj);
                    }
                    LinkSkeletonChildren(c, arm);
                }
                return;
            }
            var obj = h.Obj;
            foreach (var c in h.Children)
            {
                LinkSkeletonChildren(c, arm);
                if (c.Obj != null && obj != null && c.Obj.Parent == null && !c.IsBone) { c.Obj.Parent = obj; obj.Children.Add(c.Obj); }
            }
        }
        void SetBoneWeights(FbxImport.Helper h, Armature arm)
        {
            foreach (var (cluster, meshes) in h.Clusters)
            {
                var idxN = cluster.Record.First("Indexes"); var wN = cluster.Record.First("Weights");
                var indices = idxN != null && idxN.Properties.Count > 0 ? FbxScene.Integers(idxN, "Indexes") : new int[0];
                var weights = wN != null && wN.Properties.Count > 0 ? FbxScene.Reals(wN, "Weights") : new double[0];
                if (indices.Length != weights.Length) throw new DeclineException("a cluster whose Indexes and Weights differ in length (Blender's importer asserts)");
                if (indices.Length == 0) continue;
                foreach (var meshNode in meshes.OrderBy(m => m.Order))
                {
                    var o = meshNode.Obj; if (o == null || o.Mesh == null) continue;
                    int nv = o.Mesh.Positions.Length / 3;
                    int group = o.VertexGroups.IndexOf(h.Bone.Name);
                    if (group < 0) { group = o.VertexGroups.Count; o.VertexGroups.Add(h.Bone.Name); }
                    if (o.DefNr == null) { o.DefNr = new List<int>[nv]; o.DefWeight = new List<float>[nv]; }
                    for (int i = 0; i < indices.Length; i++)
                    {
                        int v = indices[i];
                        if (v < 0 || v >= nv) continue;   // vg.add ignores an index outside the mesh (measured: no error)
                        float w = (float)weights[i]; if (w < 0f) w = 0f; else if (w > 1f) w = 1f;   // the stored weight is clamped to 0..1 (measured)
                        if (o.DefNr[v] == null) { o.DefNr[v] = new List<int>(); o.DefWeight[v] = new List<float>(); }
                        int at = o.DefNr[v].IndexOf(group);
                        if (at < 0) { o.DefNr[v].Add(group); o.DefWeight[v].Add(w); } else o.DefWeight[v][at] = w;   // REPLACE
                    }
                }
            }
            foreach (var c in h.Children) if (c.IsBone) SetBoneWeights(c, arm);
        }
        void LinkHierarchy(FbxImport.Helper h)
        {
            if (h.IsArmature)
            {
                var arm = h.Obj.Armature;
                foreach (var c in h.Children)
                {
                    LinkSkeletonChildren(c, arm);
                    if (!c.IsBone && c.Obj != null && c.Obj.Parent == null) { c.Obj.Parent = arm.Obj; arm.Obj.Children.Add(c.Obj); }
                }
                if (h.Meshes != null)
                    foreach (var mesh in h.Meshes.OrderBy(m => m.Order))
                    {
                        if (mesh.Obj == null) continue;
                        var (mmat, amat) = mesh.ArmatureSetup[h];
                        if (amat == null) amat = h.BindMatrix;
                        amat = FbxImport.MatMul(r.GlobalMatrix, amat ?? FbxImport.Identity4());
                        if (h.MatrixGeom != null) amat = FbxImport.MatMul(amat, h.MatrixGeom);
                        mmat = FbxImport.MatMul(r.GlobalMatrix, mmat);
                        if (mesh.MatrixGeom != null) mmat = FbxImport.MatMul(mmat, mesh.MatrixGeom);
                        var basis = VehicleProbe.ToRowMajor(FbxImport.ObjectMatrixEuler(mesh.Obj.Loc, mesh.Obj.Eul, mesh.Obj.Size));
                        mesh.Obj.ParentInverse = FbxImport.MatMul(FbxImport.MatMul(VehicleProbe.InvertedSafe(amat), mmat), VehicleProbe.InvertedSafe(basis));
                        mesh.Obj.SkinArmature = arm.Obj; arm.Meshes.Add(mesh.Obj);
                    }
                foreach (var c in h.Children) if (c.IsBone) SetBoneWeights(c, arm);
                return;
            }
            if (h.Obj != null)
            {
                foreach (var c in h.Children)
                {
                    LinkHierarchy(c);
                    if (c.Obj != null && c.Obj.Parent == null && !c.IsArmature && !c.IsBone) { c.Obj.Parent = h.Obj; h.Obj.Children.Add(c.Obj); }
                }
                return;
            }
            foreach (var c in h.Children) LinkHierarchy(c);
        }
        LinkHierarchy(root);

        // ---- matrix_world: (parent @ parentinv) @ local; a bone child's parent matrix is the armature's world @ the bone's
        // pose matrix with its tail as translation (the rest here: a posed bone with an object child is left to Blender)
        var done = new HashSet<FbxImport.Obj>();
        float[] WorldOf(FbxImport.Obj o)
        {
            if (done.Contains(o)) return o.World;
            var local = FbxImport.ObjectMatrixEuler(o.Loc, o.Eul, o.Size);
            float[] parentMat = null;
            if (o.Parent != null)
            {
                parentMat = WorldOf(o.Parent);
                if (o.ParentBone != null)
                {
                    var b = o.ParentBone;
                    if (b.Posed) throw new DeclineException("an object parented to a posed bone '" + b.Name + "' (its bind pose differs from its transform): the pose evaluation is not ported (6d)");
                    var tmat = (float[])b.MatrixLocal.Clone();   // column-major: col 1 is the Y axis, col 3 the translation
                    tmat[12] = (float)(tmat[12] + (float)(tmat[4] * b.Length)); tmat[13] = (float)(tmat[13] + (float)(tmat[5] * b.Length)); tmat[14] = (float)(tmat[14] + (float)(tmat[6] * b.Length));
                    parentMat = VehicleProbe.MulM4(parentMat, tmat);
                }
            }
            if (parentMat == null) o.World = local;
            else
            {
                var pm = o.ParentInverse != null ? VehicleProbe.MulM4(parentMat, VehicleProbe.ToColumnMajor(o.ParentInverse)) : parentMat;
                o.World = VehicleProbe.MulM4(pm, local);
            }
            done.Add(o);
            return o.World;
        }
        foreach (var o in r.Objects) WorldOf(o);
    }
}
