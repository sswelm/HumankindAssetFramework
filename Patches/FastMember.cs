using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

namespace HumankindAssetFramework
{
    // COMPILED MEMBER ACCESSORS for the per-pawn hot path (perf pass 2026-08-21, FrameCost: our pawns cost 25-57 µs each
    // per frame — ~60 reflection get/sets on boxed structs, each a FieldInfo.GetValue/SetValue plus a box).
    //
    // Getter<T>(type, "Outer.Inner") / Setter<T>(type, "Outer.Inner") build a DynamicMethod that does what the IL for a
    // direct member access does — unbox (a managed pointer INTO the box, no copy), ldflda through the nested struct path,
    // ldfld/stfld the leaf (or call the leaf PROPERTY's getter/setter on that pointer — PawnEntry.HideFactor is a property
    // packed into a bitfield) — so a write lands in the boxed value exactly like FieldInfo.SetValue on a box does, but at
    // ~10 ns instead of ~500-1000 ns, and with no intermediate box for the nested struct. Numeric leaf fields convert to
    // the requested T (a uint read as int); anything else must match exactly or the accessor is null.
    //
    // NULL MEANS "NOT AVAILABLE" — a missing member, a type mismatch, or an environment where Reflection.Emit throws. Every
    // call site keeps its reflection path as the fallback (PawnFast gates them), so a game update that renames a member
    // degrades to the old speed, never to a crash. Accessors are cached per (type, path, T, get/set); thread-safe.
    internal static class FastMember
    {
        [ProcessLived("compiled accessor cache")] static readonly ConcurrentDictionary<(Type, string, Type, bool), Delegate> cache = new ConcurrentDictionary<(Type, string, Type, bool), Delegate>();
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static Func<object, T> Getter<T>(Type t, string path) =>
            (Func<object, T>)cache.GetOrAdd((t, path, typeof(T), true), k => Build<T>(k.Item1, k.Item2, getter: true));
        public static Action<object, T> Setter<T>(Type t, string path) =>
            (Action<object, T>)cache.GetOrAdd((t, path, typeof(T), false), k => Build<T>(k.Item1, k.Item2, getter: false));

        // intermediate hops must be FIELDS (we need an address to stay in place); the leaf may be a field or a property
        static bool ResolvePath(Type t, string path, out FieldInfo[] hops, out FieldInfo leafField, out PropertyInfo leafProp)
        {
            var parts = path.Split('.');
            hops = new FieldInfo[parts.Length - 1]; leafField = null; leafProp = null;
            var cur = t;
            for (int i = 0; i < parts.Length; i++)
            {
                FieldInfo f = null;
                for (var bt = cur; bt != null && f == null; bt = bt.BaseType) f = bt.GetField(parts[i], F | BindingFlags.DeclaredOnly);
                if (i < parts.Length - 1) { if (f == null) return false; hops[i] = f; cur = f.FieldType; continue; }
                if (f != null) { leafField = f; return true; }
                PropertyInfo p = null;
                for (var bt = cur; bt != null && p == null; bt = bt.BaseType) p = bt.GetProperty(parts[i], F | BindingFlags.DeclaredOnly);
                if (p == null) return false;
                leafProp = p; return true;
            }
            return false;
        }

        // numeric leaf conversions the hot path needs: int/uint/float/double <-> T
        static bool EmitConvert(ILGenerator il, Type from, Type to)
        {
            if (from == to) return true;
            if (to == typeof(int) && (from == typeof(uint) || from == typeof(short) || from == typeof(ushort) || from == typeof(byte) || from == typeof(long) || from == typeof(ulong))) { il.Emit(OpCodes.Conv_I4); return true; }
            if (to == typeof(uint) && (from == typeof(int) || from == typeof(ushort) || from == typeof(byte) || from == typeof(long) || from == typeof(ulong))) { il.Emit(OpCodes.Conv_U4); return true; }
            if (to == typeof(float) && (from == typeof(double) || from == typeof(int) || from == typeof(uint))) { il.Emit(OpCodes.Conv_R4); return true; }
            if (to == typeof(double) && (from == typeof(float) || from == typeof(int) || from == typeof(uint))) { il.Emit(OpCodes.Conv_R8); return true; }
            if (to == typeof(object) && from.IsValueType) { il.Emit(OpCodes.Box, from); return true; }
            if (to == typeof(object) && !from.IsValueType) return true;
            return false;
        }

        static Delegate Build<T>(Type t, string path, bool getter)
        {
            try
            {
                if (!ResolvePath(t, path, out var hops, out var leafField, out var leafProp)) return null;
                var leafType = leafField != null ? leafField.FieldType : leafProp.PropertyType;
                var dm = getter
                    ? new DynamicMethod("haf_get_" + path, typeof(T), new[] { typeof(object) }, typeof(FastMember).Module, true)
                    : new DynamicMethod("haf_set_" + path, typeof(void), new[] { typeof(object), typeof(T) }, typeof(FastMember).Module, true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                if (t.IsValueType) il.Emit(OpCodes.Unbox, t); else il.Emit(OpCodes.Castclass, t);   // unbox = pointer INTO the box (no copy)
                Type owner = t;
                foreach (var h in hops)
                {
                    if (!h.FieldType.IsValueType) il.Emit(OpCodes.Ldfld, h);   // a reference-typed hop: load the object
                    else il.Emit(OpCodes.Ldflda, h);                            // a nested struct: its address, stay in place
                    owner = h.FieldType;
                }
                if (getter)
                {
                    if (leafField != null) il.Emit(OpCodes.Ldfld, leafField);
                    else
                    {
                        var gm = leafProp.GetGetMethod(true); if (gm == null) return null;
                        il.Emit(owner.IsValueType ? OpCodes.Call : OpCodes.Callvirt, gm);   // struct: call on the managed pointer
                    }
                    if (!EmitConvert(il, leafType, typeof(T))) return null;
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_1);
                    if (typeof(T) == typeof(object) && leafType.IsValueType) il.Emit(OpCodes.Unbox_Any, leafType);
                    else if (!EmitConvert(il, typeof(T), leafType)) return null;
                    if (leafField != null) il.Emit(OpCodes.Stfld, leafField);
                    else
                    {
                        var sm = leafProp.GetSetMethod(true); if (sm == null) return null;
                        il.Emit(owner.IsValueType ? OpCodes.Call : OpCodes.Callvirt, sm);
                    }
                }
                il.Emit(OpCodes.Ret);
                return getter ? (Delegate)dm.CreateDelegate(typeof(Func<object, T>)) : dm.CreateDelegate(typeof(Action<object, T>));
            }
            catch { return null; }   // no Emit here (or a shape we don't handle) -> the caller keeps its reflection path
        }
    }
}
