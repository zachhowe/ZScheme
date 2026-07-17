using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;

namespace ZScheme.Compiler.Types;

/// <summary>
///     Checks that every <c>match</c> expression is exhaustive over its scrutinee's union cases,
///     reporting a compile error otherwise. Runs after type inference (so each scrutinee's
///     <see cref="AstNode.ResolvedType" /> is fully substitution-applied) and before IR lowering,
///     so codegen never runs on a program with a proven-incomplete match. This is the sole
///     production caller of <see cref="ExhaustivenessChecker" /> — mirrors
///     <see cref="EntryPointValidator" /> as a standalone post-inference validator.
///
///     Union case names come from two sources: locally-declared unions (walked out of the AST's
///     <see cref="AstNode.UnionDecl" /> forms) and unions imported from precompiled modules
///     (passed in as <see cref="IrNode.UnionDecl" />; use their full <c>Cases</c> rather than a
///     module's exported-ctor map, which is filtered to exported case names).
///
///     Non-union non-exhaustiveness (bool, bare literals) is left to the checker as a warning; the
///     backends keep a last-resort runtime throw for what the front end can't prove.
/// </summary>
public sealed class ExhaustivenessValidator(DiagnosticBag diagnostics)
{
    private readonly ExhaustivenessChecker _checker = new(diagnostics);

    public void Validate(AstNode.Program program, IEnumerable<IrNode.UnionDecl> importedUnions)
    {
        foreach (var union in importedUnions)
            _checker.RegisterUnion(
                union.Name,
                [.. union.Cases.Select(c => (c.Name, c.Fields.Count))]
            );

        foreach (var form in AllForms(program))
            if (form is AstNode.UnionDecl u)
                _checker.RegisterUnion(
                    u.UnionName,
                    [.. u.Cases.Select(c => (c.Name, c.Fields.Count))]
                );

        foreach (var form in program.TopLevelForms)
            Walk(form);
    }

    /// <summary>The type name the exhaustiveness checker keys unions on (union name for
    ///     named types, <c>"Bool"</c> for booleans, null for anything else).</summary>
    private static string? ScrutineeTypeName(ZType? type)
    {
        return type switch
        {
            ZType.ZNamedType named => named.Name,
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Bool } => "Bool",
            _ => null,
        };
    }

    private static IEnumerable<AstNode> AllForms(AstNode.Program program)
    {
        return program.TopLevelForms.SelectMany(f =>
            f is AstNode.ModuleDecl m ? new[] { f }.Concat(m.Body) : [f]
        );
    }

    // Recursive descent that visits every AstNode.Match anywhere in the tree. The child
    // enumeration mirrors TypeInferer.Resolve exactly — keep the two in sync so a match can
    // never hide inside a node type one walk covers and the other misses.
    private void Walk(AstNode? node)
    {
        switch (node)
        {
            case null:
                break;
            case AstNode.Match m:
                _checker.Check(m, ScrutineeTypeName(m.Scrutinee.ResolvedType));
                Walk(m.Scrutinee);
                foreach (var arm in m.Arms)
                    Walk(arm.Body);
                break;
            case AstNode.Program p:
                foreach (var f in p.TopLevelForms)
                    Walk(f);
                break;
            case AstNode.ModuleDecl md:
                foreach (var f in md.Body)
                    Walk(f);
                break;
            case AstNode.Define d:
                Walk(d.Body);
                break;
            case AstNode.DefineValue dv:
                Walk(dv.Value);
                break;
            case AstNode.DefineAsync da:
                Walk(da.Body);
                break;
            case AstNode.Let l:
                Walk(l.Value);
                Walk(l.Body);
                break;
            case AstNode.Use u:
                Walk(u.Value);
                Walk(u.Body);
                break;
            case AstNode.If i:
                Walk(i.Condition);
                Walk(i.Then);
                Walk(i.Else);
                break;
            case AstNode.Lambda lam:
                Walk(lam.Body);
                break;
            case AstNode.Apply app:
                Walk(app.Function);
                foreach (var a in app.Args)
                    Walk(a);
                break;
            case AstNode.Partial part:
                Walk(part.Function);
                foreach (var a in part.Args)
                    Walk(a);
                break;
            case AstNode.ClrNew cn:
                foreach (var a in cn.Args)
                    Walk(a);
                break;
            case AstNode.Raise r:
                Walk(r.Expr);
                break;
            case AstNode.Await aw:
                Walk(aw.Expr);
                break;
            case AstNode.TupleNew tn:
                foreach (var elem in tn.Elements)
                    Walk(elem);
                break;
            case AstNode.ObjectExpr oe:
                foreach (var meth in oe.Methods)
                    Walk(meth.Body);
                WalkConstructor(oe.Constructor);
                break;
            case AstNode.ClassDecl cd:
                foreach (var meth in cd.Methods)
                    Walk(meth.Body);
                WalkConstructor(cd.Constructor);
                break;
            case AstNode.WithHandlers wh:
                Walk(wh.Body);
                foreach (var h in wh.Handlers)
                    Walk(h.HandlerBody);
                break;
            case AstNode.With w:
                Walk(w.Record);
                foreach (var (_, valueExpr) in w.Updates)
                    Walk(valueExpr);
                break;
            case AstNode.SuperMethodCall smc:
                foreach (var a in smc.Args)
                    Walk(a);
                break;
            case AstNode.SetField sf:
                Walk(sf.Value);
                break;
            // Remaining node types (literals, Name, Import/Export, decls without bodies, etc.)
            // carry no nested expressions and therefore no matches.
            default:
                break;
        }
    }

    private void WalkConstructor(ConstructorDecl? ctor)
    {
        if (ctor is null)
            return;
        if (ctor.SuperArgs is not null)
            foreach (var a in ctor.SuperArgs)
                Walk(a);
        foreach (var (_, v) in ctor.FieldSets)
            Walk(v);
        foreach (var b in ctor.BodyExprs)
            Walk(b);
    }
}
