#!/usr/bin/env bash
# max_drill.sh - the native 3ds Max reader (editor/MaxDocument.cs, MaxScene.cs, MaxModel.cs) outside Unity, held to a
# second reader of the same files in Python (tools/max-drill/max_world.py): per mesh part the same name and triangle
# count, and the same area, signed volume, vector area and bounding box in world space (within 2e-4 of the part's
# size); per file the same number of parts whose file holds UVs. Then each file
# is converted and the written .glb is read back and held to the report (parts, triangles, pictures, generator).
# The second reader shares no code with the first and EVERY reading of the format: this catches a slip in one of the two
# implementations, not a misreading both make. NOT compared at all: UV values, normals, materials, pivots, parent links
# (Tests/MaxReaderTests.cs holds those on built files). The door inside Unity: tools/max_unity_drill.sh.
#   usage: tools/max_drill.sh [file.max ...]      (no arguments: every .max under the folders of $HAF_MAX_ROOTS,
#                                                  separated by ';' - default "D:/3DModels;D:/3dmax")
#   also read: HAF_MAX_BUILD (where the executable and out/N.* go; default C:/tmp/haf-build/maxdrill - out/N.* are numbered
#   in the order of the files and are overwritten by the next run), HAF_UNITY_EDITOR (Unity's Editor/Data folder).
# NOT in the push gate (2026-11-21): the population is four files of one 3ds Max generation on the author's disk.
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${HAF_UNITY_EDITOR:-C:/Program Files/Unity 2021.3.1f1/Editor/Data}"
CSC="$UNITY/DotNetSdkRoslyn/csc.dll"; API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
[ -f "$CSC" ] || { echo "FAIL - Unity's compiler not found at $CSC (set HAF_UNITY_EDITOR; the .max reader was NOT drilled)"; exit 2; }
NEWTONSOFT=""
for cand in "$ROOT/References/Newtonsoft.Json.dll" "${HAF_UNITY_PROJECT:-C:/Repo/ENCReload}/Assets/Plugins/Json.Net 11.0.1/Newtonsoft.Json.dll"; do [ -f "$cand" ] && { NEWTONSOFT="$cand"; break; }; done
[ -n "$NEWTONSOFT" ] || { echo "FAIL - Newtonsoft.Json.dll not found (References/ via fetch-refs; the .max reader was NOT drilled)"; exit 2; }
BUILD="${HAF_MAX_BUILD:-C:/tmp/haf-build/maxdrill}"
mkdir -p "$BUILD/out" || exit 2
cp "$NEWTONSOFT" "$BUILD/Newtonsoft.Json.dll"
[ -f "$ROOT/editor/Plugins/x86_64/hafkernels.dll" ] && cp "$ROOT/editor/Plugins/x86_64/hafkernels.dll" "$BUILD/"
W() { cygpath -w "$1" 2>/dev/null || echo "$1"; }
E="$ROOT/editor"
OUT=$(dotnet "$(W "$CSC")" -nologo -noconfig -nostdlib -optimize+ -platform:x64 -out:"$(W "$BUILD/maxdrill.exe")" \
  -r:"$(W "$API/mscorlib.dll")" -r:"$(W "$API/System.dll")" -r:"$(W "$API/System.Core.dll")" -r:"$(W "$API/Facades/netstandard.dll")" -r:"$(W "$API/System.Numerics.dll")" \
  -r:"$(W "$API/System.Drawing.dll")" -r:"$(W "$BUILD/Newtonsoft.Json.dll")" \
  "$(W "$ROOT/tools/max-drill/MaxDrill.cs")" "$(W "$E/MaxDocument.cs")" "$(W "$E/MaxScene.cs")" "$(W "$E/MaxModel.cs")" \
  "$(W "$E/HafModel.cs")" "$(W "$E/GlbReader.cs")" "$(W "$E/GlbWriter.cs")" "$(W "$E/PngCodec.cs")" \
  "$(W "$E/HafNativeKernels.cs")" "$(W "$E/HafNativeKernels.Png.cs")" 2>&1); rc=$?
if [ "$rc" -ne 0 ] || [ ! -s "$BUILD/maxdrill.exe" ]; then echo "$OUT" | grep -E "error" | head -20; echo "FAIL - the .max drill did not compile (csc rc=$rc)"; exit 1; fi

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
[ "${#files[@]}" -gt 0 ] || { echo "FAIL - no .max file to drill (give paths, or set HAF_MAX_ROOTS; the .max reader was NOT drilled)"; exit 2; }
fail=0; n=0
for f in "${files[@]}"; do
  n=$((n + 1)); tag="$BUILD/out/$n"
  "$BUILD/maxdrill.exe" stats "$(W "$f")" > "$tag.cs.txt" 2>"$tag.cs.err"; crc=$?
  python -I "$ROOT/tools/max-drill/max_world.py" "$f" > "$tag.py.txt" 2>"$tag.py.err"; prc=$?
  if [ "$crc" -ne 0 ] || [ "$prc" -ne 0 ]; then echo "FAIL $(basename "$f"): reader exit C# $crc / Python $prc"; head -3 "$tag.cs.txt" "$tag.cs.err" 2>/dev/null; tail -2 "$tag.py.err" 2>/dev/null; fail=1; continue; fi
  res=$(python -I "$ROOT/tools/max-drill/compare_parts.py" "$tag.cs.txt" "$tag.py.txt"); vrc=$?
  echo "$(basename "$f"): $res"
  [ "$vrc" -eq 0 ] || fail=1
  "$BUILD/maxdrill.exe" convert "$(W "$f")" "$(W "$tag.glb")" > "$tag.convert.txt" 2>&1 || { echo "FAIL $(basename "$f"): the conversion"; head -5 "$tag.convert.txt"; fail=1; continue; }
  sed 's/^/    /' "$tag.convert.txt"
done
if [ "$fail" -ne 0 ]; then echo "FAIL - .max drill: differences above ($n file(s))"; exit 1; fi
echo "PASS - .max drill: $n file(s) read by the C# reader equal to the second (Python) reader per part, converted and read back equal to the report"
