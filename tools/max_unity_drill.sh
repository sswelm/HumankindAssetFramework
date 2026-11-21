#!/usr/bin/env bash
# max_unity_drill.sh - the .max door INSIDE a Unity editor: MaxImport.Picked / Convert on copies of real .max files in an
# isolated temporary project, with Unity's own image decoder, held to the .glb the stand-alone drill's executable writes
# from the same copy with System.Drawing's decoder (geometry bit for bit, pictures within a decoder's rounding).
#   usage: tools/max_unity_drill.sh [file.max ...]   (no arguments: every .max under the folders of $HAF_MAX_ROOTS,
#                                                     separated by ';' - default "D:/3DModels;D:/3dmax")
#   also read: UNITY (Unity's Editor/Data folder), UNITY_EDITOR (Unity.exe), HAF_MAX_UNITY_REF (where the reference
#   executable is built; default C:/tmp/haf-build/maxunity-ref).
# The temporary project is removed after a pass and kept after a failure (its path is printed either way).
# The originals are never written to: each file's folder is copied into the temporary directory first.
# NOT in the push gate (the population lives on the author's disk). NOT covered: the dialogs' text and the IMGUI layout
# (a modal dialog answers itself in batch mode).
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HARNESS="$ROOT/tools/max-drill/UnityMaxImportDrill.cs"
UNITY="${UNITY:-/c/Program Files/Unity 2021.3.1f1/Editor/Data}"
EDITOR="${UNITY_EDITOR:-$UNITY/../Unity.exe}"
[ -f "$EDITOR" ] || { echo 'SKIP - Unity missing; the .max door was NOT drilled in Unity'; exit 3; }
TASK_TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)" || exit 2
TMPD="$(mktemp -d "$TASK_TEMP_ROOT/haf-max-unity.XXXXXXXXXX")" || exit 2
cleanup() {
  local result=$?
  if [ "$result" -ne 0 ]; then echo "EVIDENCE retained $TMPD"; return 0; fi
  [ -d "$TMPD" ] || return 0
  local resolved="$(cd "$TMPD" && pwd -P)"
  case "$resolved" in
    "$TASK_TEMP_ROOT"/haf-max-unity.*)
      local attempt
      for ((attempt=0; attempt<30; attempt++)); do rm -rf -- "$resolved" 2>/dev/null && return 0; sleep 1; done
      echo "FAIL - could not clean the runner project: $resolved" >&2; return 1;;
    *) echo "FAIL - refusing cleanup outside the runner temp root: $resolved"; return 1;;
  esac
}
trap cleanup EXIT
echo "EVIDENCE $TMPD (removed after a pass, kept after a failure)"

files=()
if [ "$#" -gt 0 ]; then files=("$@"); else
  IFS=';' read -r -a roots <<< "${HAF_MAX_ROOTS:-D:/3DModels;D:/3dmax}"
  for r in "${roots[@]}"; do
    [ -n "$r" ] || continue
    found=0
    while IFS= read -r f; do files+=("$f"); found=$((found + 1)); done < <([ -d "$r" ] && find "$r" -iname "*.max" 2>/dev/null | sort)
    [ "$found" -gt 0 ] || echo "NOTE - no .max under '$r' (a folder of HAF_MAX_ROOTS that gave nothing)"
  done
fi
[ "${#files[@]}" -gt 0 ] || { echo "FAIL - no .max file to drill (give paths, or set HAF_MAX_ROOTS; the .max door was NOT drilled in Unity)"; exit 2; }

# the reference: the stand-alone drill's executable (another image decoder). Built under C:/tmp/haf-build, not in the
# temporary directory: on the author's machine an executable compiled anywhere else can be taken away by the antivirus.
REF="${HAF_MAX_UNITY_REF:-C:/tmp/haf-build/maxunity-ref}"
HAF_UNITY_EDITOR="$UNITY" HAF_MAX_BUILD="$REF" bash "$ROOT/tools/max_drill.sh" "${files[@]}" > "$TMPD/ref.log" 2>&1
[ -s "$REF/maxdrill.exe" ] || { tail -5 "$TMPD/ref.log"; echo 'FAIL - the reference executable did not build'; exit 1; }

WROOT="$(cygpath -m "$ROOT")"; WTMP="$(cygpath -m "$TMPD")"; WUNITY="$(cygpath -m "$UNITY")"
mkdir -p "$TMPD/work" "$TMPD/project/Assets/Editor" "$TMPD/project/Assets/Plugins" "$TMPD/project/Packages" "$TMPD/project/ProjectSettings"
: > "$TMPD/list.txt"; n=0
for f in "${files[@]}"; do
  n=$((n + 1)); mkdir -p "$TMPD/work/$n"
  # the file's whole folder is copied (its pictures lie beside it or one folder down): a .max in a library's root would
  # copy the library, so a folder past 500 MB is refused instead
  mb=$(du -sm "$(dirname "$f")" 2>/dev/null | cut -f1)
  [ -n "$mb" ] && [ "$mb" -le 500 ] || { echo "FAIL - the folder of $(basename "$f") holds ${mb:-?} MB; give a .max that sits in its own folder (the .max door was NOT drilled in Unity)"; exit 2; }
  cp -r "$(dirname "$f")/." "$TMPD/work/$n/" || { echo "FAIL - could not copy $(dirname "$f")"; exit 2; }
  copy="$TMPD/work/$n/$(basename "$f")"
  "$REF/maxdrill.exe" convert "$(cygpath -w "$copy")" "$(cygpath -w "$TMPD/work/$n.reference.glb")" > "$TMPD/work/$n.reference.txt" 2>&1 \
    || { head -5 "$TMPD/work/$n.reference.txt"; echo "FAIL - the reference conversion of $(basename "$f")"; exit 1; }
  printf '%s|%s\n' "$(cygpath -m "$copy")" "$WTMP/work/$n.reference.glb" >> "$TMPD/list.txt"
done

printf '%s\n' '{"dependencies":{}}' > "$TMPD/project/Packages/manifest.json"
printf '%s\n' 'm_EditorVersion: 2021.3.1f1' > "$TMPD/project/ProjectSettings/ProjectVersion.txt"
NEWTONSOFT="$ROOT/References/Newtonsoft.Json.dll"
[ -f "$NEWTONSOFT" ] || { echo 'FAIL - Newtonsoft.Json missing (References/ via fetch-refs)'; exit 2; }
cp "$NEWTONSOFT" "$TMPD/project/Assets/Plugins/Newtonsoft.Json.dll"
cp "$ROOT/editor/Plugins/Haf.Schema.dll" "$TMPD/project/Assets/Plugins/Haf.Schema.dll"
WJSON="$(cygpath -m "$NEWTONSOFT")"
sed -e "s|@UNITY@|$WUNITY|g" -e "s|@ROOT@|$WROOT|g" -e "s|@NEWTONSOFT@|$WJSON|g" -e "s|@OUT@|$WTMP/project/Assets/Editor/HafMaxDoorTests.dll|g" "$ROOT/tools/editor_compile_check.rsp.in" > "$TMPD/compile.rsp"
for f in "$ROOT"/editor/*.cs; do printf '"%s/editor/%s"\n' "$WROOT" "${f##*/}" >> "$TMPD/compile.rsp"; done
printf '"%s"\n' "$(cygpath -m "$HARNESS")" >> "$TMPD/compile.rsp"
dotnet "$UNITY/DotNetSdkRoslyn/csc.dll" "@$WTMP/compile.rsp" > "$TMPD/compile.log" 2>&1
rc=$?; [ "$rc" -eq 0 ] || { grep -E 'error' "$TMPD/compile.log" | head -20; echo 'FAIL - the .max door harness did not compile'; exit 1; }
"$EDITOR" -batchmode -nographics -projectPath "$WTMP/project" -executeMethod UnityMaxImportDrill.Run -hafMaxOutput "$WTMP/result.txt" -hafMaxList "$WTMP/list.txt" -hafMaxWork "$WTMP/work" -logFile "$WTMP/unity.log"
rc=$?
[ -f "$TMPD/result.txt" ] || { tail -30 "$TMPD/unity.log"; echo 'FAIL - the .max door harness did not run'; exit 1; }
cat "$TMPD/result.txt"
[ "$rc" -eq 0 ] && grep -q '^TOTAL failed 0' "$TMPD/result.txt" || { echo "FAIL - the .max door under Unity (rc=$rc)"; exit 1; }
echo "PASS - the .max door under Unity: $n file(s) converted with Unity's decoder, equal to the reference"
