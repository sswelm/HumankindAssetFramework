using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // THE STRATEGIC-FOOTPRINT LATCH, and the two clones beside it (2026-08-23).
    //
    // `footprintMaskInjected` is a `static bool` that guards a once-per-session injection. Left unreset, it survived a
    // save reload while `ResetDistrictSessionState` destroyed the atlas and decal clone it guards — so the second save
    // load of a process showed NO strategic-zoom footprint at all, silently, until the game was restarted.
    //
    // It is exactly the shape SessionState's fence is documented as unable to police: a bare scalar, indistinguishable
    // from a constant, so `UnpolicedStaticCount()` counts it and nothing checks it. Enumerating every one-way `static
    // bool` in the plugin (set `= true`, never `= false`) finds 35 — and 34 are legitimately process-lived (`*Logged`,
    // `*Dumped`, `*Resolved`) or are properties backed by `ScopedState`, which `S = new ScopedState()` resets. So a
    // blanket "one-way latch" gate would be 34 false positives and was deliberately NOT built. These are targeted.
    //
    // HONEST LIMIT: neither test would have caught the original defect, because the original was an UNANNOTATED field —
    // there was nothing to check. They stop it regressing, and they stop the annotation from starting to lie.
    public class DistrictSessionLatchTests
    {
        static string Repo(string rel)
        {
            var d = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && d != null; i++, d = Path.GetDirectoryName(d))
                if (File.Exists(Path.Combine(d, rel))) return Path.Combine(d, rel);
            throw new FileNotFoundException(rel);
        }

        // `//`-comments only; that is the shape that fooled this test, and a block-comment stripper would need to know
        // about strings to be correct.
        static string StripComments(string src) =>
            string.Join("\n", src.Split('\n').Select(l => { int i = l.IndexOf("//", StringComparison.Ordinal); return i < 0 ? l : l.Substring(0, i); }));

        // Pull one method's body by brace-matching from its signature line.
        static string MethodBody(string src, string signature)
        {
            int i = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(i > 0, "method not found: " + signature);
            int open = src.IndexOf('{', i);
            Assert.True(open > 0);
            int depth = 0;
            for (int j = open; j < src.Length; j++)
            {
                if (src[j] == '{') depth++;
                else if (src[j] == '}' && --depth == 0) return src.Substring(open, j - open + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        // ---- the latch ----

        [Fact]
        public void FootprintMaskLatchIsClearedByTheDistrictSessionReset()
        {
            var body = MethodBody(StripComments(File.ReadAllText(Repo("Patches/DistrictInject.cs"))), "static void ResetDistrictSessionState");
            Assert.Contains("footprintMaskInjected = false", body);
        }

        // The mask TEXTURE must not be swept up with the per-session clones: we decode it from a PNG once and every
        // re-injection reuses it. Destroying it would leave the next session's atlas pointing at a dead texture.
        [Fact]
        public void TheMaskTextureIsProcessLivedAndNotTrackedForDestruction()
        {
            var src = File.ReadAllText(Repo("Patches/DistrictInject.Scoped.cs"));
            int decl = src.IndexOf("static UnityEngine.Texture2D reactorMaskTex", StringComparison.Ordinal);
            Assert.True(decl > 0, "reactorMaskTex declaration not found");
            Assert.Contains("ProcessLived", src.Substring(Math.Max(0, decl - 400), 400));
            Assert.DoesNotContain("TrackDistrictClone(reactorMaskTex)", src);
        }

        // ---- the leak the latch fix would otherwise have made WORSE ----

        // Before this pass the injection leaked its atlas and decal clone: neither went into districtOwnedClones, so
        // nothing freed them. That was survivable only because the latch meant it ran ONCE per process. Resetting the
        // latch turns a one-shot leak into a per-reload leak, which is why the two changes are one fix and not two.
        [Theory]
        [InlineData("ourAtlas")]
        [InlineData("hostClone")]
        public void EveryUnityObjectTheFootprintInjectionCreatesIsOwned(string local)
        {
            var body = MethodBody(StripComments(File.ReadAllText(Repo("Patches/DistrictInject.Scoped.cs"))), "internal static void InjectReactorFootprint");
            Assert.Contains("TrackDistrictClone(" + local + ")", body);
        }

        // Catches a NEW clone added to this method without ownership — the shape of the original leak, not just its
        // two instances. `olClone` is exempt: ClonePrivateOutputLayer tracks it at the point of creation.
        [Fact]
        public void NoUntrackedClonesAreCreatedByTheFootprintInjection()
        {
            var body = MethodBody(StripComments(File.ReadAllText(Repo("Patches/DistrictInject.Scoped.cs"))), "internal static void InjectReactorFootprint");
            var created = Regex.Matches(body, @"var\s+(\w+)\s*=\s*UnityEngine\.(?:Object\.Instantiate|ScriptableObject\.CreateInstance)")
                               .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(created);   // if this trips, the extraction broke, not the code
            var untracked = created.Where(n => !body.Contains("TrackDistrictClone(" + n + ")")).ToList();
            Assert.True(untracked.Count == 0,
                "clone(s) created but never handed to districtOwnedClones — they leak once per save reload: " + string.Join(", ", untracked));
        }

        // ---- the annotation must not start lying ----

        // A [SessionScoped(Manual = "…")] field promises it is reset by hand somewhere. Assert that something outside
        // its own declaration actually assigns or clears it.
        [Fact]
        public void EveryManuallyResetSessionFieldIsResetSomewhere()
        {
            var files = Directory.GetFiles(Path.GetDirectoryName(Repo("Patches/DistrictInject.cs")), "*.cs");
            // STRIP COMMENTS FIRST. Drilled 2026-08-23: without this, a comment that merely *quotes* the assignment
            // (`// "_subPawnScan = null" added today …`) satisfies the search, and deleting the real code leaves the
            // test green. A prose mention is not a reset.
            string all = string.Join("\n", files.Select(f => StripComments(File.ReadAllText(f))));
            var fields = new List<string>();
            foreach (var f in files)
            {
                var text = File.ReadAllText(f);
                // Anchor on the DECLARATION after the attribute, so the field name is captured and not some word out of
                // the annotation's own prose. The type is matched as `[^\n]+?` rather than a character class: a first
                // attempt used [\w<>,.\[\]]+ and silently stopped matching any generic carrying a SPACE — which dropped
                // `List<KeyValuePair<UnityEngine.Object, ModelEntry>> _subPawnScan` out of the set and made this test
                // unable to fail for it. Hence the canaries below.
                foreach (Match m in Regex.Matches(text,
                    @"\[SessionScoped\([^\]]*Manual\s*=[^\]]*\]\s*(?:internal\s+|private\s+|public\s+|protected\s+)?static\s+(?:readonly\s+)?[^\n]+?\b(\w+)\s*(?:=|;)"))
                {
                    var name = m.Groups[1].Value;
                    if (name.Length > 1 && !fields.Contains(name)) fields.Add(name);
                }
            }
            Assert.True(fields.Count >= 10, "extraction found only " + fields.Count + " Manual-reset fields — the regex broke");
            // CANARIES: two shapes the extraction has already failed on once. A guard that silently stops seeing a field
            // does not report less coverage, it reports the same green line over a smaller set — so name the awkward
            // cases explicitly. `_subPawnScan` is a generic whose type argument list contains a space; `districtOwnedClones`
            // is a readonly collection.
            foreach (var canary in new[] { "_subPawnScan", "districtOwnedClones" })
                Assert.True(fields.Contains(canary),
                    "the [SessionScoped(Manual=…)] extraction no longer sees `" + canary + "` — the regex narrowed. Found: " + string.Join(", ", fields));

            var never = fields.Where(n =>
                !Regex.IsMatch(all, @"\b" + Regex.Escape(n) + @"\s*=\s*(false|null|new\b)") &&
                !Regex.IsMatch(all, @"\b" + Regex.Escape(n) + @"\.(Clear|Reset)\(")).ToList();

            Assert.True(never.Count == 0,
                "[SessionScoped(Manual=…)] promises a hand-reset, but nothing assigns or clears: " + string.Join(", ", never));
        }
    }
}
