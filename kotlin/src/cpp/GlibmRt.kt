// GlibmRt.kt - runtime for the Kotlin translation of glibc 2.39's double-precision math routines.
//
// The translated routines (Glibm.kt, written by tools/libm/gim2x.py from GCC's optimized GIMPLE) use
// Math.fma (the JVM's exactly rounded fused multiply-add, like the hardware FMA glibc's x86-64 variants run
// on), the bit patterns of doubles, x86's float-to-integer conversions, unsigned comparison and division,
// and glibc's error helpers. All are here, plus a port of branred.c (huge-argument reduction for
// sin/cos/tan, compiled by glibc without FMA, so plain double arithmetic in source order reproduces it).
// A Kotlin port of tools/libm/GlibmRt.java, copied from the SWMM5 Kotlin port (swmm5-ports/kotlin/src/swmm5/GlibmRt.kt)
// into package cpp; only the package line differs. Derived from glibc (branred.c and the math_err.c helpers), so
// LGPL-2.1-or-later. Written by Claude (Anthropic) for Robert Dickinson; see kotlin/README.md.
@file:Suppress("NAME_SHADOWING", "LocalVariableName", "FunctionName", "ObjectPropertyName")
package cpp

import kotlin.math.abs

object GlibmRt {
  @JvmStatic fun p2(e: Int): Double = Double.fromBits((e + 1023).toLong() shl 52)

  @JvmStatic fun f2u(d: Double): Long = d.toRawBits()
  @JvmStatic fun u2f(b: Long): Double = Double.fromBits(b)
  @JvmStatic fun hi(d: Double): Int = (d.toRawBits() ushr 32).toInt()
  @JvmStatic fun lo(d: Double): Int = d.toRawBits().toInt()

  // x86-64 cvttsd2si: NaN and out-of-range values give the "integer indefinite" value
  @JvmStatic fun d2i(x: Double): Int {
    if (!(x > -2147483649.0 && x < 2147483648.0)) return Int.MIN_VALUE
    return x.toInt()
  }
  @JvmStatic fun d2l(x: Double): Long {
    if (!(x > -9223372036854775809.0 && x < 9223372036854775808.0)) return Long.MIN_VALUE
    return x.toLong()
  }
  @JvmStatic fun u64ToDouble(u: Long): Double {      // (double) of an unsigned 64-bit value, rounded once
    if (u >= 0L) return u.toDouble()
    return ((u ushr 1) or (u and 1L)).toDouble() * 2.0
  }

  // unsigned 32- and 64-bit comparison, division and remainder on the bit patterns
  @JvmStatic fun ucmp(a: Int, b: Int): Int = (a xor Int.MIN_VALUE).compareTo(b xor Int.MIN_VALUE)
  @JvmStatic fun ulcmp(a: Long, b: Long): Int = (a xor Long.MIN_VALUE).compareTo(b xor Long.MIN_VALUE)
  @JvmStatic fun udiv(a: Int, b: Int): Int = ((a.toLong() and 0xFFFFFFFFL) / (b.toLong() and 0xFFFFFFFFL)).toInt()
  @JvmStatic fun urem(a: Int, b: Int): Int = ((a.toLong() and 0xFFFFFFFFL) % (b.toLong() and 0xFFFFFFFFL)).toInt()
  @JvmStatic fun uldiv(a: Long, b: Long): Long = (a.toULong() / b.toULong()).toLong()
  @JvmStatic fun ulrem(a: Long, b: Long): Long = (a.toULong() % b.toULong()).toLong()

  @JvmStatic fun copysign(x: Double, y: Double): Double =
    Double.fromBits((x.toRawBits() and 0x7fffffffffffffffL) or (y.toRawBits() and Long.MIN_VALUE))

  // glibc's math_err.c helpers (the value they return; errno is not modelled)
  @JvmStatic fun math_oflow(sign: Int): Double { val y = p2(769); return (if (sign != 0) -y else y) * y }
  @JvmStatic fun math_uflow(sign: Int): Double { val y = p2(-767); return (if (sign != 0) -y else y) * y }
  @JvmStatic fun math_may_uflow(sign: Int): Double { val y = 1.5 * p2(-538); return (if (sign != 0) -y else y) * y }
  @JvmStatic fun math_divzero(sign: Int): Double = (if (sign != 0) -1.0 else 1.0) / 0.0
  @JvmStatic fun math_invalid(x: Double): Double = (x - x) / (x - x)
  @JvmStatic fun math_check_oflow(y: Double): Double = y
  @JvmStatic fun math_check_uflow(y: Double): Double = y

  // tables: concatenated 16-hex-digit bit patterns
  @JvmStatic fun u64tab(hex: String): LongArray {
    val n = hex.length / 16
    val out = LongArray(n)
    for (i in 0 until n) out[i] = hex.substring(16 * i, 16 * i + 16).toULong(16).toLong()
    return out
  }
  @JvmStatic fun f64tab(hex: String): DoubleArray {
    val u = u64tab(hex)
    val out = DoubleArray(u.size)
    for (i in u.indices) out[i] = Double.fromBits(u[i])
    return out
  }

  // branred.c: x = n*pi/2 + (a + aa), returns n mod 4
  @JvmField var brA = 0.0
  @JvmField var brAA = 0.0
  private val t576 = p2(576)
  private val tm600 = p2(-600)
  private val tm24 = p2(-24)
  private const val big = 6755399441055744.0
  private const val big1 = 27021597764222976.0
  private val mp1 = Double.fromBits(0x3FF921FB58000000L)
  private val mp2 = Double.fromBits(-4733882892101353472L)        // 0xBE4DDE9740000000
  private val hp0b = Double.fromBits(0x3FF921FB54442D18L)
  private val hp1b = Double.fromBits(0x3C91A62633145C07L)
  private const val SPLIT = 134217729.0

  private var hb = 0.0
  private var hbb = 0.0
  private var hsum = 0.0
  private fun half(xx: Double, toverp: DoubleArray) {
    val r = DoubleArray(6)
    var sum = 0.0
    var s: Double
    var t: Double
    var k = (hi(xx) shr 20) and 2047
    k = (k - 450) / 24
    if (k < 0) k = 0
    var gor = Double.fromBits(t576.toRawBits() - ((k * 24).toLong() shl 52))
    for (i in 0 until 6) { r[i] = xx * toverp[k + i] * gor; gor *= tm24 }
    for (i in 0 until 3) { s = (r[i] + big) - big; sum += s; r[i] -= s }
    t = 0.0
    for (i in 0 until 6) t += r[5 - i]
    var bb = (((((r[0] - t) + r[1]) + r[2]) + r[3]) + r[4]) + r[5]
    s = (t + big) - big
    sum += s
    t -= s
    val b = t + bb
    bb = (t - b) + bb
    s = (sum + big1) - big1
    sum -= s
    hb = b; hbb = bb; hsum = sum
  }
  @JvmStatic fun branred(x: Double, toverp: DoubleArray): Int {
    var x = x
    x *= tm600
    var t = x * SPLIT
    val x1 = t - (t - x)
    val x2 = x - x1
    half(x1, toverp)
    val b1 = hb; val bb1 = hbb; val sum1 = hsum
    half(x2, toverp)
    val b2 = hb; val bb2 = hbb; val sum2 = hsum
    var sum = sum1 + sum2
    var b = b1 + b2
    var bb = if (abs(b1) > abs(b2)) (b1 - b) + b2 else (b2 - b) + b1
    if (b > 0.5) { b -= 1.0; sum += 1.0 }
    else if (b < -0.5) { b += 1.0; sum -= 1.0 }
    var s = b + (bb + bb1 + bb2)
    t = ((b - s) + bb) + (bb1 + bb2)
    b = s * SPLIT
    val t1 = b - (b - s)
    val t2 = s - t1
    b = s * hp0b
    bb = (((t1 * mp1 - b) + t1 * mp2) + t2 * mp1) + (t2 * mp2 + s * hp1b + t * hp0b)
    s = b + bb
    t = (b - s) + bb
    brA = s; brAA = t
    return d2i(sum) and 3
  }
}
