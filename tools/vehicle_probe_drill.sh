#!/usr/bin/env bash
# vehicle_probe_drill.sh - the Vehicle Lab's probe in C# against Blender's own (2026-10-02, step 3 of replacing
# Blender). editor/VehicleProbe.cs (with editor/BlenderNames.cs: the names Blender's importer gives a file's nodes)
# is compiled with Unity's Roslyn and run on Unity's Mono over
#   * the fixture library (tools/glb-reader-drill/fixtures.py) and the naming fixtures written here (duplicate and
#     missing names, creation order, an armature at the root, islands laid out against their index order),
#   * every .glb the modding project's registry names, the Khronos samples when fetched,
#   * and the SOURCES OF THE LAB'S SAVED RECIPES - the probe's real inputs: the unreduced originals (925 MB, one of
#     398 MB), each recipe holding the parts Blender's probe listed when it was saved,
# then Blender runs the REAL probe (editor/Tools~/vehicle_rig.py probe, posed at the first clip's start) on a
# sample of them in one process, and the rows are compared per file: the part NAMES (the key every saved recipe
# holds) and their order, the vertex counts, the world boxes, the visibility verdicts, the dominant bones, the RIGBONE rows.
# The sample is every fixture, six rigged Khronos samples, the registry's three smallest files, and the source of
# every recipe whose stored parts DIFFER from the C# probe's (a recipe can be stale - saved before its source was
# re-cut - so Blender's probe of the source as it is today decides: only a C# row that differs from Blender's fails;
# a differing recipe over 100 MB is left to FULL=1). FULL=1 probes every file in Blender (about a minute in one
# process, against some 10 s for the C# side, reading 1.7 GB included).
# The visibility verdict (field 6) is compared since step 3b (2026-10-02). Not compared yet, said: the inside-out verdict (field 8) - step 3c.
# The naming fixtures hold one importer rule each - and the ones no real file of these populations has (review of
# PR #112: not one of 105 files had a second skin, a camera whose name clashes, a rotation that is not a unit
# quaternion, or a loose-part name already taken; each had a defect behind it that 105 PASSes could not show).
# Prerequisites and SKIP rules as glb_reader_drill.sh.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; MONO="$UNITY/MonoBleedingEdge/bin/mono.exe"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$MONO" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; the vehicle probe was NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (the vehicle probe was NOT drilled)"; exit 2; }
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; the vehicle probe was NOT drilled)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/probe.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/vehicle-probe-drill/ProbeDrill.cs" "$WROOT/editor/HafModel.cs" "$WROOT/editor/GlbReader.cs" "$WROOT/editor/HafTransforms.cs" "$WROOT/editor/BlenderNames.cs" "$WROOT/editor/VehicleProbe.cs" "$WROOT/editor/VehicleProbe.Visibility.cs" "$WROOT/editor/VehicleProbe.Islands.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/probe.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the vehicle probe drill did not compile (csc rc=$rc)"; exit 1; fi

# ---- the sources: fixtures, naming fixtures, the registry, the Khronos samples
mapfile -t FIXTURES < <(python "$ROOT/tools/glb-reader-drill/fixtures.py" "$WTMP/fixtures" | tr -d '\r')
mapfile -t NAMING < <(python "$ROOT/tools/vehicle-probe-drill/naming_fixtures.py" "$WTMP/naming" | tr -d '\r')
[ "${#FIXTURES[@]}" -gt 0 ] && [ "${#NAMING[@]}" -gt 0 ] || { echo "FAIL — could not write the fixtures"; exit 1; }
PACK=$(ls "$PROJECT"/Assets/Pack/*/pack.json 2>/dev/null | head -1)
REGISTRY=(); REGISTRY_NOTE="the registry"
if [ -n "$PACK" ]; then mapfile -t REGISTRY < <(python "$ROOT/tools/glb-reader-drill/registry_files.py" "$PACK" | tr -d '\r' | awk '!seen[tolower($0)]++'); else REGISTRY_NOTE="NO registry (no modding project at $PROJECT; set HAF_UNITY_PROJECT)"; fi
SAMPLES_OK=()
mapfile -t SLINES < <(python "$ROOT/tools/glb-reader-drill/fetch_samples.py" "$ROOT/References/gltf-samples" --list 2>/dev/null | tr -d '\r')
for l in "${SLINES[@]}"; do IFS=$'\t' read -r sp st sx <<< "$l"; [ "$st" = "ok" ] && SAMPLES_OK+=("$sp"); done
# the sample cache, when it exists, must be COMPLETE: a sample that went missing would narrow the drill without a word
if [ -d "$ROOT/References/gltf-samples" ]; then
  n_listed=$(grep -cvE '^\s*(#|$)' "$ROOT/tools/glb-reader-drill/samples.txt")
  [ "${#SLINES[@]}" -eq "$n_listed" ] || { echo "FAIL — the Khronos sample cache has ${#SLINES[@]} of the $n_listed samples samples.txt names (fetch again: python tools/glb-reader-drill/fetch_samples.py References/gltf-samples)"; exit 1; }
fi
RECIPE_SOURCES=()
[ -z "$PACK" ] || mapfile -t RECIPE_SOURCES < <(python "$ROOT/tools/vehicle-probe-drill/recipe_check.py" --list "$PROJECT" | tr -d '\r')
FILES=("${FIXTURES[@]}" "${NAMING[@]}"); [ "${#REGISTRY[@]}" -eq 0 ] || FILES+=("${REGISTRY[@]}"); [ "${#SAMPLES_OK[@]}" -eq 0 ] || FILES+=("${SAMPLES_OK[@]}"); [ "${#RECIPE_SOURCES[@]}" -eq 0 ] || FILES+=("${RECIPE_SOURCES[@]}")
mapfile -t FILES < <(printf '%s\n' "${FILES[@]}" | awk '!seen[tolower($0)]++')   # a recipe's source may also be a registry file

# ---- the C# probe on every file
"$MONO" "$TMPD/probe.exe" "${FILES[@]}" > "$TMPD/csharp_raw.txt" 2>&1; rc=$?
LC_ALL=C sed 's/\xEF\xBB\xBF//g' "$TMPD/csharp_raw.txt" | tr -d '\r' > "$TMPD/csharp.txt"
# a file the 32-bit Mono has no address space for (Unity ships its standalone Mono as a 32-bit process; the 398 MB
# recipe source with its visibility BVH is one): probe it on the 64-bit .NET runtime - the same exe, run natively -
# and splice its rows in; said in the summary. The timing line stays Mono's for the rest.
N_NATIVE=0
mapfile -t OOM < <(awk -F'\t' '$1 == "FAIL" && $3 ~ /^OutOfMemoryException/ { print $2 }' "$TMPD/csharp.txt")
for key in "${OOM[@]}"; do
  [ -n "$key" ] || continue
  src=""; for f in "${FILES[@]}"; do k=$(printf '%s' "$f" | tr '\\' '/' | tr 'A-Z' 'a-z'); [ "$k" = "$key" ] && { src="$f"; break; }; done
  [ -n "$src" ] || continue
  "$TMPD/probe.exe" "$src" 2>&1 | LC_ALL=C sed 's/\xEF\xBB\xBF//g' | tr -d '\r' | grep -E '^(ROW|FILE|FAIL)' > "$TMPD/native_$N_NATIVE.txt"
  awk -F'\t' -v k="$key" '!($1 == "FAIL" && $2 == k)' "$TMPD/csharp.txt" > "$TMPD/csharp2.txt"; cat "$TMPD/native_$N_NATIVE.txt" >> "$TMPD/csharp2.txt"; mv "$TMPD/csharp2.txt" "$TMPD/csharp.txt"
  N_NATIVE=$((N_NATIVE + 1))
done
grep -E "^FAIL" "$TMPD/csharp.txt"
n_ok=$(grep -c "^FILE" "$TMPD/csharp.txt"); n_rows=$(grep -c "^ROW" "$TMPD/csharp.txt")
[ "$n_ok" -eq "${#FILES[@]}" ] || { echo "FAIL — vehicle probe drill: a file could not be probed ($n_ok of ${#FILES[@]})"; exit 1; }
echo "C# probe: $n_ok files, $n_rows rows - ${#FIXTURES[@]} fixtures, ${#NAMING[@]} naming fixtures, $REGISTRY_NOTE (${#REGISTRY[@]}), ${#SAMPLES_OK[@]} Khronos samples, ${#RECIPE_SOURCES[@]} recipe sources ($(grep '^TOTAL' "$TMPD/csharp.txt"))"

# ---- the saved recipes: what Blender listed when each was saved, against the C# probe of its source today
DIFFERING=(); RECIPE_LINE=""
if [ "${#RECIPE_SOURCES[@]}" -gt 0 ]; then
  ROUT=$(python "$ROOT/tools/vehicle-probe-drill/recipe_check.py" "$PROJECT" "$TMPD/csharp.txt" "$TMPD/differing.txt" 2>&1 | tr -d '\r')
  echo "$ROUT" | grep -E "^DIFFERS" | cut -c1-260
  RECIPE_LINE=$(echo "$ROUT" | grep '^RECIPES')
  [ -n "$RECIPE_LINE" ] || { echo "$ROUT" | head -5; echo "FAIL — vehicle probe drill: the recipe check did not run"; exit 1; }
  mapfile -t DIFFERING < <(tr -d '\r' < "$TMPD/differing.txt")
  echo "recipes: $RECIPE_LINE"
fi

# ---- Blender's probe on a sample (FULL=1: every file)
BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
  for base in "/c/Program Files/Blender Foundation" "/c/Program Files (x86)/Blender Foundation"; do
    cand=$(ls -d "$base"/Blender* 2>/dev/null | sort -V | tail -1); [ -n "$cand" ] && [ -x "$cand/blender.exe" ] && { BLENDER="$cand/blender.exe"; break; }
  done
fi
if [ -z "$BLENDER" ]; then echo "PASS — vehicle probe drill: $n_ok files probed in C#; Blender not found, so NO parity comparison (BLENDER=<exe> to force)"; exit 0; fi
if [ "${FULL:-0}" = "1" ]; then SAMPLE=("${FILES[@]}")
else
  # every fixture (the 72k-vertex grid aside: Blender's probe walks its faces in Python), the rigged Khronos samples, the registry's three smallest
  mapfile -t SAMPLE < <( { printf '%s\n' "${FIXTURES[@]}" | grep -v '/big\.glb$'; printf '%s\n' "${NAMING[@]}";
    for s in "${SAMPLES_OK[@]}"; do case "$(basename "$s")" in SimpleSkin.gltf|RiggedSimple.glb|RiggedFigure.glb|CesiumMan.glb|BrainStem.glb|MultipleScenes.gltf) echo "$s";; esac; done;
    [ "${#REGISTRY[@]}" -eq 0 ] || ls -S "${REGISTRY[@]}" | tail -3;
    for d in "${DIFFERING[@]}"; do [ -n "$d" ] && [ "$(stat -c %s "$d" 2>/dev/null || echo 0)" -le 100000000 ] && echo "$d"; done; } | awk '!seen[tolower($0)]++' )
fi
t0=$(date +%s)
"$BLENDER" --background --python "$(cygpath -m "$ROOT/tools/vehicle-probe-drill/blender_probe_many.py")" -- "$(cygpath -m "$ROOT/editor/Tools~/vehicle_rig.py")" "${SAMPLE[@]}" > "$TMPD/blender_raw.txt" 2>&1; brc=$?
t1=$(date +%s)
tr -d '\r' < "$TMPD/blender_raw.txt" | grep -E "^ROW|^FILE" > "$TMPD/blender.txt"
n_b=$(grep -c "^FILE" "$TMPD/blender.txt")
if [ "$brc" -ne 0 ] || [ "$n_b" -ne "${#SAMPLE[@]}" ]; then
  grep -E "VEHICLE ERROR|Traceback|Error" "$TMPD/blender_raw.txt" | head -8
  echo "FAIL — vehicle probe drill: Blender's probe ran on $n_b of ${#SAMPLE[@]} files (exit $brc)"; exit 1
fi
# the comparator's status FIRST, the filter afterwards: read after `| tr`, it was tr's - always 0 - and from PR #112 until
# 2026-10-02 this drill printed the comparator's FAIL lines and then PASS (tools/check-exit-status.sh guards the shape now)
CMP=$(python "$ROOT/tools/vehicle-probe-drill/compare_probe.py" "$TMPD/csharp.txt" "$TMPD/blender.txt" 2>&1); crc=$?
CMP=$(printf '%s' "$CMP" | tr -d '\r')
echo "$CMP" | grep -E "^FAIL"
echo "$CMP" | grep -E "^COMPARED"
[ "$crc" -eq 0 ] || { echo "FAIL — vehicle probe drill: the C# probe's rows differ from Blender's"; exit 1; }
# the recipes that differ: Blender's probe of the source today agreed with the C# rows (or the compare above failed), so the recipe is stale
n_arb=0; n_left=0
for d in "${DIFFERING[@]}"; do [ -z "$d" ] && continue; if printf '%s\n' "${SAMPLE[@]}" | grep -qixF -- "$d"; then n_arb=$((n_arb + 1)); else n_left=$((n_left + 1)); echo "NOTE — a differing recipe's source was not put to Blender in this run (over 100 MB; FULL=1): $d"; fi; done
[ "${#DIFFERING[@]}" -eq 0 ] || echo "recipes that differ: $n_arb are STALE (Blender's probe of the source today gives the C# rows, not the recipe's), $n_left left for FULL=1"
NATIVE_NOTE=""; [ "$N_NATIVE" -eq 0 ] || NATIVE_NOTE=" ($N_NATIVE probed on the 64-bit .NET runtime: too large for Unity's 32-bit standalone Mono)"
echo "PASS — vehicle probe drill: $n_ok files probed in C#$NATIVE_NOTE; on ${#SAMPLE[@]} of them the rows equal Blender's own probe (names, order, vertices, visibility, boxes, bones, rig bones; Blender took $((t1 - t0)) s for those)${RECIPE_LINE:+; $RECIPE_LINE}"
