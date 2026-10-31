using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace HumankindAssetFramework
{
    // Separate from the Harmony-attributed adapter so ordinary tests can exercise engine-shaped records directly.
    internal static class MeshDropWatch
    {
        internal sealed class Snapshot
        {
            internal int Slot, Vertices, VertexMax, Indices, IndexMax, MeshMax;
            internal long WantedVertices, WantedIndices;
            internal string Asset;
        }
        internal sealed class Drop
        {
            internal int Slot;
            internal string Name, Reason, Asset;
            internal bool Capacity, Stored;
        }
        sealed class State
        {
            internal WeakReference Buffer;
            internal readonly Dictionary<string, Drop> Entries = new Dictionary<string, Drop>();
        }
        [ProcessLived("weak layer keys, with refusal records scoped to the current mesh-buffer generation")]
        static readonly ConditionalWeakTable<object, State> states = new ConditionalWeakTable<object, State>();

        static int Number(object o) => o is int n ? n : -1;
        static object Member(object o, string name) => UniversalInject.GetMember(o, name);
        static object Buffer(object layer) => Member(layer, "hxFxOneMeshComputeBuffer");
        static Array Table(object buffer) => Member(buffer, "WriteContent") as Array;
        static State Current(object layer, object buffer)
        {
            var state = states.GetValue(layer, _ => new State());
            lock (state)
            {
                if (state.Buffer == null || !ReferenceEquals(state.Buffer.Target, buffer))
                { state.Entries.Clear(); state.Buffer = new WeakReference(buffer); }
            }
            return state;
        }

        internal static Snapshot Before(object layer, object input, int indexChunk = 64)
        {
            var table = Table(Buffer(layer));
            long quads = Number(Member(input, "QuadCount")), vertices = Number(Member(input, "VertexCount"));
            long indices = quads < 0 || indexChunk <= 0 ? -1 : ((quads * 4 + indexChunk - 1) / indexChunk) * indexChunk;
            return new Snapshot {
                Slot = Number(Member(layer, "currentMeshAddedCount")),
                Vertices = Number(Member(layer, "currentVertexIndex")), VertexMax = Number(Member(Member(layer, "vertexBuffer"), "Size")),
                Indices = Number(Member(layer, "currentIndexIndex")), IndexMax = Number(Member(Member(layer, "indexBuffer"), "Size")),
                MeshMax = table?.Length ?? -1, WantedVertices = vertices, WantedIndices = indices,
                Asset = Member(input, "Guid")?.ToString()
            };
        }

        internal static Drop[] Pending(object layer)
        {
            if (layer == null) return new Drop[0];
            object buffer = Buffer(layer); var table = Table(buffer);
            if (table == null) { states.Remove(layer); return new Drop[0]; }
            var state = Current(layer, buffer);
            lock (state)
            {
                // Repairing an entry resolves its failure. A copied/grown CPU array alone does not resolve it.
                foreach (var key in state.Entries.Where(kv => kv.Value.Stored && kv.Value.Slot >= 0 && kv.Value.Slot < table.Length
                    && Number(Member(table.GetValue(kv.Value.Slot), "VertexCount")) > 0).Select(kv => kv.Key).ToArray())
                    state.Entries.Remove(key);
                return state.Entries.Values.ToArray();
            }
        }

        internal static void After(int result, object layer, string meshName, Snapshot before, Exception failure = null)
        {
            try
            {
                object buffer = Buffer(layer); var table = Table(buffer);
                if (table == null || before == null) return;
                int slot = failure == null ? result : before.Slot;
                if (failure == null && slot >= 0 && slot < table.Length)
                {
                    int count = Number(Member(table.GetValue(slot), "VertexCount"));
                    if (count > 0)
                    {
                        var healthy = Current(layer, buffer);
                        lock (healthy)
                            foreach (var resolved in healthy.Entries.Where(kv => !kv.Value.Stored && kv.Value.Asset == (before.Asset ?? meshName)).Select(kv => kv.Key).ToArray())
                                healthy.Entries.Remove(resolved);
                        Pending(layer); return;
                    }
                    if (count < 0) { Plugin.Log?.LogWarning("[Buffers] drop watch cannot inspect VertexCount; registration outcome is unknown."); return; }
                }
                string reason;
                bool capacity = true;
                if (slot >= table.Length) reason = "the mesh table is full (maxMeshCount)";
                else if (slot < 0) { capacity = false; reason = "registration returned an invalid mesh index"; }
                else if (before.Vertices >= 0 && before.VertexMax > 0 && before.WantedVertices >= 0 && before.Vertices + before.WantedVertices > before.VertexMax)
                    reason = "the vertex buffer cannot hold the requested mesh";
                else if (before.Indices >= 0 && before.IndexMax > 0 && before.WantedIndices >= 0 && before.Indices + before.WantedIndices > before.IndexMax)
                    reason = "the index buffer cannot hold the requested mesh";
                else { capacity = false; reason = failure == null ? "the encoder rejected the mesh data" : "registration threw " + failure.GetType().Name; }
                var drop = new Drop { Slot = slot, Name = meshName ?? "?", Reason = reason, Capacity = capacity,
                    Asset = before.Asset ?? meshName, Stored = failure == null && slot >= 0 && slot < table.Length };
                var state = Current(layer, buffer);
                string key = slot + ":" + (before.Asset ?? drop.Name);
                lock (state)
                {
                    if (state.Entries.ContainsKey(key)) return;   // do not flood the log when a full table is retried
                    state.Entries.Add(key, drop);
                }
                string name = Member(layer, "name") as string ?? "?";
                string remedy = capacity ? $"Raise [Buffers] BufferOverrides for '{name}' or reduce the roster."
                    : "Inspect the source mesh and encoder diagnostics; increasing GPU buffers does not repair mesh data.";
                Plugin.Log?.LogError($"[Buffers][DROPPED] mesh '{drop.Name}' was NOT stored in layer '{name}' ({reason}) and will not draw. " +
                    $"Fill before registration: verts {before.Vertices:n0}/{before.VertexMax:n0}, idx {before.Indices:n0}/{before.IndexMax:n0}, meshes {before.Slot}/{before.MeshMax}. {remedy}");
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[Buffers] drop watch: " + ex.Message); }
        }
    }
}
