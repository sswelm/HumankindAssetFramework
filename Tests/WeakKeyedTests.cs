using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public void A_factory_reentering_the_same_key_returns_the_canonical_value()
    {
        var map = new WeakKeyed<Record>(); object key = new object(); Record inner = null;
        var returned = map.GetValue(key, k => {
            inner = map.GetValue(k, _ => new Record { Hits = 7 });
            return new Record { Hits = 99 };
        });
        Assert.Same(inner, returned);
        Assert.Same(returned, map.GetValue(key, _ => new Record()));
        Assert.Equal(1, map.Count);
        GC.KeepAlive(key);
    }

    [Fact]
    public void A_factory_can_wait_for_another_thread_using_the_map()
    {
        var map = new WeakKeyed<Record>(); object first = new object(), second = new object();
        Task<Record> worker = null; bool completedInside = false;
        var outer = map.GetValue(first, _ => {
            worker = Task.Factory.StartNew(() => map.GetValue(second, k => new Record { Hits = 2 }),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            completedInside = worker.Wait(TimeSpan.FromSeconds(5));
            return new Record { Hits = 1 };
        });
        // Join even on the broken implementation: the outer call has released its lock by now.
        Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(completedInside, "the factory held the map lock while waiting for the second key");
        Assert.Equal(1, outer.Hits); Assert.Equal(2, worker.Result.Hits); Assert.Equal(2, map.Count);
        GC.KeepAlive(first); GC.KeepAlive(second);
    }

    [Fact]
    public void Concurrent_factories_publish_one_value_for_the_same_key()
    {
        var map = new WeakKeyed<Record>(); object key = new object(); var values = new Record[32];
        Parallel.For(0, values.Length, i => values[i] = map.GetValue(key, _ => new Record { Hits = i }));
        Assert.All(values, value => Assert.Same(values[0], value));
        Assert.Equal(1, map.Count);
        GC.KeepAlive(key);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static object[] AddTemporaryKeys(WeakKeyed<Record> map, int n)
    {
        var keys = new object[n];
        for (int i = 0; i < n; i++) { keys[i] = new object(); map.GetValue(keys[i], _ => new Record()); }
        return keys;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int CountWithTemporaryKeys(WeakKeyed<Record> map, int n)
    {
        var keys = AddTemporaryKeys(map, n);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int count = map.Count;
        GC.KeepAlive(keys); // the pre-collection count is captured while all its keys are actually rooted
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Record AddPredecessorsAndLiveKey(WeakKeyed<Record> map, object live)
    {
        var predecessors = AddTemporaryKeys(map, 3);
        var kept = map.GetValue(live, _ => new Record { Hits = 1 });
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Equal(4, map.Count);
        GC.KeepAlive(predecessors);
        return kept;
    }

    [Fact]
    public void Collected_keys_are_swept_on_the_next_touch_and_a_live_key_keeps_its_value()
    {
        var map = new WeakKeyed<Record>();
        object live = new object();
        var kept = map.GetValue(live, _ => new Record { Hits = 7 });
        Assert.Equal(6, CountWithTemporaryKeys(map, 5));
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
        object live = new object();
        var kept = AddPredecessorsAndLiveKey(map, live);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Same(kept, map.GetValue(live, _ => new Record { Hits = 99 }));
        Assert.Equal(1, map.Count);
        GC.KeepAlive(live);
    }
}
