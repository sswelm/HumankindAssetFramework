using System;
using System.Collections.Generic;
using System.Globalization;

namespace HumankindAssetFramework
{
    // ---------------------------------------------------------------------------------------------------------
    // LIVE DIAL FILES — the PURE parse half (2026-08-20).
    //
    // HAF ships four hand-tunable `BepInEx/config/haf_*.txt` dials (rotor trim, turn ease, terrain hug, battle
    // turn). Each used to inline its own `key=value` loop inside its Poll* method, next to File I/O, the Unity
    // clock and live-pawn reflection — so none of it could be tested, and all four shared one silent failure:
    // ANY line the parser did not understand was `continue`d away with no message. A user who typed `radus=6`,
    // `Rate = 5 deg`, or `hoverbanks=12` got a working plugin that silently ignored the setting, with nothing in
    // the log naming the typo. That is exactly the "silently disarmed" class docs/notes/Audit-2026-07-31.md was
    // written about.
    //
    // This file is the parse half, lifted out whole: no Unity, no file I/O, no reflection, no statics mutated.
    // Text in, typed config + a list of human-readable problems out. The Poll* methods keep the I/O and the
    // apply, hand the text here, and LOG whatever problems come back. Unit-tested in Tests/DialParseTests.cs.
    // Follows the SmokeVerdict pattern (docs/Testing.md): push the decision into a pure function, test that.
    //
    // NOT here: haf_hexsculpt.txt, which is a single bare value (a definition name), not a key=value file.
    // ---------------------------------------------------------------------------------------------------------
    internal static class DialConfig
    {
        internal struct Line
        {
            public int Number;       // 1-based line number in the source text (so a problem can name the line)
            public string KeyRaw;    // the whole key half, trimmed, ORIGINAL case (rotor-trim bone names need it)
            public string Key;       // KeyRaw lowercased — what the keyword dials switch on
            public string Value;     // the value half, trimmed
        }

        // Split the file into key=value lines. Blank lines and '#' comments are skipped silently (they are
        // intentional); anything else that is not exactly one 'key=value' is reported, not swallowed.
        // NOTE: the key half is kept WHOLE (a '@' qualifier is not split off here) so that `rate@2=5` stays an
        // unknown key for the keyword dials exactly as it did before. RotorTrimDial splits '@' itself.
        internal static List<Line> Tokenize(string text, List<string> problems)
        {
            var lines = new List<Line>();
            if (string.IsNullOrEmpty(text)) return lines;
            var raws = text.Split('\n');
            for (int i = 0; i < raws.Length; i++)
            {
                var line = raws[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.Split('=');
                if (eq.Length != 2)
                {
                    problems?.Add(eq.Length < 2
                        ? $"line {i + 1}: '{line}' is not a 'key=value' setting — ignored"
                        : $"line {i + 1}: '{line}' has more than one '=' — ignored");
                    continue;
                }
                var key = eq[0].Trim();
                lines.Add(new Line { Number = i + 1, KeyRaw = key, Key = key.ToLowerInvariant(), Value = eq[1].Trim() });
            }
            return lines;
        }

        // Invariant-culture float read. A comma-decimal locale writing "1,5" lands here as a named problem
        // instead of a silently-dropped line (the plugin has always parsed invariant — this just says so).
        internal static bool Num(Line l, List<string> problems, out float v)
        {
            if (float.TryParse(l.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return true;
            problems?.Add($"line {l.Number}: '{l.KeyRaw}' needs a number, got '{l.Value}' — ignored" +
                          (l.Value.IndexOf(',') >= 0 ? " (use '.' for the decimal point)" : ""));
            return false;
        }

        // Format a dial number the way the dial FILE spells it. Found by the 2026-08-20 in-game drill: the echo
        // lines used plain interpolation, so on a comma-decimal machine the log said `lookahead=1,5` — the exact
        // spelling the parser rejects, printed one line above a warning telling the user to use '.'. Copying a
        // value out of the log back into the file silently disabled the setting. Anything echoed must be
        // re-parseable by Num() — asserted as a round-trip property in DialParseTests.
        internal static string Inv(float v) => v.ToString(CultureInfo.InvariantCulture);

        internal static void Unknown(Line l, List<string> problems, string known) =>
            problems?.Add($"line {l.Number}: unknown setting '{l.KeyRaw}' — ignored. Known: {known}");
    }

    // haf_turnease.txt — eased facing. Per-model > category > the global `rate`. See docs/Turn-Ease.md.
    internal sealed class TurnEaseDial
    {
        internal float Rate, Bank, Human, Land, Turret, Hover, Ship, HoverBank, ShipBank;
        // PIVOT-IN-PLACE threshold (deg): a NON-hover eased unit whose heading change is at least this large
        // turns on the spot first and only then moves off (the rendered position holds, then catches up).
        // Default 90 — the one turn-ease key with a non-zero default, so a legacy file without it keeps the
        // behaviour a fresh install shows; 0 = off (turn while moving, the pre-2026-08-22 behaviour).
        internal float Pivot = 90f;
        internal float ElevHold = 1.5f;   // seconds the gun holds its firing elevation after the shot, before easing down
        internal float ElevFall = 2f;     // seconds it takes to ease back to the resting elevation
        const string Known = "rate, bank, human, land, turret, hover (air), ship, hoverbank, shipbank, pivot";

        internal static TurnEaseDial Parse(string text, List<string> problems)
        {
            var d = new TurnEaseDial();
            bool seenHoverBank = false;
            foreach (var l in DialConfig.Tokenize(text, problems))
            {
                switch (l.Key)
                {
                    case "rate":      if (DialConfig.Num(l, problems, out var r))  d.Rate = r;      break;
                    case "bank":      if (DialConfig.Num(l, problems, out var b))  d.Bank = b;      break;   // legacy/fallback bank
                    case "human":     if (DialConfig.Num(l, problems, out var hu)) d.Human = hu;    break;
                    case "land":      if (DialConfig.Num(l, problems, out var la)) d.Land = la;     break;   // turretless land vehicles
                    case "turret":    if (DialConfig.Num(l, problems, out var tu)) d.Turret = tu;   break;   // land vehicles WITH a traversing turret
                    case "hover":
                    case "air":       if (DialConfig.Num(l, problems, out var ho)) d.Hover = ho;    break;   // "air" = legacy alias; PLANES stay excluded
                    case "ship":      if (DialConfig.Num(l, problems, out var sh)) d.Ship = sh;     break;
                    case "hoverbank": if (DialConfig.Num(l, problems, out var hb)) { d.HoverBank = hb; seenHoverBank = true; } break;
                    case "shipbank":  if (DialConfig.Num(l, problems, out var sb)) d.ShipBank = sb; break;
                    case "elevhold":  if (DialConfig.Num(l, problems, out var eh)) d.ElevHold = eh; break;   // gun elevation: hold after the shot
                    case "elevfall":  if (DialConfig.Num(l, problems, out var ef)) d.ElevFall = ef; break;   // ...and how long the way down takes
                    case "pivot":     if (DialConfig.Num(l, problems, out var pv)) d.Pivot = pv;    break;   // 0 = off
                    default: DialConfig.Unknown(l, problems, Known); break;
                }
            }
            // Legacy files that never mention hoverbank keep inheriting the global `bank`. Resolved AFTER the
            // whole file is read, so `hoverbank` before `bank` behaves the same as after it.
            if (!seenHoverBank) d.HoverBank = d.Bank;
            return d;
        }
    }

    // haf_hugterrain.txt — fly low over open ground, climb for built districts. See docs/Donor-Clip-Flight.md.
    internal sealed class TerrainHugDial
    {
        internal float Drop, Radius, Lookahead = 3f, Ease = 4f, Cliff = 1f;   // defaults as shipped
        internal readonly List<string> Only = new List<string>();             // name whitelist (empty = all)
        internal readonly List<string> Skip = new List<string>();             // name blacklist (farms, exploitations)
        const string Known = "drop, radius, lookahead, ease, cliff, only, skip";

        internal static TerrainHugDial Parse(string text, List<string> problems)
        {
            var d = new TerrainHugDial();
            foreach (var l in DialConfig.Tokenize(text, problems))
            {
                // The two CSV name filters are read BEFORE any numeric parse — they are lists, not numbers.
                if (l.Key == "only" || l.Key == "skip")
                {
                    var target = l.Key == "only" ? d.Only : d.Skip;
                    foreach (var s in l.Value.Split(',')) if (s.Trim().Length > 0) target.Add(s.Trim());
                    if (target.Count == 0)
                        problems?.Add($"line {l.Number}: '{l.KeyRaw}' is empty — no names, so the filter does nothing");
                    continue;
                }
                switch (l.Key)
                {
                    case "drop":      if (DialConfig.Num(l, problems, out var dr)) d.Drop = dr;      break;
                    case "radius":    if (DialConfig.Num(l, problems, out var ra)) d.Radius = ra;    break;
                    case "lookahead": if (DialConfig.Num(l, problems, out var lo)) d.Lookahead = lo; break;
                    case "ease":      if (DialConfig.Num(l, problems, out var ea)) d.Ease = ea;      break;
                    case "cliff":     if (DialConfig.Num(l, problems, out var cl)) d.Cliff = cl;     break;
                    default: DialConfig.Unknown(l, problems, Known); break;
                }
            }
            // `drop` is how much LOWER to fly, so it is meant to be negative. A positive value flies HIGHER over
            // open ground than over cities — almost certainly a dropped minus sign. The pack validator already
            // warns about the same mistake in the registry (PackValidatorTests.PositiveHugDrop_Warns).
            if (d.Drop > 0f)
                problems?.Add($"drop={d.Drop} is positive — drop is how much LOWER to fly, so it should be negative (did you mean -{d.Drop}?)");
            return d;
        }
    }

    // haf_rotortrim.txt — one line per bone, `BoneSubstring@axis=degrees`. See the PollRotorTrim comment.
    internal sealed class RotorTrimDial
    {
        internal struct Trim { public string Bone; public int Axis; public float Deg; }
        internal readonly List<Trim> Trims = new List<Trim>();

        internal const int Slots = 4;   // BoneRotation slots on a pawn entry — ApplyRotorTrim stops at 4

        internal static RotorTrimDial Parse(string text, List<string> problems)
        {
            var d = new RotorTrimDial();
            foreach (var l in DialConfig.Tokenize(text, problems))
            {
                if (!DialConfig.Num(l, problems, out var deg)) continue;
                // The key half is `BoneSubstring@axis`; the bone keeps its original case (it is matched
                // case-insensitively against live bone names, but it is a name, not a keyword).
                var at = l.KeyRaw.Split('@');
                var bone = at[0].Trim();
                int axis = 0;
                if (at.Length > 1)
                {
                    if (int.TryParse(at[1].Trim(), out var a)) axis = a;
                    else problems?.Add($"line {l.Number}: axis '{at[1].Trim()}' is not a number — using axis 0 (X)");
                }
                if (bone.Length == 0) { problems?.Add($"line {l.Number}: no bone name before '@' — ignored"); continue; }
                if (axis < 0 || axis > 2)
                    problems?.Add($"line {l.Number}: axis {axis} is out of range — must be 0 (X), 1 (Y) or 2 (Z)");
                d.Trims.Add(new Trim { Bone = bone, Axis = axis, Deg = deg });
            }
            // ApplyRotorTrim writes into 4 BoneRotation slots and stops; a 5th line was silently discarded.
            if (d.Trims.Count > Slots)
                problems?.Add($"{d.Trims.Count} trim lines, but only the first {Slots} are applied " +
                              $"(a pawn has {Slots} BoneRotation slots) — these are ignored: " +
                              string.Join(", ", d.Trims.GetRange(Slots, d.Trims.Count - Slots).ConvertAll(t => t.Bone).ToArray()));
            return d;
        }
    }

    // haf_battleturn.txt — the battle turn-rate / hold-fire experiment. See Patches/BattleTurnPatch.cs.
    internal sealed class BattleTurnDial
    {
        internal bool HoldFire, Diag;
        const string Known = "hold, diag";

        internal static BattleTurnDial Parse(string text, List<string> problems)
        {
            var d = new BattleTurnDial();
            foreach (var l in DialConfig.Tokenize(text, problems))
            {
                switch (l.Key)
                {
                    case "hold": if (DialConfig.Num(l, problems, out var h))  d.HoldFire = h > 0f; break;
                    case "diag": if (DialConfig.Num(l, problems, out var dg)) d.Diag = dg > 0f;    break;
                    default: DialConfig.Unknown(l, problems, Known); break;
                }
            }
            return d;
        }
    }
}
