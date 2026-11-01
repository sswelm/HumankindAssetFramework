using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// THE FBX OBJECT GRAPH (2026-11-01, step 2 of the native FBX reader): what the record tree (FbxDocument) MEANS -
// objects and their properties with the Definitions' templates behind them, connections, the node tree with its
// transforms, meshes with their layer elements resolved per corner, materials and their textures. Read the way ufbx
// (the reference reader, github.com/ufbx/ufbx, 0.23.1) reads it, formula for formula: ufbxi_get_transform with its
// pivots, offsets, pre- and post-rotation and rotation order; ufbxi_update_node with the inherit modes; the vertex
// element mapping/reference modes of ufbxi_read_vertex_element. The drill (tools/fbx_drill.sh, --scene) holds this
// to ufbx's own dump of every node matrix, every vertex stream and every material, bit for bit.
//
// NOT YET: skins, animation, the HafModel mapping (steps 3-5). NOT MODELLED, said: ufbx's helper nodes (geometry
// transform helpers, scale helpers - off by default), its space conversion (off: the file's own frame), blend
// shapes, caches, lights and cameras beyond their node.
public sealed class FbxProp
{
    public string Name = "", Type = "", SubType = "", Flags = "";
    public long ValueInt; public bool HasInt;
    public double[] Reals = new double[4]; public int RealCount;   // value_real_arr: up to four consecutive numbers
    public string ValueStr; public bool HasStr;
    public double Real => Reals[0];
    public bool HasValue => HasInt || RealCount > 0 || HasStr;
}

public sealed class FbxProps
{
    readonly Dictionary<string, FbxProp> byName = new Dictionary<string, FbxProp>(StringComparer.Ordinal);
    public FbxProps Defaults;   // the Definitions' template for the object's class (ufbx_props.defaults)
    public int Count => byName.Count;
    public IEnumerable<FbxProp> All => byName.Values;

    /// <summary>The property by name, or the template's when undeclared. An explicitly empty P has zero values and
    /// still overrides the template; ufbx's NO_VALUE flag is for synthetic animation properties, not an empty stored P.</summary>
    public FbxProp Find(string name)
    {
        for (var p = this; p != null; p = p.Defaults)
            if (p.byName.TryGetValue(name, out var prop)) return prop;
        return null;
    }
    public double[] Vec3(string name, double dx, double dy, double dz) { var p = Find(name); return p != null ? new[] { p.Reals[0], p.Reals[1], p.Reals[2] } : new[] { dx, dy, dz }; }
    public double Real(string name, double def) { var p = Find(name); return p != null ? p.Reals[0] : def; }
    public long Int(string name, long def) { var p = Find(name); return p != null ? p.ValueInt : def; }
    public long Enum(string name, long def, long max) { var p = Find(name); if (p == null) return def; return p.ValueInt >= 0 && p.ValueInt <= max ? p.ValueInt : def; }
    public string Str(string name, string def) { var p = Find(name); return p != null && p.HasStr ? p.ValueStr : def; }

    /// <summary>A Properties70 block as ufbxi_read_property reads each P: name, type, subtype, flags, then an int from the
    /// first number, up to four reals from consecutive numbers, and a string (skipping one non-string value first). A name
    /// given twice keeps the LAST (ufbxi_deduplicate_properties).</summary>
    public static FbxProps Read(FbxNode owner)
    {
        var props = new FbxProps();
        var block = owner?.First("Properties70");
        if (block == null) return props;
        foreach (var p in block.Children)
        {
            if (p.Name != "P" || p.Properties.Count < 2) continue;
            var prop = new FbxProp { Name = p.Properties[0] as string ?? "", Type = p.Properties[1] as string ?? "" };
            prop.SubType = p.Properties.Count > 2 ? p.Properties[2] as string ?? "" : "";
            prop.Flags = p.Properties.Count > 3 ? p.Properties[3] as string ?? "" : "";
            int ix = 4;
            if (ix < p.Properties.Count && IsNumber(p.Properties[ix])) { prop.ValueInt = AsInt(p.Properties[ix]); prop.HasInt = true; }
            for (int r = 0; r < 4 && ix + r < p.Properties.Count && IsNumber(p.Properties[ix + r]); r++) { prop.Reals[r] = AsReal(p.Properties[ix + r]); prop.RealCount = r + 1; }
            if (ix < p.Properties.Count && !(p.Properties[ix] is string)) ix++;
            if (ix < p.Properties.Count && p.Properties[ix] is string s) { prop.ValueStr = s; prop.HasStr = true; }
            props.byName[prop.Name] = prop;
        }
        return props;
    }

    public static bool IsNumber(object v) => v is short || v is int || v is long || v is float || v is double || v is byte || v is sbyte || v is bool;
    /// <summary>ufbx's `vals[i].f`: an integer exactly, a float widened, a bool/char as its byte.</summary>
    public static double AsReal(object v)
    {
        switch (v) { case short y: return y; case int i: return i; case long l: return l; case float f: return f; case double d: return d; case byte c: return c; case sbyte z: return z; case bool b: return b ? 1 : 0; default: return 0; }
    }
    /// <summary>ufbx's `vals[i].i`: an integer exactly, a float truncated toward zero and saturated (ufbxi_f64_to_i64).</summary>
    public static long AsInt(object v)
    {
        switch (v)
        {
            case short y: return y; case int i: return i; case long l: return l; case byte c: return c; case sbyte z: return z; case bool b: return b ? 1 : 0;
            case float f: return F64ToI64(f); case double d: return F64ToI64(d); default: return 0;
        }
    }
    static long F64ToI64(double value) => Math.Abs(value) <= (double)long.MaxValue ? (long)value : (value >= 0 ? long.MaxValue : long.MinValue);
}

public sealed class FbxObject
{
    public long Id; public string Name = "", Class = "", SubType = "";
    public int ElementId;              // ufbx's element_id: the order of creation (the root node is 0, then the Objects in file order)
    public FbxNode Record;
    public FbxProps Props;
    public readonly List<FbxConnection> Sources = new List<FbxConnection>();   // connections INTO this object (this is the dst), file order
    public readonly List<FbxConnection> Targets = new List<FbxConnection>();   // connections OUT of this object (this is the src), file order
    public bool IsTexture => Class == "Texture" || Class == "LayeredTexture";
    public override string ToString() => $"{Class}/{SubType} '{Name}' #{Id}";
}

public sealed class FbxConnection
{
    public string Kind = "";   // OO, OP, PO, PP
    public FbxObject Src, Dst; public long SrcId, DstId;
    public string SrcProp = "", DstProp = "";
}

/// <summary>ufbx_transform: translation, rotation (x y z w), scale; and the composition helpers of ufbx, in its order of operations.</summary>
public struct FbxTransform
{
    public double tx, ty, tz, qx, qy, qz, qw, sx, sy, sz;
    public static FbxTransform Identity => new FbxTransform { qw = 1, sx = 1, sy = 1, sz = 1 };

    public void MulScale(double[] v) { tx *= v[0]; ty *= v[1]; tz *= v[2]; sx *= v[0]; sy *= v[1]; sz *= v[2]; }
    public void AddTranslate(double[] v) { tx += v[0]; ty += v[1]; tz += v[2]; }
    public void SubTranslate(double[] v) { tx -= v[0]; ty -= v[1]; tz -= v[2]; }
    public void MulRotate(double[] euler, int order)
    {
        if (euler[0] == 0.0 && euler[1] == 0.0 && euler[2] == 0.0) return;
        var q = FbxMath.EulerToQuat(euler, order);
        ApplyQuat(q);
    }
    public void MulInvRotate(double[] euler, int order)
    {
        if (euler[0] == 0.0 && euler[1] == 0.0 && euler[2] == 0.0) return;
        var q = FbxMath.EulerToQuat(euler, order);
        q[0] = -q[0]; q[1] = -q[1]; q[2] = -q[2];
        ApplyQuat(q);
    }
    void ApplyQuat(double[] q)
    {
        if (qw != 1.0) { var r = FbxMath.MulQuat(q, new[] { qx, qy, qz, qw }); qx = r[0]; qy = r[1]; qz = r[2]; qw = r[3]; }
        else { qx = q[0]; qy = q[1]; qz = q[2]; qw = q[3]; }
        if (!(tx == 0.0 && ty == 0.0 && tz == 0.0)) { var t = FbxMath.RotateVec3(q, tx, ty, tz); tx = t[0]; ty = t[1]; tz = t[2]; }
    }
}

/// <summary>ufbx's math, formula for formula (ufbx_euler_to_quat, ufbxi_mul_quat, ufbx_quat_rotate_vec3, ufbx_transform_to_matrix, ufbx_matrix_mul). Doubles throughout; a matrix is ufbx's 12 values m00 m10 m20 m01 m11 m21 m02 m12 m22 m03 m13 m23.</summary>
public static class FbxMath
{
    const double DegToRad = 3.141592653589793238462643383279502884 / 180.0;

    // ufbx_cos / ufbx_sin are the C library's double cos and sin; the oracle (ufbxdump, MinGW-w64 ucrt) and this
    // reader must call the SAME one for the quaternions to agree to the bit (the lesson of BlenderTrig, which does it
    // for the float forms). ucrtbase is P/Invoked when it answers; elsewhere Math.Cos/Sin, said by Exact.
    [System.Runtime.InteropServices.DllImport("ucrtbase.dll", EntryPoint = "cos", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_cos(double x);
    [System.Runtime.InteropServices.DllImport("ucrtbase.dll", EntryPoint = "sin", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
    static extern double ucrt_sin(double x);
    public static readonly bool Exact = Probe();
    static bool Probe()
    {
        if (IntPtr.Size != 8) return false;
        try { return ucrt_sin(0.0) == 0.0 && ucrt_cos(0.0) == 1.0; }
        catch (Exception) { return false; }
    }
    public static double Cos(double x) => Exact ? ucrt_cos(x) : Math.Cos(x);
    public static double Sin(double x) => Exact ? ucrt_sin(x) : Math.Sin(x);

    public static double[] EulerToQuat(double[] v, int order)
    {
        double vx = v[0] * (DegToRad * 0.5), vy = v[1] * (DegToRad * 0.5), vz = v[2] * (DegToRad * 0.5);
        double cx = Cos(vx), sx = Sin(vx);
        double cy = Cos(vy), sy = Sin(vy);
        double cz = Cos(vz), sz = Sin(vz);
        var q = new double[4];
        switch (order)
        {
            case 0: q[0] = -cx*sy*sz + cy*cz*sx; q[1] = cx*cz*sy + cy*sx*sz; q[2] = cx*cy*sz - cz*sx*sy; q[3] = cx*cy*cz + sx*sy*sz; break;   // XYZ
            case 1: q[0] = cx*sy*sz + cy*cz*sx;  q[1] = cx*cz*sy + cy*sx*sz; q[2] = cx*cy*sz - cz*sx*sy; q[3] = cx*cy*cz - sx*sy*sz; break;   // XZY
            case 2: q[0] = -cx*sy*sz + cy*cz*sx; q[1] = cx*cz*sy - cy*sx*sz; q[2] = cx*cy*sz + cz*sx*sy; q[3] = cx*cy*cz + sx*sy*sz; break;   // YZX
            case 3: q[0] = -cx*sy*sz + cy*cz*sx; q[1] = cx*cz*sy + cy*sx*sz; q[2] = cx*cy*sz + cz*sx*sy; q[3] = cx*cy*cz - sx*sy*sz; break;   // YXZ
            case 4: q[0] = cx*sy*sz + cy*cz*sx;  q[1] = cx*cz*sy - cy*sx*sz; q[2] = cx*cy*sz - cz*sx*sy; q[3] = cx*cy*cz + sx*sy*sz; break;   // ZXY
            case 5: q[0] = cx*sy*sz + cy*cz*sx;  q[1] = cx*cz*sy - cy*sx*sz; q[2] = cx*cy*sz + cz*sx*sy; q[3] = cx*cy*cz - sx*sy*sz; break;   // ZYX
            default: q[0] = 0; q[1] = 0; q[2] = 0; q[3] = 1; break;   // SPHERIC: identity, as ufbx
        }
        return q;
    }
    public static double[] MulQuat(double[] a, double[] b) => new[] {
        a[3]*b[0] + a[0]*b[3] + a[1]*b[2] - a[2]*b[1],
        a[3]*b[1] - a[0]*b[2] + a[1]*b[3] + a[2]*b[0],
        a[3]*b[2] + a[0]*b[1] - a[1]*b[0] + a[2]*b[3],
        a[3]*b[3] - a[0]*b[0] - a[1]*b[1] - a[2]*b[2] };
    public static double[] RotateVec3(double[] q, double vx, double vy, double vz)
    {
        double xy = q[0]*vy - q[1]*vx, xz = q[0]*vz - q[2]*vx, yz = q[1]*vz - q[2]*vy;
        return new[] {
            2.0 * (+ q[3]*yz + q[1]*xy + q[2]*xz) + vx,
            2.0 * (- q[0]*xy - q[3]*xz + q[2]*yz) + vy,
            2.0 * (- q[0]*xz - q[1]*yz + q[3]*xy) + vz };
    }
    public static double[] ToMatrix(FbxTransform t)
    {
        double sx = 2.0 * t.sx, sy = 2.0 * t.sy, sz = 2.0 * t.sz;
        double xx = t.qx*t.qx, xy = t.qx*t.qy, xz = t.qx*t.qz, xw = t.qx*t.qw;
        double yy = t.qy*t.qy, yz = t.qy*t.qz, yw = t.qy*t.qw;
        double zz = t.qz*t.qz, zw = t.qz*t.qw;
        return new[] {
            sx * (- yy - zz + 0.5), sx * (+ xy + zw), sx * (- yw + xz),
            sy * (- zw + xy), sy * (- xx - zz + 0.5), sy * (+ xw + yz),
            sz * (+ xz + yw), sz * (- xw + yz), sz * (- xx - yy + 0.5),
            t.tx, t.ty, t.tz };
    }
    public static readonly double[] IdentityMatrix = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
    // m[0..2] = column 0 (m00 m10 m20), m[3..5] column 1, m[6..8] column 2, m[9..11] translation
    public static double[] Mul(double[] a, double[] b)
    {
        var d = new double[12];
        d[9]  = a[0]*b[9] + a[3]*b[10] + a[6]*b[11] + a[9];
        d[10] = a[1]*b[9] + a[4]*b[10] + a[7]*b[11] + a[10];
        d[11] = a[2]*b[9] + a[5]*b[10] + a[8]*b[11] + a[11];
        d[0] = a[0]*b[0] + a[3]*b[1] + a[6]*b[2];
        d[1] = a[1]*b[0] + a[4]*b[1] + a[7]*b[2];
        d[2] = a[2]*b[0] + a[5]*b[1] + a[8]*b[2];
        d[3] = a[0]*b[3] + a[3]*b[4] + a[6]*b[5];
        d[4] = a[1]*b[3] + a[4]*b[4] + a[7]*b[5];
        d[5] = a[2]*b[3] + a[5]*b[4] + a[8]*b[5];
        d[6] = a[0]*b[6] + a[3]*b[7] + a[6]*b[8];
        d[7] = a[1]*b[6] + a[4]*b[7] + a[7]*b[8];
        d[8] = a[2]*b[6] + a[5]*b[7] + a[8]*b[8];
        return d;
    }
}

public sealed class FbxSceneNode
{
    public FbxObject Object;           // null for the synthetic root
    public string Name = "";
    public FbxSceneNode Parent; public readonly List<FbxSceneNode> Children = new List<FbxSceneNode>();
    public int Depth, ElementId, Index = -1;
    public bool IsRoot;
    public string AttribType = "none";  // mesh, bone, empty, light, camera, none, other
    public FbxMeshData Mesh;
    public readonly List<FbxObject> Materials = new List<FbxObject>();   // the Model's Material connections, file order
    public int RotationOrder; public bool UseRotationSpace, Visible = true;
    public int InheritMode;            // 0 normal, 1 ignore parent scale, 2 componentwise (ufbx_inherit_mode)
    public FbxSceneNode InheritScaleNode;
    public FbxTransform LocalTransform, GeometryTransform;
    public double[] InheritScale = { 1, 1, 1 };
    public double[] NodeToParent, NodeToWorld, UnscaledNodeToWorld, GeometryToNode;
    public bool HasGeometryTransform;
}

/// <summary>A per-corner vertex stream as ufbx holds it: Values (Components per element) and one index per mesh index.</summary>
public sealed class FbxVertexStream
{
    public bool Exists; public int Components;
    public double[] Values = new double[0];
    public int[] Indices = new int[0];   // one per mesh index; -1 = UFBX_NO_INDEX (reads as zeros)
    public bool UniquePerVertex;
    public string Name = ""; public uint LayerIndex;
    public int ValueCount => Components == 0 ? 0 : Values.Length / Components;
    public double Get(int index, int component) { int ix = Indices[index]; return ix < 0 ? 0.0 : Values[ix * Components + component]; }
}

public sealed class FbxMeshData
{
    public FbxObject Object; public int Index;
    public string Name = "";
    public int NumVertices, NumIndices, NumFaces, NumTriangles, MaxFaceTriangles;
    public double[] Vertices = new double[0];
    public int[] VertexIndices = new int[0];          // the polygon vertex indices, un-negated
    public int[] FaceBegin = new int[0], FaceSize = new int[0];
    public FbxVertexStream Position = new FbxVertexStream(), Normal = new FbxVertexStream(), Tangent = new FbxVertexStream(), Bitangent = new FbxVertexStream();
    public readonly List<FbxVertexStream> UvSets = new List<FbxVertexStream>(), ColorSets = new List<FbxVertexStream>();
    public int[] FaceMaterial = new int[0];
    public readonly List<FbxObject> Materials = new List<FbxObject>();
    public readonly List<FbxSceneNode> Instances = new List<FbxSceneNode>();
}

public sealed class FbxMaterialData
{
    public FbxObject Object; public int Index;
    public string Name = "", ShadingModel = "";
    public int ShaderType;   // ufbx_shader_type: 0 unknown, 1 FBX lambert, 2 FBX phong, 3 OSL, 4 Arnold, 5-7 3ds Max physical/PBR, 8 glTF, 9 OpenPBR, 10 ShaderFX
    public double[] Diffuse = { 0, 0, 0 };
    public readonly List<(string prop, FbxObject texture, string relativeFilename)> Textures = new List<(string, FbxObject, string)>();
}

public sealed class FbxScene
{
    public FbxDocument Document;
    public int Version;
    public readonly List<FbxObject> Objects = new List<FbxObject>();
    public readonly Dictionary<long, FbxObject> ById = new Dictionary<long, FbxObject>();
    public readonly List<FbxConnection> Connections = new List<FbxConnection>();
    public readonly Dictionary<string, List<(string subType, FbxProps props)>> Templates = new Dictionary<string, List<(string, FbxProps)>>(StringComparer.Ordinal);
    public readonly List<FbxSceneNode> Nodes = new List<FbxSceneNode>();   // ufbx's order: by depth, then parent's element id, then element id; the root first
    public readonly List<FbxMeshData> Meshes = new List<FbxMeshData>();
    public readonly List<FbxMaterialData> Materials = new List<FbxMaterialData>();
    public FbxProps GlobalSettings = new FbxProps();
    public int UpAxis = 6, FrontAxis = 6, RightAxis = 6;   // ufbx_coordinate_axis: +X 0, -X 1, +Y 2, -Y 3, +Z 4, -Z 5, unknown 6
    public double UnitMeters = 1, FramesPerSecond = 24;
    public readonly List<string> Warnings = new List<string>();

    public static FbxScene Read(string path) { var s = Read(FbxDocument.Read(path)); return s; }

    public static FbxScene Read(FbxDocument doc)
    {
        var s = new FbxScene { Document = doc, Version = doc.Version };
        s.ReadTemplates();
        s.ReadObjects();
        s.ReadConnections();
        s.ReadSettings();
        s.BuildNodes();
        s.BuildMeshes();
        s.BuildMaterials();
        return s;
    }

    // ---- Definitions: the property templates behind every object's own properties -----------------------------------
    void ReadTemplates()
    {
        var defs = Document.First("Definitions");
        if (defs == null) return;
        foreach (var ot in defs.All("ObjectType"))
        {
            string type = ot.Properties.Count > 0 ? ot.Properties[0] as string ?? "" : "";
            var tmpl = ot.First("PropertyTemplate");
            if (tmpl == null) continue;
            string sub = tmpl.Properties.Count > 0 ? tmpl.Properties[0] as string ?? "" : "";
            if (sub.Length > 3 && sub.StartsWith("Fbx", StringComparison.Ordinal)) { sub = sub.Substring(3); if (sub == "LODGroup") sub = "LodGroup"; }
            var props = FbxProps.Read(tmpl);
            if (!Templates.TryGetValue(type, out var list)) Templates[type] = list = new List<(string, FbxProps)>();
            list.Add((sub, props));
        }
    }

    FbxProps FindTemplate(string type, string subType)
    {
        if (!Templates.TryGetValue(type, out var list)) return null;
        foreach (var (sub, props) in list)   // ufbxi_find_template: the FIRST template of the type decides, and only the sub-type-free classes match any sub-type
        {
            if (type != "Material" && type != "Model" && type != "AnimationStack" && type != "AnimationLayer" && sub != subType) return null;
            return props.Count > 0 ? props : null;
        }
        return null;
    }

    // ---- Objects and Connections ---------------------------------------------------------------------------------------
    void ReadObjects()
    {
        var objs = Document.First("Objects");
        if (objs == null) return;
        int elementId = 1;   // 0 is the synthetic root node (ufbx creates it before reading the Objects)
        foreach (var rec in objs.Children)
        {
            if (rec.Properties.Count < 3 || !FbxProps.IsNumber(rec.Properties[0])) continue;
            long id = FbxProps.AsInt(rec.Properties[0]);
            string typeAndName = rec.Properties[1] as string ?? "";
            string name = typeAndName, cls = "";
            int sep = typeAndName.IndexOf("\0\u0001", StringComparison.Ordinal);
            if (sep >= 0) { name = typeAndName.Substring(0, sep); cls = typeAndName.Substring(sep + 2); }
            var o = new FbxObject { Id = id, Name = name, Class = rec.Name, SubType = rec.Properties[2] as string ?? "", Record = rec, ElementId = elementId++ };
            o.Props = FbxProps.Read(rec);
            o.Props.Defaults = FindTemplate(o.Class, o.SubType);
            Objects.Add(o);
            if (!ById.ContainsKey(id)) ById[id] = o;
        }
    }

    void ReadConnections()
    {
        var conns = Document.First("Connections");
        if (conns == null) return;
        foreach (var c in conns.All("C"))
        {
            if (c.Properties.Count < 3) continue;
            if (!FbxProps.IsNumber(c.Properties[1]) || !FbxProps.IsNumber(c.Properties[2]))
                throw new InvalidDataException("FBX connection requires numeric source and destination identifiers");
            var conn = new FbxConnection { Kind = c.Properties[0] as string ?? "", SrcId = FbxProps.AsInt(c.Properties[1]), DstId = FbxProps.AsInt(c.Properties[2]) };
            if (conn.Kind == "OP" && c.Properties.Count > 3) conn.DstProp = c.Properties[3] as string ?? "";
            if (conn.Kind == "PO" && c.Properties.Count > 3) conn.SrcProp = c.Properties[3] as string ?? "";
            if (conn.Kind == "PP" && c.Properties.Count > 4) { conn.SrcProp = c.Properties[3] as string ?? ""; conn.DstProp = c.Properties[4] as string ?? ""; }
            ById.TryGetValue(conn.SrcId, out conn.Src); ById.TryGetValue(conn.DstId, out conn.Dst);
            Connections.Add(conn);
            conn.Src?.Targets.Add(conn);
            conn.Dst?.Sources.Add(conn);
        }
    }

    /// <summary>ufbxi_update_scene_settings: the axes (sign default positive), the unit as UnitScaleFactor x 0.01 snapped to a power of ten when within 2^-20 of one, the frame rate from the TimeMode table (ufbx's float literals, widened) or CustomFrameRate.</summary>
    void ReadSettings()
    {
        GlobalSettings = FbxProps.Read(Document.First("GlobalSettings"));
        UpAxis = Axis(GlobalSettings.Int("UpAxis", 3), GlobalSettings.Int("UpAxisSign", 2));
        FrontAxis = Axis(GlobalSettings.Int("FrontAxis", 3), GlobalSettings.Int("FrontAxisSign", 2));
        RightAxis = Axis(GlobalSettings.Int("CoordAxis", 3), GlobalSettings.Int("CoordAxisSign", 2));
        UnitMeters = RoundIfNear(GlobalSettings.Real("UnitScaleFactor", 1.0) * 0.01);
        FramesPerSecond = GlobalSettings.Real("CustomFrameRate", 24.0);
        long timeMode = GlobalSettings.Enum("TimeMode", 11, 17);
        if (timeMode != 14) FramesPerSecond = TimeModeFps[timeMode];
    }
    static int Axis(long axis, long sign) { if (axis < 0 || axis > 2) return 6; return (int)axis * 2 + (sign > 0 ? 0 : 1); }
    static readonly double[] TimeModeFps = { 30, 120, 100, 60, 50, 48, 30, 30, (double)29.97f, (double)29.97f, 25, 24, 1000, (double)23.976f, 24, 96, 72, (double)59.94f };
    static readonly double[] Pow10Targets = { 0.0, 1e-8, 1e-7, 1e-6, 1e-5, 1e-4, 1e-3, 1e-2, 1e-1, 1e+0, 1e+1, 1e+2, 1e+3, 1e+4, 1e+5, 1e+6, 1e+7, 1e+8, 1e+9 };
    static double RoundIfNear(double value)
    {
        foreach (double target in Pow10Targets)
        {
            double error = target * 9.5367431640625e-7;
            if (error < 0.0) error = -error;
            if (error < 7.52316384526264005e-37) error = 7.52316384526264005e-37;
            if (value >= target - error && value <= target + error) return target;
        }
        return value;
    }

    // ---- Nodes: the Model objects under the synthetic root, sorted and composed as ufbx does ---------------------------
    void BuildNodes()
    {
        var root = new FbxSceneNode { IsRoot = true, Name = "", Depth = 0, ElementId = 0 };
        var byObject = new Dictionary<FbxObject, FbxSceneNode>();
        var all = new List<FbxSceneNode> { root };
        foreach (var o in Objects)
        {
            if (o.Class != "Model") continue;
            var n = new FbxSceneNode { Object = o, Name = o.Name, ElementId = o.ElementId };
            byObject[o] = n; all.Add(n);
        }
        // ufbx hooks children while iterating parent nodes in element order. The last parent in that order wins,
        // regardless of connection order; a root connection (element 0) cannot override an explicit Model parent.
        foreach (var n in all)
        {
            if (n.IsRoot) continue;
            n.Parent = root;
            foreach (var c in n.Object.Targets)
            {
                if (c.Kind != "OO") continue;
                if (c.DstId == 0) continue;
                if (c.Dst != null && c.Dst.Class == "Model" && byObject.TryGetValue(c.Dst, out var p) && p.ElementId > n.Parent.ElementId) n.Parent = p;
            }
        }
        foreach (var n in all) if (n.Parent != null) n.Parent.Children.Add(n);
        // Cache completed parent chains: linear work even for reverse-ordered deep hierarchies, and a cycle is a
        // named refusal instead of an infinite loop. 1 is the current path, 2 is a completed chain ending at root.
        var state = new Dictionary<FbxSceneNode, byte> { [root] = 2 };
        var path = new List<FbxSceneNode>();
        foreach (var n in all)
        {
            path.Clear(); var p = n;
            while (p != null && (!state.TryGetValue(p, out var mark) || mark != 2))
            {
                if (state.TryGetValue(p, out mark) && mark == 1)
                    throw new InvalidDataException($"FBX: Cyclic node hierarchy at '{p.Name}' (id {p.Object?.Id})");
                state[p] = 1; path.Add(p); p = p.Parent;
            }
            int depth = p != null ? p.Depth : -1;
            for (int i = path.Count - 1; i >= 0; i--) { path[i].Depth = ++depth; state[path[i]] = 2; }
        }
        // ufbxi_cmp_node_less: depth, then the parent's element id, then the element id (helpers are off here)
        var sorted = all.OrderBy(n => n.Depth).ThenBy(n => n.Parent != null ? n.Parent.ElementId : -1).ThenBy(n => n.ElementId).ToList();
        for (int i = 0; i < sorted.Count; i++) { sorted[i].Index = i; Nodes.Add(sorted[i]); }
        foreach (var n in Nodes) n.Children.Sort((a, b) => a.Index.CompareTo(b.Index));

        foreach (var n in Nodes)
        {
            if (n.IsRoot) continue;
            var props = n.Object.Props;
            // ufbxi_read_model: InheritType 0 (RrSs) -> componentwise, 2 (Rrs) -> ignore parent scale, else normal
            long inherit = props.Int("InheritType", -1);
            n.InheritMode = inherit == 0 ? 2 : inherit == 2 ? 1 : 0;
            // a top-level node is forced NORMAL (space_conversion TRANSFORM_ROOT, the default)
            if (n.Parent.IsRoot) n.InheritMode = 0;
            if (n.InheritMode == 2) n.InheritScaleNode = n.Parent;
            else if (n.InheritMode == 1) n.InheritScaleNode = n.Parent.InheritScaleNode;
            // the attribute: a Geometry (mesh) or a NodeAttribute connected to the Model
            foreach (var c in n.Object.Sources)
            {
                if (c.Kind != "OO" || c.Src == null) continue;
                if (c.Src.Class == "Geometry" && c.Src.SubType == "Mesh") { n.AttribType = "mesh"; break; }
                if (c.Src.Class == "NodeAttribute")
                {
                    string st = c.Src.SubType;
                    n.AttribType = st == "LimbNode" || st == "Limb" || st == "Root" ? "bone" : st == "Null" ? "empty" : st == "Light" ? "light" : st == "Camera" ? "camera" : "other";
                    break;
                }
                if (c.Src.Class == "Material") continue;
            }
            foreach (var c in n.Object.Sources) if (c.Kind == "OO" && c.Src != null && c.Src.Class == "Material") n.Materials.Add(c.Src);
        }
        foreach (var n in Nodes) UpdateNode(n);
    }

    static readonly double[] One3 = { 1, 1, 1 };

    /// <summary>ufbxi_update_node, with the default options (no overrides, no helper nodes).</summary>
    void UpdateNode(FbxSceneNode node)
    {
        var props = node.IsRoot ? new FbxProps() : node.Object.Props;
        node.RotationOrder = (int)props.Enum("RotationOrder", 0, 6);
        if (!node.IsRoot)
        {
            bool rotationActive = props.Int("RotationActive", 1) != 0;
            bool limitOnly = props.Int("RotationSpaceForLimitOnly", 0) != 0;
            node.UseRotationSpace = rotationActive && !limitOnly;
            node.LocalTransform = GetTransform(props, node.RotationOrder, node);
            node.NodeToParent = FbxMath.ToMatrix(node.LocalTransform);
            node.GeometryTransform = GetGeometryTransform(props);
        }
        else { node.LocalTransform = FbxTransform.Identity; node.NodeToParent = FbxMath.IdentityMatrix; node.GeometryTransform = FbxTransform.Identity; }

        var unscaledLocal = node.LocalTransform; unscaledLocal.sx = 1; unscaledLocal.sy = 1; unscaledLocal.sz = 1;
        var unscaledNodeToParent = FbxMath.ToMatrix(unscaledLocal);
        node.InheritScale = new[] { node.LocalTransform.sx, node.LocalTransform.sy, node.LocalTransform.sz };

        var parent = node.Parent;
        if (parent != null)
        {
            if (node.InheritMode == 0)
            {
                node.NodeToWorld = FbxMath.Mul(parent.NodeToWorld, node.NodeToParent);
                node.UnscaledNodeToWorld = FbxMath.Mul(parent.NodeToWorld, unscaledNodeToParent);
            }
            else
            {
                var t = node.LocalTransform;
                var parentScale = node.InheritScaleNode != null ? node.InheritScaleNode.InheritScale : One3;
                t.sx *= parentScale[0]; t.sy *= parentScale[1]; t.sz *= parentScale[2];
                t.tx *= parent.InheritScale[0]; t.ty *= parent.InheritScale[1]; t.tz *= parent.InheritScale[2];
                var nodeToUnscaledParent = FbxMath.ToMatrix(t);
                var tu = t; tu.sx = 1; tu.sy = 1; tu.sz = 1;
                var unscaledToUnscaled = FbxMath.ToMatrix(tu);
                node.InheritScale = new[] { t.sx, t.sy, t.sz };
                node.NodeToWorld = FbxMath.Mul(parent.UnscaledNodeToWorld, nodeToUnscaledParent);
                node.UnscaledNodeToWorld = FbxMath.Mul(parent.UnscaledNodeToWorld, unscaledToUnscaled);
            }
        }
        else { node.NodeToWorld = node.NodeToParent; node.UnscaledNodeToWorld = unscaledNodeToParent; }

        var g = node.GeometryTransform;
        bool identity = g.tx == 0.0 && g.ty == 0.0 && g.tz == 0.0 && g.qx == 0.0 && g.qy == 0.0 && g.qz == 0.0 && g.qw == 1.0 && g.sx == 1.0 && g.sy == 1.0 && g.sz == 1.0;
        node.HasGeometryTransform = !identity;
        node.GeometryToNode = identity ? FbxMath.IdentityMatrix : FbxMath.ToMatrix(g);
        node.Visible = props.Int("Visibility", 1) != 0;
    }

    /// <summary>ufbxi_get_transform: WorldTransform = T * Roff * Rp * Rpre * R * Rpost^-1 * Rp^-1 * Soff * Sp * S * Sp^-1, composed from the inside out.</summary>
    static FbxTransform GetTransform(FbxProps props, int order, FbxSceneNode node)
    {
        var scalePivot = props.Vec3("ScalingPivot", 0, 0, 0);
        var rotPivot = props.Vec3("RotationPivot", 0, 0, 0);
        var scaleOffset = props.Vec3("ScalingOffset", 0, 0, 0);
        var rotOffset = props.Vec3("RotationOffset", 0, 0, 0);
        var translation = props.Vec3("Lcl Translation", 0, 0, 0);
        var rotation = props.Vec3("Lcl Rotation", 0, 0, 0);
        var scaling = props.Vec3("Lcl Scaling", 1, 1, 1);
        var preRotation = props.Vec3("PreRotation", 0, 0, 0);
        var postRotation = props.Vec3("PostRotation", 0, 0, 0);
        var t = FbxTransform.Identity;
        t.SubTranslate(scalePivot);
        t.MulScale(scaling);
        t.AddTranslate(scalePivot);
        t.AddTranslate(scaleOffset);
        t.SubTranslate(rotPivot);
        if (node.UseRotationSpace)
        {
            t.MulInvRotate(postRotation, 0);
            t.MulRotate(rotation, order);
            t.MulRotate(preRotation, 0);
        }
        else t.MulRotate(rotation, 0);
        t.AddTranslate(rotPivot);
        t.AddTranslate(rotOffset);
        t.AddTranslate(translation);
        return t;
    }

    static FbxTransform GetGeometryTransform(FbxProps props)
    {
        var t = FbxTransform.Identity;
        t.MulScale(props.Vec3("GeometricScaling", 1, 1, 1));
        t.MulRotate(props.Vec3("GeometricRotation", 0, 0, 0), 0);
        t.AddTranslate(props.Vec3("GeometricTranslation", 0, 0, 0));
        return t;
    }

    // ---- Meshes --------------------------------------------------------------------------------------------------------
    void BuildMeshes()
    {
        var byObject = new Dictionary<FbxObject, FbxMeshData>();
        foreach (var o in Objects)
        {
            if (o.Class != "Geometry" || o.SubType != "Mesh") continue;
            var m = ReadMesh(o);
            m.Index = Meshes.Count; Meshes.Add(m); byObject[o] = m;
        }
        foreach (var n in Nodes)
        {
            if (n.IsRoot) continue;
            foreach (var c in n.Object.Sources)
                if (c.Kind == "OO" && c.Src != null && byObject.TryGetValue(c.Src, out var m)) { n.Mesh = m; m.Instances.Add(n); break; }
        }
        foreach (var m in Meshes)
        {
            // ufbxi_fetch_mesh_materials(search_node): the Geometry's own Material connections, else the first instance node's
            foreach (var c in m.Object.Sources) if (c.Kind == "OO" && c.Src != null && c.Src.Class == "Material") m.Materials.Add(c.Src);
            if (m.Materials.Count == 0 && m.Instances.Count > 0) m.Materials.AddRange(m.Instances[0].Materials);
            // ufbxi_finalize_scene: one slot -> every face 0; no slot -> no face material list at all; more -> a face
            // material past the slots is 0 (ufbxi_finalize_mesh_material)
            if (m.Materials.Count == 1) m.FaceMaterial = new int[m.NumFaces];
            else if (m.Materials.Count == 0) m.FaceMaterial = new int[0];
            else for (int i = 0; i < m.FaceMaterial.Length; i++) if (m.FaceMaterial[i] < 0 || m.FaceMaterial[i] >= m.Materials.Count) m.FaceMaterial[i] = 0;
        }
    }

    FbxMeshData ReadMesh(FbxObject o)
    {
        var m = new FbxMeshData { Object = o, Name = o.Name };
        var rec = o.Record;
        var vertsNode = rec.First("Vertices");
        if (vertsNode == null) return m;
        m.Vertices = Reals(vertsNode, "Vertices");
        if (m.Vertices.Length % 3 != 0) throw new InvalidDataException($"FBX mesh '{o.Name}': {m.Vertices.Length} vertex values are not triples");
        m.NumVertices = m.Vertices.Length / 3;
        var idxNode = rec.First("PolygonVertexIndex");
        var idx = idxNode != null ? (int[])Integers(idxNode, "PolygonVertexIndex").Clone() : new int[0];
        m.NumIndices = idx.Length;
        if (idx.Length > 0 && idx[idx.Length - 1] >= 0) idx[idx.Length - 1] = ~idx[idx.Length - 1];   // a last index not negated closes the polygon
        // ufbxi_process_indices: a negative index (~x) ends a polygon
        var begins = new List<int>(); var sizes = new List<int>();
        int faceBegin = 0, triangles = 0, maxFaceTriangles = 0;
        for (int i = 0; i < idx.Length; i++)
        {
            if (idx[i] < 0)
            {
                idx[i] = ~idx[i];
                int n = i - faceBegin + 1;
                begins.Add(faceBegin); sizes.Add(n);
                if (n >= 3) { triangles += n - 2; maxFaceTriangles = Math.Max(maxFaceTriangles, n - 2); }
                faceBegin = i + 1;
            }
            if (idx[i] < 0 || idx[i] >= m.NumVertices) throw new InvalidDataException($"FBX mesh '{o.Name}': polygon vertex index {idx[i]} is outside its {m.NumVertices} vertices");
        }
        m.VertexIndices = idx; m.FaceBegin = begins.ToArray(); m.FaceSize = sizes.ToArray();
        m.NumFaces = begins.Count; m.NumTriangles = triangles; m.MaxFaceTriangles = maxFaceTriangles;
        m.Position = new FbxVertexStream { Exists = true, Components = 3, Values = m.Vertices, Indices = idx, UniquePerVertex = true };

        var tangents = new Dictionary<uint, FbxVertexStream>();
        var bitangents = new Dictionary<uint, FbxVertexStream>();
        foreach (var n in rec.Children)
        {
            if (!n.Name.StartsWith("LayerElement", StringComparison.Ordinal)) continue;
            switch (n.Name)
            {
                case "LayerElementNormal": if (!m.Normal.Exists) m.Normal = ReadVertexElement(m, n, "Normals", "NormalsIndex", 3); break;
                case "LayerElementTangent":
                case "LayerElementBinormal":
                {
                    bool tangent = n.Name == "LayerElementTangent";
                    var stream = ReadVertexElement(m, n, tangent ? "Tangents" : "Binormals", tangent ? "TangentsIndex" : "BinormalsIndex", 3);
                    var table = tangent ? tangents : bitangents;
                    if (stream.Exists && !table.ContainsKey(stream.LayerIndex)) table.Add(stream.LayerIndex, stream);
                    break;
                }
                case "LayerElementUV": { var s = ReadVertexElement(m, n, "UV", "UVIndex", 2); if (s.Exists) m.UvSets.Add(s); break; }
                case "LayerElementColor": { var s = ReadVertexElement(m, n, "Colors", "ColorIndex", 4); if (s.Exists) m.ColorSets.Add(s); break; }
                case "LayerElementMaterial":
                {
                    if (m.FaceMaterial.Length > 0) break;
                    string mapping = n.First("MappingInformationType")?.Prop<string>(0) ?? "";
                    var arr = n.First("Materials")?.Properties.Count > 0 ? Integers(n.First("Materials"), "Materials") : null;
                    if (mapping == "ByPolygon")
                    {
                        if (arr == null) { Warnings.Add($"mesh '{o.Name}': missing geometry data: Materials"); break; }
                        m.FaceMaterial = new int[m.NumFaces];
                        Array.Copy(arr, m.FaceMaterial, Math.Min(arr.Length, m.NumFaces));   // ufbxi_read_truncated_array: short arrays are padded with zeros
                    }
                    else if (mapping == "AllSame")
                    {
                        if (arr == null || arr.Length < 1) throw new InvalidDataException($"FBX mesh '{o.Name}': an AllSame material layer without a material");
                        m.FaceMaterial = new int[m.NumFaces];
                        for (int i = 0; i < m.NumFaces; i++) m.FaceMaterial[i] = arr[0];
                    }
                    else Warnings.Add($"mesh '{o.Name}': ignoring geometry 'Materials' with bad mapping mode '{mapping}'");
                    break;
                }
            }
        }
        // ufbx stably sorts sets by their unsigned typed index, then exposes the lowest UV set's tangent pair.
        var sortedUv = m.UvSets.OrderBy(s => s.LayerIndex).ToArray(); m.UvSets.Clear(); m.UvSets.AddRange(sortedUv);
        var sortedColor = m.ColorSets.OrderBy(s => s.LayerIndex).ToArray(); m.ColorSets.Clear(); m.ColorSets.AddRange(sortedColor);
        if (m.UvSets.Count > 0)
        {
            uint primary = m.UvSets[0].LayerIndex;
            foreach (var layer in rec.All("Layer"))
            {
                uint? uv = null, tangent = null, bitangent = null;
                foreach (var element in layer.All("LayerElement"))
                {
                    var typed = element.First("TypedIndex"); var type = element.First("Type");
                    if (typed == null || typed.Properties.Count == 0 || !FbxProps.IsNumber(typed.Properties[0]) || type == null || type.Properties.Count == 0) continue;
                    uint index = unchecked((uint)FbxProps.AsInt(typed.Properties[0]));
                    string name = type.Properties[0] as string ?? "";
                    if (name == "LayerElementUV") uv = index;
                    else if (name == "LayerElementTangent") tangent = index;
                    else if (name == "LayerElementBinormal") bitangent = index;
                }
                if (uv != primary) continue;
                if (tangent.HasValue && tangents.TryGetValue(tangent.Value, out var ts)) m.Tangent = ts;
                if (bitangent.HasValue && bitangents.TryGetValue(bitangent.Value, out var bs)) m.Bitangent = bs;
            }
        }
        if (m.FaceMaterial.Length == 0) m.FaceMaterial = new int[m.NumFaces];
        return m;
    }

    static double[] Reals(FbxNode n, string what)
    {
        if (n.Properties.Count == 0) throw new InvalidDataException($"FBX: {what} has no array");
        switch (n.Properties[0])
        {
            case double[] d: return d;
            case float[] f: { var d = new double[f.Length]; for (int i = 0; i < f.Length; i++) d[i] = f[i]; return d; }
            case int[] ii: { var d = new double[ii.Length]; for (int i = 0; i < ii.Length; i++) d[i] = ii[i]; return d; }
            case long[] l: { var d = new double[l.Length]; for (int i = 0; i < l.Length; i++) d[i] = l[i]; return d; }
            case byte[] b: { var d = new double[b.Length]; for (int i = 0; i < b.Length; i++) d[i] = b[i]; return d; }
            case bool[] b: { var d = new double[b.Length]; for (int i = 0; i < b.Length; i++) d[i] = b[i] ? 1 : 0; return d; }
            default: throw new InvalidDataException($"FBX: {what} is not a number array ({n.Properties[0].GetType().Name})");
        }
    }

    static int[] Integers(FbxNode n, string what)
    {
        if (n.Properties.Count > 0 && n.Properties[0] is int[] exact) return exact;
        if (n.Properties.Count == 0) throw new InvalidDataException($"FBX: {what} has no array");
        switch (n.Properties[0])
        {
            case long[] a: { var d = new int[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = unchecked((int)a[i]); return d; }
            case byte[] a: { var d = new int[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = a[i]; return d; }
            case bool[] a: { var d = new int[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = a[i] ? 1 : 0; return d; }
            case float[] a: { var d = new int[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = ToInt32(a[i]); return d; }
            case double[] a: { var d = new int[a.Length]; for (int i = 0; i < a.Length; i++) d[i] = ToInt32(a[i]); return d; }
            default: throw new InvalidDataException($"FBX: {what} is not a number array");
        }
    }
    static int ToInt32(double value) => Math.Abs(value) <= int.MaxValue ? (int)value : value >= 0 ? int.MaxValue : int.MinValue;

    /// <summary>ufbxi_read_vertex_element: the mapping and reference modes resolved to one index per mesh index.</summary>
    FbxVertexStream ReadVertexElement(FbxMeshData m, FbxNode n, string dataName, string indexName, int components)
    {
        var s = new FbxVertexStream { Components = components, LayerIndex = n.Properties.Count > 0 && FbxProps.IsNumber(n.Properties[0]) ? unchecked((uint)FbxProps.AsInt(n.Properties[0])) : 0u, Name = n.First("Name")?.Prop<string>(0) ?? "" };
        var dataNode = n.First(dataName);
        if (dataNode == null || dataNode.Properties.Count == 0) return s;
        var data = Reals(dataNode, dataName);
        if (data.Length % components != 0) throw new InvalidDataException($"FBX mesh '{m.Name}': {dataName} has {data.Length} values, not a multiple of {components}");
        int numElems = data.Length / components;
        if (numElems == 0) return s;
        s.Exists = true; s.Values = data;
        var idxNode = n.First(indexName);
        int[] indexData = idxNode != null && idxNode.Properties.Count > 0 ? Integers(idxNode, indexName) : null;
        string mapping = n.First("MappingInformationType")?.Prop<string>(0) ?? "";
        if (mapping == "ByPolygon") { int ni = indexData != null ? indexData.Length : numElems; if (ni == m.NumIndices) mapping = "ByPolygonVertex"; }   // old exporters' ByPolygon
        var result = new int[m.NumIndices];
        if (indexData != null)
        {
            if (mapping == "ByPolygonVertex") CheckIndices(result, indexData, numElems);
            else if (mapping == "ByVertex" || mapping == "ByVertice")
            {
                for (int i = 0; i < m.NumIndices; i++) { int ix = m.VertexIndices[i]; result[i] = ix < indexData.Length ? indexData[ix] : numElems - 1; }
                CheckIndices(result, result, numElems); s.UniquePerVertex = true;
            }
            else if (mapping == "ByPolygon")
            {
                for (int f = 0; f < m.NumFaces; f++)
                {
                    int index = f < indexData.Length ? indexData[f] : -1;
                    if (index < 0 || index >= numElems) index = numElems - 1;   // UFBX_NO_INDEX or out of range: clamped (the default index_error_handling)
                    for (int k = 0; k < m.FaceSize[f]; k++) result[m.FaceBegin[f] + k] = index;
                }
            }
            else if (mapping == "AllSame") { s.UniquePerVertex = true; }   // all zeros
            else { Warnings.Add($"mesh '{m.Name}': ignoring geometry '{dataName}' with bad mapping mode '{mapping}'"); return new FbxVertexStream { Components = components }; }
        }
        else
        {
            if (mapping == "ByPolygonVertex") { for (int i = 0; i < m.NumIndices; i++) result[i] = i; if (numElems < m.NumIndices) CheckIndices(result, result, numElems); }
            else if (mapping == "ByVertex" || mapping == "ByVertice") { Array.Copy(m.VertexIndices, result, m.NumIndices); CheckIndices(result, result, numElems); s.UniquePerVertex = true; }
            else if (mapping == "ByPolygon") { for (int f = 0; f < m.NumFaces; f++) for (int k = 0; k < m.FaceSize[f]; k++) result[m.FaceBegin[f] + k] = f; CheckIndices(result, result, numElems); }
            else if (mapping == "AllSame") { s.UniquePerVertex = true; }
            else { Warnings.Add($"mesh '{m.Name}': ignoring geometry '{dataName}' with bad mapping mode '{mapping}'"); return new FbxVertexStream { Components = components }; }
        }
        s.Indices = result;
        return s;
    }

    /// <summary>ufbxi_check_indices with the default CLAMP handling: a truncated index list is extended with NO_INDEX, and every index past the elements becomes the last element.</summary>
    static void CheckIndices(int[] dst, int[] src, int numElems)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            int ix = i < src.Length ? src[i] : -1;
            if (ix < 0 || ix >= numElems) ix = numElems - 1;
            dst[i] = ix;
        }
    }

    // ---- Materials -----------------------------------------------------------------------------------------------------
    void BuildMaterials()
    {
        foreach (var o in Objects)
        {
            if (o.Class != "Material") continue;
            var mat = new FbxMaterialData { Object = o, Index = Materials.Count, Name = o.Name };
            mat.ShadingModel = o.Record.First("ShadingModel")?.Prop<string>(0) ?? "";
            var dc = o.Props.Find("DiffuseColor") ?? o.Props.Find("Diffuse");
            if (dc != null) mat.Diffuse = new[] { dc.Reals[0], dc.Reals[1], dc.Reals[2] };
            // ufbxi_finalize: lambert/Lambert 1, phong/Phong 2; a connected shader (Implementation) decides by its RenderAPI
            // (OSL 3, ARNOLD_SHADER_ID 4, SFX_PBS_SHADER 10); else 3ds Max's class ids name its PBR materials
            if (mat.ShadingModel == "lambert" || mat.ShadingModel == "Lambert") mat.ShaderType = 1;
            else if (mat.ShadingModel == "phong" || mat.ShadingModel == "Phong") mat.ShaderType = 2;
            // ufbxi_fetch_src_element(&material->element, UFBX_ELEMENT_SHADER): the MATERIAL is the source of the connection,
            // the Implementation its destination (the 3ds Max temple: `OO Material -> Implementation`, RenderAPI "OSL")
            FbxObject shader = null;
            foreach (var c in o.Targets) if (c.Kind == "OO" && c.Dst != null && c.Dst.Class == "Implementation") { shader = c.Dst; break; }
            if (shader != null)
            {
                string api = shader.Props.Str("RenderAPI", "");
                mat.ShaderType = api == "ARNOLD_SHADER_ID" ? 4 : api == "OSL" ? 3 : api == "SFX_PBS_SHADER" ? 10 : 0;
            }
            else if (mat.ShaderType == 0)
            {
                uint a = (uint)(ulong)o.Props.Int("3dsMax|ClassIDa", 0), b = (uint)(ulong)o.Props.Int("3dsMax|ClassIDb", 0);
                if (a == 0x3d6b1cecu && b == 0xdeadc001u) mat.ShaderType = 5;
                else if (a == 0xf1551e33u && b == 0x37fb1337u) mat.ShaderType = 9;
                else if (a == 0x38420192u && b == 0x45fe4e1bu) mat.ShaderType = 8;
                else if (a == 0xd00f1e00u && b == 0xbe77e500u) mat.ShaderType = 6;
                else if (a == 0xd00f1e00u && b == 0x01dbad33u) mat.ShaderType = 7;
            }
            // the textures on the material (OO, and OP on a property), as ufbx sorts them (ufbxi_cmp_tmp_material_texture_less):
            // a material's own connections all carry texture_id -1, so the order is the property name's alone (memcmp, the
            // unnamed OO ones first), ties in file order. Textures a MESH names through LayerElement*Textures (texture_id >= 0,
            // sorted after these) are not modelled: none of the files here carry them.
            var texs = new List<(string prop, FbxObject tex)>();
            foreach (var c in o.Sources) if ((c.Kind == "OP" || c.Kind == "OO") && c.Src != null && c.Src.IsTexture) texs.Add((c.Kind == "OP" ? c.DstProp : "", c.Src));
            foreach (var (prop, tex) in texs.OrderBy(t => t.prop, Utf8Order))
                mat.Textures.Add((prop, tex, tex.Record.First("RelativeFilename")?.Prop<string>(0) ?? tex.Record.First("RelativeFileName")?.Prop<string>(0) ?? ""));
            Materials.Add(mat);
        }
    }

    // UTF-8 byte order is Unicode scalar order; UTF-16 Ordinal puts surrogate pairs before BMP private-use chars.
    static readonly IComparer<string> Utf8Order = Comparer<string>.Create(CompareUtf8);
    static int CompareUtf8(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            int x = a[i++], y = b[j++];
            if (char.IsHighSurrogate((char)x) && i < a.Length && char.IsLowSurrogate(a[i])) x = char.ConvertToUtf32((char)x, a[i++]);
            if (char.IsHighSurrogate((char)y) && j < b.Length && char.IsLowSurrogate(b[j])) y = char.ConvertToUtf32((char)y, b[j++]);
            if (x != y) return x < y ? -1 : 1;
        }
        return i < a.Length ? 1 : j < b.Length ? -1 : 0;
    }
}
