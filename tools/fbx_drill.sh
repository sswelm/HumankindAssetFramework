#!/usr/bin/env bash
# fbx_drill.sh — the binary FBX node reader (editor/FbxDocument.cs) against an independent reader, record for record.
#
# STEP 1 OF THE NATIVE FBX READER (2026-10-31). The format layer has no Blender in it and needs none: the claim is that
# two readers written apart - the C# one the editor will build on, and tools/fbx-drill/fbx_nodes.py - print the same
# dump (every record's depth, name and property type codes; scalars as bits; strings and arrays as lengths and SHA-1s)
# for every binary FBX this machine has: the modding project's own (the rig route's exported clips, the Lab's probe and
# preview files - Blender's exporter, version 7400), the Lab recipes' FBX sources when they exist (the FBX SDK's
# 7500/7700, 64-bit records), and the fixtures tools/fbx-drill/fbx_fixtures.py writes for the shapes none of those
# have (every property type, stored and compressed arrays, empty arrays and names, deep nesting). A file the C# reader
# refuses is a FAIL with its message; a dump that differs is a FAIL with the first differing lines.
#
# Prerequisites as glb_reader_drill.sh (Unity's csc and Mono; the modding project from HAF_UNITY_PROJECT, default
# C:/Repo/ENCReload - without it the fixtures are drilled alone and the step SKIPs on the project population).
#   bash tools/fbx_drill.sh
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; MONO="$UNITY/MonoBleedingEdge/bin/mono.exe"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$MONO" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; FBX reader NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (FBX reader NOT drilled)"; exit 2; }
command -v python >/dev/null 2>&1 || { echo "FAIL — python not on PATH (FBX reader NOT drilled)"; exit 2; }
python "$ROOT/tools/fbx-drill/test_fbx_inputs.py" || { echo "FAIL — FBX input coverage regression tests"; exit 1; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/drill.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" \
  "$WROOT/tools/fbx-drill/FbxDrill.cs" "$WROOT/editor/FbxDocument.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/drill.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the FBX drill did not compile (csc rc=$rc)"; exit 1; fi

# ---- the population ------------------------------------------------------------------------------------------------
FILES=()
PROJECT_NOTE="NO modding project at $PROJECT (set HAF_UNITY_PROJECT)"
python "$ROOT/tools/fbx-drill/fbx_inputs.py" "$PROJECT" > "$TMPD/inputs.txt" || { echo "FAIL — could not enumerate FBX populations"; exit 1; }
mapfile -t PROJECT_FILES < <(sed -n 's/^PROJECT\t//p' "$TMPD/inputs.txt" | tr -d '\r')
mapfile -t RECIPE_FBX < <(sed -n 's/^RECIPE\t//p' "$TMPD/inputs.txt" | tr -d '\r')
sed -n 's/^NOTE\t/NOTE — /p' "$TMPD/inputs.txt"
N_PROJECT=${#PROJECT_FILES[@]}
FILES+=("${PROJECT_FILES[@]}" "${RECIPE_FBX[@]}")
[ "$N_PROJECT" -eq 0 ] || PROJECT_NOTE="$N_PROJECT project FBX files and ${#RECIPE_FBX[@]} recipe FBX sources"
FIXDIR="$TMPD/fixtures"
python "$ROOT/tools/fbx-drill/fbx_fixtures.py" "$(cygpath -m "$FIXDIR")" > "$TMPD/fixtures.txt" || { echo "FAIL — fixture generation failed"; exit 1; }
mapfile -t FIXTURES < <(tr -d '\r' < "$TMPD/fixtures.txt")
[ "${#FIXTURES[@]}" -gt 0 ] || { echo "FAIL — could not write the fixtures (tools/fbx-drill/fbx_fixtures.py)"; exit 1; }
FILES+=("${FIXTURES[@]}")
[ "${#FILES[@]}" -gt 0 ] || { echo "FAIL — nothing to drill"; exit 1; }

# Agreement on good files alone cannot prove refusal. Both readers reject this malformed corpus; the C# one runs
# on Unity's Mono here, while the unit tests also exercise .NET Framework (their inflater EOF behaviour differs).
python "$ROOT/tools/fbx-drill/fbx_fixtures.py" --bad "$WTMP/bad" > "$TMPD/bad.txt" || { echo "FAIL — malformed fixture generation failed"; exit 1; }
mapfile -t BAD < <(tr -d '\r' < "$TMPD/bad.txt")
[ "${#BAD[@]}" -gt 0 ] || { echo "FAIL — no malformed fixtures"; exit 1; }
"$MONO" "$TMPD/drill.exe" --reject "${BAD[@]}" > "$TMPD/reject-cs.txt" 2>&1 || { echo "FAIL — C# malformed-input refusals:"; cat "$TMPD/reject-cs.txt"; exit 1; }
python -I "$ROOT/tools/fbx-drill/fbx_nodes.py" --reject "${BAD[@]}" > "$TMPD/reject-py.txt" 2>&1 || { echo "FAIL — oracle malformed-input refusals:"; cat "$TMPD/reject-py.txt"; exit 1; }
echo "PASS — both readers refused ${#BAD[@]} malformed FBX fixtures"

# ---- both readers, every file, compared ----------------------------------------------------------------------------
t0=$(date +%s)
"$MONO" "$TMPD/drill.exe" "${FILES[@]}" > "$TMPD/cs.txt" 2> "$TMPD/cs.err"; crc=$?
t1=$(date +%s)
if [ "$crc" -ne 0 ]; then echo "FAIL — the C# reader exited $crc:"; tail -8 "$TMPD/cs.txt"; tail -5 "$TMPD/cs.err"; exit 1; fi
python -I "$ROOT/tools/fbx-drill/fbx_nodes.py" "${FILES[@]}" > "$TMPD/py.txt" 2> "$TMPD/py.err"; prc=$?
t2=$(date +%s)
if [ "$prc" -ne 0 ]; then echo "FAIL — the Python reader (the oracle) failed:"; tail -5 "$TMPD/py.err"; exit 1; fi
# CR LF as LF, and a UTF-8 byte-order mark dropped: Mono's stdout put one at the head of the dump on one run of three
# (2026-10-31; the exe writes raw UTF-8 bytes without one). A transport artefact, not a reader difference.
tr -d '\r' < "$TMPD/cs.txt" | sed '1s/^\xEF\xBB\xBF//' > "$TMPD/cs.lf"; tr -d '\r' < "$TMPD/py.txt" | sed '1s/^\xEF\xBB\xBF//' > "$TMPD/py.lf"
if grep -q '^ERROR ' "$TMPD/cs.lf"; then
  echo "FAIL — the C# reader refused a file:"; grep -B1 '^ERROR ' "$TMPD/cs.lf" | head -12; exit 1
fi
if ! cmp -s "$TMPD/cs.lf" "$TMPD/py.lf"; then
  echo "FAIL — the two readers' dumps differ (C# then Python, the first lines that do):"
  diff "$TMPD/cs.lf" "$TMPD/py.lf" | head -20
  exit 1
fi
n_files=$(grep -c '^=== ' "$TMPD/cs.lf"); n_records=$(awk '/^records /{ s += $2 } END { print s + 0 }' "$TMPD/cs.lf")
[ "$n_files" -eq "${#FILES[@]}" ] || { echo "FAIL — the readers dumped $n_files of ${#FILES[@]} selected files"; exit 1; }
versions=$(grep '^version ' "$TMPD/cs.lf" | sort | uniq -c | awk '{ printf "%s x%d ", $3, $1 }')
# the cost of the format layer alone, per file, for the plan
"$MONO" "$TMPD/drill.exe" --time "${FILES[@]}" > "$TMPD/time.txt" 2>/dev/null
trc=$?
[ "$trc" -eq 0 ] || { echo "FAIL — the C# timing run exited $trc:"; tail -5 "$TMPD/time.txt"; exit 1; }
tr -d '\r' < "$TMPD/time.txt" | sed '1s/^\xEF\xBB\xBF//' > "$TMPD/time.lf"
[ "$(grep -c '^TIME ' "$TMPD/time.lf")" -eq "$n_files" ] || { echo "FAIL — the timing run did not report every file"; exit 1; }
slowest=$(sort -k2,2nr "$TMPD/time.lf" | head -1 | awk '{ printf "%s ms for %s records (%s)", $2, $3, $4 }')
RESULT="PASS — FBX reader drill: $n_files binary FBX files, $n_records records, read by editor/FbxDocument.cs and by the independent Python reader with identical dumps (every record, property and array as bits and hashes) - $PROJECT_NOTE, ${#FIXTURES[@]} fixtures; versions $versions(C# $((t1 - t0)) s, Python $((t2 - t1)) s; slowest $slowest)"
echo "$RESULT"
[ "$N_PROJECT" -gt 0 ] || { echo "SKIP — ${RESULT#PASS — } | the project population was NOT drilled: no modding project at $PROJECT (set HAF_UNITY_PROJECT)"; exit 3; }
exit 0
