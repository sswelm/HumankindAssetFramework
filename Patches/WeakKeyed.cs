using System;
using System.Collections.Generic;

namespace HumankindAssetFramework
{
    // ONE VALUE PER LIVE KEY, THE KEY HELD WEAKLY - AND NOT A ConditionalWeakTable (2026-11-05).
    //
    // Unity's Mono hands a COLLECTED ephemeron value back out of ConditionalWeakTable once the collector has run over
    // the table: "Specified cast is not valid" from inside TryGetValue (a castclass on freed memory). The editor showed
    // it first (2026-10-27, every probe and fuse; PR #208 replaced the editor's table), and on 2026-11-05 the game did:
    // the drop watch logged that message 4,600 times while a 180-turn save loaded, then crashed natively reading the
    // freed record's dictionary (Player.log: MeshDropWatch.After -> Enumerable.ToArray -> access violation). The game
    // and the editor project both run the INCREMENTAL collector (boot.config gc-max-time-slice, gcIncremental: 1);
    // 30,000 rounds on the standalone mono.exe with that collector switched on did not reproduce it, so the embedded
    // runtime stays the only witness and this plugin uses no ConditionalWeakTable at all (Tests/SessionStateTests.cs
    // holds that line).
    //
    // The replacement is the editor's: a plain list under a lock, swept of dead keys on every touch. The key counts
    // here are tiny (GPU content layers, attack state machines), so a linear scan is nothing. A key is matched by
    // reference; dead keys release their values on the next touch. Values must not retain their own keys: this is
    // weak-key storage with strong values, not an ephemeron map. Both current value types contain no key references.
    internal sealed class WeakKeyed<TValue> where TValue : class
    {
        readonly List<KeyValuePair<WeakReference, TValue>> entries = new List<KeyValuePair<WeakReference, TValue>>();

        // The value for this key, made by `create` on first sight. Concurrent/reentrant factories may make candidates;
        // one canonical value is published. Never run caller code while holding the map lock.
        public TValue GetValue(object key, Func<object, TValue> create)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            lock (entries)
            {
                int found = Sweep(key);
                if (found >= 0) return entries[found].Value;
            }
            var value = create(key);
            lock (entries)
            {
                int found = Sweep(key);
                if (found >= 0) return entries[found].Value;
                entries.Add(new KeyValuePair<WeakReference, TValue>(new WeakReference(key), value));
                return value;
            }
        }

        public bool TryGetValue(object key, out TValue value)
        {
            value = null;
            if (key == null) return false;
            lock (entries)
            {
                int found = Sweep(key);
                if (found < 0) return false;
                value = entries[found].Value;
                return true;
            }
        }

        public bool Remove(object key)
        {
            if (key == null) return false;
            lock (entries)
            {
                int found = Sweep(key);
                if (found < 0) return false;
                entries.RemoveAt(found);
                return true;
            }
        }

        public void Clear() { lock (entries) entries.Clear(); }

        // Live entries after a sweep (tests; the budget readout).
        public int Count { get { lock (entries) { Sweep(null); return entries.Count; } } }

        // Drops every entry whose key is gone; returns the index of `key` or -1. Caller holds the lock.
        int Sweep(object key)
        {
            int found = -1;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                object target = entries[i].Key.Target;
                if (target == null) { entries.RemoveAt(i); if (found > i) found--; }
                else if (key != null && ReferenceEquals(target, key)) found = i;
            }
            return found;
        }
    }
}
