#!/usr/bin/env bash
# native_kernels_drill.sh - the probe's native kernels (editor/Plugins/x86_64/hafkernels.dll, built from native/ by
# tools/build_native_kernels.sh, called through editor/HafNativeKernels.cs) held to two claims, 2026-10-21:
#   1. The committed DLL IS the sources: a fresh build of native/*.cpp is byte-equal to it (the build is deterministic). A
#      change under native/ without a rebuild, or a rebuild not committed, fails here. Needs g++ (WinLibs); without it this
#      half is a SKIP that says so, after the second half ran.
#   2. The native path gives the managed path's bytes: ProbePerformance (tools/vehicle-probe-drill) reads and probes every
#      recipe source and every reader fixture twice - once with HAF_NATIVE_KERNELS=0 (the C# twins) and once with the DLL -
#      and hashes every row, bound and preview mesh buffer (positions, normal rays, preview normals, local positions, UVs,
#      triangles, materials). Every hash must agree; the native run must SAY it ran natively (its KERNELS line), and the
#      managed run that it did not - a DLL that failed to load would otherwise compare the managed path with itself.
# The Blender-parity drills (vehicle probe, prep, decimate, deploy, rig_anim, vehicle_rig) copy the DLL beside their exe
# and so run the native path against Blender; this drill is where the managed twins stay drilled against it.
# Prerequisites and SKIP rules as glb_reader_drill.sh (Unity's Roslyn and reference assemblies, dotnet, Newtonsoft).
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "FAIL — Unity prerequisite not found: $f   (set UNITY=<Unity 2021.3 .../Editor/Data>; the native kernels were NOT drilled)"; exit 2; }
done
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (the native kernels were NOT drilled)"; exit 2; }
DLL="$ROOT/editor/Plugins/x86_64/hafkernels.dll"
[ -f "$DLL" ] || { echo "FAIL — $DLL is missing (bash tools/build_native_kernels.sh, then commit it)"; exit 1; }
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs; the native kernels were NOT drilled)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"; WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
cp "$DLL" "$TMPD/hafkernels.dll"

# First-use initialization is independent of the version/trig check and must be safe before any serial dissolve warms it.
# Each executable starts a fresh DLL instance and releases 32 workers together; reuse of one host would hide the race.
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -optimize+ -platform:x64 -out:"$WTMP/first-use.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" \
  "$WROOT/tools/native-kernels-drill/FirstUseDrill.cs" 2>&1); rc=$?
if [ "$rc" -ne 0 ]; then echo "$OUT"; echo "FAIL — native first-use harness did not compile"; exit 1; fi
for trial in $(seq 1 64); do
  if ! "$TMPD/first-use.exe" > "$TMPD/first-use.txt" 2>&1; then cat "$TMPD/first-use.txt"; echo "FAIL — concurrent native first use, fresh process $trial"; exit 1; fi
done
echo "PASS — native dissolve first use: 64 fresh processes, 32 concurrent workers each"

# ---- the harness: ProbePerformance as an x64 exe (the .NET Framework's 64-bit runtime, the DLL's architecture), one run per file
sources=(HafModel GlbReader HafTransforms BlenderNames VehicleProbe VehicleProbe.Visibility VehicleProbe.Islands
  VehicleProbe.InsideOut VehicleProbe.BlenderWorld VehicleProbe.BlenderSkin VehicleProbe.CustomNormals VehicleProbe.Merge
  VehicleProbe.BlenderArmature BlenderEigen BlenderTrig HafNativeKernels BlenderMesh BMesh BlenderColor BlenderDecimate BlenderReduce BlenderExport BlenderExportTree BlenderPrep BlenderMaterialExport GlbWriter HafNativeKernels.Collapse HafNativeKernels.BMesh)
files=("$WROOT/tools/vehicle-probe-drill/ProbePerformance.cs")
for source in "${sources[@]}"; do files+=("$WROOT/editor/$source.cs"); done
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -optimize+ -platform:x64 -out:"$WTMP/kernels.exe" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/System.Numerics.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "${files[@]}" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/kernels.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the native kernels drill did not compile (csc rc=$rc)"; exit 1; fi

# ---- the sources: the reader fixtures (every shape the reader models) and every saved recipe's source (the Lab's real inputs)
mapfile -t FIXTURES < <(python "$ROOT/tools/glb-reader-drill/fixtures.py" "$WTMP/fixtures" | tr -d '\r' | grep -v "bonechild")
[ "${#FIXTURES[@]}" -gt 0 ] || { echo "FAIL — could not write the fixtures"; exit 1; }
PACK=$(ls "$PROJECT"/Assets/Pack/*/pack.json 2>/dev/null | head -1)
RECIPE_SOURCES=(); NOTE="no modding project at $PROJECT: fixtures only"
if [ -n "$PACK" ]; then mapfile -t RECIPE_SOURCES < <(python "$ROOT/tools/vehicle-probe-drill/recipe_check.py" --list "$PROJECT" | tr -d '\r' | grep -iE '\.(glb|gltf)$'); NOTE="${#RECIPE_SOURCES[@]} recipe sources"; fi
FILES=("${FIXTURES[@]}"); [ "${#RECIPE_SOURCES[@]}" -eq 0 ] || FILES+=("${RECIPE_SOURCES[@]}")
mapfile -t FILES < <(printf '%s\n' "${FILES[@]}" | awk '!seen[tolower($0)]++')
missing=0; for f in "${FILES[@]}"; do [ -f "$f" ] || { echo "FAIL — source not found: $f"; missing=1; }; done
[ "$missing" -eq 0 ] || exit 1

t0=$(date +%s)
HAF_PROBE_PERF_RUNS=1 HAF_PROBE_PERF_PREP=1 HAF_NATIVE_KERNELS=0 "$TMPD/kernels.exe" "${FILES[@]}" > "$TMPD/managed.txt" 2>&1; rc_m=$?
HAF_PROBE_PERF_RUNS=1 HAF_PROBE_PERF_PREP=1 "$TMPD/kernels.exe" "${FILES[@]}" > "$TMPD/native.txt" 2>&1; rc_n=$?
for side in managed native; do
  f="$TMPD/$side.txt"
  if [ "$side" = managed ] && [ "$rc_m" -ne 0 ] || [ "$side" = native ] && [ "$rc_n" -ne 0 ]; then tail -5 "$f"; echo "FAIL — the $side run failed (rc managed=$rc_m native=$rc_n)"; exit 1; fi
  grep -q "^BENCHMARK PASS files=${#FILES[@]}\$" "$f" || { tail -5 "$f"; echo "FAIL — the $side run did not probe all ${#FILES[@]} files"; exit 1; }
done
K_M=$(grep -m1 '^KERNELS ' "$TMPD/managed.txt"); K_N=$(grep -m1 '^KERNELS ' "$TMPD/native.txt")
case "$K_M" in "KERNELS managed (HAF_NATIVE_KERNELS=0)") ;; *) echo "FAIL — the managed run did not run the managed twins: '$K_M'"; exit 1;; esac
case "$K_N" in "KERNELS native ("*) ;; *) echo "FAIL — the native run did not run hafkernels.dll: '$K_N' (the DLL did not load, so the drill would compare the managed path with itself)"; exit 1;; esac

# ---- every file's hash, both sides
grep -E '^(MEDIAN|PREP) ' "$TMPD/managed.txt" | awk '{print $2 "/" $1, $NF}' | sort > "$TMPD/h_managed.txt"
grep -E '^(MEDIAN|PREP) ' "$TMPD/native.txt"  | awk '{print $2 "/" $1, $NF}' | sort > "$TMPD/h_native.txt"
n_m=$(wc -l < "$TMPD/h_managed.txt"); n_n=$(wc -l < "$TMPD/h_native.txt")
[ "$n_m" -eq $((2 * ${#FILES[@]})) ] && [ "$n_n" -eq $((2 * ${#FILES[@]})) ] || { echo "FAIL — $n_m managed / $n_n native hashes for ${#FILES[@]} files (a probe and a prep each)"; exit 1; }
if ! diff "$TMPD/h_managed.txt" "$TMPD/h_native.txt" > "$TMPD/h_diff.txt"; then
  grep '^[<>]' "$TMPD/h_diff.txt" | head -10
  echo "FAIL — $(grep -c '^<' "$TMPD/h_diff.txt") of $((2 * ${#FILES[@]})) probe/prep outputs hash differently between the managed twins and hafkernels.dll"
  exit 1
fi
sum_m=$(grep '^MEDIAN ' "$TMPD/managed.txt" | sed -E 's/.*probe_ms=([0-9.]+).*/\1/' | awk '{s+=$1} END {printf "%.0f", s}')
sum_n=$(grep '^MEDIAN ' "$TMPD/native.txt"  | sed -E 's/.*probe_ms=([0-9.]+).*/\1/' | awk '{s+=$1} END {printf "%.0f", s}')
t1=$(date +%s)

# ---- claim 3: the same from UNITY'S MONO, the host the Lab runs in. The .NET Framework and Mono marshal differently: a
#      bool[] with MarshalAs(LPArray, I1) crossed one byte per flag on .NET and FOUR on Mono, every hash above agreed and
#      the Khelandion's and Triconter's differed in the editor (2026-10-21, found after the first push). The harness runs
#      as a library in an isolated project with the DLL under Assets/Plugins/x86_64 (imported by name, as the package's),
#      natively, on every fixture and the three smallest recipe sources; its hashes must equal the managed ones above.
UNITY_EXE="${UNITY%/Data}/Unity.exe"
[ -x "$UNITY_EXE" ] || { echo "FAIL — $UNITY_EXE not found (the native kernels were NOT drilled in Unity's Mono)"; exit 2; }
UFILES=("${FIXTURES[@]}")
[ "${#RECIPE_SOURCES[@]}" -eq 0 ] || mapfile -t -O "${#UFILES[@]}" UFILES < <(ls -Sr "${RECIPE_SOURCES[@]}" | head -3)
mkdir -p "$TMPD/unity/project/Assets/Editor" "$TMPD/unity/project/Assets/Plugins/x86_64" "$TMPD/unity/project/Packages" "$TMPD/unity/project/ProjectSettings"
OUT=$(dotnet "$CSC" -nologo -noconfig -nostdlib -optimize+ -platform:x64 -target:library -out:"$WTMP/unity/project/Assets/Editor/ProbePerformance.dll" \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/System.Numerics.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "${files[@]}" 2>&1); rc=$?
if [ "$rc" -ne 0 ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL — the native kernels drill's Unity harness did not compile (csc rc=$rc)"; exit 1; fi
cp "$TMPD/Newtonsoft.Json.dll" "$TMPD/unity/project/Assets/Editor/"
cp "$DLL" "$ROOT/editor/Plugins/x86_64/hafkernels.dll.meta" "$TMPD/unity/project/Assets/Plugins/x86_64/"
printf '{"dependencies":{}}\n' > "$TMPD/unity/project/Packages/manifest.json"
printf 'm_EditorVersion: 2021.3.1f1\n' > "$TMPD/unity/project/ProjectSettings/ProjectVersion.txt"
HAF_PROBE_PERF_RUNS=1 HAF_PROBE_PERF_PREP=1 "$UNITY_EXE" -batchmode -nographics -quit -projectPath "$WTMP/unity/project" -logFile "$WTMP/unity/unity.log" \
  -executeMethod ProbePerformance.RunUnity -- "${UFILES[@]}" > /dev/null 2>&1; rc_u=$?
grep -q "^BENCHMARK PASS files=${#UFILES[@]}\$" "$TMPD/unity/unity.log" || { grep -E "^(KERNELS|MEASURE|Exception|.*Error)" "$TMPD/unity/unity.log" | head -10; echo "FAIL — the Unity run did not probe all ${#UFILES[@]} files (Unity rc=$rc_u; log in the drill's temp folder)"; exit 1; }
K_U=$(grep -m1 '^KERNELS ' "$TMPD/unity/unity.log")
case "$K_U" in "KERNELS native ("*) ;; *) echo "FAIL — Unity's Mono did not run hafkernels.dll: '$K_U'"; exit 1;; esac
grep -E '^(MEDIAN|PREP) ' "$TMPD/unity/unity.log" | awk '{print $2 "/" $1, $NF}' | sort > "$TMPD/h_unity.txt"
n_u=$(wc -l < "$TMPD/h_unity.txt"); [ "$n_u" -eq $((2 * ${#UFILES[@]})) ] || { echo "FAIL — $n_u Unity hashes for ${#UFILES[@]} files (a probe and a prep each)"; exit 1; }
u_bad=0
while read -r name hash; do
  want=$(awk -v n="$name" '$1 == n {print $2}' "$TMPD/h_managed.txt" | head -1)
  [ "$hash" = "$want" ] || { echo "DIFF $name: Unity native $hash, managed $want"; u_bad=$((u_bad + 1)); }
done < "$TMPD/h_unity.txt"
[ "$u_bad" -eq 0 ] || { echo "FAIL — $u_bad of $((2 * ${#UFILES[@]})) probe/prep outputs hash differently between hafkernels.dll under Unity's Mono and the managed twins"; exit 1; }
t2=$(date +%s)

# ---- the committed DLL against a fresh build (deterministic: tools/build_native_kernels.sh)
if command -v g++ >/dev/null 2>&1; then
  mkdir -p "$TMPD/fresh"
  if ! bash "$ROOT/tools/build_native_kernels.sh" "$TMPD/fresh/hafkernels.dll" > "$TMPD/build.txt" 2>&1; then cat "$TMPD/build.txt" | tail -20; echo "FAIL — native/ does not build"; exit 1; fi
  if ! cmp -s "$TMPD/fresh/hafkernels.dll" "$DLL"; then
    echo "FAIL — editor/Plugins/x86_64/hafkernels.dll is not a build of native/ as committed ($(stat -c %s "$DLL") vs $(stat -c %s "$TMPD/fresh/hafkernels.dll") bytes; bash tools/build_native_kernels.sh, then commit the DLL)"; exit 1
  fi
  echo "PASS — native kernels: ${#FILES[@]} files (${#FIXTURES[@]} fixtures, $NOTE) probe and prep hashes equal between the managed twins and hafkernels.dll on .NET ($((t1 - t0)) s; probe sum managed ${sum_m} ms, native ${sum_n} ms); ${#UFILES[@]} of them equal again from hafkernels.dll under Unity's Mono ($((t2 - t1)) s); the committed DLL equals a fresh build of native/ ($(stat -c %s "$DLL") bytes)"
  exit 0
fi
echo "SKIP — native kernels: ${#FILES[@]} files (${#FIXTURES[@]} fixtures, $NOTE) hash equal between the managed twins and hafkernels.dll on .NET, ${#UFILES[@]} of them again under Unity's Mono (probe sum managed ${sum_m} ms, native ${sum_n} ms), but g++ is not on PATH, so the committed DLL was NOT compared with a build of native/"
exit 3
