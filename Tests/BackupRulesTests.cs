using System;
using System.Collections.Generic;
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
        // first one's folder - and its cleanup, after a refused save, deleted the first one's undo. The reserve callback
        // is atomic (here: a set's Add, which is false when the name is taken).
        string root = Path.Combine(Path.GetTempPath(), "HAF_Backups");
        var taken = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        string name = BackupRules.SnapshotFolderName("2026-09-29_120000", "a/b");
        Assert.Equal(name, BackupRules.SnapshotFolderName("2026-09-29_120000", "a_b"));   // the collision
        string first = BackupRules.ReserveFolder(root, name, taken.Add);
        Assert.Equal(Path.Combine(root, name), first);                                     // the first free name is the plain one
        string second = BackupRules.ReserveFolder(root, name, taken.Add);
        Assert.NotEqual(first, second);
        Assert.Equal(Path.Combine(root, name + "-2"), second);
        Assert.Equal(Path.Combine(root, name + "-3"), BackupRules.ReserveFolder(root, name, taken.Add));
        Assert.True(BackupRules.IsRemovedSnapshotInside(root, second));                    // still a snapshot the cleanup may delete
        Assert.Throws<IOException>(() => BackupRules.ReserveFolder(root, name, _ => false));   // a root nothing can be reserved in: said, not looped forever
    }

    [Fact]
    public void Reserving_a_folder_is_atomic_on_a_real_file_system()
    {
        // sixth round: "exists, then create" let two editors pick the same free name. The reservation renames a private
        // temp folder into place, which fails when the name is taken - one winner, whoever asks first.
        string root = Path.Combine(Path.GetTempPath(), "haf_reserve_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            string want = Path.Combine(root, "_removed_2026-09-29_120000_Tank");
            Assert.True(CheckedReplace.TryReserveFolder(want));                            // the root did not even exist: made, and the folder reserved
            Assert.True(Directory.Exists(want));
            Assert.Empty(Directory.GetFileSystemEntries(want));
            Assert.False(CheckedReplace.TryReserveFolder(want));                           // the second editor: taken
            Assert.Empty(Directory.GetDirectories(root, "_tmp_*"));                        // and its temp folder is gone
            // through the rule: the second remove of the same name lands on -2, on disk
            string name = Path.GetFileName(want);
            Assert.Equal(want + "-2", BackupRules.ReserveFolder(root, name, CheckedReplace.TryReserveFolder));
            Assert.True(Directory.Exists(want + "-2"));
            File.WriteAllText(Path.Combine(root, "_removed_x_File"), "a file, not a folder");
            Assert.False(CheckedReplace.TryReserveFolder(Path.Combine(root, "_removed_x_File")));   // a file in the way is "taken" too
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Only_a_plain_name_may_locate_files()
    {
        // sixth round: the output copy uses the raw name in the source AND destination path
        Assert.True(BackupRules.IsPlainName("Tank"));
        Assert.True(BackupRules.IsPlainName("Era6_Common_StealthCorvettes_01"));
        Assert.True(BackupRules.IsPlainName("a..b"));                                      // dots inside one segment are just a name
        foreach (var hostile in new[] { @"..\..\target", "../../target", @"\..\..\target", "/target", "a/b", @"a\b", "..", ".", "", null, "Tank.", " Tank", "a:b", "a*b" })
            Assert.False(BackupRules.IsPlainName(hostile), hostile ?? "<null>");
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
    // ---- the manifest line (2026-09-30): three writers (the window, the delete guard, Ship Status' outputs snapshot),
    //      one reader, one format ----

    [Fact]
    public void A_manifest_line_round_trips_through_the_reader()
    {
        string line = BackupRules.ManifestLine("resources\\Tank_Atlas.png", "C:\\Proj\\Assets\\Resources\\Tank_Atlas.png", 1, 4096);
        Assert.Equal("SRC\tresources/Tank_Atlas.png\tC:/Proj/Assets/Resources/Tank_Atlas.png\t1\t4096", line);   // separators as `/`, tabs between
        Assert.True(BackupRules.TryParseManifestLine(line, out string rel, out string original, out int files));
        Assert.Equal("resources/Tank_Atlas.png", rel);
        Assert.Equal("C:/Proj/Assets/Resources/Tank_Atlas.png", original);
        Assert.Equal(1, files);
        // a tree line: the count is the tree's
        Assert.True(BackupRules.TryParseManifestLine(BackupRules.ManifestLine("pack/Pack", "C:/Proj/Assets/Pack", 37, 123456789L), out _, out _, out files));
        Assert.Equal(37, files);
    }

    [Fact]
    public void The_reader_takes_only_source_lines()
    {
        foreach (var notASource in new[] { null, "", "# HAF backup manifest", "# original: C:/x", "SRC", "SRC\tonly-rel", "SRC\trel\toriginal", "src\trel\toriginal\t1\t2", " SRC\trel\toriginal\t1\t2" })
            Assert.False(BackupRules.TryParseManifestLine(notASource, out _, out _, out _), notASource ?? "<null>");
        // an older or hand-edited line whose count does not parse reads as 0 files, not as no source
        Assert.True(BackupRules.TryParseManifestLine("SRC\trel\toriginal\tmany", out string rel, out _, out int files));
        Assert.Equal("rel", rel); Assert.Equal(0, files);
    }

    [Fact]
    public void A_path_with_a_tab_cannot_be_carried()
    {
        Assert.Throws<ArgumentException>(() => BackupRules.ManifestLine("a\tb", "C:/x", 1, 1));
        Assert.Throws<ArgumentException>(() => BackupRules.ManifestLine("a", "C:/x\ty", 1, 1));
        Assert.Throws<ArgumentNullException>(() => BackupRules.ManifestLine(null, "C:/x", 1, 1));
    }

    [Fact]
    public void An_outputs_snapshot_folder_is_a_delete_guard_folder_for_one_name()
    {
        string f = BackupRules.OutputsSnapshotFolderName("2026-09-30_141500", "Era6_Common_StealthCorvettes_01");
        Assert.Equal("_deleted_2026-09-30_141500_Era6_Common_StealthCorvettes_01_outputs", f);
        Assert.StartsWith(BackupRules.DeletedPrefix, f);            // listed, restored and aged with the guard's snapshots
        Assert.DoesNotContain("/", BackupRules.OutputsSnapshotFolderName("s", "../../x"));   // the name is made one segment
        Assert.DoesNotContain("\\", BackupRules.OutputsSnapshotFolderName("s", "..\\..\\x"));
    }

    // ---- the dedup's "unchanged" and the registry verify (review of PR #105 by ChatGPT: a same-length edit within 2 s
    //      of the copied version was linked as unchanged, and an existence check called the old bytes "verified") ----

    [Fact]
    public void A_copy_stands_in_only_when_it_is_the_same_bytes()
    {
        var fine = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);   // NTFS: 100 ns ticks
        var coarse = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);                   // FAT: whole 2 s slots
        int asked = 0;
        Func<bool> never = () => { asked++; return true; };
        Assert.False(BackupRules.SameCopy(10, fine, 11, fine, never));                    // a different size is a different file
        Assert.True(BackupRules.SameCopy(10, fine, 10, fine, never));                     // the same sub-second time is proof
        Assert.Equal(0, asked);                                                           // neither read a byte
        Assert.False(BackupRules.SameCopy(10, fine, 10, fine.AddSeconds(3), never));      // past the tolerance: copied, no read
        Assert.Equal(0, asked);
        // within the tolerance the bytes decide - the reported case: a same-length edit 1 s after the version copied
        Assert.False(BackupRules.SameCopy(10, fine.AddSeconds(1), 10, fine, () => false));
        Assert.True(BackupRules.SameCopy(10, fine.AddSeconds(1.5), 10, fine, () => true));   // FAT rounding, same bytes: linked
        Assert.True(BackupRules.SameCopy(10, fine, 10, fine.AddSeconds(2), () => true));
        // round 5: an EQUAL whole-second time is FAT-shaped - a same-length edit inside the slot keeps it - so the bytes decide
        Assert.False(BackupRules.SameCopy(10, coarse, 10, coarse, () => false));
        Assert.True(BackupRules.SameCopy(10, coarse, 10, coarse, () => { asked++; return true; }));
        Assert.Equal(1, asked);
    }

    [Fact]
    public void Same_bytes_is_the_whole_file()
    {
        string d = Path.Combine(Path.GetTempPath(), "haf_samebytes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        try
        {
            string a = Path.Combine(d, "a"), b = Path.Combine(d, "b"), c = Path.Combine(d, "c"), e = Path.Combine(d, "e");
            var big = new byte[200 * 1024]; new Random(7).NextBytes(big);
            File.WriteAllBytes(a, big); File.WriteAllBytes(b, big);
            var edited = (byte[])big.Clone(); edited[big.Length - 1] ^= 1;   // same length, last byte differs: the second buffer
            File.WriteAllBytes(c, edited);
            File.WriteAllBytes(e, new byte[0]);
            Assert.True(BackupRules.SameBytes(a, b));
            Assert.False(BackupRules.SameBytes(a, c));
            Assert.False(BackupRules.SameBytes(a, e));
            Assert.True(BackupRules.SameBytes(e, e));
            Assert.False(BackupRules.SameBytes(a, Path.Combine(d, "missing")));   // unreadable is not the same as anything
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void The_registry_verify_compares_bytes_not_names()
    {
        string d = Path.Combine(Path.GetTempPath(), "haf_verify_" + Guid.NewGuid().ToString("N"));
        string live = Path.Combine(d, "live"), snap = Path.Combine(d, "snap");
        string packRoot = Path.Combine(live, "Assets", "Pack"), cfgPacks = Path.Combine(live, "config", "haf_packs");
        try
        {
            Directory.CreateDirectory(Path.Combine(packRoot, "ENCReload"));
            Directory.CreateDirectory(Path.Combine(cfgPacks, "ENCReload"));
            File.WriteAllText(Path.Combine(packRoot, "ENCReload", "pack.json"), "{\"models\":[{\"n\":\"Tank\",\"v\":5}]}");
            File.WriteAllText(Path.Combine(cfgPacks, "ENCReload", "pack.json"), "{\"models\":[{\"n\":\"Tank\",\"v\":5}]}");
            var groups = new[]
            {
                new KeyValuePair<string, IEnumerable<string>>("pack", new[] { packRoot }),
                new KeyValuePair<string, IEnumerable<string>>("config", new[] { Path.Combine(live, "config", "haf_x.json"), cfgPacks }),
                new KeyValuePair<string, IEnumerable<string>>("resources", new[] { Path.Combine(live, "Assets", "Resources") }),
            };
            // nothing landed yet: both missing, both counted
            string v = BackupRules.VerifyRegistryCopies(snap, groups, out int src, out int dep);
            Assert.NotNull(v); Assert.Contains("MISSING", v); Assert.Contains("Assets/Pack/ENCReload/pack.json", v); Assert.Contains("haf_packs/ENCReload/pack.json", v);
            Assert.Equal(1, src); Assert.Equal(1, dep);
            // the snapshot's layout: <snap>/<group>/<root leaf>/<rel>
            Directory.CreateDirectory(Path.Combine(snap, "pack", "Pack", "ENCReload"));
            Directory.CreateDirectory(Path.Combine(snap, "config", "haf_packs", "ENCReload"));
            File.Copy(Path.Combine(packRoot, "ENCReload", "pack.json"), Path.Combine(snap, "pack", "Pack", "ENCReload", "pack.json"));
            File.Copy(Path.Combine(cfgPacks, "ENCReload", "pack.json"), Path.Combine(snap, "config", "haf_packs", "ENCReload", "pack.json"));
            Assert.Null(BackupRules.VerifyRegistryCopies(snap, groups, out src, out dep));
            Assert.Equal(1, src); Assert.Equal(1, dep);
            // the reported case: a SAME-LENGTH edit of the live registry after the copy - a name is there, the bytes are not
            File.WriteAllText(Path.Combine(packRoot, "ENCReload", "pack.json"), "{\"models\":[{\"n\":\"Tank\",\"v\":6}]}");
            v = BackupRules.VerifyRegistryCopies(snap, groups, out src, out dep);
            Assert.NotNull(v); Assert.Contains("DIFFERENT version of Assets/Pack/ENCReload/pack.json", v); Assert.DoesNotContain("MISSING", v);
            // a pack group whose root is gone verifies nothing and counts nothing - but a config group still does
            var packGone = new[] { new KeyValuePair<string, IEnumerable<string>>("pack", new[] { Path.Combine(live, "nowhere") }), groups[1] };
            Assert.Null(BackupRules.VerifyRegistryCopies(snap, packGone, out src, out dep));
            Assert.Equal(0, src); Assert.Equal(1, dep);
            // a snapshot with no registry group asserts nothing
            Assert.Null(BackupRules.VerifyRegistryCopies(snap, new[] { groups[2] }, out src, out dep));
            Assert.Equal(0, src); Assert.Equal(0, dep);
        }
        finally { Directory.Delete(d, true); }
    }

}
