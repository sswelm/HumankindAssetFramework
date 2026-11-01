using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// FbxImport.cs - a binary FBX file as Blender 5.1's FBX importer builds it (step 6a of the native FBX reader, 2026-11-01:
// the Factory's FBX path without Blender). Read at Blender 5.1.2, io_scene_fbx/import_fbx.py with the operator's defaults
// (no manual orientation, global scale 1, no baked space transform, custom normals on, pre/post rotation on, sRGB colours),
// bpy_extras/io_utils.py (axis_conversion) and the object kernel (object.cc). What it models, STATIC FILES:
//   * the global matrix: Scale(UnitScaleFactor / 100) @ axis_conversion(from the GlobalSettings' Up/Front/Coord axes through
//     RIGHT_HAND_AXES, Z up / Y forward when unknown) - mathutils float32 matrices throughout;
//   * every Model is a helper node (fbx_table_nodes order = the Objects' file order; a repeated id keeps its first place and
//     its last record); its transform data from Properties70 with the template's as the fallback (a property whose flags
//     contain 'U' is skipped); blen_read_object_transform_do: T @ Roff @ Rp @ Rpre @ R @ Rpost^-1 @ Rp^-1 @ Soff @ Sp @ S @
//     Sp^-1 (each a float32 mathutils matrix; the rotations Euler(radians).to_matrix() in the file's order; pre/post only
//     with RotationActive) and the geometric T @ R @ S after it; OO connections in FILE ORDER make the hierarchy (a child
//     connected twice ends under the LAST parent; a Geometry or NodeAttribute connected into a Model is its data);
//   * a Model of type LimbNode/Limb is a BONE: such a file is left to Blender here (sub-step 6c), as is a Pose, a Shape, a
//     Cluster and an AnimationStack (6c, 6d);
//   * the root's children get the global matrix as pre_matrix; an object's matrix_basis = pre @ (base @ geom) through
//     obj.matrix_basis = ... (BKE_object_apply_mat4: mat4_to_loc_rot_size, the rotation to a quaternion and on to XYZ Euler -
//     the object's rotation mode -, float32), and its matrix_world = BKE_object_to_mat4 (eulO_to_mat3, the scale, the
//     location) under its parent's: parent @ parentinv(identity) @ local, mul_m4_m4m4's association; objects are created
//     depth first from the root in children order (build_hierarchy) - that is the scene's object order -, each named by
//     validate_blend_names(the Model's name) through Blender's object name pool;
//   * meshes (blen_read_geom): positions as float32, polygons from PolygonVertexIndex (a negative index ends a polygon), the
//     Edges array (each entry a corner; the edge runs to the next corner of the polygon), the material layer (ByPolygon /
//     AllSame), UV layers (ByPolygonVertex direct or indexed, ByVertice, AllSame; an index out of range leaves the default;
//     too little data leaves the rest at the default; too much is cut), smoothing (ByEdge: sharp = not smooth; ByPolygon:
//     sharp = (value == 0)), normals per corner (the three tries: corners, polygons, vertices), mesh.validate() (polygons
//     under three corners, out-of-range or repeated vertices, duplicate polygons by their vertex ring either way round -
//     the later one goes; edges with equal ends or repeated), then normals_split_custom_set (VehicleProbe.PolygonNormals);
//     Blender's mesh name pool names the datablock after the Geometry;
//   * materials in slot order: the Material objects connected INTO the Model (connection order, any kind); a mesh shared
//     by a second object with other materials gets object-level slots; a material index past the slots resets to 0;
//   * colour layers and shape keys are NAMED (Fallback) for 6b.
// Proof: tools/fbx_model_drill.sh (Blender imports every file and exports a GLB; FbxModel writes the same from this).
public static class FbxImport
{
    public sealed class Mesh
    {
        public string Name = "";                 // the datablock's name, unique among meshes
        public FbxObject Geometry;
        public float[] Positions = new float[0]; // 3 per vertex, float32
        public int[] CornerVerts = new int[0];   // every face's corners in order
        public int[] FaceOffsets = { 0 };        // nf + 1
        public int[] FaceMaterial;               // per face, or null when the file has no material layer (every face 0)
        public bool[] SharpFace;                 // per face (false when no ByPolygon smoothing)
        public HashSet<long> SharpEdges;         // VehicleProbe.SharpEdgeKey pairs, or null
        public List<(string name, float[] uv)> Uv = new List<(string, float[])>();   // per layer, 2 per corner
        public short[] CustomNormal;             // 2 per corner, or null (no normal layer)
        public int ColorLayers;                  // named, not modelled (6b)
        public List<FbxObject> Materials = new List<FbxObject>();   // the datablock's slots (the first object's)
        public int FaceCount => FaceOffsets.Length - 1;
    }

    public sealed class Obj
    {
        public string Name = "";                 // unique object name
        public FbxObject Model;
        public Obj Parent;
        public readonly List<Obj> Children = new List<Obj>();   // creation order
        public Mesh Mesh;                        // or null (an empty)
        public List<FbxObject> Materials;        // the OBJECT's slots: the mesh's unless this object overrides them
        public float[] Basis;                    // matrix_basis as set (row-major mathutils items), before the Euler round trip
        public float[] HelperMatrix, HelperBase, HelperGeom, HelperLclRot;   // the importer's chain, for the drill's probe
        public float[] Loc, Eul, Size;           // the object's stored transform (float32)
        public float[] World;                    // matrix_world, column-major float32 (Blender's m[col][row])
    }

    public sealed class Scene
    {
        public FbxScene Source;
        public string Fallback;                  // non-null: what this file has that 6a does not model; Blender's to import
        public readonly List<string> Reasons = new List<string>();
        public readonly List<Mesh> Meshes = new List<Mesh>();       // Geometry file order
        public readonly List<Obj> Objects = new List<Obj>();        // creation order (the scene's object order)
        public readonly List<FbxObject> Materials = new List<FbxObject>();   // Material objects, file order
        public readonly Dictionary<FbxObject, string> MaterialName = new Dictionary<FbxObject, string>();
        public float[] GlobalMatrix;             // row-major mathutils items
        public double GlobalScale;
        public readonly List<string> Notes = new List<string>();
    }

    const double DegToRad = Math.PI / 180.0;

    public static Scene Read(string path) => Build(FbxScene.Read(path));

    public static Scene Build(FbxScene s)
    {
        var r = new Scene { Source = s };
        void Decline(string why) { r.Reasons.Add(why); if (r.Fallback == null) r.Fallback = why; }
        if (s.Version < 7100) Decline($"version {s.Version}: Blender's importer refuses files before 7100");
        // ---- GlobalSettings: unit scale and axes (load(): unit_scale / 100 for a factory scene; axes through RIGHT_HAND_AXES)
        var gs = s.Document.First("GlobalSettings");
        if (gs == null || gs.First("Properties70") == null) Decline("no GlobalSettings: Blender's importer refuses the file");
        double unitScale = PropNumber(s.GlobalSettings, "UnitScaleFactor", 1.0);
        r.GlobalScale = 1.0 * (unitScale / 100.0);
        var axisForward = (PropInteger(s.GlobalSettings, "FrontAxis", 1), PropInteger(s.GlobalSettings, "FrontAxisSign", 1));
        var axisUp = (PropInteger(s.GlobalSettings, "UpAxis", 2), PropInteger(s.GlobalSettings, "UpAxisSign", 1));
        var axisCoord = (PropInteger(s.GlobalSettings, "CoordAxis", 0), PropInteger(s.GlobalSettings, "CoordAxisSign", 1));
        var (up, forward) = RightHandAxes(axisUp, axisForward, axisCoord);
        r.GlobalMatrix = MatMul(MatScale((float)r.GlobalScale), AxisConversion(forward, up, "Y", "Z"));
        // ---- what 6a leaves to Blender
        foreach (var o in s.Objects)
        {
            if (o.Class == "Model" && (o.SubType == "LimbNode" || o.SubType == "Limb")) { Decline("bones: a Model of type " + o.SubType + " (an armature; sub-step 6c)"); break; }
        }
        if (s.Objects.Exists(o => o.Class == "Deformer")) Decline("deformers: a Skin, Cluster or BlendShape (sub-steps 6b/6c)");
        if (s.Objects.Exists(o => o.Class == "Geometry" && o.SubType == "Shape")) Decline("shape keys (sub-step 6b)");
        // a stack or layer without any curve node makes no action in Blender (blen_read_animations skips empty layers): static
        if (s.Objects.Exists(o => o.Class == "AnimationCurveNode")) Decline("animation (sub-step 6d)");
        if (s.Objects.Exists(o => o.Class == "NodeAttribute" && (o.SubType == "Camera" || o.SubType == "Light"))) Decline("a camera or light: its correction matrix and the exporter's filter are not modelled");
        if (r.Fallback != null) return r;
        // ---- the FBX table: id -> object, the Objects' file order; a repeated id keeps its first place and its LAST record
        var table = new List<FbxObject>(); var slot = new Dictionary<long, int>();
        foreach (var o in s.Objects)
        {
            if (slot.TryGetValue(o.Id, out int at)) table[at] = o;
            else { slot[o.Id] = table.Count; table.Add(o); }
        }
        var byId = new Dictionary<long, FbxObject>(); foreach (var o in table) byId[o.Id] = o;
        // connections as the importer maps them: forward (src -> dsts) and reverse (dst -> srcs), file order, only LL records
        var reverse = new Dictionary<long, List<FbxConnection>>();
        foreach (var c in s.Connections)
        {
            if (!reverse.TryGetValue(c.DstId, out var l)) reverse[c.DstId] = l = new List<FbxConnection>();
            l.Add(c);
        }
        // ---- meshes (blen_read_geom), in table order; materials
        var meshPool = new BlenderNames.NamePool(); var meshOf = new Dictionary<FbxObject, Mesh>();
        foreach (var o in table)
        {
            if (o.Class != "Geometry" || o.SubType != "Mesh") continue;
            var m = ReadGeometry(o, r);
            m.Name = meshPool.Unique(DefaultName(ValidateBlendName(o.Name), "Mesh"));
            r.Meshes.Add(m); meshOf[o] = m;
        }
        var materialPool = new BlenderNames.NamePool();
        foreach (var o in table)
        {
            if (o.Class != "Material") continue;
            r.Materials.Add(o); r.MaterialName[o] = materialPool.Unique(DefaultName(ValidateBlendName(o.Name), "Material"));
        }
        if (r.Fallback != null) return r;
        // ---- helper nodes: every Model, then the OO links in file order
        var helpers = new Dictionary<long, Helper>(); var helperList = new List<Helper>();
        var root = new Helper { IsRoot = true };
        helpers[0] = root;
        foreach (var o in table)
        {
            if (o.Class != "Model") continue;
            var h = new Helper { Model = o, Name = ValidateBlendName(o.Name) };
            ReadTransform(o.Props, h);
            helpers[o.Id] = h; helperList.Add(h);
        }
        foreach (var c in s.Connections)
        {
            if (c.Kind != "OO") continue;
            if (!helpers.TryGetValue(c.DstId, out var parent)) continue;
            if (helpers.TryGetValue(c.SrcId, out var child)) { child.SetParent(parent); continue; }
            if (!byId.TryGetValue(c.SrcId, out var data)) continue;
            if (data.Class != "Geometry" && data.Class != "NodeAttribute") continue;
            parent.Data = data;   // bl_data: the Geometry's mesh (or a NodeAttribute: an empty here)
        }
        // ---- the correction matrices: the root's children take the global matrix (find_correction_matrix with no bones)
        foreach (var h in root.Children) h.PreMatrix = r.GlobalMatrix;
        // ---- build_hierarchy: objects depth first from the root, named through the object pool, matrix_basis applied
        var objectPool = new BlenderNames.NamePool();
        void Build(Helper h, Obj parentObj)
        {
            if (h.Model != null)
            {
                var o = new Obj { Model = h.Model, Parent = parentObj, Name = objectPool.Unique(DefaultName(h.Name, "Object")) };
                if (h.Data != null && meshOf.TryGetValue(h.Data, out var mesh)) o.Mesh = mesh;
                o.Basis = h.GetMatrix(); o.HelperMatrix = h.Matrix; o.HelperBase = h.MatrixAsParent; o.HelperGeom = h.MatrixGeom; o.HelperLclRot = h.LclRot;
                ApplyMat4(o);
                r.Objects.Add(o);
                if (parentObj != null) parentObj.Children.Add(o);
                h.Obj = o;
                foreach (var c in h.Children) Build(c, o);
            }
            else foreach (var c in h.Children) Build(c, parentObj);
        }
        foreach (var c in root.Children) Build(c, null);
        // ---- matrix_world: parent @ local (the object's own transform through BKE_object_to_mat4), roots first
        foreach (var o in r.Objects)
        {
            var local = ObjectMatrixEuler(o.Loc, o.Eul, o.Size);
            o.World = o.Parent != null ? VehicleProbe.MulM4(o.Parent.World, local) : local;
        }
        // ---- materials: per mesh object, the Materials connected into its Model (connection order, any kind); the first
        // object fills the datablock's slots, another object with a different list overrides per slot (link = OBJECT)
        foreach (var o in r.Objects)
        {
            if (o.Mesh == null) continue;
            var mats = new List<FbxObject>();
            if (reverse.TryGetValue(o.Model.Id, out var into)) foreach (var c in into) if (c.Src != null && c.Src.Class == "Material" && byId.TryGetValue(c.SrcId, out var mo) && mo == c.Src) mats.Add(c.Src);
            var mesh = o.Mesh;
            if (mats.Count == 0) { o.Materials = mesh.Materials; continue; }
            if (mesh.Materials.Count == 0) { mesh.Materials.AddRange(mats); o.Materials = mesh.Materials; continue; }
            // num_mesh_mats < len(connections): the extra ones are appended to the MESH; the first ones compared slot by slot
            var toAppend = mats.Count > mesh.Materials.Count ? mats.Skip(mesh.Materials.Count).ToList() : new List<FbxObject>();
            var toCompare = mats.Count >= mesh.Materials.Count ? mats.Take(mesh.Materials.Count).ToList() : mats;
            mesh.Materials.AddRange(toAppend);
            var own = new List<FbxObject>(mesh.Materials);
            for (int i = 0; i < toCompare.Count && i < own.Count; i++) if (toCompare[i] != own[i]) own[i] = toCompare[i];
            o.Materials = own.SequenceEqual(mesh.Materials) ? mesh.Materials : own;
        }
        foreach (var o in r.Objects) if (o.Mesh != null && o.Materials == null) o.Materials = o.Mesh.Materials;
        // validate_material_indices: an index at or past the slot count resets to 0 (against max(slots, 1))
        foreach (var m in r.Meshes)
        {
            if (m.FaceMaterial == null) continue;
            int max = Math.Max(m.Materials.Count, 1);
            for (int f = 0; f < m.FaceMaterial.Length; f++) if (m.FaceMaterial[f] >= max) m.FaceMaterial[f] = 0;
        }
        return r;
    }

    // ---- Properties70 lookups: the object's block, then the template's; a 'U' in the flags skips the property ------------
    static FbxProp FindProp(FbxProps props, string name)
    {
        for (var p = props; p != null; p = p.Defaults)
        {
            var prop = p.Own(name);
            if (prop != null && !prop.NoValue) { if (prop.Flags.IndexOf('U') >= 0) continue; return prop; }
        }
        return null;
    }
    static double PropNumber(FbxProps props, string name, double def) { var p = FindProp(props, name); return p != null && p.RealCount > 0 ? p.Reals[0] : def; }
    static long PropInteger(FbxProps props, string name, long def) { var p = FindProp(props, name); return p != null && p.HasInt ? p.ValueInt : def; }
    static bool PropBool(FbxProps props, string name, bool def) { var p = FindProp(props, name); return p != null && p.HasInt ? p.ValueInt != 0 : def; }
    static long PropEnum(FbxProps props, string name, long def) { var p = FindProp(props, name); return p != null && p.HasInt ? p.ValueInt : def; }
    static double[] PropVec3(FbxProps props, string name, double x, double y, double z) { var p = FindProp(props, name); return p != null && p.RealCount >= 3 ? new[] { p.Reals[0], p.Reals[1], p.Reals[2] } : new[] { x, y, z }; }

    /// <summary>bpy.data.<kind>.new(name=""): an EMPTY name takes the ID type's default (BKE_libblock_alloc: "Mesh", "Object",
    /// "Material"), then the pool's numbering.</summary>
    static string DefaultName(string name, string def) => name.Length == 0 ? def : name;

    /// <summary>validate_blend_names: a name over 63 UTF-8 bytes is cut to 55 bytes + "_" + 7 hex digits of its SHA-1, cut further
    /// until the UTF-8 fits; invalid UTF-8 was already replaced by the document reader.</summary>
    internal static string ValidateBlendName(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length <= 63) return name;
        string h;
        using (var sha = System.Security.Cryptography.SHA1.Create()) h = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        int n = 55;
        string result = Decode(bytes, n) + "_" + h.Substring(0, 7);
        while (Encoding.UTF8.GetByteCount(result) > 63) { n--; result = Decode(bytes, n) + "_" + h.Substring(0, 7); }
        return result;
    }
    static string Decode(byte[] b, int n) => new UTF8Encoding(false, false).GetString(b, 0, Math.Min(n, b.Length));   // 'replace' errors: the decoder substitutes U+FFFD

    // ---- The global matrix (mathutils float32, row-major items) -----------------------------------------------------------
    static readonly Dictionary<((long, long), (long, long), (long, long)), (string up, string forward)> RightHand = new Dictionary<((long, long), (long, long), (long, long)), (string, string)>
    {
        { ((0, 1), (1, 1), (2, 1)), ("X", "-Y") }, { ((0, 1), (1, -1), (2, -1)), ("X", "Y") }, { ((0, 1), (2, 1), (1, -1)), ("X", "-Z") }, { ((0, 1), (2, -1), (1, 1)), ("X", "Z") },
        { ((0, -1), (1, 1), (2, -1)), ("-X", "-Y") }, { ((0, -1), (1, -1), (2, 1)), ("-X", "Y") }, { ((0, -1), (2, 1), (1, 1)), ("-X", "-Z") }, { ((0, -1), (2, -1), (1, -1)), ("-X", "Z") },
        { ((1, 1), (0, 1), (2, -1)), ("Y", "-X") }, { ((1, 1), (0, -1), (2, 1)), ("Y", "X") }, { ((1, 1), (2, 1), (0, 1)), ("Y", "-Z") }, { ((1, 1), (2, -1), (0, -1)), ("Y", "Z") },
        { ((1, -1), (0, 1), (2, 1)), ("-Y", "-X") }, { ((1, -1), (0, -1), (2, -1)), ("-Y", "X") }, { ((1, -1), (2, 1), (0, -1)), ("-Y", "-Z") }, { ((1, -1), (2, -1), (0, 1)), ("-Y", "Z") },
        { ((2, 1), (0, 1), (1, 1)), ("Z", "-X") }, { ((2, 1), (0, -1), (1, -1)), ("Z", "X") }, { ((2, 1), (1, 1), (0, -1)), ("Z", "-Y") }, { ((2, 1), (1, -1), (0, 1)), ("Z", "Y") },
        { ((2, -1), (0, 1), (1, -1)), ("-Z", "-X") }, { ((2, -1), (0, -1), (1, 1)), ("-Z", "X") }, { ((2, -1), (1, 1), (0, 1)), ("-Z", "-Y") }, { ((2, -1), (1, -1), (0, -1)), ("-Z", "Y") },
    };
    static (string up, string forward) RightHandAxes((long, long) up, (long, long) forward, (long, long) coord) => RightHand.TryGetValue((up, forward, coord), out var v) ? v : ("Z", "Y");

    static readonly float[][] AxisMatrices =
    {
        new float[] { -1, 0, 0, 0, -1, 0, 0, 0, 1 },
        new float[] { -1, 0, 0, 0, 0, -1, 0, -1, 0 },
        new float[] { -1, 0, 0, 0, 0, 1, 0, 1, 0 },
        new float[] { -1, 0, 0, 0, 1, 0, 0, 0, -1 },
        new float[] { 0, -1, 0, -1, 0, 0, 0, 0, -1 },
        new float[] { 0, 0, 1, -1, 0, 0, 0, -1, 0 },
        new float[] { 0, 0, -1, -1, 0, 0, 0, 1, 0 },
        new float[] { 0, 1, 0, -1, 0, 0, 0, 0, 1 },
        new float[] { 0, -1, 0, 0, 0, 1, -1, 0, 0 },
        new float[] { 0, 0, -1, 0, -1, 0, -1, 0, 0 },
        new float[] { 0, 0, 1, 0, 1, 0, -1, 0, 0 },
        new float[] { 0, 1, 0, 0, 0, -1, -1, 0, 0 },
        new float[] { 0, -1, 0, 0, 0, -1, 1, 0, 0 },
        new float[] { 0, 0, 1, 0, -1, 0, 1, 0, 0 },
        new float[] { 0, 0, -1, 0, 1, 0, 1, 0, 0 },
        new float[] { 0, 1, 0, 0, 0, 1, 1, 0, 0 },
        new float[] { 0, -1, 0, 1, 0, 0, 0, 0, 1 },
        new float[] { 0, 0, -1, 1, 0, 0, 0, -1, 0 },
        new float[] { 0, 0, 1, 1, 0, 0, 0, 1, 0 },
        new float[] { 0, 1, 0, 1, 0, 0, 0, 0, -1 },
        new float[] { 1, 0, 0, 0, -1, 0, 0, 0, -1 },
        new float[] { 1, 0, 0, 0, 0, 1, 0, -1, 0 },
        new float[] { 1, 0, 0, 0, 0, -1, 0, 1, 0 },
    };
    static readonly int[][] AxisLut =
    {
        new[] { 0x05C, 0x09A, 0x119, 0x15D, 0x223, 0x2A2, 0x2E0, 0x365, 0x413, 0x454, 0x4D0, 0x511, 0x644, 0x682, 0x701, 0x745, 0x80B, 0x88A, 0x8C8, 0x94D, 0xA2B, 0xA6C, 0xAE8, 0xB29 },
        new[] { 0x05D, 0x09C, 0x11A, 0x159, 0x22B, 0x2AC, 0x2E8, 0x369, 0x423, 0x465, 0x4E0, 0x522, 0x645, 0x684, 0x702, 0x741, 0x813, 0x894, 0x8D0, 0x951, 0xA0B, 0xA4D, 0xAC8, 0xB0A },
        new[] { 0x05A, 0x099, 0x11D, 0x15C, 0x213, 0x291, 0x2D0, 0x354, 0x40B, 0x44A, 0x4C8, 0x50D, 0x642, 0x681, 0x705, 0x744, 0x82B, 0x8A9, 0x8E8, 0x96C, 0xA23, 0xA62, 0xAE0, 0xB25 },
        new[] { 0x059, 0x09D, 0x11C, 0x15A, 0x20B, 0x28D, 0x2C8, 0x34A, 0x42B, 0x469, 0x4E8, 0x52C, 0x641, 0x685, 0x704, 0x742, 0x823, 0x8A5, 0x8E0, 0x962, 0xA13, 0xA51, 0xAD0, 0xB14 },
        new[] { 0x063, 0x0A5, 0x120, 0x162, 0x21C, 0x29D, 0x2D9, 0x35A, 0x42C, 0x46B, 0x4E9, 0x528, 0x64B, 0x68D, 0x708, 0x74A, 0x804, 0x885, 0x8C1, 0x942, 0xA14, 0xA53, 0xAD1, 0xB10 },
        new[] { 0x053, 0x094, 0x110, 0x151, 0x21A, 0x29C, 0x2DD, 0x359, 0x422, 0x463, 0x4E5, 0x520, 0x66B, 0x6AC, 0x728, 0x769, 0x802, 0x884, 0x8C5, 0x941, 0xA0A, 0xA4B, 0xACD, 0xB08 },
        new[] { 0x06B, 0x0A9, 0x128, 0x16C, 0x21D, 0x299, 0x2DA, 0x35C, 0x40D, 0x44B, 0x4CA, 0x508, 0x653, 0x691, 0x710, 0x754, 0x805, 0x881, 0x8C2, 0x944, 0xA25, 0xA63, 0xAE2, 0xB20 },
        new[] { 0x04B, 0x08A, 0x108, 0x14D, 0x219, 0x29A, 0x2DC, 0x35D, 0x411, 0x453, 0x4D4, 0x510, 0x663, 0x6A2, 0x720, 0x765, 0x801, 0x882, 0x8C4, 0x945, 0xA29, 0xA6B, 0xAEC, 0xB28 },
        new[] { 0x062, 0x0A3, 0x125, 0x160, 0x214, 0x293, 0x2D1, 0x350, 0x41C, 0x45A, 0x4D9, 0x51D, 0x64A, 0x68B, 0x70D, 0x748, 0x82C, 0x8AB, 0x8E9, 0x968, 0xA04, 0xA42, 0xAC1, 0xB05 },
        new[] { 0x06C, 0x0AB, 0x129, 0x168, 0x225, 0x2A3, 0x2E2, 0x360, 0x41D, 0x45C, 0x4DA, 0x519, 0x654, 0x693, 0x711, 0x750, 0x80D, 0x88B, 0x8CA, 0x948, 0xA05, 0xA44, 0xAC2, 0xB01 },
        new[] { 0x051, 0x093, 0x114, 0x150, 0x20A, 0x28B, 0x2CD, 0x348, 0x41A, 0x459, 0x4DD, 0x51C, 0x669, 0x6AB, 0x72C, 0x768, 0x822, 0x8A3, 0x8E5, 0x960, 0xA02, 0xA41, 0xAC5, 0xB04 },
        new[] { 0x04D, 0x08B, 0x10A, 0x148, 0x229, 0x2AB, 0x2EC, 0x368, 0x419, 0x45D, 0x4DC, 0x51A, 0x665, 0x6A3, 0x722, 0x760, 0x811, 0x893, 0x8D4, 0x950, 0xA01, 0xA45, 0xAC4, 0xB02 },
        new[] { 0x065, 0x0A0, 0x122, 0x163, 0x22C, 0x2A8, 0x2E9, 0x36B, 0x404, 0x445, 0x4C1, 0x502, 0x64D, 0x688, 0x70A, 0x74B, 0x814, 0x890, 0x8D1, 0x953, 0xA1C, 0xA5D, 0xAD9, 0xB1A },
        new[] { 0x054, 0x090, 0x111, 0x153, 0x222, 0x2A0, 0x2E5, 0x363, 0x402, 0x444, 0x4C5, 0x501, 0x66C, 0x6A8, 0x729, 0x76B, 0x80A, 0x888, 0x8CD, 0x94B, 0xA1A, 0xA5C, 0xADD, 0xB19 },
        new[] { 0x069, 0x0A8, 0x12C, 0x16B, 0x20D, 0x288, 0x2CA, 0x34B, 0x405, 0x441, 0x4C2, 0x504, 0x651, 0x690, 0x714, 0x753, 0x825, 0x8A0, 0x8E2, 0x963, 0xA1D, 0xA59, 0xADA, 0xB1C },
        new[] { 0x04A, 0x088, 0x10D, 0x14B, 0x211, 0x290, 0x2D4, 0x353, 0x401, 0x442, 0x4C4, 0x505, 0x662, 0x6A0, 0x725, 0x763, 0x829, 0x8A8, 0x8EC, 0x96B, 0xA19, 0xA5A, 0xADC, 0xB1D },
        new[] { 0x060, 0x0A2, 0x123, 0x165, 0x204, 0x282, 0x2C1, 0x345, 0x414, 0x450, 0x4D1, 0x513, 0x648, 0x68A, 0x70B, 0x74D, 0x81C, 0x89A, 0x8D9, 0x95D, 0xA2C, 0xA68, 0xAE9, 0xB2B },
        new[] { 0x068, 0x0AC, 0x12B, 0x169, 0x205, 0x284, 0x2C2, 0x341, 0x425, 0x460, 0x4E2, 0x523, 0x650, 0x694, 0x713, 0x751, 0x81D, 0x89C, 0x8DA, 0x959, 0xA0D, 0xA48, 0xACA, 0xB0B },
        new[] { 0x050, 0x091, 0x113, 0x154, 0x202, 0x281, 0x2C5, 0x344, 0x40A, 0x448, 0x4CD, 0x50B, 0x668, 0x6A9, 0x72B, 0x76C, 0x81A, 0x899, 0x8DD, 0x95C, 0xA22, 0xA60, 0xAE5, 0xB23 },
        new[] { 0x048, 0x08D, 0x10B, 0x14A, 0x201, 0x285, 0x2C4, 0x342, 0x429, 0x468, 0x4EC, 0x52B, 0x660, 0x6A5, 0x723, 0x762, 0x819, 0x89D, 0x8DC, 0x95A, 0xA11, 0xA50, 0xAD4, 0xB13 },
        new[] { 0x044, 0x085, 0x101, 0x142, 0x220, 0x2A5, 0x2E3, 0x362, 0x428, 0x46C, 0x4EB, 0x529, 0x65C, 0x69D, 0x719, 0x75A, 0x808, 0x88D, 0x8CB, 0x94A, 0xA10, 0xA54, 0xAD3, 0xB11 },
        new[] { 0x042, 0x084, 0x105, 0x141, 0x210, 0x294, 0x2D3, 0x351, 0x420, 0x462, 0x4E3, 0x525, 0x65A, 0x69C, 0x71D, 0x759, 0x828, 0x8AC, 0x8EB, 0x969, 0xA08, 0xA4A, 0xACB, 0xB0D },
        new[] { 0x045, 0x081, 0x102, 0x144, 0x228, 0x2A9, 0x2EB, 0x36C, 0x408, 0x44D, 0x4CB, 0x50A, 0x65D, 0x699, 0x71A, 0x75C, 0x810, 0x891, 0x8D3, 0x954, 0xA20, 0xA65, 0xAE3, 0xB22 },
    };
    static int AxisNum(string a) { switch (a) { case "X": return 0; case "Y": return 1; case "Z": return 2; case "-X": return 3; case "-Y": return 4; default: return 5; } }

    /// <summary>bpy_extras.io_utils.axis_conversion(from_forward, from_up, to_forward, to_up) as a 4x4 (row-major items).</summary>
    internal static float[] AxisConversion(string fromForward, string fromUp, string toForward, string toUp)
    {
        var m = Identity4();
        if (fromForward == toForward && fromUp == toUp) return m;
        int value = AxisNum(fromForward) | AxisNum(fromUp) << 3 | AxisNum(toForward) << 6 | AxisNum(toUp) << 9;
        for (int i = 0; i < AxisLut.Length; i++)
            if (Array.IndexOf(AxisLut[i], value) >= 0)
            {
                var a = AxisMatrices[i];
                for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++) m[row * 4 + col] = a[row * 3 + col];
                return m;
            }
        throw new InvalidDataException("axis_conversion: unreachable axes " + fromForward + "/" + fromUp);
    }

    // ---- mathutils, float32, row-major items --------------------------------------------------------------------------------
    internal static float[] Identity4() { var m = new float[16]; m[0] = m[5] = m[10] = m[15] = 1f; return m; }
    internal static float[] MatMul(float[] a, float[] b) => VehicleProbe.MatMulMathutils(a, b);
    static float[] MatScale(float s) { var m = Identity4(); m[0] = m[5] = m[10] = s; return m; }
    static float[] MatTranslation(double[] t) { var m = Identity4(); m[3] = (float)t[0]; m[7] = (float)t[1]; m[11] = (float)t[2]; return m; }
    static float[] MatDiagonal(double[] s) { var m = Identity4(); m[0] = (float)s[0]; m[5] = (float)s[1]; m[10] = (float)s[2]; return m; }

    /// <summary>Euler(radians, order).to_matrix().to_4x4(): eulO_to_mat3 with the C runtime's float cos and sin (mathutils
    /// stores floats; the angle is math.radians in double, rounded to float).</summary>
    static float[] MatRotation(double[] deg, int order)
    {
        float[] e = { (float)(deg[0] * DegToRad), (float)(deg[1] * DegToRad), (float)(deg[2] * DegToRad) };
        var m3 = EulOToMat3(e, order);
        var m = Identity4();
        for (int col = 0; col < 3; col++) for (int row = 0; row < 3; row++) m[row * 4 + col] = m3[col][row];   // mat[col][row] items
        return m;
    }

    // the rotation orders: (i, j, k, parity) as Blender's rotOrders table has them, XYZ 0, XZY 1, YZX 2, YXZ 3, ZXY 4, ZYX 5
    static readonly (int i, int j, int k, bool parity)[] RotOrders = { (0, 1, 2, false), (0, 2, 1, true), (1, 2, 0, false), (1, 0, 2, true), (2, 0, 1, false), (2, 1, 0, true) };

    /// <summary>eulO_to_mat3 (math_rotation.cc): the parity order negates the angles (by their axis index); cos and sin in
    /// DOUBLE (the C runtime's), the products in double, each entry rounded once to float; mat[col][row].</summary>
    internal static float[][] EulOToMat3(float[] eul, int order)
    {
        var (i, j, k, parity) = RotOrders[order];
        double ti, tj, th;
        if (parity) { ti = -eul[i]; tj = -eul[j]; th = -eul[k]; } else { ti = eul[i]; tj = eul[j]; th = eul[k]; }
        double ci = BlenderTrig.Cos(ti), cj = BlenderTrig.Cos(tj), ch = BlenderTrig.Cos(th), si = BlenderTrig.Sin(ti), sj = BlenderTrig.Sin(tj), sh = BlenderTrig.Sin(th);
        double cc = ci * ch, cs = ci * sh, sc = si * ch, ss = si * sh;
        var M = new[] { new float[3], new float[3], new float[3] };
        M[i][i] = (float)(cj * ch); M[j][i] = (float)(sj * sc - cs); M[k][i] = (float)(sj * cc + ss);
        M[i][j] = (float)(cj * sh); M[j][j] = (float)(sj * ss + cc); M[k][j] = (float)(sj * cs - sc);
        M[i][k] = (float)(-sj); M[j][k] = (float)(cj * si); M[k][k] = (float)(cj * ci);
        return M;
    }

    // ---- the helper node (FbxImportHelperNode) -----------------------------------------------------------------------------
    sealed class Helper
    {
        public FbxObject Model; public FbxObject Data; public string Name = "Unknown";
        public bool IsRoot;
        public Helper Parent; public readonly List<Helper> Children = new List<Helper>();
        public float[] Matrix, MatrixAsParent, MatrixGeom, PreMatrix, PostMatrix, LclRot;
        public Obj Obj;
        public void SetParent(Helper p) { if (Parent != null) Parent.Children.Remove(this); Parent = p; if (p != null) p.Children.Add(this); }
        /// <summary>get_matrix: pre @ matrix @ post (each absent one the identity).</summary>
        public float[] GetMatrix()
        {
            var m = Matrix ?? Identity4();
            if (PreMatrix != null) m = MatMul(PreMatrix, m);
            if (PostMatrix != null) m = MatMul(m, PostMatrix);
            return m;
        }
    }

    /// <summary>blen_read_object_transform_preprocess + blen_read_object_transform_do: the helper's matrix (base @ geom), the
    /// matrix as parent (base) and the geometric matrix, from the Model's properties with the template behind them.</summary>
    static void ReadTransform(FbxProps props, Helper h)
    {
        var loc = PropVec3(props, "Lcl Translation", 0, 0, 0);
        var rot = PropVec3(props, "Lcl Rotation", 0, 0, 0);
        var sca = PropVec3(props, "Lcl Scaling", 1, 1, 1);
        var geomLoc = PropVec3(props, "GeometricTranslation", 0, 0, 0);
        var geomRot = PropVec3(props, "GeometricRotation", 0, 0, 0);
        var geomSca = PropVec3(props, "GeometricScaling", 1, 1, 1);
        var rotOfs = PropVec3(props, "RotationOffset", 0, 0, 0);
        var rotPiv = PropVec3(props, "RotationPivot", 0, 0, 0);
        var scaOfs = PropVec3(props, "ScalingOffset", 0, 0, 0);
        var scaPiv = PropVec3(props, "ScalingPivot", 0, 0, 0);
        bool rotActive = PropBool(props, "RotationActive", false);
        double[] preRot, pstRot; int rotOrd;
        if (rotActive)
        {
            preRot = PropVec3(props, "PreRotation", 0, 0, 0);
            pstRot = PropVec3(props, "PostRotation", 0, 0, 0);
            long order = PropEnum(props, "RotationOrder", 0);
            rotOrd = order >= 0 && order <= 5 ? (int)order : 0;   // 6 (spheric) and unknown values: XYZ
        }
        else { preRot = new double[3]; pstRot = new double[3]; rotOrd = 0; }
        var lclTranslation = MatTranslation(loc);
        var geomLocM = MatTranslation(geomLoc);
        var lclRot = MatMul(MatRotation(rot, rotOrd), Identity4());   // @ rot_alt_mat (the identity)
        var pre = MatRotation(preRot, 0);
        var pst = MatRotation(pstRot, 0);
        var geomRotM = MatRotation(geomRot, 0);
        var rotOfsM = MatTranslation(rotOfs); var rotPivM = MatTranslation(rotPiv); var scaOfsM = MatTranslation(scaOfs); var scaPivM = MatTranslation(scaPiv);
        var lclScale = MatDiagonal(sca); var geomScale = MatDiagonal(geomSca);
        var baseMat = MatMul(MatMul(MatMul(MatMul(MatMul(MatMul(MatMul(MatMul(MatMul(MatMul(lclTranslation, rotOfsM), rotPivM), pre), lclRot), VehicleProbe.InvertedSafe(pst)), VehicleProbe.InvertedSafe(rotPivM)), scaOfsM), scaPivM), lclScale), VehicleProbe.InvertedSafe(scaPivM));
        var geomMat = MatMul(MatMul(geomLocM, geomRotM), geomScale);
        h.Matrix = MatMul(baseMat, geomMat); h.MatrixAsParent = baseMat; h.MatrixGeom = geomMat; h.LclRot = lclRot;
    }

    // ---- obj.matrix_basis = m (BKE_object_apply_mat4, no parent, no compat) and BKE_object_to_mat4 ----------------------------
    static void ApplyMat4(Obj o)
    {
        var bm = VehicleProbe.ToColumnMajor(o.Basis);
        VehicleProbe.Mat4ToLocRotSize(bm, out var loc, out var rot, out var size);
        // BKE_object_mat3_to_rot, Euler: mat3_normalized_to_quat, the delta rotation (the identity quaternion inverted:
        // (1, -0, -0, -0)) multiplied in, then quat_to_eulO - normalize_qt_qt, quat_to_mat3, mat3_normalized_to_eulO
        var quat = VehicleProbe.Mat3NormalizedToQuat(rot);
        quat = MulQtQt(new[] { 1f, -0f, -0f, -0f }, quat);
        o.Eul = QuatToEulXYZ(quat);
        o.Loc = new[] { (float)(loc[0] - 0f), (float)(loc[1] - 0f), (float)(loc[2] - 0f) };
        o.Size = new[] { (float)(size[0] / 1f), (float)(size[1] / 1f), (float)(size[2] / 1f) };
    }

    /// <summary>mul_qt_qtqt, float32 term by term (BlenderPosedState.MulQtQt).</summary>
    static float[] MulQtQt(float[] a, float[] b)
    {
        return new[]
        {
            (float)((float)((float)((float)(a[0] * b[0]) - (float)(a[1] * b[1])) - (float)(a[2] * b[2])) - (float)(a[3] * b[3])),
            (float)((float)((float)((float)(a[0] * b[1]) + (float)(a[1] * b[0])) + (float)(a[2] * b[3])) - (float)(a[3] * b[2])),
            (float)((float)((float)((float)(a[0] * b[2]) + (float)(a[2] * b[0])) + (float)(a[3] * b[1])) - (float)(a[1] * b[3])),
            (float)((float)((float)((float)(a[0] * b[3]) + (float)(a[3] * b[0])) + (float)(a[1] * b[2])) - (float)(a[2] * b[1])),
        };
    }

    /// <summary>quat_to_eulO for XYZ: (with `normalize`, normalize_qt_qt first - the float32 root of the float32 dot, each
    /// component scaled by its reciprocal; a zero one becomes (0, 1, 0, 0)), quat_to_mat3 (the products in double through M_SQRT2), then
    /// mat3_normalized_to_eulO (mat3_normalized_to_eulo2 with hypotf and atan2f from the C runtime; of the two candidates the
    /// smaller sum of magnitudes, the first on a tie) - BlenderDeploy.QuatToEulXYZ, kept here without its Unity neighbours.</summary>
    internal static float[] QuatToEulXYZ(float[] qIn, bool normalize = false)
    {
        var q = (float[])qIn.Clone();
        // quat_to_eulO (the object's matrix_basis setter) takes the quaternion AS IT IS: mat3_normalized_to_quat_fast's output is
        // unit to a few ulps and quat_to_mat3 asserts it rather than normalizing; BKE_rotMode_change_values normalizes first
        if (normalize)
        {
            float dot = (float)((float)((float)((float)(q[0] * q[0]) + (float)(q[1] * q[1])) + (float)(q[2] * q[2])) + (float)(q[3] * q[3]));
            float len = VehicleProbe.Sqrtf(dot);
            if (len != 0f) { float f = (float)(1.0f / len); for (int i = 0; i < 4; i++) q[i] = (float)(q[i] * f); }
            else { q[1] = 1f; q[0] = q[2] = q[3] = 0f; }
        }
        const double s2 = 1.4142135623730951;
        double q0 = s2 * q[0], q1 = s2 * q[1], q2 = s2 * q[2], q3 = s2 * q[3];
        double qda = q0 * q1, qdb = q0 * q2, qdc = q0 * q3, qaa = q1 * q1, qab = q1 * q2, qac = q1 * q3, qbb = q2 * q2, qbc = q2 * q3, qcc = q3 * q3;
        var m = new[]
        {
            new[] { (float)(1.0 - qbb - qcc), (float)(qdc + qab), (float)(-qdb + qac) },
            new[] { (float)(-qdc + qab), (float)(1.0 - qaa - qcc), (float)(qda + qbc) },
            new[] { (float)(qdb + qac), (float)(-qda + qbc), (float)(1.0 - qaa - qbb) },
        };
        const float eps = 0.0000375f;   // EULER_HYPOT_EPSILON
        float cy = BlenderTrig.Hypotf(m[0][0], m[0][1]);
        float[] e1, e2;
        if (cy > eps)
        {
            e1 = new[] { BlenderTrig.Atan2f(m[1][2], m[2][2]), BlenderTrig.Atan2f(-m[0][2], cy), BlenderTrig.Atan2f(m[0][1], m[0][0]) };
            e2 = new[] { BlenderTrig.Atan2f(-m[1][2], -m[2][2]), BlenderTrig.Atan2f(-m[0][2], -cy), BlenderTrig.Atan2f(-m[0][1], -m[0][0]) };
        }
        else
        {
            e1 = new[] { BlenderTrig.Atan2f(-m[2][1], m[1][1]), BlenderTrig.Atan2f(-m[0][2], cy), 0f };
            e2 = (float[])e1.Clone();
        }
        float d1 = (float)((float)(Math.Abs(e1[0]) + Math.Abs(e1[1])) + Math.Abs(e1[2])), d2 = (float)((float)(Math.Abs(e2[0]) + Math.Abs(e2[1])) + Math.Abs(e2[2]));
        return d1 > d2 ? e2 : e1;
    }

    /// <summary>BKE_object_to_mat4 for an XYZ Euler object: eulO_to_mat3, the delta rotation (identity) multiplied in, the scale
    /// matrix, the location plus the delta location - the quaternion variant's steps (VehicleProbe.ObjectMatrix) with the Euler
    /// rotation. Column-major.</summary>
    internal static float[] ObjectMatrixEuler(float[] loc, float[] eul, float[] size)
    {
        var r = EulOToMat3(eul, 0);
        var rot = new float[3][];
        for (int i = 0; i < 3; i++)
        {
            rot[i] = new float[3];
            for (int j = 0; j < 3; j++) rot[i][j] = (float)((float)((float)(r[i][0] * (j == 0 ? 1f : 0f)) + (float)(r[i][1] * (j == 1 ? 1f : 0f))) + (float)(r[i][2] * (j == 2 ? 1f : 0f)));
        }
        var bm = new float[16];
        for (int i = 0; i < 3; i++)
        {
            float s0 = i == 0 ? (float)(size[0] * 1f) : 0f, s1 = i == 1 ? (float)(size[1] * 1f) : 0f, s2 = i == 2 ? (float)(size[2] * 1f) : 0f;
            for (int j = 0; j < 3; j++) bm[i * 4 + j] = (float)((float)((float)(s0 * rot[0][j]) + (float)(s1 * rot[1][j])) + (float)(s2 * rot[2][j]));
        }
        bm[12] = (float)(loc[0] + 0f); bm[13] = (float)(loc[1] + 0f); bm[14] = (float)(loc[2] + 0f); bm[15] = 1f;
        return bm;
    }

    // ---- blen_read_geom -------------------------------------------------------------------------------------------------------
    static Mesh ReadGeometry(FbxObject g, Scene r)
    {
        var rec = g.Record;
        var m = new Mesh { Geometry = g };
        var verts = ArrayOf(rec, "Vertices"); var polys = ArrayOf(rec, "PolygonVertexIndex"); var edgesA = ArrayOf(rec, "Edges");
        double[] fv = verts != null ? FbxScene.Reals(verts, "Vertices") : new double[0];
        int[] fp = polys != null ? FbxScene.Integers(polys, "PolygonVertexIndex") : new int[0];
        int[] fe = edgesA != null ? FbxScene.Integers(edgesA, "Edges") : new int[0];
        int nv = fv.Length / 3;
        m.Positions = new float[nv * 3];
        for (int i = 0; i < nv * 3; i++) m.Positions[i] = (float)fv[i];
        int nc = fp.Length;
        var cornerVerts = new int[nc]; var starts = new List<int>();
        if (nc > 0)
        {
            starts.Add(0);
            for (int i = 0; i < nc; i++)
            {
                int v = fp[i];
                if (v < 0) { cornerVerts[i] = ~v; if (i + 1 < nc) starts.Add(i + 1); }
                else cornerVerts[i] = v;
            }
            // a polygon that does not end with a negative index runs to the end of the array (the importer's loop starts come
            // from the negative indices alone: a trailing open polygon is one polygon to the end)
        }
        int nf = starts.Count;
        var offsets = new int[nf + 1];
        for (int f = 0; f < nf; f++) offsets[f] = starts[f];
        offsets[nf] = nc;
        m.CornerVerts = cornerVerts; m.FaceOffsets = offsets;
        // the Edges array: each entry a corner index; the edge runs from that corner's vertex to the next corner of its polygon
        var edges = new List<(int a, int b)>();
        if (nc > 0 && fe.Length > 0)
        {
            var faceOfCorner = new int[nc];
            for (int f = 0; f < nf; f++) for (int c = offsets[f]; c < offsets[f + 1]; c++) faceOfCorner[c] = f;
            foreach (int c in fe)
            {
                if (c < 0 || c >= nc) { r.Notes.Add("edges: an Edges entry out of range in '" + g.Name + "'"); continue; }
                int f = faceOfCorner[c]; int next = c + 1 < offsets[f + 1] ? c + 1 : offsets[f];
                edges.Add((cornerVerts[c], cornerVerts[next]));
            }
        }
        else if (fe.Length > 0) r.Notes.Add("edges without polygons in '" + g.Name + "': ignored");
        // layers
        m.FaceMaterial = ReadMaterialLayer(rec, nf, r, g);
        ReadUvLayers(rec, m, r, g);
        if (rec.All("LayerElementColor").Any()) { m.ColorLayers = rec.All("LayerElementColor").Count(); r.Reasons.Add("colour layers in '" + g.Name + "' (sub-step 6b)"); if (r.Fallback == null) r.Fallback = r.Reasons[r.Reasons.Count - 1]; }
        m.SharpFace = new bool[nf];
        bool[] edgeSharp = null;
        ReadSmoothing(rec, m, edges, ref edgeSharp, r, g);
        float[] cornerNormals = ReadNormals(rec, m, r, g);
        // mesh.validate(): the polygons and edges that stay; the corner data follow the kept faces
        Validate(m, edges, edgeSharp, cornerNormals, out var keptEdges, out var keptEdgeSharp, out cornerNormals);
        // sharp_edge from the smoothing layer (kept edges), by vertex pair
        if (keptEdgeSharp != null)
        {
            var set = new HashSet<long>(VehicleProbe.PairKeyComparer.Instance);
            for (int e = 0; e < keptEdges.Count; e++) if (keptEdgeSharp[e]) set.Add(VehicleProbe.SharpEdgeKey(keptEdges[e].a, keptEdges[e].b));
            m.SharpEdges = set.Count > 0 ? set : null;
        }
        if (cornerNormals != null)
        {
            var sharp = m.SharpEdges ?? new HashSet<long>(VehicleProbe.PairKeyComparer.Instance);
            m.CustomNormal = VehicleProbe.SetCustomNormalsPerCorner(m.Positions, m.CornerVerts, m.FaceOffsets, m.SharpFace, sharp, cornerNormals);
            if (sharp.Count > 0) m.SharpEdges = sharp;
        }
        return m;
    }

    static FbxNode ArrayOf(FbxNode rec, string name) { var n = rec.First(name); return n != null && n.Properties.Count > 0 && n.Properties[0] is Array ? n : null; }

    static (string name, string mapping, string reference) LayerInfo(FbxNode layer)
    {
        string S(string child) { var n = layer.First(child); return n != null && n.Properties.Count > 0 && n.Properties[0] is string s ? s : ""; }
        return (ValidateBlendName(S("Name")), S("MappingInformationType"), S("ReferenceInformationType"));
    }

    /// <summary>blen_read_geom_parse_fbx_data + check_fbx_data_length + the three setters: the layer's values as items of
    /// itemSize (stride == itemSize here), distributed over `count` targets by the mapping and reference; what cannot be set is
    /// left at the default (and said in the notes). Returns per target itemSize values, or null when the layer is not read.</summary>
    static double[] MappedLayer(double[] data, int[] index, string mapping, string reference, int itemSize, int count, string what, Scene r, int[] loopToVert, bool allowVertice, bool allowPolygon, bool allowLoop, bool allowEdge, double[] defaults)
    {
        int items = data.Length / itemSize;
        if (data.Length % itemSize != 0) r.Notes.Add(what + ": not a whole number of items, the partial item skipped");
        var result = new double[count * itemSize];
        if (defaults != null) for (int t = 0; t < count; t++) for (int i = 0; i < itemSize; i++) result[t * itemSize + i] = defaults[i];
        void Direct()
        {
            int n = Math.Min(items, count);
            if (items < count) r.Notes.Add(what + ": not enough data, the rest left at the default");
            else if (items > count) r.Notes.Add(what + ": too much data, the excess skipped");
            Array.Copy(data, 0, result, 0, n * itemSize);
        }
        void Indexed(int[] idx)
        {
            int n = Math.Min(idx.Length, count);
            if (idx.Length < count) r.Notes.Add(what + ": not enough indices, the rest left at the default");
            else if (idx.Length > count) r.Notes.Add(what + ": too many indices, the excess skipped");
            bool invalid = false;
            for (int t = 0; t < n; t++)
            {
                int k = idx[t];
                if (k < -items || k >= items) { invalid = true; continue; }   // np.clip comparison: a negative index within range counts from the end
                if (k < 0) k += items;
                Array.Copy(data, k * itemSize, result, t * itemSize, itemSize);
            }
            if (invalid) r.Notes.Add(what + ": indices out of bounds skipped");
        }
        void AllSame()
        {
            if (items < 1) { r.Notes.Add(what + ": AllSame without data, skipped"); return; }
            for (int t = 0; t < count; t++) Array.Copy(data, 0, result, t * itemSize, itemSize);
        }
        bool ok = false;
        if (mapping == "ByPolygonVertex" && allowLoop)
        {
            if (reference == "IndexToDirect") { if (index == null) Direct(); else Indexed(index); ok = true; }
            else if (reference == "Direct") { Direct(); ok = true; }
        }
        else if (mapping == "ByVertice" && allowLoop && loopToVert != null)
        {
            if (reference == "Direct") { Indexed(loopToVert); ok = true; }
        }
        else if (mapping == "ByVertice" && allowVertice)
        {
            if (reference == "IndexToDirect") { if (index == null) Direct(); else Indexed(index); ok = true; }
            else if (reference == "Direct") { Direct(); ok = true; }
        }
        else if (mapping == "ByPolygon" && allowPolygon)
        {
            if (reference == "IndexToDirect") { if (index == null) Direct(); else Indexed(index); ok = true; }
            else if (reference == "Direct") { Direct(); ok = true; }
        }
        else if (mapping == "ByEdge" && allowEdge)
        {
            if (reference == "Direct") { Direct(); ok = true; }
        }
        else if (mapping == "AllSame")
        {
            if (reference == "IndexToDirect") { AllSame(); ok = true; }
        }
        if (!ok) { r.Notes.Add(what + ": mapping '" + mapping + "' / reference '" + reference + "' unsupported, layer skipped"); return null; }
        return result;
    }

    static int[] ReadMaterialLayer(FbxNode rec, int nf, Scene r, FbxObject g)
    {
        var layer = rec.First("LayerElementMaterial");
        if (layer == null) return null;
        var (name, mapping, reference) = LayerInfo(layer);
        var dataN = ArrayOf(layer, "Materials");
        if (dataN == null) return null;
        var data = FbxScene.Reals(dataN, "Materials");
        var res = MappedLayer(data, null, mapping, reference, 1, nf, "Materials of '" + g.Name + "'", r, null, false, true, false, false, new double[] { 0 });
        if (res == null) return null;
        var fm = new int[nf];
        for (int f = 0; f < nf; f++) fm[f] = (int)res[f];
        return fm;
    }

    static void ReadUvLayers(FbxNode rec, Mesh m, Scene r, FbxObject g)
    {
        int nc = m.CornerVerts.Length;
        foreach (var layer in rec.All("LayerElementUV"))
        {
            var (name, mapping, reference) = LayerInfo(layer);
            if (m.Uv.Count >= 8) { r.Notes.Add("more than eight UV layers in '" + g.Name + "': the rest are not added"); break; }
            var dataN = ArrayOf(layer, "UV"); var indexN = ArrayOf(layer, "UVIndex");
            var uv = new float[2 * nc];
            if (dataN == null) { r.Notes.Add("UV layer '" + name + "' missing data"); m.Uv.Add((name, uv)); continue; }
            var data = FbxScene.Reals(dataN, "UV");
            // float32: the layer's doubles become the mesh's float UVs (np.single) at the set
            var res = MappedLayer(data, indexN != null ? FbxScene.Integers(indexN, "UVIndex") : null, mapping, reference, 2, nc, "UV '" + name + "' of '" + g.Name + "'", r, m.CornerVerts, false, false, true, false, new double[] { 0, 0 });
            if (res != null) for (int i = 0; i < 2 * nc; i++) uv[i] = (float)res[i];
            m.Uv.Add((name, uv));
        }
    }

    static void ReadSmoothing(FbxNode rec, Mesh m, List<(int a, int b)> edges, ref bool[] edgeSharp, Scene r, FbxObject g)
    {
        var layer = rec.First("LayerElementSmoothing");
        if (layer == null) return;
        var (name, mapping, reference) = LayerInfo(layer);
        var dataN = ArrayOf(layer, "Smoothing");
        if (dataN == null) return;
        var data = FbxScene.Reals(dataN, "Smoothing");
        if (mapping == "ByEdge")
        {
            if (edges.Count == 0) { r.Notes.Add("sharp edges of '" + g.Name + "' skipped: no edges"); return; }
            var res = MappedLayer(data, null, mapping, reference, 1, edges.Count, "Smoothing of '" + g.Name + "'", r, null, false, false, false, true, new double[] { 1 });   // xform = logical_not: the default of a missing entry is "smooth"? sharp_edge's default is false = NOT sharp = smooth 1
            if (res == null) return;
            edgeSharp = new bool[edges.Count];
            for (int e = 0; e < edges.Count; e++) edgeSharp[e] = res[e] == 0;   // 0 (False) is sharp in FBX
        }
        else if (mapping == "ByPolygon")
        {
            var res = MappedLayer(data, null, mapping, reference, 1, m.FaceCount, "Smoothing of '" + g.Name + "'", r, null, false, true, false, false, new double[] { 1 });
            if (res == null) return;
            for (int f = 0; f < m.FaceCount; f++) m.SharpFace[f] = res[f] == 0;
        }
        else r.Notes.Add("smoothing mapping '" + mapping + "' unsupported in '" + g.Name + "'");
    }

    /// <summary>blen_read_geom_layer_normal: the normals per corner, through the three tries (corners, polygons, vertices);
    /// null when there is no layer or none applies. Values as doubles (the importer stores them to float at the set).</summary>
    static float[] ReadNormals(FbxNode rec, Mesh m, Scene r, FbxObject g)
    {
        var layer = rec.First("LayerElementNormal");
        if (layer == null) return null;
        var (name, mapping, reference) = LayerInfo(layer);
        var dataN = ArrayOf(layer, "Normals"); var indexN = ArrayOf(layer, "NormalsIndex");
        if (dataN == null) { r.Notes.Add("normals of '" + g.Name + "' missing data"); return null; }
        var data = FbxScene.Reals(dataN, "Normals");
        int[] index = indexN != null ? FbxScene.Integers(indexN, "NormalsIndex") : null;
        int nc = m.CornerVerts.Length, nf = m.FaceCount, nv = m.Positions.Length / 3;
        string what = "normals of '" + g.Name + "'";
        var quiet = new Scene();   // the tries are quiet: only the last failure is reported
        double[] res = MappedLayer(data, index, mapping, reference, 3, nc, what, quiet, m.CornerVerts, false, false, true, false, new double[] { 0, 0, 0 });
        float[] result = new float[3 * nc];
        if (res != null) { for (int i = 0; i < 3 * nc; i++) result[i] = (float)res[i]; return result; }
        res = MappedLayer(data, index, mapping, reference, 3, nf, what, quiet, null, false, true, false, false, new double[] { 0, 0, 0 });
        if (res != null)
        {
            for (int f = 0; f < nf; f++) for (int c = m.FaceOffsets[f]; c < m.FaceOffsets[f + 1]; c++) for (int i = 0; i < 3; i++) result[3 * c + i] = (float)res[3 * f + i];
            return result;
        }
        res = MappedLayer(data, index, mapping, reference, 3, nv, what, quiet, null, true, false, false, false, new double[] { 0, 0, 0 });
        if (res != null)
        {
            for (int c = 0; c < nc; c++) for (int i = 0; i < 3; i++) result[3 * c + i] = (float)res[3 * m.CornerVerts[c] + i];
            return result;
        }
        r.Notes.Add(what + ": mapping '" + mapping + "' / reference '" + reference + "' unsupported");
        return null;
    }

    /// <summary>mesh.validate(): a polygon under three corners, with a vertex out of range or repeated, or equal to an earlier
    /// kept polygon as a vertex ring (either direction) is removed with its corners; an edge with equal ends, out of range or
    /// repeated is removed (the first kept). The material index, sharp flag, UVs and normals of a removed polygon go with it.</summary>
    static void Validate(Mesh m, List<(int a, int b)> edges, bool[] edgeSharp, float[] cornerNormals, out List<(int a, int b)> keptEdges, out bool[] keptEdgeSharp, out float[] keptNormals)
    {
        int nv = m.Positions.Length / 3, nf = m.FaceCount;
        var keep = new bool[nf];
        var seenRings = new HashSet<string>();
        var seenInFace = new HashSet<int>();
        for (int f = 0; f < nf; f++)
        {
            int begin = m.FaceOffsets[f], end = m.FaceOffsets[f + 1], size = end - begin;
            if (size < 3) continue;
            bool bad = false; seenInFace.Clear();
            for (int c = begin; c < end && !bad; c++) { int v = m.CornerVerts[c]; if (v < 0 || v >= nv || !seenInFace.Add(v)) bad = true; }
            if (bad) continue;
            // the ring rotated to its smallest vertex, running towards the smaller neighbour
            int best = begin; for (int c = begin + 1; c < end; c++) if (m.CornerVerts[c] < m.CornerVerts[best]) best = c;
            int prev = best == begin ? end - 1 : best - 1, next = best + 1 == end ? begin : best + 1;
            bool forward = m.CornerVerts[next] <= m.CornerVerts[prev];
            var sb = new StringBuilder();
            for (int k = 0; k < size; k++)
            {
                int c = forward ? begin + (best - begin + k) % size : begin + ((best - begin - k) % size + size) % size;
                sb.Append(m.CornerVerts[c]).Append(',');
            }
            if (!seenRings.Add(sb.ToString())) continue;
            keep[f] = true;
        }
        // compact
        var newOffsets = new List<int> { 0 }; var newCorners = new List<int>(); var newMat = m.FaceMaterial != null ? new List<int>() : null; var newSharp = new List<bool>();
        var newUv = m.Uv.Select(u => new List<float>()).ToList(); var newNormals = cornerNormals != null ? new List<float>() : null;
        for (int f = 0; f < nf; f++)
        {
            if (!keep[f]) continue;
            for (int c = m.FaceOffsets[f]; c < m.FaceOffsets[f + 1]; c++)
            {
                newCorners.Add(m.CornerVerts[c]);
                for (int u = 0; u < m.Uv.Count; u++) { newUv[u].Add(m.Uv[u].uv[2 * c]); newUv[u].Add(m.Uv[u].uv[2 * c + 1]); }
                if (newNormals != null) { newNormals.Add(cornerNormals[3 * c]); newNormals.Add(cornerNormals[3 * c + 1]); newNormals.Add(cornerNormals[3 * c + 2]); }
            }
            newOffsets.Add(newCorners.Count);
            newMat?.Add(m.FaceMaterial[f]); newSharp.Add(m.SharpFace[f]);
        }
        m.CornerVerts = newCorners.ToArray(); m.FaceOffsets = newOffsets.ToArray(); m.FaceMaterial = newMat?.ToArray(); m.SharpFace = newSharp.ToArray();
        for (int u = 0; u < m.Uv.Count; u++) m.Uv[u] = (m.Uv[u].name, newUv[u].ToArray());
        keptNormals = newNormals?.ToArray();
        // edges: equal ends, out of range, repeated (either direction)
        keptEdges = new List<(int, int)>(); var keptSharp = edgeSharp != null ? new List<bool>() : null;
        var seenEdges = new HashSet<long>(VehicleProbe.PairKeyComparer.Instance);
        for (int e = 0; e < edges.Count; e++)
        {
            var (a, b) = edges[e];
            if (a == b || a < 0 || b < 0 || a >= nv || b >= nv) continue;
            if (!seenEdges.Add(VehicleProbe.SharpEdgeKey(a, b))) continue;
            keptEdges.Add((a, b)); keptSharp?.Add(edgeSharp[e]);
        }
        keptEdgeSharp = keptSharp?.ToArray();
    }
}
