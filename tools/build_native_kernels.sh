#!/usr/bin/env bash
# build_native_kernels.sh - compiles native/*.cpp into editor/Plugins/x86_64/hafkernels.dll, the native plugin the editor's
# VehicleProbe kernels call (editor/HafNativeKernels.cs). Run it after every change under native/ and commit the DLL: the
# package ships the binary, the gate (tools/native_kernels_drill.sh) proves it equals the managed code and this build.
#
# The arithmetic contract: float32 step for step as the C# (-ffp-contract=off: no fused multiply-add, which would round
# once where the C# and Blender round twice), SSE only (x86-64 has no x87 float), and cosf/sinf resolved from ucrtbase.dll
# at load (hafnormals.cpp), never mingw's own libm. The build is deterministic (no timestamp, fixed image base, a fixed
# random seed, stripped), so the gate can compare the committed DLL with a fresh build byte for byte.
#
# libdeflate (native/libdeflate, vendored, MIT): the PNG encoder's compressor (native/hafpng.cpp). Its compressor side is C,
# compiled with gcc into objects first and linked into the DLL; no -mavx2 and the like, so the baseline x86-64 code path is
# the one built (its compressed bytes do not depend on the CPU: the SIMD in libdeflate serves the checksums and a table
# fill, never the match finding).
#
# Compiler: WinLibs GCC (gcc and g++ on PATH, MinGW-W64 x86_64-ucrt). Fails loud without it.
#   bash tools/build_native_kernels.sh [out.dll]     default: editor/Plugins/x86_64/hafkernels.dll
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/editor/Plugins/x86_64/hafkernels.dll}"
command -v g++ >/dev/null 2>&1 || { echo "FAIL - g++ not on PATH (WinLibs MinGW-W64 x86_64-ucrt); the native kernels were NOT built" >&2; exit 2; }
command -v gcc >/dev/null 2>&1 || { echo "FAIL - gcc not on PATH (WinLibs MinGW-W64 x86_64-ucrt); the native kernels were NOT built" >&2; exit 2; }
g++ -dumpmachine | grep -q x86_64 || { echo "FAIL - g++ is not an x86_64 compiler ($(g++ -dumpmachine)); the native kernels were NOT built" >&2; exit 2; }
mkdir -p "$(dirname "$OUT")"
OBJ="$(mktemp -d "${TMPDIR:-${TEMP:-/tmp}}/haf-native-build.XXXXXXXXXX")"; trap 'rm -rf "$OBJ"' EXIT
LD="$ROOT/native/libdeflate"
for c in adler32 crc32 deflate_compress utils zlib_compress x86/cpu_features; do
  gcc -O2 -frandom-seed=hafkernels -I"$LD" -c "$LD/lib/$c.c" -o "$OBJ/$(basename "$c").o"
done
g++ -O2 -ffp-contract=off -fno-fast-math -fexcess-precision=standard -std=c++17 -frandom-seed=hafkernels -shared -static -s \
  -Wl,--no-insert-timestamp -Wl,--image-base,0x180000000 -I"$ROOT/native" \
  -o "$OUT" "$ROOT/native/hafkernels.cpp" "$ROOT/native/hafnormals.cpp" "$ROOT/native/hafdecimate.cpp" "$ROOT/native/hafcolumn.cpp" "$ROOT/native/haftwin.cpp" "$ROOT/native/hafweld.cpp" "$ROOT/native/hafbmesh.cpp" "$ROOT/native/hafpng.cpp" \
  "$OBJ"/adler32.o "$OBJ"/crc32.o "$OBJ"/deflate_compress.o "$OBJ"/utils.o "$OBJ"/zlib_compress.o "$OBJ"/cpu_features.o
python -I "$ROOT/native/deterministic.py" "$OUT"
echo "built $OUT ($(stat -c %s "$OUT") bytes, $(g++ --version | head -1))"
