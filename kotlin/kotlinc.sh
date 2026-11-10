#!/bin/bash
# kotlinc.sh ARGS... - run the Kotlin compiler: `kotlinc` if one is on the PATH, otherwise the Kotlin 2.0.21 compiler
# that ships inside Gradle 8.14.3 (KOTLIN_LIB, default /opt/gradle/lib: kotlin-compiler-embeddable-2.0.21.jar and the
# jars it needs), with a 3 GB heap and the serial collector so that it keeps to one core and leaves memory for a gate
# run beside it. Prints the standard library's path when called as `kotlinc.sh --stdlib`.
# Written by Claude (Anthropic) for Robert Dickinson's SWMM6 ports; see README.md.
L=${KOTLIN_LIB:-/opt/gradle/lib}
if command -v kotlinc >/dev/null 2>&1; then
	STDLIB=$(dirname "$(readlink -f "$(command -v kotlinc)")")/../lib/kotlin-stdlib.jar
	[ "$1" = --stdlib ] && { echo "$STDLIB"; exit 0; }
	exec env JAVA_OPTS="-Xmx3g -Xss32m -XX:+UseSerialGC" kotlinc "$@"
fi
KV=$(ls "$L"/kotlin-compiler-embeddable-*.jar | sed 's/.*embeddable-\(.*\)\.jar/\1/' | sort -V | tail -1)
STDLIB=$L/kotlin-stdlib-$KV.jar
[ "$1" = --stdlib ] && { echo "$STDLIB"; exit 0; }
CP=$L/kotlin-compiler-embeddable-$KV.jar:$STDLIB:$L/kotlin-script-runtime-$KV.jar:$L/kotlin-reflect-$KV.jar
CP=$CP:$L/kotlin-daemon-embeddable-$KV.jar:$(ls $L/trove4j-*.jar | head -1):$(ls $L/annotations-*.jar | head -1)
CP=$CP:$(ls $L/kotlinx-coroutines-core-jvm-*.jar | head -1)
exec env -u JAVA_TOOL_OPTIONS java -Xmx3g -Xss32m -XX:+UseSerialGC -cp "$CP" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler \
	-no-stdlib -no-reflect -cp "$STDLIB${KT_CP:+:$KT_CP}" "$@"
