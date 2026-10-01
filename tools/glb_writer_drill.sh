#!/usr/bin/env bash
# glb_writer_drill.sh - the GLB writer against the real registry, and against Blender (2026-10-01, step 2 of replacing
# Blender). Every model the registry names (and the two-target fixture) is read by the real GlbReader and written by
# the real GlbWriter to disk; then
#  00. every extensions/extras object in the sources sits at a path the model carries (carried_paths.py), else FAIL;
#   0. the written file is read back and compared with its source FIELD BY FIELD inside the writer drill (every
#      vertex, index, material field, sampler, image byte, skin matrix, animation key); the first difference is named;
#   1. the reader drill reads the WRITTEN files and every value (counts, world box, area, centroid, winding, bones,
#      spans) must equal the original's - the round trip, on real models, through the drill's own view;
#   2. Blender imports the WRITTEN sample and every value must equal what the C# side read from the ORIGINAL - the
#      written file means to Blender what the original meant. FULL=1 for every file.
# Prerequisites and SKIP rules as glb_reader_drill.sh.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; MONO="$UNITY/MonoBleedingEdge/bin/mono.exe"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$MONO" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; GLB writer NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (GLB writer NOT drilled)"; exit 2; }
PACK=$(ls "$PROJECT"/Assets/Pack/*/pack.json 2>/dev/null | head -1)
REGISTRY_NOTE="the registry"
[ -n "$PACK" ] || REGISTRY_NOTE="NO registry (no modding project at $PROJECT; set HAF_UNITY_PROJECT)"   # the fixtures and samples are drilled all the same
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; GLB writer NOT drilled)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
REFS=(-r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll")
SRC=("$WROOT/editor/HafModel.cs" "$WROOT/editor/GlbReader.cs" "$WROOT/editor/HafTransforms.cs" "$WROOT/editor/GlbWriter.cs" "$WROOT/editor/HafModelDiff.cs")
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/write.exe" "${REFS[@]}" "$WROOT/tools/glb-reader-drill/WriteDrill.cs" "${SRC[@]}" 2>&1); rc=$?
[ "$rc" -eq 0 ] && [ -s "$TMPD/write.exe" ] || { echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the GLB writer drill did not compile (csc rc=$rc)"; exit 1; }
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/read.exe" "${REFS[@]}" "$WROOT/tools/glb-reader-drill/Drill.cs" "${SRC[@]}" 2>&1); rc=$?
[ "$rc" -eq 0 ] && [ -s "$TMPD/read.exe" ] || { echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the GLB reader drill did not compile (csc rc=$rc)"; exit 1; }

FILES=()
[ -z "$PACK" ] || mapfile -t FILES < <(python "$ROOT/tools/glb-reader-drill/registry_files.py" "$PACK" | tr -d '\r')
[ -n "$PACK" ] && [ "${#FILES[@]}" -eq 0 ] && REGISTRY_NOTE="NO registry (it names no .glb that exists on disk)"
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
# the sample cache, when it exists, must be COMPLETE: a sample that went missing would narrow the drill without a word
if [ -d "$ROOT/References/gltf-samples" ]; then
  n_listed=$(grep -cvE '^\s*(#|$)' "$ROOT/tools/glb-reader-drill/samples.txt")
  [ "${#SLINES[@]}" -eq "$n_listed" ] || { echo "FAIL — the Khronos sample cache has ${#SLINES[@]} of the $n_listed samples samples.txt names (fetch again: python tools/glb-reader-drill/fetch_samples.py References/gltf-samples)"; exit 1; }
fi
# two registry entries may share a file; the written copies are keyed by basename, so write each file once
mapfile -t UNIQUE < <(printf '%s\n' "${FILES[@]}" | awk '!seen[tolower($0)]++')

# ---- 00. every extensions/extras object in the SOURCES sits where the model carries it (the reader does not model the
# rest, so the writer would drop it without a word and the reader-vs-reader compare below could not tell)
COUT=$(python "$ROOT/tools/glb-reader-drill/carried_paths.py" "${UNIQUE[@]}" 2>&1); rc=$?   # rc BEFORE any pipe: a pipe would report tr's
COUT=$(printf '%s' "$COUT" | tr -d '\r')
echo "$COUT" | grep -E "^FAIL"
[ "$rc" -eq 0 ] || { echo "FAIL — GLB writer drill: a source carries an extensions/extras object at a path the model does not (it would be dropped on write)"; exit 1; }
echo "sources: $(echo "$COUT" | grep '^CARRIED' | cut -f2-)"

# ---- write
WRITTEN="$WTMP/written"
WOUT=$("$MONO" "$TMPD/write.exe" "$WRITTEN" "${UNIQUE[@]}" 2>&1); rc=$?
WOUT=$(printf '%s' "$WOUT" | LC_ALL=C sed 's/\xEF\xBB\xBF//g')
echo "$WOUT" | grep -E "^FAIL|^DIFF"
n_wrote=$(echo "$WOUT" | grep -c "^WROTE")
[ "$rc" -eq 0 ] || { echo "FAIL — GLB writer drill: a file could not be written, or read back differently from its source ($n_wrote of ${#UNIQUE[@]} written)"; exit 1; }
echo "C# writer: $n_wrote files written to disk and read back equal to their source field by field - $REGISTRY_NOTE, ${#FIXTURES[@]} fixtures, $SAMPLES_NOTE ($(echo "$WOUT" | grep '^TOTAL'))"
# the samples a stage must REFUSE, by name: the source guard (a path the model does not carry) or the writer (what it cannot write as it is)
for l in "${SAMPLES_EXPECT[@]}"; do
  IFS=$'\t' read -r sp st sx <<< "$l"
  case "$st" in
    guard)  o=$(python "$ROOT/tools/glb-reader-drill/carried_paths.py" "$sp" 2>&1); erc=$?;;
    writer) o=$("$MONO" "$TMPD/write.exe" "$WTMP/refused" "$sp" 2>&1); erc=$?;;
    *) continue;;
  esac
  if [ "$erc" -ne 0 ] && printf '%s' "$o" | grep -qF -- "$sx"; then echo "PASS refused: $(basename "$sp") - the $st says '$sx'"; else echo "FAIL — the $st was to refuse $(basename "$sp") saying '$sx' (rc=$erc)"; exit 1; fi
done

# ---- 1. the reader's view of the originals and of the written copies must agree on every value
ORIG=$("$MONO" "$TMPD/read.exe" "${UNIQUE[@]}" 2>&1); ORIG=$(printf '%s' "$ORIG" | LC_ALL=C sed 's/\xEF\xBB\xBF//g')
mapfile -t WFILES < <(ls "$WRITTEN"/*.glb)
BACK=$("$MONO" "$TMPD/read.exe" "${WFILES[@]}" 2>&1); rc=$?; BACK=$(printf '%s' "$BACK" | LC_ALL=C sed 's/\xEF\xBB\xBF//g')
echo "$BACK" | grep -E "^FAIL"
[ "$rc" -eq 0 ] || { echo "FAIL — GLB writer drill: a written file did not read back"; exit 1; }
printf '%s\n' "$ORIG" > "$TMPD/orig.txt"; printf '%s\n' "$BACK" > "$TMPD/back.txt"
python "$ROOT/tools/glb-reader-drill/compare.py" "$TMPD/orig.txt" "$TMPD/back.txt" --right-tag FILE --basename | grep -E "^FAIL|^COMPARED"
python "$ROOT/tools/glb-reader-drill/compare.py" "$TMPD/orig.txt" "$TMPD/back.txt" --right-tag FILE --basename > /dev/null || { echo "FAIL — GLB writer drill: a written file reads back different from its original"; exit 1; }

# ---- 2. Blender reads the written sample as it read the originals
BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
  for base in "/c/Program Files/Blender Foundation" "/c/Program Files (x86)/Blender Foundation"; do
    [ -d "$base" ] || continue
    cand=$(ls -d "$base"/Blender* 2>/dev/null | sort -V | tail -1); [ -n "$cand" ] && [ -x "$cand/blender.exe" ] && { BLENDER="$cand/blender.exe"; break; }
  done
fi
if [ -z "$BLENDER" ]; then echo "PASS — GLB writer drill: $n_wrote files written and read back equal ($REGISTRY_NOTE, ${#FIXTURES[@]} fixtures, $SAMPLES_NOTE); Blender not found, so no import of the written files (BLENDER=<exe> to force)"; exit 0; fi
if [ "${FULL:-0}" = "1" ]; then SAMPLE=("${WFILES[@]}")
else
  # the written fixtures and Khronos samples (every one), the largest and smallest two written registry files
  mapfile -t SAMPLE < <( { for f in "${FIXTURES[@]}" "${SAMPLES_OK[@]}"; do [ -n "$f" ] && echo "$WRITTEN/$(basename "${f%.*}").glb"; done; ls -S "${WFILES[@]}" | head -2; ls -S "${WFILES[@]}" | tail -2; } | sort -u )
fi
BOUT=$("$BLENDER" --background --python-exit-code 1 --python "$(cygpath -m "$ROOT/tools/glb-reader-drill/blender_counts.py")" -- "${SAMPLE[@]}" 2>&1); brc=$?
BOUT=$(printf '%s' "$BOUT" | LC_ALL=C sed 's/\xEF\xBB\xBF//g')
[ "$brc" -eq 0 ] || { echo "$BOUT" | tail -5; echo "FAIL — Blender could not import a written file (exit $brc)"; exit 1; }
printf '%s\n' "$BOUT" > "$TMPD/blender.txt"
python "$ROOT/tools/glb-reader-drill/compare.py" "$TMPD/orig.txt" "$TMPD/blender.txt" --basename | grep -E "^FAIL|^PASS |^COMPARED"
python "$ROOT/tools/glb-reader-drill/compare.py" "$TMPD/orig.txt" "$TMPD/blender.txt" --basename > /dev/null || { echo "FAIL — GLB writer drill: Blender reads a written file differently from its original"; exit 1; }
echo "PASS — GLB writer drill: $n_wrote files written and read back equal to their originals field by field ($REGISTRY_NOTE, ${#FIXTURES[@]} fixtures, $SAMPLES_NOTE); Blender reads ${#SAMPLE[@]} written file(s) as it read the originals"
