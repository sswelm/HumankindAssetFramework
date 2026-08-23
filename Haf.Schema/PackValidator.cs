using System;
using System.Collections.Generic;
using System.Globalization;

namespace Haf.Schema
{
    // PACK PRE-FLIGHT VALIDATOR — the pure rule core (docs/Pack-Validator-Design.md, built 2026-08-18).
    // Turns SILENT content failures (a typo'd bone, a missing WAV, an out-of-range dial) into named, actionable
    // messages. ONE rule set over the shared schema, consumed by two thin hosts:
    //   - the editor's "Validate pack" button (pre-ship: files/bones/pawn/schema — the author sees it before shipping)
    //   - the plugin's boot-time pre-flight (on the end user's machine, into haf_load_report.txt)
    // The host supplies an IValidationContext for the lookups it can answer; NULL = "can't check here" and the
    // check is SKIPPED (never guessed). Severity: Warning = the feature degrades but the pack loads (the fail-soft
    // rule stands — the validator EXPLAINS, it never blocks); Error = the entry cannot work at all.
    public enum ValidationSeverity { Warning, Error }

    public struct ValidationIssue
    {
        public ValidationSeverity Severity;
        public string Field;     // the schema field at fault ("muzzleBone")
        public string Message;   // human sentence, options included where cheap
        public override string ToString() => $"[{(Severity == ValidationSeverity.Error ? "ERROR" : "warn")}] {Field}: {Message}";
    }

    // Tri-state lookups: true/false = checked; null = this host can't know (check skipped, not guessed).
    public interface IValidationContext
    {
        bool? PawnExists(string pawnDescription);
        bool? SoundFileExists(string fileName);
        bool? SkinFileExists(string fileName);
        bool? BoneExists(string boneNameSubstring);   // substring semantics, matching the runtime's bone matching
    }

    public static class PackValidator
    {
        public static List<ValidationIssue> ValidateEntry(HafModelSchema e, IValidationContext ctx)
        {
            var issues = new List<ValidationIssue>();
            void Add(ValidationSeverity s, string field, string msg) => issues.Add(new ValidationIssue { Severity = s, Field = field, Message = msg });
            void Warn(string field, string msg) => Add(ValidationSeverity.Warning, field, msg);

            // ---- identity (the only ERRORs: without these the entry can't do anything) ----
            if (string.IsNullOrWhiteSpace(e.resourceName)) Add(ValidationSeverity.Error, "resourceName", "empty — the entry has no identity, nothing can load");
            if (string.IsNullOrWhiteSpace(e.pawnDescription)) Add(ValidationSeverity.Error, "pawnDescription", "empty — no target unit, the entry will never match anything");
            else if (ctx?.PawnExists(e.pawnDescription) == false)
                Warn("pawnDescription", $"'{e.pawnDescription}' matches no known unit descriptor — check the exact name (editor: use the Pick list)");
            // NAMING CONVENTION (2026-08-21, the TankDestroyers _DRILL leftover): every game pawn definition is named
            // <Era>_<Kind>_<Unit>_NN; the runtime matches by addon.IndexOf(pawnDescription), so a pawnDescription with
            // anything AFTER the _NN can never match — the unit silently renders as its donor. Checkable without the game.
            else if (!System.Text.RegularExpressions.Regex.IsMatch(e.pawnDescription.Trim(), @"_[0-9]{2}$"))
                Warn("pawnDescription", $"'{e.pawnDescription}' does not end in _NN (e.g. Era6_Common_TankDestroyers_01) — game pawn definitions always do; a stray suffix can never match the unit's addon, so the unit would keep its donor model");

            // ---- referenced files (existence via the host; format by extension) ----
            void SndFile(string field, string file)
            {
                if (string.IsNullOrEmpty(file)) return;
                if (!file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    Warn(field, $"'{file}' is not a .wav — the loader only decodes WAV (16-bit PCM); convert mp3/ogg first");
                if (ctx?.SoundFileExists(file) == false)
                    Warn(field, $"'{file}' not found (looked in the pack's sounds/ then the shared haf_sounds/)");
            }
            SndFile("soundFile", e.soundFile); SndFile("soundStartFile", e.soundStartFile); SndFile("soundStopFile", e.soundStopFile);
            SndFile("soundIdleFile", e.soundIdleFile); SndFile("soundAttackFile", e.soundAttackFile);
            SndFile("soundDeathFile", e.soundDeathFile); SndFile("soundBattleFile", e.soundBattleFile);
            if (!string.IsNullOrEmpty(e.textureFile))
            {
                if (!e.textureFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    Warn("textureFile", $"'{e.textureFile}' is not a .png — the skin loader reads PNG only");
                if (ctx?.SkinFileExists(e.textureFile) == false)
                    Warn("textureFile", $"'{e.textureFile}' not found (looked in the pack's skins/ then the shared haf_skins/)");
            }

            // ---- bone-name substrings (existence via the host — the classic 'Turrret' typo) ----
            void Bone(string field, string sub)
            {
                if (string.IsNullOrEmpty(sub)) return;
                if (ctx?.BoneExists(sub) == false)
                    Warn(field, $"no bone matches '{sub}' on this model's skeleton — the feature will silently not happen");
            }
            Bone("turretBone", e.turretBone); Bone("muzzleBone", e.muzzleBone); Bone("handPropBone", e.handPropBone);

            // ---- "x,y,z" / "a,b,c,d" format fields ----
            if (!TripleParses(e.muzzleOffset)) Warn("muzzleOffset", $"'{e.muzzleOffset}' is not 'x,y,z' numbers — the offset will be ignored");
            if (!TripleParses(e.handPropAngles)) Warn("handPropAngles", $"'{e.handPropAngles}' is not 'x,y,z' degrees — the baked angles will be used instead");
            if (!GuidCsvParses(e.handPropGuid)) Warn("handPropGuid", $"'{e.handPropGuid}' is not 'a,b,c,d' integers (the Prop Lab prints the exact value)");
            if (!GuidCsvParses(e.handPropMat)) Warn("handPropMat", $"'{e.handPropMat}' is not 'a,b,c,d' integers");
            if (!string.IsNullOrEmpty(e.handPropName) && string.IsNullOrEmpty(e.handPropGuid))
                Warn("handPropName", $"'{e.handPropName}' is set but handPropGuid is empty — no prop will attach (paste the guid the Prop Lab bake printed)");

            // ---- numeric ranges (each bound comes from the field's own documented semantics) ----
            if (e.scale <= 0f) Warn("scale", $"{F(e.scale)} — must be > 0 (0 or negative renders invisible/inverted); 1 = unchanged");
            if (e.desaturate < 0f || e.desaturate > 1f) Warn("desaturate", $"{F(e.desaturate)} — range is 0..1 (0 = off, 1 = full grey)");
            if (e.brightness <= 0f) Warn("brightness", $"{F(e.brightness)} — must be > 0 (1 = unchanged)");
            void Tint(string field, float v) { if (v < -255f || v > 255f) Warn(field, $"{F(v)} — range is -255..+255"); }
            Tint("tintR", e.tintR); Tint("tintG", e.tintG); Tint("tintB", e.tintB);
            void Vol(string field, float v) { if (v < 0f || v > 2f) Warn(field, $"{F(v)} — volume range is 0..2"); }
            Vol("soundVolume", e.soundVolume); Vol("soundStartVolume", e.soundStartVolume); Vol("soundStopVolume", e.soundStopVolume);
            Vol("soundIdleVolume", e.soundIdleVolume); Vol("soundAttackVolume", e.soundAttackVolume);
            Vol("soundDeathVolume", e.soundDeathVolume); Vol("soundBattleVolume", e.soundBattleVolume);
            void Offset(string field, float v) { if (v < 0f) Warn(field, $"{F(v)} — a start offset can't be negative"); }
            Offset("soundAttackOffset", e.soundAttackOffset); Offset("soundDeathOffset", e.soundDeathOffset); Offset("soundBattleOffset", e.soundBattleOffset);
            if (e.animPhaseSpread < 0f || e.animPhaseSpread > 1f) Warn("animPhaseSpread", $"{F(e.animPhaseSpread)} — range is 0..1 (0 = lockstep, 1 = whole clip)");
            if (e.deployPoseTime < 0f || e.deployPoseTime > 1f) Warn("deployPoseTime", $"{F(e.deployPoseTime)} — normalized clip time, range 0..1");
            if (e.deploySpeed <= 0f) Warn("deploySpeed", $"{F(e.deploySpeed)} — must be > 0 (1 = authored speed)");
            if (e.recoilSpeed <= 0f) Warn("recoilSpeed", $"{F(e.recoilSpeed)} — must be > 0 (1 = authored speed)");
            if (e.attackRepeats < 1) Warn("attackRepeats", $"{e.attackRepeats} — must be >= 1");
            if (e.turretAxis < -1 || e.turretAxis > 2) Warn("turretAxis", $"{e.turretAxis} — -1 = game's axis, or 0/1/2 for local X/Y/Z");
            if (e.gunElevMax != 0f && (e.gunElevAxis < 0 || e.gunElevAxis > 2)) Warn("gunElevAxis", $"{e.gunElevAxis} — 0/1/2 for the gun bone's local X/Y/Z pitch axis");
            if (e.hugDrop > 0f) Warn("hugDrop", $"{F(e.hugDrop)} — terrain hug expects a NEGATIVE drop (e.g. -2); positive lifts the unit further");
            if (e.combatZ < -5f || e.combatZ > 5f) Warn("combatZ", $"{F(e.combatZ)} — combat height offset beyond ±5 units (a submarine combat dive is around -0.5)");

            // ---- mutual exclusions the schema documents ----
            if (e.animStateDriven && e.fireOnAttack)
                Warn("fireOnAttack", "state-driven mode is mutually exclusive with fireOnAttack — use the state roles' ATTACK clip instead");
            if (e.animStateDriven && e.deployOnStop)
                Warn("deployOnStop", "state-driven mode is mutually exclusive with deployOnStop — use the PRE-MOVE/IDLE state roles instead");
            if (e.turnBank != 0f && e.turnRate <= 0f)
                Warn("turnBank", $"{F(e.turnBank)} set but turnRate is 0 — bank only applies while turn ease is on (set turnRate > 0)");
            return issues;
        }

        // "" is valid (feature off); non-empty must be n,n,n floats.
        internal static bool TripleParses(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            var p = s.Split(',');
            if (p.Length != 3) return false;
            foreach (var x in p) if (!float.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;
            return true;
        }

        // "" is valid (feature off); non-empty must be a,b,c,d integers (Amplitude guid components can be negative).
        internal static bool GuidCsvParses(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            var p = s.Split(',');
            if (p.Length != 4) return false;
            foreach (var x in p) if (!int.TryParse(x.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return false;
            return true;
        }

        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // ================= PACK WRAPPER METADATA (2026-08-23) =================
        // Until now this validator had ~30 rules for entry CONTENT (bones, files, pawns, ranges) and NOT ONE for the
        // wrapper — modId / schemaVersion / dependsOn / loadAfter / overrides. So a pack whose wrapper is wrong was
        // handled gracefully at RUNTIME (it warns by name and fails soft) but was never caught at AUTHORING time,
        // which is the only place the author can still fix it.
        //
        // Every rule below MIRRORS behaviour verified in UniversalInjectPatch, not invented policy:
        //   * ParsePack           — a blank/non-string wrapper key falls back to the file/folder name (WrapperStr).
        //   * ParsePack           — an `overrides` entry with a blank modId OR pawn is SILENTLY DROPPED.
        //   * ResolvePacks        — duplicate modId => the pack is SKIPPED; dependsOn naming an absent pack => SKIPPED;
        //                           a cycle over dependsOn+loadAfter => loud fallback to file order.
        //   * CheckSchema         — a version above/below what this build implements is advisory, never a gate.
        //   * the override apply  — an override only replaces a pawn ALREADY CLAIMED, so the overriding pack must load
        //                           AFTER the pack it overrides, or the replacement silently becomes a CONFLICT.
        //
        // DELIBERATELY SELF-CONTAINED: every rule here needs only THIS pack's own wrapper. The genuinely cross-pack
        // questions — does `dependsOn` resolve, does the override's target pawn exist — need the whole pack set, which
        // an author validating one pack does not have, and which the runtime's resolution report already answers with
        // better information (it knows what actually loaded). Adding them here would mean widening IValidationContext
        // for three implementers to produce a worse version of a report that already exists.
        public struct PackOverrideRef
        {
            public string ModId;   // the pack whose entry is being replaced
            public string Pawn;    // the pawnDescription being taken over
        }

        // INPUT DTO, not a serialization schema: the editor's RegistryFile and the plugin's Pack remain the real
        // shapes, and the cross-repo parity guard still keys on those. This is only what the rules need.
        public sealed class PackWrapper
        {
            public string ModId;
            public int SchemaVersion;                       // 0 / absent = legacy unversioned, which is legal
            public IList<string> DependsOn;
            public IList<string> LoadAfter;
            public IList<PackOverrideRef> Overrides;
        }

        public static List<ValidationIssue> ValidatePack(PackWrapper w)
        {
            var issues = new List<ValidationIssue>();
            if (w == null) return issues;
            void Add(ValidationSeverity s, string field, string msg) => issues.Add(new ValidationIssue { Severity = s, Field = field, Message = msg });
            void Warn(string field, string msg) => Add(ValidationSeverity.Warning, field, msg);

            // ---- modId: the pack's IDENTITY, and what every other pack's dependsOn/overrides is written against ----
            var id = (w.ModId ?? "").Trim();
            if (id.Length == 0)
                Warn("modId", "empty — the pack still loads under an id derived from its file or folder name, but that derived name is the identity other packs must write in their `dependsOn` and `overrides`, and it changes if the file is ever renamed. Set it explicitly.");
            else if (id != w.ModId)
                Warn("modId", $"'{w.ModId}' has leading or trailing whitespace — it is trimmed on load, so a dependsOn written with the untrimmed spelling would not match. Remove the spaces.");
            else if (id.StartsWith("unnamed-pack:", StringComparison.OrdinalIgnoreCase))
                Warn("modId", "starts with 'unnamed-pack:' — that prefix is the runtime's LAST-RESORT placeholder for a pack it could not identify, not a name to author. Choose a real id.");

            // ---- schemaVersion: advisory, never a gate (additive evolution — docs/Multi-Mod.md) ----
            if (w.SchemaVersion < 0)
                Warn("schemaVersion", $"{w.SchemaVersion} is negative — meaningless; use {HafSchema.Version} (this build's schema) or omit the key for a legacy pack.");
            else if (w.SchemaVersion > HafSchema.Version)
                Warn("schemaVersion", $"{w.SchemaVersion} is newer than the schema this build implements ({HafSchema.Version}). The pack still loads and every key this build knows still works, but any key introduced after schema {HafSchema.Version} is stripped and silently ignored.");
            else if (w.SchemaVersion > 0 && w.SchemaVersion < HafSchema.MinReadable)
                Warn("schemaVersion", $"{w.SchemaVersion} is older than the oldest schema this build can read ({HafSchema.MinReadable}). It still loads, but a field whose meaning changed since then may be misread — re-bake the pack.");

            // ---- dependsOn / loadAfter ----
            CheckIdList(issues, "dependsOn", w.DependsOn, id,
                        selfMsg: "names this pack's own modId — a pack cannot depend on itself; at load this is an unsatisfiable dependency and the pack is SKIPPED entirely.",
                        selfSeverity: ValidationSeverity.Error);
            CheckIdList(issues, "loadAfter", w.LoadAfter, id,
                        selfMsg: "names this pack's own modId — an ordering constraint against itself does nothing. Remove it.",
                        selfSeverity: ValidationSeverity.Warning);

            // ---- overrides: an explicit, declared replacement of ANOTHER pack's entry ----
            if (w.Overrides != null)
            {
                var seenOv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < w.Overrides.Count; i++)
                {
                    var ov = w.Overrides[i];
                    var om = (ov.ModId ?? "").Trim();
                    var op = (ov.Pawn ?? "").Trim();

                    // Verified in ParsePack: `if (om.Length > 0 && op.Length > 0)` — anything else never reaches the
                    // pack at all, so the author's intent vanishes with no runtime message naming the entry.
                    if (om.Length == 0 || op.Length == 0)
                    {
                        Warn("overrides", $"entry {i + 1} has an empty {(om.Length == 0 ? "modId" : "pawn")} — it is DROPPED silently when the pack is parsed, so the override simply never happens.");
                        continue;
                    }

                    if (id.Length > 0 && string.Equals(om, id, StringComparison.OrdinalIgnoreCase))
                    {
                        Warn("overrides", $"entry {i + 1} overrides this pack's own modId ('{om}') — a pack does not override itself. If two of your own entries target pawn '{op}', the second is dropped as a duplicate instead.");
                        continue;
                    }

                    if (!seenOv.Add(om + " " + op))
                        Warn("overrides", $"entry {i + 1} repeats '{om}' : '{op}' — harmless, but a duplicated line usually means one of them was meant to name a different pawn.");

                    // THE ONE WORTH HAVING. An override replaces a pawn ALREADY CLAIMED by an earlier-loaded pack, so
                    // this pack has to load AFTER its target. Without a declared constraint the order is whatever the
                    // game's module order happens to be — and if this pack lands first, the target's entry arrives
                    // later, finds the pawn already owned by US, has no matching override of its own, and is dropped
                    // as an undeclared CONFLICT. The author's override then silently did the opposite of its intent.
                    if (!Names(w.DependsOn, om) && !Names(w.LoadAfter, om))
                        Warn("overrides", $"entry {i + 1} overrides '{om}' but neither `dependsOn` nor `loadAfter` names it, so nothing guarantees this pack loads after '{om}'. If it loads first the override does not apply and the other pack's entry is dropped as a conflict instead. Add '{om}' to `loadAfter` (or `dependsOn` if you also require it to be installed).");
                }
            }

            return issues;
        }

        static bool Names(IList<string> list, string id)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals((list[i] ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void CheckIdList(List<ValidationIssue> issues, string field, IList<string> list, string ownId,
                                string selfMsg, ValidationSeverity selfSeverity)
        {
            if (list == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++)
            {
                var raw = list[i];
                var s = (raw ?? "").Trim();
                if (s.Length == 0)
                {
                    issues.Add(new ValidationIssue { Severity = ValidationSeverity.Warning, Field = field, Message = $"entry {i + 1} is empty — it can never match a pack id, so the line does nothing. Remove it." });
                    continue;
                }
                if (ownId.Length > 0 && string.Equals(s, ownId, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new ValidationIssue { Severity = selfSeverity, Field = field, Message = selfMsg });
                else if (!seen.Add(s))
                    issues.Add(new ValidationIssue { Severity = ValidationSeverity.Warning, Field = field, Message = $"'{s}' is listed more than once — harmless, but a duplicate usually means one line was meant to name a different pack." });
            }
        }
    }
}
