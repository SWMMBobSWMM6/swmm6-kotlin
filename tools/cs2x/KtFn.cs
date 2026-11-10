// KtFn.cs - method bodies: C# statements and expressions (Roslyn IOperation trees) to Kotlin.
// The Java translator's JavaFn.cs, copied and changed where Kotlin differs from Java:
//   * no implicit numeric conversion: every conversion C# makes is written out (.toLong(), .code for a char, ...)
//   * bitwise operators and shifts are infix functions (and, or, xor, shl, shr, ushr, inv)
//   * == on references is ===; every dereference of a reference is `!!` (all reference types are nullable)
//   * an assignment, op= or ++ used as a value is rewritten (also { }); op= with a narrowing or bitwise operator is
//     written x = x op y, with the place's array and index in temporaries when they are not plain names
//   * a C# block is flattened (names are unique per method, and a bare { } is a lambda in Kotlin); a for loop is a
//     while loop whose continues run the steps first; switch is when; every loop and switch has a label and every
//     break and continue names it; the goto methods are a when inside a labelled loop
//   * a lambda is a SAM constructor with a label (return@label); a method reference is a lambda
// Written by Claude (Anthropic) for Robert Dickinson's SWMM6 ports; see AI-USE.md and kotlin/README.md.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

public static partial class Cs2Kotlin
{
    // references to fields (ref this.x, ref o.x, ref S.x): one helper each, in engine/FRef.kt
    static readonly SortedDictionary<string, string> fieldRefs = new();

    static string FieldRefHelper(IFieldSymbol f)
    {
        var owner = TypeIdent(f.ContainingType);
        var name = (f.IsStatic ? "s_" : "f_") + owner + "_" + FieldIdent(f);
        if (!fieldRefs.ContainsKey(name))
        {
            var rt = RefType(f.Type);
            var kt = KType(f.Type);
            var target = f.IsStatic ? owner + "." + FieldIdent(f) : "o!!." + FieldIdent(f);
            var get = PrimCode(f.Type) != null ? kt : KBoxed(f.Type);
            var setv = PrimCode(f.Type) != null ? "v" : "v";
            var body = "object : " + NN(rt) + "() { override fun get(): " + get + " { return " + target + " } override fun set(v: " + get + ") { " + target + " = " + setv + " } }";
            fieldRefs[name] = "\t@JvmStatic fun " + name + "(" + (f.IsStatic ? "" : "o: " + owner + "?") + "): " + rt + " { return " + body + " }\n";
            var sb = new StringBuilder(FileHeader).Append("object FRef {\n");
            foreach (var kv in fieldRefs) sb.Append(kv.Value);
            extraFiles["FRef"] = sb.Append("}\n").ToString();
        }
        return "FRef." + name;
    }
}

public static partial class Cs2Kotlin
{
public sealed class KtFn
{
    readonly IMethodSymbol? method;
    readonly INamedTypeSymbol cls;
    public List<string> Pre = new();
    readonly Dictionary<ISymbol, string> names;
    readonly HashSet<string> used;
    readonly int[] tmp;
    readonly List<string> prologue = new();
    ITypeSymbol? retType;
    bool retRef;
    readonly string? lambdaLabel;
    readonly Stack<string> condAccess = new();
    readonly Stack<string> implicitRecv = new();     // the object of an object initializer
    // the label of each loop and switch (by Roslyn's exit and continue labels), and a for loop's steps
    readonly Dictionary<ILabelSymbol, (string label, List<string>? steps)> jumps;

    public KtFn(IMethodSymbol? m, INamedTypeSymbol c)
    {
        method = m; cls = c;
        names = new(SymbolEqualityComparer.Default); used = new(); tmp = new int[1];
        jumps = new(SymbolEqualityComparer.Default);
        retType = m?.ReturnType; retRef = m != null && m.RefKind != RefKind.None;
    }

    KtFn(KtFn parent, IMethodSymbol lambda, string label)
    {
        lambdaLabel = label;
        method = lambda; cls = parent.cls;
        names = parent.names; used = parent.used; tmp = parent.tmp;
        jumps = new(SymbolEqualityComparer.Default);
        retType = lambda.ReturnType; retRef = lambda.RefKind != RefKind.None;
        gotoLabels = parent.gotoLabels;
    }

    string Fresh(string b) { tmp[0]++; return "__" + b + tmp[0]; }

    string Declare(ISymbol s, string name)
    {
        var n = Id(name);
        if (n == "_" || n == "__") n = "__discard";
        var b = n; int k = 2;
        while (used.Contains(n)) n = b + "_" + k++;
        used.Add(n);
        names[s] = n;
        return n;
    }

    static bool Boxed(ISymbol s) => boxed.Contains(s);

    /// <summary>Declare a parameter and return the name it has in the signature. A cell parameter is renamed and
    /// copied into its cell by the prologue; a parameter of a boxed interface signature is unboxed there; a parameter
    /// the method assigns is copied into a var (Kotlin's parameters are read-only).</summary>
    public string DeclareParam(IParameterSymbol p, bool boxedSig)
    {
        var n = Declare(p, p.Name);
        if (p.RefKind is RefKind.Ref or RefKind.Out) return n;
        if (Boxed(p))
        {
            var pn = n + "__p";
            used.Add(pn);
            prologue.Add("val " + n + ": " + CellType(p.Type) + " = " + CellType(p.Type) + "(" + (boxedSig ? Unbox(pn, p.Type) : pn) + ")");
            return pn;
        }
        if (boxedSig)
        {
            var pn = n + "__b";
            used.Add(pn);
            prologue.Add((mutatedSyms.Contains(p) ? "var " : "val ") + n + ": " + KType(p.Type) + " = " + Unbox(pn, p.Type));
            return pn;
        }
        if (mutatedSyms.Contains(p))
        {
            var pn = n + "__p";
            used.Add(pn);
            prologue.Add("var " + n + ": " + KType(p.Type) + " = " + pn);
            return pn;
        }
        return n;
    }

    static string Unbox(string s, ITypeSymbol t) => PrimCode(t) != null ? s + "!!" : s;

    public string Prologue(string ind) => string.Concat(prologue.Select(h => ind + h + "\n"));

    // -------------------------------------------------------------------------------------------- statements
    string I(int n) => baseInd + new string('\t', n);
    string baseInd = "";

    void Flush(List<string> lines, int ind)
    {
        foreach (var p in Pre) lines.Add(I(ind) + p);
        Pre.Clear();
    }

    // goto: a method whose labels all sit in its top-level block becomes a when over a state inside a labelled loop
    Dictionary<ILabelSymbol, int>? gotoLabels;

    public string Body(IOperation op, IMethodSymbol m, string ind)
    {
        baseInd = ind;
        var lines = new List<string>();
        var ops = op is IBlockOperation b ? b.Operations.ToList() : op is IMethodBodyOperation mb && mb.BlockBody != null ? mb.BlockBody.Operations.ToList() : new List<IOperation> { op };
        if (op.Descendants().Any(d => d is IBranchOperation br && br.BranchKind == BranchKind.GoTo))
            Dispatch(ops, lines, 0);
        else
            Block(ops, lines, 0);
        bool nonUnit = !(m.ReturnsVoid || m.MethodKind == MethodKind.Constructor || m.MethodKind == MethodKind.StaticConstructor);
        if (nonUnit && !EndsInJump(lines)) lines.Add(I(0) + "throw IllegalStateException(\"unreachable\")");
        return string.Concat(lines.Select(l => l + "\n"));
    }

    static bool EndsInJump(List<string> lines)
    {
        if (lines.Count == 0) return false;
        var l = lines[^1].TrimStart();
        return l.StartsWith("return") || l.StartsWith("throw ");
    }

    void Dispatch(List<IOperation> ops, List<string> lines, int ind)
    {
        gotoLabels = new(SymbolEqualityComparer.Default);
        int k = 0;
        foreach (var o in ops) if (o is ILabeledOperation l) gotoLabels[l.Label] = ++k;
        int nseg = k;
        // every local of the method is declared before the loop (a branch cannot hold a declaration the next uses)
        hoisting = true;
        var head = lines.Count;
        lines.Add(I(ind) + "var __st: Int = 0");
        lines.Add(I(ind) + "__dispatch@ while (true) {");
        lines.Add(I(ind + 1) + "when (__st) {");
        var seg = new List<IOperation>();
        int cur = 0;
        void FlushSeg()
        {
            lines.Add(I(ind + 2) + cur + " -> {");
            bool ends = Block(seg, lines, ind + 3);
            if (ends) lines.Add(I(ind + 3) + (cur < nseg ? "__st = " + (cur + 1) + "; continue@__dispatch" : "break@__dispatch"));
            lines.Add(I(ind + 2) + "}");
            seg.Clear();
        }
        foreach (var o in ops)
        {
            if (o is ILabeledOperation l)
            {
                FlushSeg();
                cur = gotoLabels[l.Label];
                if (l.Operation != null) seg.Add(l.Operation);
            }
            else seg.Add(o);
        }
        FlushSeg();
        lines.Add(I(ind + 1) + "}");
        lines.Add(I(ind + 1) + "break@__dispatch");
        lines.Add(I(ind) + "}");
        lines.InsertRange(head, hoisted.Select(h => I(ind) + h));
        hoisting = false;
    }

    bool hoisting;
    readonly List<string> hoisted = new();

    /// <summary>Statements of a block, flattened; those after one that cannot complete normally are dropped.
    /// Returns whether the block can complete normally.</summary>
    bool Block(IEnumerable<IOperation> ops, List<string> lines, int ind)
    {
        var list = ops.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is IUsingDeclarationOperation ud)
                return UsingRest(ud, list.Skip(i + 1).ToList(), lines, ind);
            if (!Stmt(list[i], lines, ind)) return false;
        }
        return true;
    }

    bool Stmt(IOperation op, List<string> lines, int ind)
    {
        try { return StmtInner(op, lines, ind); }
        catch (global::Todo e)
        {
            Pre.Clear();
            lines.Add(I(ind) + "if (true) throw IllegalStateException(\"TODO " + Esc(e.Message) + "\")");
            return true;
        }
    }

    public static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "'").Replace("\n", " ").Replace("$", "\\$");

    bool StmtInner(IOperation op, List<string> lines, int ind)
    {
        var p = I(ind);
        switch (op)
        {
            case IBlockOperation b:
                return Block(b.Operations, lines, ind);      // flattened: local names are unique per method
            case IVariableDeclarationGroupOperation g:
                foreach (var d in g.Declarations)
                    foreach (var v in d.Declarators) LocalDecl(v, d.Initializer, lines, ind);
                return true;
            case IExpressionStatementOperation es:
                {
                    var s = StmtExpr(es.Operation, lines, ind);
                    Flush(lines, ind);
                    if (s.Length > 0) lines.Add(p + s);
                    return true;
                }
            case IReturnOperation r:
                Return(r, lines, ind);
                return false;
            case IConditionalOperation c:
                return If(c, lines, ind);
            case IWhileLoopOperation w: return While(w, lines, ind);
            case IForLoopOperation f: return For(f, lines, ind);
            case IForEachLoopOperation fe: return ForEach(fe, lines, ind);
            case IBranchOperation br: Branch(br, lines, ind); return false;
            case ILabeledOperation l:
                Note("label outside a dispatch method");
                if (l.Operation != null) return Stmt(l.Operation, lines, ind);
                return true;
            case ISwitchOperation sw: return Switch(sw, lines, ind);
            case ITryOperation t: return Try(t, lines, ind);
            case IUsingOperation u: return Using(u, lines, ind);
            case IThrowOperation th:
                {
                    var v = th.Exception == null ? Rethrow() : X(th.Exception);
                    Flush(lines, ind);
                    lines.Add(p + "throw " + Paren(v) + "!!");
                    return false;
                }
            case IEmptyOperation: return true;
            case IUsingDeclarationOperation ud: return UsingRest(ud, new List<IOperation>(), lines, ind);
            case ILocalFunctionOperation: Todo("local function"); return true;
        }
        Todo("statement " + op.Kind);
        return true;
    }

    void LocalDecl(IVariableDeclaratorOperation v, IVariableInitializerOperation? outerInit, List<string> lines, int ind)
    {
        var p = I(ind);
        var loc = v.Symbol;
        var init = v.Initializer ?? outerInit;
        if (loc.RefKind != RefKind.None)
        {
            var a = init == null ? "null" : RefOf(init.Value);
            Flush(lines, ind);
            var n = Declare(loc, loc.Name);
            Emit(lines, p, RefType(loc.Type), n, a);
            return;
        }
        var kt = KType(loc.Type);
        string val = init == null ? DefaultOf(loc.Type) : Value(init.Value, loc.Type);
        Flush(lines, ind);
        var name = Declare(loc, loc.Name);
        if (Boxed(loc))
        {
            var ct = CellType(loc.Type);
            Emit(lines, p, ct, name, ct + "(" + Strip(val) + ")");
            return;
        }
        Emit(lines, p, kt, name, Strip(val));
    }

    void Emit(List<string> lines, string p, string type, string name, string val)
    {
        if (hoisting)
        {
            hoisted.Add("var " + name + ": " + type + " = " + DefaultForKt(type));
            lines.Add(p + name + " = " + val);
            return;
        }
        lines.Add(p + "var " + name + ": " + type + " = " + val);
    }

    static string DefaultForKt(string t) => t switch
    {
        "Boolean" => "false", "Byte" => "(0).toByte()", "Short" => "(0).toShort()", "Char" => "'\\u0000'", "Int" => "0", "Long" => "0L",
        "Float" => "0.0f", "Double" => "0.0", _ => t.EndsWith("?") ? "null" : t + "()"     // a cell: an empty one, replaced when declared
    };

    void Return(IReturnOperation r, List<string> lines, int ind)
    {
        var p = I(ind);
        string? v = null;
        if (r.ReturnedValue != null)
            v = retRef ? RefOf(r.ReturnedValue) : Value(r.ReturnedValue, retType);
        Flush(lines, ind);
        lines.Add(p + "return" + (lambdaLabel != null ? "@" + lambdaLabel : "") + (v != null ? " " + Strip(v) : ""));
    }

    bool If(IConditionalOperation c, List<string> lines, int ind)
    {
        var p = I(ind);
        var cond = X(c.Condition);
        Flush(lines, ind);
        lines.Add(p + "if (" + Strip(cond) + ") {");
        var a = Sub(c.WhenTrue, lines, ind + 1);
        if (c.WhenFalse == null) { lines.Add(p + "}"); return true; }
        lines.Add(p + "} else {");
        var b = Sub(c.WhenFalse, lines, ind + 1);
        lines.Add(p + "}");
        return a || b;
    }

    static string Strip(string s) => s.Length > 1 && s[0] == '(' && s[^1] == ')' && Balanced(s.Substring(1, s.Length - 2)) ? s.Substring(1, s.Length - 2) : s;

    static bool Balanced(string s)
    {
        int d = 0; bool q = false; char qc = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (q) { if (ch == '\\') i++; else if (ch == qc) q = false; continue; }
            if (ch == '"' || ch == '\'') { q = true; qc = ch; continue; }
            if (ch == '(') d++; else if (ch == ')') { d--; if (d < 0) return false; }
        }
        return d == 0;
    }

    bool Sub(IOperation op, List<string> lines, int ind)
    {
        if (op is IBlockOperation b) return Block(b.Operations, lines, ind);
        return Stmt(op, lines, ind);
    }

    static bool HasBreakOut(IOperation loop, ILabelSymbol? exit)
    {
        if (exit == null) return false;
        return loop.Descendants().Any(d => d is IBranchOperation b && b.BranchKind == BranchKind.Break && SymbolEqualityComparer.Default.Equals(b.Target, exit));
    }

    static bool IsTrue(IOperation? c) => c == null || c.ConstantValue.HasValue && c.ConstantValue.Value is true;

    void Enter(ILoopOperation loop, string label, List<string>? steps)
    {
        jumps[loop.ExitLabel] = (label, null);
        jumps[loop.ContinueLabel] = (label, steps);
    }

    bool While(IWhileLoopOperation w, List<string> lines, int ind)
    {
        var p = I(ind);
        var lab = Fresh("L");
        var brk = HasBreakOut(w, w.ExitLabel);
        Enter(w, lab, null);
        if (w.ConditionIsTop)
        {
            var cond = w.Condition == null ? "true" : X(w.Condition);
            if (Pre.Count > 0)
            {
                var pre = new List<string>(Pre); Pre.Clear();
                lines.Add(p + lab + "@ while (true) {");
                foreach (var s in pre) lines.Add(I(ind + 1) + s);
                lines.Add(I(ind + 1) + "if (!(" + cond + ")) break@" + lab);
                Sub(w.Body, lines, ind + 1);
                lines.Add(p + "}");
                return true;
            }
            lines.Add(p + lab + "@ while (" + Strip(cond) + ") {");
            Sub(w.Body, lines, ind + 1);
            lines.Add(p + "}");
            return !IsTrue(w.Condition) || brk;
        }
        lines.Add(p + lab + "@ do {");
        var bodyEnds = Sub(w.Body, lines, ind + 1);
        var c = X(w.Condition!);
        if (Pre.Count > 0) Todo("do-while condition with statements");
        lines.Add(p + "} while (" + Strip(c) + ")");
        var cont = w.Descendants().Any(d => d is IBranchOperation b && b.BranchKind == BranchKind.Continue && SymbolEqualityComparer.Default.Equals(b.Target, w.ContinueLabel));
        return ((bodyEnds || cont) && !IsTrue(w.Condition)) || brk;
    }

    bool For(IForLoopOperation f, List<string> lines, int ind)
    {
        var p = I(ind);
        foreach (var b in f.Before) Stmt(b, lines, ind);           // flattened: the loop variables are unique names
        var lab = Fresh("L");
        var cond = f.Condition == null ? "true" : X(f.Condition);
        var condPre = new List<string>(Pre); Pre.Clear();
        var posts = new List<string>();
        foreach (var a in f.AtLoopBottom)
        {
            var s = a is IExpressionStatementOperation es ? StmtExpr(es.Operation, null, 0) : Todo("for-loop step " + a.Kind);
            posts.AddRange(Pre); Pre.Clear();
            if (s.Length > 0) posts.Add(s);
        }
        Enter(f, lab, posts);
        if (condPre.Count > 0)
        {
            lines.Add(p + lab + "@ while (true) {");
            foreach (var s in condPre) lines.Add(I(ind + 1) + s);
            lines.Add(I(ind + 1) + "if (!(" + cond + ")) break@" + lab);
        }
        else lines.Add(p + lab + "@ while (" + Strip(cond) + ") {");
        var bodyEnds = Sub(f.Body, lines, ind + 1);
        if (bodyEnds) foreach (var s in posts) lines.Add(I(ind + 1) + s);
        lines.Add(p + "}");
        return !IsTrue(f.Condition) || HasBreakOut(f, f.ExitLabel) || condPre.Count > 0;
    }

    bool ForEach(IForEachLoopOperation fe, List<string> lines, int ind)
    {
        var p = I(ind);
        var coll = fe.Collection is IConversionOperation cv && cv.IsImplicit ? cv.Operand : fe.Collection;
        var ct = coll.Type!;
        var src = X(coll);
        Flush(lines, ind);
        if (fe.LoopControlVariable is not IVariableDeclaratorOperation vd) { Todo("foreach variable"); return true; }
        var loc = vd.Symbol;
        string iter;
        if (ct is IArrayTypeSymbol) iter = Paren(src) + "!!";
        else if (ct is INamedTypeSymbol nt && IsRuntime(nt)) { iter = Paren(src) + "!!.Range()!!"; Use("foreach " + nt.OriginalDefinition.ToDisplayString(), "Range() : Iterable"); }
        else if (ct is INamedTypeSymbol ie && IsSystem(ie) && ie.Name == "IEnumerable") iter = Paren(src) + "!!";
        else { Todo("foreach over " + ct.ToDisplayString()); return true; }
        if (Boxed(loc)) Todo("foreach variable in a cell");
        var name = Declare(loc, loc.Name);
        var lab = Fresh("L");
        Enter(fe, lab, null);
        var raw = Fresh("e");
        var kt = KType(loc.Type);
        bool unbox = !(ct is IArrayTypeSymbol) && PrimCode(loc.Type) != null;
        lines.Add(p + lab + "@ for (" + raw + " in " + iter + ") {");
        lines.Add(I(ind + 1) + "var " + name + ": " + kt + " = " + raw + (unbox ? "!!" : ""));
        Sub(fe.Body, lines, ind + 1);
        lines.Add(p + "}");
        return true;
    }

    void Branch(IBranchOperation br, List<string> lines, int ind)
    {
        var p = I(ind);
        switch (br.BranchKind)
        {
            case BranchKind.Break:
                {
                    if (!jumps.TryGetValue(br.Target, out var j)) { Todo("break target"); return; }
                    lines.Add(p + "break@" + j.label);
                    return;
                }
            case BranchKind.Continue:
                {
                    if (!jumps.TryGetValue(br.Target, out var j)) { Todo("continue target"); return; }
                    if (j.steps != null) foreach (var s in j.steps) lines.Add(p + s);
                    lines.Add(p + "continue@" + j.label);
                    return;
                }
            case BranchKind.GoTo:
                if (gotoLabels != null && gotoLabels.TryGetValue(br.Target, out var k)) { lines.Add(p + "__st = " + k + "; continue@__dispatch"); return; }
                Todo("goto outside a dispatch method");
                return;
        }
        Todo("branch " + br.BranchKind);
    }

    bool Switch(ISwitchOperation sw, List<string> lines, int ind)
    {
        var p = I(ind);
        var vt = sw.Value.Type!;
        var v = X(sw.Value);
        Flush(lines, ind);
        bool hasDefault = sw.Cases.Any(c => c.Clauses.Any(cl => cl is IDefaultCaseClauseOperation));
        var lab = Fresh("sw");
        jumps[sw.ExitLabel] = (lab, null);
        // a break that is not the last statement of its section leaves the when: the when is wrapped in a labelled
        // do-while(false) for it (every continue names its loop, so the wrapper cannot capture one)
        bool wrap = sw.Cases.Any(c => c.Body.Take(Math.Max(0, c.Body.Length - 1)).Any(o => o.DescendantsAndSelf().Any(d => d is IBranchOperation b && b.BranchKind == BranchKind.Break && SymbolEqualityComparer.Default.Equals(b.Target, sw.ExitLabel)))
            || c.Body.Length > 0 && !(c.Body[^1] is IBranchOperation lb && lb.BranchKind == BranchKind.Break) && c.Body[^1].DescendantsAndSelf().Any(d => d is IBranchOperation b && b.BranchKind == BranchKind.Break && SymbolEqualityComparer.Default.Equals(b.Target, sw.ExitLabel)));
        int bi = ind;
        if (wrap) { lines.Add(p + lab + "@ do {"); bi = ind + 1; }
        lines.Add(I(bi) + "when (" + Strip(v) + (PrimCode(vt) == null && vt.SpecialType != SpecialType.System_String ? "!!" : "") + ") {");
        bool canEnd = !hasDefault;
        string? defaultText = null;
        foreach (var c in sw.Cases)
        {
            var conds = new List<string>();
            bool isDef = false;
            foreach (var cl in c.Clauses)
            {
                if (cl is IDefaultCaseClauseOperation) { isDef = true; continue; }
                if (cl is ISingleValueCaseClauseOperation sv) conds.Add(Strip(Value(sv.Value, vt)));
                else Todo("case clause " + cl.CaseKind);
            }
            var body = c.Body.ToList();
            if (body.Count > 0 && body[^1] is IBranchOperation last && last.BranchKind == BranchKind.Break && SymbolEqualityComparer.Default.Equals(last.Target, sw.ExitLabel))
            {
                body.RemoveAt(body.Count - 1);
                canEnd = true;
            }
            var bl = new List<string>();
            if (Block(body, bl, bi + 2)) canEnd = true;
            if (isDef)
            {
                // C#'s default may sit among the cases; Kotlin's else goes last (no section falls through in C#)
                var sbd = new StringBuilder(I(bi + 1) + "else -> {\n");
                foreach (var l in bl) sbd.Append(l).Append('\n');
                sbd.Append(I(bi + 1)).Append('}');
                defaultText = sbd.ToString();
                continue;
            }
            lines.Add(I(bi + 1) + string.Join(", ", conds) + " -> {");
            lines.AddRange(bl);
            lines.Add(I(bi + 1) + "}");
        }
        if (defaultText != null) lines.Add(defaultText);
        lines.Add(I(bi) + "}");
        if (wrap) lines.Add(p + "} while (false)");
        return canEnd || HasBreakOut(sw, sw.ExitLabel) || sw.Cases.Length == 0;
    }

    // -------------------------------------------------------------------------------------------- try, using
    string? rethrowVar;

    string Rethrow() => rethrowVar ?? Todo("throw; outside a catch");

    bool Try(ITryOperation t, List<string> lines, int ind)
    {
        var p = I(ind);
        lines.Add(p + "try {");
        var ends = Block(t.Body.Operations, lines, ind + 1);
        foreach (var cc in t.Catches)
        {
            var et = cc.ExceptionType;
            string jt;
            if (et.SpecialType == SpecialType.System_Object || et is INamedTypeSymbol en && IsSystem(en) && en.Name == "Exception") jt = "Throwable";
            else jt = NN(KType(et));
            string ev;
            if (cc.ExceptionDeclarationOrExpression is IVariableDeclaratorOperation vd) ev = Declare(vd.Symbol, vd.Symbol.Name);
            else { ev = Fresh("ex"); used.Add(ev); }
            lines.Add(p + "} catch (" + ev + ": " + jt + ") {");
            var saved = rethrowVar; rethrowVar = ev;
            if (cc.Filter != null) Todo("exception filter");
            if (Block(cc.Handler.Operations, lines, ind + 1)) ends = true;
            rethrowVar = saved;
        }
        if (t.Finally != null)
        {
            lines.Add(p + "} finally {");
            var f = Block(t.Finally.Operations, lines, ind + 1);
            if (!f) ends = false;
        }
        lines.Add(p + "}");
        return ends;
    }

    bool UsingRest(IUsingDeclarationOperation ud, List<IOperation> rest, List<string> lines, int ind)
    {
        var p = I(ind);
        var decls = ud.DeclarationGroup.Declarations.SelectMany(d => d.Declarators).ToList();
        foreach (var d in ud.DeclarationGroup.Declarations)
            foreach (var v in d.Declarators) LocalDecl(v, d.Initializer, lines, ind);
        lines.Add(p + "try {");
        var ends = Block(rest, lines, ind + 1);
        lines.Add(p + "} finally {");
        foreach (var v in decls.AsEnumerable().Reverse())
        {
            var n = Read(v.Symbol);
            lines.Add(I(ind + 1) + "if (" + n + " !== null) " + n + "!!.Dispose()");
        }
        lines.Add(p + "}");
        Use("using: IDisposable.Dispose()", "cpp.IDisposable.Dispose()");
        return ends;
    }

    bool Using(IUsingOperation u, List<string> lines, int ind)
    {
        var p = I(ind);
        var names0 = new List<string>();
        if (u.Resources is IVariableDeclarationGroupOperation g)
        {
            foreach (var d in g.Declarations)
                foreach (var v in d.Declarators) { LocalDecl(v, d.Initializer, lines, ind); names0.Add(Read(v.Symbol)); }
        }
        else
        {
            var r = Fresh("res");
            var e = X(u.Resources);
            Flush(lines, ind);
            lines.Add(p + "val " + r + ": cpp.IDisposable? = " + e);
            names0.Add(r);
        }
        lines.Add(p + "try {");
        var ends = Sub(u.Body, lines, ind + 1);
        lines.Add(p + "} finally {");
        foreach (var n in names0.AsEnumerable().Reverse()) lines.Add(I(ind + 1) + "if (" + n + " !== null) " + n + "!!.Dispose()");
        lines.Add(p + "}");
        return ends;
    }

    string Read(ILocalSymbol l) => Boxed(l) ? Name(l) + ".v" : l.RefKind != RefKind.None ? Name(l) + "!!.get()" : Name(l);

    // -------------------------------------------------------------------------------------------- expression statements
    /// <summary>An expression used as a statement. lines may be null (a for-loop step).</summary>
    string StmtExpr(IOperation op, List<string>? lines, int ind)
    {
        switch (op)
        {
            case ISimpleAssignmentOperation a:
                {
                    if (a.IsRef) return RefRebind(a.Target, RefOf(a.Value));
                    var v = Value(a.Value, a.Target.Type);
                    return Assign(a.Target, Strip(v));
                }
            case ICompoundAssignmentOperation ca: return Compound(ca, true);
            case IIncrementOrDecrementOperation inc: return IncDec(inc, true);
            case IDiscardOperation: return "";
            case IConditionalAccessOperation ca when lines != null:
                {
                    // a?.M();  ->  if (a !== null) a!!.M()
                    var recv = X(ca.Operation);
                    string r = recv;
                    if (!Simple(recv)) { r = Fresh("ca"); Pre.Add("val " + r + ": " + KType(ca.Operation.Type!) + " = " + recv); }
                    condAccess.Push(r);
                    var inner = StmtExpr(ca.WhenNotNull, lines, ind);
                    condAccess.Pop();
                    return "if (" + r + " !== null) " + inner;
                }
            default:
                return X(op);
        }
    }

    string RefRebind(IOperation target, string r)
    {
        if (target is ILocalReferenceOperation l && l.Local.RefKind != RefKind.None) return names[l.Local] + " = " + r;
        return Todo("ref reassignment of " + target.Kind);
    }

    // -------------------------------------------------------------------------------------------- places
    /// <summary>A Kotlin assignable expression for op, if it has one (a local, a field, an array or vector element).</summary>
    string? Direct(IOperation op)
    {
        switch (op)
        {
            case ILocalReferenceOperation l:
                if (l.Local.RefKind != RefKind.None) return null;
                return Boxed(l.Local) ? Name(l.Local) + ".v" : Name(l.Local);
            case IParameterReferenceOperation pr:
                if (pr.Parameter.RefKind is RefKind.Ref or RefKind.Out) return null;
                return Boxed(pr.Parameter) ? Name(pr.Parameter) + ".v" : Name(pr.Parameter);
            case IFieldReferenceOperation f:
                if (IsRuntime(f.Field) && GenericMember(f.Field.OriginalDefinition.Type, f.Field.Type)) return null;
                return Field(f, false);
            case IArrayElementReferenceOperation ae:
                return Recv(ae.ArrayReference) + "[" + Index(ae.Indices[0]) + "]";
            case IPropertyReferenceOperation p when VecElem(p, out var vs, out var vi):
                return vs + "[" + vi + "]";       // a generic vector's Array<Any?> takes any element
            case IConversionOperation cv when cv.Conversion.IsIdentity: return Direct(cv.Operand);
        }
        return null;
    }

    /// <summary>The place can be evaluated twice without a difference (names, fields of names, constant indexes).</summary>
    static bool PurePlace(IOperation op)
    {
        switch (op)
        {
            case ILocalReferenceOperation or IParameterReferenceOperation or IInstanceReferenceOperation or ILiteralOperation: return true;
            case IFieldReferenceOperation f: return f.Instance == null || PurePlace(f.Instance);
            case IArrayElementReferenceOperation ae: return PurePlace(ae.ArrayReference) && ae.Indices.All(PurePlace);
            case IPropertyReferenceOperation p when p.Property.IsIndexer: return p.Instance != null && PurePlace(p.Instance) && p.Arguments.All(a => PurePlace(a.Value));
            case IConversionOperation cv: return PurePlace(cv.Operand);
            case IBinaryOperation b when b.OperatorMethod == null: return PurePlace(b.LeftOperand) && PurePlace(b.RightOperand);
        }
        return op.ConstantValue.HasValue;
    }

    string Name(ISymbol s) => names.TryGetValue(s, out var n) ? n : Todo("unknown symbol " + s.Name);

    string Assign(IOperation target, string v)
    {
        var d = Direct(target);
        if (d != null) return d + " = " + v;
        if (target is IPropertyReferenceOperation cp && IsCPtrIndexer(cp)) return Recv(cp.Instance!) + ".set(" + Index(cp.Arguments[0].Value) + ", " + v + ")";
        if (target is IPropertyReferenceOperation sp && !sp.Property.IsIndexer && sp.Property.SetMethod != null && sp.Property.RefKind == RefKind.None)
            return PropSet(sp, v);
        return Paren(RefOf(target)) + "!!.set(" + v + ")";
    }

    /// <summary>A place evaluated once: its array (or vector) and index in temporaries before the statement.</summary>
    string? HoistPlace(IOperation target)
    {
        switch (target)
        {
            case IArrayElementReferenceOperation ae:
                {
                    var a = Fresh("a"); var i = Fresh("i");
                    Pre.Add("val " + a + " = " + Recv(ae.ArrayReference));
                    Pre.Add("val " + i + " = " + Index(ae.Indices[0]));
                    return a + "[" + i + "]";
                }
            case IPropertyReferenceOperation p when VecElem(p, out var vs, out var vi):
                {
                    var a = Fresh("a"); var i = Fresh("i");
                    Pre.Add("val " + a + " = " + vs);
                    Pre.Add("val " + i + " = " + vi);
                    return a + "[" + i + "]";
                }
            case IFieldReferenceOperation f when f.Instance != null && !(IsRuntime(f.Field) && GenericMember(f.Field.OriginalDefinition.Type, f.Field.Type)):
                {
                    var o = Fresh("o");
                    Pre.Add("val " + o + " = " + Recv(f.Instance));
                    return o + "." + (IsRuntime(f.Field) ? RtName(f.Field.Name) : Id(f.Field.Name));
                }
        }
        return null;
    }

    string Compound(ICompoundAssignmentOperation ca, bool stmt)
    {
        var tt = ca.Target.Type!;
        string Combine(string cur)
        {
            if (ca.OperatorMethod != null) return UserBinary(ca.OperatorMethod, cur, X(ca.Value));
            var opT = ca.InConversion.Exists && !ca.InConversion.IsIdentity ? ca.Value.Type! : tt;
            var l = Conv(cur, tt, opT);
            var r = X(ca.Value);
            return Conv(BinOp(ca.OperatorKind, opT, l, r, ca.Value.Type), opT, tt);
        }
        var d = Direct(ca.Target);
        if (d != null)
        {
            // Kotlin's own op= when it means the same as C#'s (it evaluates an array element's array and index once)
            if (stmt && ca.OperatorMethod == null && KtSafeCompound(ca)) return d + " " + KtOp(ca.OperatorKind) + "= " + Strip(X(ca.Value));
            if (!PurePlace(ca.Target))
            {
                var hp = HoistPlace(ca.Target);
                if (hp == null) return Todo("op= on a place with side effects");
                d = hp;
            }
            if (!stmt) { var tv = Fresh("v"); return "(" + Strip(Combine(d)) + ").also { " + tv + " -> " + d + " = " + tv + " }"; }
            return d + " = " + Strip(Combine(d));
        }
        if (ca.Target is IPropertyReferenceOperation cp && IsCPtrIndexer(cp))
        {
            var pr = Recv(cp.Instance!); var ix = Index(cp.Arguments[0].Value);
            if (!Simple(pr)) { var t0 = Fresh("p"); Pre.Add("val " + t0 + " = " + pr); pr = t0; }
            if (!Simple(ix)) { var t1 = Fresh("i"); Pre.Add("val " + t1 + " = " + ix); ix = t1; }
            var nv = Combine(pr + ".get(" + ix + ")");
            return stmt ? pr + ".set(" + ix + ", " + Strip(nv) + ")" : pr + ".assign(" + ix + ", " + Strip(nv) + ")";
        }
        var r0 = RefOf(ca.Target);
        var rv = r0;
        if (!Simple(r0)) { rv = Fresh("r"); Pre.Add("val " + rv + ": " + RefType(tt) + " = " + r0); }
        var val = Combine(rv + "!!.get()");
        if (stmt) return rv + "!!.set(" + Strip(val) + ")";
        return NN(RefType(tt)) + ".assign(" + rv + ", " + Strip(val) + ")";
    }

    /// <summary>x op= v in Kotlin means C#'s: an arithmetic operator on Int, Long, Float or Double with the value of
    /// the target's own type (no promotion and no narrowing), and no unsigned division.</summary>
    static bool KtSafeCompound(ICompoundAssignmentOperation ca)
    {
        var t = ca.Target.Type!;
        var k = ca.OperatorKind;
        if (k is not (BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply or BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder)) return false;
        if (t.SpecialType is not (SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_Double or SpecialType.System_Single)) return false;
        if (IsUnsigned(t) && k is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder) return false;
        if (ca.InConversion.Exists && !ca.InConversion.IsIdentity) return false;
        if (ca.OutConversion.Exists && !ca.OutConversion.IsIdentity) return false;
        if (ca.Value.Type == null || KType(ca.Value.Type) != KType(t)) return false;
        return true;
    }

    static string KtOp(BinaryOperatorKind k) => k switch
    {
        BinaryOperatorKind.Add => "+", BinaryOperatorKind.Subtract => "-", BinaryOperatorKind.Multiply => "*",
        BinaryOperatorKind.Divide => "/", BinaryOperatorKind.Remainder => "%", _ => Todo("op= " + k)
    };

    static int Width(ITypeSymbol? t) => t?.SpecialType switch
    {
        SpecialType.System_Byte or SpecialType.System_SByte => 8, SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char => 16,
        SpecialType.System_Int32 or SpecialType.System_UInt32 => 32, SpecialType.System_Int64 or SpecialType.System_UInt64 => 64, _ => 0
    };

    string IncDec(IIncrementOrDecrementOperation inc, bool stmt)
    {
        var up = inc.Kind == OperationKind.Increment;
        var post = inc.IsPostfix;
        var tt = inc.Target.Type!;
        if (inc.OperatorMethod != null)
        {
            var m = up ? "op_Increment" : "op_Decrement";
            var cls0 = RuntimeClass((INamedTypeSymbol)tt);
            Use("operator " + inc.OperatorMethod.ToDisplayString(), cls0 + "." + m);
            var d0 = Direct(inc.Target);
            if (d0 != null)
            {
                if (stmt) return d0 + " = " + cls0 + "." + m + "(" + d0 + ")";
                if (!post) { var tv = Fresh("v"); return "(" + cls0 + "." + m + "(" + d0 + ")).also { " + tv + " -> " + d0 + " = " + tv + " }"; }
                var t0 = Fresh("old");
                Pre.Add("val " + t0 + " = " + d0);
                Pre.Add(d0 + " = " + cls0 + "." + m + "(" + d0 + ")");
                return t0;
            }
            var r1 = RefOf(inc.Target);
            if (stmt) { var rt = Fresh("r"); Pre.Add("val " + rt + " = " + r1); return rt + "!!.set(" + cls0 + "." + m + "(" + rt + "!!.get()))"; }
            return Todo("increment of a struct through a reference, as a value");
        }
        var d = Direct(inc.Target);
        if (d != null)
        {
            if (!PurePlace(inc.Target)) { var hp = HoistPlace(inc.Target); if (hp == null) return Todo("++ on a place with side effects"); d = hp; }
            if (tt.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char
                || tt.TypeKind == TypeKind.Enum)
            {
                // Kotlin's ++ on Byte, Short and Char wraps like C#'s; written out so that it reads the same everywhere
                var one = d + (up ? " + 1" : " - 1");
                var nv = tt.SpecialType is SpecialType.System_UInt16 or SpecialType.System_Char ? "(" + d + ".code" + (up ? " + 1" : " - 1") + ").toChar()"
                    : tt.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte ? "(" + one + ").toByte()"
                    : tt.SpecialType == SpecialType.System_Int16 ? "(" + one + ").toShort()" : "(" + one + ")";
                if (tt.TypeKind == TypeKind.Enum && PrimCode(tt) == "L") nv = "(" + d + (up ? " + 1L" : " - 1L") + ")";
                if (stmt) return d + " = " + nv;
                var tv = Fresh("v");
                if (!post) return "(" + nv + ").also { " + tv + " -> " + d + " = " + tv + " }";
                return "(" + d + ").also { " + tv + " -> " + d + " = " + nv.Replace(d, tv) + " }";
            }
            return stmt ? d + (up ? "++" : "--") : "(" + (post ? d + (up ? "++" : "--") : (up ? "++" : "--") + d) + ")";
        }
        if (inc.Target is IPropertyReferenceOperation cp && IsCPtrIndexer(cp))
        {
            var pr = Recv(cp.Instance!); var ix = Index(cp.Arguments[0].Value);
            return pr + "." + (post ? "post" : "pre") + (up ? "Inc" : "Dec") + "(" + ix + ")";
        }
        var r = RefOf(inc.Target);
        return NN(RefType(tt)) + "." + (post ? "post" : "pre") + (up ? "Inc" : "Dec") + "(" + r + ")";
    }

    // -------------------------------------------------------------------------------------------- references
    /// <summary>A Kotlin Ref object for the location op names (a ref argument, a ref local, ref return).</summary>
    public string RefOf(IOperation op)
    {
        switch (op)
        {
            case ILocalReferenceOperation l:
                if (l.Local.RefKind != RefKind.None || Boxed(l.Local)) return Name(l.Local);
                return Todo("reference to a local that is not a cell: " + l.Local.Name);
            case IParameterReferenceOperation p:
                if (p.Parameter.RefKind is RefKind.Ref or RefKind.Out || Boxed(p.Parameter)) return Name(p.Parameter);
                return Todo("reference to a parameter that is not a cell: " + p.Parameter.Name);
            case IFieldReferenceOperation f:
                {
                    var fs = f.Field;
                    if (IsRuntime(fs))
                    {
                        var n = "ref_" + fs.Name;
                        Use("field ref " + fs.OriginalDefinition.ToDisplayString(), n + "()");
                        var r = Recv(f.Instance!) + "." + n + "()";
                        return GenericMember(fs.OriginalDefinition.Type, fs.Type) && !SpecializedClass(fs.ContainingType) ? Rewrap(r, fs.Type) : r;
                    }
                    var h = FieldRefHelper(fs);
                    return fs.IsStatic ? h + "()" : h + "(" + X(f.Instance!) + ")";
                }
            case IArrayElementReferenceOperation ae:
                {
                    var et = ((IArrayTypeSymbol)ae.ArrayReference.Type!).ElementType;
                    if (PrimCode(et) != null) return NN(RefType(et)) + ".Arr(" + X(ae.ArrayReference) + ", " + Index(ae.Indices[0]) + ")";
                    return "cpp.RefO.Arr<" + KBoxed(et) + ">(" + X(ae.ArrayReference) + " as Array<Any?>?, " + Index(ae.Indices[0]) + ")";
                }
            case IPropertyReferenceOperation pr:
                {
                    if (VecElem(pr, out var vs, out var vi))
                    {
                        var et = pr.Type!;
                        if (VecGeneric(pr)) return "cpp.RefO.Arr<" + KBoxed(et) + ">(" + vs.Substring(0, vs.Length - 2) + ", " + vi + ")";
                        return NN(RefType(et)) + ".Arr(" + vs.Substring(0, vs.Length - 2) + ", " + vi + ")";
                    }
                    if (IsCPtrIndexer(pr)) return Recv(pr.Instance!) + ".ref(" + Index(pr.Arguments[0].Value) + ")";
                    if (pr.Property.RefKind != RefKind.None) return Prop(pr, true);
                    return Todo("reference to a property that returns no reference: " + pr.Property.Name);
                }
            case IInvocationOperation inv when inv.TargetMethod.RefKind != RefKind.None:
                return Invoke(inv, true);
            case IConditionalOperation c when c.IsRef:
                return "(if (" + Strip(X(c.Condition)) + ") " + RefOf(c.WhenTrue) + " else " + RefOf(c.WhenFalse!) + ")";
            case IDiscardOperation d:
                // out _: a cell nobody reads
                return CellType(d.Type!) + "(" + DefaultOf(d.Type!) + ")";
            case IConversionOperation cv when cv.Conversion.IsIdentity:
                return RefOf(cv.Operand);
        }
        return Todo("reference to " + op.Kind);
    }

    /// <summary>A RefO&lt;Double?&gt; from a generic container seen as the RefD the code expects.</summary>
    static string Rewrap(string r, ITypeSymbol t)
    {
        var c = PrimCode(t);
        if (c == null) return r;
        Use("Ref" + c + ".ofBoxed(RefO)", "cpp.Ref" + c + ".ofBoxed");
        return "cpp.Ref" + c + ".ofBoxed(" + r + ")";
    }

    static bool GenericMember(ITypeSymbol def, ITypeSymbol constructed) => def is ITypeParameterSymbol && PrimCode(constructed) != null;

    // -------------------------------------------------------------------------------------------- expressions
    /// <summary>The value of op, converted (as C# does at this point) to target if given.</summary>
    public string Value(IOperation op, ITypeSymbol? target)
    {
        var s = X(op);
        if (target != null && op.Type != null && !SymbolEqualityComparer.Default.Equals(op.Type, target))
            s = Conv(s, op.Type, target);
        return s;
    }

    static bool IsFloat(ITypeSymbol? t) => t != null && (t.SpecialType == SpecialType.System_Double || t.SpecialType == SpecialType.System_Single);
    static bool IsIntegral(ITypeSymbol? t) => t != null && t.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Char;
    static bool IsUnsigned(ITypeSymbol? t) => t != null && (t.SpecialType is SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_Byte or SpecialType.System_UInt16 or SpecialType.System_Char
        || t.TypeKind == TypeKind.Enum && t is INamedTypeSymbol e && IsUnsigned(e.EnumUnderlyingType));
    static bool IsNumeric(ITypeSymbol? t) => IsFloat(t) || IsIntegral(t);
    static ITypeSymbol Under(ITypeSymbol t) => t.TypeKind == TypeKind.Enum && t is INamedTypeSymbol e ? e.EnumUnderlyingType! : t;

    public string X(IOperation op)
    {
        if (op.ConstantValue.HasValue && !(op is IFieldReferenceOperation fr0 && fr0.Field.ContainingType.TypeKind == TypeKind.Enum && IsEngine(fr0.Field.ContainingType)))
            return Const(op.ConstantValue.Value, op.Type);
        switch (op)
        {
            case IConversionOperation c: return Conversion(c);
            case ILocalReferenceOperation l: return Read(l.Local);
            case IParameterReferenceOperation pr:
                {
                    var n = Name(pr.Parameter);
                    if (pr.Parameter.RefKind is RefKind.Ref or RefKind.Out) return n + "!!.get()";
                    return Boxed(pr.Parameter) ? n + ".v" : n;
                }
            case IInstanceReferenceOperation ir:
                if (ir.ReferenceKind == InstanceReferenceKind.ImplicitReceiver) return implicitRecv.Count > 0 ? implicitRecv.Peek() : Todo("implicit receiver");
                return "this";
            case IFieldReferenceOperation f: return Field(f, true);
            case IPropertyReferenceOperation p: return Prop(p, false);
            case IArrayElementReferenceOperation ae:
                {
                    var e = Recv(ae.ArrayReference) + "[" + Index(ae.Indices[0]) + "]";
                    if (ae.ArrayReference is IFieldReferenceOperation af && IsRuntime(af.Field) && af.Field.OriginalDefinition.Type is IArrayTypeSymbol { ElementType: ITypeParameterSymbol }
                        && !SpecializedClass(af.Field.ContainingType))
                        return "(" + e + " as " + KType(ae.Type!) + ")";
                    return e;
                }
            case IInvocationOperation inv: return Invoke(inv, false);
            case IObjectCreationOperation oc: return New(oc);
            case IArrayCreationOperation ac: return NewArray(ac);
            case IBinaryOperation b: return Binary(b);
            case IUnaryOperation u: return Unary(u);
            case IConditionalOperation c: return Ternary(c);
            case ISimpleAssignmentOperation a:
                {
                    var v = Value(a.Value, a.Target.Type);
                    var d = Direct(a.Target);
                    if (d != null)
                    {
                        if (!PurePlace(a.Target)) { var hp = HoistPlace(a.Target); if (hp == null) return Todo("assignment value to a place with side effects"); d = hp; }
                        var tv = Fresh("v");
                        return "(" + Strip(v) + ").also { " + tv + " -> " + d + " = " + tv + " }";
                    }
                    if (a.Target is IPropertyReferenceOperation cp && IsCPtrIndexer(cp)) return Recv(cp.Instance!) + ".assign(" + Index(cp.Arguments[0].Value) + ", " + Strip(v) + ")";
                    return NN(RefType(a.Target.Type!)) + ".assign(" + RefOf(a.Target) + ", " + Strip(v) + ")";
                }
            case ICompoundAssignmentOperation ca: return Compound(ca, false);
            case IIncrementOrDecrementOperation inc: return IncDec(inc, false);
            case IConditionalAccessOperation ca: return CondAccess(ca);
            case IConditionalAccessInstanceOperation: return condAccess.Count > 0 ? condAccess.Peek() : Todo("conditional access instance");
            case IAnonymousFunctionOperation af: return Lambda(af, null);
            case IDelegateCreationOperation dc: return DelegateCreation(dc);
            case IDefaultValueOperation dv: return DefaultOf(dv.Type!);
            case ILiteralOperation lit: return Const(lit.ConstantValue.Value, lit.Type);
            case IIsTypeOperation it: return "(" + X(it.ValueOperand) + " is " + IsType(it.TypeOperand) + ")";
            case IThrowOperation th:
                {
                    var e = X(th.Exception!);
                    return "(throw " + Paren(e) + "!!)";
                }
            case IParenthesizedOperation pp: return X(pp.Operand);
            case IDiscardOperation: return Todo("discard as a value");
        }
        return Todo("expression " + op.Kind);
    }

    /// <summary>The type of an is test: a generic class with star projections.</summary>
    static string IsType(ITypeSymbol t)
    {
        if (PrimCode(t) != null) return NN(KType(t));
        var s = NN(KType(t));
        var i = s.IndexOf('<');
        if (i < 0) return s;
        int n = t is INamedTypeSymbol nt ? nt.TypeArguments.Length : 1;
        return s.Substring(0, i) + "<" + string.Join(", ", Enumerable.Repeat("*", n)) + ">";
    }

    string Index(IOperation i)
    {
        // Kotlin indexes arrays with an Int
        var t = i.Type;
        var s = X(i);
        if (t == null || t.SpecialType == SpecialType.System_Int32 || t.SpecialType == SpecialType.System_UInt32) return Strip(s);
        if (t.SpecialType is SpecialType.System_Int16 or SpecialType.System_SByte) return Paren(s) + ".toInt()";
        if (t.SpecialType == SpecialType.System_Byte) return "(" + Paren(s) + ".toInt() and 0xFF)";
        if (t.SpecialType is SpecialType.System_UInt16 or SpecialType.System_Char) return Paren(s) + ".code";
        return Paren(s) + ".toInt()";
    }

    static string Paren(string s) => Simple(s) || s.StartsWith("(") && s.EndsWith(")") && Balanced(s.Substring(1, s.Length - 2)) ? s : "(" + s + ")";

    // ---- constants
    public static string Const(object? v, ITypeSymbol? t)
    {
        if (v == null) return t != null && t.IsValueType ? DefaultOf(t) : "null";
        if (t != null && t.TypeKind == TypeKind.Enum) t = Under(t);
        switch (v)
        {
            case bool b: return b ? "true" : "false";
            case string s: return KStr(s);
            case char ch: return CharLit(ch);
            case double d: return t != null && t.SpecialType == SpecialType.System_Single ? FloatLit((float)d) : DoubleLit(d);
            case float f: return t != null && t.SpecialType == SpecialType.System_Double ? DoubleLit(f) : FloatLit(f);
        }
        // integers: written as the JVM type holds them (an unsigned value as its bit pattern)
        var st = t?.SpecialType ?? SpecialType.System_Int32;
        ulong bits = v switch { int i => (ulong)(long)i, long l => (ulong)l, uint u => u, ulong ul => ul, short sh => (ulong)(long)sh, ushort us => us, byte by => by, sbyte sb => (ulong)(long)sb, _ => 0 };
        switch (st)
        {
            case SpecialType.System_Int64: case SpecialType.System_UInt64: return LongLit((long)bits);
            case SpecialType.System_UInt32: return IntLit((int)(uint)bits);
            case SpecialType.System_Byte: case SpecialType.System_SByte: return "(" + (sbyte)(byte)bits + ").toByte()";
            case SpecialType.System_Int16: return "(" + (short)bits + ").toShort()";
            case SpecialType.System_UInt16: case SpecialType.System_Char: return CharLit((char)(ushort)bits);
            case SpecialType.System_Double: return DoubleLit(v is ulong uv ? (double)uv : v is uint ui ? ui : (double)(long)bits);
            case SpecialType.System_Single: return FloatLit(v is ulong uv2 ? (float)uv2 : v is uint ui2 ? ui2 : (float)(long)bits);
            default: return IntLit((int)bits);
        }
    }

    static string IntLit(int i) => i == int.MinValue ? "Int.MIN_VALUE" : i < 0 ? "(" + i.ToString(CultureInfo.InvariantCulture) + ")" : i.ToString(CultureInfo.InvariantCulture);
    static string LongLit(long l) => l == long.MinValue ? "Long.MIN_VALUE" : l < 0 ? "(" + l.ToString(CultureInfo.InvariantCulture) + "L)" : l.ToString(CultureInfo.InvariantCulture) + "L";
    static string CharLit(char c)
    {
        if (c == '\'') return "'\\''";
        if (c == '\\') return "'\\\\'";
        if (c >= 32 && c < 127) return "'" + c + "'";
        return "'\\u" + ((int)c).ToString("x4") + "'";
    }

    static string DoubleLit(double d)
    {
        if (double.IsNaN(d)) return "Double.fromBits(" + LongLit(BitConverter.DoubleToInt64Bits(d)) + ")";
        if (double.IsPositiveInfinity(d)) return "Double.POSITIVE_INFINITY";
        if (double.IsNegativeInfinity(d)) return "Double.NEGATIVE_INFINITY";
        if (d == 0 && double.IsNegative(d)) return "(-0.0)";
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E')) s += ".0";
        return d < 0 ? "(" + s + ")" : s;
    }

    static string FloatLit(float f)
    {
        if (float.IsNaN(f)) return "Float.fromBits(" + IntLit(BitConverter.SingleToInt32Bits(f)) + ")";
        if (float.IsPositiveInfinity(f)) return "Float.POSITIVE_INFINITY";
        if (float.IsNegativeInfinity(f)) return "Float.NEGATIVE_INFINITY";
        if (f == 0 && float.IsNegative(f)) return "(-0.0f)";
        var s = f.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E')) s += ".0";
        s += "f";
        return f < 0 ? "(" + s + ")" : s;
    }

    public static string KStr(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
        {
            int c = ch;
            if (c > 255) Todo("non-Latin-1 character in a string");
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '$') sb.Append("\\$");
            else if (c >= 32 && c < 127) sb.Append(ch);
            else sb.Append("\\u").Append(c.ToString("x4"));
        }
        return sb.Append('"').ToString();
    }

    // ---- conversions
    string Conversion(IConversionOperation c)
    {
        var from = c.Operand.Type;
        var to = c.Type!;
        if (c.OperatorMethod != null) return RuntimeConvOp(c.OperatorMethod, X(c.Operand), c.Operand.Type);
        if (c.Operand is IAnonymousFunctionOperation af) return Lambda(af, to);
        if (c.Operand is IDelegateCreationOperation dc0 && to.TypeKind == TypeKind.Delegate) return DelegateCreation(dc0);
        if (from == null)
        {
            if (c.Operand is ILiteralOperation || c.Operand is IDefaultValueOperation) return DefaultOf(to);
            return X(c.Operand);
        }
        if (c.IsTryCast) return "(" + X(c.Operand) + " as? " + NN(KType(to)) + ")";
        return Conv(X(c.Operand), from, to);
    }

    public string Conv(string s, ITypeSymbol from, ITypeSymbol to)
    {
        if (SymbolEqualityComparer.Default.Equals(from, to)) return s;
        var f = Under(from); var t = Under(to);
        if (SymbolEqualityComparer.Default.Equals(f, t)) return s;
        if (IsNumeric(t) && IsNumeric(f))
        {
            var kt = KType(t);
            var p = Paren(s);
            bool fChar = f.SpecialType is SpecialType.System_UInt16 or SpecialType.System_Char;
            bool tChar = t.SpecialType is SpecialType.System_UInt16 or SpecialType.System_Char;
            if (IsFloat(f) && IsIntegral(t))
            {
                // C# on x86-64 (cvttsd2si): NaN and out-of-range values give the integer indefinite
                var k = t.SpecialType switch
                {
                    SpecialType.System_Int32 => "i32", SpecialType.System_UInt32 => "u32", SpecialType.System_Int64 => "i64", SpecialType.System_UInt64 => "u64",
                    SpecialType.System_Int16 => "i16", SpecialType.System_UInt16 => "u16", SpecialType.System_SByte => "i8", SpecialType.System_Byte => "u8", SpecialType.System_Char => "u16",
                    _ => "x"
                };
                Use("float-to-int " + k, "cpp.F2." + k + "(double)");
                return "cpp.F2." + k + "(" + Strip(s) + (f.SpecialType == SpecialType.System_Single ? ".toDouble()" : "") + ")";
            }
            if (IsFloat(t))
            {
                var to2 = t.SpecialType == SpecialType.System_Double ? "toDouble()" : "toFloat()";
                if (f.SpecialType == SpecialType.System_UInt64) { Use("ulong-to-" + kt, "cpp.R.u2" + (t.SpecialType == SpecialType.System_Double ? "d" : "f")); return "cpp.R.u2" + (t.SpecialType == SpecialType.System_Double ? "d" : "f") + "(" + Strip(s) + ")"; }
                if (f.SpecialType == SpecialType.System_UInt32) return "Integer.toUnsignedLong(" + Strip(s) + ")." + to2;
                if (f.SpecialType == SpecialType.System_Byte) return "(" + p + ".toInt() and 0xFF)." + to2;
                if (fChar) return p + ".code." + to2;
                return p + "." + to2;
            }
            // integral to integral
            int wf = Width(f), wt = Width(t);
            if (wt > wf)
            {
                if (f.SpecialType == SpecialType.System_Byte)
                    return wt == 64 ? "(" + p + ".toInt() and 0xFF).toLong()" : tChar ? "(" + p + ".toInt() and 0xFF).toChar()" : wt == 16 ? "(" + p + ".toInt() and 0xFF).toShort()" : "(" + p + ".toInt() and 0xFF)";
                if (f.SpecialType == SpecialType.System_UInt32) return "Integer.toUnsignedLong(" + Strip(s) + ")";
                if (fChar) return wt == 64 ? p + ".code.toLong()" : p + ".code";
                // signed: widened with the sign, as C# does (also to an unsigned type)
                if (tChar) return p + ".toInt().toChar()";
                return p + "." + ToFn(kt);
            }
            // same width or narrower: the low bits
            if (KType(f) == kt) return s;
            if (fChar) return kt == "Int" ? p + ".code" : p + ".code." + ToFn(kt);
            if (tChar) return KType(f) == "Int" ? p + ".toChar()" : p + ".toInt().toChar()";
            return p + "." + ToFn(kt);
        }
        if (t.SpecialType == SpecialType.System_Boolean && f.SpecialType == SpecialType.System_Boolean) return s;
        // boxing (Kotlin boxes implicitly) and unboxing
        if (to.SpecialType == SpecialType.System_Object || to.TypeKind == TypeKind.Interface && !IsRuntime(to)) return s;
        if (from.SpecialType == SpecialType.System_Object || from.TypeKind == TypeKind.Interface || from is ITypeParameterSymbol)
        {
            if (PrimCode(to) != null) return "(" + s + " as " + NN(KType(to)) + ")";
            if (to is ITypeParameterSymbol tp2) return "(" + s + " as " + tp2.Name + ")";
            return "(" + s + " as " + KType(to) + ")";
        }
        if (to is INamedTypeSymbol tex && IsSystem(tex) && tex.Name == "Exception") return s;
        if (from is INamedTypeSymbol fn && to is INamedTypeSymbol tn && (IsEngine(fn) || IsRuntime(fn)) && (IsEngine(tn) || IsRuntime(tn)))
        {
            if (Derives(fn, tn) || tn.TypeKind == TypeKind.Interface) return KType(fn) == KType(tn) || !fn.IsGenericType ? s : "(" + s + " as " + KType(tn) + ")";
            if (Derives(tn, fn)) return "(" + s + " as " + KType(tn) + ")";
        }
        if (from.TypeKind == TypeKind.Delegate && to.TypeKind == TypeKind.Delegate) return Todo("delegate conversion " + from.ToDisplayString() + " -> " + to.ToDisplayString());
        if (to is INamedTypeSymbol nl && nl.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return s;
        if (from is INamedTypeSymbol nf && nf.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return PrimCode(to) != null ? Paren(s) + "!!" : s;
        if (SymbolEqualityComparer.Default.Equals(from.OriginalDefinition, to.OriginalDefinition)) return KType(from) == KType(to) ? s : "(" + s + " as " + KType(to) + ")";
        if (from.TypeKind == TypeKind.Array && to.TypeKind == TypeKind.Array) return KType(from) == KType(to) ? s : "(" + s + " as " + KType(to) + ")";
        return Todo("conversion " + from.ToDisplayString() + " -> " + to.ToDisplayString());
    }

    static string ToFn(string kt) => kt switch { "Int" => "toInt()", "Long" => "toLong()", "Short" => "toShort()", "Byte" => "toByte()", "Double" => "toDouble()", "Float" => "toFloat()", _ => Todo("toFn " + kt) };

    string RuntimeConvOp(IMethodSymbol op, string arg, ITypeSymbol? argType)
    {
        var t = op.ContainingType;
        var p0 = op.OriginalDefinition.Parameters[0].Type;
        var nm = RuntimeClass(t) + ".from_" + ShortType(p0) + Spec(t, op);
        Use("conversion " + op.ToDisplayString(), nm);
        return nm + "(" + Strip(argType != null ? Conv(arg, argType, op.Parameters[0].Type) : arg) + ")";
    }

    static string Spec(INamedTypeSymbol? t, IMethodSymbol? m)
    {
        var args = new List<ITypeSymbol>();
        if (t != null && t.IsGenericType && !SpecializedClass(t)) args.AddRange(t.TypeArguments);
        if (m != null && m.IsGenericMethod) args.AddRange(m.TypeArguments);
        if (args.Count == 0 || args.All(a => PrimCode(a) == null)) return "";
        return "__" + string.Concat(args.Select(a => PrimCode(a) ?? "O"));
    }

    static bool SpecializedClass(INamedTypeSymbol t) => RuntimeTypeName(t) is var n && !n.Contains('<') && t.IsGenericType;

    // ---- fields and properties
    string Field(IFieldReferenceOperation f, bool read)
    {
        var fs = f.Field;
        if (fs.ContainingType.TypeKind == TypeKind.Enum)
        {
            if (IsEngine(fs.ContainingType)) return TypeIdent(fs.ContainingType) + "." + Id(fs.Name);
            return Const(fs.ConstantValue, fs.Type);
        }
        if (IsRuntime(fs))
        {
            if (fs.IsStatic || fs.IsConst)
            {
                var cn = RuntimeClass(fs.ContainingType);
                var n = cn + "." + RtName(fs.Name);
                Use("static field " + fs.OriginalDefinition.ToDisplayString(), n);
                // a static field of a generic runtime class (Less<T>.Fn) is declared raw in the runtime: cast to its type here
                if (read && fs.ContainingType.IsGenericType && !SymbolEqualityComparer.Default.Equals(fs.OriginalDefinition.Type, fs.Type) && !SpecializedClass(fs.ContainingType))
                    return "(" + n + " as " + KType(fs.Type) + ")";
                return n;
            }
            var jf = RtName(fs.Name);
            Use("field " + fs.OriginalDefinition.ToDisplayString(), jf);
            var e = Recv(f.Instance!) + "." + jf;
            if (read && GenericMember(fs.OriginalDefinition.Type, fs.Type)) return "(" + e + " as " + NN(KType(fs.Type)) + ")";
            return e;
        }
        if (fs.IsStatic || fs.IsConst) return TypeIdent(fs.ContainingType) + "." + Id(fs.Name);
        return Recv(f.Instance!) + "." + Id(fs.Name);
    }

    /// <summary>A receiver: `!!` unless it is this or cannot be null.</summary>
    string Recv(IOperation inst)
    {
        var s = X(inst);
        if (inst is IInstanceReferenceOperation ir && ir.ReferenceKind != InstanceReferenceKind.ImplicitReceiver) return s;
        if (inst.Type != null && PrimCode(inst.Type) != null) return Paren(s);
        if (inst is IObjectCreationOperation) return Paren(s);
        if (s == "this" || s.StartsWith("__o") && Simple(s)) return s;
        return Paren(s) + "!!";
    }

    static bool Simple(string s)
    {
        int depth = 0;
        foreach (var ch in s)
        {
            if (ch == '(' || ch == '[') depth++;
            else if (ch == ')' || ch == ']') depth--;
            else if (depth == 0 && !(char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' || ch == '`')) return false;
        }
        return depth == 0 && s.Length > 0 && !char.IsDigit(s[0]);
    }

    static bool IsCPtrIndexer(IPropertyReferenceOperation p) => p.Property.IsIndexer && p.Property.ContainingType.Name == "CPtr" && IsRuntime(p.Property);

    /// <summary>A vector or string element: the backing array (with its !!) and the index.</summary>
    bool VecElem(IPropertyReferenceOperation p, out string arr, out string idx)
    {
        arr = idx = "";
        var tn = StripArity(p.Property.ContainingType.Name);
        if (!p.Property.IsIndexer || !IsRuntime(p.Property) || p.Property.RefKind == RefKind.None) return false;
        if (tn == "Vec") { arr = Recv(p.Instance!) + ".a!!"; idx = Index(p.Arguments[0].Value); Use("Vec.a (backing array)", "a"); return true; }
        if (tn == "Str") { arr = Recv(p.Instance!) + ".b!!"; idx = Index(p.Arguments[0].Value); Use("Str.b (backing array)", "b"); return true; }
        return false;
    }

    static bool VecGeneric(IPropertyReferenceOperation p) => StripArity(p.Property.ContainingType.Name) == "Vec" && PrimCode(p.Property.ContainingType.TypeArguments[0]) == null;

    string Prop(IPropertyReferenceOperation p, bool asRef)
    {
        var ps = p.Property;
        if (IsSystem(ps) && ps.Name == "Length")
            return ps.ContainingType.SpecialType == SpecialType.System_String ? Recv(p.Instance!) + ".length" : Recv(p.Instance!) + ".size";
        if (!IsRuntime(ps)) return Todo((IsSystem(ps) ? "system" : "engine") + " property " + ps.ContainingType.Name + "." + ps.Name);
        if (!asRef && VecElem(p, out var arr, out var idx))
        {
            var e = arr + "[" + idx + "]";
            return VecGeneric(p) ? "(" + e + " as " + KType(p.Type!) + ")" : e;
        }
        if (IsCPtrIndexer(p) && !asRef) return Recv(p.Instance!) + ".get(" + Index(p.Arguments[0].Value) + ")";
        var cn = RuntimeClass(ps.ContainingType);
        var nm = ps.IsIndexer ? "Item" : ps.Name;
        var sfx = ps.IsIndexer && ps.ContainingType.GetMembers().OfType<IPropertySymbol>().Count(x => x.IsIndexer) > 1 ? "_" + ShortType(ps.OriginalDefinition.Parameters[0].Type) : "";
        var args = string.Join(", ", p.Arguments.Select(a => Strip(Value(a.Value, a.Parameter!.Type))));
        var key = "property " + ps.ContainingType.OriginalDefinition.ToDisplayString() + "." + ps.Name + (ps.IsIndexer ? "[" + string.Join(",", ps.Parameters.Select(x => x.Type.ToDisplayString())) + "]" : "");
        string call;
        if (asRef)
        {
            var rn = "ref_" + nm + sfx;
            Use(key + " (ref)", rn);
            call = (ps.IsStatic ? cn : Recv(p.Instance!)) + "." + rn + "(" + args + ")";
            return GenericMember(ps.OriginalDefinition.Type, ps.Type) && !SpecializedClass(ps.ContainingType) ? Rewrap(call, ps.Type) : call;
        }
        var gn = "get_" + nm + sfx;
        Use(key, gn);
        call = (ps.IsStatic ? cn : Recv(p.Instance!)) + "." + gn + "(" + args + ")";
        if (GenericMember(ps.OriginalDefinition.Type, ps.Type)) return "(" + call + " as " + NN(KType(ps.Type)) + ")";
        return call;
    }

    string PropSet(IPropertyReferenceOperation p, string v)
    {
        var ps = p.Property;
        if (!IsRuntime(ps)) return Todo("set engine property " + ps.Name);
        var sn = "set_" + ps.Name;
        Use("property set " + ps.ContainingType.OriginalDefinition.ToDisplayString() + "." + ps.Name, sn);
        return (ps.IsStatic ? RuntimeClass(ps.ContainingType) : Recv(p.Instance!)) + "." + sn + "(" + v + ")";
    }

    // ---- calls
    public string Args(IMethodSymbol m, IEnumerable<IArgumentOperation> args)
    {
        var outv = new List<string>();
        foreach (var a in args.OrderBy(a => a.Parameter?.Ordinal ?? 0))
        {
            var prm = a.Parameter!;
            if (a.ArgumentKind == ArgumentKind.ParamArray)
            {
                if (a.Value is IArrayCreationOperation ac && ac.Initializer != null)
                {
                    var et = ((IArrayTypeSymbol)prm.Type).ElementType;
                    foreach (var e in ac.Initializer.ElementValues) outv.Add(VarArg(e, et));
                }
                else outv.Add("*" + Paren(X(a.Value)) + "!!");
                continue;
            }
            if (prm.RefKind is RefKind.Ref or RefKind.Out) outv.Add(RefOf(a.Value));
            else if (prm.IsParams && prm.Type is IArrayTypeSymbol && a.Value.Type is IArrayTypeSymbol)
                outv.Add("*" + Paren(Value(a.Value, prm.Type)) + "!!");     // an array given for a params array: Kotlin spreads it
            else outv.Add(Strip(Value(a.Value, prm.Type)));
        }
        return string.Join(", ", outv);
    }

    /// <summary>A params object[] element: a primitive is boxed by its exact JVM type (Kotlin boxes an Int as an
    /// Integer, a Long as a Long), and uint/ulong values carry their C# types through cpp.U32/cpp.U64.</summary>
    string VarArg(IOperation e, ITypeSymbol et)
    {
        var src = e is IConversionOperation cv && cv.IsImplicit && et.SpecialType == SpecialType.System_Object ? cv.Operand : e;
        if (et.SpecialType == SpecialType.System_Object && src.Type != null)
        {
            var st = Under(src.Type);
            var v = X(src);
            if (st.SpecialType == SpecialType.System_UInt32) { Use("vararg uint", "cpp.U32"); return "cpp.U32(" + Strip(v) + ")"; }
            if (st.SpecialType == SpecialType.System_UInt64) { Use("vararg ulong", "cpp.U64"); return "cpp.U64(" + Strip(v) + ")"; }
            if (st.SpecialType == SpecialType.System_Byte) { Use("vararg byte", "cpp.U8"); return "cpp.U8(" + Strip(v) + ")"; }
            if (st.SpecialType == SpecialType.System_UInt16) { Use("vararg ushort", "cpp.U16"); return "cpp.U16(" + Strip(v) + ")"; }
            return Strip(v);
        }
        return Strip(Value(e, et));
    }

    string Invoke(IInvocationOperation inv, bool asRef)
    {
        var m = inv.TargetMethod;
        string call;
        if (m.MethodKind == MethodKind.DelegateInvoke)
        {
            var dt = (INamedTypeSymbol)m.ContainingType;
            call = Recv(inv.Instance!) + "." + DelegateMethod(dt) + "(" + Args(m, inv.Arguments) + ")";
            if (IsSystem(dt) && !m.ReturnsVoid && PrimCode(m.ReturnType) != null) call = "(" + call + " as " + NN(KType(m.ReturnType)) + ")";
            if (m.RefKind != RefKind.None) return asRef ? call : call + "!!.get()";
            return call;
        }
        if (IsRuntime(m) || IsSystem(m)) return RuntimeCall(m, inv.Instance, inv.Arguments, asRef);
        if (m.IsStatic) call = TypeIdent(m.ContainingType) + "." + MethodIdent(m) + "(" + Args(m, inv.Arguments) + ")";
        else
        {
            string r;
            if (inv.Instance is IInstanceReferenceOperation && inv.Instance.Syntax is Microsoft.CodeAnalysis.CSharp.Syntax.BaseExpressionSyntax)
                r = "super";      // base.M(): the base class's M, never a derived override
            else r = Recv(inv.Instance!);
            call = r + "." + MethodIdent(m) + (m.IsGenericMethod ? "<" + string.Join(", ", m.TypeArguments.Select(KBoxed)) + ">" : "") + "(" + Args(m, inv.Arguments) + ")";
        }
        if (m.IsStatic && m.IsGenericMethod) call = call.Insert(call.IndexOf('('), "<" + string.Join(", ", m.TypeArguments.Select(KBoxed)) + ">");
        if (m.RefKind != RefKind.None) return asRef ? call : call + "!!.get()";
        return call;
    }

    string RuntimeCall(IMethodSymbol m, IOperation? inst, System.Collections.Immutable.ImmutableArray<IArgumentOperation> argOps, bool asRef)
    {
        var t = m.ContainingType;
        if (IsSystem(m)) return SystemCall(m, inst, argOps);
        var args = Args(m, argOps);
        if (m.Name == "ToString" && !m.IsStatic) return Recv(inst!) + ".toString()";
        var overloads = t.GetMembers(m.Name).OfType<IMethodSymbol>().Count();
        var sfx = overloads > 1 ? "_" + string.Join("_", m.OriginalDefinition.Parameters.Select(p => ShortType(p.Type) + (p.RefKind != RefKind.None ? "r" : ""))) : "";
        var spec = Spec(m.IsStatic ? t : null, m);
        var nm = m.Name + sfx + spec;
        var refm = m.RefKind != RefKind.None;
        if (refm && asRef) nm = "ref_" + nm;
        string call;
        if (m.IsStatic && m.IsGenericMethod && spec.Length == 0 && (t.Name == "Mv" && m.Name == "Take" || t.Name == "Arr" && (m.Name == "Make" && m.Parameters.Length == 1 || m.Name == "Make2")))
        {
            // these make a T with Elem<T>.Make: Kotlin, like Java, needs the maker
            args = (args.Length > 0 ? args + ", " : "") + Maker(m.TypeArguments[0]);
            Use("static " + m.OriginalDefinition.ToDisplayString() + " + maker", RuntimeClass(t) + "." + nm);
        }
        if (m.IsStatic)
        {
            var cn = RuntimeClass(t);
            Use("static " + m.OriginalDefinition.ToDisplayString() + (spec.Length > 0 ? " " + spec : "") + (refm && asRef ? " (ref)" : ""), cn + "." + nm);
            var targs = spec.Length == 0 ? TypeWitness(t, m) : "";
            call = cn + "." + RtName(nm) + targs + "(" + args + ")";
        }
        else
        {
            Use("method " + m.OriginalDefinition.ToDisplayString() + (spec.Length > 0 ? " " + spec : "") + (refm && asRef ? " (ref)" : ""), nm);
            var targs = spec.Length == 0 && m.IsGenericMethod ? "<" + string.Join(", ", m.TypeArguments.Select(KBoxed)) + ">" : "";
            call = Recv(inst!) + "." + RtName(nm) + targs + "(" + args + ")";
        }
        if (refm && asRef) return GenericMember(m.OriginalDefinition.ReturnType, m.ReturnType) && !SpecializedClass(t) ? Rewrap(call, m.ReturnType) : call;
        if (!m.ReturnsVoid && GenericMember(m.OriginalDefinition.ReturnType, m.ReturnType) && spec.Length == 0 && !SpecializedClass(t))
            return "(" + call + " as " + NN(KType(m.ReturnType)) + ")";
        // a static generic runtime method declared raw in the runtime (Iter.zero()) is cast to the type wanted here
        if (!m.ReturnsVoid && m.IsStatic && t.IsGenericType && !SpecializedClass(t) && m.ReturnType is INamedTypeSymbol rnt && rnt.IsGenericType && RawInRuntime(m))
            return "(" + call + " as " + KType(m.ReturnType) + ")";
        return call;
    }

    /// <summary>Runtime statics that the Java runtime (and so the Kotlin one) declares without type parameters.</summary>
    static bool RawInRuntime(IMethodSymbol m) => m.Name == "zero";

    /// <summary>Kotlin type arguments for a static generic runtime method (the class's type arguments, then the method's).</summary>
    static string TypeWitness(INamedTypeSymbol t, IMethodSymbol m)
    {
        var a = new List<ITypeSymbol>();
        if (t.IsGenericType && !SpecializedClass(t)) a.AddRange(t.TypeArguments);
        if (m.IsGenericMethod) a.AddRange(m.TypeArguments);
        return a.Count == 0 ? "" : "<" + string.Join(", ", a.Select(KBoxed)) + ">";
    }

    string SystemCall(IMethodSymbol m, IOperation? inst, System.Collections.Immutable.ImmutableArray<IArgumentOperation> argOps)
    {
        var t = m.ContainingType.Name;
        var args = Args(m, argOps);
        if (t == "Math" || t == "MathF")
        {
            var n = "cpp.SysMath." + m.Name + (m.Parameters.Length > 0 ? "_" + ShortType(m.Parameters[0].Type) : "");
            Use("System.Math." + m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.Type.Name)) + ")", n);
            return n + "(" + args + ")";
        }
        var a0 = argOps.Length > 0 ? Strip(Value(argOps[0].Value, argOps[0].Parameter!.Type)) : "";
        if (t == "Object" && m.Name == "ReferenceEquals") return "(" + string.Join(" === ", argOps.Select(a => X(a.Value))) + ")";
        if (t == "Double" && m.Name == "IsFinite") return Paren(a0) + ".isFinite()";
        if (t == "Double" && m.Name == "IsNaN") return Paren(a0) + ".isNaN()";
        if (t == "Double" && m.Name == "IsInfinity") return Paren(a0) + ".isInfinite()";
        if (t == "Double" && m.Name == "IsPositiveInfinity") return "(" + a0 + " == Double.POSITIVE_INFINITY)";
        if (t == "Double" && m.Name == "IsNegativeInfinity") return "(" + a0 + " == Double.NEGATIVE_INFINITY)";
        if (t == "Double" && m.Name == "IsNegative") { Use("Double.IsNegative", "cpp.R.signbit(double)"); return "cpp.R.signbit(" + args + ")"; }
        if (t == "BitConverter" && m.Name == "DoubleToInt64Bits") return Paren(a0) + ".toRawBits()";
        if (t == "BitConverter" && m.Name == "Int64BitsToDouble") return "Double.fromBits(" + args + ")";
        if (t == "Array" && m.Name == "Fill") { Use("Array.Fill", "java.util.Arrays.fill"); return "java.util.Arrays.fill(" + args + ")"; }
        if (t == "File" && m.Name == "Exists") { Use("File.Exists(string)", "cpp.R.File_Exists(String)"); return "cpp.R.File_Exists(" + args + ")"; }
        if (m.Name == "Dispose" && inst != null) return Recv(inst) + ".Dispose()";
        if (m.Name == "GetHashCode" && inst != null) return Recv(inst) + ".hashCode()";
        if (m.Name == "Equals" && inst != null) return "(" + X(inst) + " == " + args + ")";
        return Todo("system call " + m.ContainingType.ToDisplayString() + "." + m.Name);
    }

    string New(IObjectCreationOperation oc)
    {
        var t = (INamedTypeSymbol)oc.Type!;
        var c = oc.Constructor!;
        string e;
        if (t.TypeKind == TypeKind.Delegate) return Todo("delegate construction");
        if (IsEngine(t))
            e = TypeIdent(t) + "(" + Args(c, oc.Arguments) + ")";
        else if (IsRuntime(t))
        {
            var kt = NN(KType(t));
            var args = MakerArgs(c, oc.Arguments, t);
            Use("new " + c.OriginalDefinition.ToDisplayString() + (kt.Contains('<') || !t.IsGenericType ? "" : " (" + kt + ")") + (args.appended ? " + makers" : ""), kt.Contains('<') ? Raw(kt) + "<>" : kt);
            e = kt + "(" + args.text + ")";
        }
        else if (IsSystem(t))
        {
            if (t.Name.EndsWith("Exception"))
            {
                Use("system exception", "cpp.SysException");
                e = "cpp.SysException(\"" + t.Name + "\", " + (oc.Arguments.Length > 0 ? "java.lang.String.valueOf(" + X(oc.Arguments[0].Value) + ")" : "\"\"") + ")";
            }
            else return Todo("new " + t.ToDisplayString());
        }
        else return Todo("new " + t.ToDisplayString());
        if (oc.Initializer == null || oc.Initializer.Initializers.Length == 0) return e;
        // new X() { a = 1, b = 2 }: a temporary, set member by member before the statement
        var o = Fresh("o");
        Pre.Add("val " + o + " = " + e);
        implicitRecv.Push(o);
        foreach (var init in oc.Initializer.Initializers)
        {
            var st = StmtExpr(init, null, 0);
            if (st.Length > 0) Pre.Add(st);
        }
        implicitRecv.Pop();
        return o;
    }

    static string Raw(string t) { var i = t.IndexOf('<'); return i < 0 ? t : t.Substring(0, i); }

    // ---- element makers
    // C# makes a container's elements with reflection (Elem<T>.Make: the type's parameterless constructor); the JVM's
    // erased generics cannot, so a generic runtime container is handed a maker, cpp.Fn.F0<T>, for the elements it
    // creates itself: appended to a constructor that has no Func<T> parameter, or standing in for a null one.
    static readonly Dictionary<string, int[]> makerSlots = new() { ["Vec"] = new[] { 0 }, ["Map"] = new[] { 1 }, ["UMap"] = new[] { 1 }, ["UMapC"] = new[] { 1 } };

    (string text, bool appended) MakerArgs(IMethodSymbol c, System.Collections.Immutable.ImmutableArray<IArgumentOperation> argOps, INamedTypeSymbol t)
    {
        var name = StripArity(t.Name);
        var specialized = !RuntimeTypeName(t).Contains('<');
        var outv = new List<string>();
        bool hasFunc = false;
        foreach (var a in argOps.OrderBy(a => a.Parameter?.Ordinal ?? 0))
        {
            var prm = a.Parameter!;
            if (prm.Type is INamedTypeSymbol ft && IsSystem(ft) && ft.Name == "Func" && ft.TypeArguments.Length == 1 && !specialized)
            {
                hasFunc = true;
                if (a.Value.ConstantValue is { HasValue: true, Value: null } || a.Value is IDefaultValueOperation || a.ArgumentKind == ArgumentKind.DefaultValue && a.Value.ConstantValue.HasValue)
                { outv.Add(Maker(ft.TypeArguments[0])); continue; }
            }
            outv.Add(ArgOne(a));
        }
        bool appended = false;
        if (!specialized && !hasFunc)
        {
            if (makerSlots.TryGetValue(name, out var slots))
                foreach (var k in slots) { outv.Add(Maker(t.TypeArguments[k])); appended = true; }
            else if ((name == "Pair" || name == "Tup") && c.Parameters.Length == 0)
                foreach (var ta in t.TypeArguments) { outv.Add(Maker(ta)); appended = true; }
        }
        // a container with the engine's own hasher and key-equal (C# makes them with new H(), new E())
        if (name == "UMapC") { outv.Add(Maker(t.TypeArguments[2])); outv.Add(Maker(t.TypeArguments[3])); appended = true; }
        if (name == "USetC") { outv.Add(Maker(t.TypeArguments[1])); outv.Add(Maker(t.TypeArguments[2])); appended = true; }
        return (string.Join(", ", outv), appended);
    }

    string ArgOne(IArgumentOperation a)
    {
        var prm = a.Parameter!;
        if (prm.RefKind is RefKind.Ref or RefKind.Out) return RefOf(a.Value);
        if (a.ArgumentKind == ArgumentKind.ParamArray)
        {
            // the elements of a params array, each as its own argument (Kotlin's vararg), as Args does
            if (a.Value is IArrayCreationOperation ac && ac.Initializer != null)
                return string.Join(", ", ac.Initializer.ElementValues.Select(e => VarArg(e, ((IArrayTypeSymbol)prm.Type).ElementType)));
            return "*" + Paren(X(a.Value)) + "!!";
        }
        if (prm.IsParams && prm.Type is IArrayTypeSymbol && a.Value.Type is IArrayTypeSymbol)
            return "*" + Paren(Value(a.Value, prm.Type)) + "!!";
        return Strip(Value(a.Value, prm.Type));
    }

    /// <summary>A cpp.Fn.F0 that makes a default T, as C#'s Elem&lt;T&gt;.Make does.</summary>
    static string Maker(ITypeSymbol t)
    {
        var bt = KBoxed(t);
        string body;
        if (PrimCode(t) != null) body = DefaultOf(t);
        else if (IsStructType(t)) body = DefaultOf(t);
        else if (t is INamedTypeSymbol n && IsEngine(n) && n.TypeKind == TypeKind.Class && !n.IsAbstract && !n.IsStatic) body = TypeIdent(n) + "()";
        else if (t is INamedTypeSymbol r && IsRuntime(r) && r.TypeKind == TypeKind.Class && !r.IsAbstract && r.InstanceConstructors.Any(k => k.Parameters.Length == 0 || k.Parameters.All(p => p.IsOptional)))
        {
            var kt = NN(KType(r));
            var nm = StripArity(r.Name);
            if (kt.Contains('<'))
            {
                var inner = makerSlots.TryGetValue(nm, out var slots) ? string.Join(", ", slots.Select(k => Maker(r.TypeArguments[k])))
                          : nm is "Pair" or "Tup" ? string.Join(", ", r.TypeArguments.Select(Maker)) : "";
                if (nm == "UMapC") inner += ", " + Maker(r.TypeArguments[2]) + ", " + Maker(r.TypeArguments[3]);
                if (nm == "USetC") inner = Maker(r.TypeArguments[1]) + ", " + Maker(r.TypeArguments[2]);
                if (nm is "Map" or "UMap")
                {
                    // the map constructors' leading optional parameters (hash, equality, ordering) are null
                    var ctor = r.InstanceConstructors.First(k => k.Parameters.All(p => p.IsOptional) && k.Parameters.Length > 0);
                    var nulls = string.Join(", ", ctor.Parameters.Take(ctor.Parameters.Length - 1).Select(p => "null as " + KType(p.Type)));
                    inner = nulls.Length > 0 ? nulls + ", " + inner : inner;
                }
                body = kt + "(" + inner + ")";
            }
            else body = kt + "()";
        }
        else body = "null";
        return "cpp.Fn.F0<" + bt + "> { " + body + " }";
    }

    string NewArray(IArrayCreationOperation ac)
    {
        var at = (IArrayTypeSymbol)ac.Type!;
        var et = at.ElementType;
        bool prim = PrimCode(et) != null;
        string primArr = prim ? NN(KType(et)) + "Array" : "";
        if (ac.Initializer != null && ac.Initializer.ElementValues.Length > 16 && prim &&
            ac.Initializer.ElementValues.All(v => v.ConstantValue.HasValue && v.ConstantValue.Value is not null && Convert.ToDouble(v.ConstantValue.Value) == 0 && !(v.ConstantValue.Value is double dz && double.IsNegative(dz)) && !(v.ConstantValue.Value is float fz && float.IsNegative(fz))))
            // T a[N] = {} : a long run of zeros is the array's default (an initializer that long exceeds a method's 64 KB)
            return primArr + "(" + ac.Initializer.ElementValues.Length + ")";
        if (ac.Initializer != null)
        {
            var elems = string.Join(", ", ac.Initializer.ElementValues.Select(v => Strip(Value(v, et))));
            if (prim) return char.ToLowerInvariant(primArr[0]) + primArr.Substring(1) + "Of(" + elems + ")";
            return "arrayOf<" + KBoxed(et) + ">(" + elems + ")";
        }
        var n = Index(ac.DimensionSizes[0]);
        if (prim) return primArr + "(" + n + ")";
        if (IsStructType(et))
        {
            var z = DefaultOf(et);
            return "Array<" + KBoxed(et) + ">(" + n + ") { " + z + " }";
        }
        return "arrayOfNulls<" + NN(KBoxed(et)) + ">(" + n + ") as Array<" + KBoxed(et) + ">";
    }

    // ---- operators
    /// <summary>l op r for operands of C# type t (after C#'s promotions), with the unsigned helpers.</summary>
    static string BinOp(BinaryOperatorKind k, ITypeSymbol t, string l, string r, ITypeSymbol? rt)
    {
        var u = Under(t);
        bool uns = u.SpecialType is SpecialType.System_UInt32 or SpecialType.System_UInt64;
        bool wide = u.SpecialType == SpecialType.System_UInt64;
        string box = wide ? "java.lang.Long" : "Integer";
        if (uns)
        {
            switch (k)
            {
                case BinaryOperatorKind.Divide: return box + ".divideUnsigned(" + Strip(l) + ", " + Strip(r) + ")";
                case BinaryOperatorKind.Remainder: return box + ".remainderUnsigned(" + Strip(l) + ", " + Strip(r) + ")";
                case BinaryOperatorKind.RightShift: return "(" + l + " ushr " + Strip(r) + ")";
                case BinaryOperatorKind.LessThan: return "(" + box + ".compareUnsigned(" + Strip(l) + ", " + Strip(r) + ") < 0)";
                case BinaryOperatorKind.LessThanOrEqual: return "(" + box + ".compareUnsigned(" + Strip(l) + ", " + Strip(r) + ") <= 0)";
                case BinaryOperatorKind.GreaterThan: return "(" + box + ".compareUnsigned(" + Strip(l) + ", " + Strip(r) + ") > 0)";
                case BinaryOperatorKind.GreaterThanOrEqual: return "(" + box + ".compareUnsigned(" + Strip(l) + ", " + Strip(r) + ") >= 0)";
            }
        }
        bool isBool = u.SpecialType == SpecialType.System_Boolean;
        switch (k)
        {
            case BinaryOperatorKind.Add: return "(" + l + " + " + r + ")";
            case BinaryOperatorKind.Subtract: return "(" + l + " - " + r + ")";
            case BinaryOperatorKind.Multiply: return "(" + l + " * " + r + ")";
            case BinaryOperatorKind.Divide: return "(" + l + " / " + r + ")";
            case BinaryOperatorKind.Remainder: return "(" + l + " % " + r + ")";
            case BinaryOperatorKind.LeftShift: return "(" + l + " shl " + Strip(r) + ")";
            case BinaryOperatorKind.RightShift: return "(" + l + " shr " + Strip(r) + ")";
            case BinaryOperatorKind.UnsignedRightShift: return "(" + l + " ushr " + Strip(r) + ")";
            case BinaryOperatorKind.And: return "(" + l + " and " + r + ")";
            case BinaryOperatorKind.Or: return "(" + l + " or " + r + ")";
            case BinaryOperatorKind.ExclusiveOr: return "(" + l + " xor " + r + ")";
            case BinaryOperatorKind.ConditionalAnd: return "(" + l + " && " + r + ")";
            case BinaryOperatorKind.ConditionalOr: return "(" + l + " || " + r + ")";
            case BinaryOperatorKind.Equals: return "(" + l + " == " + r + ")";
            case BinaryOperatorKind.NotEquals: return "(" + l + " != " + r + ")";
            case BinaryOperatorKind.LessThan: return "(" + l + " < " + r + ")";
            case BinaryOperatorKind.LessThanOrEqual: return "(" + l + " <= " + r + ")";
            case BinaryOperatorKind.GreaterThan: return "(" + l + " > " + r + ")";
            case BinaryOperatorKind.GreaterThanOrEqual: return "(" + l + " >= " + r + ")";
        }
        return Todo("operator " + k + (isBool ? " on bool" : ""));
    }

    string Binary(IBinaryOperation b)
    {
        if (b.OperatorMethod != null)
        {
            if (IsSystem(b.OperatorMethod) && b.OperatorMethod.ContainingType.SpecialType == SpecialType.System_String)
            {
                var a0 = X(b.LeftOperand); var b0 = X(b.RightOperand);
                if (b.OperatorKind == BinaryOperatorKind.Equals) return "(" + a0 + " == " + b0 + ")";
                if (b.OperatorKind == BinaryOperatorKind.NotEquals) return "(" + a0 + " != " + b0 + ")";
                if (b.OperatorKind == BinaryOperatorKind.Add)
                    return "(" + (b.LeftOperand.Type?.SpecialType == SpecialType.System_String ? a0 : "\"\" + " + a0) + " + " + b0 + ")";
            }
            return UserBinary(b.OperatorMethod, Value(b.LeftOperand, b.OperatorMethod.Parameters[0].Type), Value(b.RightOperand, b.OperatorMethod.Parameters[1].Type));
        }
        var lt = b.LeftOperand.Type;
        var rt = b.RightOperand.Type;
        // a short-circuit operator whose right side needs statements first: an if before the statement
        var l = X(b.LeftOperand);
        var saved = Pre; Pre = new List<string>();
        var r = X(b.RightOperand);
        var pr = Pre; Pre = saved;
        if (pr.Count > 0)
        {
            if (b.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
            {
                var tv = Fresh("t");
                Pre.Add("var " + tv + ": Boolean = " + Strip(l));
                Pre.Add("if (" + (b.OperatorKind == BinaryOperatorKind.ConditionalAnd ? tv : "!" + tv) + ") {");
                Pre.AddRange(pr);
                Pre.Add(tv + " = " + Strip(r));
                Pre.Add("}");
                return tv;
            }
            Pre.AddRange(pr);
        }
        if (lt != null && lt.SpecialType == SpecialType.System_String && b.OperatorKind == BinaryOperatorKind.Add)
        {
            if (rt is ITypeSymbol rts && IsFloat(rts) || IsFloat(lt)) Note("string concatenation with a floating-point number");
            return "(" + l + " + " + r + ")";
        }
        if (b.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && lt != null && rt != null && PrimCode(lt) == null && PrimCode(rt) == null)
            return "(" + l + (b.OperatorKind == BinaryOperatorKind.Equals ? " === " : " !== ") + r + ")";       // references: identity
        if (b.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && (lt == null || rt == null))
            return "(" + l + (b.OperatorKind == BinaryOperatorKind.Equals ? " === " : " !== ") + r + ")";
        if (b.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && lt != null && rt != null &&
            (PrimCode(lt) == null) != (PrimCode(rt) == null))
            Note("equality of a primitive and a reference");
        return BinOp(b.OperatorKind, lt ?? b.Type!, l, r, rt);
    }

    string UserBinary(IMethodSymbol op, string l, string r)
    {
        var t = op.ContainingType;
        var opn = op.Name switch
        {
            "op_Equality" => "op_Eq", "op_Inequality" => "op_Ne", "op_Addition" => "op_Add", "op_Subtraction" => "op_Sub",
            "op_LessThan" => "op_Lt", "op_GreaterThan" => "op_Gt", "op_LessThanOrEqual" => "op_Le", "op_GreaterThanOrEqual" => "op_Ge",
            "op_Multiply" => "op_Mul", "op_Division" => "op_Div",
            _ => Todo("user operator " + op.Name)
        };
        if (!IsRuntime(op)) return Todo("engine operator");
        var overloads = t.GetMembers(op.Name).OfType<IMethodSymbol>().Count();
        var sfx = overloads > 1 ? "_" + string.Join("_", op.OriginalDefinition.Parameters.Select(p => ShortType(p.Type))) : "";
        var cn = RuntimeClass(t);
        var spec = Spec(t, null);
        var n = cn + "." + opn + sfx + spec + (spec.Length == 0 ? TypeWitness(t, op) : "");
        Use("operator " + op.ToDisplayString(), cn + "." + opn + sfx + spec);
        return n + "(" + Strip(l) + ", " + Strip(r) + ")";
    }

    string Unary(IUnaryOperation u)
    {
        if (u.OperatorMethod != null) return Todo("user unary operator");
        var o = X(u.Operand);
        return u.OperatorKind switch
        {
            UnaryOperatorKind.Minus => "(-" + Paren(o) + ")",
            UnaryOperatorKind.Plus => o,
            UnaryOperatorKind.BitwiseNegation => Paren(o) + ".inv()",
            UnaryOperatorKind.Not => "(!" + Paren(o) + ")",
            _ => Todo("unary " + u.OperatorKind)
        };
    }

    string Ternary(IConditionalOperation c)
    {
        var cond = X(c.Condition);
        var t = c.Type!;
        if (c.IsRef) return "(if (" + Strip(cond) + ") " + RefOf(c.WhenTrue) + " else " + RefOf(c.WhenFalse!) + ")!!.get()";
        var saved = Pre; Pre = new List<string>();
        var a = Value(c.WhenTrue, t); var pa = Pre; Pre = new List<string>();
        var b = Value(c.WhenFalse!, t); var pb = Pre; Pre = saved;
        var kt = KType(t);
        if (pa.Count > 0 || pb.Count > 0)
        {
            // a branch needs statements: an if-else before the statement fills a temporary
            var tv = Fresh("t");
            Pre.Add("var " + tv + ": " + kt + " = " + DefaultForKt(kt));
            Pre.Add("if (" + Strip(cond) + ") {");
            Pre.AddRange(pa); Pre.Add(tv + " = " + Strip(a));
            Pre.Add("} else {");
            Pre.AddRange(pb); Pre.Add(tv + " = " + Strip(b));
            Pre.Add("}");
            return tv;
        }
        return "(if (" + Strip(cond) + ") " + Strip(a) + " else " + Strip(b) + ")";
    }

    string CondAccess(IConditionalAccessOperation ca)
    {
        var recv = X(ca.Operation);
        var r = recv;
        if (!Simple(recv)) { r = Fresh("ca"); Pre.Add("val " + r + ": " + KType(ca.Operation.Type!) + " = " + recv); }
        condAccess.Push(r);
        var inner = X(ca.WhenNotNull);
        condAccess.Pop();
        var t = ca.Type!;
        var none = PrimCode(t) != null ? DefaultOf(t) : "null";
        return "(if (" + r + " === null) " + none + " else " + Strip(inner) + ")";
    }

    string Lambda(IAnonymousFunctionOperation af, ITypeSymbol? target)
    {
        var m = af.Symbol;
        var lab = Fresh("lam");
        var sub = new KtFn(this, m, lab);
        var dt = target as INamedTypeSymbol;
        if (dt == null) return Todo("lambda without a delegate type");
        bool sys = IsSystem(dt);
        var ps = m.Parameters.Select(p => sub.DeclareParam(p, sys && p.RefKind == RefKind.None && PrimCode(p.Type) != null)).ToList();
        var body = new List<string>();
        sub.baseInd = baseInd;
        sub.Block(af.Body.Operations, body, 2);
        if (!m.ReturnsVoid && !EndsInJump(body)) body.Add(I(2) + "throw IllegalStateException(\"unreachable\")");
        var head = NN(KType(dt)) + " " + lab + "@{ " + string.Join(", ", ps) + (ps.Count > 0 ? " ->" : "");
        return "(" + head + "\n" + sub.Prologue(I(2)) + string.Concat(body.Select(l => l + "\n")) + I(1) + "})";
    }

    string DelegateCreation(IDelegateCreationOperation dc)
    {
        var t = dc.Type!;
        if (dc.Target is IAnonymousFunctionOperation af) return Lambda(af, t);
        if (dc.Target is IMethodReferenceOperation mr)
        {
            var m = mr.Method;
            var dt = (INamedTypeSymbol)t;
            var inv = dt.DelegateInvokeMethod!;
            bool sys = IsSystem(dt);
            var ps = inv.Parameters.Select(_ => Fresh("p")).ToList();
            var args = string.Join(", ", inv.Parameters.Select((p, i) => sys && PrimCode(p.Type) != null ? ps[i] + "!!" : ps[i]));
            string f;
            if (IsRuntime(m)) f = (m.IsStatic ? RuntimeClass(m.ContainingType) : Recv(mr.Instance!)) + "." + RtName(m.Name);
            else if (m.IsStatic) f = TypeIdent(m.ContainingType) + "." + MethodIdent(m);
            else f = Recv(mr.Instance!) + "." + MethodIdent(m);
            return "(" + NN(KType(t)) + " { " + string.Join(", ", ps) + (ps.Count > 0 ? " -> " : "") + f + "(" + args + ") })";
        }
        return Todo("delegate creation " + dc.Target.Kind);
    }
}
}
