#!/usr/bin/env bash
# prep_drill.sh - the model prep in C# against Blender's own (2026-10-05, step 5 of replacing Blender, milestone d: the mesh
# as Blender's glTF exporter lays it out after the reduce). Blender runs the REAL editor/Tools~/prep_model.py on a sample of
# the fixtures and the registry - twice per file: a target of a third of its triangles, and a target of all of them (nothing
# collapses, the file still goes through importer, apply and exporter) - and writes GLBs; the C# side reduces every mesh
# object of the source (BlenderReduce), lays it out as the exporter does (BlenderExport) and holds each primitive to the one
# Blender wrote for the node of the same name: vertex count, positions, normals, UV sets, colour sets, joints and weights
# (a skinned object's joint list too, by name) and indices, bit for bit. FULL=1 takes every file. The C# side runs as a 64-bit process (the exe directly), as decimate_drill.sh does and for
# the same reason (BlenderTrig.cs). Prerequisites and SKIP rules as glb_reader_drill.sh.
# The C# side also prints which rules of the layout its compared objects exercised (COVER rows); a row at zero FAILS the
# drill, because equal output says nothing about a rule no object reached - the exporter's validate step was missing while
# the gate sample passed, and only FULL=1 showed it (five fused ships, 2026-10-05). A new rule needs a COVER key in
# PrepDrill.cs and an object in fixtures.py's export_layout that reaches it.
# Edit this script only while no FULL run is executing it: bash reads a script as it goes. Run FULL from a copy.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; the prep was NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (the prep was NOT drilled)"; exit 2; }
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; the prep was NOT drilled)"; exit 2; }
BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then
  for base in "/c/Program Files/Blender Foundation" "/c/Program Files (x86)/Blender Foundation"; do
    cand=$(ls -d "$base"/Blender* 2>/dev/null | sort -V | tail -1); [ -n "$cand" ] && [ -x "$cand/blender.exe" ] && { BLENDER="$cand/blender.exe"; break; }
  done
fi
[ -n "$BLENDER" ] || { echo "SKIP — prep drill: Blender not found (BLENDER=<exe> to force); the prep was NOT drilled"; exit 0; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -optimize+ -out:"$WTMP/prep.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/prep-drill/PrepDrill.cs" \
  "$WROOT/editor/HafModel.cs" "$WROOT/editor/GlbReader.cs" "$WROOT/editor/HafTransforms.cs" "$WROOT/editor/BlenderNames.cs" "$WROOT/editor/BlenderMesh.cs" "$WROOT/editor/BMesh.cs" \
  "$WROOT/editor/BlenderColor.cs" "$WROOT/editor/BlenderTrig.cs" "$WROOT/editor/BlenderDecimate.cs" "$WROOT/editor/BlenderReduce.cs" "$WROOT/editor/BlenderExport.cs" \
  "$WROOT/editor/VehicleProbe.cs" "$WROOT/editor/VehicleProbe.Visibility.cs" "$WROOT/editor/VehicleProbe.Islands.cs" "$WROOT/editor/VehicleProbe.InsideOut.cs" \
  "$WROOT/editor/VehicleProbe.BlenderWorld.cs" "$WROOT/editor/VehicleProbe.BlenderSkin.cs" "$WROOT/editor/VehicleProbe.CustomNormals.cs" "$WROOT/editor/VehicleProbe.Merge.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/prep.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the prep drill did not compile (csc rc=$rc)"; exit 1; fi

mapfile -t FIXTURES < <(python "$ROOT/tools/glb-reader-drill/fixtures.py" "$WTMP/fixtures" | tr -d '\r')
[ "${#FIXTURES[@]}" -gt 0 ] || { echo "FAIL — could not write the fixtures"; exit 1; }
PACK=$(ls "$PROJECT"/Assets/Pack/*/pack.json 2>/dev/null | head -1)
REGISTRY=(); REGISTRY_NOTE="the registry"
if [ -n "$PACK" ]; then mapfile -t REGISTRY < <(python "$ROOT/tools/glb-reader-drill/registry_files.py" "$PACK" | tr -d '\r' | awk '!seen[tolower($0)]++'); else REGISTRY_NOTE="NO registry (no modding project at $PROJECT; set HAF_UNITY_PROJECT)"; fi
RECIPE_SOURCES=()
[ -z "$PACK" ] || mapfile -t RECIPE_SOURCES < <(python "$ROOT/tools/vehicle-probe-drill/recipe_check.py" --list "$PROJECT" | tr -d '\r')
# the sample: every fixture but the big grid, the registry's three smallest files; FULL=1 everything (.glb / .gltf only)
if [ "${FULL:-0}" = "1" ]; then mapfile -t SAMPLE < <( { printf '%s\n' "${FIXTURES[@]}"; [ "${#REGISTRY[@]}" -eq 0 ] || printf '%s\n' "${REGISTRY[@]}"; [ "${#RECIPE_SOURCES[@]}" -eq 0 ] || printf '%s\n' "${RECIPE_SOURCES[@]}"; } | grep -iE '\.(glb|gltf)$' | awk '!seen[tolower($0)]++' )
else mapfile -t SAMPLE < <( { printf '%s\n' "${FIXTURES[@]}" | grep -v '/big\.glb$'; [ "${#REGISTRY[@]}" -eq 0 ] || ls -S "${REGISTRY[@]}" | tail -3; } | grep -iE '\.(glb|gltf)$' | awk '!seen[tolower($0)]++' ); fi

t0=$(date +%s)
"$BLENDER" --background --python "$(cygpath -m "$ROOT/tools/prep-drill/blender_prep_many.py")" -- "$(cygpath -m "$ROOT/editor/Tools~/prep_model.py")" "$WTMP/out" "${SAMPLE[@]}" > "$TMPD/blender_raw.txt" 2>&1; brc=$?
t1=$(date +%s)
tr -d '\r' < "$TMPD/blender_raw.txt" | grep -E "^PREP	|^PREPFAIL	|^FILE	|^FAIL	" > "$TMPD/rows.txt"
n_b=$(grep -c "^FILE" "$TMPD/rows.txt")
if [ "$brc" -ne 0 ] || [ "$n_b" -ne "${#SAMPLE[@]}" ]; then
  grep -E "^FAIL|Traceback|Error" "$TMPD/blender_raw.txt" | head -8
  echo "FAIL — prep drill: Blender prepared $n_b of ${#SAMPLE[@]} files (exit $brc)"; exit 1
fi
"$TMPD/prep.exe" "$WTMP/rows.txt" > "$TMPD/csharp_raw.txt" 2>&1; rc=$?
LC_ALL=C sed 's/\xEF\xBB\xBF//g' "$TMPD/csharp_raw.txt" | tr -d '\r' > "$TMPD/csharp.txt"
RUNTIME=$(grep -E "^RUNTIME" "$TMPD/csharp.txt" | head -1 | cut -f2-)
echo "$RUNTIME" | grep -q "64-bit.*trig exact" || { echo "FAIL — prep drill: the C# side did not run as a 64-bit process with the C runtime's cosf ($RUNTIME); the prep was NOT drilled"; exit 2; }
grep -E "^FAIL|^NOTE|^COVER" "$TMPD/csharp.txt"
TOTAL=$(grep -E "^TOTAL" "$TMPD/csharp.txt" | tail -1)
echo "$TOTAL"
[ -n "$TOTAL" ] || { tail -5 "$TMPD/csharp.txt"; echo "FAIL — prep drill: the C# side gave no total (rc=$rc)"; exit 1; }
[ "$rc" -eq 0 ] || { echo "FAIL — prep drill: the C# prep's meshes differ from the ones Blender's prep_model.py wrote"; exit 1; }
n_runs=$(echo "$TOTAL" | awk '{print $3}'); n_obj=$(echo "$TOTAL" | awk '{print $7}'); n_prim=$(echo "$TOTAL" | awk '{print $9}'); n_vert=$(echo "$TOTAL" | awk '{print $11}'); n_decl=$(echo "$TOTAL" | awk '{print $13}'); n_pf=$(echo "$TOTAL" | awk '{print $15}')
[ "$n_obj" -gt 0 ] || { echo "FAIL — prep drill: no object was compared at all"; exit 1; }
# every rule of the layout has a fixture (export_layout.glb, decimate_attrs.glb): a COVER row at zero means a rule was
# held to nothing in this run - a fixture went missing, or a rule was added without one
UNCOVERED=$(grep -E "^COVER 0 " "$TMPD/csharp.txt" | cut -d' ' -f3- | paste -sd';' -)
[ -z "$UNCOVERED" ] || { echo "FAIL — prep drill: no compared object exercised: $UNCOVERED (a rule Blender did not judge; add a fixture to tools/glb-reader-drill/fixtures.py)"; exit 1; }
[ "$(grep -cE "^COVER " "$TMPD/csharp.txt")" -gt 0 ] || { echo "FAIL — prep drill: the C# side printed no COVER rows"; exit 1; }
echo "PASS — prep drill: $n_runs runs of prep_model.py on ${#SAMPLE[@]} files; $n_obj object runs ($n_prim primitives, $n_vert vertices) laid out equal to the meshes Blender wrote - positions, normals, UVs, colours, joints and weights, indices; $n_decl object runs not laid out yet (named above), $n_pf runs where prep_model.py itself fails as known (Blender took $((t1 - t0)) s); ${#FIXTURES[@]} fixtures, $REGISTRY_NOTE (${#REGISTRY[@]}), ${#RECIPE_SOURCES[@]} recipe sources known"
