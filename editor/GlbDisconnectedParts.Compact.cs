// GlbDisconnectedParts.Compact.cs — drop from a GLB what no node can reach any more (2026-10-02).
//
// Every Workshop operation edits the JSON and only ever APPENDS to the BIN: a Split hangs new part meshes under a
// node and takes the node's own mesh away, a Fuse appends one shell and takes the meshes of the parts it fused, a Cut
// and a Delete do the same. The meshes taken away stayed in the file, with their accessors and their bytes. Measured
// on the Lab's own sources: 25 files, 1,394 MB, of which 614 MB was geometry nothing draws — a Split-then-Fuse of the
// Saleg's Revenge is 398 MB of which 276 MB is dead, and the GLB reader ran out of memory decoding it.
//
// So every output is compacted on its way out (Write): the meshes no node uses, the accessors no live mesh, skin or
// animation uses, and the BYTES no live accessor covers are left out, and what remains is renumbered.
//   * NODES are never touched: their indices are what the Workshop's marks and sidecars key on.
//   * Materials, textures, images and samplers are never touched: together they were 3 MB of the 614, and renumbering
//     them would need every material extension's references.
//   * By byte RANGE, not by buffer view: an exporter packs every mesh's indices into one view, so after a Split the
//     dead index ranges sit INSIDE a view that live accessors share (102 MB on the Saleg's Revenge split). A view
//     is cut into the segments its live accessors cover; an interleaved view's overlapping ranges merge and keep
//     their stride. Every live accessor reads exactly the bytes it read before, at an offset of the same alignment.
//   * It either compacts completely or leaves the document exactly as it was: the whole plan is made before anything
//     is changed, and a file this code cannot follow (an extension that may reference accessors or views, a reference
//     that points nowhere) is REFUSED with a line saying why — the operation itself still succeeds, uncompacted.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

public static partial class GlbDisconnectedParts
{
    /// <summary>What one compaction did, or why it did nothing.</summary>
    public sealed class Compaction
    {
        /// <summary>The document was rewritten without its unreachable data.</summary>
        public bool Changed;
        /// <summary>Non-null: compaction was refused without changing the document — the reason.</summary>
        public string Skipped;
        public int MeshesBefore, MeshesAfter, AccessorsBefore, AccessorsAfter, ViewsBefore, ViewsAfter;
        public long BinBefore, BinAfter;

        public string Line
        {
            get
            {
                var inv = CultureInfo.InvariantCulture;
                if (Skipped != null) return "Not compacted: " + Skipped;
                if (!Changed) return "Nothing to compact: every mesh, accessor and buffer view is used; no unused binary ranges remain.";
                return string.Format(inv, "Compacted: {0} unused mesh(es) and {1} accessor(s) left out; geometry and images {2:0.0} -> {3:0.0} MB.",
                    MeshesBefore - MeshesAfter, AccessorsBefore - AccessorsAfter, BinBefore / 1e6, BinAfter / 1e6);
            }
        }
    }

    sealed class CompactionRefused : Exception { public CompactionRefused(string why) : base(why) { } }

    /// <summary>An extension that cannot reference a mesh, an accessor or a buffer view: with only such extensions in
    /// use, the references this code follows are all there are. Material extensions point at textures; the texture
    /// ones at images; lights at nothing.</summary>
    internal static bool ExtensionCannotReachGeometry(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (name.StartsWith("KHR_materials_", StringComparison.Ordinal)) return true;
        switch (name)
        {
            case "KHR_texture_transform": case "KHR_texture_basisu": case "EXT_texture_webp": case "EXT_texture_avif": case "MSFT_texture_dds":
            case "KHR_lights_punctual": case "KHR_mesh_quantization": case "KHR_xmp_json_ld":
                return true;
        }
        return false;
    }

    static int CompactComponentBytes(int componentType)
    {
        switch (componentType) { case 5120: case 5121: return 1; case 5122: case 5123: return 2; case 5125: case 5126: return 4; }
        throw new CompactionRefused("an accessor has component type " + componentType.ToString(CultureInfo.InvariantCulture) + ", which this tool does not know");
    }

    static int CompactComponentCount(string type)
    {
        switch (type) { case "SCALAR": return 1; case "VEC2": return 2; case "VEC3": return 3; case "VEC4": return 4; case "MAT2": return 4; case "MAT3": return 9; case "MAT4": return 16; }
        throw new CompactionRefused("an accessor has type '" + type + "', which this tool does not know");
    }

    sealed class Segment { public long Start, End; public int NewView; public long NewOffset; }   // Start/End relative to the old view

    /// <summary>Compact the document in place, or leave it exactly as it was (see the file header).</summary>
    static Compaction CompactDocument(Document document)
    {
        var report = new Compaction();
        try { CompactOrRefuse(document, report); }
        catch (CompactionRefused refused) { report.Changed = false; report.Skipped = refused.Message; }
        return report;
    }

    static void CompactOrRefuse(Document document, Compaction report)
    {
        var inv = CultureInfo.InvariantCulture;
        JObject root = document.Root;
        byte[] bin = document.Chunks[document.BinIndex].Data;
        JArray meshes = root["meshes"] as JArray ?? new JArray();
        JArray accessors = root["accessors"] as JArray ?? new JArray();
        JArray views = root["bufferViews"] as JArray ?? new JArray();
        JArray nodes = root["nodes"] as JArray ?? new JArray();
        report.MeshesBefore = report.MeshesAfter = meshes.Count;
        report.AccessorsBefore = report.AccessorsAfter = accessors.Count;
        report.ViewsBefore = report.ViewsAfter = views.Count;
        report.BinBefore = report.BinAfter = bin.Length;

        // ---- 1. what a node can still reach: its mesh; from a live mesh, a skin or an animation, their accessors
        var liveMesh = new bool[meshes.Count];
        foreach (JObject node in nodes.OfType<JObject>())
        {
            JToken mt = node["mesh"];
            if (mt == null || mt.Type == JTokenType.Null) continue;
            int mi = mt.Value<int>();
            if (mi < 0 || mi >= meshes.Count) throw new CompactionRefused("a node references mesh " + mi.ToString(inv) + ", which does not exist");
            liveMesh[mi] = true;
        }
        var liveAccessor = new bool[accessors.Count];
        void Use(JToken reference)
        {
            if (reference == null || reference.Type == JTokenType.Null) return;
            int a = reference.Value<int>();
            if (a < 0 || a >= accessors.Count) throw new CompactionRefused("accessor " + a.ToString(inv) + " is referenced and does not exist");
            liveAccessor[a] = true;
        }
        for (int mi = 0; mi < meshes.Count; mi++)
        {
            if (!liveMesh[mi]) continue;
            foreach (JObject primitive in ((meshes[mi] as JObject)?["primitives"] as JArray ?? new JArray()).OfType<JObject>())
            {
                foreach (JProperty attribute in (primitive["attributes"] as JObject ?? new JObject()).Properties()) Use(attribute.Value);
                Use(primitive["indices"]);
                foreach (JObject target in (primitive["targets"] as JArray ?? new JArray()).OfType<JObject>())
                    foreach (JProperty attribute in target.Properties()) Use(attribute.Value);
            }
        }
        foreach (JObject skin in (root["skins"] as JArray ?? new JArray()).OfType<JObject>()) Use(skin["inverseBindMatrices"]);
        foreach (JObject animation in (root["animations"] as JArray ?? new JArray()).OfType<JObject>())
            foreach (JObject sampler in (animation["samplers"] as JArray ?? new JArray()).OfType<JObject>()) { Use(sampler["input"]); Use(sampler["output"]); }

        // ---- 2. whether we may: an extension that could hold a reference we do not follow
        foreach (string list in new[] { "extensionsUsed", "extensionsRequired" })
            foreach (JToken name in root[list] as JArray ?? new JArray())
                if (!ExtensionCannotReachGeometry((string)name))
                    throw new CompactionRefused("the file uses the extension '" + (string)name + "', which may reference accessors or buffer views in ways this tool does not follow");

        // ---- 3. per buffer view, the byte ranges its live accessors cover (relative to the view's start)
        int viewCount = views.Count;
        var viewOffset = new long[viewCount]; var viewLength = new long[viewCount]; var viewBuffer = new int[viewCount]; var viewStride = new long[viewCount];
        for (int v = 0; v < viewCount; v++)
        {
            var view = views[v] as JObject;
            if (view == null) throw new CompactionRefused("buffer view " + v.ToString(inv) + " is not an object");
            viewBuffer[v] = view["buffer"]?.Value<int>() ?? 0;
            viewOffset[v] = view["byteOffset"]?.Value<long>() ?? 0;
            viewLength[v] = view["byteLength"]?.Value<long>() ?? 0;
            viewStride[v] = view["byteStride"]?.Value<long>() ?? 0;
            if (viewBuffer[v] == 0 && (viewOffset[v] < 0 || viewLength[v] < 0 || viewOffset[v] + viewLength[v] > bin.Length))
                throw new CompactionRefused("buffer view " + v.ToString(inv) + " runs past the binary chunk");
        }
        var whole = new bool[viewCount];
        var ranges = new List<(long start, long end)>[viewCount];
        int View(JToken reference, string owner)
        {
            int v = reference.Value<int>();
            if (v < 0 || v >= viewCount) throw new CompactionRefused(owner + " references buffer view " + v.ToString(inv) + ", which does not exist");
            return v;
        }
        foreach (JObject image in (root["images"] as JArray ?? new JArray()).OfType<JObject>())
            if (image["bufferView"] != null && image["bufferView"].Type != JTokenType.Null) whole[View(image["bufferView"], "an image")] = true;
        var accessorStart = new long[accessors.Count];   // of a live accessor in a view that is cut: its start relative to the view
        for (int a = 0; a < accessors.Count; a++)
        {
            if (!liveAccessor[a]) continue;
            var accessor = accessors[a] as JObject;
            if (accessor == null) throw new CompactionRefused("accessor " + a.ToString(inv) + " is not an object");
            if (accessor["sparse"] is JObject sparse)   // its two views are kept whole: their layout is the sparse block's own
            {
                if (sparse["indices"]?["bufferView"] != null) whole[View(sparse["indices"]["bufferView"], "a sparse accessor")] = true;
                if (sparse["values"]?["bufferView"] != null) whole[View(sparse["values"]["bufferView"], "a sparse accessor")] = true;
            }
            if (accessor["bufferView"] == null || accessor["bufferView"].Type == JTokenType.Null) continue;
            int v = View(accessor["bufferView"], "accessor " + a.ToString(inv));
            int bytes = CompactComponentBytes(accessor.Value<int>("componentType"));
            string type = (string)accessor["type"];
            int element = bytes * CompactComponentCount(type);
            long count = accessor["count"]?.Value<long>() ?? 0;
            // kept whole, never cut: another buffer's view; a matrix of 1- or 2-byte components (its columns are padded,
            // so its size is not components x bytes); an empty accessor (it covers nothing to place it by)
            if (viewBuffer[v] != 0 || count <= 0 || (bytes < 4 && type != null && type.StartsWith("MAT", StringComparison.Ordinal))) { whole[v] = true; continue; }
            long stride = viewStride[v] > 0 ? viewStride[v] : element;
            if (stride < element) throw new CompactionRefused("accessor " + a.ToString(inv) + " has a stride smaller than its element");
            long start = accessor["byteOffset"]?.Value<long>() ?? 0;
            long end = start + stride * (count - 1) + element;
            if (start < 0 || end > viewLength[v]) throw new CompactionRefused("accessor " + a.ToString(inv) + " runs past its buffer view");
            accessorStart[a] = start;
            (ranges[v] ?? (ranges[v] = new List<(long, long)>())).Add((start, end));
        }

        // ---- 4. cut each view into the segments those ranges cover. A segment starts on a multiple of 4 from the
        //         view's start and is placed at an offset with the view's own remainder mod 4: every accessor keeps an
        //         absolute offset of the same alignment, and an offset within its view that is still a multiple of its
        //         component size. Overlapping ranges (an interleaved view; a shared vertex accessor) merge.
        var segments = new List<Segment>[viewCount];
        var newViews = new JArray();
        long cursor = 0;
        for (int v = 0; v < viewCount; v++)
        {
            var list = segments[v] = new List<Segment>();
            if (whole[v]) list.Add(new Segment { Start = 0, End = viewLength[v] });
            else if (ranges[v] != null)
            {
                foreach (var range in ranges[v].OrderBy(r => r.start).ThenBy(r => r.end))
                {
                    long start = range.start & ~3L;
                    if (list.Count > 0 && start <= list[list.Count - 1].End) list[list.Count - 1].End = Math.Max(list[list.Count - 1].End, range.end);
                    else list.Add(new Segment { Start = start, End = range.end });
                }
            }
            foreach (Segment segment in list)
            {
                var view = (JObject)views[v].DeepClone();
                if (viewBuffer[v] == 0)
                {
                    long absolute = viewOffset[v] + segment.Start;
                    while ((cursor & 3) != (absolute & 3)) cursor++;
                    segment.NewOffset = cursor;
                    cursor += segment.End - segment.Start;
                    view["byteOffset"] = segment.NewOffset;
                    view["byteLength"] = segment.End - segment.Start;
                }
                segment.NewView = newViews.Count;
                newViews.Add(view);
            }
        }
        // Referenced meshes/accessors can still leave whole views or binary ranges unused. Decide that there is
        // nothing to remove only after planning the ranges. For otherwise unchanged views, examine their UNION:
        // overlapping views do not waste bytes, and up to three bytes between ranges preserve their alignment.
        bool viewsUnchanged = Enumerable.Range(0, viewCount).All(v => segments[v].Count == 1
            && segments[v][0].Start == 0 && segments[v][0].End == viewLength[v]);
        if (liveMesh.All(l => l) && liveAccessor.All(l => l) && viewsUnchanged)
        {
            long covered = 0;
            bool unusedBytes = false;
            foreach (int v in Enumerable.Range(0, viewCount).Where(v => viewBuffer[v] == 0).OrderBy(v => viewOffset[v]))
            {
                if (viewOffset[v] - covered >= 4) unusedBytes = true;
                covered = Math.Max(covered, viewOffset[v] + viewLength[v]);
            }
            if (!unusedBytes && bin.Length - covered < 4) return;   // only required alignment / final GLB padding remains
        }

        if (cursor <= 0) throw new CompactionRefused("nothing in the binary chunk would remain");
        if (cursor > int.MaxValue - 4) throw new CompactionRefused("the compacted binary chunk would exceed 2 GiB");

        // ---- 5. where each live accessor lands (still nothing changed)
        var accessorView = new int[accessors.Count]; var accessorOffset = new long[accessors.Count];
        for (int a = 0; a < accessors.Count; a++)
        {
            if (!liveAccessor[a]) continue;
            var accessor = (JObject)accessors[a];
            if (accessor["bufferView"] == null || accessor["bufferView"].Type == JTokenType.Null) { accessorView[a] = -1; continue; }
            int v = accessor["bufferView"].Value<int>();
            if (whole[v]) { accessorView[a] = segments[v][0].NewView; accessorOffset[a] = accessor["byteOffset"]?.Value<long>() ?? 0; continue; }
            var list = segments[v];
            int lo = 0, hi = list.Count - 1;   // the last segment starting at or before the accessor
            while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (list[mid].Start <= accessorStart[a]) lo = mid; else hi = mid - 1; }
            accessorView[a] = list[lo].NewView; accessorOffset[a] = accessorStart[a] - list[lo].Start;
        }
        int WholeView(JToken reference) => segments[reference.Value<int>()][0].NewView;

        // ================= the plan is complete; from here on nothing can refuse =================
        var newBin = new byte[(int)cursor];
        for (int v = 0; v < viewCount; v++)
            if (viewBuffer[v] == 0)
                foreach (Segment segment in segments[v])
                    Buffer.BlockCopy(bin, (int)(viewOffset[v] + segment.Start), newBin, (int)segment.NewOffset, (int)(segment.End - segment.Start));

        var accessorMap = new int[accessors.Count]; int liveAccessors = 0;
        for (int a = 0; a < accessors.Count; a++) accessorMap[a] = liveAccessor[a] ? liveAccessors++ : -1;
        var meshMap = new int[meshes.Count]; int liveMeshes = 0;
        for (int mi = 0; mi < meshes.Count; mi++) meshMap[mi] = liveMesh[mi] ? liveMeshes++ : -1;

        for (int a = 0; a < accessors.Count; a++)
        {
            if (!liveAccessor[a]) continue;
            var accessor = (JObject)accessors[a];
            if (accessorView[a] >= 0) { accessor["bufferView"] = accessorView[a]; accessor["byteOffset"] = accessorOffset[a]; }
            if (accessor["sparse"] is JObject sparse)
            {
                if (sparse["indices"]?["bufferView"] != null) sparse["indices"]["bufferView"] = WholeView(sparse["indices"]["bufferView"]);
                if (sparse["values"]?["bufferView"] != null) sparse["values"]["bufferView"] = WholeView(sparse["values"]["bufferView"]);
            }
        }
        foreach (JObject image in (root["images"] as JArray ?? new JArray()).OfType<JObject>())
            if (image["bufferView"] != null && image["bufferView"].Type != JTokenType.Null) image["bufferView"] = WholeView(image["bufferView"]);

        void Renumber(JObject owner, string key) { JToken t = owner[key]; if (t != null && t.Type != JTokenType.Null) owner[key] = accessorMap[t.Value<int>()]; }
        for (int mi = 0; mi < meshes.Count; mi++)
        {
            if (!liveMesh[mi]) continue;
            foreach (JObject primitive in ((meshes[mi] as JObject)?["primitives"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (primitive["attributes"] is JObject attributes) foreach (JProperty attribute in attributes.Properties().ToList()) attributes[attribute.Name] = accessorMap[attribute.Value.Value<int>()];
                Renumber(primitive, "indices");
                foreach (JObject target in (primitive["targets"] as JArray ?? new JArray()).OfType<JObject>())
                    foreach (JProperty attribute in target.Properties().ToList()) target[attribute.Name] = accessorMap[attribute.Value.Value<int>()];
            }
        }
        foreach (JObject skin in (root["skins"] as JArray ?? new JArray()).OfType<JObject>()) Renumber(skin, "inverseBindMatrices");
        foreach (JObject animation in (root["animations"] as JArray ?? new JArray()).OfType<JObject>())
            foreach (JObject sampler in (animation["samplers"] as JArray ?? new JArray()).OfType<JObject>()) { Renumber(sampler, "input"); Renumber(sampler, "output"); }
        foreach (JObject node in nodes.OfType<JObject>())
            if (node["mesh"] != null && node["mesh"].Type != JTokenType.Null) node["mesh"] = meshMap[node["mesh"].Value<int>()];

        var keptAccessors = new JArray(); for (int a = 0; a < accessors.Count; a++) if (liveAccessor[a]) keptAccessors.Add(accessors[a]);
        var keptMeshes = new JArray(); for (int mi = 0; mi < meshes.Count; mi++) if (liveMesh[mi]) keptMeshes.Add(meshes[mi]);
        Replace(root, "accessors", keptAccessors); Replace(root, "meshes", keptMeshes); Replace(root, "bufferViews", newViews);
        if (root["buffers"] is JArray buffers && buffers.Count > 0 && buffers[0] is JObject first) first["byteLength"] = newBin.Length;
        document.Chunks[document.BinIndex].Data = newBin;

        report.Changed = true;
        report.MeshesAfter = keptMeshes.Count; report.AccessorsAfter = keptAccessors.Count; report.ViewsAfter = newViews.Count; report.BinAfter = newBin.Length;
    }

    static void Replace(JObject root, string key, JArray array)
    {
        if (array.Count > 0) root[key] = array;   // an empty array is not valid glTF: the property goes instead
        else root.Remove(key);
    }

    /// <summary>
    /// Compact an EXISTING GLB — one the Workshop wrote before it compacted its own outputs. `Bytes` is set when
    /// something was left out; otherwise it is null and `Details` says why nothing changed (already compact, or refused).
    /// </summary>
    public static Result Compact(byte[] source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        Document document = Parse(source);
        var result = new Result();
        byte[] bytes = Write(document, out Compaction compaction);
        result.Compaction = compaction;
        result.Details.Add(compaction.Line);
        if (!compaction.Changed) return result;
        ValidateOutput(bytes);
        result.Bytes = bytes;
        return result;
    }

    public static Result CompactFile(string inputPath, string outputPath)
    {
        GuardPaths(inputPath, outputPath);
        Result result = Compact(File.ReadAllBytes(inputPath));
        if (result.Bytes != null) File.WriteAllBytes(outputPath, result.Bytes);
        return result;
    }
}
