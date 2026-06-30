using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Types;

/// <summary>
///     Validates the signature of the program's entry point (<c>main</c>) before IR lowering
///     and code generation. <c>main</c> is compiled to the CLR entry point directly — there is
///     no synthesized wrapper that forwards to it and no implicit argument conversion — so its
///     signature must be one the runtime accepts:
///     <list type="bullet">
///         <item>at most one parameter, which (if present) must be a CLR string array —
///             <c>(Mutable-Vector String)</c> or <c>(Clr-Array String)</c>;</item>
///         <item>a return type of <c>Int</c> or <c>Unit</c>. An async <c>main</c>
///             (<c>define-async</c>) may return <c>(Task Int)</c> or <c>(Task Unit)</c>.</item>
///     </list>
///     Reporting these here turns malformed entry points into clear compile errors instead of
///     opaque downstream C#/IL failures (or silently-wrong behavior).
/// </summary>
public sealed class EntryPointValidator(DiagnosticBag diagnostics, TypeAliasRegistry typeAliases)
{
    public void Validate(AstNode.Program program)
    {
        foreach (var form in AllForms(program))
            switch (form)
            {
                case AstNode.Define { FnName: "main" } d:
                    ValidateParam(d.Params, NameSpanOf(d.NameSpan, d.Span));
                    ValidateSyncReturn(ReturnTypeOf(d), NameSpanOf(d.NameSpan, d.Span));
                    break;
                case AstNode.DefineAsync { FnName: "main" } da:
                    ValidateParam(da.Params, NameSpanOf(da.NameSpan, da.Span));
                    ValidateAsyncReturn(ReturnTypeOf(da), NameSpanOf(da.NameSpan, da.Span));
                    break;
            }
    }

    private static IEnumerable<AstNode> AllForms(AstNode.Program program)
    {
        return program.TopLevelForms.SelectMany(f =>
            f is AstNode.ModuleDecl m ? new[] { f }.Concat(m.Body) : [f]
        );
    }

    private static SourceSpan NameSpanOf(SourceSpan nameSpan, SourceSpan span)
    {
        return nameSpan == default ? span : nameSpan;
    }

    // main's inferred type is a ZFuncType whose Return is the (possibly Task-wrapped) result.
    private static ZType? ReturnTypeOf(AstNode node)
    {
        return node.ResolvedType is ZType.ZFuncType ft ? ft.Return : null;
    }

    private void ValidateParam(IReadOnlyList<Param> parms, SourceSpan span)
    {
        if (parms.Count > 1)
        {
            diagnostics.Error(
                $"'main' must take at most one parameter (the command-line arguments), but takes {parms.Count}.",
                span
            );
            return;
        }

        if (parms.Count == 0)
            return;

        var p = parms[0];
        if (p.IsVariadic)
        {
            diagnostics.Error(
                "'main' may not take a variadic parameter; declare its argument as a CLR string array, e.g. [args : (Mutable-Vector String)].",
                p.Span
            );
            return;
        }

        var pType = p.ResolvedType ?? p.TypeAnnotation;
        if (
            pType is ZType.ZNamedType { TypeArgs: [var elem] } named
            && typeAliases.IsArrayName(named.Name)
            && elem is ZType.ZPrimitiveType { Kind: PrimitiveKind.String }
        )
            return;

        diagnostics.Error(
            $"'main' parameter must be a CLR string array — (Mutable-Vector String) or (Clr-Array String) — but was {DescribeType(pType)}.",
            p.Span
        );
    }

    private void ValidateSyncReturn(ZType? returnType, SourceSpan span)
    {
        if (returnType is ZType.ZNamedType nt && typeAliases.IsTaskName(nt.Name))
        {
            diagnostics.Error(
                "'main' returns a Task; declare it with (define-async (main ...) ...) to make it an async entry point.",
                span
            );
            return;
        }

        if (!IsIntOrUnit(returnType))
            diagnostics.Error(
                $"'main' must return Int or Unit, but returns {DescribeType(returnType)}.",
                span
            );
    }

    private void ValidateAsyncReturn(ZType? returnType, SourceSpan span)
    {
        // An async main's return is Task<inner> (or non-generic Task, which awaits to Unit).
        if (returnType is not ZType.ZNamedType nt || !typeAliases.IsTaskName(nt.Name))
        {
            diagnostics.Error(
                $"async 'main' must return (Task Int) or (Task Unit), but returns {DescribeType(returnType)}.",
                span
            );
            return;
        }

        var inner = nt.TypeArgs is [var arg] ? arg : ZType.Unit;
        if (!IsIntOrUnit(inner))
            diagnostics.Error(
                $"async 'main' must return (Task Int) or (Task Unit), but returns Task of {DescribeType(inner)}.",
                span
            );
    }

    private static bool IsIntOrUnit(ZType? t)
    {
        return t is ZType.ZPrimitiveType { Kind: PrimitiveKind.Int or PrimitiveKind.Unit };
    }

    private static string DescribeType(ZType? t)
    {
        return t is null ? "an unknown type" : ZType.Format(t);
    }
}
