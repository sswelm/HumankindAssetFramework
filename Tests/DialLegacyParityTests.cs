using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // EXTRACTION PARITY (2026-08-20). Lifting the dial parsing out of the Poll* methods into the pure
    // Patches/DialConfig.cs was a refactor of SHIPPED behaviour, and the tests in DialParseTests.cs only pin
    // what the code does NOW — they would happily pass over a subtly different parser. So this file keeps the
    // ORIGINAL inline loops, copied verbatim from the pre-extraction Poll* methods (commit 9569abb), as an
    // oracle, and asserts the new parser produces the same VALUES over a corpus of real and malformed input.
    //
    // Diagnostics are deliberately NOT compared: emitting a named problem where the old code silently dropped
    // the line is the entire point of the change. There is exactly ONE value divergence, asserted explicitly
    // at the bottom, and it is a bug fix.
    public class DialLegacyParityTests
    {
        // A corpus that mixes valid input, the malformed input a live-edited file passes through while being
        // typed, and the specific shapes the two parsers could plausibly disagree on.
        public static IEnumerable<object[]> Corpus => new[]
        {
            "",
            "\n\n   \n",
            "# just a comment\n",
            "rate=90",
            "  rate  =  90  ",
            "RATE=90",                              // key case-insensitivity
            "rate=90\r\nbank=12\r\n",               // CRLF
            "rate=90\nbank=12\nhoverbank=20",
            "hoverbank=20\nbank=12",                // fallback order-independence
            "bank=12",                              // hoverbank inherits
            "hoverbank=0\nbank=12",                 // explicit zero
            "air=120",                              // legacy alias
            "rate=90\nrate=45",                     // last one wins
            "rate=fast",                            // non-numeric
            "rate=1,5",                             // comma decimal
            "rate=",                                // empty value
            "=90",                                  // empty key
            "rate",                                 // no '='
            "rate=5=7",                             // two '='
            "bogus=1",                              // unknown key
            "rate@2=5",                             // a '@' on a keyword dial
            "drop=-2\nradius=6\nlookahead=5\nease=8\ncliff=2",
            "only=City, Quarter ,Harbour\nskip=Exploitation,Ruin",
            "only=City,,  ,Quarter",
            "only=",
            "only=City\nonly=Quarter",              // repeated list key APPENDS (it never cleared)
            "drop=2",
            "MainRotor@1=-12.5",
            "MainRotor=5",
            "MainRotor@x=5",
            "MainRotor@3=5",
            "MainRotor@-1=5",
            "A=1\nB=2\nC=3\nD=4\nE=5\nF=6",
            "  MainRotor @1 = 5 ",                  // whitespace around the '@' qualifier
            "hold=1",
            "hold=0.5",
            "hold=-1",
            "diag=1",
            "rate=90\n???\nbank=12",                // one broken line among good ones
        }.Select(s => new object[] { s });

        // ------------------------------------------------------------------ the legacy oracles (verbatim)

        static void LegacyTurnEase(string txt, out float rate, out float bank, out float cHum, out float cLand,
                                   out float cTur, out float cHov, out float cShip, out float hoverBank, out float shipBank)
        {
            rate = 0f; bank = 0f; cHum = 0f; cLand = 0f; cTur = 0f; cHov = 0f; cShip = 0f;
            float hovBank = 0f; shipBank = 0f; bool seenHovBank = false;
            foreach (var raw in txt.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.Split('=');
                if (eq.Length != 2 || !float.TryParse(eq[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v)) continue;
                switch (eq[0].Trim().ToLowerInvariant())
                {
                    case "rate": rate = v; break;
                    case "bank": bank = v; break;
                    case "human": cHum = v; break;
                    case "land": cLand = v; break;
                    case "turret": cTur = v; break;
                    case "hover": case "air": cHov = v; break;
                    case "ship": cShip = v; break;
                    case "hoverbank": hovBank = v; seenHovBank = true; break;
                    case "shipbank": shipBank = v; break;
                }
            }
            hoverBank = seenHovBank ? hovBank : bank;
        }

        static void LegacyHug(string txt, out float drop, out float radius, out float look, out float ease,
                              out float cliff, List<string> only, List<string> skip)
        {
            drop = 0f; radius = 0f; look = 3f; ease = 4f; cliff = 1f;
            foreach (var raw in txt.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.Split('=');
                if (eq.Length != 2) continue;
                string key = eq[0].Trim().ToLowerInvariant(), val = eq[1].Trim();
                if (key == "only" || key == "skip")
                {
                    var target = key == "only" ? only : skip;
                    foreach (var s in val.Split(',')) if (s.Trim().Length > 0) target.Add(s.Trim());
                    continue;
                }
                if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                switch (key)
                {
                    case "drop": drop = v; break;
                    case "radius": radius = v; break;
                    case "lookahead": look = v; break;
                    case "ease": ease = v; break;
                    case "cliff": cliff = v; break;
                }
            }
        }

        struct LegacyTrim { public string bone; public int axis; public float deg; }

        static List<LegacyTrim> LegacyRotorTrim(string txt)
        {
            var trims = new List<LegacyTrim>();
            foreach (var raw in txt.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.Split('=');
                if (eq.Length != 2 || !float.TryParse(eq[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var deg)) continue;
                var at = eq[0].Split('@');
                trims.Add(new LegacyTrim
                {
                    bone = at[0].Trim(),
                    axis = at.Length > 1 && int.TryParse(at[1].Trim(), out var a) ? a : 0,
                    deg = deg,
                });
            }
            return trims;
        }

        static void LegacyBattleTurn(string txt, out bool holdFire, out bool diag)
        {
            float h = 0f, dg = 0f;
            foreach (var raw in txt.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.Split('=');
                if (eq.Length != 2 || !float.TryParse(eq[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v)) continue;
                switch (eq[0].Trim().ToLowerInvariant())
                {
                    case "hold": h = v; break;
                    case "diag": dg = v; break;
                }
            }
            holdFire = h > 0f; diag = dg > 0f;
        }

        // ------------------------------------------------------------------ parity

        [Theory, MemberData(nameof(Corpus))]
        public void TurnEase_MatchesTheLegacyLoop(string text)
        {
            LegacyTurnEase(text, out var rate, out var bank, out var hum, out var land,
                           out var tur, out var hov, out var ship, out var hoverBank, out var shipBank);
            var d = TurnEaseDial.Parse(text, null);
            Assert.Equal(rate, d.Rate);
            Assert.Equal(bank, d.Bank);
            Assert.Equal(hum, d.Human);
            Assert.Equal(land, d.Land);
            Assert.Equal(tur, d.Turret);
            Assert.Equal(hov, d.Hover);
            Assert.Equal(ship, d.Ship);
            Assert.Equal(hoverBank, d.HoverBank);
            Assert.Equal(shipBank, d.ShipBank);
        }

        [Theory, MemberData(nameof(Corpus))]
        public void TerrainHug_MatchesTheLegacyLoop(string text)
        {
            var only = new List<string>(); var skip = new List<string>();
            LegacyHug(text, out var drop, out var radius, out var look, out var ease, out var cliff, only, skip);
            var d = TerrainHugDial.Parse(text, null);
            Assert.Equal(drop, d.Drop);
            Assert.Equal(radius, d.Radius);
            Assert.Equal(look, d.Lookahead);
            Assert.Equal(ease, d.Ease);
            Assert.Equal(cliff, d.Cliff);
            Assert.Equal(only, d.Only);
            Assert.Equal(skip, d.Skip);
        }

        [Theory, MemberData(nameof(Corpus))]
        public void RotorTrim_MatchesTheLegacyLoop_ExceptTheEmptyBoneFix(string text)
        {
            // The one deliberate divergence — see EmptyBone_WasTheOneBehaviourChange below.
            var legacy = LegacyRotorTrim(text).Where(t => t.bone.Length > 0).ToList();
            var d = RotorTrimDial.Parse(text, null);
            Assert.Equal(legacy.Count, d.Trims.Count);
            for (int i = 0; i < legacy.Count; i++)
            {
                Assert.Equal(legacy[i].bone, d.Trims[i].Bone);
                Assert.Equal(legacy[i].axis, d.Trims[i].Axis);
                Assert.Equal(legacy[i].deg, d.Trims[i].Deg);
            }
        }

        [Theory, MemberData(nameof(Corpus))]
        public void BattleTurn_MatchesTheLegacyLoop(string text)
        {
            LegacyBattleTurn(text, out var hold, out var diag);
            var d = BattleTurnDial.Parse(text, null);
            Assert.Equal(hold, d.HoldFire);
            Assert.Equal(diag, d.Diag);
        }

        // THE ONE VALUE CHANGE, and it is a fix. A line like `@1=5` (bone name omitted, e.g. mid-edit) produced
        // a legacy trim with bone == "". ApplyRotorTrim matches a bone with
        //     name.IndexOf(t.bone, OrdinalIgnoreCase) >= 0
        // and IndexOf("") is 0 for ANY string — so an empty bone name matched the FIRST bone in the skeleton and
        // silently rotated it. The new parser drops the line and says why.
        [Fact]
        public void EmptyBone_WasTheOneBehaviourChange_AndItWasABug()
        {
            var legacy = LegacyRotorTrim("@1=5");
            Assert.Single(legacy);                       // the old parser accepted it...
            Assert.Equal("", legacy[0].bone);            // ...with an empty bone that matches bone 0 on apply

            var problems = new List<string>();
            var d = RotorTrimDial.Parse("@1=5", problems);
            Assert.Empty(d.Trims);                       // the new parser drops it...
            Assert.Contains("no bone name", Assert.Single(problems));   // ...and names it
        }
    }
}
