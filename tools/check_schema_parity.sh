#!/usr/bin/env bash
# Schema parity guard for the Model Factory registry.
#
# The registry (enc_models.json) is WRITTEN by the editor baker from ModelDef (ModelRegistry.cs, JsonUtility) and READ by
# the runtime plugin (UniversalInjectPatch.cs) two ways: the PRIMARY Newtonsoft parse and a REGEX fallback. The Newtonsoft
# path now deserializes GENERICALLY (`m.ToObject<ModelEntry>()`), so every name-matching field is mapped automatically —
# there's no Newtonsoft hand-list left to drift. What still hand-syncs across the two repos: the GUID arrays (one JSON
# array -> four ints, hand-extracted in BOTH paths) and the entire REGEX fallback. So the drift is now one-directional —
# a field the fallback FORGOT — plus the GUID hand-lists. This guard makes that drift LOUD. It checks:
#
#   1. G ⊆ R   — every GUID/position key the Newtonsoft path hand-extracts is also parsed by the regex fallback.
#   2. SH ⊆ R  — every shared HafModelSchema field (auto-read by ToObject) is covered by the regex fallback, so it can't lag.
#   3. R ⊆ W   — every regex-fallback key is a ModelDef/HafModelSchema field the baker writes (minus a runtime-only allowlist).
#   INFO       — ModelDef fields the plugin never reads (expected for bake-time-only knobs; eyeball for a forgotten one).
#
# Type parity is no longer checked here: the shared fields live in ONE class (HafModelSchema) that BOTH ModelDef and
# ModelEntry inherit, so the write type and the read type are the SAME declaration — compiler-enforced, can't diverge.
#
# Source-text comparison, no build coupling. Run before committing a registry-schema change:
#   Tools/check_schema_parity.sh [ENCReload_root] [HumankindAssetFramework_root]
set -u
# BOTH halves now live in THIS repo (the tools moved to editor/ on 2026-08-24), so this stopped being a
# cross-repo guard: no ENCReload checkout, no [SKIP] branch, nothing to be one-directional about.
PROOF="${1:-$(cd "$(dirname "$0")/.." && pwd)}"
DEF="$PROOF/editor/ModelRegistry.cs"
PLUG="$PROOF/Patches/UniversalInjectPatch.cs"
[ -f "$DEF" ]  || { echo "MISSING: $DEF"; exit 2; }
[ -f "$PLUG" ] || { echo "MISSING: $PLUG"; exit 2; }

# Runtime-ONLY keys: intentional overrides the baker deliberately doesn't write (the user hand-edits them into the JSON).
# `scale` fixes a mis-scaled animated model without a re-bake. Add here ONLY for a conscious runtime-only override.
# rotorSpinBones/rotorSpinSpeed: advanced rotor-reclaim (Pose.cs) authored directly in pack.json; not (yet) an editor field.
allow=" scale rotorSpinBones rotorSpinSpeed "

# Map a C# type to a one-letter JSON-shape code (how JsonUtility serializes it): S string, I int, F float, B bool,
# V object (Vector3), A array (int[]). Enums serialize as int.
canon() {
  case "$1" in
    string) echo S;; int) echo I;; float) echo F;; bool) echo B;;
    Vector3) echo V;; "int[]") echo A;; MaterialMode) echo I;;
    *) echo "?$1";;
  esac
}

# --- W: ModelDef serialized fields + types (the WRITE schema) ---
declare -A W
SCHEMA="$PROOF/Haf.Schema/HafModelSchema.cs"
defbody=$(awk '/public class ModelDef/{f=1} /class (OverrideRef|RegistryFile)/{f=0} f' "$DEF")
# The ~64 shared fields moved to Haf.Schema.HafModelSchema, inherited by BOTH ModelDef and ModelEntry — so parity for
# those is now COMPILER-enforced. Union the shared class's fields into the WRITE schema so the check still sees the full set.
[ -f "$SCHEMA" ] && defbody="$defbody
$(awk '/public class HafModelSchema/{f=1} f&&/^    }/{f=0} f' "$SCHEMA")"
while read -r ty nm; do
  [ -n "${nm:-}" ] && W["$nm"]=$(canon "$ty")
done < <(grep -oE 'public[[:space:]]+[A-Za-z0-9_]+(\[\])?[[:space:]]+[A-Za-z_][A-Za-z0-9_]*[[:space:]]*[=;]' <<<"$defbody" \
         | sed -E 's/public[[:space:]]+([A-Za-z0-9_]+(\[\])?)[[:space:]]+([A-Za-z_][A-Za-z0-9_]*).*/\1 \3/')

# --- SH: the shared HafModelSchema fields. The Newtonsoft path deserializes GENERICALLY (m.ToObject<ModelEntry>), so each
#     of these is auto-read by name — and W ∩ ModelEntry == exactly this set (ModelDef-only fields aren't ModelEntry fields,
#     so ToObject ignores them). This is precisely what the regex fallback must also cover, or the fallback silently drops it.
SH=$(awk '/public class HafModelSchema/{f=1} f&&/^    }/{f=0} f' "$SCHEMA" \
     | grep -oE 'public[[:space:]]+[A-Za-z0-9_]+(\[\])?[[:space:]]+[A-Za-z_][A-Za-z0-9_]*[[:space:]]*[=;]' \
     | sed -E 's/.*[[:space:]]([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*[=;]/\1/' | sort -u)

# --- G: GUID-array + position keys STILL hand-extracted in the Newtonsoft path (the one m["..."] block that remains).
#     Each maps one JSON array -> four ints (skel[] -> sa/sb/sc/sd), so it can't deserialize by name; both paths hand-list it.
G=$(grep -oE 'm\["[A-Za-z_][A-Za-z0-9_]*"\]' "$PLUG" | sed -E 's/m\["(.*)"\]/\1/' | sort -u)

# --- R: keys the regex fallback reads (first "key" of each Regex.Matches(text, ...)) ---
R=$(grep -oE 'Regex\.Matches\(text, "\\"[A-Za-z_][A-Za-z0-9_]*' "$PLUG" \
    | grep -oE '[A-Za-z_][A-Za-z0-9_]*$' | sort -u)

fail=0

# 1) G ⊆ R — every GUID/position key the Newtonsoft path hand-extracts must also be parsed by the regex fallback.
gMissingR=$(comm -23 <(echo "$G") <(echo "$R"))
if [ -n "$gMissingR" ]; then
  fail=1
  echo "FAIL — GUID/position key(s) read by the Newtonsoft path but NOT by the regex fallback: $(tr '\n' ' ' <<<"$gMissingR")"
  echo "  -> add the matching Regex.Matches(...) to the fallback in ParseModels."
fi

# 2) SH ⊆ R — every shared field (auto-read by ToObject) must be covered by the regex fallback, so the fallback can't lag.
shMissingR=$(comm -23 <(echo "$SH") <(echo "$R"))
if [ -n "$shMissingR" ]; then
  fail=1
  echo "FAIL — shared HafModelSchema field(s) the Newtonsoft path auto-reads but the regex fallback MISSES: $(tr '\n' ' ' <<<"$shMissingR")"
  echo "  -> add the matching Regex.Matches(...) to the fallback in ParseModels (Newtonsoft covers it automatically)."
fi

# 3) R ⊆ W (+allowlist) — every regex key must be a field the baker writes (catches a typo'd or removed key).
missing=""
for k in $R; do
  case "$allow" in *" $k "*) continue;; esac
  [ -z "${W[$k]+x}" ] && missing="$missing $k"
done
if [ -n "$missing" ]; then
  fail=1
  echo "FAIL — regex fallback reads key(s) the baker never writes:$missing"
  echo "  -> add the field to ModelDef/HafModelSchema (ModelRegistry.cs), fix the key name, or allowlist a runtime-only override."
fi

# 4) WRAPPER parity (HAF multi-mod): the plugin's top-level root["..."] reads must all be RegistryFile fields the baker
#    writes. These are per-FILE keys (modId/schemaVersion/dependsOn/loadAfter/overrides), a separate surface from the
#    per-model keys above — so they drift independently and need their own guard.
rfbody=$(awk '/class RegistryFile/{f=1} f&&/^}/{f=0} f' "$DEF")
WR=$(grep -oE 'public[[:space:]]+[A-Za-z0-9_<>]+[[:space:]]+[A-Za-z_][A-Za-z0-9_]*[[:space:]]*[=;]' <<<"$rfbody" \
     | sed -E 's/.*[[:space:]]([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*[=;]/\1/' | sort -u)
NR=$(grep -oE 'root\["[A-Za-z_][A-Za-z0-9_]*"\]' "$PLUG" | sed -E 's/root\["(.*)"\]/\1/' | sort -u)
# `districts` is a FALSE MATCH of the root["..."] grep: that read parses enc_districts.json (its own file, written by
# DistrictRegistry.cs with its own schema), not the model registry this wrapper check guards.
wrapallow=" districts "
wrapmiss=""
for k in $NR; do
  case "$wrapallow" in *" $k "*) continue;; esac
  case " $(tr '\n' ' ' <<<"$WR") " in *" $k "*) ;; *) wrapmiss="$wrapmiss $k";; esac
done
if [ -n "$wrapmiss" ]; then
  fail=1
  echo "FAIL — plugin reads wrapper key(s) the baker never writes:$wrapmiss"
  echo "  -> add the field to RegistryFile (ModelRegistry.cs) or fix the key name in ParsePack (UniversalInjectPatch.cs)."
fi

# INFO: ModelDef fields never read at runtime (expected for bake-time-only knobs; scan for a genuinely-forgotten one).
# The consumed surface is the regex set R (which, per checks 1-2, is a superset of the GUID hand-list G and the shared SH).
unread=""
for k in "${!W[@]}"; do
  case " $(tr '\n' ' ' <<<"$R") " in *" $k "*) ;; *) unread="$unread $k";; esac
done

echo "Shared fields (HafModelSchema): $(wc -w <<<"$SH") | GUID/pos hand-list (Newtonsoft): $(wc -w <<<"$G")"
echo "Plugin reads (regex fallback) : $(wc -w <<<"$R") keys"
echo "Baker writes (ModelDef+shared): ${#W[@]} fields"
echo "Wrapper reads (root)          : $(wc -w <<<"$NR") keys | RegistryFile writes: $(wc -w <<<"$WR") fields"
[ -n "$unread" ] && echo "INFO — baker fields not read at runtime (bake-time-only, expected):$(echo "$unread" | tr ' ' '\n' | sort | tr '\n' ' ')"

if [ "$fail" -ne 0 ]; then exit 1; fi
echo "PASS — GUID hand-lists agree, every shared field is covered by the regex fallback, and all read keys are written by the baker."
