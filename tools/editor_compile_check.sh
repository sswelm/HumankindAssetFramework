#!/usr/bin/env bash
# REAL compile check for editor/*.cs — Roslyn against Unity's own reference assemblies.
# The Assembly-CSharp-Editor.csproj route compiles NONE of our scripts (see memory/editor-compile-check).
# Unity itself only compiles when the editor window is focused, so this is the way to verify a script edit
# from the CLI. First run that ever worked end to end: 2026-07-29 (0 errors over ~30 scripts).
#
# The reference set that finally worked, and why each piece is needed:
#   - MonoBleedingEdge/lib/mono/4.7.1-api/{mscorlib,System,System.Core,System.Xml,System.Xml.Linq}.dll  (-nostdlib+)
#   - Managed/UnityEditor.dll + Managed/UnityEngine.dll + every Managed/UnityEngine/*.dll module
#   - Newtonsoft.Json.dll (ModelFactoryWindow uses it) — repo-local References/ copy preferred
#   - 4.7.1-api/Facades/netstandard.dll                          (Newtonsoft is netstandard —
#     NOTE: the NetStandard/ref/2.1.0/netstandard.dll does NOT work with -nostdlib+, it collides with
#     mscorlib and yields ~2200 "predefined type not defined" errors. Use the mono FACADE.)
#
# SOURCES ARE DISCOVERED AT RUN TIME (2026-08-19): the .rsp used to hand-list every source file, and the list
# had silently drifted — GameSoundLabWindow.cs, HafCli.cs and SoundOverrideRegistry.cs were NEVER compile-checked
# by this gate (found via the logging audit). The .rsp now holds only references/options; every
# editor/*.cs is appended fresh on each run, so a new file can never be forgotten again.
#
# THE GATE MUST NEVER PASS WITHOUT COMPILING (2026-09-02): before this date a missing csc made dotnet print
# a "file not found" line, the "error"-grep counted zero, and the script reported PASS having compiled
# NOTHING — on every machine but the author's. Success is now csc's OWN exit code plus a non-empty output
# assembly; an absent prerequisite is a loud FAIL, never a silent pass. The committed .rsp.in is a template
# (@UNITY@/@ROOT@/@NEWTONSOFT@/@OUT@) so no machine-specific path is baked into the repo.
set -u
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
RSP_IN="$(dirname "$0")/editor_compile_check.rsp.in"
[ -f "$RSP_IN" ] || { echo "FAIL — missing template: $RSP_IN"; exit 2; }
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
WUNITY="$(cygpath -m "$UNITY" 2>/dev/null || echo "$UNITY")"

CSC="$UNITY/DotNetSdkRoslyn/csc.dll"
[ -f "$CSC" ] || { echo "FAIL — Unity compiler not found: $CSC   (set UNITY=<Unity 2021.3 .../Editor/Data>; editor compile NOT verified)"; exit 2; }
command -v dotnet >/dev/null 2>&1 || { echo "FAIL — dotnet not on PATH (editor compile NOT verified)"; exit 2; }

# Newtonsoft: prefer the repo-local copy (References/, populated by tools/fetch-refs.ps1); the ENCReload
# Unity-project copy is the fallback for byte-fidelity with the dev project. The template makes both work —
# the old committed .rsp hardcoded ONLY the cross-repo absolute path.
NEWTONSOFT=""
for cand in "$WROOT/References/Newtonsoft.Json.dll" "C:/Repo/ENCReload/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do
  [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }
done
[ -n "$NEWTONSOFT" ] || { echo "FAIL — Newtonsoft.Json.dll not found (References/ via fetch-refs, or the ENCReload plugin copy; editor compile NOT verified)"; exit 2; }

TMPD="$(mktemp -d)"; trap 'rm -rf "$TMPD"' EXIT
OUTDLL="$TMPD/editorcheck.dll"
WOUT="$(cygpath -m "$OUTDLL" 2>/dev/null || echo "$OUTDLL")"
FULL="$TMPD/full.rsp"
sed -e "s|@UNITY@|$WUNITY|g" -e "s|@ROOT@|$WROOT|g" -e "s|@NEWTONSOFT@|$NEWTONSOFT|g" -e "s|@OUT@|$WOUT|g" "$RSP_IN" > "$FULL"
n_src=0
for f in "$ROOT"/editor/*.cs; do printf '"%s/editor/%s"\n' "$WROOT" "$(basename "$f")" >> "$FULL"; n_src=$((n_src+1)); done
[ "$n_src" -gt 0 ] || { echo "FAIL — no editor/*.cs sources found (editor compile NOT verified)"; exit 2; }

OUT=$(dotnet "$CSC" "@$FULL" 2>&1); rc=$?
if [ "$rc" -ne 0 ]; then
  shown=$(echo "$OUT" | grep -E "error|not found|Could not" | head -30)
  # The dotnet host localizes its messages (this project's own machine runs Dutch Windows) — a failure whose
  # text matches none of the patterns above must still show its reason, never a bare exit code.
  [ -n "$shown" ] || shown=$(echo "$OUT" | head -15)
  echo "$shown"
  echo "FAIL — csc exited $rc ($n_src sources)"
  exit 1
fi
# Belt and braces: rc=0 with no assembly on disk means nothing was actually verified.
[ -s "$OUTDLL" ] || { echo "FAIL — csc exited 0 but produced no assembly (editor compile NOT verified)"; exit 1; }
echo "PASS — editor scripts compile ($n_src sources, csc rc=0, newtonsoft=$(basename "$(dirname "$NEWTONSOFT")"))"
