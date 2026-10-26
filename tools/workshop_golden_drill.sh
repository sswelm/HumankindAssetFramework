#!/usr/bin/env bash
# workshop_golden_drill.sh — the Bake Tests' Workshop row on the recipe sources, outside Unity (2026-10-12).
#
# The row (editor/WorkshopGateTest.cs: split every multi-island node, tear the smallest, fuse the largest up to
# 150k triangles, in memory) ran only inside the editor, by hand. Its last run was red on seven sources and the
# goldens were then rewritten by hand to the failing values — nothing could say which commit moved a count. This
# compiles the row's own Exercise from editor/ (Unity's Roslyn, the mono 4.7.1 API, as the other drills) with
# tools/workshop-golden-drill/Stubs.cs for the Unity bits, and runs it on every Vehicle Lab recipe source against
# the project's goldens, Tools/workshop_golden. Nothing is written into the project: candidates go to --out (a temp
# dir by default), a missing golden is NOGOLDEN, a child that is not one island is named.
#
#   bash tools/workshop_golden_drill.sh [--only a,b] [--out <dir>]
#   HAF_EDITOR_ROOT=<another checkout>  compiles THAT checkout's editor/ (the drill and stubs stay this one's):
#                                       run at two commits, the diff between them is the commits' doing
#   HAF_UNITY_PROJECT=<project>         the modding project (default C:/Repo/ENCReload)
#
# The runtime is the 64-bit .NET Framework (the exe, directly): Unity's standalone mono.exe is 32-bit and the
# frigate's fuse does not fit it. It is NOT Unity's 64-bit Mono: a count that differs between the two is a float
# precision question (Mono evaluates an uncast float product in double), and a golden is blessed by the Unity row
# only. Exit 0 PASS, 1 FAIL, 3 SKIP (no project, no recipe sources on disk) — SKIP is not PASS.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
EDITOR_ROOT="${HAF_EDITOR_ROOT:-$ROOT}"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
WEDITOR="$(cygpath -m "$EDITOR_ROOT" 2>/dev/null || echo "$EDITOR_ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
for f in "$CSC" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "SKIP — Unity prerequisite not found: $f (set UNITY=<Unity 2021.3 .../Editor/Data>); the Workshop row was NOT run"; exit 3; }
done
command -v dotnet >/dev/null 2>&1 || { echo "SKIP — dotnet not on PATH; the Workshop row was NOT run"; exit 3; }
[ -f "$EDITOR_ROOT/editor/WorkshopGateTest.cs" ] || { echo "FAIL — $EDITOR_ROOT/editor/WorkshopGateTest.cs not found (HAF_EDITOR_ROOT)"; exit 1; }
NEWTONSOFT=""
for cand in "$ROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "SKIP — Newtonsoft.Json.dll not found (References/ via fetch-refs); the Workshop row was NOT run"; exit 3; }

ONLY=""; OUT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --only) ONLY="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    *) echo "unknown argument: $1"; exit 2 ;;
  esac
done

# the editor files the row needs; the ones a checkout does not have yet (Compact.cs and the reader are from
# 2026-10-01/02) are left out, so a checkout from before them compiles too
SRCS=()
for f in WorkshopGateTest.cs BakeGoldenFiles.cs GitStamp.cs EditorRules.cs QuadEstimate.cs GlbDisconnectedParts.cs GlbDisconnectedParts.Compact.cs GlbDisconnectedParts.Occluders.cs GlbDisconnectedParts.Weld.cs GlbDisconnectedParts.Explain.cs HafModel.cs GlbReader.cs HafTransforms.cs HafModelDiff.cs HafNativeKernels.cs HafNativeKernels.Column.cs HafNativeKernels.Twin.cs; do
  [ -f "$EDITOR_ROOT/editor/$f" ] && SRCS+=("$WEDITOR/editor/$f")
done
TMPD="$(mktemp -d 2>/dev/null)" || TMPD="$(mktemp -d -p "$ROOT/obj" 2>/dev/null)" || TMPD=""
[ -n "$TMPD" ] && [ -d "$TMPD" ] || { echo "FAIL — no temp directory could be made (TMPDIR=${TMPDIR:-unset}); nothing was checked"; exit 1; }
trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
[ -f "$EDITOR_ROOT/editor/Plugins/x86_64/hafkernels.dll" ] && cp "$EDITOR_ROOT/editor/Plugins/x86_64/hafkernels.dll" "$TMPD/hafkernels.dll"   # the fuse's native column and twin kernels beside the exe; HAF_NATIVE_KERNELS=0 runs the managed fuse
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
OUTC=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/drill.exe" -platform:x64 \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/workshop-golden-drill/WorkshopGoldenDrill.cs" "$WROOT/tools/workshop-golden-drill/Stubs.cs" "${SRCS[@]}" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/drill.exe" ]; then echo "$OUTC" | grep -E "error" | head -20; echo "FAIL — the Workshop golden drill did not compile (csc rc=$rc; editor from $EDITOR_ROOT)"; exit 1; fi
echo "editor sources: $EDITOR_ROOT ($(git -C "$EDITOR_ROOT" rev-parse --short HEAD 2>/dev/null || echo 'no git'))"

# ---- the sources: every Vehicle Lab recipe's .glb
[ -d "$PROJECT" ] || { echo "SKIP — no modding project at $PROJECT (HAF_UNITY_PROJECT); the Workshop row was NOT run"; exit 3; }
mapfile -t SOURCES < <(python "$ROOT/tools/vehicle-probe-drill/recipe_check.py" --list "$PROJECT" | tr -d '\r' | grep -i '\.glb$' | awk '!seen[tolower($0)]++')
[ "${#SOURCES[@]}" -gt 0 ] || { echo "SKIP — no recipe names a .glb source under $PROJECT; the Workshop row was NOT run"; exit 3; }
GOLDENS="$PROJECT/Tools/workshop_golden"
[ -n "$OUT" ] || OUT="$TMPD/candidates"
PROGRESS="${HAF_PROGRESS:-$ROOT/../PROGRESS.md}"
[ -f "$PROGRESS" ] || PROGRESS=""
ARGS=(--goldens "$(cygpath -m "$GOLDENS")" --out "$(cygpath -m "$OUT")")
[ -z "$PROGRESS" ] || ARGS+=(--progress "$(cygpath -m "$PROGRESS")")
[ -z "$ONLY" ] || ARGS+=(--only "$ONLY")
"$TMPD/drill.exe" "${ARGS[@]}" "${SOURCES[@]}"; rc=$?
echo "candidates: $OUT"
case "$rc" in
  0) echo "PASS — every judged recipe source equals its golden (Workshop row outside Unity; a golden is blessed by the Unity row only)" ;;
  3) echo "SKIP — no recipe source could be judged" ;;
  *) echo "FAIL — a Workshop row differs from its golden or broke an invariant (rc=$rc)" ;;
esac
exit "$rc"
