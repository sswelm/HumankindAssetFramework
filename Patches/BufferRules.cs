using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HumankindAssetFramework
{
    // [Buffers] BufferOverrides, the pure half (Tests/BufferRulesTests.cs). The format is
    //   "<layerName>:verts=+N,idx=+N,meshes=+N,maxtris=N;<layerName>:..."
    // A key names ONE layer, exactly (case-insensitive); a trailing '*' makes it a prefix. Until 2026-10-31 the key was a
    // SUBSTRING, so "MeshWithSkeleton" also enlarged the layer 'MeshWithSkeleton' beside the pawn layer
    // 'MeshWithSkeletonParticleIndexBuffer' (700k -> 3.7M vertices of VRAM for nothing), and a key that matched no
    // layer - a typo - was dropped without a word. Problems are reported, not skipped; a key no layer carries is
    // reported once the layers exist (Unmatched).
    public static class BufferRules
    {
        // THE 24-BIT START INDEX: the encoded mesh handle is StartIndex | (particleCount << 24)
        // (Amplitude.Graphics FxComponentMeshContentManager.GetEncodedMeshAndVisualParticleCount), and the start index is
        // NOT masked in the encode, so an index buffer past this would corrupt the count byte rather than clamp.
        public const int IndexCeiling = 16_777_216;

        public sealed class Override
        {
            public string Layer; public bool Prefix;
            public int Verts, Idx, Meshes;   // added to the layer's sizes (0 = untouched)
            public int MaxTris = -1;         // set absolutely; -1 = untouched (0 = unlimited)
            public string Key => Prefix ? Layer + "*" : Layer;
        }

        public static List<Override> Parse(string spec, List<string> problems)
        {
            var list = new List<Override>();
            foreach (var raw in (spec ?? "").Split(';'))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                int colon = part.IndexOf(':');
                if (colon < 0) { problems.Add($"'{part}': expected <layerName>:verts=+N,idx=+N,meshes=+N,maxtris=N"); continue; }
                string key = part.Substring(0, colon).Trim();
                var o = new Override { Prefix = key.EndsWith("*", StringComparison.Ordinal), Layer = key.TrimEnd('*').Trim() };
                if (o.Layer.Length == 0) { problems.Add($"'{part}': the layer name is empty"); continue; }
                bool any = false;
                foreach (var kv in part.Substring(colon + 1).Split(','))
                {
                    string field = kv.Trim();
                    if (field.Length == 0) continue;
                    var p = field.Split('=');
                    if (p.Length != 2 || !int.TryParse(p[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                    { problems.Add($"'{key}': '{field}' is not <field>=<number>"); continue; }
                    switch (p[0].Trim().ToLowerInvariant())
                    {
                        case "verts":   o.Verts = val; break;
                        case "idx":     o.Idx = val; break;
                        case "meshes":  o.Meshes = val; break;
                        case "maxtris":
                            if (val < 0) { problems.Add($"'{key}': maxtris must be zero (unlimited) or positive"); continue; }
                            o.MaxTris = val; break;
                        default: problems.Add($"'{key}': unknown field '{p[0].Trim()}' (verts, idx, meshes, maxtris)"); continue;
                    }
                    any = true;
                }
                if (any) list.Add(o); else problems.Add($"'{key}': nothing to apply");
            }
            return list;
        }

        public static bool Matches(Override o, string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return false;
            return o.Prefix ? layerName.StartsWith(o.Layer, StringComparison.OrdinalIgnoreCase)
                            : string.Equals(layerName, o.Layer, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The keys that name no layer among <paramref name="layerNames"/> - a typo, or a layer this game does not create.</summary>
        public static List<string> Unmatched(IEnumerable<Override> overrides, IEnumerable<string> layerNames)
        {
            var names = layerNames.Where(n => n != null).ToList();
            return overrides.Where(o => !names.Any(n => Matches(o, n))).Select(o => o.Key).ToList();
        }

        /// <summary>The index buffer size after an addition, held under the 24-bit ceiling; <paramref name="note"/> says when it was.</summary>
        public static int ClampedIndexSize(int current, int extra, out string note)
        {
            long want = (long)current + extra;
            if (want < 1 && extra != 0) { note = "an index buffer must have a positive size: override not applied"; return current; }
            if (want <= IndexCeiling) { note = null; return (int)Math.Max(0, want); }
            note = $"an index buffer of {want:n0} would pass the 24-bit start-index ceiling ({IndexCeiling:n0}), which the encode does not mask: clamped to the ceiling";
            return IndexCeiling;
        }

        public static int AddedSize(int current, int extra, out string note)
        {
            long want = (long)current + extra;
            if (want < 1 || want > int.MaxValue)
            { note = $"size {want} is outside the positive Int32 range: override not applied"; return current; }
            note = null; return (int)want;
        }
    }
}
