// KtTypes.cs - C# types to Kotlin types, the boxing analysis, and the type declarations of the Kotlin engine.
// The Java translator's JavaTypes.cs, copied and changed for Kotlin: every reference type is nullable (T?); a class
// is open (Kotlin's are final by default) and a method that overrides one in a supertype says override; static
// members go to a companion object (@JvmStatic, @JvmField) or, for a static class, to an object; an enum is an
// object of const vals; a delegate is a fun interface.
// Written by Claude (Anthropic) for Robert Dickinson's SWMM6 ports; see AI-USE.md and kotlin/README.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

public static partial class Cs2Kotlin
{
    static readonly List<INamedTypeSymbol> engineTypes = new();

    static bool IsEngine(ITypeSymbol t) => t is INamedTypeSymbol n && !IsRuntime(n) && !IsSystem(n) && n.Locations.Any(l => l.IsInSource);

    static void CollectTypes(List<SyntaxTree> gen)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var t in gen)
        {
            foreach (var td in t.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var s = (INamedTypeSymbol)M(td).GetDeclaredSymbol(td)!;
                if (seen.Add(s)) engineTypes.Add(s);
            }
            foreach (var dd in t.GetRoot().DescendantNodes().OfType<DelegateDeclarationSyntax>())
            {
                var s = (INamedTypeSymbol)M(dd).GetDeclaredSymbol(dd)!;
                if (seen.Add(s)) engineTypes.Add(s);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ boxing analysis
    // A local or parameter is a cell (cpp.RefX.Cell) when its address is taken (a ref argument, ref local or ref
    // return) or when a lambda captures it and it is assigned anywhere but its declaration (as in the Java engine;
    // Kotlin could capture it, but the cell keeps the two translations alike). A parameter that is assigned is copied
    // into a local var of the same name, since Kotlin's parameters are read-only.
    static readonly HashSet<ISymbol> boxed = new(SymbolEqualityComparer.Default);
    static readonly HashSet<ISymbol> mutatedSyms = new(SymbolEqualityComparer.Default);

    static void Analyze(List<SyntaxTree> gen)
    {
        var addr = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var mutated = mutatedSyms;
        var captured = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var t in gen)
        {
            var m = models[t];
            foreach (var node in t.GetRoot().DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax || n is VariableDeclaratorSyntax && n.Parent?.Parent is FieldDeclarationSyntax))
            {
                IOperation? root = node switch
                {
                    BaseMethodDeclarationSyntax bm => bm.Body != null ? m.GetOperation(bm.Body) : bm.ExpressionBody != null ? m.GetOperation(bm.ExpressionBody) : null,
                    VariableDeclaratorSyntax vd => vd.Initializer != null ? m.GetOperation(vd.Initializer) : null,
                    _ => null
                };
                if (root == null) continue;
                if (node is ConstructorDeclarationSyntax cds && cds.Initializer != null && m.GetOperation(cds.Initializer) is IOperation ci) Scan(ci);
                Scan(root);
            }
        }
        void Scan(IOperation root)
        {
            foreach (var op in root.DescendantsAndSelf())
            {
                switch (op)
                {
                    case IArgumentOperation a when a.Parameter != null && (a.Parameter.RefKind == RefKind.Ref || a.Parameter.RefKind == RefKind.Out):
                        Mark(a.Value, addr); break;
                    case IVariableDeclaratorOperation vd when vd.Symbol.RefKind != RefKind.None && vd.Initializer != null:
                        Mark(vd.Initializer.Value, addr); break;
                    case IReturnOperation r when r.ReturnedValue != null && IsRefReturn(r):
                        Mark(r.ReturnedValue, addr); break;
                    case ISimpleAssignmentOperation sa when sa.IsRef:
                        Mark(sa.Value, addr); break;
                    case IConditionalOperation c when c.IsRef:
                        Mark(c.WhenTrue, addr); if (c.WhenFalse != null) Mark(c.WhenFalse, addr); break;
                    case IAssignmentOperation asg:
                        Mark(asg.Target, mutated); break;
                    case IIncrementOrDecrementOperation inc:
                        Mark(inc.Target, mutated); break;
                    case IAnonymousFunctionOperation af:
                        foreach (var r in af.Body.Descendants())
                        {
                            ISymbol? s = r is ILocalReferenceOperation lr ? lr.Local : r is IParameterReferenceOperation pr ? pr.Parameter : null;
                            if (s == null) continue;
                            if (!SymbolEqualityComparer.Default.Equals(s.ContainingSymbol, af.Symbol) && !IsInside(s.ContainingSymbol, af.Symbol)) captured.Add(s);
                        }
                        break;
                }
            }
        }
        foreach (var s in addr) if (!IsRefSym(s)) boxed.Add(s);
        foreach (var s in captured) if ((mutated.Contains(s) || addr.Contains(s)) && !IsRefSym(s)) boxed.Add(s);
    }

    static bool IsRefSym(ISymbol s) => s is ILocalSymbol l && l.RefKind != RefKind.None || s is IParameterSymbol p && p.RefKind != RefKind.None;

    static bool IsInside(ISymbol? s, ISymbol lambda)
    {
        for (var x = s; x != null; x = x.ContainingSymbol) if (SymbolEqualityComparer.Default.Equals(x, lambda)) return true;
        return false;
    }

    static bool IsRefReturn(IReturnOperation r)
    {
        for (var p = r.Parent; p != null; p = p.Parent)
        {
            if (p is IAnonymousFunctionOperation af) return af.Symbol.RefKind != RefKind.None;
            if (p is IMethodBodyOperation || p is IBlockOperation && p.Parent == null) break;
        }
        var sm = r.SemanticModel;
        var md = r.Syntax.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return md != null && sm != null && sm.GetDeclaredSymbol(md) is IMethodSymbol ms && ms.RefKind != RefKind.None;
    }

    static void Mark(IOperation op, HashSet<ISymbol> set)
    {
        while (op is IConversionOperation cv && cv.Conversion.IsIdentity) op = cv.Operand;
        if (op is ILocalReferenceOperation l) set.Add(l.Local);
        else if (op is IParameterReferenceOperation p) set.Add(p.Parameter);
    }

    // ------------------------------------------------------------------------------------------------ types
    static readonly HashSet<string> Specializable = new() { "Vec", "Ptr", "Iter", "RevIter", "CSpan", "Opt", "Atomic", "RefAt", "RefGet" };

    /// <summary>The one-letter code of a primitive JVM type (D, F, I, L, B, Z, S, C), or null.</summary>
    static string? PrimCode(ITypeSymbol t)
    {
        if (t.TypeKind == TypeKind.Enum && t is INamedTypeSymbol en) return PrimCode(en.EnumUnderlyingType!);
        return t.SpecialType switch
        {
            SpecialType.System_Double => "D", SpecialType.System_Single => "F",
            SpecialType.System_Int32 or SpecialType.System_UInt32 => "I",
            SpecialType.System_Int64 or SpecialType.System_UInt64 => "L",
            SpecialType.System_Byte or SpecialType.System_SByte => "B",
            SpecialType.System_Boolean => "Z",
            SpecialType.System_Int16 => "S",
            SpecialType.System_UInt16 or SpecialType.System_Char => "C",
            _ => null
        };
    }

    /// <summary>The Kotlin type of a value: a primitive as itself, everything else nullable.</summary>
    static string KType(ITypeSymbol t) => KT(t, false);
    /// <summary>A type argument: primitives boxed (Int?), everything else as KType.</summary>
    static string KBoxed(ITypeSymbol t) => KT(t, true);
    /// <summary>The type without its trailing ? (a supertype in a class header, a SAM constructor, a cast target).</summary>
    static string NN(string t) => t.EndsWith("?") ? t.Substring(0, t.Length - 1) : t;

    static string KT(ITypeSymbol t, bool box)
    {
        string q = box ? "?" : "";
        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean: return "Boolean" + q;
            case SpecialType.System_SByte: case SpecialType.System_Byte: return "Byte" + q;
            case SpecialType.System_Int16: return "Short" + q;
            case SpecialType.System_UInt16: case SpecialType.System_Char: return "Char" + q;
            case SpecialType.System_Int32: case SpecialType.System_UInt32: return "Int" + q;
            case SpecialType.System_Int64: case SpecialType.System_UInt64: return "Long" + q;
            case SpecialType.System_Single: return "Float" + q;
            case SpecialType.System_Double: return "Double" + q;
            case SpecialType.System_String: return "String?";
            case SpecialType.System_Object: return "Any?";
            case SpecialType.System_Void: return "Unit";
        }
        if (t is IArrayTypeSymbol at)
        {
            if (at.Rank != 1) Todo("multidimensional array");
            var e = at.ElementType;
            if (PrimCode(e) != null) return KT(e, false) + "Array?";
            return "Array<" + KBoxed(e) + ">?";
        }
        if (t is ITypeParameterSymbol tp) return tp.Name;
        if (t is INamedTypeSymbol n)
        {
            if (n.TypeKind == TypeKind.Enum) return KT(n.EnumUnderlyingType!, box);
            if (n.TypeKind == TypeKind.Delegate)
            {
                if (IsSystem(n)) return SysDelegate(n) + "?";
                if (IsRuntime(n)) return RuntimeTypeName(n) + "?";
                return TypeIdent(n) + "?";
            }
            if (IsRuntime(n)) return RuntimeTypeName(n) + "?";
            if (IsSystem(n))
            {
                switch (StripArity(n.Name))
                {
                    case "Exception": return "Throwable?";
                    case "IDisposable": return "cpp.IDisposable?";
                    case "Nullable": return KBoxed(n.TypeArguments[0]);
                    case "IEnumerable": return "Iterable<" + KBoxed(n.TypeArguments[0]) + ">?";
                    case "ValueType": return "Any?";
                }
                return Todo("system type " + n.ToDisplayString());
            }
            return TypeIdent(n) + "?";
        }
        return Todo("type " + t.ToDisplayString());
    }

    /// <summary>A runtime type: cpp.Name, a specialised cpp.NameD for a primitive argument of the containers in
    /// Specializable, otherwise cpp.Name&lt;boxed arguments&gt; (no trailing ?).</summary>
    static string RuntimeTypeName(INamedTypeSymbol n)
    {
        var parts = new List<string>();
        for (var c = n; c != null; c = c.ContainingType) parts.Insert(0, StripArity(c.Name));
        var s = "cpp." + string.Join(".", parts);
        if (n.TypeArguments.Length == 1 && Specializable.Contains(StripArity(n.Name)) && PrimCode(n.TypeArguments[0]) is string code)
        {
            Use("type " + n.ConstructUnboundGenericType().ToDisplayString().Replace("<>", "<" + n.TypeArguments[0].ToDisplayString() + ">"), s + code);
            return s + code;
        }
        Use("type " + n.OriginalDefinition.ToDisplayString(), s);
        if (n.TypeArguments.Length > 0) s += "<" + string.Join(", ", n.TypeArguments.Select(KBoxed)) + ">";
        return s;
    }

    /// <summary>The raw (unparameterised) class of a runtime type, for static members.</summary>
    static string RuntimeClass(INamedTypeSymbol n)
    {
        var s = RuntimeTypeName(n);
        var i = s.IndexOf('<');
        return i < 0 ? s : s.Substring(0, i);
    }

    static string SysDelegate(INamedTypeSymbol n)
    {
        var inv = n.DelegateInvokeMethod!;
        var name = StripArity(n.Name);
        if (name == "Func") return "cpp.Fn.F" + inv.Parameters.Length + "<" + string.Join(", ", inv.Parameters.Select(p => KBoxed(p.Type)).Append(KBoxed(inv.ReturnType))) + ">";
        if (name == "Action") return "cpp.Fn.A" + inv.Parameters.Length + (inv.Parameters.Length > 0 ? "<" + string.Join(", ", inv.Parameters.Select(p => KBoxed(p.Type))) + ">" : "");
        if (name == "Predicate") return "cpp.Fn.F1<" + KBoxed(inv.Parameters[0].Type) + ", Boolean?>";
        if (name == "Comparison") return "cpp.Fn.F2<" + KBoxed(inv.Parameters[0].Type) + ", " + KBoxed(inv.Parameters[1].Type) + ", Int?>";
        return Todo("system delegate " + n.ToDisplayString());
    }

    /// <summary>The method a delegate of type t is called through.</summary>
    static string DelegateMethod(INamedTypeSymbol t)
    {
        if (IsSystem(t)) return StripArity(t.Name) == "Action" ? "run" : "apply";
        return "invoke";
    }

    /// <summary>The Kotlin type of a ref to a value of type t: cpp.RefD? ... cpp.RefO&lt;T?&gt;?.</summary>
    static string RefType(ITypeSymbol t) => PrimCode(t) is string c ? "cpp.Ref" + c + "?" : "cpp.RefO<" + KBoxed(t) + ">?";
    static string CellType(ITypeSymbol t) => PrimCode(t) is string c ? "cpp.Ref" + c + ".Cell" : "cpp.RefO.Cell<" + KBoxed(t) + ">";

    static string ParamType(IParameterSymbol p) => p.RefKind is RefKind.Ref or RefKind.Out ? RefType(p.Type) : KType(p.Type);
    static string RetType(IMethodSymbol m) => m.RefKind != RefKind.None ? RefType(m.ReturnType) : KType(m.ReturnType);

    static bool IsStructType(ITypeSymbol t) => t.TypeKind == TypeKind.Struct && t.SpecialType == SpecialType.None && !(t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);

    /// <summary>The value a field or array element of type t starts with (C#'s default).</summary>
    static string DefaultOf(ITypeSymbol t)
    {
        if (t.SpecialType == SpecialType.System_Boolean) return "false";
        if (PrimCode(t) is string c)
            return c switch { "D" => "0.0", "F" => "0.0f", "L" => "0L", "B" => "(0).toByte()", "S" => "(0).toShort()", "C" => "'\\u0000'", _ => "0" };
        if (IsStructType(t) && t is INamedTypeSymbol n)
        {
            if (IsRuntime(n))
            {
                var tn = RuntimeTypeName(n);
                if (tn.Contains('<')) { Use("static " + n.OriginalDefinition.ToDisplayString() + ".zero()", RuntimeClass(n) + ".zero()"); return "(" + RuntimeClass(n) + ".zero() as " + tn + "?)"; }
                Use("static " + n.ToDisplayString() + ".ZERO", tn + ".ZERO");
                return tn + ".ZERO";
            }
            return Todo("engine struct " + n.Name);
        }
        if (t is ITypeParameterSymbol tp) return "(null as " + tp.Name + ")";
        return "null";
    }

    // ------------------------------------------------------------------------------------------------ overrides
    /// <summary>The Kotlin signature key of a method as declared (name and parameter types).</summary>
    static string SigKey(IMethodSymbol m)
    {
        var bs = BoxedSignature(m);
        return MethodIdent(m) + "(" + string.Join(",", m.Parameters.Select((p, i) => bs != null ? bs.Value.ps[i] : ParamType(p))) + ")";
    }

    /// <summary>Whether a method must say override in Kotlin: C# overrides, implements a runtime or system interface
    /// member, or has the Kotlin name and parameter types of a method of an engine base class (Clone and MoveFrom
    /// re-declared down an ICopy hierarchy).</summary>
    static bool KtOverrides(IMethodSymbol m)
    {
        if (m.IsStatic || m.MethodKind != MethodKind.Ordinary) return false;
        if (m.IsOverride) return true;
        // a runtime or system interface member it implements, if the class (or a base) lists that interface in Kotlin
        // (a generic runtime interface a base class already implements is not listed again: ICopy<Base> only)
        foreach (var i in m.ContainingType.AllInterfaces)
        {
            if (!(IsRuntime(i) || IsSystem(i))) continue;
            bool listed = !(IsRuntime(i) && i.IsGenericType && m.ContainingType.BaseType is INamedTypeSymbol bb && IsEngine(bb)
                && bb.AllInterfaces.Any(bi => SymbolEqualityComparer.Default.Equals(bi.OriginalDefinition, i.OriginalDefinition))
                && !bb.AllInterfaces.Any(bi => SymbolEqualityComparer.Default.Equals(bi, i)));
            foreach (var im in i.GetMembers().OfType<IMethodSymbol>())
                if (SymbolEqualityComparer.Default.Equals(m.ContainingType.FindImplementationForInterfaceMember(im), m) && listed) return true;
        }
        var key = SigKey(m);
        for (var b = m.ContainingType.BaseType; b != null && IsEngine(b); b = b.BaseType)
            foreach (var x in b.GetMembers().OfType<IMethodSymbol>())
                if (x.MethodKind == MethodKind.Ordinary && !x.IsStatic && SigKey(x) == key) return true;
        return false;
    }

    // ------------------------------------------------------------------------------------------------ declarations
    static void TypeDecl(INamedTypeSymbol s, StringBuilder sb)
    {
        var nm = TypeIdent(s);
        if (s.TypeKind == TypeKind.Delegate)
        {
            var inv = s.DelegateInvokeMethod!;
            sb.Append("fun interface ").Append(nm).Append(" {\n\tfun invoke(")
              .Append(string.Join(", ", inv.Parameters.Select(p => Id(p.Name) + ": " + ParamType(p)))).Append(")");
            var rt = RetType(inv);
            if (rt != "Unit") sb.Append(": ").Append(rt);
            sb.Append("\n}\n");
            return;
        }
        if (s.TypeKind == TypeKind.Enum)
        {
            sb.Append("object ").Append(nm).Append(" {\n");
            var ut = KType(s.EnumUnderlyingType!);
            foreach (var f in s.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
                sb.Append("\tconst val ").Append(Id(f.Name)).Append(": ").Append(ut).Append(" = ").Append(KtFn.Const(f.ConstantValue, s.EnumUnderlyingType!)).Append('\n');
            sb.Append("}\n");
            return;
        }
        if (s.TypeKind == TypeKind.Interface) { sb.Append("interface ").Append(nm).Append('\n'); Note("engine interface " + s.Name); return; }
        if (s.TypeKind != TypeKind.Class) { sb.Append("// TODO type kind ").Append(s.TypeKind).Append('\n'); Note("type kind " + s.TypeKind); return; }

        var decls = s.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<ClassDeclarationSyntax>().ToList();
        bool hasCtor = decls.Any(cd => cd.Members.Any(m => m is ConstructorDeclarationSyntax c && !c.Modifiers.Any(SyntaxKind.StaticKeyword)));
        bool isObject = s.IsStatic;
        var head = new StringBuilder();
        if (isObject) head.Append("object ").Append(nm);
        else
        {
            head.Append(s.IsAbstract ? "abstract " : "open ");
            head.Append("class ").Append(nm);
        }
        var sup = new List<string>();
        var b = s.BaseType;
        if (b != null && b.SpecialType != SpecialType.System_Object)
        {
            string? bt = null;
            if (IsEngine(b)) bt = TypeIdent(b);
            else if (IsRuntime(b)) bt = RuntimeTypeName(b);
            else if (IsSystem(b) && b.Name == "Exception") bt = "RuntimeException";
            else Note("base type " + b.ToDisplayString());
            if (bt != null) sup.Add(bt + (hasCtor ? "" : "()"));
        }
        foreach (var i in s.Interfaces)
        {
            // Kotlin, like Java, cannot implement ICopy<Base> (inherited) and ICopy<Derived> both: keep the base's
            if (IsRuntime(i) && i.IsGenericType && b != null && IsEngine(b) && b.AllInterfaces.Any(bi => SymbolEqualityComparer.Default.Equals(bi.OriginalDefinition, i.OriginalDefinition)))
                continue;
            if (IsRuntime(i)) sup.Add(RuntimeTypeName(i));
            else if (IsSystem(i) && i.Name == "IDisposable") sup.Add("cpp.IDisposable");
            else if (IsSystem(i) && (i.Name == "IEquatable" || i.Name == "IComparable")) { }
            else if (IsEngine(i)) sup.Add(TypeIdent(i));
            else Note("interface " + i.ToDisplayString());
        }
        if (sup.Count > 0) head.Append(" : ").Append(string.Join(", ", sup));
        sb.Append(head).Append(" {\n");

        var inst = new StringBuilder();
        var stat = new StringBuilder();
        foreach (var f in s.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsImplicitlyDeclared))
        {
            var dst = f.IsStatic || f.IsConst ? stat : inst;
            try { FieldDecl(f, dst, isObject ? "\t" : f.IsStatic || f.IsConst ? "\t\t" : "\t"); }
            catch (Todo e) { dst.Append("\t// TODO field ").Append(f.Name).Append(": ").Append(e.Message).Append('\n'); }
        }
        foreach (var cd in decls)
            foreach (var m in cd.Members)
            {
                bool isStatic = m is BaseMethodDeclarationSyntax bm && bm.Modifiers.Any(SyntaxKind.StaticKeyword);
                var dst = isStatic ? stat : inst;
                var ind = isObject ? "\t" : isStatic ? "\t\t" : "\t";
                try
                {
                    switch (m)
                    {
                        case MethodDeclarationSyntax md: MethodDecl(md, s, dst, ind, isObject); break;
                        case ConstructorDeclarationSyntax ctor: CtorDecl(ctor, s, dst, ind); break;
                        case FieldDeclarationSyntax: break;
                        case BaseTypeDeclarationSyntax: break;      // nested: a class of its own
                        case DelegateDeclarationSyntax: break;
                        default: dst.Append(ind).Append("// TODO member ").Append(m.Kind()).Append('\n'); Note("member " + m.Kind()); break;
                    }
                }
                catch (Todo e) { dst.Append(ind).Append("// TODO ").Append(e.Message).Append('\n'); }
                catch (Exception e) when (e is not global::Todo) { dst.Append(ind).Append("// TODO translator error: ").Append(e.GetType().Name).Append(' ').Append(e.Message.Replace("\n", " ")).Append('\n'); Note("translator error " + e.GetType().Name); }
            }
        if (isObject) { sb.Append(stat).Append(inst); }
        else
        {
            sb.Append(inst);
            if (stat.Length > 0) sb.Append("\tcompanion object {\n").Append(stat).Append("\t}\n");
        }
        sb.Append("}\n");
    }

    static void FieldDecl(IFieldSymbol f, StringBuilder sb, string ind)
    {
        var kt = KType(f.Type);
        var name = FieldIdent(f);
        if (f.IsConst)
        {
            var v = KtFn.Const(f.ConstantValue, f.Type);
            bool constOk = PrimCode(f.Type) != null || f.Type.SpecialType == SpecialType.System_String;
            sb.Append(ind).Append(constOk ? "const val " : "@JvmField val ").Append(name).Append(": ").Append(constOk && f.Type.SpecialType == SpecialType.System_String ? "String" : kt).Append(" = ").Append(v).Append('\n');
            return;
        }
        var syn = f.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        if (syn?.Initializer != null)
        {
            var op = M(syn).GetOperation(syn.Initializer) is IFieldInitializerOperation fi ? fi.Value : M(syn).GetOperation(syn.Initializer.Value);
            if (op == null) Todo("field initializer");
            var fn = new KtFn(null, f.ContainingType);
            var val = fn.Value(op!, f.Type);
            if (fn.Pre.Count == 0) { sb.Append(ind).Append("@JvmField var ").Append(name).Append(": ").Append(kt).Append(" = ").Append(val).Append('\n'); return; }
            if (!f.IsStatic) Todo("instance field initializer with statements");
            sb.Append(ind).Append("@JvmField var ").Append(name).Append(": ").Append(kt).Append(" = ").Append(DefaultOf(f.Type)).Append('\n');
            sb.Append(ind).Append("init {\n");
            foreach (var p in fn.Pre) sb.Append(ind).Append('\t').Append(p).Append('\n');
            sb.Append(ind).Append('\t').Append(name).Append(" = ").Append(val).Append('\n').Append(ind).Append("}\n");
            return;
        }
        sb.Append(ind).Append("@JvmField var ").Append(name).Append(": ").Append(kt).Append(" = ").Append(DefaultOf(f.Type)).Append('\n');
    }

    static void MethodDecl(MethodDeclarationSyntax md, INamedTypeSymbol s, StringBuilder sb, string ind, bool inObject)
    {
        var m = (IMethodSymbol)M(md).GetDeclaredSymbol(md)!;
        var fn = new KtFn(m, s);
        var boxedSig = BoxedSignature(m);
        var head = new StringBuilder(ind);
        if (m.IsStatic) head.Append("@JvmStatic ");
        else if (m.IsAbstract) head.Append("abstract ");
        else if (KtOverrides(m)) head.Append("override ");
        else head.Append("open ");
        head.Append("fun ");
        if (m.IsGenericMethod) head.Append('<').Append(string.Join(", ", m.TypeParameters.Select(t => t.Name))).Append("> ");
        var rt = boxedSig != null ? boxedSig.Value.ret : RetType(m);
        head.Append(MethodIdent(m)).Append('(');
        head.Append(string.Join(", ", m.Parameters.Select((p, i) => fn.DeclareParam(p, boxedSig != null && boxedSig.Value.ps[i] != ParamType(p)) + ": " + (boxedSig != null ? boxedSig.Value.ps[i] : ParamType(p)))));
        head.Append(')');
        if (rt != "Unit") head.Append(": ").Append(rt);
        if (m.IsAbstract) { sb.Append(head).Append("\n\n"); return; }
        string body;
        IOperation? op = md.Body != null ? M(md).GetOperation(md.Body) : md.ExpressionBody != null ? M(md).GetOperation(md.ExpressionBody) : null;
        var bind = ind + "\t";
        try { body = op == null ? "" : fn.Body(op, m, bind); }
        catch (Todo e) { body = bind + "throw IllegalStateException(\"TODO " + KtFn.Esc(e.Message) + "\")\n"; }
        sb.Append(head).Append(" {\n").Append(fn.Prologue(bind)).Append(body).Append(ind).Append("}\n\n");
    }

    /// <summary>A method implementing a member of a generic runtime interface whose type arguments are primitive
    /// (IHashFn&lt;int&gt;.HashOf): Kotlin needs the interface's boxed signature to implement it.</summary>
    static (string ret, string[] ps)? BoxedSignature(IMethodSymbol m)
    {
        if (m.IsStatic) return null;
        foreach (var i in m.ContainingType.AllInterfaces)
        {
            if (!IsRuntime(i) || !i.IsGenericType) continue;
            foreach (var im in i.GetMembers().OfType<IMethodSymbol>())
            {
                var impl = m.ContainingType.FindImplementationForInterfaceMember(im);
                if (!SymbolEqualityComparer.Default.Equals(impl, m)) continue;
                var od = im.OriginalDefinition;
                var ps = od.Parameters.Select((p, k) => p.Type is ITypeParameterSymbol ? (p.RefKind is RefKind.Ref or RefKind.Out ? "cpp.RefO<" + KBoxed(m.Parameters[k].Type) + ">?" : KBoxed(m.Parameters[k].Type)) : ParamType(m.Parameters[k])).ToArray();
                var ret = od.ReturnType is ITypeParameterSymbol ? KBoxed(m.ReturnType) : RetType(m);
                return (ret, ps);
            }
        }
        return null;
    }

    static void CtorDecl(ConstructorDeclarationSyntax cd, INamedTypeSymbol s, StringBuilder sb, string ind)
    {
        var c = (IMethodSymbol)M(cd).GetDeclaredSymbol(cd)!;
        var fn = new KtFn(c, s);
        var bind = ind + "\t";
        if (c.IsStatic)
        {
            var sbody = cd.Body != null ? fn.Body(M(cd).GetOperation(cd.Body)!, c, bind) : "";
            sb.Append(ind).Append("init {\n").Append(fn.Prologue(bind)).Append(sbody).Append(ind).Append("}\n\n");
            return;
        }
        var head = new StringBuilder(ind).Append("constructor(")
            .Append(string.Join(", ", c.Parameters.Select(p => fn.DeclareParam(p, false) + ": " + ParamType(p)))).Append(')');
        string deleg = "super()";
        if (cd.Initializer != null && M(cd).GetOperation(cd.Initializer) is IOperation init)
        {
            var inv = init is IExpressionStatementOperation es ? es.Operation : init;
            if (inv is IInvocationOperation ci)
            {
                var who = cd.Initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword) ? "this" : "super";
                var args = fn.Args(ci.TargetMethod, ci.Arguments);
                if (fn.Pre.Count > 0) Todo("constructor initializer with statements");
                deleg = who + "(" + args + ")";
            }
            else Todo("constructor initializer " + inv.Kind);
        }
        head.Append(" : ").Append(deleg);
        var body = cd.Body != null ? fn.Body(M(cd).GetOperation(cd.Body)!, c, bind) : "";
        sb.Append(head).Append(" {\n").Append(fn.Prologue(bind)).Append(body).Append(ind).Append("}\n\n");
    }
}
