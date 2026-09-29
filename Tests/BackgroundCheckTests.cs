using System;
using System.Diagnostics;
using System.Threading;
using Xunit;

// A check too slow for a repaint (git), run off the caller's thread (outside review of PR #101, fifth round: the Prop
// Lab asked git synchronously on repaint, and stamped its cache before the call, so a call that timed out left the
// cache expired and blocked the next repaint again). Sixth round: "not known yet" and "couldn't tell" are null, never
// false - the window showed a tracked registry as an empty one while git was slow.
public class BackgroundCheckTests
{
    static void WaitUntilLanded(BackgroundCheck c)
    {
        var sw = Stopwatch.StartNew();
        while (c.Pending) { Assert.True(sw.ElapsedMilliseconds < 5000, "the check never landed"); Thread.Sleep(5); }
    }

    [Fact]
    public void A_slow_check_never_holds_up_the_caller_and_until_it_answers_nothing_is_known()
    {
        using (var gate = new ManualResetEventSlim(false))
        {
            var c = new BackgroundCheck(() => { gate.Wait(); return true; }, 5);   // hangs like a git call to its timeout
            var sw = Stopwatch.StartNew();
            Assert.Null(c.Latest(0, out bool started));                            // NOT KNOWN yet - not "no" - at once
            Assert.True(started);
            Assert.True(sw.ElapsedMilliseconds < 500, $"Latest waited {sw.ElapsedMilliseconds} ms");
            Assert.True(c.Pending);
            Assert.Null(c.Latest(1, out _));                                       // still not known while it hangs
            gate.Set();
            WaitUntilLanded(c);
            Assert.Equal(true, c.Latest(1, out _));                                // the answer, once it has arrived
        }
    }

    [Fact]
    public void A_check_that_couldnt_tell_or_threw_is_not_known_and_a_no_is_a_no()
    {
        var cantTell = new BackgroundCheck(() => null, 5);                         // git timed out / not installed
        cantTell.Latest(0, out _);
        WaitUntilLanded(cantTell);
        Assert.Null(cantTell.Latest(0.1, out _));

        var threw = new BackgroundCheck(() => throw new InvalidOperationException("git blew up"), 5);
        threw.Latest(0, out _);
        WaitUntilLanded(threw);
        Assert.Null(threw.Latest(0.1, out _));

        var no = new BackgroundCheck(() => false, 5);                              // git answered: not tracked
        no.Latest(0, out _);
        WaitUntilLanded(no);
        Assert.Equal(false, no.Latest(0.1, out _));
    }

    [Fact]
    public void Only_one_check_runs_at_a_time_however_often_the_caller_asks()
    {
        int calls = 0;
        using (var gate = new ManualResetEventSlim(false))
        {
            var c = new BackgroundCheck(() => { Interlocked.Increment(ref calls); gate.Wait(); return false; }, 5);
            for (int i = 0; i < 50; i++) c.Latest(i, out _);                       // 50 repaints over 50 s while it hangs
            gate.Set();
            WaitUntilLanded(c);
        }
        Assert.Equal(1, calls);
    }

    [Fact]
    public void The_age_counts_from_when_the_answer_arrived_not_from_when_it_was_asked()
    {
        // the reported defect: asked at 0, the call hangs to its 5 s timeout. Stamped at 0, it was already expired when
        // it came back, so the next repaint asked (and blocked) again. Stamped on arrival, the next ask is 5 s later.
        int calls = 0;
        using (var gate = new ManualResetEventSlim(false))
        {
            var c = new BackgroundCheck(() => { Interlocked.Increment(ref calls); gate.Wait(); return true; }, 5);
            c.Latest(0, out bool first);
            Assert.True(first);
            gate.Set();
            WaitUntilLanded(c);
            Assert.Equal(true, c.Latest(5.5, out bool again));                     // it came back at 5.5 s
            Assert.False(again);                                                   // fresh: no new check
            c.Latest(10.4, out again);
            Assert.False(again);
            Assert.Equal(true, c.Latest(10.6, out again));                         // 5 s after it ARRIVED; the old answer meanwhile
            Assert.True(again);
            WaitUntilLanded(c);
        }
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Forget_makes_the_next_call_ask_afresh_and_know_nothing_meanwhile()
    {
        var c = new BackgroundCheck(() => true, 5);
        c.Latest(0, out _);
        WaitUntilLanded(c);
        Assert.Equal(true, c.Latest(1, out bool started));
        Assert.False(started);
        c.Forget();                                                                // the file came back: a new episode
        Assert.Null(c.Latest(1.1, out started));                                   // nothing known for this episode yet
        Assert.True(started);
        WaitUntilLanded(c);
        Assert.Equal(true, c.Latest(1.2, out _));
    }
}
