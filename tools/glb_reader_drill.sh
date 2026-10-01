#!/usr/bin/env bash
# glb_reader_drill.sh - the GLB reader against the real registry, and against Blender (2026-09-30, step 1 of
# replacing Blender: docs/Review-Backlog.md "Replace Blender with our own code").
#
# 1. The real editor/GlbReader.cs (compiled with Unity's Roslyn, run on Unity's Mono) reads every .glb the registry
#    of the modding project names; a file that does not read, or whose skinned vertices do not weigh to 1 over the
#    skin's joints, fails the drill. The total read time is printed: the number PR #108's plan rests on.
# 2. Blender imports the same files (one process; SKIP with a note when Blender is absent) and the counts a file
#    cannot disagree about are compared per file: triangles, materials, images, bones = skin joints. Animation
#    counts are printed from both sides but not compared (Blender makes one action per animated object).
#    The comparison covers a SAMPLE (the largest, the smallest, and every animated file) unless FULL=1: Blender's
#    import of 784 MB is minutes, the gate is not.
# Prerequisites as registry_engine_drill.sh; the modding project from HAF_UNITY_PROJECT (default C:/Repo/ENCReload),
# SKIP when it is not there (hosted CI).
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; MONO="$UNITY/MonoBleedingEdge/bin/mono.exe"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$MONO" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; GLB reader NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (GLB reader NOT drilled)"; exit 2; }
PACK=$(ls "$PROJECT"/Assets/Pack/*/pack.json 2>/dev/null | head -1)
[ -n "$PACK" ] || { echo "SKIP — no modding project at $PROJECT (set HAF_UNITY_PROJECT); the GLB reader was NOT drilled against the registry"; exit 0; }
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; GLB reader NOT drilled)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/drill.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/glb-reader-drill/Drill.cs" "$WROOT/editor/HafModel.cs" "$WROOT/editor/GlbReader.cs" "$WROOT/editor/HafTransforms.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/drill.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the GLB reader drill did not compile (csc rc=$rc)"; exit 1; fi

# the registry's .glb model files (the recipes' sources), as the editor resolves them
mapfile -t FILES < <(python "$ROOT/tools/glb-reader-drill/registry_files.py" "$PACK" | tr -d '\r')   # python on Windows ends lines with CR LF; Mono refuses a path with a CR
[ "${#FILES[@]}" -gt 0 ] || { echo "SKIP — the registry names no .glb that exists on disk; the GLB reader was NOT drilled"; exit 0; }
# plus the shapes the registry does not have: the fixture library (fixtures.py, one small file per shape) and the
# Khronos sample assets when they are fetched (fetch_samples.py; other exporters' output). A sample line may carry an
# expectation - a stage that must refuse the file by name - checked below instead of round-tripped.
FIXDIR="$WTMP/fixtures"
mapfile -t FIXTURES < <(python "$ROOT/tools/glb-reader-drill/fixtures.py" "$FIXDIR" | tr -d '\r')
[ "${#FIXTURES[@]}" -gt 0 ] || { echo "FAIL — could not write the fixtures (tools/glb-reader-drill/fixtures.py)"; exit 1; }
FILES+=("${FIXTURES[@]}")
SAMPLES_OK=(); SAMPLES_EXPECT=()
mapfile -t SLINES < <(python "$ROOT/tools/glb-reader-drill/fetch_samples.py" "$ROOT/References/gltf-samples" --list 2>/dev/null | tr -d '\r')
for l in "${SLINES[@]}"; do IFS=$'\t' read -r sp st sx <<< "$l"; if [ "$st" = "ok" ]; then SAMPLES_OK+=("$sp"); else SAMPLES_EXPECT+=("$l"); fi; done
[ "${#SAMPLES_OK[@]}" -eq 0 ] || FILES+=("${SAMPLES_OK[@]}")
SAMPLES_NOTE="${#SAMPLES_OK[@]} Khronos samples"
[ "${#SLINES[@]}" -gt 0 ] || SAMPLES_NOTE="no Khronos samples (fetch them once: python tools/glb-reader-drill/fetch_samples.py References/gltf-samples)"

RESULT=$("$MONO" "$TMPD/drill.exe" "${FILES[@]}" 2>&1); rc=$?
RESULT=$(printf '%s' "$RESULT" | LC_ALL=C sed 's/\xEF\xBB\xBF//g')
echo "$RESULT" | grep -E "^FAIL"
n_ok=$(echo "$RESULT" | grep -c "^FILE")
total=$(echo "$RESULT" | grep "^TOTAL")
if [ "$rc" -ne 0 ]; then echo "FAIL — GLB reader drill: a registry file did not read ($n_ok of ${#FILES[@]} read)"; exit 1; fi
echo "C# reader: $n_ok files read and checked - the registry, ${#FIXTURES[@]} fixtures, $SAMPLES_NOTE ($total)"
# the samples a stage must REFUSE, by name: the contract's "no" cases drilled like the "yes" cases
for l in "${SAMPLES_EXPECT[@]}"; do
  IFS=$'\t' read -r sp st sx <<< "$l"; [ "$st" = "reader" ] || continue
  o=$("$MONO" "$TMPD/drill.exe" "$sp" 2>&1); erc=$?
  if [ "$erc" -ne 0 ] && printf '%s' "$o" | grep -qF -- "$sx"; then echo "PASS refused: $(basename "$sp") - the reader says '$sx'"; else echo "FAIL — the reader was to refuse $(basename "$sp") saying '$sx' (rc=$erc)"; exit 1; fi
done

# ---- Blender parity on a sample (FULL=1 for every file) ----
BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
  for base in "/c/Program Files/Blender Foundation" "/c/Program Files (x86)/Blender Foundation"; do
    [ -d "$base" ] || continue
    cand=$(ls -d "$base"/Blender* 2>/dev/null | sort -V | tail -1); [ -n "$cand" ] && [ -x "$cand/blender.exe" ] && { BLENDER="$cand/blender.exe"; break; }
  done
fi
if [ -z "$BLENDER" ]; then echo "PASS — GLB reader drill: $n_ok registry files read; Blender not found, so no parity comparison (BLENDER=<exe> to force)"; exit 0; fi
if [ "${FULL:-0}" = "1" ]; then SAMPLE=("${FILES[@]}")
else
  # every fixture and every Khronos sample (small, and the diversity is the point), the registry's largest and smallest two, four animated ones
  mapfile -t SAMPLE < <( { printf '%s\n' "${FIXTURES[@]}"; [ "${#SAMPLES_OK[@]}" -eq 0 ] || printf '%s\n' "${SAMPLES_OK[@]}"; ls -S "${FILES[@]}" | head -2; ls -S "${FILES[@]}" | tail -2; echo "$RESULT" | awk -F'\t' '/^FILE/ { for (i=2;i<=NF;i++) if ($i ~ /^animations=/ && $i != "animations=0") print $2 }' | head -4 | while read -r n; do for f in "${FILES[@]}"; do case "$f" in */"$n"/*) echo "$f";; esac; done; done; } | sort -u )
fi
BOUT=$("$BLENDER" --background --python-exit-code 1 --python "$(cygpath -m "$ROOT/tools/glb-reader-drill/blender_counts.py")" -- "${SAMPLE[@]}" 2>&1); brc=$?
BOUT=$(printf '%s' "$BOUT" | LC_ALL=C sed 's/\xEF\xBB\xBF//g')
if [ "$brc" -ne 0 ]; then echo "$BOUT" | tail -5; echo "FAIL — Blender could not import the sample (exit $brc)"; exit 1; fi
printf '%s
' "$RESULT" > "$TMPD/csharp.txt"; printf '%s
' "$BOUT" > "$TMPD/blender.txt"
python "$ROOT/tools/glb-reader-drill/compare.py" "$TMPD/csharp.txt" "$TMPD/blender.txt"; crc=$?
if [ "$crc" -ne 0 ]; then echo "FAIL — GLB reader drill: a sampled file disagrees with Blender (or none compared)"; exit 1; fi
echo "PASS — GLB reader drill: $n_ok files read and checked (the registry, ${#FIXTURES[@]} fixtures, $SAMPLES_NOTE); ${#SAMPLE[@]} of them agree with Blender on counts, box, area, centroid, winding, bones and durations"
