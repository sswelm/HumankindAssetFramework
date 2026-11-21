// MaxScene.cs - the object layer of the native 3ds Max reader (2026-11-21): the class directory, the scene's objects and
// the references between them, and from those the things the tools use - nodes with their place in the world, the
// triangles of each Editable Mesh, Standard and Multi/Sub-Object materials with their diffuse map.
//
// How a .max scene is laid out (held on the user's four models: an armoured car, a second one, a torpedo boat, a stealth
// ship - 257 mesh parts, 244,337 visible faces and 16 hidden ones):
// - the class directory lists every class the scene uses (a class id of two 32-bit numbers plus a superclass id);
// - the Scene stream holds ONE container whose children are the scene's objects; a child's chunk id is its class's
//   index in the directory, except 0x2032 / 0x2033 = a derived object (an object with modifiers on it);
// - an object names others by INDEX in that list: chunk 0x2034 is a plain array of indices (0xFFFFFFFF = none),
//   chunk 0x2035 a count followed by (slot, index) pairs;
// - a node's references are 0 = its transform controller, 1 = its object, 3 = its material.
//
// WHAT IS READ: Editable Mesh and the older MeshObject (vertices, faces with smoothing group and material id, one UV
// channel); the Position/Rotation/Scale controller with Bezier position, TCB rotation, Bezier scale and list controllers
// (their first entry); the object offset; Standard materials (diffuse colour, opacity, two-sided, the diffuse slot's
// Bitmap or Mask-of-two-Bitmaps with its amount, offset, tiling and the tile switches); Multi/Sub-Object.
// WHAT IS NOT, said by name in the notes and never guessed: modifiers (a .max stores the base object and the list of
// modifiers, not the result - only 3ds Max can evaluate them; a Skin is one of them and is counted with them),
// parametric primitives (stored as their parameters), Editable Poly (no sample file yet to hold a reader to),
// controllers of other classes, procedural maps, every map slot but diffuse (named by slot), a map's angle and mirror
// switches. Lights, cameras and helpers are counted by class; hidden faces are left out and counted.
// NOT READ AND NOT DETECTED, so the report cannot name it: animation (a part stands where the value stored in each of
// its controllers puts it; whether that controller also holds keys is not looked at), whether an object is hidden,
// UV channels beyond the first and vertex colours.
//
// Pure C#: no Unity. Numbers are doubles here; MaxModel casts to the float32 a GLB holds.
using System;
using System.Collections.Generic;
using System.IO;

public sealed class MaxClass
{
    public string Name = "";
    public uint A, B, Super;
    public int Dll;
    public bool Is(uint a, uint b, uint super) => A == a && B == b && Super == super;
}

/// <summary>One map slot's bitmap as the file describes it.</summary>
public sealed class MaxBitmap
{
    public string File = "";                 // the file NAME (the path the artist's disk had is dropped)
    public bool TileU = true, TileV = true;  // the Placement's tile switches
    public double OffsetU, OffsetV, TilingU = 1, TilingV = 1, Angle;
    public bool HasPlacement => OffsetU != 0 || OffsetV != 0 || TilingU != 1 || TilingV != 1;
    public bool PlacementLeft;               // the file's offset / tiling / angle held an unusable number: none is applied
    public bool MirrorU, MirrorV;            // the Placement's mirror switches: read as plain tiling (and said)
}

public sealed class MaxMaterial
{
    public int Object = -1;                  // its index in the scene's objects (identity)
    public string Name = "";
    public double[] Diffuse = { 0.6, 0.6, 0.6 };   // as the file holds it (display values, 0..1)
    public double Opacity = 1;
    public bool TwoSided;
    public MaxBitmap Map;                    // the diffuse slot's picture, or null
    public MaxBitmap Mask;                   // a Mask map's second picture (white shows the map), or null
    public double Amount = 1;                // the diffuse slot's amount: below 1 the map mixes with the colour
    public string MapLeft;                   // why the diffuse slot's map is not read (a procedural map, ...), or null
    public bool DiffuseRead;                 // false: the colour was not found where this reader looks (the grey above stands in)
    public readonly List<(string slot, string what)> OtherMaps = new List<(string, string)>();   // maps in slots other than diffuse: not read, named
}

public sealed class MaxMeshData
{
    public double[] Verts;                   // 3 per vertex, object space
    public int[] Faces;                      // 3 vertex indices per face
    public uint[] Smoothing;                 // per face
    public int[] MatId;                      // per face
    public double[] TVerts;                  // 3 per UV vertex (u, v, w), or null
    public int[] TFaces;                     // 3 UV indices per face, or null
    public string UvLeft;                    // why the file's UVs are not read (a list that does not fit its faces, a UV that is no number), or null
    public int Hidden;                      // faces the file marks hidden (3ds Max does not draw them): left out, counted
    public int FaceCount => Faces.Length / 3;
}

public sealed class MaxNode
{
    public int Object;                       // index in the scene's objects
    public string Name = "";
    public int Parent = -1;                  // index into MaxScene.Nodes, or -1 (a child of the scene root)
    public double[] Local;                   // 4x4 row-major, column vectors: parent-from-node
    public double[] World;                   // world-from-node
    public double[] Offset;                  // the object offset: node-from-geometry (not inherited by children)
    public string BaseClass = "";            // the class of the object under the modifiers ("Editable Mesh", "Target Spot", ...)
    public int Modifiers;                    // modifiers on the stack above the base object (not evaluated)
    public MaxMeshData Mesh;                 // or null
    public string MeshLeft;                  // why a geometry object gave no mesh, or null
    public int MaterialObject = -1;          // scene object index of the node's material, or -1
    public string TransformNote;             // what about its placement is not held (an unread controller, ...), or null
}

public sealed class MaxScene
{
    public readonly List<MaxClass> Classes = new List<MaxClass>();
    public readonly List<MaxChunk> Objects = new List<MaxChunk>();
    public readonly List<MaxNode> Nodes = new List<MaxNode>();
    public string ClassDirectoryStream = "";
    public bool Compressed;
    readonly Dictionary<int, MaxMaterial> materials = new Dictionary<int, MaxMaterial>();

    // class ids (classA, classB, superclass), as the four sample files hold them
    const uint SuperNode = 0x1, SuperGeom = 0x10, SuperMtl = 0xC00, SuperTex = 0xC10;
    static bool IsNode(MaxClass c) => c != null && c.Is(0x1, 0, SuperNode);
    static bool IsEditablePoly(MaxClass c) => c != null && c.Is(0x1BF8338D, 0x192F6098, SuperGeom);
    static bool IsStandard(MaxClass c) => c != null && c.Is(0x2, 0, SuperMtl);
    static bool IsMultiSub(MaxClass c) => c != null && c.Is(0x200, 0, SuperMtl);
    static bool IsBitmap(MaxClass c) => c != null && c.Is(0x240, 0, SuperTex);
    static bool IsMask(MaxClass c) => c != null && c.Is(0x220, 0, SuperTex);
    static bool IsPrs(MaxClass c) => c != null && c.Is(0x2005, 0, 0x9008);
    static bool IsList(MaxClass c) => c != null && c.B == 0 && (c.A == 0x4B4B1002 || c.A == 0x4B4B1003 || c.A == 0x4B4B1004);
    const int DerivedObject = 0x2032, WsmDerivedObject = 0x2033;

    public static MaxScene Read(string path) => From(MaxDocument.Read(path));

    public static MaxScene From(MaxDocument doc)
    {
        var s = new MaxScene { Compressed = doc.Compressed };
        foreach (var name in new[] { "ClassDirectory3", "ClassDirectory2", "ClassDirectory" })
            if (doc.Streams.ContainsKey(name)) { s.ClassDirectoryStream = name; break; }
        if (s.ClassDirectoryStream == "") throw new InvalidDataException("the .max file has no class directory stream (ClassDirectory, ClassDirectory2 or ClassDirectory3)");
        foreach (var c in doc.Chunks(s.ClassDirectoryStream))
        {
            var k = c.First(0x2060); var nm = c.First(0x2042);
            if (k == null || k.IsContainer || k.Length < 16) throw new InvalidDataException("the class directory has an entry without its class id");
            s.Classes.Add(new MaxClass { Dll = k.I32(0), A = k.U32(4), B = k.U32(8), Super = k.U32(12), Name = nm == null ? "" : nm.Text() });
        }
        var top = doc.Chunks("Scene");
        if (top == null) throw new InvalidDataException("the .max file has no Scene stream");
        var root = top.Find(c => c.IsContainer);
        if (root == null) throw new InvalidDataException("the Scene stream holds no object list");
        s.Objects.AddRange(root.Kids);
        s.ReadNodes();
        return s;
    }

    public MaxClass ClassOf(MaxChunk c)
        => c == null || c.Id == DerivedObject || c.Id == WsmDerivedObject || c.Id >= Classes.Count ? null : Classes[c.Id];
    public string ClassName(MaxChunk c)
        => c == null ? "nothing" : c.Id == DerivedObject || c.Id == WsmDerivedObject ? "a derived object" : c.Id < Classes.Count ? Classes[c.Id].Name : $"class {c.Id:X4}";
    public MaxChunk Get(long index) => index >= 0 && index < Objects.Count ? Objects[(int)index] : null;

    /// <summary>References that name an object the scene does not have (a damaged file): each is read as "none", and
    /// MaxModel says how many there were.</summary>
    public int DanglingReferences;
    static readonly SortedDictionary<int, int> NoRefs = new SortedDictionary<int, int>();
    readonly Dictionary<MaxChunk, SortedDictionary<int, int>> refsOf = new Dictionary<MaxChunk, SortedDictionary<int, int>>();
    readonly Dictionary<MaxChunk, int> slotsOf = new Dictionary<MaxChunk, int>();

    /// <summary>An object's references: slot -> object index. Empty slots (0xFFFFFFFF) are left out. Built once per
    /// object and kept (callers only read it): rebuilt on every call, one object with 200,000 references named by 20
    /// nodes took about 20 seconds (review 2026-11-21).</summary>
    public SortedDictionary<int, int> Refs(MaxChunk c)
    {
        if (c == null || !c.IsContainer) return NoRefs;
        if (refsOf.TryGetValue(c, out var have)) return have;
        var r = new SortedDictionary<int, int>(); int slots = 0;
        var a = c.First(0x2034);
        if (a != null && !a.IsContainer)
        {
            slots = a.Length / 4;
            for (int i = 0; i + 4 <= a.Length; i += 4)
            {
                uint v = a.U32(i);
                if (v == 0xFFFFFFFF) continue;
                if (v < Objects.Count) r[i / 4] = (int)v; else DanglingReferences++;
            }
        }
        else
        {
            var p = c.First(0x2035);
            // the pair form opens with the object's SLOT count (in the samples' 1,245 such chunks it is always larger
            // than the largest slot named - a node's 6, a map table's 22 - and never the number of pairs)
            if (p != null && !p.IsContainer && p.Length >= 4 && p.U32(0) <= int.MaxValue) slots = (int)p.U32(0);
            if (p != null && !p.IsContainer)
                for (int i = 4; i + 8 <= p.Length; i += 8)
                {
                    uint key = p.U32(i), v = p.U32(i + 4);
                    if (key > int.MaxValue - 1) continue;
                    if ((int)key + 1 > slots) slots = (int)key + 1;
                    if (v == 0xFFFFFFFF) continue;
                    if (v < Objects.Count) r[(int)key] = (int)v; else DanglingReferences++;
                }
        }
        refsOf[c] = r; slotsOf[c] = slots;
        return r;
    }
    /// <summary>How many reference slots an object has, the empty ones included (a Multi/Sub-Object's material ids
    /// count its slots, not its filled slots).</summary>
    public int RefSlots(MaxChunk c) { Refs(c); return c != null && slotsOf.TryGetValue(c, out int n) ? n : 0; }
    MaxChunk Ref(MaxChunk c, int slot) => Refs(c).TryGetValue(slot, out int i) ? Objects[i] : null;

    static double[] Floats(MaxChunk c, int id, int n)
    {
        var k = c == null ? null : c.First(id);
        if (k == null || k.IsContainer || k.Length < 4 * n) return null;
        var v = new double[n];
        for (int i = 0; i < n; i++) v[i] = k.F32(4 * i);
        return v;
    }

    // ---------------------------------------------------------------------------------------------- matrices
    // 4x4, row-major, column vectors: world = M * v. Every product is written out in one fixed order, the order the
    // drill's second reader (tools/max-drill/max_world.py) uses for the same arithmetic in doubles.
    public static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    public static double[] Mul(double[] a, double[] b)
    {
        var m = new double[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += a[i * 4 + k] * b[k * 4 + j];
                m[i * 4 + j] = s;
            }
        return m;
    }
    /// <summary>The rotation a .max quaternion (x, y, z, w) stands for. 3ds Max stores the INVERSE of the usual
    /// quaternion: read plainly, the armoured car's turret hatch and spare wheel fly off; conjugated, all four models
    /// assemble (2026-11-20).</summary>
    public static double[] Rotation(double[] q)
    {
        double x = -q[0], y = -q[1], z = -q[2], w = q[3];
        double n = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (n == 0) n = 1;
        x = x / n; y = y / n; z = z / n; w = w / n;
        var m = Identity();
        m[0] = 1 - 2 * (y * y + z * z); m[1] = 2 * (x * y - z * w); m[2] = 2 * (x * z + y * w);
        m[4] = 2 * (x * y + z * w); m[5] = 1 - 2 * (x * x + z * z); m[6] = 2 * (y * z - x * w);
        m[8] = 2 * (x * z - y * w); m[9] = 2 * (y * z + x * w); m[10] = 1 - 2 * (x * x + y * y);
        return m;
    }
    /// <summary>Translate * Rotate * Scale. A scale chunk holds three factors and, where it is seven numbers long, a
    /// quaternion: the axis system the factors apply in (a part scaled along its PARENT's axes after it was turned is
    /// stretched along directions that are not its own). The scale is then U * diag(factors) * U-transposed, with U
    /// that quaternion's rotation read like every other one in the file.
    /// Measured 2026-11-21 on the twelve parts of the four models where the rule changes anything (factors that
    /// differ, a turned axis): how far the part's stretch in the world sits from the world's axes (the largest
    /// off-diagonal of A*A-transposed over its largest diagonal entry). With the axis applied this way nine of them
    /// lie on the world's axes (0.015 or less; the torpedo boat's PIEZA-156 goes from 0.891 to 0.015) and a tenth
    /// close to them (0.093 from 0.814); with it left out, or applied the other way round, none of the boat's eleven
    /// does. Two (its anchor and PIEZA-75) lie on them under no reading, and further from them with the axis than
    /// without (0.21 to 0.36, 0.14 to 0.50): the rule stands on the other ten. PIEZA-74 and PIEZA-75 are the same fitting on
    /// the boat's two sides: with the axis applied their boxes are 30 x 167 x 54 and 35 x 163 x 54, without it
    /// 46 x 50 x 54 and 39 x 206 x 54 - two different things where the boat has a pair.
    /// Why nine land on the axes: a part turned by R and then scaled along its PARENT's axes has the matrix
    /// K*R = R*(R-transposed*K*R), so its stored axis system is the inverse of its rotation - which, up to a quarter
    /// or half turn and a degree or two, is what the file holds for those parts (their parents are not turned, so
    /// parent and world are one here), and what only this reading turns back into a stretch along those axes.
    /// The first version of this reader left the axis out on the anchor's evidence (more right angles survive without
    /// it); a diagonal scale keeps every right angle of the part's own axes whatever the file means, so that test
    /// could not tell the readings apart (review 2026-11-21).</summary>
    public static double[] Trs(double[] p, double[] q, double[] s)
    {
        var sm = Identity(); sm[0] = s[0]; sm[5] = s[1]; sm[10] = s[2];
        if (s.Length >= 7 && (s[0] != s[1] || s[1] != s[2]))       // three equal factors: the axis system changes nothing
        {
            var u = Rotation(new[] { s[3], s[4], s[5], s[6] });
            var ut = Identity();
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) ut[i * 4 + j] = u[j * 4 + i];
            sm = Mul(Mul(u, sm), ut);
        }
        var m = Mul(Rotation(q), sm);
        m[3] = p[0]; m[7] = p[1]; m[11] = p[2];
        return m;
    }
    public static double Det3(double[] m)
        => m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);

    // ---------------------------------------------------------------------------------------------- nodes
    double[] ControllerValue(MaxChunk c, int valueChunk, int n, ref string note, int depth = 0)
    {
        if (c == null) return null;
        var v = Floats(c, valueChunk, n);
        if (v != null) return v;
        string what = valueChunk == 0x2503 ? "position" : valueChunk == 0x2504 ? "rotation" : "scale";
        if (IsList(ClassOf(c)) && depth < 8)
        {
            // a list controller: its first entry. 3ds Max combines all of a list's entries, so a list of more than one
            // (or of none) is said. Each sample scene holds list objects, and no node of theirs uses one.
            var r = Refs(c);
            // (the entries are counted as the list's references; whether a real list keeps other references there too
            // is not known - with one it would be named here needlessly, never read wrongly)
            if (r.Count != 1 && note == null) note = "its " + what + " is a list controller with " + (r.Count == 0 ? "no entry" : r.Count + " entries, of which only the first is read");
            foreach (var kv in r) return ControllerValue(Objects[kv.Value], valueChunk, n, ref note, depth + 1);
            return null;
        }
        if (note == null) note = "its " + what + " controller '" + ClassName(c) + "' is not read";
        return null;
    }

    void ReadNodes()
    {
        var nodeOf = new Dictionary<int, int>();      // object index -> Nodes index
        for (int i = 0; i < Objects.Count; i++)
            if (IsNode(ClassOf(Objects[i]))) { nodeOf[i] = Nodes.Count; Nodes.Add(new MaxNode { Object = i }); }
        foreach (var n in Nodes)
        {
            var c = Objects[n.Object];
            var nm = c.First(0x0962);
            n.Name = nm == null ? "" : nm.Text();
            var pk = c.First(0x0960);
            if (pk != null && !pk.IsContainer && pk.Length >= 4 && nodeOf.TryGetValue((int)Math.Min(pk.U32(0), int.MaxValue), out int pi) && pi != nodeOf[n.Object]) n.Parent = pi;
            var refs = Refs(c);

            // the transform controller
            string note = null;
            n.Local = Identity();
            var ctrl = refs.TryGetValue(0, out int ci) ? Objects[ci] : null;
            if (ctrl != null)
            {
                if (IsPrs(ClassOf(ctrl)))
                {
                    var cr = Refs(ctrl);
                    var p = ControllerValue(cr.TryGetValue(0, out int a) ? Objects[a] : null, 0x2503, 3, ref note) ?? new double[] { 0, 0, 0 };
                    var q = ControllerValue(cr.TryGetValue(1, out int b) ? Objects[b] : null, 0x2504, 4, ref note) ?? new double[] { 0, 0, 0, 1 };
                    var sc = ScaleValue(cr.TryGetValue(2, out int d) ? Objects[d] : null, ref note) ?? new double[] { 1, 1, 1 };
                    n.Local = Trs(p, q, sc);
                }
                else note = "its transform controller '" + ClassName(ctrl) + "' is not read";
            }
            n.TransformNote = note;
            var op = Floats(c, 0x096A, 3) ?? new double[] { 0, 0, 0 };
            var oq = Floats(c, 0x096B, 4) ?? new double[] { 0, 0, 0, 1 };
            var os = Floats(c, 0x096C, 7) ?? Floats(c, 0x096C, 3) ?? new double[] { 1, 1, 1 };
            n.Offset = Trs(op, oq, os);

            // the object: walk past the modifiers to the base object (a derived object's LAST reference)
            var o = refs.TryGetValue(1, out int oi) ? Objects[oi] : null;
            for (int depth = 0; o != null && (o.Id == DerivedObject || o.Id == WsmDerivedObject) && depth < 16; depth++)
            {
                var rr = Refs(o);
                if (rr.Count == 0) { o = null; break; }
                int last = -1, lastKey = -1;
                foreach (var kv in rr) if (kv.Key > lastKey) { lastKey = kv.Key; last = kv.Value; }
                n.Modifiers += rr.Count - 1;
                o = Objects[last];
            }
            n.BaseClass = o == null ? "" : ClassName(o);
            var cls = ClassOf(o);
            if (o != null && o.IsContainer && o.First(0x08FE) != null)
            {
                try { n.Mesh = ReadMesh(o.First(0x08FE)); }
                catch (InvalidDataException e) { n.MeshLeft = e.Message + (IsEditablePoly(cls) ? " (an Editable Poly object is not read yet: no sample file to hold the reader to)" : ""); }
            }
            else if (IsEditablePoly(cls)) n.MeshLeft = "an Editable Poly object is not read yet (no sample file to hold the reader to)";
            else if (cls != null && cls.Super == SuperGeom && !IsTargetLike(cls)) n.MeshLeft = "'" + cls.Name + "' is stored as its parameters, not as a mesh (only 3ds Max can build it)";
            if (refs.TryGetValue(3, out int mi)) n.MaterialObject = mi;
        }
        // world transforms, parents first (a cycle of parents is refused)
        var state = new int[Nodes.Count];
        for (int i = 0; i < Nodes.Count; i++) World(i, state);
    }

    // a camera's or light's target is a geometry-superclass helper with nothing to draw
    static bool IsTargetLike(MaxClass c) => c.Is(0x1020, 0, SuperGeom);

    double[] ScaleValue(MaxChunk c, ref string note)
    {
        string n2 = null;
        var v = ControllerValue(c, 0x2505, 7, ref n2);
        if (v != null) { if (note == null) note = n2; return v; }
        return ControllerValue(c, 0x2505, 3, ref note);
    }

    /// <summary>World transforms, parents first, without recursion: a chain of any length is read (the first version
    /// refused a chain deeper than 4,096 as a cycle), and a real cycle of parents is refused by name.</summary>
    void World(int i, int[] state)
    {
        if (state[i] == 2) return;
        var chain = new List<int>();
        for (int at = i; at >= 0 && state[at] != 2; at = Nodes[at].Parent)
        {
            if (state[at] == 1) throw new InvalidDataException($"node '{Nodes[at].Name}' is its own ancestor - the hierarchy has a cycle");
            state[at] = 1; chain.Add(at);
        }
        for (int k = chain.Count - 1; k >= 0; k--)
        {
            var n = Nodes[chain[k]];
            n.World = n.Parent >= 0 ? Mul(Nodes[n.Parent].World, n.Local) : n.Local;
            state[chain[k]] = 2;
        }
    }

    // ---------------------------------------------------------------------------------------------- meshes
    /// <summary>A UV, an offset or a tiling beyond this is not a placement on a picture but a damaged number; their
    /// product must also stay inside a 32-bit float (a GLB's UVs), and 1e6 * 1e6 does.</summary>
    public const double MaxUv = 1e6;

    static MaxMeshData ReadMesh(MaxChunk g)
    {
        var v = g.First(0x0914); var f = g.First(0x0912);
        if (v == null || f == null || v.IsContainer || f.IsContainer) throw new InvalidDataException("the mesh has no vertex or face list");
        long nv = v.Length >= 4 ? v.U32(0) : 0, nf = f.Length >= 4 ? f.U32(0) : 0;
        if (v.Length != 4 + 12 * nv) throw new InvalidDataException($"the vertex list declares {nv} vertices in {v.Length} bytes");
        if (f.Length != 4 + 20 * nf) throw new InvalidDataException($"the face list declares {nf} faces in {f.Length} bytes");
        var m = new MaxMeshData { Verts = new double[nv * 3] };
        for (int i = 0; i < nv * 3; i++) m.Verts[i] = v.F32(4 + 4 * i);
        for (int i = 0; i < m.Verts.Length; i++) if (double.IsNaN(m.Verts[i]) || double.IsInfinity(m.Verts[i])) throw new InvalidDataException("a vertex is not a finite number");

        var tv = g.First(0x0916); var tf = g.First(0x0918);
        long nt = 0; bool uv = false;
        if (tv != null && tf != null && !tv.IsContainer && !tf.IsContainer && tv.Length >= 4)
        {
            nt = tv.U32(0);
            uv = tv.Length == 4 + 12 * nt && tf.Length == 12 * nf && nt > 0;
            if (!uv && nt > 0) m.UvLeft = tv.Length != 4 + 12 * nt ? $"its UV list declares {nt} entries in {tv.Length} bytes" : $"its UV faces fill {tf.Length} bytes where {nf} faces need {12 * nf}";
        }
        // a UV list without its faces (or the other way round, or either one holding chunks): said too
        else if (tv != null || tf != null) m.UvLeft = tv == null ? "it has UV faces and no UV list" : tf == null ? "it has a UV list and no UV faces" : "its UV chunks are not the lists this reader knows";
        var keep = new List<int>();
        for (int i = 0; i < nf; i++)
        {
            int at = 4 + 20 * i;
            ushort flags = f.U16(at + 16);
            if ((flags & 0x8) != 0) { m.Hidden++; continue; }      // FACE_HIDDEN
            for (int k = 0; k < 3; k++) if (f.U32(at + 4 * k) >= nv) throw new InvalidDataException($"face {i} names vertex {f.U32(at + 4 * k)} of {nv}");
            keep.Add(i);
        }
        m.Faces = new int[keep.Count * 3]; m.Smoothing = new uint[keep.Count]; m.MatId = new int[keep.Count];
        if (uv) { m.TVerts = new double[nt * 3]; for (int i = 0; i < nt * 3; i++) m.TVerts[i] = tv.F32(4 + 4 * i); m.TFaces = new int[keep.Count * 3]; }
        for (int o = 0; o < keep.Count; o++)
        {
            int i = keep[o], at = 4 + 20 * i;
            for (int k = 0; k < 3; k++) m.Faces[o * 3 + k] = (int)f.U32(at + 4 * k);
            m.Smoothing[o] = f.U32(at + 12);
            m.MatId[o] = f.U16(at + 18);
            if (uv)
                for (int k = 0; k < 3; k++)
                {
                    uint t = tf.U32(12 * i + 4 * k);
                    if (t >= nt) throw new InvalidDataException($"face {i} names UV vertex {t} of {nt}");
                    m.TFaces[o * 3 + k] = (int)t;
                }
        }
        // a UV that is no number, or so far out that a tiling could push it past a 32-bit float: the part is read without UVs, and says so
        if (uv)
            for (int i = 0; i < m.TVerts.Length; i++)
            {
                double t = m.TVerts[i];
                if (!double.IsNaN(t) && Math.Abs(t) <= MaxUv) continue;
                m.TVerts = null; m.TFaces = null;
                m.UvLeft = "a UV is not a usable number (" + t.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")";
                break;
            }
        return m;
    }

    // ---------------------------------------------------------------------------------------------- materials
    /// <summary>The Standard material a face of this node draws with: the node's material, or the sub-material its
    /// material id picks from a Multi/Sub-Object (ids wrap around the number of sub-materials, as 3ds Max wraps them).
    /// Null with `why` when the node has no material or one of a class this reader does not know.</summary>
    public MaxMaterial MaterialFor(MaxNode node, int matId, out string why)
    {
        why = null;
        int at = node.MaterialObject;
        if (at < 0) { why = "no material"; return null; }
        for (int depth = 0; depth < 8; depth++)
        {
            var m = Objects[at];
            var cls = ClassOf(m);
            if (IsStandard(cls)) return Standard(at);
            if (!IsMultiSub(cls)) { why = "a material of class '" + ClassName(m) + "' is not read"; return null; }
            var r = Refs(m);
            if (r.Count == 0) { why = "a Multi/Sub-Object material without sub-materials"; return null; }
            // the id counts the material's SLOTS, the empty ones too: counted over the filled slots only, one empty
            // slot moved every later sub-material one id down (review 2026-11-21)
            int slots = RefSlots(m), slot = matId % slots;
            if (!r.TryGetValue(slot, out at)) { why = "its Multi/Sub-Object material '" + MaterialName(m) + "' has no sub-material in slot " + (slot + 1); return null; }
            matId = 0;
        }
        why = "materials nested deeper than 8";
        return null;
    }

    static string MaterialName(MaxChunk m)
    {
        var nc = m.First(0x4000);
        return nc != null && nc.First(0x4001) != null ? nc.First(0x4001).Text() : "Material";
    }

    // the Standard material's map slots in the order of its map table (3ds Max 2 and 3; the diffuse slot is 1)
    static readonly string[] SlotNames = { "ambient", "diffuse", "specular", "shininess", "shininess strength", "self-illumination", "opacity", "filter colour", "bump", "reflection", "refraction", "displacement" };

    MaxMaterial Standard(int index)
    {
        if (materials.TryGetValue(index, out var have)) return have;
        var m = Objects[index];
        var mat = new MaxMaterial { Object = index, Name = MaterialName(m) };
        var fl = m.First(0x5000);
        if (fl != null && !fl.IsContainer && fl.Length >= 4) mat.TwoSided = (fl.U32(0) & 0x8) != 0;
        var r = Refs(m);
        var pb = r.TryGetValue(0, out int pbi) ? Objects[pbi] : null;
        if (pb != null && pb.IsContainer)
            foreach (var k in pb.All(0x0002))
            {
                var ix = k.First(0x0003);
                if (ix == null || ix.IsContainer || ix.Length < 4) continue;
                if (ix.U32(0) == 1) { var c = Floats(k, 0x0102, 3); if (c != null) { mat.Diffuse = c; mat.DiffuseRead = true; } }
                if (ix.U32(0) == 6) { var c = Floats(k, 0x0100, 1); if (c != null) mat.Opacity = c[0]; }
            }
        var tm = r.TryGetValue(1, out int tmi) ? Objects[tmi] : null;
        var tr = Refs(tm);
        // slot n of the map table: reference 2n = its amount, 2n+1 = its map; the diffuse slot is 1
        var map = tr.TryGetValue(3, out int di) ? Objects[di] : null;
        if (map != null)
        {
            var am = tr.TryGetValue(2, out int ai) ? Floats(Objects[ai], 0x2501, 1) : null;
            if (am != null) mat.Amount = am[0];
            if (IsBitmap(ClassOf(map))) mat.Map = Bitmap(map);
            else if (IsMask(ClassOf(map)))
            {
                var mr = Refs(map);
                var a = mr.TryGetValue(0, out int a0) ? Objects[a0] : null;
                var b = mr.TryGetValue(1, out int b0) ? Objects[b0] : null;
                if (a != null && IsBitmap(ClassOf(a))) { mat.Map = Bitmap(a); if (b != null && IsBitmap(ClassOf(b))) mat.Mask = Bitmap(b); else if (b != null) mat.MapLeft = "its mask is a '" + ClassName(b) + "' map, which is not read (the map is shown whole)"; }
                else mat.MapLeft = "a Mask map over " + ClassName(a) + " is not read";
            }
            else mat.MapLeft = "a '" + ClassName(map) + "' map is not read (plain colour instead)";
            if (mat.Map != null && mat.Map.File == "") { mat.Map = null; mat.Mask = null; mat.MapLeft = "its bitmap names no file"; }
        }
        // every other slot that holds a map: not read, and said by slot (an opacity map cuts holes in 3ds Max; here the
        // faces are written whole)
        foreach (var kv in tr)
        {
            if (kv.Key % 2 == 0 || kv.Key == 3) continue;
            int slot = kv.Key / 2;
            var other = Objects[kv.Value];
            string file = IsBitmap(ClassOf(other)) ? Bitmap(other).File : "";
            mat.OtherMaps.Add((slot < SlotNames.Length ? SlotNames[slot] : "slot " + slot, file != "" ? "'" + file + "'" : "a " + ClassName(other) + " map"));
        }
        materials[index] = mat;
        return mat;
    }

    MaxBitmap Bitmap(MaxChunk b)
    {
        var bm = new MaxBitmap();
        var fc = b.First(0x5010);
        var fn = fc == null ? null : fc.First(0x1230);
        if (fn != null)
        {
            string full = fn.Text();
            int cut = Math.Max(full.LastIndexOf('\\'), full.LastIndexOf('/'));
            bm.File = cut >= 0 ? full.Substring(cut + 1) : full;
        }
        var pl = Ref(b, 0);
        if (pl != null && pl.IsContainer)
        {
            var fl = pl.First(0x9002);
            if (fl != null && !fl.IsContainer && fl.Length >= 4)
            {
                // bits 0 and 1 are the tile switches (held on the samples' decals); bits 2 and 3 are TAKEN for the mirror
                // switches, the order 3ds Max's own names for them have (U_WRAP, V_WRAP, U_MIRROR, V_MIRROR) - no sample
                // sets either: the four models hold 3 (41 times) or 0 (17 times) in these four bits. A mirrored map
                // repeats, so it is NOT a decal: read as plain tiling, and said, since every second repeat is then
                // the wrong way round.
                uint f = fl.U32(0);
                bm.MirrorU = (f & 4) != 0; bm.MirrorV = (f & 8) != 0;
                bm.TileU = (f & 1) != 0 || bm.MirrorU; bm.TileV = (f & 2) != 0 || bm.MirrorV;
            }
            var pb = Ref(pl, 0);
            if (pb != null && pb.IsContainer)
                foreach (var k in pb.All(0x0002))
                {
                    var ix = k.First(0x0003); var v = Floats(k, 0x0100, 1);
                    if (ix == null || ix.IsContainer || ix.Length < 4 || v == null) continue;
                    switch (ix.U32(0))
                    {
                        case 0: bm.OffsetU = v[0]; break;
                        case 1: bm.OffsetV = v[0]; break;
                        case 2: bm.TilingU = v[0]; break;
                        case 3: bm.TilingV = v[0]; break;
                        case 4: bm.Angle = v[0]; break;
                    }
                }
        }
        // an offset or tiling that is no number (or an absurd one) would make every UV of the material one: the
        // picture is then placed as if it had none, and the report says so - one bad number must not refuse the file
        foreach (double v in new[] { bm.OffsetU, bm.OffsetV, bm.TilingU, bm.TilingV, bm.Angle })
            if (double.IsNaN(v) || Math.Abs(v) > MaxUv) bm.PlacementLeft = true;
        if (bm.PlacementLeft) { bm.OffsetU = bm.OffsetV = bm.Angle = 0; bm.TilingU = bm.TilingV = 1; }
        return bm;
    }
}
