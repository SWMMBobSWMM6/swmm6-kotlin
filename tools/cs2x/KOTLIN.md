# C# to Kotlin: how tools/cs2x/Cs2Kotlin.cs maps the generated engine

The Kotlin engine (kotlin/gen/engine) is written from the verified C# engine (cs/gen) the way the Java engine is:
Roslyn's semantic model, one IOperation tree per method, printed as Kotlin. The translator (Cs2Kotlin.cs,
KtTypes.cs, KtFn.cs; built by build_kotlin.sh) began as a copy of the Java translator, so it keeps the Java
translator's names, its runtime calls and its analysis (which locals are cells, which methods are renamed, which
containers get element makers; see JAVA.md), and changes the printing. The Kotlin runtime (package `cpp`,
kotlin/src/cpp) is the Java port's runtime translated by kotlin/tools/J2Kt.java (below), so it has the same classes,
members and static functions, and the translator calls it exactly as the Java translator calls the Java runtime.

    tools/cs2x/build_kotlin.sh && dotnet tools/cs2x/bin/cs2kotlin.dll cs kotlin/gen

## Types

| C# | Kotlin | notes |
|---|---|---|
| int, uint / long, ulong | Int / Long | unsigned arithmetic through `java.lang.Integer` / `java.lang.Long` (`compareUnsigned`, `divideUnsigned`, ...), as the Java engine does |
| short, ushort, sbyte, byte, char | Short, Char, Byte, Byte, Char | every operation widens explicitly (`b.toInt() and 0xFF`); Kotlin has no implicit widening |
| bool, double, float, string, object | Boolean, Double, Float, String?, Any? | |
| engine class, runtime class | `X?` | every reference type is nullable and dereferenced with `!!`, as C# would throw where Kotlin's `!!` throws |
| engine enum | `object` with `const val` members of the underlying type | |
| engine static class | `object` | members `@JvmStatic`; a class's static members are in its `companion object` |
| delegate | `fun interface` | a lambda is `(Type lab@{ p -> ... })`, its returns `return@lab` |
| array | `DoubleArray`, `IntArray`, ...; `Array<X?>` | Kotlin arrays are invariant: a runtime parameter that takes Java's `Object[]` is `Array<*>`; a C# `params` array is passed element by element or spread with `*` (lessons/34) |

Fields are `@JvmField var` (no getters), so the runtime's Java-shaped field access and the engine's direct
access are plain JVM field reads and writes. Every engine class is `open` (or `abstract`) and every instance
method `open`, as Java's are; `override` is written wherever C# overrides, implements a runtime interface member,
or Java would override (the same signature key as a base method).

## Statements

- Kotlin has no `for(;;)`: a for loop is a `while` labelled `__Lk@`, with its steps written before every
  `continue@__Lk` as well as at the end of the body; every `break` and `continue` names its loop.
- `switch` is `when`; a `break` that is not the last statement of its section leaves a labelled
  `do { ... } while (false)` around the `when`.
- The gotos of hydrology/surface/UebReference become a dispatch loop,
  `__dispatch@ while (true) { when (__st) { k -> { ...; __st = k + 1; continue@__dispatch } } break@__dispatch }`,
  with the method's locals declared before it.
- An assignment used as a value is `x.also { ... }` or a temporary before the statement; `a = b = c` is two statements.
- A method whose body can fall off the end in Kotlin's analysis but not in C#'s gets
  `throw IllegalStateException("unreachable")`.
- A local that is a cell and is only assigned later starts as a new cell of its type's default (never `null!!`,
  which compiles anywhere and throws when it runs; lessons/33).

## Numbers

Kotlin's Double and Float arithmetic is the JVM's: IEEE, no fusion. Every conversion is explicit (`toInt()`,
`toLong()`, `toDouble()`), and double-to-int goes through the runtime's x86 `cvttsd2si` helpers (`cpp.F2.i32`),
as in C# and Java. Integer literals are typed by their use (`0L`, `0.toByte()`), bit operations are Kotlin's
infix functions (`and`, `or`, `xor`, `shl`, `shr`, `ushr`, `inv()`), and a char's code is `.code`. The math
functions come from Glibm.kt and Glibm2.kt, written by tools/libm/gim2x.py's Kotlin back end from glibc's own
code (kotlin/make_glibm.sh); `kotlin.math` and `java.lang.Math`/`StrictMath` are not used for any of the 19 glibc
functions.

## Names

Kotlin's hard keywords (`as`, `fun`, `in`, `is`, `object`, `typealias`, `typeof`, `val`, `when`, and `it`,
`field`, `kotlin`) are renamed with an underscore in the engine and backquoted where a runtime member has one of
these names. A field that hides a base class's field of the same name (C# allows it, Kotlin does not) gets its
class's name appended. A static member of a runtime class that a subclass inherits in Java (`R.stderr_` from
`RFs`) is not inherited by a Kotlin companion; J2Kt writes a forwarder in the subclass (lessons/32).

## The runtime: kotlin/tools/J2Kt.java

J2Kt reads the Java runtime (java/src/cpp/*.java) with the JDK's own compiler (javac's tree API with attribution)
and prints every file as Kotlin: classes and interfaces with their visibility, `open`/`abstract`/`override`, a
companion object for statics (`@JvmStatic`, `@JvmField`), `fun interface` for single-method interfaces, every
expression with Java's types made explicit (numeric conversions, unboxing, `.code`, infix bit operations, `===`
for reference identity, `!!` at dereferences), labelled loops, `when` for switch, instanceof patterns as casts,
method references and lambdas as SAM constructors with labelled returns, and Java's collection interfaces as
Kotlin's mutable ones. Package-private members that other runtime classes do not use are private. GlibmRt.kt and
Glibm2Rt.kt (the gim2x helpers) and Glibm.kt/Glibm2.kt (the math) come from tools/libm, not from J2Kt.

Written by Claude (Anthropic) for Robert Dickinson's SWMM6 ports; see AI-USE.md and kotlin/README.md.
