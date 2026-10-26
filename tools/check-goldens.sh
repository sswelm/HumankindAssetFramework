#!/usr/bin/env bash
# check-goldens.sh — check Bake Tests golden bodies against their recorded checksums (2026-10-12).
#
# On 2026-10-10 the in-Unity Bake Tests were red on eleven goldens (Tools/workshop_golden, Tools/lab_golden in the
# modding project) and the eleven files were then rewritten by hand to the failing values, with no run after them.
# A golden now carries a header - capture time, editor commit, input hashes, a checksum of its lines
# (BakeGoldenRules.Compose) - and this step reads every golden's header against its lines, in seconds, no source
# needed: a checksum that does not match is an edit outside a run and FAILS the push, named. A golden without a
# header is legacy: counted, allowed, and given its header by the first Unity run that matches it. The rows
# themselves run in Unity (and, for the Workshop row, in tools/workshop_golden_drill.sh). This detects edits that
# leave the recorded checksum unchanged; it cannot authenticate the header or prove which process wrote it.
# Exit 0 PASS, 1 FAIL (an edited golden), 3 SKIP (no modding project, no goldens) — SKIP is not PASS.
set -u
UNITY="${UNITY:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
PROJECT="${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
[ -d "$PROJECT/Tools" ] || { echo "SKIP — no modding project at $PROJECT (HAF_UNITY_PROJECT); the goldens were NOT checked"; exit 3; }
DIRS=()
for d in workshop_golden lab_golden; do [ -d "$PROJECT/Tools/$d" ] && DIRS+=("$(cygpath -m "$PROJECT/Tools/$d")"); done
[ "${#DIRS[@]}" -gt 0 ] || { echo "SKIP — no Tools/workshop_golden or Tools/lab_golden under $PROJECT; the goldens were NOT checked"; exit 3; }
for f in "$CSC" "$API/mscorlib.dll"; do
  [ -f "$f" ] || { echo "SKIP — Unity prerequisite not found: $f (set UNITY=<Unity 2021.3 .../Editor/Data>); the goldens were NOT checked"; exit 3; }
done
command -v dotnet >/dev/null 2>&1 || { echo "SKIP — dotnet not on PATH; the goldens were NOT checked"; exit 3; }
NEWTONSOFT=""
for cand in "$ROOT/References/Newtonsoft.Json.dll" "$PROJECT/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "SKIP — Newtonsoft.Json.dll not found (References/ via fetch-refs); the goldens were NOT checked"; exit 3; }

TMPD="$(mktemp -d 2>/dev/null)" || TMPD="$(mktemp -d -p "$ROOT/obj" 2>/dev/null)" || TMPD=""
[ -n "$TMPD" ] && [ -d "$TMPD" ] || { echo "FAIL — no temp directory could be made (TMPDIR=${TMPDIR:-unset}); nothing was checked"; exit 1; }
trap 'rm -rf "$TMPD"' EXIT
cp "$NEWTONSOFT" "$TMPD/Newtonsoft.Json.dll"
WAPI="$(cygpath -m "$API" 2>/dev/null || echo "$API")"; WTMP="$(cygpath -m "$TMPD" 2>/dev/null || echo "$TMPD")"
SRCS=()
for f in WorkshopGateTest.cs BakeGoldenFiles.cs GitStamp.cs EditorRules.cs QuadEstimate.cs GlbDisconnectedParts.cs GlbDisconnectedParts.Compact.cs GlbDisconnectedParts.Occluders.cs GlbDisconnectedParts.Weld.cs GlbDisconnectedParts.Explain.cs HafNativeKernels.cs HafNativeKernels.Column.cs HafNativeKernels.Twin.cs HafModel.cs GlbReader.cs HafTransforms.cs HafModelDiff.cs; do
  SRCS+=("$WROOT/editor/$f")
done
OUTC=$(dotnet "$CSC" -nologo -noconfig -nostdlib -out:"$WTMP/drill.exe" -platform:x64 \
  -r:"$WAPI/mscorlib.dll" -r:"$WAPI/System.dll" -r:"$WAPI/System.Core.dll" -r:"$WAPI/Facades/netstandard.dll" -r:"$WTMP/Newtonsoft.Json.dll" \
  "$WROOT/tools/workshop-golden-drill/WorkshopGoldenDrill.cs" "$WROOT/tools/workshop-golden-drill/Stubs.cs" "${SRCS[@]}" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$TMPD/drill.exe" ]; then echo "$OUTC" | grep -E "error" | head -20; echo "FAIL — the goldens check did not compile (csc rc=$rc)"; exit 1; fi

# ---- self-check: a golden with a header, edited by one character, must read EDITED (the check cannot be a no-op)
PLANT="$TMPD/plant"; mkdir -p "$PLANT"
printf '%s\n' '# golden captured=2026-10-12T00:00 editor=0000000 in.source=0123456789abcdef sum=0000000000000000' 'FUSE parts=1 tris=1->1' > "$PLANT/edited.txt"
"$TMPD/drill.exe" --verify-goldens "$(cygpath -m "$PLANT")" > "$TMPD/plant.out" 2>&1; prc=$?
if [ "$prc" -ne 1 ] || ! grep -q '^EDITED edited.txt' "$TMPD/plant.out"; then cat "$TMPD/plant.out"; echo "FAIL — the goldens check did not flag a planted edit (rc=$prc)"; exit 1; fi

ARGS=()
for d in "${DIRS[@]}"; do ARGS+=(--verify-goldens "$d"); done
"$TMPD/drill.exe" "${ARGS[@]}"; rc=$?
case "$rc" in
  0) echo "PASS — every header golden matches its recorded checksum (self-check: a planted edit is flagged)" ;;
  3) echo "SKIP — no golden to check" ;;
  *) echo "FAIL — a golden was edited outside a run (rc=$rc); restore it, or delete it and let the Unity row re-bless it" ;;
esac
exit "$rc"
