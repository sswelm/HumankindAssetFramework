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

# ---- the harness: ProbePerformance as an x64 exe (the .NET Framework's 64-bit runtime, the DLL's architecture), one run per file
sources=(HafModel GlbReader HafTransforms BlenderNames VehicleProbe VehicleProbe.Visibility VehicleProbe.Islands
  VehicleProbe.InsideOut VehicleProbe.BlenderWorld VehicleProbe.BlenderSkin VehicleProbe.CustomNormals VehicleProbe.Merge
  VehicleProbe.BlenderArmature BlenderEigen BlenderTrig HafNativeKernels)
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
HAF_PROBE_PERF_RUNS=1 HAF_NATIVE_KERNELS=0 "$TMPD/kernels.exe" "${FILES[@]}" > "$TMPD/managed.txt" 2>&1; rc_m=$?
HAF_PROBE_PERF_RUNS=1 "$TMPD/kernels.exe" "${FILES[@]}" > "$TMPD/native.txt" 2>&1; rc_n=$?
for side in managed native; do
  f="$TMPD/$side.txt"
  if [ "$side" = managed ] && [ "$rc_m" -ne 0 ] || [ "$side" = native ] && [ "$rc_n" -ne 0 ]; then tail -5 "$f"; echo "FAIL — the $side run failed (rc managed=$rc_m native=$rc_n)"; exit 1; fi
  grep -q "^BENCHMARK PASS files=${#FILES[@]}\$" "$f" || { tail -5 "$f"; echo "FAIL — the $side run did not probe all ${#FILES[@]} files"; exit 1; }
done
K_M=$(grep -m1 '^KERNELS ' "$TMPD/managed.txt"); K_N=$(grep -m1 '^KERNELS ' "$TMPD/native.txt")
case "$K_M" in "KERNELS managed (HAF_NATIVE_KERNELS=0)") ;; *) echo "FAIL — the managed run did not run the managed twins: '$K_M'"; exit 1;; esac
case "$K_N" in "KERNELS native ("*) ;; *) echo "FAIL — the native run did not run hafkernels.dll: '$K_N' (the DLL did not load, so the drill would compare the managed path with itself)"; exit 1;; esac

# ---- every file's hash, both sides
grep '^MEDIAN ' "$TMPD/managed.txt" | awk '{print $2, $NF}' | sort > "$TMPD/h_managed.txt"
grep '^MEDIAN ' "$TMPD/native.txt"  | awk '{print $2, $NF}' | sort > "$TMPD/h_native.txt"
n_m=$(wc -l < "$TMPD/h_managed.txt"); n_n=$(wc -l < "$TMPD/h_native.txt")
[ "$n_m" -eq "${#FILES[@]}" ] && [ "$n_n" -eq "${#FILES[@]}" ] || { echo "FAIL — $n_m managed / $n_n native hashes for ${#FILES[@]} files"; exit 1; }
if ! diff "$TMPD/h_managed.txt" "$TMPD/h_native.txt" > "$TMPD/h_diff.txt"; then
  grep '^[<>]' "$TMPD/h_diff.txt" | head -10
  echo "FAIL — $(grep -c '^<' "$TMPD/h_diff.txt") of ${#FILES[@]} files hash differently between the managed twins and hafkernels.dll"
  exit 1
fi
sum_m=$(grep '^MEDIAN ' "$TMPD/managed.txt" | sed -E 's/.*probe_ms=([0-9.]+).*/\1/' | awk '{s+=$1} END {printf "%.0f", s}')
sum_n=$(grep '^MEDIAN ' "$TMPD/native.txt"  | sed -E 's/.*probe_ms=([0-9.]+).*/\1/' | awk '{s+=$1} END {printf "%.0f", s}')
t1=$(date +%s)

# ---- the committed DLL against a fresh build (deterministic: tools/build_native_kernels.sh)
if command -v g++ >/dev/null 2>&1; then
  mkdir -p "$TMPD/fresh"
  if ! bash "$ROOT/tools/build_native_kernels.sh" "$TMPD/fresh/hafkernels.dll" > "$TMPD/build.txt" 2>&1; then cat "$TMPD/build.txt" | tail -20; echo "FAIL — native/ does not build"; exit 1; fi
  if ! cmp -s "$TMPD/fresh/hafkernels.dll" "$DLL"; then
    echo "FAIL — editor/Plugins/x86_64/hafkernels.dll is not a build of native/ as committed ($(stat -c %s "$DLL") vs $(stat -c %s "$TMPD/fresh/hafkernels.dll") bytes; bash tools/build_native_kernels.sh, then commit the DLL)"; exit 1
  fi
  echo "PASS — native kernels: ${#FILES[@]} files (${#FIXTURES[@]} fixtures, $NOTE) hash equal between the managed twins and hafkernels.dll; probe sum managed ${sum_m} ms, native ${sum_n} ms; the committed DLL equals a fresh build of native/ ($(stat -c %s "$DLL") bytes); $((t1 - t0)) s"
  exit 0
fi
echo "SKIP — native kernels: ${#FILES[@]} files (${#FIXTURES[@]} fixtures, $NOTE) hash equal between the managed twins and hafkernels.dll (probe sum managed ${sum_m} ms, native ${sum_n} ms), but g++ is not on PATH, so the committed DLL was NOT compared with a build of native/"
exit 3
