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
python "$ROOT/Tests/test_compare_fbx_scene.py" || { echo "FAIL — FBX scene comparison regression tests"; exit 1; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/drill.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" \
  "$WROOT/tools/fbx-drill/FbxDrill.cs" "$WROOT/tools/fbx-drill/FbxSceneDump.cs" "$WROOT/editor/FbxDocument.cs" "$WROOT/editor/FbxScene.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/drill.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the FBX drill did not compile (csc rc=$rc)"; exit 1; fi
# THE OBJECT LAYER RUNS ON UNITY'S 64-BIT MONO (2026-11-01): the stand-alone mono.exe is a 32-bit process whose double
# arithmetic goes through the x87 unit with its extended intermediates - the matrices came out an ulp from ufbx's on the
# first run, trig or no trig. tools/deploy-drill/MonoHost.cs enters the editor's own 64-bit runtime (SSE2 doubles, the
# C runtime's cos/sin reachable), the runtime the reader will actually run on. The record-tree step stays on the
# stand-alone Mono: bits are bits there.
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -platform:x64 -out:"$WTMP/mono-host.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" "$WROOT/tools/deploy-drill/MonoHost.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/mono-host.exe" ]; then echo "$OUT" | grep -E "error" | head -10; echo "FAIL — the 64-bit Mono bootstrap did not compile (csc rc=$rc)"; exit 1; fi

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

# ---- STEP 2, THE OBJECT LAYER: editor/FbxScene.cs against ufbx, the reference reader ---------------------------------
# tools/fbx-drill/ufbx/ufbxdump.c (ufbx 0.23.1, vendored with its licence) prints what ufbx makes of a file: every
# node's local transform and matrices as bits, every mesh's faces and vertex streams as hashes over the bits of their
# values, every material. The C# dump (FbxDrill.exe --scene) must be the same text. Built with the native kernels'
# compiler (WinLibs GCC, the ucrt flavour: its cos/sin are the ones the C# P/Invokes); -ffp-contract=off keeps the
# oracle's arithmetic the IEEE one the reader reproduces.
command -v gcc >/dev/null 2>&1 || { echo "FAIL — gcc not on PATH (WinLibs MinGW-W64 x86_64-ucrt, the native kernels' compiler): the FBX object layer was NOT drilled"; exit 2; }
# -DUFBX_EXTERNAL_MATH: ufbxdump.c resolves sin, cos and the rest from ucrtbase.dll, the library the C# side P/Invokes
# (MinGW's own sin/cos differ from it by an ulp on some angles: the first drill run's only difference, 2026-11-01).
OUT=$(cd "$ROOT/tools/fbx-drill/ufbx" && gcc -municode -O2 -ffp-contract=off -fno-fast-math -fexcess-precision=standard -DUFBX_EXTERNAL_MATH -o "$TMPD/ufbxdump.exe" ufbxdump.c ufbx.c 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/ufbxdump.exe" ]; then echo "$OUT" | grep -E "error" | head -10; echo "FAIL — ufbxdump (the object-layer oracle) did not compile (gcc rc=$rc)"; exit 1; fi
# Scene fixtures exercise property defaults, numeric IDs, layer ordering and tangent associations which the real
# files do not cover. Record-tree-only fixtures remain separate from these semantic scenes.
python "$ROOT/tools/fbx-drill/fbx_scene_fixtures.py" "$WTMP/scenes" > "$TMPD/scenes.txt" || { echo "FAIL — scene fixture generation failed"; exit 1; }
mapfile -t SCENE_FIXTURES < <(tr -d '\r' < "$TMPD/scenes.txt")
[ "${#SCENE_FIXTURES[@]}" -gt 0 ] || { echo "FAIL — no scene fixtures generated"; exit 1; }
SCENE_FILES=("${PROJECT_FILES[@]}" "${RECIPE_FBX[@]}" "${SCENE_FIXTURES[@]}")
if [ "${#SCENE_FILES[@]}" -gt 0 ]; then
  t3=$(date +%s)
  # MonoPosixHelper.dll (Mono's zlib, behind DeflateStream) beside the HOST exe: the hosted runtime resolves it by the
  # process's own directory, and outside the editor nothing puts the runtime's folder on that path (the first hosted run
  # refused every file with "MonoPosixHelper"; a PATH prefix did not reach it, a copy beside mono-host.exe does)
  cp "$UNITY/MonoBleedingEdge/EmbedRuntime/MonoPosixHelper.dll" "$TMPD/" || { echo "FAIL — MonoPosixHelper.dll not found under $UNITY/MonoBleedingEdge/EmbedRuntime"; exit 2; }
  "$WTMP/mono-host.exe" "$UNITY/MonoBleedingEdge" "$WTMP/drill.exe" --scene "${SCENE_FILES[@]}" > "$TMPD/scene-cs.txt" 2> "$TMPD/scene-cs.err"; src=$?
  t4=$(date +%s)
  [ "$src" -eq 0 ] || { echo "FAIL — the hosted C# scene reader exited $src:"; tail -8 "$TMPD/scene-cs.txt"; tail -5 "$TMPD/scene-cs.err"; exit 1; }
  "$TMPD/ufbxdump.exe" "${SCENE_FILES[@]}" > "$TMPD/scene-u.txt" 2> "$TMPD/scene-u.err"; urc=$?
  t5=$(date +%s)
  [ "$urc" -eq 0 ] || { echo "FAIL — ufbxdump exited $urc:"; tail -5 "$TMPD/scene-u.err"; exit 1; }
  tr -d '\r' < "$TMPD/scene-cs.txt" | sed '1s/^\xEF\xBB\xBF//' > "$TMPD/scene-cs.lf"; tr -d '\r' < "$TMPD/scene-u.txt" > "$TMPD/scene-u.lf"
  python "$ROOT/tools/fbx-drill/compare_scene.py" "$TMPD/scene-cs.lf" "$TMPD/scene-u.lf" "${#SCENE_FILES[@]}" "$src" "$urc" || exit 1
  n_nodes=$(grep -c '^node ' "$TMPD/scene-cs.lf"); n_meshes=$(grep -c '^mesh ' "$TMPD/scene-cs.lf"); n_mats=$(grep -c '^material ' "$TMPD/scene-cs.lf")
  n_streams=$(grep -cE '^  (position|normal|uv[0-9]+|color[0-9]+|tangent|bitangent) values=' "$TMPD/scene-cs.lf")
  RESULT2="PASS — FBX object layer: ${#SCENE_FILES[@]} files, $n_nodes nodes (every local transform, node-to-parent and node-to-world matrix as bits), $n_meshes meshes ($n_streams vertex streams and every face as hashes), $n_mats materials - equal to ufbx 0.23.1's own reading (C# $((t4 - t3)) s, ufbx $((t5 - t4)) s)"
  echo "$RESULT2"
  RESULT="$RESULT | $RESULT2"
  python "$ROOT/tools/fbx-drill/fbx_scene_fixtures.py" --bad "$WTMP/bad-scenes" > "$TMPD/bad-scenes.txt" || { echo "FAIL — bad scene fixture generation failed"; exit 1; }
  mapfile -t BAD_SCENES < <(tr -d '\r' < "$TMPD/bad-scenes.txt")
  [ "${#BAD_SCENES[@]}" -gt 0 ] || { echo "FAIL — no malformed scenes"; exit 1; }
  "$WTMP/mono-host.exe" "$UNITY/MonoBleedingEdge" "$WTMP/drill.exe" --reject-scene "${BAD_SCENES[@]}" > "$TMPD/reject-scene-cs.txt" 2>&1 || { echo "FAIL — C# scene refusals:"; cat "$TMPD/reject-scene-cs.txt"; exit 1; }
  "$TMPD/ufbxdump.exe" --reject "${BAD_SCENES[@]}" > "$TMPD/reject-scene-u.txt" 2>&1 || { echo "FAIL — ufbx scene refusals:"; cat "$TMPD/reject-scene-u.txt"; exit 1; }
  echo "PASS — both scene readers refused ${#BAD_SCENES[@]} cyclic hierarchies"
fi
[ "$N_PROJECT" -gt 0 ] || { echo "SKIP — ${RESULT#PASS — } | the project population was NOT drilled: no modding project at $PROJECT (set HAF_UNITY_PROJECT)"; exit 3; }
exit 0
