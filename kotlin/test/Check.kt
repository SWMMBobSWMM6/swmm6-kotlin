// Check.kt - compare the Kotlin glibc ports (and Math.fma) bit for bit with system-libm vectors (mkvectors.c,
// mkedge.c). A Kotlin port of tools/libm/test/Check.java, copied from the SWMM5 Kotlin port (package changed).
// Written by Claude (Anthropic) for Robert Dickinson.
package cpp

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.file.Files
import java.nio.file.Paths

fun main(a: Array<String>) {
  val b = ByteBuffer.wrap(Files.readAllBytes(Paths.get(a[0]))).order(ByteOrder.LITTLE_ENDIAN)
  val only = if (a.size > 1) a[1].split(",").toSet() else null
  while (b.hasRemaining()) {
    val sb = StringBuilder()
    while (true) { val c = b.get(); if (c.toInt() == 0) break; sb.append(c.toInt().toChar()) }
    val name = sb.toString()
    val n = b.getInt()
    val na = if (name == "pow") 2 else if (name == "fma") 3 else 1
    if (only != null && name !in only) { b.position(b.position() + n * 8 * (na + 1)); continue }
    var bad = 0L; var nanSign = 0L; var shown = 0
    val t0 = System.nanoTime()
    for (i in 0 until n) {
      val x = b.getDouble(); val y = if (na > 1) b.getDouble() else 0.0; val z = if (na > 2) b.getDouble() else 0.0
      val want = b.getDouble()
      val got = when (name) {
        "exp" -> Glibm.exp(x); "log" -> Glibm.log(x); "log10" -> Glibm.log10(x); "pow" -> Glibm.pow(x, y)
        "sin" -> Glibm.sin(x); "cos" -> Glibm.cos(x); "tan" -> Glibm.tan(x); "asin" -> Glibm.asin(x)
        "acos" -> Glibm.acos(x); "atan" -> Glibm.atan(x)
        else -> Math.fma(x, y, z)
      }
      val gb = got.toRawBits(); val wb = want.toRawBits()
      if (got.isNaN() && want.isNaN()) { if ((gb ushr 63) != (wb ushr 63)) nanSign++; continue }
      if (gb != wb) {
        bad++
        if (shown++ < 4) println("  $name($x" + (if (na > 1) ", $y" else "") + (if (na > 2) ", $z" else "") + ") = $got, libm $want")
      }
    }
    println(String.format("%-6s %d of %d differ, %d NaN sign   (%.3f us/call)", name, bad, n, nanSign, (System.nanoTime() - t0) / 1e3 / n))
  }
}
