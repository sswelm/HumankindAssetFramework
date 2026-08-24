#!/usr/bin/env bash
# REAL compile check for editor/*.cs — Roslyn against Unity's own reference assemblies.
# The Assembly-CSharp-Editor.csproj route compiles NONE of our scripts (see memory/editor-compile-check).
# Unity itself only compiles when the editor window is focused, so this is the way to verify a script edit
# from the CLI. First run that ever worked end to end: 2026-07-29 (0 errors over ~30 scripts).
#
# The reference set that finally worked, and why each piece is needed:
#   - MonoBleedingEdge/lib/mono/4.7.1-api/{mscorlib,System,System.Core,System.Xml,System.Xml.Linq}.dll  (-nostdlib+)
#   - Managed/UnityEditor.dll + Managed/UnityEngine.dll + every Managed/UnityEngine/*.dll module
#   - Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll        (ModelFactoryWindow uses it)
#   - 4.7.1-api/Facades/netstandard.dll                          (Newtonsoft is netstandard —
#     NOTE: the NetStandard/ref/2.1.0/netstandard.dll does NOT work with -nostdlib+, it collides with
#     mscorlib and yields ~2200 "predefined type not defined" errors. Use the mono FACADE.)
#
# SOURCES ARE DISCOVERED AT RUN TIME (2026-08-19): the .rsp used to hand-list every source file, and the list
# had silently drifted — GameSoundLabWindow.cs, HafCli.cs and SoundOverrideRegistry.cs were NEVER compile-checked
# by this gate (found via the logging audit). The .rsp now holds only references/options; every
# editor/*.cs is appended fresh on each run, so a new file can never be forgotten again.
set -u
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
RSP="$(dirname "$0")/editor_compile_check.rsp"
[ -f "$RSP" ] || { echo "MISSING: $RSP"; exit 2; }
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"
FULL="$(mktemp)"
cat "$RSP" > "$FULL"
for f in "$ROOT"/editor/*.cs; do printf '"%s/editor/%s"\n' "$WROOT" "$(basename "$f")" >> "$FULL"; done
OUT=$(dotnet "$UNITY/DotNetSdkRoslyn/csc.dll" "@$FULL" 2>&1)
rm -f "$FULL"
echo "$OUT" | grep -E "error" | head -30
n=$(echo "$OUT" | grep -c "error")
[ "$n" -eq 0 ] && echo "PASS — editor scripts compile ($RSP)" || echo "FAIL — $n error line(s)"
exit $([ "$n" -eq 0 ] && echo 0 || echo 1)
