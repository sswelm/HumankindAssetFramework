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
    public string SceneName = "";                 // the default scene's name (Blender writes "Scene" in every file)
    public string SourcePath = "";                // where it was read from ("" for bytes)
    public readonly List<HafNode> Nodes = new List<HafNode>();
    public readonly List<int> Roots = new List<int>();          // the default scene's root nodes (every node without a parent when no scene is declared)
    public readonly List<HafMesh> Meshes = new List<HafMesh>();
    public readonly List<HafMaterial> Materials = new List<HafMaterial>();
    public readonly List<HafTexture> Textures = new List<HafTexture>();
    public readonly List<HafImage> Images = new List<HafImage>();
    public readonly List<string> Samplers = new List<string>();   // the file's texture samplers, each the verbatim JSON object (wrap, filters): carried, not interpreted (review of PR #110: 28 non-default samplers in the registry were flattened)
    public readonly List<HafSkin> Skins = new List<HafSkin>();
    public readonly List<HafAnimation> Animations = new List<HafAnimation>();
    public readonly List<string> ExtensionsUsed = new List<string>();

    public long TriangleCount
    {
        get { long n = 0; foreach (var m in Meshes) foreach (var p in m.Primitives) n += p.TriangleCount; return n; }
    }
    public long VertexCount
    {
        get { long n = 0; foreach (var m in Meshes) foreach (var p in m.Primitives) n += p.VertexCount; return n; }
    }
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
