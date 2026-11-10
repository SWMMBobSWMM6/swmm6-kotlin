#!/bin/bash
# run_libm_tests.sh [N] [N2]
# Check the Kotlin translations of glibc's math routines (src/cpp/Glibm.kt, Glibm2.kt and their runtimes GlibmRt.kt,
# Glibm2Rt.kt) bit for bit against the system libm (glibc 2.39, the library the C++ SWMM6 engine calls), by the method
# of tools/libm/test/run_tests.sh:
#   set 1: N random arguments per function over the ranges SWMM uses plus special values (tools/libm/test/mkvectors.c)
#     and 100,000 edge cases per function (mkedge.c): exp, log, log10, pow, sin, cos, tan, asin, acos, atan and fma;
#   set 2: N2 random arguments per function over wide ranges and every branch threshold, +-0, +-inf, subnormals and
#     NaNs of both signs with several payloads, every pair of special values for atan2 and hypot (mkvectors2.c):
#     cbrt, hypot, atan2, cosh, sinh, tanh, log2, expm1, log1p, frexp, ldexp; a NaN must match in sign and payload;
#     run twice, with the JIT and with -Xint (the interpreter).
# The vectors (about 0.5 GB at the defaults) go to W (default /dev/shm/kt6_libm_test) and are deleted at the end.
# Written by Claude (Anthropic) for Robert Dickinson's SWMM6 ports; see ../README.md.
set -e
HERE=$(cd "$(dirname "$0")" && pwd); K=$(cd "$HERE/.." && pwd); ROOT=$(cd "$K/.." && pwd)
N=${1:-1000000}; N2=${2:-1000000}; W=${W:-/dev/shm/kt6_libm_test}; rm -rf "$W"; mkdir -p "$W"
T=$ROOT/tools/libm/test
gcc -O2 -o "$W/mkvectors" "$T/mkvectors.c" -lm
gcc -O2 -o "$W/mkedge" "$T/mkedge.c" -lm
gcc -O2 -fno-builtin -o "$W/mkvectors2" "$T/mkvectors2.c" -lm
"$W/mkvectors" "$N" "$W/vectors.bin" > /dev/null
"$W/mkedge" 100000 "$W/edge.bin" > /dev/null
"$W/mkvectors2" "$N2" "$W/vectors2.bin" > /dev/null
"$K/kotlinc.sh" -jvm-target 17 -nowarn -d "$W/classes" "$K/src/cpp/Glibm.kt" "$K/src/cpp/GlibmRt.kt" "$K/src/cpp/Glibm2.kt" \
    "$K/src/cpp/Glibm2Rt.kt" "$HERE/Check.kt" "$HERE/Check2.kt" 2>&1 | grep -v '^Picked up' || true
STDLIB=$("$K/kotlinc.sh" --stdlib)
J="env -u JAVA_TOOL_OPTIONS java -cp $W/classes:$STDLIB"
echo "== Kotlin, set 1: vectors.bin (N = $N per function)"; $J cpp.CheckKt "$W/vectors.bin"
echo "== Kotlin, set 1: edge.bin"; $J cpp.CheckKt "$W/edge.bin"
echo "== Kotlin, set 2: vectors2.bin (N2 = $N2 per function)"; $J cpp.Check2 "$W/vectors2.bin"
echo "== Kotlin, set 2, -Xint (interpreter)"; env -u JAVA_TOOL_OPTIONS java -Xint -cp "$W/classes:$STDLIB" cpp.Check2 "$W/vectors2.bin"
rm -rf "$W"
