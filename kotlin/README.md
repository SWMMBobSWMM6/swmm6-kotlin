# The OpenSWMM 6 1D engine in Kotlin, byte for byte

A translation of the 1D engine of OpenSWMM 6 (HydroCouple/openswmm.engine, branch `swmm6_rel`, commit 88329d6) into
Kotlin, held to the same output as the C++ engine: the same binary `.out` file and the same `.rpt` report, apart from
the three clock lines. It is not SWMM6 and not a HydroCouple or EPA release; it is a translation checked against
SWMM6.

## Result

**480 of 480 models byte-identical** against the C++ reference engine built from the same commit and run on one
thread (`OMP_NUM_THREADS=1`), on the 480-model gate set the SWMM5 ports were checked on, drawn from
1729-SWMM5-Models-2030. Gate runs `kt6_a` (239 of 239) and `kt6_b` (241 of 241), the corpus in two halves
(`gate/ids_corpus_a.txt`, `_b`); 21.2 and 22.1 minutes, a median of 2.96 s per model.

**452 of 452 extra decks byte-identical**: SWMM6's own tests (the input files in the upstream repository's `tests`
folder at 88329d6) and the synthetic decks of swmm6-rust's gate (the finite-volume solver, its structure coupling,
the newer hydrology and the input checks). Gate runs `kt6_x_a` (225 of 225) and `kt6_x_b` (227 of 227); 11.8 and
7.8 minutes, a median of 0.78 s per deck.

**The independent rerun: 480 of 480.** A clean copy of commit 4f866e7 (`git archive`) where the translator was
rebuilt, the engine regenerated from `cs/gen` (1,258 files, byte-identical to the committed ones), the runtime
translated again from `java/src/cpp` (185 files, byte-identical) and everything compiled again
(`gate/kt6_v_build.txt`), then gated: runs `kt6_v_a` (239 of 239) and `kt6_v_b` (241 of 241); 10.4 and 10.6
minutes, a median of 1.43 s per model (the machine was less busy than during kt6_a and kt6_b).

**The main session's check: 48 of 48 and 12 of 12.** A clean `git clone` of commit 280b0e2 (in memory), where the
translator was rebuilt, the engine regenerated from `cs/gen` and the runtime from `java/src/cpp` (no file changed:
`git status` empty) and the jar built with `build.sh`, then gated: run `kt6_main` on 45 corpus models drawn at random
(`random.Random(20261110)`) plus `Hydrology/save_rtk_long.inp`, `one_sub_one_rdii_node.inp` and
`Use_SeasonalRDIIn.inp`, the decks that SAVE and USE `cho.txt` (48 of 48; 380 s), and run `kt6_main_x` on 12 extra
decks drawn the same way (12 of 12; 26 s). Ids in `gate/ids_main_check.txt` and `gate/ids_main_check_extra.txt`.
Neither this check nor the rerun regenerated the math at first (the GIMPLE dumps `make_glibm.sh` reads were no
longer on the machine). The main session did that later the same day, in a second clean clone (commit 296062e):
glibc 2.39's source compiled to GIMPLE again with `tools/libm/build_glibm.sh` (compiled only; nothing of glibc is
run), its tables read with `extract_tables.py`, then `kotlin/make_glibm.sh`: `Glibm.kt` and `Glibm2.kt` byte-identical to the
committed files. The math's own tests are in `results/libm_tests.txt`. `Hydrology/cho.txt` was unchanged afterwards.

Every result, with the SHA-256 of each `.out` and `.rpt`, is in `results/NAME.json`, with the run's log beside it.

A model passes when its `.out` is byte-identical to the reference's (when the reference writes one), its `.rpt` is
identical after removing the lines "Analysis begun", "Analysis ended" and "Total elapsed time", and its exit code is
the same. The gate runs every model through `runswmm6_saves.sh`, which backs up the files a deck SAVEs and restores
them afterwards, so that no deck changes a file another deck reads.

## How it was made

The C++ engine was translated to C# first (`cs/gen`, 480 of 480), and this engine is translated from that C#, not
from the C++, by a translator written for it: `tools/cs2x/Cs2Kotlin.cs`, `KtTypes.cs` and `KtFn.cs`, which read the C#
through Roslyn's semantic model and print Kotlin (`kotlin/gen/engine`, 1,258 files, 186,556 lines; the mapping is
in `tools/cs2x/KOTLIN.md`). It began as a copy of the Java translator and keeps its names, runtime calls and analysis.

The runtime (`kotlin/src/cpp`, package `cpp`, 181 files, 20,587 lines) is the small part of the C++ standard
library and of glibc that the engine calls. It is the Java port's runtime (`java/src/cpp`) translated into Kotlin by
`kotlin/tools/J2Kt.java`, which reads the Java with the JDK's own compiler (javac's tree API and attribution) and
prints Kotlin; nothing in it is edited by hand. The 19 glibc math functions the engine calls (exp, log, pow, sin,
cos, tan, asin, acos, atan, log10, atan2, log2, sinh, tanh, expm1, log1p, cosh, cbrt, hypot) are `Glibm.kt` and
`Glibm2.kt` (7,678 lines), written by the Kotlin back end of `tools/libm/gim2x.py` from GCC's compiled form of
glibc 2.39's own code (`make_glibm.sh`), with glibc's FMA variants where x86-64 glibc picks them; `kotlin.math`,
`java.lang.Math` and `StrictMath` are not used for any of them. `GlibmRt.kt` and `Glibm2Rt.kt` (the helpers the
generated math calls) and `src/main/Main.kt` (the command line, a port of `java/src/main/Main.java`) are written by
hand. The Kotlin back end of gim2x.py came from the SWMM5 Kotlin port and was extended to the second set of
functions; the Go, Java and TypeScript files it writes are unchanged (`results/gim2x_unchanged.txt`).

Nothing in the generated engine is edited by hand: every fix went into the translator or into J2Kt.

## How it was checked

- **The math**: `test/run_libm_tests.sh` compares each of the 19 functions (and fma, frexp, ldexp) with the system's
  glibc 2.39, bit for bit, by the method of `tools/libm/test`: a million arguments per function over the ranges SWMM
  uses with special values, and 100,000 edge cases each, for the first ten; a million per function over wide ranges
  and every branch threshold, with signed zeros, infinities, subnormals and NaNs of both signs with several
  payloads, JIT-compiled and interpreted, for the other nine: **0 differences** (`results/libm_tests.txt`).
- **The runtime**: `test/run_runtime_checks.sh` compiles the check programs that prove the Java runtime
  (`tests/`) against the Kotlin runtime's classes and compares their output with glibc's and libstdc++'s:
  printf (179,756 cases), sscanf, strtod and the other C library calls, introsort's permutations, the hash tables'
  node order and std::hash, the streams and the file system: **identical**, apart from one printf case C# formats
  differently from glibc, `%#.2g` of 99.995, where the Kotlin runtime agrees with glibc (`results/runtime_checks.txt`).
- **The engine**: the gate (above), run on the corpus and on the extra decks.

## Build and run

Needs JDK 21 and the Kotlin 2.0.21 compiler: `kotlinc.sh` uses `kotlinc` if it is on the PATH, otherwise the
compiler jars that ship with Gradle 8.14.3 (`KOTLIN_LIB`, default `/opt/gradle/lib`). Build (about 4 minutes, 2 GB
of memory): `kotlin/build.sh`, which writes `kotlin/bin/openswmm.jar` with the Kotlin standard library inside. Then
run a model the way the C++ command line runs it:

    kotlin/runswmm6 model.inp model.rpt model.out

To regenerate: `tools/cs2x/build_kotlin.sh && dotnet tools/cs2x/bin/cs2kotlin.dll cs kotlin/gen` (the engine, from
`cs/gen`; needs the .NET 8 SDK), `kotlin/make_runtime.sh` (the runtime, from `java/src/cpp`), `kotlin/make_glibm.sh
GLIBM_BUILD` (the math, from the GIMPLE dumps that `tools/libm/build_glibm.sh` writes).

The gate: `kotlin/gate/run_chunk.sh NAME corpus|extra [IDS_FILE]` runs `gate/gate.py` on this port with the
SAVE-file wrapper; `kotlin/gate/rerun_build.sh` makes the clean copy of a commit that the independent rerun gates.

## What this does not show

- Agreement on models outside the 480 and the 452. A feature no gate model or deck uses is untested.
- The reference's parallel runs: every parallel loop runs on one thread, in order, as the C++ engine does when it
  is held to one thread.
- The 2D and other modules of OpenSWMM 6 beyond the 1D engine the command line runs.
- Every input to the 19 math functions: about a million arguments each is a test, not a proof.
- Speed. The times above were measured on a shared two-core machine while other gate runs were going.

## License and attribution

The engine is a translation of the OpenSWMM Engine, Copyright 2026 HydroCouple Developers, licensed under the
Apache License, Version 2.0 (https://github.com/HydroCouple/openswmm.engine, branch `swmm6_rel`, commit 88329d6).
Portions derive from the USEPA Storm Water Management Model, which is in the public domain; the USEPA does not
endorse this translation, and neither does HydroCouple. The math routines `src/cpp/Glibm.kt` and `src/cpp/Glibm2.kt`
are translations of glibc 2.39 code and remain under glibc's licence, the GNU Lesser General Public License 2.1 or
later.

## AI use

The translator (`tools/cs2x/Cs2Kotlin.cs`, `KtTypes.cs`, `KtFn.cs`), the runtime (the Java port's runtime translated by J2Kt, which Claude also wrote, and the
hand-written parts), the port's scripts and checks, the Kotlin back end of gim2x.py and these notes were written by
Claude, Anthropic's AI model, for Robert Dickinson, who directed the work. The engine is a mechanical translation of
OpenSWMM 6 (Apache-2.0, HydroCouple Developers), itself derived in part from the public-domain EPA SWMM; it was
checked by running it against the C++ engine, not by reading it. The glibc-derived math is LGPL-2.1-or-later. See
`../AI-USE.md`.
