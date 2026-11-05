using System;
using System.Runtime.CompilerServices;
using HumankindAssetFramework;
using Xunit;

// WeakKeyed<T>: the plugin's weakly keyed map (Patches/WeakKeyed.cs), which replaced ConditionalWeakTable after the
// 2026-11-05 game crash. One value per live key, matched by reference; dead keys swept on every touch.
public class WeakKeyedTests
{
    sealed class Record { public int Hits; }

    [Fact]
    public void One_value_per_key_made_once_and_keys_match_by_reference()
    {
        var map = new WeakKeyed<Record>();
        object a = new object(), b = new object();
        int made = 0;
        var ra = map.GetValue(a, _ => { made++; return new Record(); });
        Assert.Same(ra, map.GetValue(a, _ => { made++; return new Record(); }));
        Assert.NotSame(ra, map.GetValue(b, _ => { made++; return new Record(); }));
        Assert.Equal(2, made);
        Assert.True(map.TryGetValue(a, out var again)); Assert.Same(ra, again);
        Assert.False(map.TryGetValue(new object(), out var none)); Assert.Null(none);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Remove_and_Clear_drop_entries_and_a_null_key_is_refused()
    {
        var map = new WeakKeyed<Record>();
        object a = new object(), b = new object();
        map.GetValue(a, _ => new Record()); map.GetValue(b, _ => new Record());
        Assert.True(map.Remove(a)); Assert.False(map.Remove(a)); Assert.Equal(1, map.Count);
        Assert.False(map.Remove(null)); Assert.False(map.TryGetValue(null, out _));
        Assert.Throws<ArgumentNullException>(() => map.GetValue(null, _ => new Record()));
        map.Clear(); Assert.Equal(0, map.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void AddTemporaryKeys(WeakKeyed<Record> map, int n) { for (int i = 0; i < n; i++) map.GetValue(new object(), _ => new Record()); }

    [Fact]
    public void Collected_keys_are_swept_on_the_next_touch_and_a_live_key_keeps_its_value()
    {
        var map = new WeakKeyed<Record>();
        object live = new object();
        var kept = map.GetValue(live, _ => new Record { Hits = 7 });
        AddTemporaryKeys(map, 5);
        Assert.Equal(6, map.Count);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(map.TryGetValue(live, out var found)); Assert.Same(kept, found); Assert.Equal(7, found.Hits);
        Assert.Equal(1, map.Count);
        GC.KeepAlive(live);
    }

    [Fact]
    public void The_found_index_survives_sweeping_entries_below_it()
    {
        // the sweep walks from the end; a dead entry BELOW the found key shifts its index, which the sweep must track
        var map = new WeakKeyed<Record>();
        AddTemporaryKeys(map, 3);
        object live = new object();
        var kept = map.GetValue(live, _ => new Record { Hits = 1 });
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Same(kept, map.GetValue(live, _ => new Record { Hits = 99 }));
        Assert.Equal(1, map.Count);
        GC.KeepAlive(live);
    }
}
