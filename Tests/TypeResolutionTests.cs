using System;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // TYPE RESOLUTION goes through GameBinding.Cached, never a raw AccessTools.TypeByName (tools/check-catalog.sh fails
    // the gate on one). The reason is a measurement, 2026-08-23: TypeByName memoises NOTHING — 1,032 ns on a hit and
    // 7.85 MILLISECONDS on a miss, paid again on every single call. Cached is 19 ns / 13.7 µs.
    //
    // That swap is only safe because of ONE property, and it is the property with no test: Cached memoises the HIT and
    // deliberately does NOT memoise the MISS. HAF resolves game types from polls that start running BEFORE the game has
    // loaded the assembly that declares them — DistrictInject.SchematicVis asks for RenderFeatureProvider every 10
    // frames and gets null until the provider exists. A negative cache would latch that first null and the type would
    // never resolve for the rest of the session: the district visuals would simply never appear, with no error.
    //
    // So the test does the real thing rather than asserting the shape — it asks for a type that does not exist, then
    // brings it into being in a dynamic assembly, then asks again.
    //
    // DRILL RESULT, 2026-08-23 — read this before concluding the test is weak. `Cached` defends the property TWICE: the
    // write skips a null (`if (t != null) _typeCache[name] = t`) and the read ignores one (`&& c != null`). Mutating
    // either guard ALONE survives, because the other still covers it; mutating BOTH is caught. That is belt-and-braces,
    // not a hole — but it does mean a one-line change to Cached's caching will not trip this test. Check both lines.
    public class TypeResolutionTests
    {
        static void DefineTypeInANewAssembly(string ns, string name)
        {
            var an = new AssemblyName("HafLateAsm_" + name);
            var ab = AppDomain.CurrentDomain.DefineDynamicAssembly(an, AssemblyBuilderAccess.Run);
            var mb = ab.DefineDynamicModule("m");
            mb.DefineType(ns + "." + name, TypeAttributes.Public).CreateType();
        }

        // THE ONE THAT MATTERS. If this fails, every type HAF resolves before the game finishes loading is dead for the
        // session — the exact silent-nothing-happens failure the district subsystem would show.
        [Fact]
        public void AMissIsNotCached_SoATypeThatArrivesLaterStillResolves()
        {
            const string ns = "HafLate", n = "ArrivesLater";
            Assert.Null(GameBinding.Cached(ns + "." + n));    // not loaded yet — the poll's first few hundred calls
            DefineTypeInANewAssembly(ns, n);                  // ...the game loads the assembly...
            Assert.NotNull(GameBinding.Cached(ns + "." + n)); // ...and the next poll must see it
        }

        [Fact]
        public void AMissThroughAFallbackChainIsAlsoNotCached()
        {
            const string ns = "HafLate", n = "ArrivesLaterViaFallback";
            Assert.Null(GameBinding.Cached("HafLate.NeverExists", ns + "." + n));
            DefineTypeInANewAssembly(ns, n);
            Assert.NotNull(GameBinding.Cached("HafLate.NeverExists", ns + "." + n));
        }

        // The other half: a HIT must be cached, or the swap buys nothing.
        [Fact]
        public void AHitIsCached()
        {
            const string ns = "HafLate", n = "CachedOnce";
            DefineTypeInANewAssembly(ns, n);
            var a = GameBinding.Cached(ns + "." + n);
            Assert.NotNull(a);
            Assert.Same(a, GameBinding.Cached(ns + "." + n));
        }

        // A fallback chain caches the WINNER, not the primary's miss — this is what collapses BattleTurn's three-name
        // probe (two of which are meant to fail) from ~15.7 ms to one dictionary hit.
        [Fact]
        public void AFallbackChainCachesTheWinner()
        {
            const string ns = "HafLate", n = "FallbackWinner";
            DefineTypeInANewAssembly(ns, n);
            var a = GameBinding.Cached("HafLate.NoSuchPrimary", ns + "." + n);
            Assert.NotNull(a);
            Assert.Same(a, GameBinding.Cached("HafLate.NoSuchPrimary", ns + "." + n));
            Assert.Equal(ns + "." + n, a.FullName);
        }

        // ---- the accessors this pass added or corrected ----

        // ORDER inside a fallback chain is not cosmetic: the primary is the only name that costs nothing to try, and
        // every probe ahead of the real one is a full miss. bindcheck cannot catch a wrong order — it reports 132/132
        // whether the accessor resolved on its primary or limped in on a fallback — so these two are pinned by hand,
        // against what `typeprobe` says is actually in the game build (2026-08-23).
        //
        // The call site here probed the NESTED name first; typeprobe says the nested form does not exist, so that
        // probe was a guaranteed 7.85 ms miss on every call. Flat name is the real one.
        [Fact]
        public void GroundMaterialTextureData_TriesTheNameThatActuallyExistsFirst()
        {
            string line = AccessorLine("Type GroundMaterialTextureData");
            int flat = line.IndexOf("\"Amplitude.Mercury.Terrain.GroundMaterialTextureData\"", StringComparison.Ordinal);
            int nested = line.IndexOf("GroundMaterialAuthoringData+GroundMaterialTextureData", StringComparison.Ordinal);
            Assert.True(flat > 0, "the flat (real) name is not on the accessor: " + line);
            Assert.True(nested > flat, "the flat name is the one this build has — the nested form must be the FALLBACK: " + line);
        }

        // Same trap, worse: the three names the battle replay probed are ALL wrong. The real type is
        // `Amplitude.Mercury.AnimationVariableNames`; the bare name only ever resolved through the simple-name
        // GetTypes() walk over every assembly.
        [Fact]
        public void AnimationVariableNames_LeadsWithTheRealFullName()
        {
            string line = AccessorLine("Type AnimationVariableNames");
            Assert.StartsWith("\"Amplitude.Mercury.AnimationVariableNames\"",
                line.Substring(line.IndexOf("Cached(", StringComparison.Ordinal) + "Cached(".Length));
        }

        // Every name the A8 sweep moved off a call site has an accessor to move it to.
        [Theory]
        [InlineData("StaticString")]
        [InlineData("FxManager")]
        [InlineData("FxTextureAtlas")]
        [InlineData("DefaultTextureAtlas")]
        [InlineData("FxEvolverMaterialLevelBuildMatching")]
        [InlineData("FxEvolverDescriptorLevelBuildDecal")]
        [InlineData("ImageConversion")]
        public void TheA8AccessorsExist(string accessor)
        {
            Assert.NotNull(typeof(GameBinding).GetProperty(accessor, BindingFlags.NonPublic | BindingFlags.Static)
                        ?? (MemberInfo)typeof(GameBinding).GetProperty(accessor, BindingFlags.Public | BindingFlags.Static));
        }

        // FxManager keeps BOTH names it used to probe with `??` — dropping the second would silently narrow the binding.
        [Fact]
        public void FxManagerKeepsItsFallbackName()
        {
            string line = AccessorLine("Type FxManager ");
            Assert.Contains("\"Amplitude.Graphics.Fx.FxManager\"", line);   // the one this build has (typeprobe)
            Assert.Contains("\"Amplitude.Graphics.FxManager\"", line);      // the alternate the `??` chain used to carry
        }

        static string AccessorLine(string decl)
        {
            var src = System.IO.File.ReadAllText(RepoFile("Patches/GameBinding.cs"));
            int i = src.IndexOf(decl, StringComparison.Ordinal);
            Assert.True(i > 0, "accessor not found: " + decl);
            return src.Substring(i, src.IndexOf('\n', i) - i);
        }

        static string RepoFile(string rel)
        {
            var d = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && d != null; i++, d = System.IO.Path.GetDirectoryName(d))
                if (System.IO.File.Exists(System.IO.Path.Combine(d, rel))) return System.IO.Path.Combine(d, rel);
            throw new System.IO.FileNotFoundException(rel);
        }
    }
}
