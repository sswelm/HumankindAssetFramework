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
            var bad = new UniversalInject.DistrictModel { district = "Reactor", fxMeshGuid = new object(), groundMaterial = "Prairie_Grasland", groundIdx = -1 };
            var f = new UniversalInject.SmokeFacts();
            UniversalInject.GatherDistrictFacts(bad, f);
            Assert.Contains("'Reactor' ground material 'Prairie_Grasland' not found", f.DistrictIssues);

            var pending = new UniversalInject.DistrictModel { district = "R2", fxMeshGuid = new object(), groundMaterial = "Prairie_Grassland", groundIdx = int.MinValue };
            var f2 = new UniversalInject.SmokeFacts();
            UniversalInject.GatherDistrictFacts(pending, f2);
            Assert.Empty(f2.DistrictIssues);   // not-yet-resolved is pending, never a failure
            Assert.Equal(1, f2.DistrictsChecked);
        }

        [Fact]
        public void GatherDistrict_UnparsedFxMeshGuid_IsIssue()
        {
            var d = new UniversalInject.DistrictModel { district = "Silo", fxMeshGuid = null };
            var f = new UniversalInject.SmokeFacts();
            UniversalInject.GatherDistrictFacts(d, f);
            Assert.Contains("'Silo' fxMesh GUID unparsed", f.DistrictIssues);
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
            var e = new ModelEntry { resourceName = "C", repointed = true, sa = 1, ta = 1, mca = 1, moveAnimId = 4,
                                     soundFile = "a.wav", customClipTried = true };
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

        // The 36-int wiring guard (the `cb`/`cbb` typo class the review flagged — and which the FIRST DRAFT of
        // GatherEntryFacts actually shipped: `Role(e.ala, e.alb, e.ald, e.ald, ...)` dropped `alc`). Every single
        // GUID component of every role must arm its role's dead-role check on its own.
        [Theory]
        [InlineData("ca", "cb", "cc", "cd", "primary")]
        [InlineData("mca", "mcb", "mcc", "mcd", "move")]
        [InlineData("aca", "acb", "acc", "acd", "after")]
        [InlineData("ata", "atb", "atc", "atd", "attack")]
        [InlineData("cba", "cbb", "cbc", "cbd", "combat")]
        [InlineData("pva", "pvb", "pvc", "pvd", "preMove")]
        [InlineData("iea", "ieb", "iec", "ied", "idleOverride")]
        [InlineData("ala", "alb", "alc", "ald", "idleAlt")]
        [InlineData("a2a", "a2b", "a2c", "a2d", "idleAlt2")]
        public void Gather_DeadRole_EveryGuidComponentArmsItsRole(string fa, string fb, string fc, string fd, string role)
        {
            foreach (var fieldName in new[] { fa, fb, fc, fd })
            {
                var e = new ModelEntry { resourceName = "T", repointed = true };
                typeof(ModelEntry).GetField(fieldName).SetValue(e, 1);
                var f = new UniversalInject.SmokeFacts();
                UniversalInject.GatherEntryFacts(e, f);
                Assert.True(f.DeadRoles.Count == 1 && f.DeadRoles[0] == "T " + role,
                    $"GUID component '{fieldName}' did not arm the '{role}' dead-role check (wiring typo?)");
            }
        }
    }
}
