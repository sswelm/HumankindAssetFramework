#!/usr/bin/env bash
# probe_vis.sh - the Vehicle Lab's probe over the advisor corpus's files (2026-11-09, Workshop-Advisor-Plan.md Phase 1).
# tools/advisor/ProbeVis.cs is compiled with Unity's Roslyn against the editor sources (the compile list is the vehicle
# probe drill's, tools/vehicle_probe_drill.sh - a new cross-file reference under editor/ must be added to both) and run as
# a 64-BIT process (the exe directly, on the .NET Framework's runtime, like the drill: the custom-normal path calls the
# C runtime's cosf/sinf, which Unity's 32-bit stand-alone Mono has not; a 398 MB source needs the address space too).
# Every file named in the list is probed and its PART rows written to the table corpus.py joins with --probe.
#   tools/advisor/probe_vis.sh <files.txt> <out.tsv>
# <files.txt>: one GLB path per line - what `python tools/advisor/corpus.py --list` prints.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
[ $# -eq 2 ] || { echo "usage: $0 <files.txt> <out.tsv>"; exit 2; }
LIST="$1"; OUT="$2"
[ -s "$LIST" ] || { echo "FAIL — no file list at $LIST"; exit 2; }
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH"; exit 2; }
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs)"; exit 2; }

TMPD="$(mktemp -d "${TMPDIR:-${TEMP:-/tmp}}/haf-probe-vis.XXXXXXXXXX")"; trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
cp "$ROOT/editor/Plugins/x86_64/hafkernels.dll" "$TMPD/hafkernels.dll"   # the native kernels beside the exe; absent, the managed twins run
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
COMPILE=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/probevis.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WAPI/System.Numerics.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/advisor/ProbeVis.cs" "$WROOT/editor/HafModel.cs" "$WROOT/editor/GlbReader.cs" "$WROOT/editor/HafModelLoader.cs" "$WROOT/editor/FbxDocument.cs" "$WROOT/editor/FbxScene.cs" "$WROOT/editor/FbxAnimation.cs" "$WROOT/editor/FbxTriangulate.cs" "$WROOT/editor/FbxImport.cs" "$WROOT/editor/FbxModel.cs" "$WROOT/editor/FbxMaterials.cs" "$WROOT/editor/PngCodec.cs" "$WROOT/editor/FbxArmature.cs" "$WROOT/editor/FbxActions.cs" "$WROOT/editor/BlenderExport.cs" "$WROOT/editor/BlenderExportTree.cs" "$WROOT/editor/BlenderPolyfill.cs" "$WROOT/editor/BlenderWeld.cs" "$WROOT/editor/BlenderColor.cs" "$WROOT/editor/BlenderMesh.cs" "$WROOT/editor/BMesh.cs" "$WROOT/editor/BlenderDecimate.cs" "$WROOT/editor/BlenderReduce.cs" "$WROOT/editor/HafNativeKernels.Collapse.cs" "$WROOT/editor/HafNativeKernels.BMesh.cs" "$WROOT/editor/VehicleProbe.PolygonNormals.cs" "$WROOT/editor/HafTransforms.cs" "$WROOT/editor/BlenderNames.cs" "$WROOT/editor/VehicleProbe.cs" "$WROOT/editor/VehicleProbe.Visibility.cs" "$WROOT/editor/VehicleProbe.Islands.cs" "$WROOT/editor/VehicleProbe.InsideOut.cs" "$WROOT/editor/VehicleProbe.BlenderWorld.cs" "$WROOT/editor/VehicleProbe.BlenderSkin.cs" "$WROOT/editor/VehicleProbe.CustomNormals.cs" "$WROOT/editor/HafNativeKernels.cs" "$WROOT/editor/HafNativeKernels.Png.cs" "$WROOT/editor/VehicleProbe.Merge.cs" "$WROOT/editor/VehicleProbe.BlenderArmature.cs" "$WROOT/editor/VehicleProbe.BlenderPose.cs" "$WROOT/editor/BlenderPosedState.cs" "$WROOT/editor/BlenderEigen.cs" "$WROOT/editor/BlenderTrig.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/probevis.exe" ]; then echo "$COMPILE" | grep -E "error" | head -20; echo "FAIL — ProbeVis did not compile (csc rc=$rc)"; exit 1; fi

WLIST="$(cygpath -m "$LIST" 2>/dev/null || echo "$LIST")"
"$TMPD/probevis.exe" "@$WLIST" > "$TMPD/raw.tsv"; rc=$?
LC_ALL=C sed 's/\xEF\xBB\xBF//g' "$TMPD/raw.tsv" | tr -d '\r' > "$OUT"
n_files=$(grep -c '^FILE' "$OUT"); n_parts=$(grep -c '^PART' "$OUT"); n_fail=$(grep -c '^FAIL' "$OUT"); n_left=$(grep -c '^LEFT' "$OUT")
grep -E '^(FAIL|LEFT)' "$OUT"
echo "probe_vis: $n_files file(s) probed, $n_parts part row(s), $n_left left, $n_fail failed -> $OUT"
[ "$rc" -eq 0 ] && [ "$n_fail" -eq 0 ]
