#!/usr/bin/env bash
# workshop_compact_drill.sh - the Workshop's compaction on real files (2026-10-02).
#
# Every Workshop operation (Split, Cut, Fuse, Delete) takes meshes off their nodes; until now the meshes, their
# accessors and their bytes stayed in the file (614 MB of 1,394 on the Lab's sources). editor/GlbDisconnectedParts
# .Compact.cs leaves them out when an output is written. This drill is what says that nothing ELSE is left out:
#   1. the real compaction (compiled with Unity's Roslyn, run on Unity's Mono) runs over
#        * every .glb in the folders of the Lab's recipe sources - the Workshop's own outputs, written by the
#          operations as they were BEFORE they compacted, orphans and all (SKIP without the modding project),
#        * the fixture library (tools/glb-reader-drill/fixtures.py) - one holds a mesh no node uses,
#        * the fixtures and the Khronos samples (when fetched) through a real OPERATION: the first mesh node is
#          removed (RemoveMeshes - the Del key), which orphans its mesh and compacts on the way out. Other exporters'
#          layouts - interleaved, sparse, skinned, animated - that no Workshop file has;
#   2. for every file something was left out of, the GLB READER - which shares no code with the Workshop - reads the
#      file as it was and as it is now: the first, less the meshes no node uses, must equal the second field by
#      field (every vertex of every attribute, every index, material, image byte, skin matrix, animation key);
#   3. a second compaction of the result must find nothing more;
#   4. Blender imports original and compacted copy of a sample (every pair under FULL=1) and must report THE SAME
#      for both, as printed: triangles, materials, images, box, area, centroid, winding, bones, durations.
# A compaction that is REFUSED (an extension this tool does not follow) is listed, not failed: the operation still
# writes its output, uncompacted. A file too large for the drill's runtime is verified on the 64-bit .NET runtime
# instead, and said: Unity ships its standalone Mono as a 32-bit process (the editor itself is 64-bit).
# Prerequisites and SKIP rules as glb_reader_drill.sh.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; MONO="$UNITY/MonoBleedingEdge/bin/mono.exe"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$MONO" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; the compaction was NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (the compaction was NOT drilled)"; exit 2; }
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; the compaction was NOT drilled)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/drill.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/workshop-compact-drill/CompactDrill.cs" "$WROOT/editor/GlbDisconnectedParts.cs" "$WROOT/editor/GlbDisconnectedParts.Compact.cs" \
  "$WROOT/editor/HafModel.cs" "$WROOT/editor/GlbReader.cs" "$WROOT/editor/HafTransforms.cs" "$WROOT/editor/HafModelDiff.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/drill.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the compaction drill did not compile (csc rc=$rc)"; exit 1; fi

# ---- the sources
PACK=$(ls "$PROJECT"/Assets/Pack/*/pack.json 2>/dev/null | head -1)
WORKSHOP=()
if [ -n "$PACK" ]; then
  mapfile -t RECIPE_SOURCES < <(python "$ROOT/tools/vehicle-probe-drill/recipe_check.py" --list "$PROJECT" | tr -d '\r')
  mapfile -t WORKSHOP < <(for s in "${RECIPE_SOURCES[@]}"; do dirname "$s"; done | awk '!seen[tolower($0)]++' | while IFS= read -r d; do [ -d "$d" ] && find "$d" -iname '*.glb' -type f; done | sort | awk '!seen[tolower($0)]++')
fi
WORKSHOP_NOTE="${#WORKSHOP[@]} file(s) beside the Lab's recipe sources"
[ "${#WORKSHOP[@]}" -gt 0 ] || WORKSHOP_NOTE="NO Workshop files (no modding project at $PROJECT; set HAF_UNITY_PROJECT)"
mapfile -t FIXTURES < <(python "$ROOT/tools/glb-reader-drill/fixtures.py" "$WTMP/fixtures" | tr -d '\r' | grep -i '\.glb$')
[ "${#FIXTURES[@]}" -gt 0 ] || { echo "FAIL — could not write the fixtures (tools/glb-reader-drill/fixtures.py)"; exit 1; }
SAMPLES=()
mapfile -t SLINES < <(python "$ROOT/tools/glb-reader-drill/fetch_samples.py" "$ROOT/References/gltf-samples" --list 2>/dev/null | tr -d '\r')
for l in "${SLINES[@]}"; do IFS=$'\t' read -r sp st sx <<< "$l"; [ "$st" = "ok" ] && case "$sp" in *.glb|*.GLB) SAMPLES+=("$sp");; esac; done
if [ -d "$ROOT/References/gltf-samples" ]; then   # the cache, when it exists, must be complete: a missing sample would narrow the drill without a word
  n_listed=$(grep -cvE '^\s*(#|$)' "$ROOT/tools/glb-reader-drill/samples.txt")
  [ "${#SLINES[@]}" -eq "$n_listed" ] || { echo "FAIL — the Khronos sample cache has ${#SLINES[@]} of the $n_listed samples samples.txt names (fetch again: python tools/glb-reader-drill/fetch_samples.py References/gltf-samples)"; exit 1; }
fi
SAMPLES_NOTE="${#SAMPLES[@]} Khronos samples"
[ "${#SLINES[@]}" -gt 0 ] || SAMPLES_NOTE="no Khronos samples (fetch them once: python tools/glb-reader-drill/fetch_samples.py References/gltf-samples)"

# what Blender is to look at: every pair under FULL=1; else the fixtures, every Workshop file up to 16 MB, and the
# smallest FUSED one whatever its size (a fuse is where the bytes were)
KEEP="$TMPD/keep.txt"; : > "$KEEP"
if [ "${FULL:-0}" != "1" ]; then
  printf '%s\n' "${FIXTURES[@]}" >> "$KEEP"
  for f in "${WORKSHOP[@]}"; do [ "$(stat -c %s "$f" 2>/dev/null || echo 0)" -le 16000000 ] && printf '%s\n' "$f" >> "$KEEP"; done
  [ "${#WORKSHOP[@]}" -eq 0 ] || { ls -S "${WORKSHOP[@]}" 2>/dev/null | grep -i '_fused\.glb$' | tail -1 >> "$KEEP"; }
fi
ARGS=()
[ "${#WORKSHOP[@]}" -eq 0 ] || ARGS+=("${WORKSHOP[@]}")
ARGS+=("${FIXTURES[@]}")
for f in "${FIXTURES[@]}"; do ARGS+=("remove:$f"); done
for f in "${SAMPLES[@]}"; do ARGS+=("remove:$f"); done
KEEPARG="$(cygpath -m "$KEEP")"; [ "${FULL:-0}" = "1" ] && KEEPARG="-"

"$MONO" "$WTMP/drill.exe" "$WTMP/out" "$KEEPARG" "${ARGS[@]}" 2>&1 | LC_ALL=C sed 's/\xEF\xBB\xBF//g' | tr -d '\r' > "$TMPD/result.txt"
# a file the 32-bit Mono has no address space for: verify it on the 64-bit .NET runtime (the same exe, run natively)
N_NATIVE=0
mapfile -t OOM < <(awk -F'\t' '$1 == "COMPACT" && $3 ~ /^FAIL: OutOfMemoryException/ { print $2 }' "$TMPD/result.txt")
for src in "${OOM[@]}"; do
  [ -n "$src" ] || continue
  line=$("$TMPD/drill.exe" "$WTMP/out_native_$N_NATIVE" "$KEEPARG" "$src" 2>&1 | LC_ALL=C sed 's/\xEF\xBB\xBF//g' | tr -d '\r' | grep '^COMPACT')
  [ -n "$line" ] || line=$(printf 'COMPACT\t%s\tFAIL: the 64-bit run printed nothing\t-' "$src")
  awk -F'\t' -v s="$src" '!($1 == "COMPACT" && $2 == s && $3 ~ /^FAIL: OutOfMemoryException/)' "$TMPD/result.txt" > "$TMPD/result2.txt"
  printf '%s\n' "$line" >> "$TMPD/result2.txt"; mv "$TMPD/result2.txt" "$TMPD/result.txt"
  N_NATIVE=$((N_NATIVE + 1))
done

n_all=$(grep -c '^COMPACT' "$TMPD/result.txt"); n_fail=$(grep -cP '^COMPACT\t[^\t]*\tFAIL' "$TMPD/result.txt")
n_compacted=$(grep -cP '^COMPACT\t[^\t]*\tcompacted\t' "$TMPD/result.txt")
# per group: the files as they are (Workshop outputs, fixtures) and the ones a part was first removed from
printf '%s\n' "${WORKSHOP[@]}" | tr 'A-Z' 'a-z' > "$TMPD/workshop.txt"
GROUPLINE=$(awk -F'\t' 'NR == FNR { w[$0] = 1; next }
  $1 != "COMPACT" { next }
  { g = ($9 == "mode=remove") ? "r" : ((tolower($2) in w) ? "w" : "f"); n[g]++
    if ($3 ~ /^compacted/) { c[g]++; if (g == "w") { split($7, b, /=|->/); was += b[2]; now += b[3] } } else if ($3 ~ /^refused/) x[g]++; else if ($3 == "nothing") z[g]++ }
  END { printf "Workshop files: %d of %d had something to leave out (%.0f -> %.0f MB), %d were left as they are", c["w"], n["w"], was / 1e6, now / 1e6, z["w"] + x["w"]
        printf "; fixtures: %d of %d compacted; after a part was removed (fixtures and Khronos samples): %d of %d compacted, %d refused, %d with nothing to leave out", c["f"], n["f"], c["r"], n["r"], x["r"], z["r"] }' "$TMPD/workshop.txt" "$TMPD/result.txt")
if [ "$n_all" -ne "${#ARGS[@]}" ]; then tail -3 "$TMPD/result.txt"; echo "FAIL — compaction drill: $n_all of ${#ARGS[@]} files reported"; exit 1; fi
grep -P '^COMPACT\t[^\t]*\trefused' "$TMPD/result.txt" | awk -F'\t' '{ n = split($2, a, "/"); print "NOTE — not compacted, " a[n] ": " substr($3, 10) }' | cut -c1-300 | head -12
if [ "$n_fail" -gt 0 ]; then
  grep -P '^COMPACT\t[^\t]*\tFAIL' "$TMPD/result.txt" | awk -F'\t' '{ n = split($2, a, "/"); print "FAIL " a[n] ": " substr($3, 7) }' | cut -c1-400 | head -12
  echo "FAIL — compaction drill: $n_fail file(s) do not read as before, or did not compact cleanly"; exit 1
fi
SUMMARY="$n_all runs ($WORKSHOP_NOTE, ${#FIXTURES[@]} fixtures, $SAMPLES_NOTE) - $GROUPLINE; every compacted file reads as before through the GLB reader and compacts no further"
[ "$N_NATIVE" -eq 0 ] || SUMMARY="$SUMMARY ($N_NATIVE verified on the 64-bit .NET runtime: too large for Unity's 32-bit standalone Mono)"
[ "$n_compacted" -gt 0 ] || { echo "FAIL — compaction drill: nothing was compacted ($SUMMARY)"; exit 1; }

# ---- Blender: original beside compacted copy
BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
  for base in "/c/Program Files/Blender Foundation" "/c/Program Files (x86)/Blender Foundation"; do
    cand=$(ls -d "$base"/Blender* 2>/dev/null | sort -V | tail -1); [ -n "$cand" ] && [ -x "$cand/blender.exe" ] && { BLENDER="$cand/blender.exe"; break; }
  done
fi
if [ -z "$BLENDER" ]; then echo "PASS — compaction drill: $SUMMARY; Blender not found, so NO import comparison (BLENDER=<exe> to force)"; exit 0; fi
awk -F'\t' '$3 == "compacted" && $4 != "-" { print $2 "\t" $4 }' "$TMPD/result.txt" > "$TMPD/pairs.txt"
n_pairs=$(grep -c . "$TMPD/pairs.txt")
[ "$n_pairs" -gt 0 ] || { echo "FAIL — compaction drill: no compacted pair was kept for Blender"; exit 1; }
mapfile -t BFILES < <(tr '\t' '\n' < "$TMPD/pairs.txt")
t0=$(date +%s)
"$BLENDER" --background --python-exit-code 1 --python "$(cygpath -m "$ROOT/tools/glb-reader-drill/blender_counts.py")" -- "${BFILES[@]}" > "$TMPD/blender_raw.txt" 2>&1; brc=$?
t1=$(date +%s)
LC_ALL=C sed 's/\xEF\xBB\xBF//g' "$TMPD/blender_raw.txt" | tr -d '\r' | grep '^BLENDER' > "$TMPD/blender.txt"
if [ "$brc" -ne 0 ]; then tail -5 "$TMPD/blender_raw.txt"; echo "FAIL — compaction drill: Blender could not import the pairs (exit $brc)"; exit 1; fi
# Capture Python's status before normalizing its output; a pipeline would return tr's success on a mismatch.
CMP=$(python "$ROOT/tools/workshop-compact-drill/compare_pairs.py" "$TMPD/pairs.txt" "$TMPD/blender.txt" 2>&1); crc=$?
CMP=$(printf '%s' "$CMP" | tr -d '\r')
echo "$CMP" | grep -E "^FAIL"
echo "$CMP" | grep -E "^COMPARED"
[ "$crc" -eq 0 ] || { echo "FAIL — compaction drill: Blender does not see an original and its compacted copy as the same"; exit 1; }
echo "PASS — compaction drill: $SUMMARY; Blender imports $n_pairs original(s) and their compacted copies and reports the same for both, as printed (triangles, materials, images, box, area, centroid, winding, bones, durations; $((t1 - t0)) s)"
