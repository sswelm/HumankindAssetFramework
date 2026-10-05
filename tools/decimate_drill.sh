#!/usr/bin/env bash
# decimate_drill.sh - Blender's mesh layout and BMesh links in C# against Blender's own (2026-10-03, step 5 of replacing
# Blender, milestones a and b: the edge order the Decimate modifier's heap sees and the disk/radial link order its collapse
# walks - which of two equal-cost edges collapses first). editor/BlenderMesh.cs and editor/BMesh.cs (with
# editor/BlenderNames.cs for the object names) are compiled with Unity's Roslyn and run on Unity's Mono over the fixtures,
# the registry and the recipe sources; Blender imports a sample of them in one process and dumps every mesh object's edge
# list and bmesh link lists (then a scripted sequence of kills and vertex splices on objects of at most 3,000 edges, the
# lists after every step); all compared per object, exact (hashes of the whole lists, the counts, the first edges on a
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
  "$WROOT/tools/decimate-drill/EdgesDrill.cs" "$WROOT/tools/decimate-drill/BMeshDrill.cs" "$WROOT/tools/decimate-drill/DecimateDrill.cs" \
  "$WROOT/editor/HafModel.cs" "$WROOT/editor/GlbReader.cs" "$WROOT/editor/HafTransforms.cs" "$WROOT/editor/BlenderNames.cs" "$WROOT/editor/BlenderMesh.cs" "$WROOT/editor/BMesh.cs" \
  "$WROOT/editor/BlenderColor.cs" "$WROOT/editor/BlenderDecimate.cs" "$WROOT/editor/BlenderReduce.cs" "$WROOT/editor/VehicleProbe.cs" "$WROOT/editor/VehicleProbe.Visibility.cs" "$WROOT/editor/VehicleProbe.Islands.cs" \
  "$WROOT/editor/VehicleProbe.InsideOut.cs" "$WROOT/editor/VehicleProbe.BlenderWorld.cs" "$WROOT/editor/VehicleProbe.BlenderSkin.cs" "$WROOT/editor/VehicleProbe.CustomNormals.cs" "$WROOT/editor/VehicleProbe.Merge.cs" "$WROOT/editor/BlenderTrig.cs" 2>&1); rc=$?
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

# the C# side on the sample, as a 64-BIT process: the exe is run directly (the .NET Framework's 64-bit runtime), because the
# collapse needs cosf and sinf as Blender computes them - the 64-bit C runtime's, which are not the rounded double functions,
# and which the 32-bit runtime Unity's stand-alone Mono loads does not export at all (BlenderTrig.cs). Unity's editor is 64-bit.
"$TMPD/edges.exe" "${SAMPLE[@]}" > "$TMPD/csharp_raw.txt" 2>&1; rc=$?
LC_ALL=C sed 's/\xEF\xBB\xBF//g' "$TMPD/csharp_raw.txt" | tr -d '\r' > "$TMPD/csharp.txt"
grep -E "^FAIL" "$TMPD/csharp.txt"
n_ok=$(grep -c "^FILE" "$TMPD/csharp.txt"); n_rows=$(grep -c "^MESH" "$TMPD/csharp.txt")
[ "$n_ok" -eq "${#SAMPLE[@]}" ] || { echo "FAIL — mesh layout drill: a file could not be laid out ($n_ok of ${#SAMPLE[@]})"; exit 1; }
RUNTIME=$(grep -E "^RUNTIME" "$TMPD/csharp.txt" | head -1 | cut -f2-)
echo "$RUNTIME" | grep -q "64-bit.*trig exact" || { echo "FAIL — decimate drill: the C# side did not run as a 64-bit process with the C runtime's cosf ($RUNTIME); the collapse was NOT drilled"; exit 2; }

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
tr -d '\r' < "$TMPD/blender_raw.txt" | grep -E "^MESH|^BM|^OP|^FILE|^FAIL" > "$TMPD/blender.txt"
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
# milestone c: Blender's Decimate COLLAPSE on every mesh object of the sample at three ratios (1, a half, prep_model's
# ratio for a third of the file's triangles), applied as prep_model applies it, against BlenderReduce's DEC rows
t2=$(date +%s)
"$BLENDER" --background --python "$(cygpath -m "$ROOT/tools/decimate-drill/blender_decimate_many.py")" -- "${SAMPLE[@]}" > "$TMPD/blender_dec_raw.txt" 2>&1; drc=$?
t3=$(date +%s)
tr -d '\r' < "$TMPD/blender_dec_raw.txt" | grep -E "^DEC|^FILE|^FAIL" > "$TMPD/blender_dec.txt"
n_d=$(grep -c "^FILE" "$TMPD/blender_dec.txt")
if [ "$drc" -ne 0 ] || [ "$n_d" -ne "${#SAMPLE[@]}" ]; then
  grep -E "^FAIL|Traceback|Error" "$TMPD/blender_dec_raw.txt" | head -8
  echo "FAIL — decimate drill: Blender decimated $n_d of ${#SAMPLE[@]} files (exit $drc)"; exit 1
fi
DCMP=$(python "$ROOT/tools/decimate-drill/compare_decimate.py" "$TMPD/csharp.txt" "$TMPD/blender_dec.txt" 2>&1); dcrc=$?
DCMP=$(printf '%s' "$DCMP" | tr -d '\r')
echo "$DCMP" | grep -E "^FAIL|^NOTE"
echo "$DCMP" | grep -E "^COMPARED"
[ "$dcrc" -eq 0 ] || { echo "FAIL — decimate drill: the C# collapse differs from Blender's Decimate"; exit 1; }
# the same collapses once more on Unity's stand-alone Mono (32-bit), on the gate sample only (the big sources exhaust it):
# Mono evaluates an uncast float product in double, which the .NET Framework run above cannot show (the stop count was one
# face off there). A mesh with normals is declined in this pass (no cosf: BlenderTrig.cs); the meshes without - the fixture's
# "Bare" grid with its UVs among them - go through the quadrics, the costs and the UV blend and must still equal Blender's.
MONO_NOTE="not run under Mono (FULL=1)"
if [ "${FULL:-0}" != "1" ]; then
  "$MONO" "$TMPD/edges.exe" --decimate-only "${SAMPLE[@]}" > "$TMPD/mono_raw.txt" 2>&1; mrc=$?
  LC_ALL=C sed 's/\xEF\xBB\xBF//g' "$TMPD/mono_raw.txt" | tr -d '\r' > "$TMPD/mono.txt"
  grep -E "^FAIL" "$TMPD/mono.txt"
  MCMP=$(python "$ROOT/tools/decimate-drill/compare_decimate.py" "$TMPD/mono.txt" "$TMPD/blender_dec.txt" 2>&1); mcrc=$?
  MCMP=$(printf '%s' "$MCMP" | tr -d '\r')
  echo "$MCMP" | grep -E "^FAIL"
  if [ "$mrc" -ne 0 ] || [ "$mcrc" -ne 0 ]; then echo "FAIL — decimate drill: under Unity's 32-bit Mono the C# collapse differs from Blender's (an uncast float expression?)"; exit 1; fi
  m_rows=$(echo "$MCMP" | grep -E '^COMPARED' | awk '{print $6}'); m_skip=$(echo "$MCMP" | grep -E '^COMPARED' | awk '{print $8}')
  [ "$((m_rows - m_skip))" -gt 0 ] || { echo "FAIL — decimate drill: the Mono pass compared no collapse at all (every mesh declined)"; exit 1; }
  MONO_NOTE="under Unity's 32-bit Mono $((m_rows - m_skip)) collapses of meshes without normals equal too, $m_skip declined there for the missing cosf"
fi
echo "PASS — mesh layout drill: on ${#SAMPLE[@]} files ($(echo "$CMP" | grep -E '^COMPARED' | awk '{print $6, "mesh objects,", $8, "edges"}')) every edge list equals Blender's own, in order, and so does every BMesh link list ($(echo "$CMP" | grep -E '^COMPARED' | awk '{print $10, "objects,", $12, "scripted kill/splice steps"}')) (Blender took $((t1 - t0)) s); ${#FIXTURES[@]} fixtures, ${#NAMING[@]} naming fixtures, $REGISTRY_NOTE (${#REGISTRY[@]}), ${#RECIPE_SOURCES[@]} recipe sources known"
echo "PASS — decimate drill: $(echo "$DCMP" | grep -E '^COMPARED' | awk '{print $6, "collapses of", $10, "faces"}') equal to Blender's Decimate COLLAPSE as prep_model applies it - positions, faces, UVs, custom normals, materials and sharp faces, vertex groups, colours - $(echo "$DCMP" | grep -E '^COMPARED' | awk '{print $8}') kept for Blender by design (Blender took $((t3 - t2)) s); $MONO_NOTE"
