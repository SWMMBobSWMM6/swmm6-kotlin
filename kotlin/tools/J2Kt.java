// J2Kt.java - translate the Java runtime of the SWMM6 Java port (java/src/cpp, package cpp: the small part of the C++
// standard library and of glibc that the translated engine calls) into Kotlin, for the Kotlin port's runtime
// (kotlin/src/cpp). It reads the Java with javac's own parser and attribution (the com.sun.source / javac tree API of
// the JDK), so every expression's type is known, and writes Kotlin that keeps Java's semantics:
//   * every numeric conversion Java makes implicitly (widening, binary numeric promotion, char to int, unboxing) is
//     written out, since Kotlin makes none; char arithmetic goes through .code; compound assignments narrow as Java's do
//   * == and != on references are identity (===), on primitives IEEE's (Kotlin's == on statically typed numbers)
//   * bitwise operators and shifts are Kotlin's infix functions; assignments, ++ and op= used as values are rewritten
//   * every reference type is nullable and every dereference is `!!`, which throws where Java would
//   * a class with only static members becomes an object; static members of other classes go to a companion object
//     with @JvmStatic / @JvmField, so the JVM sees the Java runtime's shape (the translated engine calls it that way)
//   * Java's for loop is a while loop whose continues run the update first; switch is when, with fall-through written
//     out; local names are made unique per method, so Java's nested blocks can be flattened
// Anything it does not handle stops it with the source position, so nothing is translated silently wrong.
//
//   javac --add-exports ... -d BIN J2Kt.java ; java --add-exports ... -cp BIN J2Kt OUTDIR FILE.java...   (see make_runtime.sh)
//
// Written by Claude (Anthropic) for Robert Dickinson's SWMM6 ports; see ../README.md.
import com.sun.source.tree.*;
import com.sun.source.util.*;
import com.sun.tools.javac.code.*;
import com.sun.tools.javac.code.Symbol.*;
import com.sun.tools.javac.code.Type.*;
import com.sun.tools.javac.tree.JCTree;
import com.sun.tools.javac.tree.JCTree.*;
import com.sun.tools.javac.tree.TreeInfo;
import com.sun.tools.javac.util.Context;
import com.sun.tools.javac.api.JavacTaskImpl;

import javax.lang.model.element.Modifier;
import javax.lang.model.type.TypeKind;
import javax.tools.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.stream.*;

public final class J2Kt {
	static com.sun.tools.javac.code.Types types;
	static Symtab syms;
	static final Set<String> KW = Set.of("as", "break", "class", "continue", "do", "else", "false", "for", "fun", "if", "in",
		"interface", "is", "null", "object", "package", "return", "super", "this", "throw", "true", "try", "typealias",
		"typeof", "val", "var", "when", "while");

	static final class Fail extends RuntimeException { Fail(String m) { super(m); } }

	public static void main(String[] args) throws Exception {
		String out = args[0];
		List<File> files = new ArrayList<>();
		for (int i = 1; i < args.length; i++) files.add(new File(args[i]));
		JavaCompiler jc = ToolProvider.getSystemJavaCompiler();
		DiagnosticCollector<JavaFileObject> dc = new DiagnosticCollector<>();
		StandardJavaFileManager fm = jc.getStandardFileManager(dc, null, StandardCharsets.UTF_8);
		JavacTask task = (JavacTask) jc.getTask(null, fm, dc, List.of("-proc:none", "-encoding", "UTF-8", "-nowarn"), null,
			fm.getJavaFileObjectsFromFiles(files));
		List<CompilationUnitTree> units = new ArrayList<>();
		task.parse().forEach(units::add);
		task.analyze();
		boolean bad = false;
		for (Diagnostic<? extends JavaFileObject> d : dc.getDiagnostics())
			if (d.getKind() == Diagnostic.Kind.ERROR) { System.err.println(d); bad = true; }
		if (bad) System.exit(1);
		Context ctx = ((JavacTaskImpl) task).getContext();
		types = com.sun.tools.javac.code.Types.instance(ctx);
		syms = Symtab.instance(ctx);
		// pass 1: what the printer needs to know about the whole runtime
		for (CompilationUnitTree u : units) new Pre().scan(u, null);
		for (CompilationUnitTree u : units)
			for (JCTree d : ((JCCompilationUnit) u).defs)
				if (d instanceof JCClassDecl c && new Printer((JCCompilationUnit) u).isObject(c)) objects.add(c.sym);
		int n = 0, failed = 0;
		for (CompilationUnitTree u : units) {
			JCCompilationUnit cu = (JCCompilationUnit) u;
			String name = Paths.get(cu.sourcefile.toUri()).getFileName().toString().replace(".java", ".kt");
			if (name.startsWith("Glibm")) continue;     // the math comes from tools/libm/gim2x.py's Kotlin back end
			try {
				String text = new Printer(cu).file();
				Files.writeString(Paths.get(out, name), text, StandardCharsets.UTF_8);
				n++;
			} catch (Fail e) {
				System.err.println(name + ": " + e.getMessage());
				failed++;
			}
		}
		System.out.println("J2Kt: wrote " + n + " files" + (failed > 0 ? ", " + failed + " failed" : ""));
		if (failed > 0) System.exit(1);
	}

	// ------------------------------------------------------------------------------------------------ pass 1
	/** Private members used from another class (Kotlin's private is narrower than Java's), parameters a method assigns,
	 *  methods some subclass or interface member overrides. */
	static final Set<Symbol> crossPrivate = new HashSet<>();
	/** Package-private members (Java's default access) and the classes that use them: Kotlin has no package access, so
	 *  one used only in its own class is private, only by subclasses protected (UMap's constructor from hash and
	 *  equality functions must not be visible beside the public one, or a call with nulls would be ambiguous). */
	static final Map<Symbol, Set<ClassSymbol>> pkgUsers = new HashMap<>();
	static final Set<Symbol> assignedParams = new HashSet<>();
	static final Set<Symbol> instantiated = new HashSet<>();
	static final Set<ClassSymbol> objects = new HashSet<>();
	static final Set<MethodSymbol> overridden = new HashSet<>();

	static final class Pre extends TreeScanner<Void, Void> {
		final Deque<ClassSymbol> cls = new ArrayDeque<>();
		@Override public Void visitClass(ClassTree t, Void v) { cls.push(((JCClassDecl) t).sym); super.visitClass(t, v); cls.pop(); return null; }
		@Override public Void visitMethod(MethodTree t, Void v) {
			MethodSymbol m = ((JCMethodDecl) t).sym;
			if (m != null && !m.isStatic() && !m.isConstructor()) {
				ClassSymbol owner = (ClassSymbol) m.owner;
				for (Type st : types.closure(owner.type)) {
					if (st.tsym == owner) continue;
					for (Symbol x : st.tsym.members().getSymbolsByName(m.name))
						if (x instanceof MethodSymbol xm && !xm.isStatic() && m.overrides(xm, owner, types, false)) overridden.add(xm);
				}
			}
			return super.visitMethod(t, v);
		}
		void use(Symbol s) {
			if (s != null && !cls.isEmpty() && (s.flags() & (Flags.PRIVATE | Flags.PUBLIC | Flags.PROTECTED)) == 0
				&& (s.kind == Kinds.Kind.VAR || s.kind == Kinds.Kind.MTH) && s.owner instanceof ClassSymbol)
				pkgUsers.computeIfAbsent(s, k -> new HashSet<>()).add(cls.peek());
			if (s == null || cls.isEmpty() || (s.flags() & Flags.PRIVATE) == 0) return;
			if (s.kind != Kinds.Kind.VAR && s.kind != Kinds.Kind.MTH) return;
			ClassSymbol owner = s.enclClass();
			if (owner == null) return;
			// Kotlin: a private member is visible in its class and in the classes nested in it, not in an outer class
			for (Symbol c = cls.peek(); c != null; c = c.owner) if (c == owner) return;
			crossPrivate.add(s);
		}
		@Override public Void visitIdentifier(IdentifierTree t, Void v) { use(((JCIdent) t).sym); return null; }
		@Override public Void visitMemberSelect(MemberSelectTree t, Void v) { use(((JCFieldAccess) t).sym); return super.visitMemberSelect(t, v); }
		@Override public Void visitNewClass(NewClassTree t, Void v) {
			JCNewClass nc = (JCNewClass) t;
			use(nc.constructor);
			if (nc.constructor != null) instantiated.add(nc.constructor.owner);
			return super.visitNewClass(t, v);
		}
		void assigned(JCExpression e) {
			e = (JCExpression) TreeInfo.skipParens(e);
			if (e instanceof JCIdent id && id.sym != null && (id.sym.flags() & Flags.PARAMETER) != 0) assignedParams.add(id.sym);
		}
		@Override public Void visitAssignment(AssignmentTree t, Void v) { assigned(((JCAssign) t).lhs); return super.visitAssignment(t, v); }
		@Override public Void visitCompoundAssignment(CompoundAssignmentTree t, Void v) { assigned(((JCAssignOp) t).lhs); return super.visitCompoundAssignment(t, v); }
		@Override public Void visitUnary(UnaryTree t, Void v) {
			JCUnary u = (JCUnary) t;
			if (u.getTag() == JCTree.Tag.PREINC || u.getTag() == JCTree.Tag.PREDEC || u.getTag() == JCTree.Tag.POSTINC || u.getTag() == JCTree.Tag.POSTDEC) assigned(u.arg);
			return super.visitUnary(t, v);
		}
	}

	// ------------------------------------------------------------------------------------------------ helpers on types
	static boolean prim(Type t) { return t != null && t.isPrimitive(); }
	static boolean isBoxed(Type t) { return t != null && !t.isPrimitive() && types.unboxedType(t).isPrimitive(); }
	static boolean isString(Type t) { return t != null && t.tsym == syms.stringType.tsym; }
	static TypeTag tag(Type t) { return t.getTag(); }
	static boolean numeric(Type t) { return prim(t) && tag(t) != TypeTag.BOOLEAN; }
	static boolean integral(Type t) { TypeTag g = tag(t); return g == TypeTag.INT || g == TypeTag.LONG || g == TypeTag.SHORT || g == TypeTag.BYTE || g == TypeTag.CHAR; }

	static String primName(Type t) {
		switch (tag(t)) {
			case BOOLEAN: return "Boolean"; case BYTE: return "Byte"; case SHORT: return "Short"; case CHAR: return "Char";
			case INT: return "Int"; case LONG: return "Long"; case FLOAT: return "Float"; case DOUBLE: return "Double";
			case VOID: return "Unit";
			default: throw new Fail("primitive " + t);
		}
	}

	static String id(String n) { return KW.contains(n) ? "`" + n + "`" : n; }

	/** A class's name in Kotlin source: package cpp's by its nesting (Outer.Inner), the JDK's fully qualified, with the
	 *  java.lang types Kotlin maps written as Kotlin's. */
	static String className(ClassSymbol c) {
		String q = c.getQualifiedName().toString();
		switch (q) {
			case "java.lang.Object": return "Any";
			case "java.lang.String": return "String";
			case "java.lang.Integer": return "Int"; case "java.lang.Long": return "Long"; case "java.lang.Double": return "Double";
			case "java.lang.Float": return "Float"; case "java.lang.Short": return "Short"; case "java.lang.Byte": return "Byte";
			case "java.lang.Character": return "Char"; case "java.lang.Boolean": return "Boolean";
			case "java.lang.Number": return "Number";
			case "java.lang.Throwable": return "Throwable";
			case "java.lang.Comparable": return "Comparable";
			case "java.lang.CharSequence": return "CharSequence";
			case "java.lang.Iterable": return "Iterable";
			case "java.util.Iterator": return "Iterator";
			case "java.lang.Cloneable": return "Cloneable";
			case "java.util.Set": return "MutableSet"; case "java.util.List": return "MutableList";
			case "java.util.Collection": return "MutableCollection"; case "java.util.Map": return "MutableMap";
			case "java.util.Map.Entry": return "MutableMap.MutableEntry";
		}
		// package cpp's classes in full: a member may have a class's simple name (Less has a field called Fn)
		if (q.startsWith("cpp.")) return q;
		if (c.owner instanceof PackageSymbol p && p.getQualifiedName().toString().isEmpty()) return q;
		return q;
	}

	/** The name of a class used as a qualifier for its static members: java.lang's boxes and String by their Java names. */
	static String staticOwner(ClassSymbol c) {
		String q = c.getQualifiedName().toString();
		switch (q) {
			case "java.lang.Integer": return "Integer"; case "java.lang.Character": return "Character";
			case "java.lang.Long": case "java.lang.Double": case "java.lang.Float": case "java.lang.Short": case "java.lang.Byte":
			case "java.lang.Boolean": case "java.lang.String": case "java.lang.Object": return q;
		}
		if (q.startsWith("java.lang.") && q.indexOf('.', 10) < 0) return q.substring(10);
		return className(c);
	}

	/** Kotlin type of a Java type; nullable unless nn (supertypes, SAM constructors) or primitive. */
	static String kt(Type t) { return kt(t, false); }
	static String kt(Type t, boolean nn) {
		if (t == null) throw new Fail("null type");
		switch (tag(t)) {
			case BOOLEAN: case BYTE: case SHORT: case CHAR: case INT: case LONG: case FLOAT: case DOUBLE: case VOID: return primName(t);
			case BOT: return "Nothing?";
			case TYPEVAR: return t.tsym.name.toString();
			case ARRAY: {
				Type e = ((ArrayType) t).elemtype;
				String s = prim(e) ? primName(e) + "Array" : "Array<" + targ(e) + ">";
				return nn ? s : s + "?";
			}
			case CLASS: {
				ClassType ct = (ClassType) t;
				ClassSymbol cs = (ClassSymbol) ct.tsym;
				StringBuilder sb = new StringBuilder();
				// a nested generic class of a generic class (none in the runtime) is not handled
				sb.append(className(cs));
				List<Type> formals = cs.type.getTypeArguments();
				List<Type> actuals = ct.getTypeArguments();
				if (!formals.isEmpty()) {
					sb.append('<');
					if (actuals.isEmpty()) sb.append(formals.stream().map(x -> "Any?").collect(Collectors.joining(", ")));     // a raw type
					else sb.append(actuals.stream().map(J2Kt::targ).collect(Collectors.joining(", ")));
					sb.append('>');
				}
				return nn ? sb.toString() : sb + "?";
			}
			case WILDCARD: return targ(t);
			default:
				if (t instanceof CapturedType cap) return kt(cap.wildcard.type != null ? cap.wildcard.type : syms.objectType, nn);
				if (t.isIntersection()) return kt(((IntersectionClassType) t).supertype_field != null ? ((IntersectionClassType) t).supertype_field : syms.objectType, nn);
				throw new Fail("type " + t + " " + tag(t));
		}
	}

	/** A type argument: a type variable as itself, a wildcard as a projection, anything else nullable. */
	static String targ(Type t) {
		if (t instanceof WildcardType w) {
			if (w.kind == BoundKind.UNBOUND) return "*";
			if (w.kind == BoundKind.EXTENDS) return "out " + targ(w.type);
			return "in " + targ(w.type);
		}
		if (t instanceof CapturedType cap) return targ(cap.wildcard);
		if (tag(t) == TypeTag.TYPEVAR) return t.tsym.name.toString();
		if (prim(t)) return primName(t);
		return kt(t, false);
	}

	/** The default value of a field of type t. */
	static String zero(Type t) {
		switch (tag(t)) {
			case BOOLEAN: return "false"; case BYTE: return "0.toByte()"; case SHORT: return "0.toShort()"; case CHAR: return "'\\u0000'";
			case INT: return "0"; case LONG: return "0L"; case FLOAT: return "0.0f"; case DOUBLE: return "0.0";
			default: return tag(t) == TypeTag.TYPEVAR ? "null as " + t.tsym.name : "null";
		}
	}

	// ------------------------------------------------------------------------------------------------ literals
	static String intLit(long v, TypeTag t) {
		switch (t) {
			case LONG: return v == Long.MIN_VALUE ? "Long.MIN_VALUE" : v < 0 ? "(" + v + "L)" : v + "L";
			case INT: return v == Integer.MIN_VALUE ? "Int.MIN_VALUE" : v < 0 ? "(" + v + ")" : Long.toString(v);
			case SHORT: return "(" + v + ").toShort()";
			case BYTE: return "(" + v + ").toByte()";
			case CHAR: return charLit((char) v);
			case DOUBLE: return dblLit((double) v);
			case FLOAT: return fltLit((float) v);
			default: throw new Fail("int literal as " + t);
		}
	}
	static String dblLit(double d) {
		if (Double.isNaN(d)) return "Double.fromBits(" + intLit(Double.doubleToRawLongBits(d), TypeTag.LONG) + ")";
		if (Double.isInfinite(d)) return d > 0 ? "Double.POSITIVE_INFINITY" : "Double.NEGATIVE_INFINITY";
		if (d == 0 && 1 / d < 0) return "(-0.0)";
		String s = Double.toString(d);
		return d < 0 ? "(" + s + ")" : s;
	}
	static String fltLit(float f) {
		if (Float.isNaN(f)) return "Float.fromBits(" + intLit(Float.floatToRawIntBits(f), TypeTag.INT) + ")";
		if (Float.isInfinite(f)) return f > 0 ? "Float.POSITIVE_INFINITY" : "Float.NEGATIVE_INFINITY";
		if (f == 0 && 1 / f < 0) return "(-0.0f)";
		String s = Float.toString(f) + "f";
		return f < 0 ? "(" + s + ")" : s;
	}
	static String charLit(char c) {
		switch (c) {
			case '\'': return "'\\''"; case '\\': return "'\\\\'"; case '\n': return "'\\n'"; case '\r': return "'\\r'";
			case '\t': return "'\\t'"; case '\b': return "'\\b'";
		}
		if (c >= 32 && c < 127) return "'" + c + "'";
		return String.format("'\\u%04x'", (int) c);
	}
	static String strLit(String s) {
		StringBuilder sb = new StringBuilder("\"");
		for (char c : s.toCharArray()) {
			switch (c) {
				case '"': sb.append("\\\""); break; case '\\': sb.append("\\\\"); break; case '$': sb.append("\\$"); break;
				case '\n': sb.append("\\n"); break; case '\r': sb.append("\\r"); break; case '\t': sb.append("\\t"); break;
				case '\b': sb.append("\\b"); break;
				default:
					if (c >= 32 && c < 127) sb.append(c); else sb.append(String.format("\\u%04x", (int) c));
			}
		}
		return sb.append('"').toString();
	}

	// ------------------------------------------------------------------------------------------------ the printer
	static final class Printer {
		final JCCompilationUnit cu;
		final StringBuilder out = new StringBuilder();
		Fn enclosing;            // an anonymous class's printer: the method it is in (locals it captures)
		ClassSymbol cur;         // the class whose members are being printed
		Printer(JCCompilationUnit cu) { this.cu = cu; }

		String pos(JCTree t) { return cu.sourcefile.getName() + ":" + cu.getLineMap().getLineNumber(t.pos); }
		Fail fail(JCTree t, String what) { return new Fail(pos(t) + ": " + what + ": " + t.toString().replace('\n', ' ').substring(0, Math.min(120, t.toString().replace('\n', ' ').length()))); }

		String file() {
			StringBuilder h = new StringBuilder();
			h.append("// Generated by kotlin/tools/J2Kt.java from java/src/cpp/").append(Paths.get(cu.sourcefile.toUri()).getFileName()).append(" (the SWMM6 Java port's runtime). Do not edit; regenerate.\n");
			h.append("// The Java's own comments are in the Java file. Written by Claude (Anthropic) for Robert Dickinson's SWMM6 ports.\n");
			h.append("@file:Suppress(\"UNCHECKED_CAST\", \"NAME_SHADOWING\", \"UNUSED_VARIABLE\", \"UNUSED_PARAMETER\", \"UNUSED_VALUE\", \"PLATFORM_CLASS_MAPPED_TO_KOTLIN\",\n")
			 .append("    \"UNNECESSARY_NOT_NULL_ASSERTION\", \"USELESS_CAST\", \"REDUNDANT_PROJECTION\", \"FunctionName\", \"LocalVariableName\", \"ClassName\", \"PropertyName\",\n")
			 .append("    \"ObjectPropertyName\", \"KotlinConstantConditions\", \"ReplaceGetOrSet\", \"VARIABLE_WITH_REDUNDANT_INITIALIZER\", \"DEPRECATION\")\n");
			String pkg = cu.getPackageName() == null ? "" : cu.getPackageName().toString();
			if (!pkg.isEmpty()) h.append("package ").append(pkg).append("\n");
			h.append("\n");
			for (JCTree d : cu.defs) if (d instanceof JCClassDecl c) classDecl(c, 0);
			return h + out.toString();
		}

		void line(int ind, String s) { out.append("\t".repeat(ind)).append(s).append('\n'); }

		// ---------------------------------------------------------------------------------------- classes
		boolean isObject(JCClassDecl c) {
			if ((c.mods.flags & (Flags.INTERFACE | Flags.ABSTRACT | Flags.ENUM)) != 0) return false;
			if (c.extending != null || !c.implementing.isEmpty() || !c.typarams.isEmpty()) return false;
			if (c.sym.owner.kind == Kinds.Kind.MTH || c.sym.name.isEmpty() || instantiated.contains(c.sym)) return false;
			for (JCTree m : c.defs) {
				if (m instanceof JCMethodDecl md) {
					if (md.sym.isConstructor()) { if ((md.mods.flags & (Flags.PRIVATE | Flags.GENERATEDCONSTR)) == 0) return false; }
					else if ((md.mods.flags & Flags.STATIC) == 0) return false;
				} else if (m instanceof JCVariableDecl vd) { if ((vd.mods.flags & Flags.STATIC) == 0) return false; }
				else if (m instanceof JCBlock) return false;
			}
			return true;
		}

		void classDecl(JCClassDecl c, int ind) {
			ClassSymbol cs = c.sym;
			ClassSymbol saveCur = cur; cur = cs;
			boolean iface = (c.mods.flags & Flags.INTERFACE) != 0;
			boolean obj = isObject(c);
			String tps = typeParams(c.typarams);
			List<JCMethodDecl> ctors = new ArrayList<>();
			for (JCTree m : c.defs) if (m instanceof JCMethodDecl md && md.sym.isConstructor() && (md.mods.flags & Flags.GENERATEDCONSTR) == 0) ctors.add(md);
			StringBuilder head = new StringBuilder();
			if (obj) head.append("object ").append(id(cs.name.toString()));
			else if (iface) {
				boolean fun = isFunInterface(cs);
				head.append(fun ? "fun interface " : "interface ").append(id(cs.name.toString())).append(tps);
			} else {
				if ((c.mods.flags & Flags.ABSTRACT) != 0) head.append("abstract ");
				else if ((c.mods.flags & Flags.FINAL) == 0) head.append("open ");
				head.append("class ").append(id(cs.name.toString())).append(tps);
			}
			List<String> sup = new ArrayList<>();
			if (c.extending != null) sup.add(kt(c.extending.type, true) + (ctors.isEmpty() ? "()" : ""));
			for (JCExpression i : c.implementing) sup.add(kt(i.type, true));
			if (!sup.isEmpty()) head.append(" : ").append(String.join(", ", sup));
			line(ind, head + " {");
			List<JCTree> statics = new ArrayList<>(), inst = new ArrayList<>();
			for (JCTree m : c.defs) {
				boolean st = m instanceof JCVariableDecl vd ? (vd.mods.flags & Flags.STATIC) != 0 || iface
					: m instanceof JCMethodDecl md ? (md.mods.flags & Flags.STATIC) != 0
					: m instanceof JCBlock b ? (b.flags & Flags.STATIC) != 0 : false;
				if (m instanceof JCClassDecl) { inst.add(m); continue; }
				(st && !obj ? statics : inst).add(m);
			}
			for (JCTree m : inst) member(m, c, ind + 1, obj, false);
			List<String> fwd = obj || iface ? List.of() : inheritedStatics(c);
			if (!statics.isEmpty() || !fwd.isEmpty()) {
				line(ind + 1, "companion object {");
				for (JCTree m : statics) member(m, c, ind + 2, true, true);
				for (String f : fwd) line(ind + 2, f);
				line(ind + 1, "}");
			}
			line(ind, "}");
			cur = saveCur;
		}

		/** Java lets a class's static members be named through a subclass (the engine calls cpp.R.getenv, declared in
		 *  RFs, which R extends); Kotlin does not inherit a companion's members, so the subclass's companion gets
		 *  forwarders: a function calling the superclass's, a property reading and writing its field. */
		List<String> inheritedStatics(JCClassDecl c) {
			List<String> out = new ArrayList<>();
			Set<String> own = new HashSet<>();
			for (Symbol m : c.sym.members().getSymbols()) own.add(m.name.toString());
			for (Type st = types.supertype(c.sym.type); st != null && st.getTag() == TypeTag.CLASS; st = types.supertype(st)) {
				ClassSymbol sc = (ClassSymbol) st.tsym;
				if (!sc.getQualifiedName().toString().startsWith("cpp.")) break;
				List<Symbol> ms = new ArrayList<>();
				for (Symbol m : sc.members().getSymbols()) ms.add(m);
				Collections.reverse(ms);
				for (Symbol m : ms) {
					if (!m.isStatic() || (m.flags() & Flags.PRIVATE) != 0 || !vis(m).isEmpty() || own.contains(m.name.toString() + "#" + m.type)) continue;
					String nm = id(m.name.toString());
					String owner = staticOwner(sc);
					if (m instanceof MethodSymbol ms2 && !ms2.isConstructor() && !ms2.name.contentEquals("<clinit>")) {
						if (own.contains(m.name.toString()) && c.sym.members().getSymbolsByName(m.name).iterator().hasNext()
							&& java.util.stream.StreamSupport.stream(c.sym.members().getSymbolsByName(m.name).spliterator(), false).anyMatch(x -> types.isSameType(types.erasure(x.type), types.erasure(m.type)))) continue;
						own.add(m.name.toString() + "#" + m.type);
						String tps = ms2.getTypeParameters().isEmpty() ? "" : "<" + ms2.getTypeParameters().stream().map(tp -> tp.name.toString()).collect(Collectors.joining(", ")) + "> ";
						List<String> ps = new ArrayList<>(), as = new ArrayList<>();
						int k = 0;
						for (VarSymbol p : ms2.params()) {
							String pn = "p" + k++;
							boolean va = ms2.isVarArgs() && k == ms2.params().size();
							if (va) { ps.add("vararg " + pn + ": " + targ(((ArrayType) p.type).elemtype)); as.add("*" + pn); }
							else { ps.add(pn + ": " + kt(p.type)); as.add(pn); }
						}
						String rt = kt(ms2.getReturnType());
						out.add("@JvmStatic fun " + tps + nm + "(" + String.join(", ", ps) + ")" + (rt.equals("Unit") ? "" : ": " + rt) + " { "
							+ (rt.equals("Unit") ? "" : "return ") + owner + "." + nm + "(" + String.join(", ", as) + ") }");
					} else if (m instanceof VarSymbol v) {
						if (own.contains(m.name.toString())) continue;
						own.add(m.name.toString());
						boolean fin = (v.flags() & Flags.FINAL) != 0;
						out.add((fin ? "val " : "var ") + nm + ": " + kt(v.type) + "\n\t\t\tget() = " + owner + "." + nm
							+ (fin ? "" : "\n\t\t\tset(v) { " + owner + "." + nm + " = v }"));
					}
				}
			}
			return out;
		}

		boolean isFunInterface(ClassSymbol cs) {
			int abs = 0;
			for (Symbol m : cs.members().getSymbols()) {
				if (m instanceof MethodSymbol ms && (ms.flags() & Flags.ABSTRACT) != 0 && (ms.flags() & Flags.DEFAULT) == 0) {
					abs++;
					if (!ms.getTypeParameters().isEmpty()) return false;
				}
			}
			return abs == 1 && cs.getInterfaces().isEmpty();
		}

		String typeParams(com.sun.tools.javac.util.List<JCTypeParameter> tps) {
			if (tps.isEmpty()) return "";
			return "<" + tps.stream().map(tp -> {
				String s = tp.name.toString();
				if (!tp.bounds.isEmpty()) s += " : " + kt(tp.bounds.head.type);
				return s;
			}).collect(Collectors.joining(", ")) + ">";
		}

		void member(JCTree m, JCClassDecl c, int ind, boolean isStatic, boolean companion) {
			if (m instanceof JCClassDecl cd) { classDecl(cd, ind); return; }
			if (m instanceof JCVariableDecl vd) { field(vd, c, ind, isStatic); return; }
			if (m instanceof JCMethodDecl md) {
				if ((md.mods.flags & Flags.GENERATEDCONSTR) != 0) return;
				if (md.sym.isConstructor()) { if (!isObject(c)) ctor(md, c, ind); return; }
				method(md, c, ind, isStatic);
				return;
			}
			if (m instanceof JCBlock b) {
				Fn f = new Fn(this, null);
				line(ind, "init {");
				f.block(b.stats, ind + 1);
				line(ind, "}");
				return;
			}
			throw fail(m, "member");
		}

		String vis(Symbol s) {
			if ((s.flags() & Flags.PRIVATE) != 0 && !crossPrivate.contains(s)) return "private ";
			if ((s.flags() & (Flags.PRIVATE | Flags.PUBLIC | Flags.PROTECTED)) == 0 && s.owner instanceof ClassSymbol oc
				&& (oc.flags() & Flags.INTERFACE) == 0 && (s.kind == Kinds.Kind.MTH || s.kind == Kinds.Kind.VAR) && !isObjectClass(oc)) {
				Set<ClassSymbol> us = pkgUsers.getOrDefault(s, Set.of());
				boolean own = true, sub = true;
				for (ClassSymbol u : us) {
					boolean in = false;
					for (Symbol c = u; c != null; c = c.owner) if (c == oc) { in = true; break; }
					if (!in) own = false;
					if (!in && !u.isSubClass(oc, types)) sub = false;
				}
				if (s instanceof MethodSymbol ms && !ms.isConstructor() && !ms.isStatic() && overriddenAnywhere(ms)) return "";
				if (own) return "private ";
				if (sub && !s.isStatic()) return "protected ";
			}
			return "";
		}

		/** A class printed as an object (its members are not in a class a subclass could see). */
		static boolean isObjectClass(ClassSymbol c) { return objects.contains(c); }

		/** A method some other method overrides or implements (it must stay visible). */
		static boolean overriddenAnywhere(MethodSymbol m) { return overridden.contains(m); }

		void field(JCVariableDecl vd, JCClassDecl c, int ind, boolean isStatic) {
			VarSymbol s = vd.sym;
			boolean fin = (vd.mods.flags & Flags.FINAL) != 0 && vd.init != null;
			boolean iface = (c.mods.flags & Flags.INTERFACE) != 0;
			String v = vis(s);
			StringBuilder sb = new StringBuilder();
			if (v.isEmpty()) sb.append("@JvmField "); else sb.append(v);
			if ((vd.mods.flags & Flags.VOLATILE) != 0) sb.append("@Volatile ");
			sb.append(fin || iface ? "val " : "var ").append(id(s.name.toString())).append(": ").append(kt(s.type)).append(" = ");
			if (vd.init != null) {
				Fn f = enclosing != null ? new Fn(enclosing, null, null, this) : new Fn(this, null);
				String e = f.expr(vd.init, s.type);
				if (!f.pre.isEmpty()) throw fail(vd, "field initializer with statements");
				sb.append(e);
			} else sb.append(zero(s.type));
			line(ind, sb.toString());
		}

		String params(Fn f, com.sun.tools.javac.util.List<JCVariableDecl> ps) {
			return ps.stream().map(p -> {
				String n = f.declare(p.sym);
				boolean va = (p.mods.flags & Flags.VARARGS) != 0;
				if (va) { f.varargs.add(p.sym); return "vararg " + n + ": " + targ(((ArrayType) p.sym.type).elemtype); }
				String pn = assignedParams.contains(p.sym) ? n + "__p" : n;
				// an Object[] parameter takes any array of references (Kotlin's arrays are invariant): it is declared
				// Array<*> and read as Array<Any?>, so the engine passes an Array<CPtr?> to Ptr(Object[] a, long o)
				boolean objArr = p.sym.type.getTag() == TypeTag.ARRAY && ((ArrayType) p.sym.type).elemtype.tsym == syms.objectType.tsym;
				if (objArr) {
					if (!pn.equals(n)) f.prologue.add("var " + n + ": " + kt(p.sym.type) + " = " + pn + " as " + kt(p.sym.type));
					else f.objArrays.add(p.sym);
					return pn + ": Array<*>?";
				}
				if (!pn.equals(n)) f.prologue.add("var " + n + ": " + kt(p.sym.type) + " = " + pn);
				return pn + ": " + kt(p.sym.type);
			}).collect(Collectors.joining(", "));
		}

		boolean overrides(MethodSymbol m) {
			ClassSymbol owner = (ClassSymbol) m.owner;
			if (m.isStatic() || m.isConstructor()) return false;
			for (Type st : types.closure(owner.type)) {
				if (st.tsym == owner) continue;
				for (Symbol x : st.tsym.members().getSymbolsByName(m.name)) {
					if (x instanceof MethodSymbol xm && !xm.isStatic() && m.overrides(xm, owner, types, false)) return true;
				}
			}
			return false;
		}

		Fn newFn(MethodSymbol ms) { return enclosing != null ? new Fn(enclosing, null, null, this) : new Fn(this, ms); }

		void method(JCMethodDecl md, JCClassDecl c, int ind, boolean isStatic) {
			MethodSymbol ms = md.sym;
			boolean iface = (c.mods.flags & Flags.INTERFACE) != 0;
			Fn f = newFn(ms);
			StringBuilder sb = new StringBuilder();
			boolean ov = overrides(ms);
			if (isStatic) sb.append("@JvmStatic ");
			if ((md.mods.flags & Flags.SYNCHRONIZED) != 0) sb.append("@Synchronized ");
			String v = vis(ms);
			sb.append(v);
			if (!isStatic) {
				if ((md.mods.flags & Flags.ABSTRACT) != 0 && !iface) sb.append("abstract ");
				if (ov) sb.append("override ");
				else if (!iface && !v.equals("private ") && (md.mods.flags & (Flags.ABSTRACT | Flags.PRIVATE | Flags.FINAL)) == 0 && (c.mods.flags & Flags.FINAL) == 0 && c.sym.name.length() > 0) sb.append("open ");
			}
			sb.append("fun ");
			String tps = typeParams(md.typarams);
			if (!tps.isEmpty()) sb.append(tps).append(' ');
			sb.append(id(ms.name.toString())).append('(').append(params(f, md.params)).append(')');
			String rt = retType(ms);
			if (!rt.equals("Unit")) sb.append(": ").append(rt);
			if (md.body == null) { line(ind, sb.toString()); return; }
			f.retType = ms.getReturnType();
			f.retNonNull = rt.equals("String") || rt.startsWith("Iterator<") || rt.equals("Int") && ms.name.contentEquals("hashCode");
			line(ind, sb + " {");
			for (String p : f.prologue) line(ind + 1, p);
			boolean ends = f.block(md.body.stats, ind + 1);
			if (ends && !rt.equals("Unit")) line(ind + 1, "throw IllegalStateException(\"unreachable\")");
			line(ind, "}");
		}

		/** The return type; overrides of kotlin.Any's toString and of Iterable.iterator must say non-null. */
		String retType(MethodSymbol ms) {
			Type r = ms.getReturnType();
			if (ms.name.contentEquals("toString") && ms.type.getParameterTypes().isEmpty()) return "String";
			if (ms.name.contentEquals("iterator") && ms.type.getParameterTypes().isEmpty() && overrides(ms)) return kt(r, true);
			if (ms.name.contentEquals("hasNext") || ms.name.contentEquals("equals") && ms.type.getParameterTypes().size() == 1) return kt(r);
			if (ms.name.contentEquals("next") && ms.type.getParameterTypes().isEmpty() && overrides(ms)) return targ(r);
			return kt(r);
		}

		void ctor(JCMethodDecl md, JCClassDecl c, int ind) {
			MethodSymbol ms = md.sym;
			Fn f = new Fn(this, ms);
			StringBuilder sb = new StringBuilder(vis(ms)).append("constructor(").append(params(f, md.params)).append(")");
			List<JCStatement> stats = new ArrayList<>(md.body.stats);
			String deleg = null;
			if (!stats.isEmpty() && stats.get(0) instanceof JCExpressionStatement es && es.expr instanceof JCMethodInvocation mi) {
				com.sun.tools.javac.util.Name n = TreeInfo.name(mi.meth);
				if (n.contentEquals("super") || n.contentEquals("this")) {
					String a = f.args(mi);
					if (!f.pre.isEmpty()) throw fail(md, "constructor call with statements");
					deleg = n + "(" + a + ")";
					stats.remove(0);
				}
			}
			if (deleg == null && c.extending != null) deleg = "super()";
			if (deleg == null && c.extending == null) deleg = null;
			if (deleg != null) sb.append(" : ").append(deleg);
			line(ind, sb + " {");
			for (String p : f.prologue) line(ind + 1, p);
			f.block(com.sun.tools.javac.util.List.from(stats), ind + 1);
			line(ind, "}");
		}
	}

	// ------------------------------------------------------------------------------------------------ method bodies
	static final class Fn {
		final Printer P;
		final MethodSymbol method;
		final List<String> pre = new ArrayList<>();          // statements an expression needs before it
		final List<String> prologue = new ArrayList<>();
		final Map<Symbol, String> names;
		final Set<String> used;
		final Set<Symbol> varargs = new HashSet<>();
		final Set<Symbol> objArrays = new HashSet<>();     // Object[] parameters, declared Array<*>
		final int[] counter;
		Type retType;
		boolean retNonNull;
		String lambdaLabel;      // inside a lambda: its label, for return@label
		final Map<Symbol, String> bindings = new HashMap<>();   // instanceof pattern variables
		final Deque<Object[]> loops;     // {tree, label, updates (List<String>) or null}

		Fn(Printer p, MethodSymbol m) {
			P = p; method = m; names = new HashMap<>(); used = new HashSet<>(); counter = new int[1]; loops = new ArrayDeque<>();
		}
		Fn(Fn parent, String label, Type ret) { this(parent, label, ret, parent.P); }
		Fn(Fn parent, String label, Type ret, Printer p) {
			P = p; method = parent.method; names = parent.names; used = parent.used; counter = parent.counter;
			loops = new ArrayDeque<>(); lambdaLabel = label; retType = ret;
			bindings.putAll(parent.bindings);
			varargs.addAll(parent.varargs);
			objArrays.addAll(parent.objArrays);
		}

		String fresh(String b) { counter[0]++; return "__" + b + counter[0]; }

		String declare(Symbol s) {
			String n = s.name.toString();
			if (KW.contains(n) || n.equals("it") || n.equals("field")) n = n + "_";
			String b = n; int k = 2;
			while (used.contains(n)) n = b + "_" + k++;
			used.add(n);
			names.put(s, n);
			return n;
		}

		String name(Symbol s) {
			String n = names.get(s);
			if (n == null) throw new Fail("unknown local " + s.name);
			return n;
		}

		void flush(List<String> lines, int ind) { for (String p : pre) lines.add("\t".repeat(ind) + p); pre.clear(); }

		// ---------------------------------------------------------------------------------------- statements
		/** Print statements at indent ind into the printer; returns whether the last can complete normally. */
		boolean block(List<JCStatement> stats, int ind) {
			List<String> lines = new ArrayList<>();
			boolean ends = stmts(stats, lines, ind);
			for (String l : lines) P.out.append(l).append('\n');
			return ends;
		}

		boolean stmts(List<JCStatement> stats, List<String> lines, int ind) {
			boolean ends = true;
			for (JCStatement s : stats) {
				if (!ends) break;
				ends = stmt(s, lines, ind);
			}
			return ends;
		}

		String I(int n) { return "\t".repeat(n); }

		boolean stmt(JCStatement s, List<String> L, int ind) {
			String p = I(ind);
			if (s instanceof JCBlock b) return stmts(b.stats, L, ind);       // names are unique per method: flattened
			if (s instanceof JCVariableDecl vd) {
				String n = declare(vd.sym);
				if (vd.init == null) { L.add(p + "var " + n + ": " + kt(vd.sym.type)); return true; }
				String e = expr(vd.init, vd.sym.type);
				flush(L, ind);
				L.add(p + "var " + n + ": " + kt(vd.sym.type) + " = " + e);
				return true;
			}
			if (s instanceof JCExpressionStatement es) {
				String e = stmtExpr(es.expr);
				flush(L, ind);
				if (!e.isEmpty()) L.add(p + e);
				return true;
			}
			if (s instanceof JCReturn r) {
				String e = r.expr == null ? null : expr(r.expr, retType);
				if (e != null && retNonNull && !prim(retType)) e = "(" + e + ")!!";
				flush(L, ind);
				L.add(p + "return" + (lambdaLabel != null ? "@" + lambdaLabel : "") + (e != null ? " " + e : ""));
				return false;
			}
			if (s instanceof JCIf i) {
				String c = cond(i.cond);
				flush(L, ind);
				L.add(p + "if (" + c + ") {");
				boolean a = stmt(i.thenpart, L, ind + 1);
				if (i.elsepart == null) { L.add(p + "}"); return true; }
				L.add(p + "} else {");
				boolean b = stmt(i.elsepart, L, ind + 1);
				L.add(p + "}");
				return a || b;
			}
			if (s instanceof JCWhileLoop w) {
				String lab = fresh("L");
				String c = cond(w.cond);
				if (!pre.isEmpty()) {
					List<String> cp = new ArrayList<>(pre); pre.clear();
					L.add(p + lab + "@ while (true) {");
					for (String x : cp) L.add(I(ind + 1) + x);
					L.add(I(ind + 1) + "if (!(" + c + ")) break@" + lab);
				} else L.add(p + lab + "@ while (" + c + ") {");
				loops.push(new Object[] { w, lab, null });
				stmt(w.body, L, ind + 1);
				loops.pop();
				L.add(p + "}");
				return !isTrue(w.cond) || hasBreak(w);
			}
			if (s instanceof JCDoWhileLoop d) {
				String lab = fresh("L");
				L.add(p + lab + "@ do {");
				loops.push(new Object[] { d, lab, null });
				stmt(d.body, L, ind + 1);
				loops.pop();
				String c = cond(d.cond);
				if (!pre.isEmpty()) throw P.fail(d, "do-while condition with statements");
				L.add(p + "} while (" + c + ")");
				return !isTrue(d.cond) || hasBreak(d);
			}
			if (s instanceof JCForLoop f) {
				for (JCStatement i : f.init) stmt(i, L, ind);
				String lab = fresh("L");
				String c = f.cond == null ? "true" : cond(f.cond);
				List<String> cp = new ArrayList<>(pre); pre.clear();
				List<String> ups = new ArrayList<>();
				for (JCExpressionStatement u : f.step) {
					String e = stmtExpr(u.expr);
					ups.addAll(pre); pre.clear();
					if (!e.isEmpty()) ups.add(e);
				}
				if (!cp.isEmpty()) {
					L.add(p + lab + "@ while (true) {");
					for (String x : cp) L.add(I(ind + 1) + x);
					L.add(I(ind + 1) + "if (!(" + c + ")) break@" + lab);
				} else L.add(p + lab + "@ while (" + c + ") {");
				loops.push(new Object[] { f, lab, ups });
				boolean bodyEnds = stmt(f.body, L, ind + 1);
				loops.pop();
				if (bodyEnds) for (String u : ups) L.add(I(ind + 1) + u);
				L.add(p + "}");
				return f.cond != null && !isTrue(f.cond) || hasBreak(f);
			}
			if (s instanceof JCEnhancedForLoop ef) {
				String lab = fresh("L");
				String coll = expr(ef.expr, null);
				flush(L, ind);
				String v = declare(ef.var.sym);
				Type ct = ef.expr.type;
				String src = ct.getTag() == TypeTag.ARRAY ? coll + "!!" : coll + "!!";
				String raw = fresh("e");
				L.add(p + lab + "@ for (" + raw + " in " + src + ") {");
				Type et = ct.getTag() == TypeTag.ARRAY ? ((ArrayType) ct).elemtype : iterElem(ct);
				L.add(I(ind + 1) + "var " + v + ": " + kt(ef.var.sym.type) + " = " + conv(raw, et, ef.var.sym.type, null));
				loops.push(new Object[] { ef, lab, null });
				stmt(ef.body, L, ind + 1);
				loops.pop();
				L.add(p + "}");
				return true;
			}
			if (s instanceof JCBreak b) {
				Object[] t = target(b.target, b);
				L.add(p + "break@" + t[1]);
				return false;
			}
			if (s instanceof JCContinue c) {
				Object[] t = target(c.target, c);
				@SuppressWarnings("unchecked") List<String> ups = (List<String>) t[2];
				if (ups != null) for (String u : ups) L.add(p + u);
				L.add(p + "continue@" + t[1]);
				return false;
			}
			if (s instanceof JCThrow t) {
				String e = expr(t.expr, null);
				flush(L, ind);
				L.add(p + "throw " + e + "!!");
				return false;
			}
			if (s instanceof JCSwitch sw) return sw(sw, L, ind);
			if (s instanceof JCTry t) return tryStmt(t, L, ind);
			if (s instanceof JCSynchronized sy) {
				String e = expr(sy.lock, null);
				flush(L, ind);
				L.add(p + "synchronized(" + e + "!!) {");
				boolean r = stmts(sy.body.stats, L, ind + 1);
				L.add(p + "}");
				return r;
			}
			if (s instanceof JCSkip) return true;
			if (s instanceof JCLabeledStatement ls) throw P.fail(s, "labelled statement");
			throw P.fail(s, "statement " + s.getKind());
		}

		Type iterElem(Type ct) {
			Type it = types.asSuper(ct, syms.iterableType.tsym);
			if (it == null || it.getTypeArguments().isEmpty()) return syms.objectType;
			Type a = it.getTypeArguments().head;
			return types.wildUpperBound(a);
		}

		static boolean isTrue(JCExpression c) { return c == null || c.type != null && c.type.constValue() instanceof Integer i && i == 1; }

		boolean hasBreak(JCTree loop) {
			boolean[] r = new boolean[1];
			new TreeScanner<Void, Void>() {
				@Override public Void visitBreak(BreakTree b, Void v) { if (((JCBreak) b).target == loop) r[0] = true; return null; }
			}.scan(loop, null);
			return r[0];
		}

		Object[] target(JCTree t, JCTree from) {
			for (Object[] l : loops) if (l[0] == t) return l;
			throw P.fail(from, "jump target not found");
		}

		boolean sw(JCSwitch sw, List<String> L, int ind) {
			String p = I(ind);
			String sel = expr(sw.selector, null);
			flush(L, ind);
			Type st = sw.selector.type;
			String lab = fresh("sw");
			// cases with empty bodies fall into the next: merged; a body that can complete normally falls into the next
			// body: that body is appended (C's fall-through written out); a break ends a case
			List<JCCase> cs = sw.cases;
			L.add(p + lab + "@ do {");
			L.add(I(ind + 1) + "when (" + (prim(st) ? sel : sel + "!!") + ") {");
			loops.push(new Object[] { sw, lab, null });
			boolean anyEnds = false;
			boolean hasDefault = false;
			List<String> labels = new ArrayList<>();
			String defaultBranch = null;
			for (int k = 0; k < cs.size(); k++) {
				JCCase c = cs.get(k);
				boolean isDef = false;
				for (JCCaseLabel cl : c.labels) {
					if (cl instanceof JCDefaultCaseLabel) isDef = true;
					else if (cl instanceof JCConstantCaseLabel ccl) labels.add(expr(ccl.expr, st));
					else throw P.fail(c, "case label");
				}
				if (isDef) hasDefault = true;
				if (c.stats.isEmpty() && k + 1 < cs.size()) { if (isDef) defaultBranch = "pending"; continue; }
				List<String> body = new ArrayList<>();
				boolean ends = true;
				int j = k;
				while (true) {
					List<JCStatement> stats = new ArrayList<>(cs.get(j).stats);
					boolean last = !stats.isEmpty() && stats.get(stats.size() - 1) instanceof JCBreak br && br.target == sw;
					if (last) stats.remove(stats.size() - 1);
					ends = stmts(stats, body, ind + 3);
					if (last) { ends = true; break; }
					if (!ends || j + 1 >= cs.size()) break;
					j++;     // fall through into the next case's body
				}
				if (ends) anyEnds = true;
				String head = (isDef || "pending".equals(defaultBranch)) ? "else" : String.join(", ", labels);
				if ((isDef || "pending".equals(defaultBranch)) && !labels.isEmpty() && !isDef) head = "else";
				if (head.equals("else")) {
					StringBuilder sb = new StringBuilder(I(ind + 2) + "else -> {\n");
					for (String b : body) sb.append(b).append('\n');
					sb.append(I(ind + 2)).append("}");
					defaultBranch = sb.toString();
				} else {
					L.add(I(ind + 2) + head + " -> {");
					L.addAll(body);
					L.add(I(ind + 2) + "}");
				}
				labels.clear();
			}
			loops.pop();
			if (defaultBranch != null && !defaultBranch.equals("pending")) L.add(defaultBranch);
			L.add(I(ind + 1) + "}");
			L.add(p + "} while (false)");
			return anyEnds || !hasDefault || hasBreak(sw);
		}

		boolean tryStmt(JCTry t, List<String> L, int ind) {
			String p = I(ind);
			List<String> res = new ArrayList<>();
			for (JCTree r : t.resources) {
				if (!(r instanceof JCVariableDecl vd)) throw P.fail(t, "try resource");
				stmt(vd, L, ind);
				res.add(name(vd.sym));
			}
			L.add(p + "try {");
			boolean ends = stmts(t.body.stats, L, ind + 1);
			for (JCCatch c : t.catchers) {
				String n = declare(c.param.sym);
				List<Type> alts = c.param.vartype instanceof JCTypeUnion u ? u.alternatives.stream().map(x -> x.type).toList() : List.of(c.param.sym.type);
				for (Type at : alts) {
					L.add(p + "} catch (" + n + ": " + kt(at, true) + ") {");
					if (stmts(c.body.stats, L, ind + 1)) ends = true;
				}
			}
			if (t.finalizer != null || !res.isEmpty()) {
				L.add(p + "} finally {");
				for (int i = res.size() - 1; i >= 0; i--) L.add(I(ind + 1) + res.get(i) + "?.close()");
				if (t.finalizer != null && !stmts(t.finalizer.stats, L, ind + 1)) ends = false;
			}
			L.add(p + "}");
			return ends;
		}

		// ---------------------------------------------------------------------------------------- expressions as statements
		String stmtExpr(JCExpression e) {
			e = (JCExpression) TreeInfo.skipParens(e);
			if (e instanceof JCAssign a) return assign(a.lhs, expr(a.rhs, a.lhs.type));
			if (e instanceof JCAssignOp a) return compound(a, true);
			if (e instanceof JCUnary u && isIncDec(u)) return incdec(u, true);
			if (e instanceof JCMethodInvocation || e instanceof JCNewClass) return expr(e, null);
			throw P.fail(e, "expression statement");
		}

		static boolean isIncDec(JCUnary u) {
			JCTree.Tag g = u.getTag();
			return g == JCTree.Tag.PREINC || g == JCTree.Tag.PREDEC || g == JCTree.Tag.POSTINC || g == JCTree.Tag.POSTDEC;
		}

		/** A Kotlin assignable expression for a Java lvalue. */
		String lvalue(JCExpression e) {
			e = (JCExpression) TreeInfo.skipParens(e);
			if (e instanceof JCIdent id) return identRef(id);
			if (e instanceof JCFieldAccess fa) return fieldRef(fa);
			if (e instanceof JCArrayAccess aa) return recv(aa.indexed) + "[" + expr(aa.index, syms.intType) + "]";
			throw P.fail(e, "lvalue");
		}

		/** An lvalue is safe to evaluate twice (op= written as x = x op y). */
		boolean pure(JCExpression e) {
			e = (JCExpression) TreeInfo.skipParens(e);
			if (e instanceof JCIdent || e instanceof JCLiteral) return true;
			if (e instanceof JCFieldAccess fa) return pure(fa.selected) || isTypeOrPkg(fa.selected);
			if (e instanceof JCArrayAccess aa) return pure(aa.indexed) && pure(aa.index);
			if (e instanceof JCBinary b) return pure(b.lhs) && pure(b.rhs);
			return false;
		}

		String assign(JCExpression lhs, String v) { return lvalue(lhs) + " = " + v; }

		String compound(JCAssignOp a, boolean stmt) {
			Type lt = a.lhs.type;
			JCTree.Tag op = a.getTag().noAssignOp();
			if (!pure(a.lhs)) {
				// evaluate the array and index once: x[i] op= v with temporaries
				JCExpression l = (JCExpression) TreeInfo.skipParens(a.lhs);
				if (l instanceof JCArrayAccess aa) {
					String arr = fresh("a"), idx = fresh("i");
					pre.add("val " + arr + " = " + recv(aa.indexed));
					pre.add("val " + idx + " = " + expr(aa.index, syms.intType));
					String cur = arr + "[" + idx + "]";
					String v = combine(op, cur, lt, a.rhs, a.type);
					if (stmt) return cur + " = " + v;
					return "(" + v + ").also { __v -> " + cur + " = __v }";
				}
				throw P.fail(a, "op= on an impure place");
			}
			String place = lvalue(a.lhs);
			String v = combine(op, place, lt, a.rhs, a.type);
			if (stmt) return place + " = " + v;
			return "(" + v + ").also { __v -> " + place + " = __v }";
		}

		/** cur op rhs computed by Java's rules for op= (promotion, then narrowing to the target's type). */
		String combine(JCTree.Tag op, String cur, Type lt, JCExpression rhs, Type resT) {
			if (isString(lt) && op == JCTree.Tag.PLUS) return cur + " + " + expr(rhs, null);
			Type lu = isBoxed(lt) ? types.unboxedType(lt) : lt;
			Type rt = rhs.type;
			Type ru = isBoxed(rt) ? types.unboxedType(rt) : rt;
			Type pt;
			if (op == JCTree.Tag.SL || op == JCTree.Tag.SR || op == JCTree.Tag.USR) pt = promote1(lu);
			else if (tag(lu) == TypeTag.BOOLEAN) pt = lu;
			else pt = promote2(lu, ru);
			String l = conv(isBoxed(lt) ? cur + "!!" : cur, lu, pt, null);
			String r = (op == JCTree.Tag.SL || op == JCTree.Tag.SR || op == JCTree.Tag.USR) ? shiftCount(rhs) : expr(rhs, pt);
			String v = binop(op, l, r, pt);
			return conv(v, pt, lu, null);
		}

		String incdec(JCUnary u, boolean stmt) {
			Type t = u.arg.type;
			JCTree.Tag g = u.getTag();
			boolean inc = g == JCTree.Tag.PREINC || g == JCTree.Tag.POSTINC;
			boolean post = g == JCTree.Tag.POSTINC || g == JCTree.Tag.POSTDEC;
			if (!prim(t)) throw P.fail(u, "++ on a boxed value");
			if (!pure(u.arg)) {
				JCExpression l = (JCExpression) TreeInfo.skipParens(u.arg);
				if (l instanceof JCArrayAccess aa) {
					String arr = fresh("a"), idx = fresh("i");
					pre.add("val " + arr + " = " + recv(aa.indexed));
					pre.add("val " + idx + " = " + expr(aa.index, syms.intType));
					return (post ? "" : (inc ? "++" : "--")) + arr + "[" + idx + "]" + (post ? (inc ? "++" : "--") : "");
				}
				throw P.fail(u, "++ on an impure place");
			}
			String place = lvalue(u.arg);
			if (stmt) return place + (inc ? "++" : "--");
			return "(" + (post ? place + (inc ? "++" : "--") : (inc ? "++" : "--") + place) + ")";
		}

		// ---------------------------------------------------------------------------------------- expressions
		String cond(JCExpression c) { return strip(expr(c, syms.booleanType)); }

		static String strip(String s) {
			if (s.length() > 1 && s.charAt(0) == '(' && s.charAt(s.length() - 1) == ')' && balanced(s.substring(1, s.length() - 1))) return s.substring(1, s.length() - 1);
			return s;
		}
		static boolean balanced(String s) {
			int d = 0; boolean q = false; char qc = 0;
			for (int i = 0; i < s.length(); i++) {
				char ch = s.charAt(i);
				if (q) { if (ch == '\\') i++; else if (ch == qc) q = false; continue; }
				if (ch == '"' || ch == '\'') { q = true; qc = ch; continue; }
				if (ch == '(') d++; else if (ch == ')') { if (--d < 0) return false; }
			}
			return d == 0;
		}
		static boolean simple(String s) {
			for (int i = 0; i < s.length(); i++) { char c = s.charAt(i); if (!(Character.isLetterOrDigit(c) || c == '_' || c == '.' || c == '`')) return false; }
			return !s.isEmpty() && !Character.isDigit(s.charAt(0));
		}
		static String paren(String s) { return simple(s) || s.startsWith("(") && s.endsWith(")") && balanced(s.substring(1, s.length() - 1)) ? s : "(" + s + ")"; }

		/** e converted to type target (null: e's own type). */
		String expr(JCExpression e, Type target) {
			if (target != null && numeric(target) && e.type != null && numeric(e.type) && e.type.constValue() != null)
				return constAs(e.type.constValue(), e.type, target);
			String s = x(e, target);
			Type from = e.type;
			if (e instanceof JCLambda || e instanceof JCMemberReference) {
				// a lambda given to a raw functional interface type (Less.Fn)
				if (target != null && e.type != null && target.isRaw() && !types.isSameType(e.type, target)) return "(" + s + " as " + kt(target) + ")";
				return s;
			}
			if (target != null && from != null) s = conv(s, from, target, e);
			return s;
		}

		boolean isTypeOrPkg(JCExpression e) {
			Symbol s = TreeInfo.symbol(e);
			return s != null && (s.kind == Kinds.Kind.TYP || s.kind == Kinds.Kind.PCK);
		}

		/** The code of a receiver: !! unless it cannot be null. */
		String recv(JCExpression e) {
			JCExpression u = (JCExpression) TreeInfo.skipParens(e);
			if (isTypeOrPkg(u)) return staticName(u);
			String s = x(u, null);
			if (prim(u.type)) return paren(s);
			if (u instanceof JCIdent id && (id.name.contentEquals("this") || id.name.contentEquals("super"))) return s;
			if (u instanceof JCFieldAccess fa && (fa.name.contentEquals("this"))) return s;
			if (u instanceof JCNewClass || u instanceof JCLiteral) return paren(s);
			return paren(s) + "!!";
		}

		String staticName(JCExpression e) {
			Symbol s = TreeInfo.symbol(e);
			if (s instanceof ClassSymbol cs) return staticOwner(cs);
			return e.toString();
		}

		String x(JCExpression e, Type target) {
			if (e instanceof JCParens p) return "(" + strip(x(p.expr, target)) + ")";
			if (e instanceof JCLiteral lit) return literal(lit, target);
			if (e.type != null && e.type.constValue() != null && !(e instanceof JCIdent) && !(e instanceof JCFieldAccess) && prim(e.type))
				return constant(e.type.constValue(), e.type);
			if (e instanceof JCIdent id) {
				if (bindings.containsKey(id.sym)) return bindings.get(id.sym);
				return identRef(id);
			}
			if (e instanceof JCFieldAccess fa) {
				if (fa.name.contentEquals("length") && fa.selected.type != null && fa.selected.type.getTag() == TypeTag.ARRAY) return recv(fa.selected) + ".size";
				if (fa.name.contentEquals("class")) return classLit(fa.selected.type);
				return fieldRef(fa);
			}
			if (e instanceof JCArrayAccess aa) return recv(aa.indexed) + "[" + strip(expr(aa.index, syms.intType)) + "]";
			if (e instanceof JCMethodInvocation mi) return call(mi);
			if (e instanceof JCNewClass nc) return newClass(nc);
			if (e instanceof JCNewArray na) return newArray(na, target);
			if (e instanceof JCTypeCast tc) return cast(tc);
			if (e instanceof JCInstanceOf io) return instOf(io);
			if (e instanceof JCConditional c) {
				Type t = c.type;
				String cc = cond(c.cond);
				List<String> save = new ArrayList<>(pre); pre.clear();
				String a = expr(c.truepart, t);
				List<String> pa = new ArrayList<>(pre); pre.clear();
				String b = expr(c.falsepart, t);
				List<String> pb = new ArrayList<>(pre); pre.clear();
				pre.addAll(save);
				if (!pa.isEmpty() || !pb.isEmpty()) {
					String tv = fresh("t");
					pre.add("var " + tv + ": " + kt(t));
					pre.add("if (" + cc + ") {"); pre.addAll(pa); pre.add(tv + " = " + a);
					pre.add("} else {"); pre.addAll(pb); pre.add(tv + " = " + b); pre.add("}");
					return tv;
				}
				return "(if (" + cc + ") " + strip(a) + " else " + strip(b) + ")";
			}
			if (e instanceof JCBinary b) return binary(b);
			if (e instanceof JCUnary u) return unary(u);
			if (e instanceof JCAssign a) {
				String v = expr(a.rhs, a.lhs.type);
				String place = lvalue(a.lhs);
				return "(" + v + ").also { __v -> " + place + " = __v }";
			}
			if (e instanceof JCAssignOp a) return compound(a, false);
			if (e instanceof JCLambda l) return lambda(l);
			if (e instanceof JCMemberReference mr) return memberRef(mr);
			throw P.fail(e, "expression " + e.getKind());
		}

		String classLit(Type t) {
			if (prim(t)) return "java.lang." + switch (tag(t)) { case INT -> "Integer"; case CHAR -> "Character"; default -> primName(t); } + ".TYPE";
			return kt(types.erasure(t), true).replaceAll("<.*>", "<*>") + "::class.java";
		}

		String literal(JCLiteral lit, Type target) {
			Object v = lit.getValue();
			if (v == null) {
				if (target != null && tag(target) == TypeTag.TYPEVAR) return "(null as " + target.tsym.name + ")";
				return "null";
			}
			return constant(lit.type.constValue() != null ? lit.type.constValue() : v, lit.type, target);
		}
		String constant(Object v, Type t) { return constant(v, t, null); }

		/** A numeric constant converted to a numeric type by Java's casting rules, written as that type's literal. */
		static String constAs(Object v, Type from, Type to) {
			long l; double d; boolean fl = tag(from) == TypeTag.DOUBLE || tag(from) == TypeTag.FLOAT;
			if (tag(from) == TypeTag.CHAR) { l = v instanceof Character c ? c : ((Number) v).intValue(); d = l; }
			else { Number n = (Number) v; l = n.longValue(); d = tag(from) == TypeTag.FLOAT ? n.floatValue() : n.doubleValue(); }
			switch (tag(to)) {
				case DOUBLE: return dblLit(fl ? d : (double) l);
				case FLOAT: return fltLit(fl ? (float) d : (float) l);
				case LONG: return intLit(fl ? (long) d : l, TypeTag.LONG);
				case INT: return intLit(fl ? (int) d : (int) l, TypeTag.INT);
				case SHORT: return intLit(fl ? (short) (int) d : (short) l, TypeTag.SHORT);
				case BYTE: return intLit(fl ? (byte) (int) d : (byte) l, TypeTag.BYTE);
				case CHAR: return charLit(fl ? (char) (int) d : (char) l);
				default: throw new Fail("constant as " + to);
			}
		}
		String constant(Object v, Type t, Type target) {
			TypeTag tt = tag(t);
			// an integer literal in a wider numeric context is written as the wider literal
			TypeTag want = target != null && numeric(target) && (tt == TypeTag.INT || tt == TypeTag.LONG || tt == TypeTag.CHAR) ? tag(target) : tt;
			switch (tt) {
				case BOOLEAN: return ((Integer) v) != 0 ? "true" : "false";
				case CHAR:
					if (want != TypeTag.CHAR) return intLit(((Number) v).intValue(), want);
					return charLit((char) ((Number) v).intValue());
				case BYTE: case SHORT: case INT: return intLit(((Number) v).longValue(), want);
				case LONG: return want == TypeTag.DOUBLE ? dblLit((double) ((Number) v).longValue()) : want == TypeTag.FLOAT ? fltLit((float) ((Number) v).longValue()) : intLit(((Number) v).longValue(), TypeTag.LONG);
				case FLOAT: return want == TypeTag.DOUBLE ? dblLit((double) ((Number) v).floatValue()) : fltLit(((Number) v).floatValue());
				case DOUBLE: return dblLit(((Number) v).doubleValue());
				case CLASS: if (v instanceof String s) return strLit(s);
			}
			throw new Fail("constant " + v + " of " + t);
		}

		String identRef(JCIdent id) {
			Symbol s = id.sym;
			if (id.name.contentEquals("this")) return "this";
			if (id.name.contentEquals("super")) return "super";
			if (s.kind == Kinds.Kind.VAR && (s.owner.kind == Kinds.Kind.MTH || s.owner.kind == Kinds.Kind.VAR)) {
				String n = name(s);
				if (varargs.contains(s) || objArrays.contains(s)) return "(" + n + " as " + kt(s.type) + ")";
				return n;
			}
			if (s.kind == Kinds.Kind.VAR) {
				if (s.isStatic()) return staticOwner((ClassSymbol) s.owner) + "." + id(s.name.toString());
				return thisFor(s.owner) + "." + id(s.name.toString());
			}
			if (s.kind == Kinds.Kind.TYP) return staticOwner((ClassSymbol) s);
			throw P.fail(id, "identifier");
		}

		/** `this` for a member of class owner seen from the current class (this@Outer from an inner or anonymous class). */
		String thisFor(Symbol owner) {
			ClassSymbol c = P.cur;
			if (c == null || owner == null) return "this";
			if (c == owner || c.isSubClass(owner, types)) return "this";
			// a member of an enclosing class, used from a nested anonymous class
			for (Symbol o = c.owner; o != null; o = o.owner) {
				ClassSymbol oc = o instanceof ClassSymbol x ? x : o.enclClass();
				if (oc != null && (oc == owner || oc.isSubClass(owner, types))) return "this@" + oc.name;
				if (o instanceof PackageSymbol) break;
			}
			return "this";
		}

		String fieldRef(JCFieldAccess fa) {
			Symbol s = fa.sym;
			if (fa.name.contentEquals("this")) {
				return "this@" + ((ClassSymbol) TreeInfo.symbol(fa.selected)).name;
			}
			if (isTypeOrPkg(fa.selected)) {
				if (s.kind == Kinds.Kind.TYP) return staticOwner((ClassSymbol) s);
				return staticOwner((ClassSymbol) s.owner) + "." + id(s.name.toString());
			}
			if (s.isStatic()) return staticOwner((ClassSymbol) s.owner) + "." + id(s.name.toString());
			return recv(fa.selected) + "." + id(s.name.toString());
		}

		String shiftCount(JCExpression r) {
			Type rt = isBoxed(r.type) ? types.unboxedType(r.type) : r.type;
			Type pt = promote1(rt);
			String s = expr(r, pt);
			return tag(pt) == TypeTag.LONG ? paren(s) + ".toInt()" : s;
		}

		Type promote1(Type t) {
			switch (tag(t)) { case BYTE: case SHORT: case CHAR: case INT: return syms.intType; default: return t; }
		}
		Type promote2(Type a, Type b) {
			if (tag(a) == TypeTag.DOUBLE || tag(b) == TypeTag.DOUBLE) return syms.doubleType;
			if (tag(a) == TypeTag.FLOAT || tag(b) == TypeTag.FLOAT) return syms.floatType;
			if (tag(a) == TypeTag.LONG || tag(b) == TypeTag.LONG) return syms.longType;
			return syms.intType;
		}

		String binop(JCTree.Tag op, String l, String r, Type t) {
			switch (op) {
				case PLUS: return "(" + l + " + " + r + ")";
				case MINUS: return "(" + l + " - " + r + ")";
				case MUL: return "(" + l + " * " + r + ")";
				case DIV: return "(" + l + " / " + r + ")";
				case MOD: return "(" + l + " % " + r + ")";
				case BITAND: return "(" + l + " and " + r + ")";
				case BITOR: return "(" + l + " or " + r + ")";
				case BITXOR: return "(" + l + " xor " + r + ")";
				case SL: return "(" + l + " shl " + r + ")";
				case SR: return "(" + l + " shr " + r + ")";
				case USR: return "(" + l + " ushr " + r + ")";
				default: throw new Fail("operator " + op);
			}
		}

		String binary(JCBinary b) {
			JCTree.Tag op = b.getTag();
			Type lt = b.lhs.type, rt = b.rhs.type;
			if (op == JCTree.Tag.AND || op == JCTree.Tag.OR) {
				String l = expr(b.lhs, syms.booleanType);
				List<String> save = new ArrayList<>(pre); pre.clear();
				String r = expr(b.rhs, syms.booleanType);
				if (!pre.isEmpty()) throw P.fail(b, "&& or || whose right side needs statements");
				pre.addAll(save);
				return "(" + l + (op == JCTree.Tag.AND ? " && " : " || ") + r + ")";
			}
			if (op == JCTree.Tag.PLUS && (isString(lt) || isString(rt))) {
				String l = expr(b.lhs, null), r = expr(b.rhs, null);
				if (!isString(lt)) l = "\"\" + " + l;
				return "(" + l + " + " + r + ")";
			}
			if (op == JCTree.Tag.EQ || op == JCTree.Tag.NE) {
				boolean eq = op == JCTree.Tag.EQ;
				if (!prim(lt) && !prim(rt) && !(isBoxed(lt) && prim(rt)) || (tag(lt) == TypeTag.BOT || tag(rt) == TypeTag.BOT)) {
					return "(" + expr(b.lhs, null) + (eq ? " === " : " !== ") + expr(b.rhs, null) + ")";
				}
			}
			Type lu = isBoxed(lt) ? types.unboxedType(lt) : lt;
			Type ru = isBoxed(rt) ? types.unboxedType(rt) : rt;
			if (tag(lu) == TypeTag.BOOLEAN && tag(ru) == TypeTag.BOOLEAN) {
				String l = expr(b.lhs, syms.booleanType), r = expr(b.rhs, syms.booleanType);
				switch (op) {
					case EQ: return "(" + l + " == " + r + ")";
					case NE: return "(" + l + " != " + r + ")";
					case BITAND: return "(" + l + " and " + r + ")";
					case BITOR: return "(" + l + " or " + r + ")";
					case BITXOR: return "(" + l + " xor " + r + ")";
					default: throw P.fail(b, "boolean operator");
				}
			}
			if (op == JCTree.Tag.SL || op == JCTree.Tag.SR || op == JCTree.Tag.USR) {
				Type pt = promote1(lu);
				return binop(op, expr(b.lhs, pt), shiftCount(b.rhs), pt);
			}
			// two chars compared: Kotlin compares Chars directly
			if (tag(lu) == TypeTag.CHAR && tag(ru) == TypeTag.CHAR && isCompare(op))
				return "(" + expr(b.lhs, syms.charType) + " " + cmpOp(op) + " " + expr(b.rhs, syms.charType) + ")";
			Type pt = promote2(lu, ru);
			String l = expr(b.lhs, pt), r = expr(b.rhs, pt);
			if (isCompare(op)) return "(" + l + " " + cmpOp(op) + " " + r + ")";
			return binop(op, l, r, pt);
		}

		static boolean isCompare(JCTree.Tag op) {
			return op == JCTree.Tag.EQ || op == JCTree.Tag.NE || op == JCTree.Tag.LT || op == JCTree.Tag.GT || op == JCTree.Tag.LE || op == JCTree.Tag.GE;
		}
		static String cmpOp(JCTree.Tag op) {
			switch (op) { case EQ: return "=="; case NE: return "!="; case LT: return "<"; case GT: return ">"; case LE: return "<="; default: return ">="; }
		}

		String unary(JCUnary u) {
			JCTree.Tag g = u.getTag();
			if (isIncDec(u)) return incdec(u, false);
			Type t = isBoxed(u.arg.type) ? types.unboxedType(u.arg.type) : u.arg.type;
			switch (g) {
				case NOT: return "(!" + paren(expr(u.arg, syms.booleanType)) + ")";
				case NEG: { Type pt = promote1(t); return "(-" + paren(expr(u.arg, pt)) + ")"; }
				case POS: { Type pt = promote1(t); return expr(u.arg, pt); }
				case COMPL: { Type pt = promote1(t); return paren(expr(u.arg, pt)) + ".inv()"; }
				default: throw P.fail(u, "unary");
			}
		}

		String cast(JCTypeCast tc) {
			Type to = tc.clazz.type;
			Type from = tc.expr.type;
			String s = x(tc.expr, null);
			if (prim(to)) {
				if (prim(from)) return conv(s, from, to, tc.expr);
				// unboxing cast: (double) obj
				return conv("(" + s + " as " + kt(types.boxedClass(to).type, true) + ")", types.boxedClass(to).type, to, null);
			}
			if (prim(from)) return conv(s, from, to, tc.expr);      // boxing cast
			if (tag(to) == TypeTag.TYPEVAR) return "(" + s + " as " + to.tsym.name + ")";
			return "(" + s + " as " + kt(to) + ")";
		}

		String instOf(JCInstanceOf io) {
			Type t = io.pattern instanceof JCBindingPattern bp ? bp.var.sym.type : io.pattern.type;
			String e = x(io.expr, null);
			Type et = types.erasure(t);
			String ty;
			if (tag(et) == TypeTag.CLASS) {
				int np = et.tsym.type.getTypeArguments().size();
				ty = className((ClassSymbol) et.tsym) + (np == 0 ? "" : "<" + String.join(", ", Collections.nCopies(np, "*")) + ">");
			} else if (tag(et) == TypeTag.ARRAY && !prim(((ArrayType) et).elemtype)) ty = "Array<*>";
			else ty = kt(et, true);
			if (io.pattern instanceof JCBindingPattern bp) {
				if (!simple(e)) {
					// evaluated once, into a temporary before the statement (both such patterns in the runtime are the
					// whole condition of an if, so nothing in the statement is evaluated before it)
					String tv = fresh("io");
					pre.add("val " + tv + " = " + e);
					e = tv;
				}
				bindings.put(bp.var.sym, "(" + e + " as " + kt(t, true) + ")");
			}
			return "(" + e + " is " + ty + ")";
		}

		// ---- conversions (Java's implicit ones, written out)
		String conv(String s, Type from, Type to, JCExpression e) {
			if (from == null || to == null) return s;
			if (tag(to) == TypeTag.VOID || tag(from) == TypeTag.VOID) return s;
			if (types.isSameType(from, to)) return s;
			if (prim(to) && prim(from)) return numConv(s, from, to);
			if (prim(to)) {
				// unboxing (then maybe widening)
				Type ub = types.unboxedType(from);
				if (!ub.isPrimitive()) {
					// Object or Number to a primitive (a cast was printed): unbox by the target's box
					return "(" + s + " as " + kt(types.boxedClass(to).type, true) + ")";
				}
				return numConv(paren(s) + "!!", ub, to);
			}
			if (prim(from)) {
				// boxing: to the box of from (Kotlin boxes implicitly), or to Object/Number
				Type bx = types.boxedClass(from).type;
				if (isBoxed(to) && !types.isSameType(types.unboxedType(to), from)) return numConv(s, from, types.unboxedType(to));
				return s;
			}
			if (tag(from) == TypeTag.BOT) return tag(to) == TypeTag.TYPEVAR ? "(null as " + to.tsym.name + ")" : s;
			// reference to reference: Kotlin's arrays are invariant where Java's are covariant; a type variable, a raw
			// type or a generic type Java converts unchecked needs Kotlin's cast
			if (tag(to) == TypeTag.ARRAY && tag(from) == TypeTag.ARRAY && !types.isSameType(from, to))
				return "(" + s + " as " + kt(to) + ")";
			if (tag(to) == TypeTag.TYPEVAR && !types.isSameType(from, to)) return "(" + s + " as " + to.tsym.name + ")";
			if (tag(to) == TypeTag.CLASS && tag(from) == TypeTag.CLASS && !types.isSubtype(types.erasure(from), types.erasure(to)))
				return "(" + s + " as " + kt(to) + ")";
			if (tag(to) == TypeTag.CLASS && !to.getTypeArguments().isEmpty() && !types.isAssignable(from, to))
				return "(" + s + " as " + kt(to) + ")";
			if (tag(to) == TypeTag.CLASS && !to.getTypeArguments().isEmpty() && tag(from) == TypeTag.CLASS && from.isRaw())
				return "(" + s + " as " + kt(to) + ")";
			if (tag(to) == TypeTag.CLASS && to.isRaw() && tag(from) == TypeTag.CLASS && !from.isRaw() && !from.getTypeArguments().isEmpty())
				return "(" + s + " as " + kt(to) + ")";
			return s;
		}

		String numConv(String s, Type from, Type to) {
			TypeTag f = tag(from), t = tag(to);
			if (f == t) return s;
			if (t == TypeTag.BOOLEAN || f == TypeTag.BOOLEAN) throw new Fail("boolean conversion " + from + " -> " + to);
			String p = paren(s);
			if (f == TypeTag.CHAR) {
				if (t == TypeTag.INT) return p + ".code";
				return p + ".code." + toFn(t);
			}
			if (t == TypeTag.CHAR) {
				if (f == TypeTag.INT) return p + ".toChar()";
				return p + "." + toFn(f == TypeTag.LONG || f == TypeTag.DOUBLE || f == TypeTag.FLOAT || f == TypeTag.SHORT || f == TypeTag.BYTE ? TypeTag.INT : TypeTag.INT) + ".toChar()";
			}
			if ((f == TypeTag.DOUBLE || f == TypeTag.FLOAT) && (t == TypeTag.SHORT || t == TypeTag.BYTE)) return p + ".toInt()." + toFn(t);
			return p + "." + toFn(t);
		}

		static String toFn(TypeTag t) {
			switch (t) {
				case INT: return "toInt()"; case LONG: return "toLong()"; case DOUBLE: return "toDouble()"; case FLOAT: return "toFloat()";
				case SHORT: return "toShort()"; case BYTE: return "toByte()"; default: throw new Fail("toFn " + t);
			}
		}

		// ---- calls
		String args(JCMethodInvocation mi) {
			MethodSymbol ms = (MethodSymbol) TreeInfo.symbol(mi.meth);
			Type mt = mi.meth.type;
			List<Type> pts = mt instanceof MethodType m ? m.getParameterTypes() : ms.type.getParameterTypes();
			return argList(mi.args, pts, ms, mi.varargsElement);
		}

		String argList(List<JCExpression> args, List<Type> pts, MethodSymbol ms, Type varargsElem) {
			List<String> out = new ArrayList<>();
			for (int i = 0; i < args.size(); i++) {
				JCExpression a = args.get(i);
				Type pt;
				boolean spread = false;
				if (ms != null && ms.isVarArgs() && i >= pts.size() - 1) {
					if (varargsElem != null) pt = varargsElem;
					else { pt = pts.get(pts.size() - 1); spread = true; }
				} else pt = i < pts.size() ? pts.get(i) : null;
				String s = expr(a, pt);
				out.add(spread ? "*" + paren(s) + (s.endsWith("!!") ? "" : "!!") : strip(s));
			}
			return String.join(", ", out);
		}

		String call(JCMethodInvocation mi) {
			MethodSymbol ms = (MethodSymbol) TreeInfo.symbol(mi.meth);
			String name = ms.name.toString();
			String targs = mi.typeargs.isEmpty() ? "" : "<" + mi.typeargs.stream().map(t -> targ(t.type)).collect(Collectors.joining(", ")) + ">";
			ClassSymbol owner = (ClassSymbol) ms.owner;
			String oq = owner.getQualifiedName().toString();
			String recvCode;
			JCExpression sel = mi.meth instanceof JCFieldAccess fa ? fa.selected : null;
			if (mi.meth instanceof JCFieldAccess) {
				if (isTypeOrPkg(sel)) recvCode = staticOwner(owner);
				else if (ms.isStatic()) recvCode = staticOwner(owner);
				else recvCode = recv(sel);
			} else {
				if (ms.isStatic()) recvCode = staticOwner(owner);
				else recvCode = name.equals("super") || name.equals("this") ? null : thisFor(owner);
			}
			// java.lang and java.util methods that Kotlin spells differently
			String special = special(mi, ms, oq, recvCode, sel);
			if (special != null) return special;
			String a = args(mi);
			return (recvCode == null ? "" : recvCode + ".") + id(name) + targs + "(" + a + ")";
		}

		String special(JCMethodInvocation mi, MethodSymbol ms, String oq, String r, JCExpression sel) {
			String n = ms.name.toString();
			int np = ms.type.getParameterTypes().size();
			Type rt = sel != null ? sel.type : null;
			boolean onString = rt != null && isString(rt) || oq.equals("java.lang.String") && !ms.isStatic();
			boolean onCharSeq = oq.equals("java.lang.CharSequence") || oq.equals("java.lang.AbstractStringBuilder") || oq.equals("java.lang.StringBuilder");
			if (!ms.isStatic() && r != null) {
				if (n.equals("getClass") && np == 0) return r + ".javaClass";
				if ((onString || onCharSeq) && n.equals("length") && np == 0) return r + ".length";
				if ((onString || onCharSeq) && n.equals("charAt") && np == 1) return r + "[" + strip(expr(mi.args.head, syms.intType)) + "]";
				if (onString && n.equals("equals") && np == 1) return "(" + r + " == " + expr(mi.args.head, null) + ")";
				if (onString && n.equals("getBytes") && np == 1) return r + ".toByteArray(" + expr(mi.args.head, null) + "!!)";
				if (onString && (n.equals("indexOf") || n.equals("lastIndexOf")) && np >= 1 && tag(ms.type.getParameterTypes().head) == TypeTag.INT) {
					JCExpression a0 = mi.args.head;
					String c = tag(a0.type) == TypeTag.CHAR ? expr(a0, syms.charType) : expr(a0, syms.intType) + ".toChar()";
					return r + "." + n + "(" + c + (np > 1 ? ", " + expr(mi.args.get(1), syms.intType) : "") + ")";
				}
				if (onString && Set.of("trim", "toLowerCase", "toUpperCase", "split", "matches", "replaceAll", "intern", "isBlank", "strip", "chars", "format", "formatted", "codePointAt").contains(n))
					return "(" + r + " as java.lang.String)." + n + "(" + args(mi) + ")";
				boolean num = isBoxedOwner(oq) || rt != null && tag(rt) == TypeTag.CLASS
					&& types.closure(rt).stream().anyMatch(st -> st.tsym.getQualifiedName().contentEquals("java.lang.Number"));
				if (num && n.endsWith("Value") && np == 0) {
					String k = n.substring(0, n.length() - 5);
					String to = switch (k) { case "int" -> "toInt()"; case "long" -> "toLong()"; case "double" -> "toDouble()"; case "float" -> "toFloat()"; case "short" -> "toShort()"; case "byte" -> "toByte()"; case "char" -> ""; case "boolean" -> ""; default -> null; };
					if (to != null) return to.isEmpty() ? r : r + "." + to;
				}
				boolean thr = rt != null && types.isSubtype(types.erasure(rt), syms.throwableType) || oq.equals("java.lang.Throwable");
				if (thr && n.equals("getMessage") && np == 0) return r + ".message";
				if (thr && n.equals("getCause") && np == 0) return r + ".cause";
				if (thr && n.equals("getLocalizedMessage") && np == 0) return r + ".localizedMessage";
				if ((oq.equals("java.util.ArrayList") || oq.equals("java.util.List") || oq.equals("java.util.Collection") || oq.equals("java.util.Set")
					|| oq.equals("java.util.HashMap") || oq.equals("java.util.Map") || oq.equals("java.util.AbstractCollection") || oq.equals("java.util.LinkedHashSet")
					|| oq.equals("java.util.HashSet") || oq.equals("java.util.IdentityHashMap") || oq.equals("java.util.AbstractMap") || oq.equals("java.util.concurrent.ConcurrentHashMap")) && n.equals("size") && np == 0)
					return r + ".size";
				if ((oq.equals("java.util.ArrayList") || oq.equals("java.util.List")) && n.equals("remove") && np == 1 && tag(ms.type.getParameterTypes().head) == TypeTag.INT)
					return r + ".removeAt(" + expr(mi.args.head, syms.intType) + ")";
				if (n.equals("toArray") && np == 1 && oq.startsWith("java.util.")) return r + ".toTypedArray()";
				if (Set.of("addAll", "putAll", "containsAll", "removeAll", "retainAll").contains(n) && np == 1 && oq.startsWith("java.util."))
					return r + "." + n + "(" + paren(expr(mi.args.head, null)) + "!!)";
				if (oq.endsWith("Map") && n.equals("keySet") && np == 0) return r + ".keys";
				if (oq.endsWith("Map") && n.equals("values") && np == 0) return r + ".values";
				if (oq.endsWith("Map") && n.equals("entrySet") && np == 0) return r + ".entries";
				if (oq.equals("java.util.Map.Entry") && n.equals("getKey")) return r + ".key";
				if (oq.equals("java.util.Map.Entry") && n.equals("getValue")) return r + ".value";
			}
			if (ms.isStatic() && oq.equals("java.lang.String") && n.equals("valueOf") && np == 1 && tag(ms.type.getParameterTypes().head) == TypeTag.CHAR)
				return expr(mi.args.head, syms.charType) + ".toString()";
			return null;
		}

		static boolean isBoxedOwner(String q) {
			return q.equals("java.lang.Integer") || q.equals("java.lang.Long") || q.equals("java.lang.Double") || q.equals("java.lang.Float")
				|| q.equals("java.lang.Short") || q.equals("java.lang.Byte") || q.equals("java.lang.Character") || q.equals("java.lang.Boolean") || q.equals("java.lang.Number");
		}

		String newClass(JCNewClass nc) {
			MethodSymbol ctor = (MethodSymbol) nc.constructor;
			Type t = nc.type;
			List<Type> pts = nc.constructorType instanceof MethodType m ? m.getParameterTypes() : ctor.type.getParameterTypes();
			String a = argList(nc.args, pts, ctor, nc.varargsElement);
			if (nc.def != null) {
				// an anonymous class: an object expression
				JCClassDecl d = nc.def;
				Type st = types.supertype(d.sym.type);
				boolean iface = !d.sym.getInterfaces().isEmpty();
				String sup = iface ? kt(d.sym.getInterfaces().head, true) : kt(st, true) + "(" + a + ")";
				StringBuilder sb = new StringBuilder("object : " + sup + " {\n");
				Printer sub = new Printer(P.cu);
				sub.enclosing = this;
				sub.cur = d.sym;
				// members of the object, printed with the method printer of a fresh Fn that shares this method's names
				for (JCTree m : d.defs) {
					if (m instanceof JCMethodDecl md && (md.mods.flags & Flags.GENERATEDCONSTR) == 0 && !md.sym.isConstructor()) sub.method(md, d, 1, false);
					else if (m instanceof JCVariableDecl vd) sub.field(vd, d, 1, false);
					else if (m instanceof JCMethodDecl) { }
					else throw P.fail(m, "anonymous class member");
				}
				sb.append(sub.out).append("}");
				return sb.toString();
			}
			String tn = kt(t, true);
			if (isString(t)) {
				// Kotlin's String(...) functions take non-null arrays
				List<String> as = new ArrayList<>();
				for (int i = 0; i < nc.args.size(); i++) { String v = strip(expr(nc.args.get(i), pts.get(i))); as.add(prim(nc.args.get(i).type) ? v : paren(v) + "!!"); }
				a = String.join(", ", as);
			}
			return tn + "(" + a + ")";
		}

		String newArray(JCNewArray na, Type target) {
			ArrayType at = (ArrayType) na.type;
			Type et = at.elemtype;
			if (na.elems != null) {
				String els = na.elems.stream().map(e -> strip(expr(e, et))).collect(Collectors.joining(", "));
				if (prim(et)) return primName(et).toLowerCase() + "ArrayOf(" + els + ")";
				return "arrayOf<" + targ(et) + ">(" + els + ")";
			}
			if (na.dims.size() == 1) {
				String n = strip(expr(na.dims.head, syms.intType));
				if (prim(et)) return primName(et) + "Array(" + n + ")";
				return "arrayOfNulls<" + kt(et, true) + ">(" + n + ") as " + kt(at, true);
			}
			if (na.dims.size() == 2) {
				String n = strip(expr(na.dims.head, syms.intType)), m = strip(expr(na.dims.get(1), syms.intType));
				Type e2 = ((ArrayType) et).elemtype;
				String inner = prim(e2) ? primName(e2) + "Array(" + m + ")" : "arrayOfNulls<" + kt(e2, true) + ">(" + m + ")";
				return "Array<" + targ(et) + ">(" + n + ") { " + inner + " }";
			}
			throw P.fail(na, "array creation");
		}

		// ---- lambdas and method references
		String lambda(JCLambda l) {
			Type ft = l.type;
			String lab = fresh("lam");
			MethodSymbol desc = (MethodSymbol) types.findDescriptorSymbol(ft.tsym);
			Type dt = types.findDescriptorType(ft);
			Fn sub = new Fn(this, lab, dt.getReturnType());
			List<String> ps = new ArrayList<>();
			for (JCVariableDecl p : l.params) {
				String n = sub.declare(p.sym);
				ps.add(n);
			}
			StringBuilder sb = new StringBuilder(samName(ft) + " " + lab + "@{ " + String.join(", ", ps) + (ps.isEmpty() ? "" : " ->") + "\n");
			for (JCVariableDecl p : l.params) if (assignedParams.contains(p.sym)) throw P.fail(l, "lambda assigns its parameter");
			if (l.body instanceof JCExpression be) {
				String e = tag(dt.getReturnType()) == TypeTag.VOID ? sub.stmtExpr(be) : sub.expr(be, dt.getReturnType());
				List<String> lines = new ArrayList<>();
				sub.flush(lines, 1);
				for (String s : lines) sb.append(s).append('\n');
				sb.append("\t").append(e).append("\n}");
			} else {
				List<String> lines = new ArrayList<>();
				boolean ends = sub.stmts(((JCBlock) l.body).stats, lines, 1);
				for (String s : lines) sb.append(s).append('\n');
				if (ends && tag(dt.getReturnType()) != TypeTag.VOID) sb.append("\tthrow IllegalStateException(\"unreachable\")\n");
				sb.append("}");
			}
			return sb.toString();
		}

		/** The SAM constructor of a functional interface type (type arguments without projections). */
		String samName(Type ft) {
			ClassSymbol cs = (ClassSymbol) ft.tsym;
			String n = className(cs);
			if (cs.type.getTypeArguments().isEmpty()) return n;
			return n + "<" + ft.getTypeArguments().stream().map(a -> {
				if (a instanceof WildcardType w) return w.kind == BoundKind.UNBOUND ? "Any?" : targ(w.type);
				if (a instanceof CapturedType c) return c.wildcard.kind == BoundKind.UNBOUND ? "Any?" : targ(c.wildcard.type);
				return targ(a);
			}).collect(Collectors.joining(", ")) + ">";
		}

		String memberRef(JCMemberReference mr) {
			Type ft = mr.type;
			Type dt = types.findDescriptorType(ft);
			List<Type> pts = dt.getParameterTypes();
			List<String> ps = new ArrayList<>();
			for (int i = 0; i < pts.size(); i++) ps.add(fresh("p"));
			MethodSymbol ms = (MethodSymbol) mr.sym;
			String body;
			if (mr.getMode() == MemberReferenceTree.ReferenceMode.NEW) {
				Type t = mr.expr.type;
				if (tag(t) == TypeTag.ARRAY) throw P.fail(mr, "array constructor reference");
				body = kt(t, true) + "(" + String.join(", ", ps) + ")";
			} else if (ms.isStatic()) {
				body = staticOwner((ClassSymbol) ms.owner) + "." + id(ms.name.toString()) + "(" + String.join(", ", ps) + ")";
			} else if (isTypeOrPkg(mr.expr)) {
				// unbound receiver: the first parameter
				body = ps.get(0) + "!!." + id(ms.name.toString()) + "(" + String.join(", ", ps.subList(1, ps.size())) + ")";
			} else {
				body = recv(mr.expr) + "." + id(ms.name.toString()) + "(" + String.join(", ", ps) + ")";
			}
			return samName(ft) + " { " + String.join(", ", ps) + (ps.isEmpty() ? "" : " -> ") + body + " }";
		}
	}
}
