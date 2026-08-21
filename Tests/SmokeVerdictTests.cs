using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The in-game smoke harness's runtime side can't be unit-tested (needs the game), but its VERDICT is a pure function.
    // These lock the PASS/FAIL rules so the harness's assertion stays trustworthy.
    public class SmokeVerdictTests
    {
        [Fact]
        public void Pass_WhenAllGood()
        {
            var r = UniversalInject.SmokeVerdict(gbMissing: 0, injectionErrors: 0, models: 22, repointed: 5);
            Assert.True(r.Pass);
            Assert.Contains("PASS", r.Summary);
        }

        [Fact]
        public void Fail_OnMissingBinding()
        {
            var r = UniversalInject.SmokeVerdict(gbMissing: 2, injectionErrors: 0, models: 22, repointed: 5);
            Assert.False(r.Pass);
            Assert.Contains("2 game type/member(s) missing", r.Summary);
        }

        [Fact]
        public void Fail_OnInjectionError()
        {
            var r = UniversalInject.SmokeVerdict(gbMissing: 0, injectionErrors: 3, models: 22, repointed: 5);
            Assert.False(r.Pass);
            Assert.Contains("3 injection error(s)", r.Summary);
        }

        [Fact]
        public void Fail_OnNoModelsLoaded()
        {
            var r = UniversalInject.SmokeVerdict(gbMissing: 0, injectionErrors: 0, models: 0, repointed: 0);
            Assert.False(r.Pass);
            Assert.Contains("no models loaded", r.Summary);
        }

        [Fact]
        public void Fail_ReportsAllReasonsAtOnce()
        {
            var r = UniversalInject.SmokeVerdict(gbMissing: 1, injectionErrors: 2, models: 0, repointed: 0);
            Assert.False(r.Pass);
            Assert.Contains("missing", r.Summary);
            Assert.Contains("injection error", r.Summary);
            Assert.Contains("no models loaded", r.Summary);
        }

        [Fact]
        public void RepointedZero_StillPasses_WhenNoUnitsPresent()
        {
            // repointed is informational (depends which units are on the map), so 0 injected is NOT a failure by itself
            var r = UniversalInject.SmokeVerdict(gbMissing: 0, injectionErrors: 0, models: 22, repointed: 0);
            Assert.True(r.Pass);
        }

        // ---- depth pass (2026-08-17): the four per-entry deep checks, each earned by a shipped bug class ----

        static UniversalInject.SmokeFacts Healthy() =>
            new UniversalInject.SmokeFacts { GbMissing = 0, InjectionErrors = 0, Models = 22, Repointed = 5 };

        [Fact]
        public void Fail_OnDeadClipRole_NamesEntryAndRole()
        {
            var f = Healthy();
            f.DeadRoles.Add("Howitzer idleOverride");   // the "forgot to deploy" trap, now a named FAIL
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("1 dead clip role(s)", r.Summary);
            Assert.Contains("Howitzer idleOverride", r.Summary);
        }

        [Fact]
        public void Fail_OnMissingAsset()
        {
            var f = Healthy();
            f.MissingAssets.Add("OrganGun atlas");
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("missing asset", r.Summary);
            Assert.Contains("OrganGun atlas", r.Summary);
        }

        [Fact]
        public void Fail_OnFailedSound()
        {
            var f = Healthy();
            f.FailedSounds.Add("Zeppelin loop 'engine.wav'");
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("sound file(s) failed to load", r.Summary);
            Assert.Contains("engine.wav", r.Summary);
        }

        [Fact]
        public void Fail_OnBudgetAlarm()
        {
            var f = Healthy();
            f.BudgetAlarms.Add("L2 'MeshWithSkeleton' verts 97% / idx 61%");
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("near the wall", r.Summary);
            Assert.Contains("97%", r.Summary);
        }

        [Fact]
        public void DeepChecks_AllReasonsSurfaceAtOnce_WithTheShallowOnes()
        {
            var f = new UniversalInject.SmokeFacts { GbMissing = 1, InjectionErrors = 0, Models = 22, Repointed = 5 };
            f.DeadRoles.Add("A move");
            f.FailedSounds.Add("B loop 'x.wav'");
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("missing", r.Summary);
            Assert.Contains("dead clip role", r.Summary);
            Assert.Contains("x.wav", r.Summary);
        }

        [Fact]
        public void Pass_SaysDeepChecksClean()
        {
            // a PASS must claim only what was checked: the summary names the deep-check families explicitly
            var r = UniversalInject.SmokeVerdict(Healthy());
            Assert.True(r.Pass);
            Assert.Contains("deep checks clean", r.Summary);
        }

        [Fact]
        public void Pass_ShowsItsWork_WithCoverageCounts()
        {
            // "checked 47 roles" is auditable; "clean" alone is not — the PASS line must carry the counters
            var f = Healthy();
            f.RolesChecked = 47; f.AssetsChecked = 17; f.SoundsChecked = 12; f.FilesChecked = 9; f.LayersChecked = 3;
            var r = UniversalInject.SmokeVerdict(f);
            Assert.True(r.Pass);
            Assert.Contains("verified 47 clip role(s), 17 asset(s), 12 sound(s), 9 file(s) on disk, 3 GPU layer(s)", r.Summary);
        }

        [Fact]
        public void Gather_AuthoredSkinPng_NoTextureLanded_IsMissing()
        {
            var e = new ModelEntry { resourceName = "R", repointed = true, textureFile = "camo.png" };
            var f = new UniversalInject.SmokeFacts();
            UniversalInject.GatherEntryFacts(e, f);
            Assert.Contains("R skin 'camo.png'", f.MissingAssets);
        }

        [Fact]
        public void Gather_AuthoredHandProp_MissingLayer_IsMissing()
        {
            var e = new ModelEntry { resourceName = "M60", repointed = true, handPropGuid = "1,2,3,4" };
            var f = new UniversalInject.SmokeFacts();
            UniversalInject.GatherEntryFacts(e, f);
            Assert.Contains("M60 hand-prop layer", f.MissingAssets);
        }

        [Fact]
        public void GatherDistrict_GroundMaterialNameNotFound_IsIssue_UnresolvedIsPending()
        {
            var bad = new DistrictInject.DistrictModel { district = "Reactor", fxMeshGuid = new object(), groundMaterial = "Prairie_Grasland", groundIdx = -1 };
            var f = new UniversalInject.SmokeFacts();
            UniversalInject.GatherDistrictFacts(bad, f);
            Assert.Contains("'Reactor' ground material 'Prairie_Grasland' not found", f.DistrictIssues);

            var pending = new DistrictInject.DistrictModel { district = "R2", fxMeshGuid = new object(), groundMaterial = "Prairie_Grassland", groundIdx = int.MinValue };
            var f2 = new UniversalInject.SmokeFacts();
            UniversalInject.GatherDistrictFacts(pending, f2);
            Assert.Empty(f2.DistrictIssues);   // not-yet-resolved is pending, never a failure
            Assert.Equal(1, f2.DistrictsChecked);
        }

        [Fact]
        public void GatherDistrict_UnparsedFxMeshGuid_IsIssue()
        {
            var d = new DistrictInject.DistrictModel { district = "Silo", fxMeshGuid = null };
            var f = new UniversalInject.SmokeFacts();
            UniversalInject.GatherDistrictFacts(d, f);
            Assert.Contains("'Silo' fxMesh GUID unparsed", f.DistrictIssues);
        }

        // Drill 2026-08-21: the reactor (SCOPED path) was bound across 1 tile per the log, yet the smoke said
        // "0 tiles live — district path UNTESTED" because it only read the ISOLATE ledger (d.tiles). The scoped
        // ledger is passed in by the caller; the pure fact must count it, label it, and drop the vacuous note.
        [Fact]
        public void GatherDistrict_ScopedPath_CountsScopedTiles_NotIsolateLedger()
        {
            var reactor = new DistrictInject.DistrictModel { district = "Extension_Base_BreederReactor", fxMeshGuid = new object(), selectorGuid = new object() };
            var f = new UniversalInject.SmokeFacts { Models = 3, Repointed = 1 };
            UniversalInject.GatherDistrictFacts(reactor, f, scoped: true, scopedTiles: 1);
            Assert.Equal(1, f.TilesActive);
            Assert.Equal(1, f.ScopedTilesActive);
            Assert.Empty(f.DistrictIssues);
            var r = UniversalInject.SmokeVerdict(f);
            Assert.True(r.Pass);
            Assert.Contains("1 district(s) [1 tile(s) live, 1 scoped]", r.Summary);
            Assert.DoesNotContain("UNTESTED", r.Summary);

            // a scoped district with stale isolate tiles (never the case, but the ledgers must not be summed)
            reactor.tiles.Add(new DistrictInject.DistrictModel.TileState());
            var f2 = new UniversalInject.SmokeFacts();
            UniversalInject.GatherDistrictFacts(reactor, f2, scoped: true, scopedTiles: 0);
            Assert.Equal(0, f2.TilesActive);   // scoped reads ONLY the scoped ledger
        }

        // TEXTURE HEALTH (2026-08-21). A live tile proves the mesh bound, not that the albedo landed. Both apply paths
        // give up after 3 exceptions by latching texApplied=true — so the judgement must read texErrors FIRST, or a
        // district rendering untextured passes as "applied".
        static DistrictInject.DistrictModel TexturedReactor() =>
            new DistrictInject.DistrictModel { district = "Extension_Base_BreederReactor", fxMeshGuid = new object(), atlasGuid = new object(), selectorGuid = new object() };

        [Fact]
        public void DistrictTexture_Applied_CountsInPassLine()
        {
            var f = new UniversalInject.SmokeFacts { Models = 3, Repointed = 1 };
            UniversalInject.GatherDistrictFacts(TexturedReactor(), f, scoped: true, scopedTiles: 1,
                new UniversalInject.DistrictTexState { Textured = true, Applied = true, Errors = 0, Wait = 900 });
            Assert.Equal(1, f.TexturedChecked); Assert.Equal(1, f.TexturedApplied);
            Assert.Empty(f.DistrictIssues); Assert.Empty(f.DistrictNotes);
            var r = UniversalInject.SmokeVerdict(f);
            Assert.True(r.Pass);
            Assert.Contains("[1 tile(s) live, 1 scoped, 1/1 textured]", r.Summary);
        }

        [Fact]
        public void DistrictTexture_GaveUp_FailsEvenThoughAppliedLatched()
        {
            var f = new UniversalInject.SmokeFacts { Models = 3, Repointed = 1 };
            // the exact give-up signature: Applied=true (the latch) AND Errors>=3
            UniversalInject.GatherDistrictFacts(TexturedReactor(), f, scoped: true, scopedTiles: 1,
                new UniversalInject.DistrictTexState { Textured = true, Applied = true, Errors = UniversalInject.TexGiveUpErrors, Wait = 10 });
            Assert.Equal(1, f.TexturedChecked); Assert.Equal(0, f.TexturedApplied);
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("'Extension_Base_BreederReactor' texture apply GAVE UP after 3 error(s)", r.Summary);
        }

        [Fact]
        public void DistrictTexture_Pending_IsNoteNeverFail()
        {
            var f = new UniversalInject.SmokeFacts { Models = 3, Repointed = 1 };
            UniversalInject.GatherDistrictFacts(TexturedReactor(), f, scoped: true, scopedTiles: 1,
                new UniversalInject.DistrictTexState { Textured = true, Applied = false, Errors = 1, Wait = UniversalInject.TexPendingPolls });   // one transient error, still retrying
            var r = UniversalInject.SmokeVerdict(f);
            Assert.True(r.Pass);
            Assert.Contains("NOTE: 'Extension_Base_BreederReactor' texture still pending after 300 polls", r.Summary);
            Assert.Contains("0/1 textured", r.Summary);

            var f2 = new UniversalInject.SmokeFacts { Models = 3, Repointed = 1 };
            UniversalInject.GatherDistrictFacts(TexturedReactor(), f2, scoped: true, scopedTiles: 1,
                new UniversalInject.DistrictTexState { Textured = true, Applied = false, Errors = 0, Wait = 5 });
            Assert.Contains("texture pending (just bound", UniversalInject.SmokeVerdict(f2).Summary);
        }

        [Fact]
        public void DistrictTexture_NotJudged_WhenUntexturedOrOffScreen()
        {
            // untextured by design (pre-2.0 entry, no atlas): nothing to judge even with live tiles
            var f = new UniversalInject.SmokeFacts { Models = 3, Repointed = 1 };
            var plain = new DistrictInject.DistrictModel { district = "Oracle", fxMeshGuid = new object(), atlasGuid = null };
            plain.tiles.Add(new DistrictInject.DistrictModel.TileState());
            UniversalInject.GatherDistrictFacts(plain, f);
            Assert.Equal(0, f.TexturedChecked);
            Assert.DoesNotContain("textured", UniversalInject.SmokeVerdict(f).Summary);

            // textured but off-screen (0 live tiles): it hasn't tried yet — a stale gave-up counter must not fail it
            var f2 = new UniversalInject.SmokeFacts { Models = 3, Repointed = 1 };
            UniversalInject.GatherDistrictFacts(TexturedReactor(), f2, scoped: true, scopedTiles: 0,
                new UniversalInject.DistrictTexState { Textured = true, Applied = false, Errors = 3, Wait = 0 });
            Assert.Equal(0, f2.TexturedChecked); Assert.Empty(f2.DistrictIssues);
        }

        [Fact]
        public void DistrictTexture_IsolateOverload_ReadsTheModelLedger()
        {
            var f = new UniversalInject.SmokeFacts { Models = 3, Repointed = 1 };
            var d = new DistrictInject.DistrictModel { district = "Silo", fxMeshGuid = new object(), atlasGuid = new object(), texApplied = true, texErrors = 3 };
            d.tiles.Add(new DistrictInject.DistrictModel.TileState());
            UniversalInject.GatherDistrictFacts(d, f);   // 2-arg overload lifts tex state off DistrictModel
            Assert.Contains(f.DistrictIssues, s => s.StartsWith("'Silo' texture apply GAVE UP"));
        }

        [Fact]
        public void GatherDistrict_IsolatePath_Unchanged_NoScopedLabel()
        {
            var oracle = new DistrictInject.DistrictModel { district = "Oracle", fxMeshGuid = new object() };
            oracle.tiles.Add(new DistrictInject.DistrictModel.TileState()); oracle.tiles.Add(new DistrictInject.DistrictModel.TileState());
            var f = new UniversalInject.SmokeFacts { Models = 3, Repointed = 1 };
            UniversalInject.GatherDistrictFacts(oracle, f);   // back-compat overload = isolate
            Assert.Equal(2, f.TilesActive);
            Assert.Equal(0, f.ScopedTilesActive);
            Assert.Contains("[2 tile(s) live]", UniversalInject.SmokeVerdict(f).Summary);   // no ", N scoped" suffix when none
        }

        [Fact]
        public void Verdict_DistrictIssues_FailAndAreNamed_CountersInPassLine()
        {
            var f = Healthy();
            f.DistrictIssues.Add("'Reactor' ground material 'X' not found");
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("district issue", r.Summary);

            var ok = Healthy(); ok.DistrictsChecked = 2; ok.TilesActive = 5;
            var r2 = UniversalInject.SmokeVerdict(ok);
            Assert.True(r2.Pass);
            Assert.Contains("2 district(s) [5 tile(s) live]", r2.Summary);
        }

        [Fact]
        public void LooseFiles_MissingIsNamed_PresentInEitherDirIsClean_CheckedWithoutInjection()
        {
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "haf-loosefile-" + System.Guid.NewGuid().ToString("N"));
            var shared = System.IO.Path.Combine(tmp, "haf_sounds"); var skins = System.IO.Path.Combine(tmp, "haf_skins");
            var packDir = System.IO.Path.Combine(tmp, "pack"); var packSounds = System.IO.Path.Combine(packDir, "sounds");
            System.IO.Directory.CreateDirectory(shared); System.IO.Directory.CreateDirectory(skins); System.IO.Directory.CreateDirectory(packSounds);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(shared, "inShared.wav"), "x");
                System.IO.File.WriteAllText(System.IO.Path.Combine(packSounds, "inPack.wav"), "x");

                // NOT repointed, NOT customClipTried — the sweep must still check (the whole point: units absent
                // from the current save get their files verified too)
                var e = new ModelEntry { resourceName = "Z", assetDir = packDir,
                                         soundFile = "inPack.wav", soundIdleFile = "inShared.wav", soundAttackFile = "gone.wav",
                                         textureFile = "gone.png" };
                var f = new UniversalInject.SmokeFacts();
                UniversalInject.CheckLooseFiles(e, f, shared, skins);
                Assert.Equal(4, f.FilesChecked);
                Assert.Contains("Z attack 'gone.wav'", f.MissingFiles);
                Assert.Contains("Z skin 'gone.png'", f.MissingFiles);
                Assert.Equal(2, f.MissingFiles.Count);   // the two present files (pack dir + shared dir) are clean
            }
            finally { System.IO.Directory.Delete(tmp, true); }
        }

        // ---- shared-seam census: REAL Harmony patches in the test host, two owners on one method ----

        public static void SeamDummy() { }          // patch target
        static void SeamNoop() { }                   // patch implementation

        [Fact]
        public void SharedSeams_ForeignOwnerOnOurMethod_IsNamed_ButNeverFails()
        {
            var ours = new HarmonyLib.Harmony("test.haf");
            var other = new HarmonyLib.Harmony("test.neighbor");
            var target = typeof(SmokeVerdictTests).GetMethod(nameof(SeamDummy));
            var noop = new HarmonyLib.HarmonyMethod(typeof(SmokeVerdictTests).GetMethod(nameof(SeamNoop), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
            try
            {
                ours.Patch(target, postfix: noop);
                other.Patch(target, postfix: noop);

                var f = new UniversalInject.SmokeFacts { Models = 22, Repointed = 1 };
                UniversalInject.GatherSharedSeams(f, "test.haf");
                Assert.True(f.SeamsChecked >= 1);
                Assert.Contains(f.SharedSeams, s => s.Contains("SeamDummy") && s.Contains("test.neighbor"));

                // a shared seam is information, NOT a failure — and both the census and the sharers show in the summary
                var r = UniversalInject.SmokeVerdict(f);
                Assert.True(r.Pass);
                Assert.Contains($"{f.SeamsChecked} patched seam(s) [{f.SharedSeams.Count} shared]", r.Summary);
                Assert.Contains("test.neighbor", r.Summary);
            }
            finally { ours.UnpatchSelf(); other.UnpatchSelf(); }
        }

        [Fact]
        public void SharedSeams_OurSeamAlone_CountsButListsNothing()
        {
            var ours = new HarmonyLib.Harmony("test.haf.solo");
            var target = typeof(SmokeVerdictTests).GetMethod(nameof(SeamDummy));
            var noop = new HarmonyLib.HarmonyMethod(typeof(SmokeVerdictTests).GetMethod(nameof(SeamNoop), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
            try
            {
                ours.Patch(target, postfix: noop);
                var f = new UniversalInject.SmokeFacts();
                UniversalInject.GatherSharedSeams(f, "test.haf.solo");
                Assert.True(f.SeamsChecked >= 1);
                Assert.DoesNotContain(f.SharedSeams, s => s.Contains("SeamDummy"));
            }
            finally { ours.UnpatchSelf(); }
        }

        [Fact]
        public void Gather_CountsWhatItChecks()
        {
            var e = new ModelEntry { resourceName = "C", repointed = true, sa = 1, ta = 1, moveAnimId = 4,
                                     soundFile = "a.wav", customClipTried = true };
            e.Role(ClipRole.Move).Set(1, 0, 0, 0);   // one authored role (move)
            var f = new UniversalInject.SmokeFacts();
            UniversalInject.GatherEntryFacts(e, f);
            Assert.Equal(1, f.RolesChecked);    // one authored role (move)
            Assert.Equal(2, f.AssetsChecked);   // skeleton + atlas authored
            Assert.Equal(1, f.SoundsChecked);   // one configured sound
        }

        // ---- GatherEntryFacts (pure over ModelEntry) — pinned after the FIRST live run caught a false positive:
        // ---- the skeleton check fired on a retexture-only entry, which legitimately has no skeleton. ----

        [Fact]
        public void Gather_RetexOnlyEntry_NoSkeletonAuthored_IsHealthy()
        {
            var e = new ModelEntry { resourceName = "Retex_X", repointed = true };   // no skel/atlas GUIDs authored
            var f = new UniversalInject.SmokeFacts();
            UniversalInject.GatherEntryFacts(e, f);
            Assert.Empty(f.MissingAssets);
            Assert.Empty(f.DeadRoles);
        }

        [Fact]
        public void Gather_AuthoredSkeletonNotLoaded_IsMissing()
        {
            var e = new ModelEntry { resourceName = "X", repointed = true, sa = 1 };
            var f = new UniversalInject.SmokeFacts();
            UniversalInject.GatherEntryFacts(e, f);
            Assert.Contains("X skeleton", f.MissingAssets);
        }

        [Fact]
        public void Gather_NotRepointed_SkipsDeepChecks()
        {
            var e = new ModelEntry { resourceName = "P", sa = 1 };   // authored but never injected this session
            var f = new UniversalInject.SmokeFacts();
            UniversalInject.GatherEntryFacts(e, f);
            Assert.Empty(f.MissingAssets);
        }

        // ---- 2026-08-19 five-point upgrade ----

        [Fact]
        public void SeamWriteBack_Failed_FailsTheSmoke_SkippedAndOkDoNot()
        {
            var bad = UniversalInject.SmokeVerdict(new UniversalInject.SmokeFacts { Models = 1, SeamWriteBack = "FAILED (died in the box)" });
            Assert.False(bad.Pass);
            Assert.Contains("write-back self-test FAILED", bad.Summary);
            var ok = UniversalInject.SmokeVerdict(new UniversalInject.SmokeFacts { Models = 1, SeamWriteBack = "ok" });
            Assert.True(ok.Pass);
            Assert.Contains("seam write-back ok", ok.Summary);
            var skipped = UniversalInject.SmokeVerdict(new UniversalInject.SmokeFacts { Models = 1, SeamWriteBack = "skipped (no live pawns)" });
            Assert.True(skipped.Pass);   // nothing to probe is not a failure — but it says so
            Assert.Contains("skipped (no live pawns)", skipped.Summary);
        }

        [Fact]
        public void Gather_NotRepointed_IsNamedWithReason_ButInformational()
        {
            var f = new UniversalInject.SmokeFacts { Models = 2, Repointed = 1 };
            UniversalInject.GatherEntryFacts(new ModelEntry { resourceName = "P", sa = 1 }, f);
            UniversalInject.GatherEntryFacts(new ModelEntry { resourceName = "D", disabled = true }, f);
            Assert.Contains("P (no unit on the map this session)", f.Uninjected);
            Assert.Contains("D (disabled)", f.Uninjected);
            var res = UniversalInject.SmokeVerdict(f);
            Assert.True(res.Pass);   // never a failure — but the delta is NAMED
            Assert.Contains("awaiting injection: P (no unit on the map this session), D (disabled)", res.Summary);
        }

        [Fact]
        public void VacuousCoverage_IsNoted_NeverFailed()
        {
            var f = new UniversalInject.SmokeFacts { Models = 3, Repointed = 1, DistrictsChecked = 2, TilesActive = 0 };
            var res = UniversalInject.SmokeVerdict(f);
            Assert.True(res.Pass);
            Assert.Contains("NOTE: districts authored but 0 tiles live", res.Summary);
            var f2 = new UniversalInject.SmokeFacts { Models = 3, Repointed = 0 };
            var res2 = UniversalInject.SmokeVerdict(f2);
            Assert.True(res2.Pass);
            Assert.Contains("NOTE: no entries injected", res2.Summary);
        }

        [Fact]
        public void Gather_SamplerStarved_IsNoted_NeverFailed()
        {
            var e = new ModelEntry { resourceName = "Sub", repointed = true, combatZ = -0.13f };   // needs the sampler, has no samples
            var f = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
            UniversalInject.GatherEntryFacts(e, f);
            Assert.Single(f.SamplerNotes);
            var res = UniversalInject.SmokeVerdict(f);
            Assert.True(res.Pass);
            Assert.Contains("NOTE: state sampler has no samples for 'Sub'", res.Summary);
        }

        [Fact]
        public void Gather_ConfiguredSound_FailsOnceTried_PendingBeforeThat()
        {
            var tried = new ModelEntry { resourceName = "S", soundFile = "engine.wav", customClipTried = true };
            var f = new UniversalInject.SmokeFacts();
            UniversalInject.GatherEntryFacts(tried, f);
            Assert.Contains("S loop 'engine.wav'", f.FailedSounds);

            var pending = new ModelEntry { resourceName = "S2", soundFile = "engine.wav" };   // audio poll hasn't tried yet
            var f2 = new UniversalInject.SmokeFacts();
            UniversalInject.GatherEntryFacts(pending, f2);
            Assert.Empty(f2.FailedSounds);
        }

        // The wiring guard, table edition (was the "36-int" reflection test over nine hand-named quads — the `alc`
        // component the FIRST DRAFT of GatherEntryFacts dropped is why it existed). Every GUID component of every
        // role must arm its role's dead-role check on its own, and the role must be reported under its table name.
        [Theory]
        [InlineData(ClipRole.Primary, "primary")]
        [InlineData(ClipRole.Move, "move")]
        [InlineData(ClipRole.After, "after")]
        [InlineData(ClipRole.Attack, "attack")]
        [InlineData(ClipRole.Combat, "combat")]
        [InlineData(ClipRole.PreMove, "preMove")]
        [InlineData(ClipRole.IdleOverride, "idleOverride")]
        [InlineData(ClipRole.IdleAlt, "idleAlt")]
        [InlineData(ClipRole.IdleAlt2, "idleAlt2")]
        public void Gather_DeadRole_EveryGuidComponentArmsItsRole(ClipRole role, string reportedName)
        {
            Assert.Equal(reportedName, ClipRoles.Name(role));
            for (int component = 0; component < 4; component++)
            {
                var e = new ModelEntry { resourceName = "T", repointed = true };
                e.Role(role).Set(component == 0 ? 1 : 0, component == 1 ? 1 : 0, component == 2 ? 1 : 0, component == 3 ? 1 : 0);
                var f = new UniversalInject.SmokeFacts();
                UniversalInject.GatherEntryFacts(e, f);
                Assert.True(f.DeadRoles.Count == 1 && f.DeadRoles[0] == "T " + reportedName,
                    $"GUID component {component} of {role} did not arm its dead-role check");
            }
        }
        // THE _DRILL CLASS (2026-08-21): TankDestroyers shipped with pawnDescription "…_01_DRILL" for weeks; the game loaded
        // "…_01", nothing matched, the unit rendered as its donor, and the smoke said "no unit on the map this session".
        [Fact]
        public void Uninjected_StraySuffix_IsNamedMismatch_AndFails()
        {
            var e = new ModelEntry { resourceName = "TankDestroyers", pawnDescription = "Era6_Common_TankDestroyers_01_DRILL" };
            var seen = new[] { "Era6_Common_TankDestroyers_01", "Era6_Common_UniversalTanks_01" };
            var reason = UniversalInject.UninjectedReason(e, seen, out bool mismatch);
            Assert.True(mismatch);
            Assert.Contains("it loaded 'Era6_Common_TankDestroyers_01'", reason);
            Assert.Contains("stray suffix '_DRILL'", reason);
            var f = new UniversalInject.SmokeFacts { Models = 22, Repointed = 17 };
            UniversalInject.GatherEntryFacts(e, f, seen);
            Assert.Single(f.MatchIssues); Assert.Empty(f.Uninjected);
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass);
            Assert.Contains("cannot match the loaded unit", r.Summary);
        }

        [Fact]
        public void Uninjected_NoUnitOnMap_StaysInformational()
        {
            var e = new ModelEntry { resourceName = "Canoe", pawnDescription = "Era1_Common_DugoutCanoe_01" };
            Assert.Equal("no unit on the map this session", UniversalInject.UninjectedReason(e, new[] { "Era6_Common_UniversalTanks_01" }, out bool m1)); Assert.False(m1);
            Assert.Equal("no unit on the map this session", UniversalInject.UninjectedReason(e, null, out bool m2)); Assert.False(m2);
            Assert.Equal("disabled", UniversalInject.UninjectedReason(new ModelEntry { disabled = true, pawnDescription = "x" }, null, out _));
            // the addon DID load and WOULD match -> a different diagnosis (repoint failure), never "no unit"
            Assert.Contains("repoint did not run", UniversalInject.UninjectedReason(e, new[] { "Era1_Common_DugoutCanoe_01" }, out bool m3)); Assert.False(m3);
        }

        // The injection-error ledger (2026-08-21): named sites, counted once each, per session — not a frame counter.
        [Fact]
        public void InjectionErrors_CountOncePerSite_AndNameTheSites()
        {
            lock (UniversalInject.InjectionErrorSites) { UniversalInject.InjectionErrorSites.Clear(); UniversalInject.InjectionErrors = 0; }
            for (int frame = 0; frame < 500; frame++) UniversalInject.NoteInjectionError("pose:TankDestroyers");   // one throwing model, 500 frames
            UniversalInject.NoteInjectionError("repoint");
            UniversalInject.NoteInjectionError("repoint");
            Assert.Equal(2, UniversalInject.InjectionErrors);
            Assert.Equal(new[] { "pose:TankDestroyers", "repoint" }, UniversalInject.ErrorSitesSnapshot().ToArray());

            var r = UniversalInject.SmokeVerdict(new UniversalInject.SmokeFacts
            {
                GbMissing = 0, Models = 22, Repointed = 5,
                InjectionErrors = UniversalInject.InjectionErrors, ErrorSites = UniversalInject.ErrorSitesSnapshot(),
            });
            Assert.False(r.Pass);
            Assert.Contains("2 injection error(s) at pose:TankDestroyers, repoint", r.Summary);
            lock (UniversalInject.InjectionErrorSites) { UniversalInject.InjectionErrorSites.Clear(); UniversalInject.InjectionErrors = 0; }
        }


        // ---- LIVE-PAWN TRUTH (2026-08-21): skeleton truth, pose-hook liveness, sub-pawn walk coverage ----
        static ModelEntry Live(string name, int desc, int skel, float lastHook) =>
            new ModelEntry { resourceName = name, repointed = true, descId = desc, skeletonId = skel, lastPoseHookAt = lastHook };

        [Fact]
        public void LivePawns_AllOnOurSkeleton_AndHookFresh_IsClean()
        {
            var f = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
            var e = Live("Tank", desc: 40, skel: 7, lastHook: 99f);
            UniversalInject.GatherLivePawnFacts(new[] { new UniversalInject.LiveSlot(40, 7), new UniversalInject.LiveSlot(40, 7), new UniversalInject.LiveSlot(3, 1) }, new[] { e }, now: 100f, f);
            Assert.Equal(2, f.LivePawnsChecked);   // the vanilla slot (desc 3) is not ours — not counted
            Assert.Equal(1, f.EntriesWithLivePawns);
            Assert.Empty(f.PawnSkinIssues); Assert.Empty(f.PoseIdle);
            Assert.True(UniversalInject.SmokeVerdict(f).Pass);
        }

        [Fact]
        public void LivePawns_OnDonorSkeleton_FailsAndNamesTheEntry()
        {
            var f = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
            var e = Live("TankDestroyers", desc: 40, skel: 7, lastHook: 99f);
            UniversalInject.GatherLivePawnFacts(new[] { new UniversalInject.LiveSlot(40, 7), new UniversalInject.LiveSlot(40, 2) }, new[] { e }, now: 100f, f);
            var issue = Assert.Single(f.PawnSkinIssues);
            Assert.Contains("TankDestroyers: 1 of 2 live pawn(s) on skeleton 2, ours is 7", issue);
            var r = UniversalInject.SmokeVerdict(f);
            Assert.False(r.Pass); Assert.Contains("rendering the donor", r.Summary);
        }

        [Fact]
        public void LivePawns_HookIdleOrNeverRun_Fails()
        {
            var f = new UniversalInject.SmokeFacts { Models = 2, Repointed = 2 };
            var stale = Live("Heli", desc: 40, skel: 7, lastHook: 80f);    // 20 s ago
            var never = Live("Drone", desc: 41, skel: 8, lastHook: -1f);
            UniversalInject.GatherLivePawnFacts(new[] { new UniversalInject.LiveSlot(40, 7), new UniversalInject.LiveSlot(41, 8) }, new[] { stale, never }, now: 100f, f);
            Assert.Equal(2, f.PoseIdle.Count);
            Assert.Contains(f.PoseIdle, s => s.StartsWith("Heli:") && s.Contains("last ran 20s ago"));
            Assert.Contains(f.PoseIdle, s => s.StartsWith("Drone:") && s.Contains("never run"));
            Assert.False(UniversalInject.SmokeVerdict(f).Pass);
        }

        [Fact]
        public void LivePawns_EntryWithoutLiveSlots_IsNotJudgedForLiveness()
        {
            var f = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
            var e = Live("Zeppelin", desc: 40, skel: 7, lastHook: -1f);   // injected, but no unit on the map right now
            UniversalInject.GatherLivePawnFacts(new UniversalInject.LiveSlot[0], new[] { e }, now: 100f, f);
            Assert.Empty(f.PoseIdle); Assert.Equal(0, f.EntriesWithLivePawns);
        }

        [Fact]
        public void SubPawnWalk_MissedSubPawn_Fails_AndCoverageShowsInSummary()
        {
            var ok = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1, SubPawnWalk = 6, SubPawnScene = 6 };
            var r = UniversalInject.SmokeVerdict(ok);
            Assert.True(r.Pass); Assert.Contains("sub-pawn walk 6/6", r.Summary);

            var bad = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1, SubPawnWalk = 5, SubPawnScene = 6 };
            bad.SubPawnMissed.Add("Zeppelin_Body→ReconZeppelin");
            r = UniversalInject.SmokeVerdict(bad);
            Assert.False(r.Pass); Assert.Contains("sub-pawn walk missed 1 of 6: Zeppelin_Body→ReconZeppelin", r.Summary);
        }

        [Fact]
        public void LivePawns_RetextureOnlyEntry_RidesVanillaSkeleton_NotJudged()   // the stealth-corvette false FAIL, first in-game run
        {
            var f = new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 };
            var retex = Live("Retex_StealthCorvettes", desc: 40, skel: -1, lastHook: -1f);   // no skeleton authored, hook never matches it
            UniversalInject.GatherLivePawnFacts(new[] { new UniversalInject.LiveSlot(40, 53) }, new[] { retex }, now: 100f, f);
            Assert.Empty(f.PawnSkinIssues); Assert.Empty(f.PoseIdle); Assert.Equal(0, f.LivePawnsChecked);
            Assert.True(UniversalInject.SmokeVerdict(f).Pass);
        }

        [Fact]
        public void Tier_PrefixesTheVerdictLine()
        {
            var r = UniversalInject.SmokeVerdict(new UniversalInject.SmokeFacts { Tier = "load", Models = 22, Repointed = 3 });
            Assert.True(r.Pass); Assert.StartsWith("[load] PASS", r.Summary);
            r = UniversalInject.SmokeVerdict(new UniversalInject.SmokeFacts { Tier = "full", Models = 0 });
            Assert.StartsWith("[full] FAIL (", r.Summary);
            Assert.StartsWith("PASS", UniversalInject.SmokeVerdict(new UniversalInject.SmokeFacts { Models = 1, Repointed = 1 }).Summary);   // untagged stays as it was
        }
    }
}
