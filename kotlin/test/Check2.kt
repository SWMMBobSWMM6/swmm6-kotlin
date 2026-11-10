// Check2.kt - compare set 2 of the Kotlin glibc ports (Glibm2: cbrt, hypot, atan2, cosh, sinh, tanh, log2, expm1,
// log1p, and the runtime's frexp and ldexp) bit for bit with system-libm vectors (tools/libm/test/mkvectors2.c). A
// NaN result must match glibc's in sign and payload too. Optional second argument: a comma-separated list of
// functions. A Kotlin port of tools/libm/test/Check2.java. Written by Claude (Anthropic) for Robert Dickinson.
package cpp

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.file.Files
import java.nio.file.Paths

object Check2 {
  @JvmStatic fun main(a: Array<String>) {
    val b = ByteBuffer.wrap(Files.readAllBytes(Paths.get(a[0]))).order(ByteOrder.LITTLE_ENDIAN)
    val only = if (a.size > 1) a[1].split(",").toSet() else null
    var total = 0L
    while (b.hasRemaining()) {
      val sb = StringBuilder()
      while (true) { val c = b.get(); if (c.toInt() == 0) break; sb.append(c.toInt().toChar()) }
      val name = sb.toString()
      val n = b.getInt()
      val na = if (name == "hypot" || name == "atan2" || name == "ldexp") 2 else 1
      val nr = if (name == "frexp") 2 else 1
      if (only != null && name !in only) { b.position(b.position() + n * 8 * (na + nr)); continue }
      var bad = 0L; var nanSign = 0L; var nanPay = 0L; var shown = 0
      val t0 = System.nanoTime()
      for (i in 0 until n) {
        val x = b.getDouble(); val y = if (na > 1) b.getDouble() else 0.0
        val want = b.getDouble(); val wantE = if (nr > 1) b.getDouble() else 0.0
        var gotE = 0
        val got = when (name) {
          "cbrt" -> Glibm2.cbrt(x); "hypot" -> Glibm2.hypot(x, y); "atan2" -> Glibm2.atan2(x, y)
          "cosh" -> Glibm2.cosh(x); "sinh" -> Glibm2.sinh(x); "tanh" -> Glibm2.tanh(x); "log2" -> Glibm2.log2(x)
          "expm1" -> Glibm2.expm1(x); "log1p" -> Glibm2.log1p(x)
          "frexp" -> { gotE = Glibm2Rt.frexpExp(x); Glibm2Rt.frexp(x) }
          else -> Glibm2Rt.ldexp(x, y.toInt())
        }
        val gb = got.toRawBits(); val wb = want.toRawBits()
        if (got.isNaN() && want.isNaN()) {
          if ((gb ushr 63) != (wb ushr 63)) nanSign++ else if (gb != wb) nanPay++
          continue
        }
        if (gb != wb || gotE.toDouble() != wantE) {
          bad++
          if (shown++ < 4) println(String.format("  %s(%s [%016x], %s [%016x]) = %s [%016x], libm %s [%016x]", name, x,
              x.toRawBits(), y, y.toRawBits(), got, gb, want, wb))
        }
      }
      total += bad + nanSign + nanPay
      println(String.format("%-6s %d of %d differ, %d NaN sign, %d NaN payload   (%.3f us/call)", name, bad, n, nanSign, nanPay,
          (System.nanoTime() - t0) / 1e3 / n))
    }
    println("total differences: $total")
  }
}
