// Main.kt - the SWMM6 command line (src/cli/main.cpp): a hand port to Kotlin of the Java port's java/src/main/Main.java,
// itself a port of cs/runtime/Program.cs: the same calls into the engine's C API (engine.F__global) in the same order,
// the same messages on stdout and the same exit code; R.FlushAll before exit, as returning from main calls exit(),
// which flushes every open FILE. As in the Java port, the engine runs on a thread with a large stack, and an exception
// that escapes the engine ends the process as .NET's unhandled-exception handler does: "Unhandled exception. ..." on
// stderr, no flushing of the C files, and exit code 134 (SIGABRT's).
// Written by Claude (Anthropic) for Robert Dickinson's SWMM6 ports; see ../../README.md.
import cpp.CPtr
import cpp.PtrD
import cpp.R
import engine.F__global
import java.math.BigDecimal
import java.math.RoundingMode
import java.nio.charset.StandardCharsets

object Main {
	const val VERSION = "6.0.0-alpha.4"
	const val STACK_BYTES = 256L shl 20

	@JvmStatic fun Out(s: String) {
		val b = s.toByteArray(StandardCharsets.ISO_8859_1)
		R.stdout_!!.S!!.Write(b, 0, b.size)
	}

	@JvmStatic fun main(argv: Array<String>) {
		val rc = IntArray(1)
		val crash = arrayOfNulls<Throwable>(1)
		val t = Thread(null, {
			try { rc[0] = Run(argv) } catch (e: Throwable) { crash[0] = e }
		}, "openswmm", STACK_BYTES)
		t.start()
		t.join()
		val c = crash[0]
		if (c != null) {
			System.err.println("Unhandled exception. $c")
			c.printStackTrace(System.err)
			System.err.flush()
			Runtime.getRuntime().halt(134)
		}
		R.FlushAll()          // returning from main calls exit(), which flushes every open FILE
		System.exit(rc[0])
	}

	@JvmStatic fun Run(argv: Array<String>): Int {
		if (argv.size < 1) { Out("\nError: not enough arguments. Use --help for usage.\n\n"); return 1 }
		if (argv[0] == "--version" || argv[0] == "-v") { Out("openswmm.engine $VERSION\n"); return 0 }
		if (argv.size < 2) {
			Out("\nError: need at least input and report file paths.\n")
			Out("Usage: openswmm <input.inp> <report.rpt> [output.out]\n\n")
			return 1
		}
		val inp = argv[0]; val rpt = argv[1]; val outf = if (argv.size > 2) argv[2] else ""
		Out("\n... OpenSWMM Engine $VERSION\n... Input: $inp\n... Report: $rpt\n... Output: $outf\n")
		R.fflush(R.stdout_)
		val t0 = System.nanoTime()
		val engine = F__global.swmm_engine_create()
		if (engine == null) { Out("Error: failed to create engine instance.\n"); return 1 }
		var err = F__global.swmm_engine_open(engine, R.Lit(inp), R.Lit(rpt), R.Lit(outf), CPtr.ZERO)
		if (err != 0) {
			Out("Error opening input file: " + R.Str(F__global.swmm_get_last_error_msg(engine)) + "\n")
			F__global.swmm_engine_destroy(engine)
			return err
		}
		err = F__global.swmm_engine_initialize(engine)
		if (err != 0) {
			Out("Error initializing: " + R.Str(F__global.swmm_get_last_error_msg(engine)) + "\n")
			F__global.swmm_engine_close(engine)
			F__global.swmm_engine_destroy(engine)
			return err
		}
		val save = if (outf.length > 0) 1 else 0
		err = F__global.swmm_engine_start(engine, save)
		if (err != 0) {
			Out("Error starting simulation: " + R.Str(F__global.swmm_get_last_error_msg(engine)) + "\n")
			F__global.swmm_engine_close(engine)
			F__global.swmm_engine_destroy(engine)
			return err
		}
		val elapsed = DoubleArray(1)
		var steps = 0L
		while (true) {
			err = F__global.swmm_engine_step(engine, PtrD(elapsed, 0L))
			if (err != 0) { Out("\nError at step " + steps + ": " + R.Str(F__global.swmm_get_last_error_msg(engine)) + "\n"); break }
			if (elapsed[0] <= 0.0) break
			steps++
		}
		Out("\n... $steps steps completed.\n")
		val result = intArrayOf(err)
		Fin(engine, result, F__global.swmm_engine_end(engine), "ending simulation")
		Fin(engine, result, F__global.swmm_engine_report(engine), "writing report")
		Fin(engine, result, F__global.swmm_engine_close(engine), "closing simulation")
		F__global.swmm_engine_destroy(engine)
		// {1:F2}: the elapsed seconds rounded to two places, half away from zero on the exact value (.NET Core)
		val secs = (System.nanoTime() - t0) / 1e9
		val f2 = BigDecimal(secs).setScale(2, RoundingMode.HALF_UP).toPlainString()
		Out("... OpenSWMM Engine " + (if (result[0] == 0) "completed" else "failed") + " in " + f2 + " seconds.\n\n")
		return result[0]
	}

	@JvmStatic fun Fin(engine: Any?, result: IntArray, status: Int, phase: String) {
		if (status != 0) {
			Out("Error " + phase + ": " + R.Str(F__global.swmm_get_last_error_msg(engine)) + "\n")
			if (result[0] == 0) result[0] = status
		}
	}
}
