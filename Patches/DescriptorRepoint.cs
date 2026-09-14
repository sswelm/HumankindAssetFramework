using System;
using System.Collections.Generic;

namespace HumankindAssetFramework
{
    // THE DESCRIPTOR REPOINT, as a pure kernel (coverage tier, 2026-09-14). Two injection sites — the hand prop
    // (`InjectHandProp`) and the multi-mesh overflow chunks (`InjectExtraMeshFragments`) — append fragments to a
    // registered pawn definition the same way: copy the descriptor's current fragment block to the tail of the GPU
    // fragment array, append the new entries after the copy, point the descriptor at the tail, advance the tail.
    // Every one of those numbers is an off-by-one away from the "spike plague" (every pawn of the unit rendering
    // a foreign mesh), and until this file the arithmetic lived twice, inline, reachable only with the game running.
    //
    // Pure over `Array` + reflection on the descriptor STRUCT's two fields, so the tests drive it with their own
    // struct types. The caller keeps the game-side effects: write the grown array back to the manager, set
    // `persistentFragmentEntryCount` to `NewTail`, raise `descriptorBufferDirty`.
    //
    // NOT `UpdateDescriptorBufferContent`: the game's full re-pack shifts unloaded definitions onto wrong fragments
    // (the reason the hand-prop path went surgical in the first place). The old block is left in place — the GPU
    // may still read it until the dirty flag re-uploads.
    internal static class DescriptorRepoint
    {
        internal const int GrowSlack = 100;   // grow past `need` so a burst of appends does not reallocate per entry

        internal struct Outcome
        {
            public int OldStart, OldCount;    // the block the descriptor pointed at before
            public int NewStart, NewCount;    // what it points at now
            public int NewTail;               // the manager's new persistentFragmentEntryCount
            public bool Grown;                // `gfrags` was replaced — the caller must write it back
        }

        // The descriptor's current block. `count == 0` means the definition is allocated but NOT YET REGISTERED (first
        // in-game run of the smoke fact, 2026-09-14: both repoints it flagged had been made on a 0+0 descriptor, and
        // the game's registration then wrote the real block — body plus our entries — at its own tail). A surgical
        // repoint of an empty block is pointless: the registration snapshot of FragmentEntries carries the appended
        // entries. Callers skip the repoint in that case and let the smoke verify the registration did its job.
        internal static bool TryReadBlock(Array descs, int defId, out int start, out int count)
        {
            start = count = -1;
            if (descs == null || defId < 0 || defId >= descs.Length) return false;
            var d = descs.GetValue(defId);
            var startF = d?.GetType().GetField("StartFragment"); var countF = d?.GetType().GetField("FragmentCount");
            if (startF == null || countF == null) return false;
            start = Convert.ToInt32(startF.GetValue(d)); count = Convert.ToInt32(countF.GetValue(d));
            return start >= 0 && count >= 0;
        }

        /// <param name="gfrags">the GPU fragment array; replaced when it must grow</param>
        /// <param name="descs">the descriptor array (boxed structs with StartFragment / FragmentCount)</param>
        /// <param name="defId">the descriptor to repoint</param>
        /// <param name="tail">persistentFragmentEntryCount before the append</param>
        /// <param name="newEntries">fully built fragment entries, appended after the copied block in order</param>
        internal static bool Apply(ref Array gfrags, Array descs, int defId, int tail, IList<object> newEntries, out Outcome o, out string error)
        {
            o = default; error = null;
            if (gfrags == null || descs == null) { error = "descriptor arrays unreadable"; return false; }
            if (defId < 0 || defId >= descs.Length) { error = $"defId {defId} is outside the descriptor table ({descs.Length})"; return false; }
            if (newEntries == null || newEntries.Count == 0) { error = "nothing to append"; return false; }
            if (tail < 0 || tail > gfrags.Length) { error = $"tail {tail} is outside the fragment array ({gfrags.Length})"; return false; }
            var dEntry = descs.GetValue(defId);
            if (dEntry == null) { error = $"descriptor {defId} is null"; return false; }
            var dT = dEntry.GetType();
            var startF = dT.GetField("StartFragment");
            var countF = dT.GetField("FragmentCount");
            if (startF == null || countF == null) { error = dT.Name + " has no StartFragment/FragmentCount (game update?)"; return false; }
            int start = Convert.ToInt32(startF.GetValue(dEntry));
            int count = Convert.ToInt32(countF.GetValue(dEntry));
            if (start < 0 || count < 0 || start + count > gfrags.Length) { error = $"descriptor block {start}+{count} is outside the fragment array ({gfrags.Length})"; return false; }

            int n = newEntries.Count;
            int need = tail + count + n;
            bool grown = false;
            if (gfrags.Length < need)
            {
                var bigger = Array.CreateInstance(gfrags.GetType().GetElementType(), need + GrowSlack);
                Array.Copy(gfrags, bigger, gfrags.Length);
                gfrags = bigger; grown = true;
            }
            for (int k = 0; k < count; k++) gfrags.SetValue(gfrags.GetValue(start + k), tail + k);
            for (int i = 0; i < n; i++) gfrags.SetValue(newEntries[i], tail + count + i);
            startF.SetValue(dEntry, Convert.ChangeType(tail, startF.FieldType));
            countF.SetValue(dEntry, Convert.ChangeType(count + n, countF.FieldType));
            descs.SetValue(dEntry, defId);   // boxed struct: the write-back IS the write
            o = new Outcome { OldStart = start, OldCount = count, NewStart = tail, NewCount = count + n, NewTail = need, Grown = grown };
            return true;
        }
    }
}
