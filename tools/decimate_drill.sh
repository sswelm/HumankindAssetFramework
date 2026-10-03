#!/usr/bin/env bash
# decimate_drill.sh - Blender's mesh layout in C# against Blender's own (2026-10-03, step 5 of replacing Blender, milestone a:
# the edge order the Decimate modifier's heap sees - which of two equal-cost edges collapses first). editor/BlenderMesh.cs
# (with editor/BlenderNames.cs for the object names) is compiled with Unity's Roslyn and run on Unity's Mono over the
# fixtures, the registry and the recipe sources; Blender imports a sample of them in one process and dumps every mesh
# object's edge list; the lists are compared per object, exact (the whole list by hash, the counts, the first edges on a
# mismatch). FULL=1 compares every file (Blender takes some ten minutes). Prerequisites and SKIP rules as glb_reader_drill.sh.
# The bucket count mesh_calc_edges uses depends on the machine's thread count (1 under 1,000 faces, else min(8, threads) as
# a power of two): the C# side reads the same count from the machine it runs on.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; MONO="$UNITY/MonoBleedingEdge/bin/mono.exe"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$MONO" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; the mesh layout was NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (the mesh layout was NOT drilled)"; exit 2; }
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; the mesh layout was NOT drilled)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/edges.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/decimate-drill/EdgesDrill.cs" "$WROOT/editor/HafModel.cs" "$WROOT/editor/GlbReader.cs" "$WROOT/editor/BlenderNames.cs" "$WROOT/editor/BlenderMesh.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/edges.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the mesh layout drill did not compile (csc rc=$rc)"; exit 1; fi

mapfile -t FIXTURES < <(python "$ROOT/tools/glb-reader-drill/fixtures.py" "$WTMP/fixtures" | tr -d '\r')
mapfile -t NAMING < <(python "$ROOT/tools/vehicle-probe-drill/naming_fixtures.py" "$WTMP/naming" | tr -d '\r')
[ "${#FIXTURES[@]}" -gt 0 ] && [ "${#NAMING[@]}" -gt 0 ] || { echo "FAIL — could not write the fixtures"; exit 1; }
PACK=$(ls "$PROJECT"/Assets/Pack/*/pack.json 2>/dev/null | head -1)
REGISTRY=(); REGISTRY_NOTE="the registry"
if [ -n "$PACK" ]; then mapfile -t REGISTRY < <(python "$ROOT/tools/glb-reader-drill/registry_files.py" "$PACK" | tr -d '\r' | awk '!seen[tolower($0)]++'); else REGISTRY_NOTE="NO registry (no modding project at $PROJECT; set HAF_UNITY_PROJECT)"; fi
RECIPE_SOURCES=()
[ -z "$PACK" ] || mapfile -t RECIPE_SOURCES < <(python "$ROOT/tools/vehicle-probe-drill/recipe_check.py" --list "$PROJECT" | tr -d '\r')
FILES=("${FIXTURES[@]}" "${NAMING[@]}"); [ "${#REGISTRY[@]}" -eq 0 ] || FILES+=("${REGISTRY[@]}"); [ "${#RECIPE_SOURCES[@]}" -eq 0 ] || FILES+=("${RECIPE_SOURCES[@]}")
mapfile -t FILES < <(printf '%s\n' "${FILES[@]}" | awk '!seen[tolower($0)]++')

# the sample Blender imports: every fixture, the registry's three smallest files; FULL=1 everything
if [ "${FULL:-0}" = "1" ]; then SAMPLE=("${FILES[@]}")
else mapfile -t SAMPLE < <( { printf '%s\n' "${FIXTURES[@]}" | grep -v '/big\.glb$'; printf '%s\n' "${NAMING[@]}"; [ "${#REGISTRY[@]}" -eq 0 ] || ls -S "${REGISTRY[@]}" | tail -3; } | awk '!seen[tolower($0)]++' ); fi

# the C# side on the sample (every file in FULL=1 only: the layout of the 398 MB source takes minutes on the 32-bit Mono)
"$MONO" "$TMPD/edges.exe" "${SAMPLE[@]}" > "$TMPD/csharp_raw.txt" 2>&1; rc=$?
LC_ALL=C sed 's/\xEF\xBB\xBF//g' "$TMPD/csharp_raw.txt" | tr -d '\r' > "$TMPD/csharp.txt"
grep -E "^FAIL" "$TMPD/csharp.txt"
n_ok=$(grep -c "^FILE" "$TMPD/csharp.txt"); n_rows=$(grep -c "^MESH" "$TMPD/csharp.txt")
[ "$n_ok" -eq "${#SAMPLE[@]}" ] || { echo "FAIL — mesh layout drill: a file could not be laid out ($n_ok of ${#SAMPLE[@]})"; exit 1; }

BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
  for base in "/c/Program Files/Blender Foundation" "/c/Program Files (x86)/Blender Foundation"; do
    cand=$(ls -d "$base"/Blender* 2>/dev/null | sort -V | tail -1); [ -n "$cand" ] && [ -x "$cand/blender.exe" ] && { BLENDER="$cand/blender.exe"; break; }
  done
fi
if [ -z "$BLENDER" ]; then echo "PASS — mesh layout drill: $n_ok files laid out in C# ($n_rows mesh objects); Blender not found, so NO parity comparison (BLENDER=<exe> to force)"; exit 0; fi
t0=$(date +%s)
"$BLENDER" --background --python "$(cygpath -m "$ROOT/tools/decimate-drill/blender_edges_many.py")" -- "${SAMPLE[@]}" > "$TMPD/blender_raw.txt" 2>&1; brc=$?
t1=$(date +%s)
tr -d '\r' < "$TMPD/blender_raw.txt" | grep -E "^MESH|^FILE|^FAIL" > "$TMPD/blender.txt"
n_b=$(grep -c "^FILE" "$TMPD/blender.txt")
if [ "$brc" -ne 0 ] || [ "$n_b" -ne "${#SAMPLE[@]}" ]; then
  grep -E "^FAIL|Traceback|Error" "$TMPD/blender_raw.txt" | head -8
  echo "FAIL — mesh layout drill: Blender imported $n_b of ${#SAMPLE[@]} files (exit $brc)"; exit 1
fi
CMP=$(python "$ROOT/tools/decimate-drill/compare_edges.py" "$TMPD/csharp.txt" "$TMPD/blender.txt" 2>&1); crc=$?
CMP=$(printf '%s' "$CMP" | tr -d '\r')
echo "$CMP" | grep -E "^FAIL"
echo "$CMP" | grep -E "^COMPARED"
[ "$crc" -eq 0 ] || { echo "FAIL — mesh layout drill: the C# edge lists differ from Blender's"; exit 1; }
echo "PASS — mesh layout drill: on ${#SAMPLE[@]} files ($(echo "$CMP" | grep -E '^COMPARED' | awk '{print $6, "mesh objects,", $8, "edges"}')) every edge list equals Blender's own, in order (Blender took $((t1 - t0)) s); ${#FIXTURES[@]} fixtures, ${#NAMING[@]} naming fixtures, $REGISTRY_NOTE (${#REGISTRY[@]}), ${#RECIPE_SOURCES[@]} recipe sources known"
