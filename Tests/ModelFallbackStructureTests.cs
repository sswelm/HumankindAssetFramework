using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // WHEN THE BRACES OF A PACK CANNOT BE TRUSTED (2026-11-24).
    //
    // ParseModels' regex fallback reads one object per model, found by counting braces outside strings (2026-08-23).
    // That is exact while the array's structure survived - and the fallback runs on files a hand has damaged. A closing
    // quote missing in one model turns the rest of the array inside out for a walk that respects strings; a closing
    // brace missing keeps it one level deep. Either way every model AFTER the damaged one was swallowed into it: on
    // the reference pack, each of its 51 models damaged in turn, half the models came back - one lost quote in a
    // hand-edited pack.json took every later unit out of the game.
    //
    // UniversalInject.RegistryEntryTexts counts: the walk must hand back at least as many objects carrying a "resourceName" as
    // the array's text holds objects with one, or each model is read from the object AROUND its key. The district
    // fallback shares it (Tests/DistrictRegistryParseTests.cs holds that side).
    //
    // Two kinds of test here, and the second matters as much as the first: damages after which every model must come
    // back, and files the recovery must leave ALONE - a sound structure with something odd in it. The first version of
    // this reader failed the second kind (its review, same day): it dropped every model after a stray `]`.
    public class ModelFallbackStructureTests
    {
        public ModelFallbackStructureTests()
        {
            if (Plugin.Log == null) Plugin.Log = new ManualLogSource("test");
        }

        // Shaped like a pack the Factory writes: the wrapper keys, `overrides` BEFORE `models` (an override carries a
        // pawnDescription, which a reader that looked outside the array would hand to a model), and after the array a
        // tuning table whose rows carry a "scale" of their own.
        const string Head = @"{ ""schemaVersion"": 1, ""modId"": ""mymod"", ""overrides"": [ { ""modId"": ""enc"", ""pawnDescription"": ""OVERRIDE_PAWN"" } ], ";
        const string Models = @"""models"": [ ";
        const string Tail = @" ], ""unitScales"": [ { ""match"": ""PA"", ""scale"": 9, ""era"": 0 } ], ""waterLevel"": 0.16 }";

        const string A = @"{ ""resourceName"": ""A"", ""pawnDescription"": ""PA"", ""position"": { ""x"": 1, ""y"": 2, ""z"": 3 }, ""scale"": 2, ""soundFile"": ""a.wav"", ""hideMeshes"": ""ha"", ""skel"": [1,2,3,4], ""atlas"": [5,6,7,8] }";
        // no position, scale, hideMeshes or atlas: each must stay at its default, whatever its neighbours wrote
        const string B = @"{ ""resourceName"": ""B"", ""pawnDescription"": ""PB"", ""skel"": [11,12,13,14] }";
        const string C = @"{ ""resourceName"": ""C"", ""pawnDescription"": ""PC"", ""scale"": 3, ""hideMeshes"": ""hc"", ""skel"": [21,22,23,24], ""atlas"": [25,26,27,28] }";
        const string Nameless = @"{ ""pawnDescription"": ""PN"", ""textureFile"": ""n.png"", ""atlas"": [95,96,97,98] }";
        const string Parked = @"{ ""resourceName"": ""PARKED"", ""pawnDescription"": ""PP"", ""scale"": 5 }";

        static string WithoutItsLast(string entry, string what)
        {
            int at = entry.LastIndexOf(what, System.StringComparison.Ordinal);
            Assert.True(at >= 0, what);
            return entry.Remove(at, what.Length);
        }

        static string Swap(string entry, string what, string with)
        {
            Assert.Contains(what, entry);
            return entry.Replace(what, with);
        }

        static void DoesNotParse(string doc) => Assert.ThrowsAny<System.Exception>(() => Newtonsoft.Json.Linq.JObject.Parse(doc));

        // Which way a document takes: null = by its braces, otherwise what the count found.
        static string Damage(string doc)
        {
            UniversalInject.RegistryEntryTexts(doc, "models", "resourceName", out string damage, out _, out _);
            return damage;
        }

        // What the plugin says while it reads `doc`.
        static List<string> SaidWhile(System.Action read)
        {
            var said = new List<string>();
            System.EventHandler<LogEventArgs> listen = (s, e) => { lock (said) said.Add(e.Data?.ToString() ?? ""); };
            Plugin.Log.LogEvent += listen;
            try { read(); }
            finally { Plugin.Log.LogEvent -= listen; }
            lock (said) return said.ToList();
        }

        // ---- damages after which every model must come back, each with its own values ----

        // `byBraces`: the row is read by the brace walk (the structure the walk needs survived); the others need the
        // recovery around the name. The test holds which, so a row cannot quietly change sides.
        public static IEnumerable<object[]> DamagedStructures()
        {
            yield return new object[] { "a comma missing between two models", true, Head + Models + A + " " + B + ", " + C + Tail };
            yield return new object[] { "a closing brace missing on the last model", true, Head + Models + A + ", " + B + ", " + WithoutItsLast(C, " }") + Tail };
            yield return new object[] { "a closing quote missing in the first model", false, Head + Models + Swap(A, @"""a.wav""", @"""a.wav") + ", " + B + ", " + C + Tail };
            yield return new object[] { "a closing brace missing on the first model", false, Head + Models + WithoutItsLast(A, " }") + ", " + B + ", " + C + Tail };
            yield return new object[] { "a closing brace missing on the middle model", false, Head + Models + A + ", " + WithoutItsLast(B, " }") + ", " + C + Tail };
            yield return new object[] { "the opening brace missing on the middle model", false, Head + Models + A + ", " + B.Substring(2) + ", " + C + Tail };
            yield return new object[] { "the opening brace missing on the first model", false, Head + Models + A.Substring(2) + ", " + B + ", " + C + Tail };
            yield return new object[] { "the opening brace missing on the last model", false, Head + Models + A + ", " + B + ", " + C.Substring(2) + Tail };
            yield return new object[] { "a stray closing brace before a nested object", false, Head + Models + Swap(A, @"""PA"",", @"""PA"" },") + ", " + B + ", " + C + Tail };
            yield return new object[] { "the ] of skel lost before the name, and the closing brace", false,
                Head + Models + @"{ ""skel"": [1,2,3,4, ""resourceName"": ""A"", ""pawnDescription"": ""PA"", ""position"": { ""x"": 1, ""y"": 2, ""z"": 3 }, ""scale"": 2, ""hideMeshes"": ""ha"", ""skel"": [1,2,3,4], ""atlas"": [5,6,7,8], " + B + ", " + C + Tail };
            yield return new object[] { "the array lost its colon", false, Head + @"""models"" [ " + A + ", " + B + ", " + C + Tail };
            yield return new object[] { "a quote and a brace missing, in two models", false, Head + Models + Swap(A, @"""a.wav""", @"""a.wav") + ", " + WithoutItsLast(B, " }") + ", " + C + Tail };
            yield return new object[] { "a stray closing brace in the middle of the first model", false, Head + Models + Swap(A, @"""a.wav"",", @"""a.wav"" },") + ", " + B + ", " + C + Tail };
            yield return new object[] { "a stray ] between two models", false, Head + Models + A + " ], " + B + ", " + C + Tail };
            yield return new object[] { "a stray opening brace in the middle of the first model", false, Head + Models + Swap(A, @"""a.wav"",", @"""a.wav"", {") + ", " + B + ", " + C + Tail };
            yield return new object[] { "a stray opening brace right behind a nested object", false, Head + Models + Swap(A, @"""z"": 3 },", @"""z"": 3 }, {") + ", " + B + ", " + C + Tail };
            yield return new object[] { "a stray opening brace in the middle of the last model", true, Head + Models + A + ", " + B + ", " + Swap(C, @"""PC"",", @"""PC"", {") + Tail };
        }

        [Theory]
        [MemberData(nameof(DamagedStructures))]
        public void ADamagedStructure_StillYieldsEveryModelWithItsOwnValues(string damage, bool byBraces, string doc)
        {
            DoesNotParse(doc);
            Assert.True(byBraces == (Damage(doc) == null), damage + ": " + (Damage(doc) ?? "read by its braces"));
            var got = UniversalInject.ParseModels(doc);
            Assert.True(new[] { "A", "B", "C" }.SequenceEqual(got.Select(m => m.resourceName)), damage + ": " + string.Join(",", got.Select(m => m.resourceName)));
            Assert.Equal(new[] { "PA", "PB", "PC" }, got.Select(m => m.pawnDescription));
            Assert.Equal(new[] { 2f, 1f, 3f }, got.Select(m => m.scale));          // B wrote none: not A's, not the tuning table's 9
            Assert.Equal(new[] { "ha", "", "hc" }, got.Select(m => m.hideMeshes));
            Assert.Equal(new[] { 1, 11, 21 }, got.Select(m => m.sa));
            Assert.Equal(new[] { 4, 14, 24 }, got.Select(m => m.sd));
            Assert.Equal(new[] { 5, 0, 25 }, got.Select(m => m.ta));
            Assert.Equal(new[] { 1f, 0f, 0f }, got.Select(m => m.position.x));
            Assert.Equal(new[] { 3f, 0f, 0f }, got.Select(m => m.position.z));
        }

        // THE LAST MODEL AND THE TABLE AFTER IT. The walk cannot close a last model that lost its brace, and ran with it
        // to the end of the FILE: a key the model did not write was read from the arrays after it.
        [Fact]
        public void TheLastModelWithoutItsBrace_DoesNotReadTheTuningTable()
        {
            string doc = Head + Models + A + ", " + WithoutItsLast(B, " }") + Tail;
            DoesNotParse(doc);
            Assert.Null(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B" }, got.Select(m => m.resourceName));
            Assert.Equal(1f, got[1].scale);     // 9, the first unitScales rule, until 2026-11-24
            Assert.Equal(14, got[1].sd);
        }

        // ...at the FIRST `]` that closes nothing: the array's own. A second one further on (the table after it lost its
        // `[`) is not where the model ends.
        [Fact]
        public void TheLastModelWithoutItsBrace_EndsAtTheFirstBracketThatClosesNothing()
        {
            string doc = Head + Models + A + ", " + WithoutItsLast(B, " }") + @" ], ""unitScales"": { ""match"": ""PA"", ""scale"": 9 } ], ""waterLevel"": 0.16 }";
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B" }, got.Select(m => m.resourceName));
            Assert.Equal(1f, got[1].scale);
        }

        // ...and the `]` is the one the walk found: a `]` inside a string of the unclosed model is not the array's.
        [Theory]
        [InlineData("its closing brace lost", " ] }")]
        [InlineData("the file cut short", "")]
        public void TheLastModelWithoutItsBrace_IsNotCutAtABracketInItsString(string what, string end)
        {
            string doc = Head + Models + A + @", { ""resourceName"": ""B"", ""hideMeshes"": ""x]y"", ""pawnDescription"": ""PB"", ""skel"": [11,12,13,14]" + end;
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.True(new[] { "A", "B" }.SequenceEqual(got.Select(m => m.resourceName)), what);
            Assert.Equal("PB", got[1].pawnDescription);
            Assert.Equal(11, got[1].sa);
            Assert.Equal("x]y", got[1].hideMeshes);
        }

        // A file cut short in the second model, with a `]` in a string of the first: nothing is cut before its start.
        [Fact]
        public void ATruncatedFileWithABracketInAnEarlierString_IsReadToItsEnd()
        {
            string doc = Head + Models + Swap(A, @"""ha""", @"""x]y""") + @", { ""resourceName"": ""C"", ""pawnDescription"": ""PC"", ""scale"": 3, ""hideMeshes"": ""hc"", ""skel"": [21,22,23,24], ""atl";
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "C" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { 2f, 3f }, got.Select(m => m.scale));
            Assert.Equal(new[] { 1, 21 }, got.Select(m => m.sa));
        }

        // A HAND-WRITTEN KEY ORDER. The Factory writes the name first; a hand may not. The model is the object around
        // its name, so the keys before the name are its own - not the model's above, which is where a reader that
        // went from name to name put them.
        [Fact]
        public void KeysBeforeTheName_StayWithTheirModel()
        {
            const string nameLast = @"{ ""position"": { ""x"": 7, ""y"": 8, ""z"": 9 }, ""pawnDescription"": ""PB"", ""skel"": [11,12,13,14], ""resourceName"": ""B"" }";
            string doc = Head + Models + WithoutItsLast(A, " }") + ", " + nameLast + ", " + C + Tail;
            DoesNotParse(doc);
            Assert.NotNull(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B", "C" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PA", "PB", "PC" }, got.Select(m => m.pawnDescription));
            Assert.Equal(new[] { 1f, 7f, 0f }, got.Select(m => m.position.x));
            Assert.Equal(new[] { 1, 11, 21 }, got.Select(m => m.sa));
        }

        // ...also when an array of objects stands before the name, one of them with a name key of its own: the count of
        // open arrays starts again at the model's brace.
        [Fact]
        public void KeysBeforeTheName_WithANamedObjectInAnArrayBeforeIt_StayWithTheirModel()
        {
            const string nameLast = @"{ ""parts"": [ { ""resourceName"": ""part"" } ], ""pawnDescription"": ""PB"", ""skel"": [11,12,13,14], ""resourceName"": ""B"" }";
            string doc = Head + Models + WithoutItsLast(A, " }") + ", " + nameLast + ", " + C + Tail;
            DoesNotParse(doc);
            // held on the texts: the field reader takes the FIRST resourceName in a model's text, here the part's
            var texts = UniversalInject.RegistryEntryTexts(doc, "models", "resourceName", out string damage, out _, out _);
            Assert.NotNull(damage);
            Assert.Equal(new[] { WithoutItsLast(A, " }"), nameLast, C }, texts);
        }

        // ...also the FIRST model's, whose brace stands directly at the array's opening.
        [Fact]
        public void KeysBeforeTheName_OfTheFirstModel_StayWithIt()
        {
            string doc = @"{ ""models"": [{ ""pawnDescription"": ""PA"", ""scale"": 2, ""resourceName"": ""A"", " + B + ", " + C + " ] }";
            DoesNotParse(doc);
            Assert.NotNull(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B", "C" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PA", "PB", "PC" }, got.Select(m => m.pawnDescription));
            Assert.Equal(new[] { 2f, 1f, 3f }, got.Select(m => m.scale));
        }

        // AN ARRAY OF OBJECTS INSIDE A MODEL is the model's own: its objects are neither neighbours nor models, and the
        // keys after it are still read.
        [Fact]
        public void AModelsOwnArrayOfObjects_DoesNotEndIt()
        {
            const string withParts = @"{ ""resourceName"": ""B"", ""pawnDescription"": ""PB"", ""parts"": [ { ""k"": 1 }, { ""name"": ""part"" } ], ""scale"": 4 }";
            string doc = Head + Models + WithoutItsLast(A, " }") + ", " + withParts + ", " + C + Tail;
            DoesNotParse(doc);
            Assert.NotNull(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B", "C" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { 2f, 4f, 3f }, got.Select(m => m.scale));
        }

        // NO OTHER OBJECT'S VALUES. Two files look alike to a reader that cannot trust brackets: a model with an array
        // of objects, and a model whose skel lost its `]` followed by the NEXT model. Reading on is harmless in the
        // first and hands the model its neighbour's skel and scale in the second (the second review measured exactly
        // that). What tells them apart is the model's own closing brace: it stands between the `[` and the next
        // model, and no element of an array is preceded by a brace that closes nothing opened in the array. So the
        // models behind the lost `]` are models (the third review; until then they were lost with it).
        [Fact]
        public void AModelWhoseSkelLostItsBracket_InADamagedStructure_TakesNothingFromTheModelsAfterIt_AndCostsNone()
        {
            const string d = @"{ ""resourceName"": ""D"", ""pawnDescription"": ""PD"", ""scale"": 4, ""skel"": [31,32,33,34], ""atlas"": [35,36,37,38] }";
            string doc = Head + Models + WithoutItsLast(A, " }") + ", " + Swap(B, "[11,12,13,14]", "[11,12,13,14") + ", " + C + ", " + d + Tail;
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B", "C", "D" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PA", "PB", "PC", "PD" }, got.Select(m => m.pawnDescription));
            Assert.Equal(new[] { 2f, 1f, 3f, 4f }, got.Select(m => m.scale));      // B: not C's 3
            Assert.Equal(new[] { 1, 0, 21, 31 }, got.Select(m => m.sa));            // B: not C's 21 (its own lost its bracket)
            Assert.Equal(new[] { 5, 0, 25, 35 }, got.Select(m => m.ta));
        }

        [Fact]
        public void AnObjectWithANameInAModelsOwnArray_EndsTheModelThere()
        {
            const string withParts = @"{ ""resourceName"": ""B"", ""pawnDescription"": ""PB"", ""parts"": [ { ""k"": 1 }, { ""resourceName"": ""part"", ""scale"": 7 } ], ""scale"": 4 }";
            string doc = Head + Models + WithoutItsLast(A, " }") + ", " + withParts + ", " + C + Tail;
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B", "C" }, got.Select(m => m.resourceName));   // "part" is no model
            Assert.Equal(new[] { 2f, 1f, 3f }, got.Select(m => m.scale));              // B: neither the part's 7 nor its own 4, behind the part
        }

        // A lost quote AND a file cut short: nothing closes anything any more, and nothing is read past the end.
        [Fact]
        public void ALostQuoteInATruncatedFile_StillYieldsBothModels()
        {
            string doc = Head + Models + Swap(A, @"""a.wav""", @"""a.wav") + @", { ""resourceName"": ""B"", ""pawnDescription"": ""PB"", ""skel"": [11,12";
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PA", "PB" }, got.Select(m => m.pawnDescription));
            Assert.Equal(new[] { 2f, 1f }, got.Select(m => m.scale));
        }

        // ---- files the recovery must leave alone ----

        // A STRAY OR UNPAIRED BRACKET INSIDE A MODEL. The braces are sound, so the walk reads every model. The first
        // version of this reader took such a `]` for the array's own and dropped every model after it: on the
        // reference pack, the `[` of one model's skel lost, 1,225 of 2,450 other models read right against 2,450
        // before (its review, 2026-11-24).
        [Theory]
        [InlineData("the [ of skel lost", @"""skel"": [1,2,3,4]", @"""skel"": 1,2,3,4]", 0)]
        [InlineData("a doubled ]", @"""skel"": [1,2,3,4]", @"""skel"": [1,2,3,4]]", 1)]
        [InlineData("a ] after a number", @"""scale"": 2,", @"""scale"": 2],", 1)]
        [InlineData("the ] of skel lost", @"""skel"": [1,2,3,4]", @"""skel"": [1,2,3,4", 0)]
        public void AnUnpairedBracketInAModel_CostsNoOtherModel(string what, string from, string to, int firstSkel)
        {
            string doc = Head + Models + Swap(A, from, to) + ", " + B + ", " + C + Tail;
            DoesNotParse(doc);
            Assert.True(Damage(doc) == null, what);
            var got = UniversalInject.ParseModels(doc);
            Assert.True(new[] { "A", "B", "C" }.SequenceEqual(got.Select(m => m.resourceName)), what + ": " + string.Join(",", got.Select(m => m.resourceName)));
            Assert.Equal(new[] { 2f, 1f, 3f }, got.Select(m => m.scale));
            Assert.Equal(new[] { firstSkel, 11, 21 }, got.Select(m => m.sa));
            Assert.Equal(new[] { 5, 0, 25 }, got.Select(m => m.ta));
        }

        // ...and in the LAST model, where the unpaired bracket is the first that closes nothing: the model is closed by
        // its own brace, and what stands behind the bracket is still its own.
        [Fact]
        public void AnUnpairedBracketInTheLastModel_CostsItNothingBehindTheBracket()
        {
            string doc = Head + Models + A + ", " + B + ", " + Swap(C, @"""skel"": [21,22,23,24]", @"""skel"": 21,22,23,24]") + Tail;
            DoesNotParse(doc);
            Assert.Null(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B", "C" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { 5, 0, 25 }, got.Select(m => m.ta));
        }

        // BRACKETS INSIDE STRINGS do not move the array's end: one unpaired `[` in a model's string and one unpaired
        // `]` in a string of the array after it, and the count without regard to strings runs to that second array.
        [Fact]
        public void UnpairedBracketsInStrings_DoNotBringTheNextArrayIn()
        {
            string doc = @"{ ""modId"": ""m"",, ""models"": [ " + Swap(A, @"""ha""", @"""Gun[0""") + ", " + B + @" ], ""models_off"": [ { ""textureFile"": ""x]y"", ""resourceName"": ""P1"" }, " + Parked + @", { ""resourceName"": ""P3"" } ] }";
            DoesNotParse(doc);
            Assert.Null(Damage(doc));
            Assert.Equal(new[] { "A", "B" }, UniversalInject.ParseModels(doc).Select(m => m.resourceName));
        }

        // ...also when the walk's end has an open `[` before it (a model's skel lost its `]`), so that the names
        // behind it decide: those of the parked array all lie INSIDE an array that opened after the end, and do not
        // speak against it.
        [Fact]
        public void AnUnclosedArrayInAModel_DoesNotBringTheNextArrayIn()
        {
            string doc = @"{ ""modId"": ""m"",, ""models"": [ " + Swap(A, "[1,2,3,4]", "[1,2,3,4") + ", " + B + @" ], ""models_off"": [ { ""textureFile"": ""x]y"", ""resourceName"": ""P1"" }, " + Parked + @", { ""resourceName"": ""P3"" } ] }";
            DoesNotParse(doc);
            Assert.Null(Damage(doc));
            Assert.Equal(new[] { "A", "B" }, UniversalInject.ParseModels(doc).Select(m => m.resourceName));
        }

        // A key of the DOCUMENT with the name key's name, behind a damaged models array, is no model.
        [Fact]
        public void ANameKeyOfTheDocumentItself_BehindADamagedArray_IsNoModel()
        {
            string doc = @"{ ""models"": [ " + WithoutItsLast(A, " }") + ", " + B + @" ], ""resourceName"": ""TOP"", ""pawnDescription"": ""PT"" }";
            DoesNotParse(doc);
            Assert.NotNull(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PA", "PB" }, got.Select(m => m.pawnDescription));
        }

        // ...and so is one behind a SOUND array, whatever stands in the models' strings; and the objects of a parked
        // array that lost its `[` are not models. The array's end is the walk's: its `]` closes nothing that opened
        // outside a model, and no object stands right behind it. (Before the third review a name key behind that `]`
        // was enough to distrust it, and the count without regard to strings then ran past it: an unpaired `[` in a
        // model's string, "Gun[0", made a model of the document's own key, and a parked object came to life.)
        [Theory]
        [InlineData("a key of the document", @" ], ""resourceName"": ""TOP"", ""unitScales"": [ { ""match"": ""PA"", ""scale"": 9 } ] }")]
        [InlineData("a parked array that lost its [", @" ], ""models_off"": { ""resourceName"": ""PARKED1"", ""scale"": 5 }, { ""resourceName"": ""PARKED2"" } ], ""waterLevel"": 0.16 }")]
        public void ANameBehindASoundModelsArray_IsNoModel(string what, string tail)
        {
            string doc = @"{ ""modId"": ""m"",, " + Models + Swap(A, @"""ha""", @"""Gun[0""") + ", " + B + ", " + Nameless + tail;
            DoesNotParse(doc);
            Assert.True(Damage(doc) == null, what + ": " + Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.True(new[] { "A", "B", "" }.SequenceEqual(got.Select(m => m.resourceName)), what + ": " + string.Join(",", got.Select(m => m.resourceName)));
            Assert.Equal(new[] { "PA", "PB", "PN" }, got.Select(m => m.pawnDescription));
        }

        // TWO DAMAGES, held for what stays right: a model with an unpaired `]`, then a model without its opening brace.
        // The second is lost (the first `]` that closes nothing ends the array for the count); the first is closed by
        // its own brace and keeps what stands behind its bracket.
        [Fact]
        public void AnUnpairedBracketThenAModelWithoutItsOpeningBrace_KeepsTheFirstWhole()
        {
            string doc = @"{ ""models"": [ " + Swap(A, @"""skel"": [1,2,3,4]", @"""skel"": 1,2,3,4]") + ", " + B.Substring(2) + " ] }";
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal("A", got[0].resourceName);
            Assert.Equal(5, got[0].ta);
        }

        // A SECOND NAME KEY THAT IS NO SECOND MODEL. The count sorts name keys into the objects they stand in; each of
        // these stands in the model's own object, or in an object that belongs to it. Counted as models, they sent a
        // sound file through the recovery, which ignores strings: here it would end A at the brace in its string (scale
        // back to 1) and lose the model without a name.
        [Theory]
        [InlineData("a nested object with the key", @"""extra"": { ""resourceName"": ""x"" }")]
        [InlineData("an array of objects with the key", @"""parts"": [ { ""resourceName"": ""p1"" }, { ""k"": 1, ""resourceName"": ""p2"" } ]")]
        [InlineData("a comment that quotes the old key", @"/* was ""resourceName"": ""OLD"" */ ""k"": 1")]
        [InlineData("the key written twice", @"""resourceName"": ""A2""")]
        [InlineData("the key's text as a value", @"""note"": ""resourceName""")]
        public void ASecondNameKeyInOneModel_DoesNotSwitchTheReader(string what, string extra)
        {
            string doc = @"{ ""modId"": ""m"",, ""models"": [ { ""resourceName"": ""A"", " + extra + @", ""hideMeshes"": ""a } b"", ""pawnDescription"": ""PA"", ""scale"": 2 }, { ""pawnDescription"": ""nameless"" } ] }";
            DoesNotParse(doc);
            Assert.True(Damage(doc) == null, what + ": " + Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.True(new[] { "A", "" }.SequenceEqual(got.Select(m => m.resourceName)), what + ": " + string.Join(",", got.Select(m => m.resourceName)));
            Assert.Equal(new[] { "PA", "nameless" }, got.Select(m => m.pawnDescription));
            Assert.Equal(new[] { 2f, 1f }, got.Select(m => m.scale));
            Assert.Equal("a } b", got[0].hideMeshes);
        }

        // ARRAYS BESIDE THE MODELS ARRAY are not models, whatever the damage inside it does to a count of brackets.
        [Theory]
        [InlineData("a closing brace missing", @"""atlas"": [5,6,7,8] }", @"""atlas"": [5,6,7,8]")]
        [InlineData("an unpaired [ in a string, and a doubled comma", @"""hideMeshes"": ""ha"",", @"""hideMeshes"": ""Gun[0"",,")]
        [InlineData("the ] of skel lost", @"""skel"": [1,2,3,4]", @"""skel"": [1,2,3,4")]
        public void ArraysBesideADamagedModelsArray_AreNotModels(string what, string from, string to)
        {
            string doc = @"{ ""before"": [ " + Parked + @" ], ""models"": [ " + Swap(A, from, to) + ", " + B + @" ], ""models_off"": [ " + Parked + " ] }";
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.True(new[] { "A", "B" }.SequenceEqual(got.Select(m => m.resourceName)), what + ": " + string.Join(",", got.Select(m => m.resourceName)));
            Assert.Equal(new[] { 2f, 1f }, got.Select(m => m.scale));
            Assert.Equal(new[] { "PA", "PB" }, got.Select(m => m.pawnDescription));
        }

        // NOT MORE THAN THE DOCUMENT SAYS. A pack whose models array is renamed, absent or not an array loads no models
        // when it parses; it loads none when it is damaged as well. Reading models wherever a "resourceName" stands
        // would bring a parked array to life on the day a comma went missing.
        [Theory]
        [InlineData("parses, the array renamed", @"{ ""models_off"": [ { ""resourceName"": ""A"", ""pawnDescription"": ""PA"" } ] }", true)]
        [InlineData("parses, the key in another case", @"{ ""Models"": [ { ""resourceName"": ""A"", ""pawnDescription"": ""PA"" } ] }", true)]
        [InlineData("parses, models is an object", @"{ ""models"": { ""resourceName"": ""A"", ""pawnDescription"": ""PA"" } }", true)]
        [InlineData("damaged, the array renamed", @"{ ""modId"": ""m"",, ""models_off"": [ { ""resourceName"": ""A"", ""pawnDescription"": ""PA"" ] }", false)]
        [InlineData("damaged, the key in another case", @"{ ""Models"": [ { ""resourceName"": ""A"", ""pawnDescription"": ""PA"" ] }", false)]
        [InlineData("damaged, a bare array", @"[ { ""resourceName"": ""A"", ""pawnDescription"": ""PA"" ]", false)]
        [InlineData("damaged, the word as a value", @"{ ""note"": ""models"", ""parked"": [ { ""resourceName"": ""A"", ""pawnDescription"": ""PA"" ] }", false)]
        [InlineData("damaged, models is an object (or lost its bracket)", @"{ ""models"": { ""resourceName"": ""A"", ""pawnDescription"": ""PA"" }, { ""resourceName"": ""B"" } ] }", false)]
        [InlineData("damaged, models is null beside a parked array", @"{ ""models"": null,, ""parked"": [ { ""resourceName"": ""A"", ""pawnDescription"": ""PA"" } ] }", false)]
        [InlineData("damaged, models is an empty object beside a parked array", @"{ ""models"": {},, ""parked"": [ { ""resourceName"": ""A"", ""pawnDescription"": ""PA"" } ] }", false)]
        public void ADocumentWithoutAModelsArray_LoadsNoModels(string what, string doc, bool parses)
        {
            if (parses) Newtonsoft.Json.Linq.JObject.Parse(doc);
            else DoesNotParse(doc);
            int count = -1;
            var said = SaidWhile(() => count = UniversalInject.ParseModels(doc).Count);
            Assert.True(count == 0, what);
            // a damaged one says why; one that parses is read as it always was, and this reader adds nothing to it
            if (parses) Assert.DoesNotContain(said, line => line.Contains("in a readable \"models\" array"));
            else Assert.Contains(said, line => line.Contains("in a readable \"models\" array"));
        }

        // ---- what the recovery says, and its edges ----

        [Fact]
        public void TheRecovery_SaysWhatTheCountFound()
        {
            string doc = Head + Models + WithoutItsLast(A, " }") + ", " + B + ", " + C + Tail;
            var said = SaidWhile(() => UniversalInject.ParseModels(doc));
            Assert.Contains(said, line => line.Contains("the structure of the models array is damaged too (3 object(s) with a \"resourceName\" in the text, 1 readable by their braces)"));
            // ...and a file read by its braces does not say it
            said = SaidWhile(() => UniversalInject.ParseModels(Head + Models + A + " " + B + ", " + C + Tail));
            Assert.DoesNotContain(said, line => line.Contains("damaged too"));
        }

        // Two name keys in one object are one model, also when the structure around it is damaged. (The JSON parser
        // would keep the LAST of the two; the fallback reads the first, as it does with the braces intact.)
        [Fact]
        public void ANameWrittenTwiceInOneModel_IsOneModel()
        {
            const string twice = @"{ ""resourceName"": ""B"", ""resourceName"": ""B2"", ""pawnDescription"": ""PB"", ""skel"": [11,12,13,14] }";
            string doc = Head + Models + WithoutItsLast(A, " }") + ", " + twice + ", " + C + Tail;
            Assert.NotNull(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B", "C" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PA", "PB", "PC" }, got.Select(m => m.pawnDescription));
        }

        // A KNOWN LIMIT, HELD SO IT CANNOT WIDEN. In a structure this damaged a model WITHOUT a resourceName is not
        // found: nothing in the text says the object is a model (the docs require the key). What must not happen is that
        // its keys are read as the model's above, which closed before it. With the braces intact the same entry is
        // kept, as the object path keeps it (RegexFallbackTests.AMissingResourceName_DefaultsTheSameAsTheObjectPath).
        [Fact]
        public void AModelWithoutAName_InADamagedStructure_IsLeftOutAndGivesNothingToItsNeighbour()
        {
            string doc = Head + Models + WithoutItsLast(A, " }") + ", " + B + ", " + Nameless + ", " + C + Tail;
            DoesNotParse(doc);
            Assert.NotNull(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B", "C" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PA", "PB", "PC" }, got.Select(m => m.pawnDescription));
            Assert.Equal("", got[1].textureFile);   // B wrote none; the nameless entry's is not B's
            Assert.Equal(0, got[1].ta);
        }

        // ...and the count is of objects that carry a NAME: two nameless models the walk closed do not stand in for two
        // named ones it swallowed.
        [Fact]
        public void NamelessModelsBeforeTheDamage_DoNotHideIt()
        {
            string doc = Head + Models + Nameless + ", " + Nameless + ", " + WithoutItsLast(A, " }") + ", " + B + Tail;
            DoesNotParse(doc);
            Assert.NotNull(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PA", "PB" }, got.Select(m => m.pawnDescription));
            Assert.Equal(new[] { 2f, 1f }, got.Select(m => m.scale));
        }

        // The structure intact, the entry texts are the objects and nothing is reported. The document does not parse (a
        // comma is missing between the first two), and its strings hold a bracket and a brace.
        [Fact]
        public void EntryTexts_OfAnIntactArray_AreItsObjects_AndReportNoDamage()
        {
            const string doc = @"{ ""models"": [ { ""hideMeshes"": ""x ] y"", ""resourceName"": ""A"" } { ""pawnDescription"": ""nameless"" }, { ""resourceName"": ""B"", ""hideMeshes"": ""a } b ] c"" } ] }";
            DoesNotParse(doc);
            var texts = UniversalInject.RegistryEntryTexts(doc, "models", "resourceName", out string damage, out int namesWithoutArray, out _);
            Assert.Null(damage);
            Assert.Equal(0, namesWithoutArray);
            Assert.Equal(new[] { @"{ ""hideMeshes"": ""x ] y"", ""resourceName"": ""A"" }", @"{ ""pawnDescription"": ""nameless"" }", @"{ ""resourceName"": ""B"", ""hideMeshes"": ""a } b ] c"" }" }, texts);
        }

        // ...and of a damaged one, the objects around the names: what stands between two entries belongs to neither.
        [Fact]
        public void EntryTexts_OfADamagedArray_AreTheObjectsAroundTheNames()
        {
            var texts = UniversalInject.RegistryEntryTexts(Head + Models + WithoutItsLast(A, " }") + ", " + B + ", " + C + Tail, "models", "resourceName", out string damage, out _, out _);
            Assert.Equal("3 object(s) with a \"resourceName\" in the text, 1 readable by their braces", damage);
            Assert.Equal(new[] { WithoutItsLast(A, " }"), B, C }, texts);
        }

        // A DOCUMENT THAT PARSES and still reaches the fallback (every model fails the object parse: a scale that is no
        // number) is read by its braces, whatever the count says. This one the count would send through the recovery
        // (a brace in a string inside an array of objects, then a named object: 2 names, 1 object by braces) - held,
        // or the test proves nothing, as its first version did - and the recovery would make a model of "p" and lose
        // the nameless one.
        [Fact]
        public void AParsedDocumentWhoseModelsAllFailed_KeepsEveryObject()
        {
            const string doc = @"{ ""models"": [ { ""resourceName"": ""A"", ""scale"": ""big"", ""parts"": [ { ""k"": ""}"" }, { ""resourceName"": ""p"" } ] }, { ""pawnDescription"": ""nameless"", ""scale"": ""big"" } ] }";
            Newtonsoft.Json.Linq.JObject.Parse(doc);
            Assert.NotNull(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "", "nameless" }, got.Select(m => m.pawnDescription));
        }

        // ---- the third review (2026-11-24, on the pushed PR): hand edits, and what a blind reread found ----

        // A REMARK THAT QUOTES A NAME, standing between two models, is no model. The count took it for a model that
        // lost its opening brace: `}, # old: "resourceName": "OldName"` made a 52nd model of the reference pack, every
        // value at its default, and sent a file with sound braces through the recovery. A name with nothing but its
        // value before the next object or the array's end is a remark.
        [Theory]
        [InlineData("behind a model", A + @", # old: ""resourceName"": ""OldName""" + "\n" + B + ", " + C)]
        [InlineData("on its own line, with a date", A + ",\n" + @"// renamed from ""resourceName"": ""Tank1"" on 11-20" + "\n" + B + ", " + C)]
        [InlineData("at the array's opening", @"# was ""resourceName"": ""OldName""" + "\n" + A + ", " + B + ", " + C)]
        [InlineData("before the array's end", A + ", " + B + ", " + C + @" # ""resourceName"": ""OldName""" + "\n")]
        public void ARemarkThatQuotesAName_BetweenTwoModels_IsNoModel(string what, string body)
        {
            string doc = @"{ ""modId"": ""m"",, " + Models + body + Tail;
            DoesNotParse(doc);
            Assert.True(Damage(doc) == null, what + ": " + Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.True(new[] { "A", "B", "C" }.SequenceEqual(got.Select(m => m.resourceName)), what + ": " + string.Join(",", got.Select(m => m.resourceName)));
            Assert.Equal(new[] { "PA", "PB", "PC" }, got.Select(m => m.pawnDescription));
        }

        // ...and a model that DID lose its opening brace is still one: its own closing brace follows its keys.
        // Its text begins behind the closing brace of the model above, not at its name: written name-last, its keys
        // were read as the model's above (B took the scale and atlas X wrote), and it came back with its name alone.
        [Fact]
        public void KeysBeforeTheName_OfAModelWithoutItsOpeningBrace_StayWithIt()
        {
            const string x = @"""pawnDescription"": ""PX"", ""scale"": 4, ""atlas"": [41,42,43,44], ""resourceName"": ""X"" }";
            string doc = Head + Models + B + ", " + x + ", " + C + Tail;
            DoesNotParse(doc);
            Assert.NotNull(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "B", "X", "C" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PB", "PX", "PC" }, got.Select(m => m.pawnDescription));
            Assert.Equal(new[] { 1f, 4f, 3f }, got.Select(m => m.scale));     // B wrote none: not X's 4
            Assert.Equal(new[] { 0, 41, 25 }, got.Select(m => m.ta));
        }

        // ...and so is a model whose name line slipped ABOVE its opening brace (two lines swapped): a comma stands
        // between the name and the object, which a remark does not end in.
        [Fact]
        public void ANameLineAboveItsOpeningBrace_IsStillThatModel()
        {
            string doc = Head + Models + A + @", ""resourceName"": ""B""," + "\n" + @"{ ""pawnDescription"": ""PB"", ""skel"": [11,12,13,14] }, " + C + Tail;
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B", "C" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PA", "PB", "PC" }, got.Select(m => m.pawnDescription));
            Assert.Equal(new[] { 1, 11, 21 }, got.Select(m => m.sa));
        }

        // TWO ADJACENT LINES DELETED at the end of a model: the `]` of its last array and its `},`. The array looks
        // open, and the count took every model behind it for one of its objects: nothing told the damage, the braces
        // read one model, and every later unit was gone (49 of the reference pack's 51 positions, as before this
        // reader). An element of an array has a `[` or a comma before it; the next model here has a number.
        [Fact]
        public void TheEndOfAModelsLastArrayAndItsBraceDeleted_CostNoLaterModel()
        {
            string doc = Head + Models + Swap(A, @"""atlas"": [5,6,7,8] }", @"""atlas"": [5,6,7,8") + " " + B + ", " + C + Tail;
            DoesNotParse(doc);
            Assert.NotNull(Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B", "C" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PA", "PB", "PC" }, got.Select(m => m.pawnDescription));
            Assert.Equal(new[] { 2f, 1f, 3f }, got.Select(m => m.scale));
            Assert.Equal(new[] { 1, 11, 21 }, got.Select(m => m.sa));
        }

        // ...and the last model then ends at the array's `]`, though the count of brackets paid that one to the array
        // that lost its own: B wrote no scale and must not take the tuning table's.
        [Fact]
        public void TheEndOfAModelsLastArrayAndItsBraceDeleted_TheLastModelDoesNotReadTheTuningTable()
        {
            string doc = Head + Models + Swap(A, @"""atlas"": [5,6,7,8] }", @"""atlas"": [5,6,7,8") + " " + C + ", " + B + Tail;
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "C", "B" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { 2f, 3f, 1f }, got.Select(m => m.scale));   // B: not the table's 9
        }

        // A KNOWN LIMIT, HELD SO IT CANNOT MOVE UNSEEN. Three lines deleted (the array's last number with them) leave
        // a comma before the next model: it reads exactly like an object in a mixed array, `[ 5, 6, 7, { ... } ]`,
        // and is taken for one. The models behind it are lost, as before this reader - and the log says a name was
        // passed over inside a model.
        [Fact]
        public void TheLastNumberTheBracketAndTheBraceDeleted_StillCostTheLaterModels_AndTheLogSaysANameWasPassedOver()
        {
            string doc = Head + Models + Swap(A, @"""atlas"": [5,6,7,8] }", @"""atlas"": [5,6,7,") + " " + B + ", " + C + Tail;
            DoesNotParse(doc);
            List<ModelEntry> got = null;
            var said = SaidWhile(() => got = UniversalInject.ParseModels(doc));
            Assert.Equal(new[] { "A" }, got.Select(m => m.resourceName));
            Assert.Contains(said, line => line.Contains("\"resourceName\" key(s) stand inside a model that already has one"));
        }

        // AN UNPAIRED `[` INSIDE A STRING ("Gun[0") looked like an open array too: the intact model behind it was
        // counted as its object and dropped, where the braces alone had read it.
        [Fact]
        public void AnUnpairedBracketInAString_ThenADamage_CostsNoModel()
        {
            string doc = Head + Models + Swap(A, @"""ha""", @"""Gun[0""") + ", " + B + ", " + C.Substring(2) + Tail;
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B", "C" }, got.Select(m => m.resourceName));
            Assert.Equal(new[] { "PA", "PB", "PC" }, got.Select(m => m.pawnDescription));
            Assert.Equal("Gun[0", got[0].hideMeshes);
        }

        // A MODEL WHOSE NAME KEY LOST A QUOTE has no name to be read under, and is not read. It must still be counted:
        // left out, the model after it made the count come out even when it was the last but one, and the last model
        // was read under its own name with this one's pawn and skeleton - on the reference pack "Zeppelin" with the
        // volley gun's, without a word in the log. Anywhere else in the array the count already fired.
        [Theory]
        [InlineData("its opening quote, last but one", @"{ resourceName"": ""B""", false)]
        [InlineData("its closing quote, last but one", @"{ ""resourceName: ""B""", false)]
        [InlineData("its opening quote, first", @"{ resourceName"": ""B""", true)]
        public void AModelWhoseNameKeyLostAQuote_IsNotRead_AndGivesTheNextModelNothing(string what, string damagedKey, bool first)
        {
            string b = Swap(B, @"{ ""resourceName"": ""B""", damagedKey);
            string doc = Head + Models + (first ? b + ", " + A : A + ", " + b) + ", " + C + Tail;
            DoesNotParse(doc);
            List<ModelEntry> got = null;
            var said = SaidWhile(() => got = UniversalInject.ParseModels(doc));
            Assert.True(new[] { "A", "C" }.SequenceEqual(got.Select(m => m.resourceName)), what + ": " + string.Join(",", got.Select(m => m.resourceName)));
            Assert.Equal(new[] { "PA", "PC" }, got.Select(m => m.pawnDescription));   // C: not B's PB
            Assert.Equal(new[] { 1, 21 }, got.Select(m => m.sa));                      // C: not B's 11
            Assert.Contains(said, line => line.Contains("1 of them not read, a quote of the \"resourceName\" key itself is lost"));
        }

        // A BRACE INSIDE A STRING, between two name keys of ONE model. The count walks back from a key without regard
        // to strings and took that brace for the model's end: a file with sound braces went through the recovery, the
        // second key became a ghost model with the first one's pawn and scale, and the nameless model was lost.
        [Theory]
        [InlineData("the key written twice", @"""hideMeshes"": ""a } b"", ""resourceName"": ""A2""")]
        [InlineData("a comment that quotes the old key", @"""hideMeshes"": ""a } b"", /* was ""resourceName"": ""OLD"" */ ""k"": 1")]
        [InlineData("an opening brace in the string", @"""hideMeshes"": ""a { b"", ""resourceName"": ""A2""")]
        public void ASecondNameKeyBehindABraceInAString_DoesNotSwitchTheReader(string what, string extra)
        {
            string doc = @"{ ""modId"": ""m"",, ""models"": [ { ""resourceName"": ""A"", " + extra + @", ""pawnDescription"": ""PA"", ""scale"": 2 }, { ""pawnDescription"": ""nameless"" } ] }";
            DoesNotParse(doc);
            Assert.True(Damage(doc) == null, what + ": " + Damage(doc));
            var got = UniversalInject.ParseModels(doc);
            Assert.True(new[] { "A", "" }.SequenceEqual(got.Select(m => m.resourceName)), what + ": " + string.Join(",", got.Select(m => m.resourceName)));
            Assert.Equal(new[] { "PA", "nameless" }, got.Select(m => m.pawnDescription));
            Assert.Equal(new[] { 2f, 1f }, got.Select(m => m.scale));
        }

        // TWO MODELS THAT LOST THE `}, {` BETWEEN THEM read as one, under the first one's name: nothing tells that
        // from a name written twice. The second is lost - and the log says that a second name was passed over.
        [Fact]
        public void TwoModelsWithoutTheSeamBetweenThem_AreOne_AndTheLogSaysASecondNameWasPassedOver()
        {
            string doc = @"{ ""modId"": ""m"",, " + Models + WithoutItsLast(A, " }") + ", " + B.Substring(2) + ", " + C + Tail;
            DoesNotParse(doc);
            List<ModelEntry> got = null;
            var said = SaidWhile(() => got = UniversalInject.ParseModels(doc));
            Assert.Equal(new[] { "A", "C" }, got.Select(m => m.resourceName));
            Assert.Contains(said, line => line.Contains("1 \"resourceName\" key(s) stand inside a model that already has one"));
            // ...and a pack with one name per model does not say it
            said = SaidWhile(() => UniversalInject.ParseModels(@"{ ""modId"": ""m"",, " + Models + A + ", " + B + ", " + C + Tail));
            Assert.DoesNotContain(said, line => line.Contains("already has one"));
        }

        // THE ARRAY'S KEY WRITTEN TWICE, the empty one first: the fallback reads the first, as it always did, and
        // finds no model. It must not leave that unexplained.
        [Fact]
        public void AnEmptyModelsArrayBeforeTheRealOne_LoadsNothing_AndSaysWhy()
        {
            string doc = @"{ ""modId"": ""m"",, ""models"": [], " + Models + A + ", " + B + " ] }";
            DoesNotParse(doc);
            List<ModelEntry> got = null;
            var said = SaidWhile(() => got = UniversalInject.ParseModels(doc));
            Assert.Empty(got);
            Assert.Contains(said, line => line.Contains("the pack holds 2 \"resourceName\" key(s), none of them in a readable \"models\" array"));
        }

        // A bracket count is kept per entry: carried from one entry to the next, the `]` a model's skel lost would be
        // paid by the array's own, and the last model would run on into the tuning table behind it.
        [Fact]
        public void ABracketLostInOneModel_IsNotOwedByTheLast()
        {
            const string doc = @"{ ""models"": [ { ""resourceName"": ""A"", ""skel"": [1,2,3,4 }, { ""resourceName"": ""B"", ""pawnDescription"": ""PB"" ], ""unitScales"": [ { ""match"": ""PA"", ""scale"": 9 } ] }";
            DoesNotParse(doc);
            var got = UniversalInject.ParseModels(doc);
            Assert.Equal(new[] { "A", "B" }, got.Select(m => m.resourceName));
            Assert.Equal(1f, got[1].scale);   // not the table's 9
        }

        // The pattern that finds the array must not try every split of a run of white space: `"models"` followed by
        // 20,000 spaces and no `[` took 5 s (12 s on the game's Mono).
        [Fact]
        public void AModelsKeyBeforeALongRunOfWhiteSpace_IsRefusedAtOnce()
        {
            string doc = @"{ ""resourceName"": ""A"", ""models""" + new string(' ', 20000) + "x }";
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.Empty(UniversalInject.RegistryEntryTexts(doc, "models", "resourceName", out _, out int names, out _));
            clock.Stop();
            Assert.Equal(1, names);
            Assert.True(clock.ElapsedMilliseconds < 1000, clock.ElapsedMilliseconds + " ms");
        }
    }
}
