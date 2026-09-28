using System;
using System.IO;
using System.Linq;
using Xunit;

// The registry's save guards, pinned (review of PR #100): they stand between a 37-model pack and an empty list, and
// until now the only proof they worked was a one-off manual drill that later commits never re-ran.
public class RegistryRulesTests
{
    [Fact]
    public void An_empty_parse_is_a_pack_only_when_the_models_array_is_really_there()
    {
        // JsonUtility reads `{}` and `"models": []` alike, so the raw text decides
        Assert.True(RegistryRules.HasModelsArray("{ \"modId\": \"x\", \"models\": [] }"));
        Assert.True(RegistryRules.HasModelsArray("{ \"models\": [ { \"resourceName\": \"Tank\" } ] }"));
        Assert.False(RegistryRules.HasModelsArray("{}"));                                   // wiped every model via SaveStatics
        Assert.False(RegistryRules.HasModelsArray("{ \"models\": null }"));
        Assert.False(RegistryRules.HasModelsArray("{ \"models\": {} }"));
        Assert.False(RegistryRules.HasModelsArray("<<<<<<< HEAD\n{ \"models\": [] }"));     // conflict markers
        Assert.False(RegistryRules.HasModelsArray("{ \"models\": [ { \"a\": 1 "));          // truncated half way
        Assert.False(RegistryRules.HasModelsArray(""));
        Assert.False(RegistryRules.HasModelsArray(null));
    }

    [Fact]
    public void A_save_over_an_empty_source_goes_ahead_only_when_nothing_else_could_still_hold_the_models()
    {
        var allow = RegistryRules.EmptySourceVerdict.Allow;
        // not an empty source at all
        Assert.Equal(allow, RegistryRules.JudgeEmptySource(true, 37, false, 37));
        Assert.Equal(allow, RegistryRules.JudgeEmptySource(false, 0, false, 37));           // no source: that path builds on the deployed copy instead
        // the editor emptied it itself (Remove() of the last model) — even when its deploy failed and the copy is stale
        Assert.Equal(allow, RegistryRules.JudgeEmptySource(true, 0, true, 37));
        Assert.Equal(allow, RegistryRules.JudgeEmptySource(true, 0, true, -1));
        // emptied outside the editor while a copy still holds them: a Factory bake Upsert()ed a one-model registry over 37
        Assert.Equal(RegistryRules.EmptySourceVerdict.RefuseDeployedHasModels, RegistryRules.JudgeEmptySource(true, 0, false, 37));
        // a deployed copy that can't be READ is no evidence of an empty pack ("unreadable" once counted as "empty")
        Assert.Equal(RegistryRules.EmptySourceVerdict.RefuseDeployedUnreadable, RegistryRules.JudgeEmptySource(true, 0, false, -1));
        // both copies provably empty: a genuinely empty pack
        Assert.Equal(allow, RegistryRules.JudgeEmptySource(true, 0, false, 0));
    }

    [Fact]
    public void The_editors_own_write_is_recognised_through_a_line_ending_rewrite()
    {
        // the source is git-tracked, and autocrlf rewriting it on a checkout is not somebody else emptying it
        string lf = "{\n  \"models\": []\n}", crlf = "{\r\n  \"models\": []\r\n}";
        Assert.Equal(RegistryRules.FingerprintText(lf), RegistryRules.FingerprintText(crlf));
        Assert.NotEqual(RegistryRules.FingerprintText(lf), RegistryRules.FingerprintText("{\n  \"models\": [ {} ]\n}"));
        Assert.Equal("", RegistryRules.FingerprintText(null));
    }

    [Fact]
    public void A_pending_deploy_is_retried_with_backoff_not_on_every_poll()
    {
        Assert.True(RegistryRules.PendingRetryDue(100, -1, 0));                             // never tried: now
        Assert.False(RegistryRules.PendingRetryDue(101, 100, 0));                           // 1 s after the save that failed: not yet
        Assert.True(RegistryRules.PendingRetryDue(102, 100, 0));                            // 2 s
        Assert.Equal(new[] { 2.0, 4.0, 8.0, 16.0, 30.0, 30.0 },
                     new[] { 0, 1, 2, 3, 4, 50 }.Select(n => RegistryRules.PendingRetryDelay(n)).ToArray());
        Assert.False(RegistryRules.PendingRetryDue(115, 100, 3));                           // 16 s wanted, 15 s passed
        Assert.True(RegistryRules.PendingRetryDue(130, 100, 9));                            // capped at 30 s however long it has failed
    }

    [Fact]
    public void A_file_that_could_not_be_read_is_not_a_file_that_is_broken_and_the_two_kinds_are_told_apart()
    {
        // only a file that was READ and is broken may raise the recovery banner, whose "Restore last commit" is a git checkout
        Assert.Equal(RegistryRules.ReadFailure.Locked, RegistryRules.ClassifyReadFailure(new IOException("being used by another process")));
        Assert.Equal(RegistryRules.ReadFailure.Locked, RegistryRules.ClassifyReadFailure(new FileNotFoundException("gone mid-rename")));
        Assert.Equal(RegistryRules.ReadFailure.AccessDenied, RegistryRules.ClassifyReadFailure(new UnauthorizedAccessException("denied")));
        Assert.Equal(RegistryRules.ReadFailure.NotARead, RegistryRules.ClassifyReadFailure(new ArgumentException("JSON parse error")));
        Assert.Equal(RegistryRules.ReadFailure.NotARead, RegistryRules.ClassifyReadFailure(new InvalidOperationException()));
        // and the advice is true for each (second round: "nothing is wrong, it retries by itself" was untrue for a permissions denial)
        string locked = RegistryRules.ReadFailureAdvice(RegistryRules.ReadFailure.Locked), denied = RegistryRules.ReadFailureAdvice(RegistryRules.ReadFailure.AccessDenied);
        Assert.Contains("tries again by itself", locked);
        Assert.DoesNotContain("tries again by itself", denied);
        Assert.Contains("permissions", denied);
        Assert.Contains("will not clear by itself", denied);
    }

    [Fact]
    public void The_recovery_controls_never_show_while_the_file_cannot_be_read()
    {
        // outside review of PR #100, second round: a repaired source caught briefly locked kept "Restore last commit" —
        // a git checkout — one click from discarding that uncommitted repair. A lock takes precedence.
        Assert.True(RegistryRules.ShowRecoveryControls(true, false));
        Assert.False(RegistryRules.ShowRecoveryControls(true, true));
        Assert.False(RegistryRules.ShowRecoveryControls(false, true));
        Assert.False(RegistryRules.ShowRecoveryControls(false, false));
    }
}
