#!/bin/bash
# build.sh - compile the Kotlin SWMM6 engine and its runtime into bin/openswmm.jar (Main-Class Main, with the Kotlin
# standard library inside, so `java -jar` needs nothing else; runswmm6 runs it).
#   ./build.sh            the runtime (src/cpp), the generated engine (gen/engine) and the runner (src/main)
#   ./build.sh runtime    the runtime alone (a quick compile check; no jar)
# The compiler is kotlinc.sh's (Kotlin 2.0.21 from Gradle's jars unless kotlinc is on the PATH), with a 3 GB heap and
# the serial collector so that it keeps to one core while a gate run may be using the other. The classes are built in
# a temporary folder (KT_BUILD_TMP, default /dev/shm) and only the jar is kept.
# Written by Claude (Anthropic) for Robert Dickinson's SWMM6 ports; see README.md.
set -e
cd "$(dirname "$0")"
T=$(mktemp -d "${KT_BUILD_TMP:-/dev/shm}/kt6build.XXXXXX"); trap 'rm -rf "$T"' EXIT
mkdir -p "$T/classes" bin
list=$T/files.txt
if [ "$1" = runtime ]; then ls src/cpp/*.kt > "$list"; else ls src/cpp/*.kt gen/engine/*.kt src/main/*.kt > "$list"; fi
echo "kotlinc: $(wc -l < "$list") files"
set +e
./kotlinc.sh -jvm-target 17 -nowarn -Xno-call-assertions -Xno-param-assertions -Xno-receiver-assertions -d "$T/classes" @"$list" 2>&1 | grep -v '^Picked up' > "$T/log.txt"
rc=${PIPESTATUS[0]}
set -e
grep -c 'error:' "$T/log.txt" > /dev/null && { head -100 "$T/log.txt"; echo "errors: $(grep -c 'error:' "$T/log.txt")"; }
[ "$rc" = 0 ] || { echo "build failed (kotlinc exit $rc)"; cp "$T/log.txt" "${KT_BUILD_LOG:-/dev/shm/kt6_build_errors.txt}"; exit 1; }
if [ "$1" = runtime ]; then echo "runtime compiled"; exit 0; fi
STDLIB=$(./kotlinc.sh --stdlib)
(cd "$T/classes" && unzip -qo "$STDLIB" -x 'META-INF/*' && rm -rf META-INF)
rm -f bin/openswmm.jar
env -u JAVA_TOOL_OPTIONS jar --create --file bin/openswmm.jar --main-class Main -C "$T/classes" .
echo "built bin/openswmm.jar ($(du -h bin/openswmm.jar | cut -f1))"
