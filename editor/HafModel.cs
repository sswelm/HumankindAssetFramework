using System.Collections.Generic;

// THE IN-MEMORY MODEL (2026-09-30, step 1 of replacing Blender - docs/Review-Backlog.md "Replace Blender with our own
// code"). Everything the pipeline's later steps consume - the decimator, the bone-per-part rig, the rest-pose fold,
// the clip slicing, the vehicle rigs - reads THIS, never a file format. It is the union of what a glTF 2.0 file
// carries that HAF uses (the registry's 35 of 37 recipes point at a GLB; FBX/OBJ sources reach the pipeline as a GLB
// today, through prep_model): a node hierarchy with transforms, meshes made of primitives with every vertex attribute,
// PBR materials with their textures and images, skins with inverse bind matrices, and animations as sampled curves.
// Indices between the lists are the file's own (a node's Mesh is an index into Meshes, a channel's Node into Nodes).
//
// Pure C#: no Unity, no Newtonsoft. The reader (GlbReader) fills it; the tests build one from a synthetic file and
// read every field back; the parity drill reads every registry GLB with it and with Blender and compares the counts.
// Arrays hold the file's numbers as the file holds them (float32 attributes, double transforms) - conversions are the
// consumer's, said where they happen.

public sealed class HafModel
{
    public string Generator = "";                 // asset.generator, for the log
    public string Copyright = "";                 // asset.copyright, verbatim
    public string AssetExtrasJson;                // asset.extras, verbatim JSON (any type the file gave - the schema allows any; `{}` included) or null when absent: Sketchfab's author/license/source/title live here (6 registry files; review of PR #110 found them dropped)
    public string SourcePath = "";                // where it was read from ("" for bytes)
    // EVERY scene the file declares, in order, and nothing the file does not (review of PR #111: the writer kept the default
    // one and dropped the rest without a word, and a reader-vs-reader compare could not see it). Scene = the default one, the
    // file's `scene`, or -1 when it names none: the specification gives an absent default a meaning of its own (a viewer
    // renders nothing at load), so it is carried as absent, not as 0.
    public readonly List<HafScene> Scenes = new List<HafScene>();
    public int Scene = -1;
    public bool HasDefaultScene => Scene >= 0 && Scene < Scenes.Count;
    public readonly List<HafNode> Nodes = new List<HafNode>();
    /// <summary>What a viewer shows at load: the default scene's root nodes, or none when the file names no default
    /// scene (the specification's reading; Blender imports every node regardless, and the drill counts as Blender does).
    /// Computed - to change it, change the scene.</summary>
    public IReadOnlyList<int> Roots => HasDefaultScene ? (IReadOnlyList<int>)Scenes[Scene].Nodes : new int[0];

    /// <summary>Every node reachable from a scene's roots through Children (the nodes a viewer of that scene draws);
    /// an index outside the scenes gives none. Cycles are tolerated here (the reader and writer refuse them).</summary>
    public HashSet<int> NodesInScene(int scene)
    {
        var seen = new HashSet<int>();
        if (scene < 0 || scene >= Scenes.Count) return seen;
        var stack = new Stack<int>(Scenes[scene].Nodes);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (i < 0 || i >= Nodes.Count || !seen.Add(i)) continue;
            foreach (var c in Nodes[i].Children) stack.Push(c);
        }
        return seen;
    }
    public readonly List<HafMesh> Meshes = new List<HafMesh>();
    public readonly List<HafMaterial> Materials = new List<HafMaterial>();
    public readonly List<HafTexture> Textures = new List<HafTexture>();
    public readonly List<HafImage> Images = new List<HafImage>();
    public readonly List<string> Samplers = new List<string>();   // the file's texture samplers, each the verbatim JSON object (wrap, filters): carried, not interpreted (review of PR #110: 28 non-default samplers in the registry were flattened)
    // the file's cameras, each the verbatim JSON object: carried, not interpreted. A node names one by index (HafNode.Camera).
    // Review of PR #112: they were dropped on a round trip without a word (Duck.glb has one), and a camera object takes a
    // NAME in Blender's pool that the names computation needs.
    public readonly List<string> Cameras = new List<string>();
    public readonly List<HafSkin> Skins = new List<HafSkin>();
    public readonly List<HafAnimation> Animations = new List<HafAnimation>();
    public readonly List<string> ExtensionsUsed = new List<string>();
    // The extensions the file REQUIRES that the reader accepted: only those whose whole effect is a material's
    // payload, which is carried verbatim (KHR_materials_pbrSpecularGlossiness on a Lab source, 2026-10-02). The
    // geometry, skins and animations of such a file are what the specification's core says; what its materials look
    // like is in HafMaterial.ExtensionsJson for whoever draws them. The writer writes these back as required.
    public readonly List<string> ExtensionsRequired = new List<string>();

    public long TriangleCount
    {
        get { long n = 0; foreach (var m in Meshes) foreach (var p in m.Primitives) n += p.TriangleCount; return n; }
    }
    public long VertexCount
    {
        get { long n = 0; foreach (var m in Meshes) foreach (var p in m.Primitives) n += p.VertexCount; return n; }
    }
}

public sealed class HafScene
{
    public string Name = "";
    public readonly List<int> Nodes = new List<int>();   // root nodes (a node without a parent); two scenes may share one
    public string ExtrasJson;                            // verbatim JSON of any type, or null when absent
}

public sealed class HafNode
{
    public string Name = "";
    public int Parent = -1;
    public readonly List<int> Children = new List<int>();
    // TRS as the file gives them (glTF defaults when absent); Matrix set only when the file gave a matrix instead.
    public double[] Translation = { 0, 0, 0 };
    public double[] Rotation = { 0, 0, 0, 1 };    // quaternion x, y, z, w
    public double[] Scale = { 1, 1, 1 };
    public double[] Matrix;                       // 16 doubles, column-major, or null
    public int Mesh = -1;
    public int Skin = -1;
    public int Camera = -1;                       // index into HafModel.Cameras, or -1
    public string ExtrasJson;                     // the node's `extras`, verbatim JSON of any type, or null when absent: carried, not interpreted
    public bool HasMatrix => Matrix != null;
}

public sealed class HafMesh
{
    public string Name = "";
    public string ExtrasJson;                     // the mesh's `extras`, verbatim JSON of any type, or null when absent
    public readonly List<HafPrimitive> Primitives = new List<HafPrimitive>();
}

public sealed class HafPrimitive
{
    public int Mode = 4;                          // glTF primitive mode; 4 = TRIANGLES
    public int Material = -1;
    public int VertexCount;
    // The arrays below may be SHARED with other primitives: the reader decodes an accessor once, and a file whose parts
    // reference one vertex accessor (every Workshop split) gives them one array. Read them freely; to edit one, replace it
    // with a copy. The writer writes a shared array once.
    public float[] Positions;                     // 3 per vertex; never null for a primitive that was read
    public float[] Normals;                       // 3 per vertex, or null
    public float[] Tangents;                      // 4 per vertex (xyz + handedness), or null
    public float[] Uv0, Uv1;                      // 2 per vertex, or null
    public float[] Colors;                        // 4 per vertex (RGB padded with alpha 1), or null
    public ushort[] Joints;                       // 4 per vertex, or null (JOINTS_0)
    public float[] Weights;                       // 4 per vertex, or null (WEIGHTS_0)
    public ushort[] Joints1;                      // influences 5-8 (JOINTS_1 / WEIGHTS_1), or null; a third set is refused by the reader
    public float[] Weights1;
    public int[] Indices;                         // or null = non-indexed (vertex order)
    public int MorphTargets;                      // targets the file declared; their data is NOT read (said, not silently dropped)
    public bool Skinned => Joints != null && Weights != null;
    public long TriangleCount
    {
        get
        {
            long n = Indices != null ? Indices.Length : VertexCount;
            switch (Mode) { case 4: return n / 3; case 5: case 6: return n < 3 ? 0 : n - 2; default: return 0; }
        }
    }

    /// <summary>The triangles this primitive draws, as vertex indices with the drawn winding: TRIANGLES in threes, a
    /// strip (mode 5) alternating so every triangle faces the same way, a fan (mode 6) around its first vertex; lines
    /// and points draw none. One definition for the drill, the preview and whatever counts faces next.</summary>
    public IEnumerable<(int a, int b, int c)> Triangles()
    {
        int count = Indices != null ? Indices.Length : VertexCount;
        int At(int i) => Indices != null ? Indices[i] : i;
        if (Mode == 4) for (int t = 0; t + 2 < count; t += 3) yield return (At(t), At(t + 1), At(t + 2));
        else if (Mode == 5) for (int t = 0; t + 2 < count; t++) yield return t % 2 == 0 ? (At(t), At(t + 1), At(t + 2)) : (At(t + 1), At(t), At(t + 2));
        else if (Mode == 6) for (int t = 1; t + 1 < count; t++) yield return (At(0), At(t), At(t + 1));
    }
}

public sealed class HafMaterial
{
    public string Name = "";
    public float[] BaseColorFactor = { 1, 1, 1, 1 };
    public int BaseColorTexture = -1; public int BaseColorTexCoord;
    public float MetallicFactor = 1, RoughnessFactor = 1;
    public int MetallicRoughnessTexture = -1; public int MetallicRoughnessTexCoord;
    public int NormalTexture = -1; public int NormalTexCoord; public float NormalScale = 1;
    public int OcclusionTexture = -1; public int OcclusionTexCoord; public float OcclusionStrength = 1;
    public int EmissiveTexture = -1; public int EmissiveTexCoord;
    public float[] EmissiveFactor = { 0, 0, 0 };
    public string AlphaMode = "OPAQUE";
    public float AlphaCutoff = 0.5f;
    public bool DoubleSided;
    // The material's `extensions` object, verbatim JSON, or null: KHR_materials_specular, clearcoat, ior, ... carry their
    // own texture references and factors. Not interpreted here (nothing in HAF reads them), but CARRIED, so a file
    // written from this model keeps them and Blender still finds every texture (writer drill 2026-10-01: six registry
    // files lost 1-6 images on a round trip while the payload was dropped). The writer declares the names it carries.
    public string ExtensionsJson;
    public string ExtrasJson;                     // the material's `extras`, verbatim JSON of any type, or null when absent
}

public sealed class HafTexture
{
    public string Name = "";
    public int Source = -1;                       // index into Images
    public int Sampler = -1;                      // the file's sampler index (contents not modelled)
}

public sealed class HafImage
{
    public string Name = "";
    public string MimeType = "";
    public string Uri = "";                       // as the file gave it (a data URI is decoded into Bytes and left here truncated)
    public byte[] Bytes;                          // the encoded image (PNG/JPEG bytes), or null when a uri could not be resolved
}

public sealed class HafSkin
{
    public string Name = "";
    public int[] Joints;                          // node indices
    public double[] InverseBindMatrices;          // 16 per joint, column-major, or null (identity)
    public int Skeleton = -1;                     // root node, or -1
}

public sealed class HafAnimation
{
    public string Name = "";
    public readonly List<HafSampler> Samplers = new List<HafSampler>();
    public readonly List<HafChannel> Channels = new List<HafChannel>();
    public double Duration;                       // the largest input time over every sampler, seconds
}

public sealed class HafSampler
{
    public float[] Times;                         // input, seconds, ascending
    public float[] Values;                        // output; Components per key (3× per key for CUBICSPLINE: in-tangent, value, out-tangent)
    public int Components;                        // 3 translation/scale, 4 rotation, n morph weights
    public string Interpolation = "LINEAR";       // LINEAR, STEP, CUBICSPLINE
    public int KeyCount => Times == null ? 0 : Times.Length;
}

public sealed class HafChannel
{
    public int Sampler;
    public int Node = -1;                         // -1 = the file gave no target node (allowed by the spec; extensions)
    public string Path = "";                      // translation, rotation, scale, weights
}
