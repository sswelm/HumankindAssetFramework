using System.IO;
using Xunit;

// The Factory's remove snapshot: its folder name, and where a cleanup may delete (outside review of PR #102, fourth
// round: a resource name with separators or `..` built a snapshot path outside the backup root, and the cleanup
// after a refused remove deleted it recursively).
public class BackupRulesTests
{
    static readonly char S = Path.DirectorySeparatorChar;

    [Fact]
    public void A_resource_name_becomes_one_path_segment()
    {
        Assert.Equal("Tank", BackupRules.SafeSegment("Tank"));
        Assert.Equal("Era6_Common_StealthCorvettes_01", BackupRules.SafeSegment("Era6_Common_StealthCorvettes_01"));
        // the reported shape: separators and parents
        foreach (var hostile in new[] { "..\\..\\target", "../../target", "\\..\\..\\target", "/target", "a/b\\c" })
        {
            string seg = BackupRules.SafeSegment(hostile);
            Assert.DoesNotContain("/", seg);
            Assert.DoesNotContain("\\", seg);
            Assert.False(seg.StartsWith("."), seg);   // never a parent, never hidden
        }
        Assert.Equal("a_b_c", BackupRules.SafeSegment("a:b*c"));
        Assert.Equal("_", BackupRules.SafeSegment(""));
        Assert.Equal("_", BackupRules.SafeSegment(null));
        Assert.Equal("_", BackupRules.SafeSegment("..."));
        Assert.Equal("name", BackupRules.SafeSegment(" name. "));
        Assert.Equal("_", BackupRules.SafeSegment("\\"));
    }

    [Fact]
    public void The_snapshot_folder_name_carries_the_prefix_and_stamp_and_no_separators()
    {
        string n = BackupRules.SnapshotFolderName("2026-09-29_120000", "\\..\\..\\target");
        Assert.StartsWith("_removed_2026-09-29_120000_", n);
        Assert.Equal(n, BackupRules.SafeSegment(n));   // a folder name is itself one segment
    }

    [Fact]
    public void Two_removes_never_share_a_snapshot_folder()
    {
        // fifth round: the stamp is to the second and "a/b" and "a_b" sanitise alike, so a second remove wrote into the
        // first one's folder - and its cleanup, after a refused save, deleted the first one's undo
        string root = Path.Combine(Path.GetTempPath(), "HAF_Backups");
        var taken = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        string name = BackupRules.SnapshotFolderName("2026-09-29_120000", "a/b");
        Assert.Equal(name, BackupRules.SnapshotFolderName("2026-09-29_120000", "a_b"));   // the collision
        string first = BackupRules.UniqueFolder(root, name, taken.Contains);
        Assert.Equal(Path.Combine(root, name), first);                                     // the first free name is the plain one
        taken.Add(first);
        string second = BackupRules.UniqueFolder(root, name, taken.Contains);
        Assert.NotEqual(first, second);
        Assert.Equal(Path.Combine(root, name + "-2"), second);
        taken.Add(second);
        Assert.Equal(Path.Combine(root, name + "-3"), BackupRules.UniqueFolder(root, name, taken.Contains));
        Assert.True(BackupRules.IsRemovedSnapshotInside(root, second));                    // still a snapshot the cleanup may delete
    }

    [Fact]
    public void A_snapshots_own_bookkeeping_is_never_copied_back_as_a_baked_file()
    {
        Assert.True(BackupRules.IsSnapshotMetadata("entry.json"));
        Assert.True(BackupRules.IsSnapshotMetadata("manifest.txt"));
        Assert.True(BackupRules.IsSnapshotMetadata(BackupRules.AttemptedMarker));            // fifth round: it landed in Assets/Resources
        Assert.True(BackupRules.IsSnapshotMetadata("ENTRY.JSON"));
        Assert.False(BackupRules.IsSnapshotMetadata("Tank_Skeleton.asset"));
        Assert.False(BackupRules.IsSnapshotMetadata("Tank_Atlas.png"));
    }

    [Fact]
    public void Only_a_snapshot_directly_inside_the_root_may_be_deleted()
    {
        string root = Path.Combine(Path.GetTempPath(), "HAF_Backups");
        Assert.True(BackupRules.IsRemovedSnapshotInside(root, Path.Combine(root, "_removed_2026-09-29_120000_Tank")));
        Assert.True(BackupRules.IsRemovedSnapshotInside(root + S, Path.Combine(root, "_removed_x_Tank")));   // a trailing separator on the root
        Assert.True(BackupRules.IsRemovedSnapshotInside(root, Path.Combine(root, "_removed_x_Tank") + S));   // or on the folder
        // the reported path: the folder name escapes the root once resolved
        string escaped = Path.Combine(root, "_removed_x_" + S + ".." + S + ".." + S + "target");
        Assert.False(BackupRules.IsRemovedSnapshotInside(root, escaped));
        Assert.False(BackupRules.IsRemovedSnapshotInside(root, Path.Combine(root, "_removed_x_" + S + ".." + S + "target")));   // resolves to root/target: no prefix
        Assert.False(BackupRules.IsRemovedSnapshotInside(root, root));                                            // the root itself
        Assert.False(BackupRules.IsRemovedSnapshotInside(root, Path.GetDirectoryName(root)));                    // its parent
        Assert.False(BackupRules.IsRemovedSnapshotInside(root, Path.Combine(root, "2026-09-29_manual")));         // a full backup: no prefix
        Assert.False(BackupRules.IsRemovedSnapshotInside(root, Path.Combine(root, "_deleted_x_Tank.prefab")));    // another kind
        Assert.False(BackupRules.IsRemovedSnapshotInside(root, Path.Combine(root, "_removed_x_Tank", "child")));  // a grandchild
        Assert.False(BackupRules.IsRemovedSnapshotInside(root, Path.Combine(Path.GetDirectoryName(root), "Other", "_removed_x_Tank")));   // a sibling tree
        Assert.False(BackupRules.IsRemovedSnapshotInside("", Path.Combine(root, "_removed_x_Tank")));
        Assert.False(BackupRules.IsRemovedSnapshotInside(root, null));
    }
}
