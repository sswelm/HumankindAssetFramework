using System.Collections.Generic;
using Xunit;

// A read that FAILED returns an empty list, and that list is not "no entries" (outside review of PR #102): a rename
// reported its old entry removed from it, and the Sound Lab cached it as "no units with audio" until refocus.
public class RegistryReadFailureTests
{
    [Fact]
    public void A_remove_is_a_removal_only_when_the_read_worked_and_the_save_went_through()
    {
        int saves = 0;
        bool Save(bool result) { saves++; return result; }

        // the reported case: the read failed, so the entry is "not found" in an empty list - that proves nothing
        Assert.Equal(RegistryRules.RemoveResult.Failed, RegistryRules.JudgeRemove(true, false, () => Save(true)));
        Assert.Equal(RegistryRules.RemoveResult.Failed, RegistryRules.JudgeRemove(true, true, () => Save(true)));
        Assert.Equal(0, saves);                                               // and nothing is written from it
        // a read that worked may say it isn't there
        Assert.Equal(RegistryRules.RemoveResult.NotPresent, RegistryRules.JudgeRemove(false, false, () => Save(true)));
        Assert.Equal(0, saves);
        // found: removed only if the save went through
        Assert.Equal(RegistryRules.RemoveResult.Removed, RegistryRules.JudgeRemove(false, true, () => Save(true)));
        Assert.Equal(RegistryRules.RemoveResult.Failed, RegistryRules.JudgeRemove(false, true, () => Save(false)));
        Assert.Equal(2, saves);
    }

    // a fake registry: what the next read returns, and whether it fails
    sealed class FakeRegistry
    {
        public bool Fails;
        public int Reads;
        public List<string> Entries = new List<string> { "Tank", "Howitzer" };
        public List<string> Read() { Reads++; return Fails ? new List<string>() : new List<string>(Entries); }
    }

    [Fact]
    public void A_failed_read_is_never_kept_and_the_list_comes_back_by_itself_once_the_file_can_be_read()
    {
        // the reported sequence: Clear -> the save is refused -> the cache is dropped -> the next reload happens while
        // the registry is unreadable. It used to cache that empty list until the window regained focus.
        var reg = new FakeRegistry { Fails = true };
        var cache = new ReadCache<List<string>>(reg.Read, () => reg.Fails, 1.0);
        Assert.Empty(cache.Get(0));
        Assert.True(cache.ReadFailed);                                        // the window can say WHY it is empty
        Assert.Empty(cache.Get(0.5));                                         // not read again within the second
        Assert.Equal(1, reg.Reads);
        reg.Fails = false;                                                    // the lock is gone
        Assert.Equal(new[] { "Tank", "Howitzer" }, cache.Get(1.2));           // read again on a later repaint - no refocus needed
        Assert.False(cache.ReadFailed);
        Assert.Equal(2, reg.Reads);
    }

    [Fact]
    public void A_read_that_worked_is_kept_until_it_is_dropped()
    {
        var reg = new FakeRegistry();
        var cache = new ReadCache<List<string>>(reg.Read, () => reg.Fails, 1.0);
        cache.Get(0);
        cache.Get(100);
        Assert.Equal(1, reg.Reads);                                           // kept: the read is slow, and the window repaints often
        reg.Entries.Add("Galley");
        cache.Drop();                                                         // after a save, or on focus
        Assert.Equal(3, cache.Get(100.1).Count);
        Assert.Equal(2, reg.Reads);

        // a drop while the registry is unreadable: the failure is shown, then the next read after a second recovers
        reg.Fails = true;
        cache.Drop();
        Assert.Empty(cache.Get(200));
        Assert.True(cache.ReadFailed);
        reg.Fails = false;
        Assert.Equal(3, cache.Get(201.5).Count);
        Assert.False(cache.ReadFailed);
    }
}
