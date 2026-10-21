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
# Compiler: WinLibs GCC (g++ on PATH, MinGW-W64 x86_64-ucrt). Fails loud without it.
#   bash tools/build_native_kernels.sh [out.dll]     default: editor/Plugins/x86_64/hafkernels.dll
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/editor/Plugins/x86_64/hafkernels.dll}"
command -v g++ >/dev/null 2>&1 || { echo "FAIL - g++ not on PATH (WinLibs MinGW-W64 x86_64-ucrt); the native kernels were NOT built" >&2; exit 2; }
g++ -dumpmachine | grep -q x86_64 || { echo "FAIL - g++ is not an x86_64 compiler ($(g++ -dumpmachine)); the native kernels were NOT built" >&2; exit 2; }
mkdir -p "$(dirname "$OUT")"
g++ -O2 -ffp-contract=off -fno-fast-math -fexcess-precision=standard -std=c++17 -frandom-seed=hafkernels -shared -static -s \
  -Wl,--no-insert-timestamp -Wl,--image-base,0x180000000 \
  -o "$OUT" "$ROOT/native/hafkernels.cpp" "$ROOT/native/hafnormals.cpp"
python -I "$ROOT/native/deterministic.py" "$OUT"
echo "built $OUT ($(stat -c %s "$OUT") bytes, $(g++ --version | head -1))"
