#!/usr/bin/env bash
# fbx_model_drill.sh - the FBX path into the Factory against Blender's own (step 6a of the native FBX reader, 2026-11-01).
# For every FBX source and fixture, Blender (live) imports the file with io_scene_fbx's defaults and exports a GLB
# (tools/fbx-drill/fbx_to_glb.py); the C# side (editor/FbxImport.cs + editor/FbxModel.cs) builds the HafModel Blender's
# exporter would write and holds it to that GLB on what sub-step 6a models: the node tree (names, parents, children order,
# mesh references, every written translation / rotation / scale as bits), the meshes (names, the primitives' material names,
# vertex counts, positions, normals, UV sets and indices as bits) and the material names. A file the import names as a
# fallback (bones, deformers, animation, cameras, colour layers: sub-steps 6b-6d) is a SKIP row, not a failure.
# The C# side runs as a 64-bit process (BlenderTrig: the C runtime's float trig). Prerequisites as prep_drill.sh.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; the FBX model was NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (the FBX model was NOT drilled)"; exit 2; }
command -v python >/dev/null 2>&1 || { echo "FAIL — python not on PATH (the FBX model was NOT drilled)"; exit 2; }
BLENDER="${BLENDER:-}"
if [ -z "$BLENDER" ]; then for cand in "/c/Program Files/Blender Foundation/Blender 5.1/blender.exe" "/c/Program Files/Blender Foundation/Blender 4.2/blender.exe"; do [ -f "$cand" ] && { BLENDER="$cand"; break; }; done; fi
[ -n "$BLENDER" ] && [ -f "$BLENDER" ] || { echo "SKIP — Blender not found (set BLENDER=<blender.exe>): the FBX model was NOT drilled"; exit 3; }
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }; done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; the FBX model was NOT drilled)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
cp "$ROOT/editor/Plugins/x86_64/hafkernels.dll" "$TMPD/hafkernels.dll" 2>/dev/null
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -optimize+ -platform:x64 -out:"$WTMP/fbxmodel.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WAPI/System.Numerics.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/fbx-drill/FbxModelDrill.cs" \
  "$WROOT/editor/FbxDocument.cs" "$WROOT/editor/FbxScene.cs" "$WROOT/editor/FbxAnimation.cs" "$WROOT/editor/FbxTriangulate.cs" "$WROOT/editor/FbxImport.cs" "$WROOT/editor/FbxModel.cs" \
  "$WROOT/editor/HafModel.cs" "$WROOT/editor/GlbReader.cs" "$WROOT/editor/HafTransforms.cs" "$WROOT/editor/BlenderNames.cs" "$WROOT/editor/BlenderMesh.cs" "$WROOT/editor/BMesh.cs" \
  "$WROOT/editor/BlenderColor.cs" "$WROOT/editor/BlenderTrig.cs" "$WROOT/editor/BlenderDecimate.cs" "$WROOT/editor/BlenderReduce.cs" "$WROOT/editor/BlenderExport.cs" "$WROOT/editor/BlenderExportTree.cs" "$WROOT/editor/BlenderPolyfill.cs" "$WROOT/editor/BlenderWeld.cs" \
  "$WROOT/editor/VehicleProbe.cs" "$WROOT/editor/VehicleProbe.Visibility.cs" "$WROOT/editor/VehicleProbe.Islands.cs" "$WROOT/editor/VehicleProbe.InsideOut.cs" \
  "$WROOT/editor/VehicleProbe.BlenderWorld.cs" "$WROOT/editor/VehicleProbe.BlenderSkin.cs" "$WROOT/editor/VehicleProbe.CustomNormals.cs" "$WROOT/editor/VehicleProbe.PolygonNormals.cs" "$WROOT/editor/HafNativeKernels.cs" "$WROOT/editor/HafNativeKernels.Collapse.cs" "$WROOT/editor/HafNativeKernels.BMesh.cs" "$WROOT/editor/VehicleProbe.Merge.cs" "$WROOT/editor/VehicleProbe.BlenderArmature.cs" "$WROOT/editor/BlenderEigen.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/fbxmodel.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the FBX model drill did not compile (csc rc=$rc)"; exit 1; fi

# the population: the recipe and model-drive sources (fbx_inputs.py RECIPE rows and every FBX under D:/3DModels that is not a Lab
# output), and the generated fixtures
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
SOURCES=()
while IFS=$'\t' read -r kind path; do [ "$kind" = RECIPE ] && [ -f "$path" ] && SOURCES+=("$path"); done < <(python "$ROOT/tools/fbx-drill/fbx_inputs.py" "$PROJECT" 2>/dev/null | tr -d '\r')
if [ -d /d/3DModels ]; then while IFS= read -r f; do case "$f" in *_Spin*|*_preview*|*_probe*) continue;; esac; skip=0; for s in "${SOURCES[@]}"; do [ "$s" = "$f" ] && skip=1; done; [ $skip = 0 ] && SOURCES+=("$f"); done < <(find /d/3DModels -iname '*.fbx' 2>/dev/null); fi
mkdir -p "$TMPD/fixtures"
mapfile -t FIXTURES < <(python "$ROOT/tools/fbx-drill/fbx_model_fixtures.py" "$WTMP/fixtures" | tr -d '\r')
FILES=("${SOURCES[@]}" "${FIXTURES[@]}")
[ "${#FILES[@]}" -gt 0 ] || { echo "FAIL — no FBX file to drill"; exit 1; }

# Blender: import + export, one process per file (3-7 s each)
PAIRS=(); t0=$(date +%s); n_blender=0
mkdir -p "$TMPD/oracle"
for f in "${FILES[@]}"; do
  out="$TMPD/oracle/$(basename "${f%.*}")_$n_blender.glb"
  "$BLENDER" -b --python "$ROOT/tools/fbx-drill/fbx_to_glb.py" -- "$f" "$out" > "$TMPD/blender_$n_blender.log" 2>&1
  if [ ! -s "$out" ]; then echo "FAIL — Blender wrote nothing for $f:"; grep -iE 'error|Traceback' "$TMPD/blender_$n_blender.log" | head -5; exit 1; fi
  PAIRS+=("$f" "$out"); n_blender=$((n_blender + 1))
done
t1=$(date +%s)
"$TMPD/fbxmodel.exe" "${PAIRS[@]}" > "$TMPD/result.txt" 2>&1; rc=$?
t2=$(date +%s)
grep -E '^(DIFF|ERROR|SKIP) ' "$TMPD/result.txt" | cut -c1-400
RES=$(grep '^RESULT ' "$TMPD/result.txt")
[ "$rc" -eq 0 ] && [ -n "$RES" ] || { echo "FAIL — FBX model drill: $RES (C# exit $rc)"; tail -5 "$TMPD/result.txt"; exit 1; }
n_pass=$(grep -c '^PASS ' "$TMPD/result.txt"); n_skip=$(grep -c '^SKIP ' "$TMPD/result.txt")
echo "PASS — FBX model drill: $n_pass FBX files read by editor/FbxImport.cs + FbxModel.cs equal to Blender 5.1's import + glTF export on every node (name, parent, order, transform bits), mesh (every primitive's positions, normals, UVs, indices, material name) and material name; $n_skip left to Blender by name (bones, deformers, animation, cameras, colours: sub-steps 6b-6d) (Blender $((t1 - t0)) s for ${#FILES[@]} files, C# $((t2 - t1)) s)"
