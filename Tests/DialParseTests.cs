using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // The live `BepInEx/config/haf_*.txt` dials (rotor trim, turn ease, terrain hug, battle turn). Their parsing
    // was lifted out of the Poll* methods into the pure Patches/DialConfig.cs so it could be tested at all.
    //
    // Two things are under test here. First, that the parse still means exactly what it meant when it lived
    // inline — the order-independent `hoverbank` fallback, the `air` alias, the CSV name filters, the shipped
    // defaults. Second, the reason the extraction happened: every line the parser cannot understand must now
    // produce a NAMED problem instead of being silently `continue`d. A typo in a dial file used to yield a
    // working plugin that quietly ignored the setting, with nothing in the log — the "silently disarmed" class.
    public class DialParseTests
    {
        static List<string> Probs() => new List<string>();

        // ------------------------------------------------------------------ tokenizer

        [Fact]
        public void Tokenize_BlanksAndComments_AreSkippedSilently()
        {
            var p = Probs();
            var lines = DialConfig.Tokenize("# a comment\n\n   \n#another\nrate=5\n", p);
            Assert.Single(lines);
            Assert.Equal("rate", lines[0].Key);
            Assert.Empty(p);                       // deliberate blanks/comments are NOT problems
        }

        [Fact]
        public void Tokenize_CrLfLineEndings_ParseCleanly()
        {
            var p = Probs();
            var lines = DialConfig.Tokenize("rate=5\r\nbank=3\r\n", p);
            Assert.Equal(2, lines.Count);
            Assert.Equal("5", lines[0].Value);     // the \r must not survive into the value
            Assert.Equal("3", lines[1].Value);
            Assert.Empty(p);
        }

        [Fact]
        public void Tokenize_KeyAndValue_AreTrimmed_AndKeyLowercasedButKeyRawIsNot()
        {
            var lines = DialConfig.Tokenize("   Rate   =   5   ", Probs());
            Assert.Equal("rate", lines[0].Key);
            Assert.Equal("Rate", lines[0].KeyRaw);  // original case survives — bone names need it
            Assert.Equal("5", lines[0].Value);
        }

        [Fact]
        public void Tokenize_LineWithNoEquals_IsReportedWithItsLineNumber()
        {
            var p = Probs();
            var lines = DialConfig.Tokenize("rate=5\nthis is not a setting\nbank=3", p);
            Assert.Equal(2, lines.Count);           // the good lines still parse
            Assert.Single(p);
            Assert.Contains("line 2", p[0]);
            Assert.Contains("this is not a setting", p[0]);
        }

        [Fact]
        public void Tokenize_LineWithTwoEquals_IsReported_NotGuessedAt()
        {
            var p = Probs();
            var lines = DialConfig.Tokenize("rate=5=7", p);
            Assert.Empty(lines);
            Assert.Single(p);
            Assert.Contains("more than one '='", p[0]);
        }

        [Fact]
        public void Tokenize_LineNumbers_CountCommentsAndBlanks()
        {
            var p = Probs();
            DialConfig.Tokenize("# c\n\nbroken line\n", p);
            Assert.Contains("line 3", p[0]);        // 1-based, counting the comment and the blank
        }

        [Fact]
        public void Tokenize_NullAndEmpty_AreSafe()
        {
            Assert.Empty(DialConfig.Tokenize(null, Probs()));
            Assert.Empty(DialConfig.Tokenize("", Probs()));
        }

        // ------------------------------------------------------------------ turn ease

        [Fact]
        public void TurnEase_ReadsEveryKnownKey()
        {
            var d = TurnEaseDial.Parse(
                "rate=90\nbank=12\nhuman=45\nland=30\nturret=60\nhover=120\nship=15\nhoverbank=20\nshipbank=8", Probs());
            Assert.Equal(90f, d.Rate);
            Assert.Equal(12f, d.Bank);
            Assert.Equal(45f, d.Human);
            Assert.Equal(30f, d.Land);
            Assert.Equal(60f, d.Turret);
            Assert.Equal(120f, d.Hover);
            Assert.Equal(15f, d.Ship);
            Assert.Equal(20f, d.HoverBank);
            Assert.Equal(8f, d.ShipBank);
        }

        [Fact]
        public void TurnEase_AirIsALegacyAliasForHover()
        {
            var p = Probs();
            var d = TurnEaseDial.Parse("air=120", p);
            Assert.Equal(120f, d.Hover);
            Assert.Empty(p);                        // a legacy file must not be nagged at
        }

        [Fact]
        public void TurnEase_NoHoverBank_InheritsTheGlobalBank()
        {
            var d = TurnEaseDial.Parse("bank=12", Probs());
            Assert.Equal(12f, d.HoverBank);
        }

        // THE SUBTLE ONE. The fallback is resolved after the whole file is read, so a `hoverbank` line ABOVE the
        // `bank` line behaves identically to one below it. Resolving inline (the obvious refactor) would make
        // hoverbank inherit whatever `bank` happened to be at that point — i.e. 0 — and silently flatten the bank
        // of every helicopter in a file that happens to list its keys alphabetically.
        [Theory]
        [InlineData("bank=12\nhoverbank=20")]
        [InlineData("hoverbank=20\nbank=12")]
        public void TurnEase_ExplicitHoverBank_WinsRegardlessOfLineOrder(string text)
        {
            var d = TurnEaseDial.Parse(text, Probs());
            Assert.Equal(20f, d.HoverBank);
            Assert.Equal(12f, d.Bank);
        }

        [Theory]
        [InlineData("hoverbank=0\nbank=12", 0f)]    // an explicit zero is a CHOICE, not "unset" — it must not inherit
        [InlineData("bank=12", 12f)]
        public void TurnEase_ExplicitZeroHoverBank_IsNotTreatedAsUnset(string text, float expected)
        {
            Assert.Equal(expected, TurnEaseDial.Parse(text, Probs()).HoverBank);
        }

        [Fact]
        public void TurnEase_UnknownKey_IsNamedAndListsWhatIsValid()
        {
            var p = Probs();
            var d = TurnEaseDial.Parse("rate=90\nhoverbanks=20", p);   // the plausible typo: a trailing 's'
            Assert.Equal(90f, d.Rate);                                 // the good line still applies
            Assert.Single(p);
            Assert.Contains("hoverbanks", p[0]);
            Assert.Contains("line 2", p[0]);
            Assert.Contains("hoverbank", p[0]);                        // the known-key list names the right spelling
        }

        [Fact]
        public void TurnEase_NonNumericValue_IsNamedAgainstItsKey()
        {
            var p = Probs();
            var d = TurnEaseDial.Parse("rate=fast", p);
            Assert.Equal(0f, d.Rate);
            Assert.Single(p);
            Assert.Contains("rate", p[0]);
            Assert.Contains("needs a number", p[0]);
        }

        // The plugin has always parsed invariant-culture, so a European "1,5" silently vanished. Now it says so.
        [Fact]
        public void TurnEase_CommaDecimal_IsNamedWithTheFix()
        {
            var p = Probs();
            TurnEaseDial.Parse("rate=1,5", p);
            Assert.Single(p);
            Assert.Contains("use '.' for the decimal point", p[0]);
        }

        [Fact]
        public void TurnEase_EmptyFile_IsAllZeroes_AndNotAProblem()
        {
            var p = Probs();
            var d = TurnEaseDial.Parse("", p);
            Assert.Equal(0f, d.Rate);
            Assert.Equal(0f, d.HoverBank);
            Assert.Empty(p);                        // a missing dial file is how you turn the feature OFF
        }

        // ------------------------------------------------------------------ terrain hug

        [Fact]
        public void TerrainHug_UnsetKeys_KeepTheShippedDefaults()
        {
            var d = TerrainHugDial.Parse("", Probs());
            Assert.Equal(0f, d.Drop);
            Assert.Equal(0f, d.Radius);
            Assert.Equal(3f, d.Lookahead);          // NOT zero — these three defaults are load-bearing
            Assert.Equal(4f, d.Ease);
            Assert.Equal(1f, d.Cliff);
        }

        [Fact]
        public void TerrainHug_ReadsEveryNumericKey()
        {
            var d = TerrainHugDial.Parse("drop=-2\nradius=6\nlookahead=5\nease=8\ncliff=2", Probs());
            Assert.Equal(-2f, d.Drop);
            Assert.Equal(6f, d.Radius);
            Assert.Equal(5f, d.Lookahead);
            Assert.Equal(8f, d.Ease);
            Assert.Equal(2f, d.Cliff);
        }

        // only/skip are CSV NAME LISTS, read before any numeric parse. If that ordering ever regressed they would
        // fail the float check and be dropped as "not a number", killing the district filters silently.
        [Fact]
        public void TerrainHug_OnlyAndSkip_AreCsvNameLists_NotNumbers()
        {
            var p = Probs();
            var d = TerrainHugDial.Parse("only=City, Quarter ,Harbour\nskip=Exploitation,Ruin", p);
            Assert.Equal(new[] { "City", "Quarter", "Harbour" }, d.Only.ToArray());   // inner whitespace trimmed
            Assert.Equal(new[] { "Exploitation", "Ruin" }, d.Skip.ToArray());
            Assert.Empty(p);
        }

        [Fact]
        public void TerrainHug_EmptyCsvEntries_AreDropped_NotKeptAsBlanks()
        {
            var d = TerrainHugDial.Parse("only=City,,  ,Quarter", Probs());
            Assert.Equal(new[] { "City", "Quarter" }, d.Only.ToArray());
        }

        [Fact]
        public void TerrainHug_EmptyFilter_SaysItDoesNothing()
        {
            var p = Probs();
            var d = TerrainHugDial.Parse("only=", p);
            Assert.Empty(d.Only);
            Assert.Single(p);
            Assert.Contains("does nothing", p[0]);
        }

        // drop is how much LOWER to fly. A positive value flies higher over open ground than over cities —
        // a dropped minus sign. The pack validator warns about the same mistake in the registry.
        [Fact]
        public void TerrainHug_PositiveDrop_IsWarnedWithTheSuggestedFix()
        {
            var p = Probs();
            var d = TerrainHugDial.Parse("drop=2", p);
            Assert.Equal(2f, d.Drop);               // still applied — it is a warning, not a veto
            Assert.Single(p);
            Assert.Contains("should be negative", p[0]);
            Assert.Contains("-2", p[0]);
        }

        [Theory]
        [InlineData("drop=-2")]
        [InlineData("drop=0")]
        public void TerrainHug_NegativeOrZeroDrop_IsNotWarned(string text)
        {
            var p = Probs();
            TerrainHugDial.Parse(text, p);
            Assert.Empty(p);
        }

        [Fact]
        public void TerrainHug_UnknownKey_IsNamed()
        {
            var p = Probs();
            var d = TerrainHugDial.Parse("radus=6", p);       // the classic transposition
            Assert.Equal(0f, d.Radius);
            Assert.Single(p);
            Assert.Contains("radus", p[0]);
            Assert.Contains("radius", p[0]);                  // known-key list carries the right spelling
        }

        // ------------------------------------------------------------------ rotor trim

        [Fact]
        public void RotorTrim_ParsesBoneAxisAndDegrees()
        {
            var d = RotorTrimDial.Parse("MainRotor@1=-12.5", Probs());
            var t = Assert.Single(d.Trims);
            Assert.Equal("MainRotor", t.Bone);     // case preserved
            Assert.Equal(1, t.Axis);
            Assert.Equal(-12.5f, t.Deg);
        }

        [Fact]
        public void RotorTrim_NoAxisQualifier_DefaultsToX()
        {
            var p = Probs();
            var t = Assert.Single(RotorTrimDial.Parse("MainRotor=5", p).Trims);
            Assert.Equal(0, t.Axis);
            Assert.Empty(p);                        // omitting the axis is legal, not a problem
        }

        [Fact]
        public void RotorTrim_NonNumericAxis_FallsBackToXAndSaysSo()
        {
            var p = Probs();
            var t = Assert.Single(RotorTrimDial.Parse("MainRotor@x=5", p).Trims);
            Assert.Equal(0, t.Axis);                // preserved behaviour
            Assert.Single(p);
            Assert.Contains("axis 0", p[0]);
        }

        // The axis is written to the engine as a uint, so a negative silently becomes an enormous index and an
        // out-of-range one addresses nothing. Both used to be accepted in total silence.
        [Theory]
        [InlineData("MainRotor@3=5", 3)]
        [InlineData("MainRotor@-1=5", -1)]
        public void RotorTrim_AxisOutOfRange_IsNamed(string text, int axis)
        {
            var p = Probs();
            var t = Assert.Single(RotorTrimDial.Parse(text, p).Trims);
            Assert.Equal(axis, t.Axis);
            Assert.Single(p);
            Assert.Contains("out of range", p[0]);
        }

        [Fact]
        public void RotorTrim_MissingBoneName_IsDroppedAndNamed()
        {
            var p = Probs();
            var d = RotorTrimDial.Parse("@1=5", p);
            Assert.Empty(d.Trims);
            Assert.Single(p);
            Assert.Contains("no bone name", p[0]);
        }

        // ApplyRotorTrim writes into 4 BoneRotation slots and stops. A 5th line was accepted, reported in the
        // "reloaded N line(s)" log, and then silently never applied.
        [Fact]
        public void RotorTrim_MoreThanFourTrims_NamesTheOnesThatWillNotBeApplied()
        {
            var p = Probs();
            var d = RotorTrimDial.Parse("A=1\nB=2\nC=3\nD=4\nE=5\nF=6", p);
            Assert.Equal(6, d.Trims.Count);         // all parsed...
            Assert.Single(p);
            Assert.Contains("only the first 4", p[0]);
            Assert.Contains("E", p[0]);             // ...but the ignored ones are named
            Assert.Contains("F", p[0]);
        }

        [Fact]
        public void RotorTrim_ExactlyFourTrims_IsNotWarned()
        {
            var p = Probs();
            var d = RotorTrimDial.Parse("A=1\nB=2\nC=3\nD=4", p);
            Assert.Equal(4, d.Trims.Count);
            Assert.Empty(p);
        }

        [Fact]
        public void RotorTrim_NonNumericDegrees_IsNamedAndDropped()
        {
            var p = Probs();
            var d = RotorTrimDial.Parse("MainRotor@1=lots", p);
            Assert.Empty(d.Trims);
            Assert.Single(p);
            Assert.Contains("needs a number", p[0]);
        }

        // ------------------------------------------------------------------ battle turn

        [Theory]
        [InlineData("hold=1", true)]
        [InlineData("hold=0.5", true)]              // any positive value arms it
        [InlineData("hold=0", false)]
        [InlineData("hold=-1", false)]
        [InlineData("", false)]
        public void BattleTurn_HoldIsAnyPositiveValue(string text, bool expected)
        {
            Assert.Equal(expected, BattleTurnDial.Parse(text, Probs()).HoldFire);
        }

        [Fact]
        public void BattleTurn_DiagIsIndependentOfHold()
        {
            var d = BattleTurnDial.Parse("diag=1", Probs());
            Assert.True(d.Diag);
            Assert.False(d.HoldFire);
        }

        [Fact]
        public void BattleTurn_UnknownKey_IsNamed()
        {
            var p = Probs();
            BattleTurnDial.Parse("holdfire=1", p);
            Assert.Single(p);
            Assert.Contains("holdfire", p[0]);
        }

        // ------------------------------------------------------------------ across all four

        // One bad line must never cost the rest of the file. Every dial is hand-edited live, mid-session, so a
        // half-typed line is the NORMAL state of the file for a second or two.
        [Fact]
        public void EveryDial_KeepsTheGoodLines_WhenOneLineIsBroken()
        {
            const string junk = "\n???\n";
            Assert.Equal(90f, TurnEaseDial.Parse("rate=90" + junk, Probs()).Rate);
            Assert.Equal(6f, TerrainHugDial.Parse("radius=6" + junk, Probs()).Radius);
            Assert.Single(RotorTrimDial.Parse("MainRotor=5" + junk, Probs()).Trims);
            Assert.True(BattleTurnDial.Parse("hold=1" + junk, Probs()).HoldFire);
        }

        // Every problem must name a line or a value the user can actually find in their file — a message that
        // says only "parse error" is barely better than the silence it replaced.
        [Fact]
        public void EveryProblem_IsSpecificEnoughToAct_On()
        {
            var p = Probs();
            TurnEaseDial.Parse("rate=fast\nnonsense=1\nnot a setting", p);
            TerrainHugDial.Parse("drop=2", p);
            RotorTrimDial.Parse("@1=5", p);
            Assert.Equal(5, p.Count);
            Assert.All(p, msg => Assert.True(msg.Length > 20 && (msg.Contains("line ") || msg.Contains("drop")),
                                             $"unhelpful problem message: '{msg}'"));
        }

        // ------------------------------------------------------------------ the drill finding

        // FOUND IN-GAME 2026-08-20, not by any test above. The `[Hug]`/`[TurnEase]` echo lines used plain string
        // interpolation, so on a comma-decimal machine the log read `lookahead=1,5` — the exact spelling the
        // parser rejects, printed one line above the new warning telling the user to use '.'. Copying a value out
        // of the log back into the dial file silently disabled the setting. The invariant that closes it: whatever
        // the log prints must parse straight back, in any culture.
        [Theory]
        [InlineData(1.5f)]
        [InlineData(-2f)]
        [InlineData(0f)]
        [InlineData(180f)]
        [InlineData(0.25f)]
        public void EchoedNumber_ParsesBackExactly_EvenInACommaDecimalCulture(float v)
        {
            var prev = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("nl-NL");   // the locale the bug was found on
                var p = Probs();
                var d = TerrainHugDial.Parse("lookahead=" + DialConfig.Inv(v), p);
                Assert.Empty(p);                 // the echoed spelling must not trip the parser's own warning
                Assert.Equal(v, d.Lookahead);
            }
            finally { CultureInfo.CurrentCulture = prev; }
        }

        [Fact]
        public void Inv_NeverEmitsACommaDecimal()
        {
            var prev = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
                Assert.Equal("1.5", DialConfig.Inv(1.5f));
                Assert.Equal("-2", DialConfig.Inv(-2f));
            }
            finally { CultureInfo.CurrentCulture = prev; }
        }

        // A null problems list must be accepted everywhere (the ?. on every Add) — a caller that does not care
        // about diagnostics must not crash the dial poll, which runs inside the per-frame pump.
        [Fact]
        public void NullProblemsList_IsAcceptedByEveryDial()
        {
            Assert.Equal(90f, TurnEaseDial.Parse("rate=90\nbogus=1\n???", null).Rate);
            Assert.Equal(2f, TerrainHugDial.Parse("drop=2\nbogus=1\n???", null).Drop);
            Assert.Single(RotorTrimDial.Parse("A@9=1\n???", null).Trims);
            Assert.True(BattleTurnDial.Parse("hold=1\nbogus=1\n???", null).HoldFire);
        }
    }
}
