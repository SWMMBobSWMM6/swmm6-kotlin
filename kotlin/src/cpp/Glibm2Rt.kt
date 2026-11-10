// Glibm2Rt.kt - runtime additions for set 2 of the Kotlin translation of glibc 2.39's math routines (Glibm2.kt):
// frexp and ldexp, which glibc's cbrt calls, and quiet; the rest of what Glibm2.kt calls is GlibmRt's. A Kotlin port of
// tools/libm/Glibm2Rt.java (hand ports of glibc 2.39's sysdeps/ieee754/dbl-64/s_frexp.c, s_scalbn.c and
// math/s_ldexp_template.c: exact operations only, scalings by powers of two and, for a subnormal result, one
// correctly rounded multiplication). Derived from the GNU C Library and so, like it, licensed under the GNU Lesser
// General Public License, version 2.1 or later (LGPL-2.1-or-later).
// Written by Claude (Anthropic) for Robert Dickinson's SWMM6 ports; see kotlin/README.md.
@file:Suppress("NAME_SHADOWING", "FunctionName")
package cpp

object Glibm2Rt {
  // what Glibm2.kt calls of set 1's runtime (Kotlin objects do not inherit, so these forward to GlibmRt)
  @JvmStatic fun d2i(x: Double): Int = GlibmRt.d2i(x)
  @JvmStatic fun d2l(x: Double): Long = GlibmRt.d2l(x)
  @JvmStatic fun copysign(x: Double, y: Double): Double = GlibmRt.copysign(x, y)
  @JvmStatic fun f64tab(hex: String): DoubleArray = GlibmRt.f64tab(hex)
  @JvmStatic fun u64tab(hex: String): LongArray = GlibmRt.u64tab(hex)
  @JvmStatic fun u64ToDouble(u: Long): Double = GlibmRt.u64ToDouble(u)
  @JvmStatic fun ucmp(a: Int, b: Int): Int = GlibmRt.ucmp(a, b)
  @JvmStatic fun ulcmp(a: Long, b: Long): Int = GlibmRt.ulcmp(a, b)
  @JvmStatic fun math_divzero(sign: Int): Double = GlibmRt.math_divzero(sign)
  @JvmStatic fun math_invalid(x: Double): Double = GlibmRt.math_invalid(x)
  @JvmStatic fun math_oflow(sign: Int): Double = GlibmRt.math_oflow(sign)
  @JvmStatic fun math_uflow(sign: Int): Double = GlibmRt.math_uflow(sign)

  /** A NaN as x86 passes it on: quieted, sign and payload kept. */
  @JvmStatic fun quiet(x: Double): Double = Double.fromBits(x.toRawBits() or (1L shl 51))

  private val TWO54 = Double.fromBits(0x4350000000000000L)     // 0x1p54
  private val TWOM54 = Double.fromBits(0x3c90000000000000L)    // 0x1p-54
  private const val HUGE = 1.0e+300
  private const val TINY = 1.0e-300

  /** glibc's __frexp, the mantissa: 0.5 <= |m| < 1; zero, infinity and NaN come back as x + x. */
  @JvmStatic fun frexp(x: Double): Double {
    var x = x
    var ix = x.toRawBits()
    val ex = (0x7ffL and (ix shr 52)).toInt()
    if (ex != 0x7ff && x != 0.0) {
      if (ex == 0) {                                   // subnormal
        x *= TWO54
        ix = x.toRawBits()
      }
      return Double.fromBits((ix and -0x7ff0000000000001L) or 0x3fe0000000000000L)   // ix & 0x800fffffffffffff
    }
    return x + x                                       // quiets a signaling NaN
  }
  /** glibc's __frexp, the exponent (0 for zero, infinity and NaN). */
  @JvmStatic fun frexpExp(x: Double): Int {
    var x = x
    var ix = x.toRawBits()
    val ex = (0x7ffL and (ix shr 52)).toInt()
    if (ex != 0x7ff && x != 0.0) {
      if (ex == 0) {
        x *= TWO54
        ix = x.toRawBits()
        return (0x7ffL and (ix shr 52)).toInt() - 1022 - 54
      }
      return ex - 1022
    }
    return 0
  }

  /** glibc's __scalbn (dbl-64/s_scalbn.c). */
  @JvmStatic fun scalbn(x: Double, n: Int): Double {
    var x = x
    var ix = x.toRawBits()
    var k = (ix shr 52) and 0x7ffL
    if (k == 0L) {                                     // 0 or subnormal x
      if ((ix and 0xfffffffffffffL) == 0L) return x
      x *= TWO54
      ix = x.toRawBits()
      k = ((ix shr 52) and 0x7ffL) - 54
    }
    if (k == 0x7ffL) return x + x                      // NaN or Inf
    if (n < -50000) return TINY * copysign(TINY, x)
    if (n > 50000 || k + n > 0x7fe) return HUGE * copysign(HUGE, x)
    k += n
    if (k > 0) return Double.fromBits((ix and -0x7ff0000000000001L) or (k shl 52))
    if (k <= -54) return TINY * copysign(TINY, x)
    k += 54
    x = Double.fromBits((ix and -0x7ff0000000000001L) or (k shl 52))
    return x * TWOM54
  }
  /** glibc's __ldexp (math/s_ldexp_template.c; errno not modelled). */
  @JvmStatic fun ldexp(value: Double, exp: Int): Double {
    if (value.isNaN() || value.isInfinite() || value == 0.0) return value + value
    return scalbn(value, exp)
  }
}
