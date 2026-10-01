using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Two models compared FIELD BY FIELD - every node, every vertex of every attribute array, every index, every
/// material field, texture, sampler, image byte, skin matrix, animation key, the asset's extras, the scene name - and
/// the first difference named. The writer drill runs it on every file it writes (review of PR #110: value summaries
/// could not see a dropped sampler setting or a lost extras object); the Model Reader's Save-as reads its output back
/// through it and says so; the headless editor lane runs it in Unity's own runtime.</summary>
public static class HafModelDiff
{
    /// <summary>The first field on which the written-and-read model differs from the original, or null. Generator (the
    /// writer stamps its own), an image's Uri (an embedded image has none) and an image's MimeType the source never
    /// declared (the writer must) are the fields allowed to differ;
    /// extensionsUsed may shrink to what is carried (a name the source declared without any payload) but never grow.</summary>
    public static string FirstDifference(HafModel a, HafModel b)
    {
        equalPairs = null;   // per comparison: the arrays of a model that was edited since are compared afresh
        try { return Compare(a, b); }
        finally { equalPairs = null; }   // and not kept alive after it: the set holds both models' arrays
    }

    static string Compare(HafModel a, HafModel b)
    {
        string d;
        if ((d = Count("nodes", a.Nodes.Count, b.Nodes.Count)) != null) return d;
        if ((d = Count("meshes", a.Meshes.Count, b.Meshes.Count)) != null) return d;
        if ((d = Count("materials", a.Materials.Count, b.Materials.Count)) != null) return d;
        if ((d = Count("textures", a.Textures.Count, b.Textures.Count)) != null) return d;
        if ((d = Count("images", a.Images.Count, b.Images.Count)) != null) return d;
        if ((d = Count("samplers", a.Samplers.Count, b.Samplers.Count)) != null) return d;
        if ((d = Count("skins", a.Skins.Count, b.Skins.Count)) != null) return d;
        if ((d = Count("animations", a.Animations.Count, b.Animations.Count)) != null) return d;
        if ((d = Count("scenes", a.Scenes.Count, b.Scenes.Count)) != null) return d;
        if (a.Scene != b.Scene) return $"default scene: {a.Scene} -> {b.Scene}";
        for (int i = 0; i < a.Scenes.Count; i++)
        {
            HafScene x = a.Scenes[i], y = b.Scenes[i];
            if (x.Name != y.Name) return $"scene {i} name: {x.Name} -> {y.Name}";
            if (!x.Nodes.SequenceEqual(y.Nodes)) return $"scene {i} ({x.Name}) nodes";
            if (x.ExtrasJson != y.ExtrasJson) return $"scene {i} ({x.Name}) extras";
        }
        if (a.Copyright != b.Copyright) return "asset copyright";
        if (a.AssetExtrasJson != b.AssetExtrasJson) return $"asset extras: {a.AssetExtrasJson} -> {b.AssetExtrasJson}";
        foreach (var e in b.ExtensionsUsed) if (!a.ExtensionsUsed.Contains(e)) return $"extensionsUsed gained {e}";
        if (!a.ExtensionsRequired.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.ExtensionsRequired.OrderBy(x => x, StringComparer.Ordinal))) return $"extensionsRequired: [{string.Join(", ", a.ExtensionsRequired)}] -> [{string.Join(", ", b.ExtensionsRequired)}]";
        for (int i = 0; i < a.Nodes.Count; i++)
        {
            HafNode x = a.Nodes[i], y = b.Nodes[i]; string w = $"node {i} ({x.Name})";
            if (x.Name != y.Name) return w + " name";
            if (x.Parent != y.Parent) return w + " parent";
            if (!x.Children.SequenceEqual(y.Children)) return w + " children";
            if (x.Mesh != y.Mesh) return w + " mesh";
            if (x.Skin != y.Skin) return w + " skin";
            if (x.HasMatrix != y.HasMatrix) return w + " matrix presence";
            if (x.HasMatrix ? !x.Matrix.SequenceEqual(y.Matrix) : !(x.Translation.SequenceEqual(y.Translation) && x.Rotation.SequenceEqual(y.Rotation) && x.Scale.SequenceEqual(y.Scale))) return w + " transform";
            if (x.ExtrasJson != y.ExtrasJson) return w + " extras";
        }
        for (int i = 0; i < a.Meshes.Count; i++)
        {
            HafMesh x = a.Meshes[i], y = b.Meshes[i]; string w = $"mesh {i} ({x.Name})";
            if (x.Name != y.Name) return w + " name";
            if (x.ExtrasJson != y.ExtrasJson) return w + " extras";
            if ((d = Count(w + " primitives", x.Primitives.Count, y.Primitives.Count)) != null) return d;
            for (int k = 0; k < x.Primitives.Count; k++)
            {
                HafPrimitive p = x.Primitives[k], q = y.Primitives[k]; string pw = $"{w} primitive {k}";
                if (p.Mode != q.Mode) return pw + " mode";
                if (p.Material != q.Material) return pw + " material";
                if (p.VertexCount != q.VertexCount) return pw + " vertex count";
                if (p.MorphTargets != q.MorphTargets) return pw + " morph targets";
                if ((d = Arr(pw + " POSITION", p.Positions, q.Positions)) != null) return d;
                if ((d = Arr(pw + " NORMAL", p.Normals, q.Normals)) != null) return d;
                if ((d = Arr(pw + " TANGENT", p.Tangents, q.Tangents)) != null) return d;
                if ((d = Arr(pw + " TEXCOORD_0", p.Uv0, q.Uv0)) != null) return d;
                if ((d = Arr(pw + " TEXCOORD_1", p.Uv1, q.Uv1)) != null) return d;
                if ((d = Arr(pw + " COLOR_0", p.Colors, q.Colors)) != null) return d;
                if ((d = Arr(pw + " JOINTS_0", p.Joints, q.Joints)) != null) return d;
                if ((d = Arr(pw + " WEIGHTS_0", p.Weights, q.Weights)) != null) return d;
                if ((d = Arr(pw + " JOINTS_1", p.Joints1, q.Joints1)) != null) return d;
                if ((d = Arr(pw + " WEIGHTS_1", p.Weights1, q.Weights1)) != null) return d;
                if ((d = Arr(pw + " indices", p.Indices, q.Indices)) != null) return d;
            }
        }
        for (int i = 0; i < a.Materials.Count; i++)
        {
            HafMaterial x = a.Materials[i], y = b.Materials[i]; string w = $"material {i} ({x.Name})";
            if (x.Name != y.Name) return w + " name";
            if (!x.BaseColorFactor.SequenceEqual(y.BaseColorFactor)) return w + " baseColorFactor";
            if (x.BaseColorTexture != y.BaseColorTexture || x.BaseColorTexCoord != y.BaseColorTexCoord) return w + " baseColorTexture";
            if (x.MetallicFactor != y.MetallicFactor || x.RoughnessFactor != y.RoughnessFactor) return w + " metallic/roughness";
            if (x.MetallicRoughnessTexture != y.MetallicRoughnessTexture || x.MetallicRoughnessTexCoord != y.MetallicRoughnessTexCoord) return w + " metallicRoughnessTexture";
            if (x.NormalTexture != y.NormalTexture || x.NormalTexCoord != y.NormalTexCoord || x.NormalScale != y.NormalScale) return w + " normalTexture";
            if (x.OcclusionTexture != y.OcclusionTexture || x.OcclusionTexCoord != y.OcclusionTexCoord || x.OcclusionStrength != y.OcclusionStrength) return w + " occlusionTexture";
            if (x.EmissiveTexture != y.EmissiveTexture || x.EmissiveTexCoord != y.EmissiveTexCoord) return w + " emissiveTexture";
            if (!x.EmissiveFactor.SequenceEqual(y.EmissiveFactor)) return w + " emissiveFactor";
            if (x.AlphaMode != y.AlphaMode) return w + " alphaMode";
            if (x.AlphaCutoff != y.AlphaCutoff) return w + " alphaCutoff";
            if (x.DoubleSided != y.DoubleSided) return w + " doubleSided";
            if (x.ExtensionsJson != y.ExtensionsJson) return w + " extensions";
            if (x.ExtrasJson != y.ExtrasJson) return w + " extras";
        }
        for (int i = 0; i < a.Textures.Count; i++)
        {
            HafTexture x = a.Textures[i], y = b.Textures[i];
            if (x.Name != y.Name || x.Source != y.Source || x.Sampler != y.Sampler) return $"texture {i}";
        }
        for (int i = 0; i < a.Samplers.Count; i++) if (a.Samplers[i] != b.Samplers[i]) return $"sampler {i}: {a.Samplers[i]} -> {b.Samplers[i]}";
        for (int i = 0; i < a.Images.Count; i++)
        {
            HafImage x = a.Images[i], y = b.Images[i]; string w = $"image {i} ({x.Name})";
            if (x.Name != y.Name) return w + " name";
            // a .gltf image by uri declares no type; the writer embeds it and must say the one the bytes are (PNG or JPEG by signature)
            if (x.MimeType != y.MimeType && !(x.MimeType.Length == 0 && y.MimeType == MimeOf(x.Bytes))) return w + $" mimeType '{x.MimeType}' -> '{y.MimeType}' (the bytes are {MimeOf(x.Bytes) ?? "neither PNG nor JPEG"})";
            if ((d = Arr(w + " bytes", x.Bytes, y.Bytes)) != null) return d;
        }
        for (int i = 0; i < a.Skins.Count; i++)
        {
            HafSkin x = a.Skins[i], y = b.Skins[i]; string w = $"skin {i} ({x.Name})";
            if (x.Name != y.Name) return w + " name";
            if (x.Skeleton != y.Skeleton) return w + " skeleton";
            if ((d = Arr(w + " joints", x.Joints, y.Joints)) != null) return d;
            if ((d = Arr(w + " inverseBindMatrices", x.InverseBindMatrices, y.InverseBindMatrices)) != null) return d;
        }
        for (int i = 0; i < a.Animations.Count; i++)
        {
            HafAnimation x = a.Animations[i], y = b.Animations[i]; string w = $"animation {i} ({x.Name})";
            if (x.Name != y.Name) return w + " name";
            if (x.Duration != y.Duration) return w + " duration";
            if ((d = Count(w + " samplers", x.Samplers.Count, y.Samplers.Count)) != null) return d;
            if ((d = Count(w + " channels", x.Channels.Count, y.Channels.Count)) != null) return d;
            for (int k = 0; k < x.Samplers.Count; k++)
            {
                HafSampler p = x.Samplers[k], q = y.Samplers[k]; string sw = $"{w} sampler {k}";
                if (p.Components != q.Components) return sw + " components";
                if (p.Interpolation != q.Interpolation) return sw + " interpolation";
                if ((d = Arr(sw + " input", p.Times, q.Times)) != null) return d;
                if ((d = Arr(sw + " output", p.Values, q.Values)) != null) return d;
            }
            for (int k = 0; k < x.Channels.Count; k++)
            {
                HafChannel p = x.Channels[k], q = y.Channels[k];
                if (p.Sampler != q.Sampler || p.Node != q.Node || p.Path != q.Path) return $"{w} channel {k}";
            }
        }
        return null;
    }

    static string Count(string what, int a, int b) => a == b ? null : $"{what}: {a} -> {b}";
    static string MimeOf(byte[] b) => b == null ? null : b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "image/png" : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg" : null;

    static string Arr<T>(string what, T[] a, T[] b) where T : IEquatable<T>
    {
        if (a == null || b == null) return a == b ? null : $"{what}: {(a == null ? "absent" : "present")} -> {(b == null ? "absent" : "present")}";
        if (a.Length != b.Length) return $"{what}: {a.Length} -> {b.Length} values";
        // two primitives may share one array on each side (the reader shares what the file shared): a pair found equal once is equal
        if (equalPairs == null) equalPairs = new HashSet<(object, object)>(PairComparer.Instance);
        if (equalPairs.Contains((a, b))) return null;
        for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return $"{what}[{i}]: {a[i]} -> {b[i]}";
        equalPairs.Add((a, b));
        return null;
    }

    [ThreadStatic] static HashSet<(object, object)> equalPairs;
    sealed class PairComparer : IEqualityComparer<(object, object)>
    {
        public static readonly PairComparer Instance = new PairComparer();
        public bool Equals((object, object) x, (object, object) y) => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);
        public int GetHashCode((object, object) p) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item1) * 397 ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item2);
    }

}
