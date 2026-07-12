using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Types;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>The declared parameter names of a callee, with a trailing rest-parameter
///     marked variadic.</summary>
internal sealed record ResolvedParams(IReadOnlyList<string> Names, bool IsVariadic);

/// <summary>
///     Recovers a callee's declared parameter names — <c>ZFuncType</c> carries none.
///     Same-file callees read names straight off the AST's <c>Define</c> nodes;
///     imported callees use the <see cref="IndexedDefinition.ParamNames" /> facet of
///     the workspace index. Returns null whenever the answer could be wrong: unknown
///     callee, no arity match, or several arity-matching candidates that disagree on
///     names. Shared by signature help and call-site inlay hints so both agree.
/// </summary>
internal static class ParamNameResolver
{
    /// <summary>Names for the callee of a call with <paramref name="argCount" />
    ///     arguments (arity-matched, honoring variadics).</summary>
    public static ResolvedParams? ForCallSite(
        AstNode.Name fn,
        int argCount,
        DocumentState state,
        WorkspaceIndex? index
    )
    {
        // Overloaded imported name: only trust names when exactly one candidate fits
        // the call's arity.
        if (fn.OverloadCandidates is { Candidates.Count: > 0 } set)
        {
            var fitting = set
                .Candidates.Select(c => (Candidate: c, Func: UnwrapFunc(c.Type)))
                .Where(x => x.Func is not null && FuncArityMatches(x.Func!, argCount))
                .ToList();
            if (fitting.Count != 1)
                return null;
            return ForDeclaredArity(
                fitting[0].Candidate.QualifiedName,
                fn.Value,
                fitting[0].Func!.Params.Count,
                state,
                index
            );
        }

        return Pick(
            SameFileDefines(state, fn.Value)
                .Concat(IndexedDefines(index, fn.ResolvedQualifiedName, fn.Value))
                .Where(p => ParamArityMatches(p, argCount))
        );
    }

    /// <summary>Names for a specific overload with <paramref name="paramCount" />
    ///     declared parameters (signature help renders one signature per overload).</summary>
    public static ResolvedParams? ForDeclaredArity(
        string? qualifiedKey,
        string bareName,
        int paramCount,
        DocumentState state,
        WorkspaceIndex? index
    )
    {
        return Pick(
            SameFileDefines(state, bareName)
                .Concat(IndexedDefines(index, qualifiedKey, bareName))
                .Where(p => p.Names.Count == paramCount)
        );
    }

    /// <summary>The single distinct name list among the candidates, or null when they
    ///     disagree (a wrong name hint is worse than none).</summary>
    private static ResolvedParams? Pick(IEnumerable<ResolvedParams> candidates)
    {
        ResolvedParams? picked = null;
        foreach (var candidate in candidates)
        {
            if (picked is null)
            {
                picked = candidate;
                continue;
            }

            if (
                picked.IsVariadic != candidate.IsVariadic
                || !picked.Names.SequenceEqual(candidate.Names, StringComparer.Ordinal)
            )
                return null;
        }

        return picked;
    }

    private static IEnumerable<ResolvedParams> SameFileDefines(DocumentState state, string name)
    {
        if (state.Ast is null)
            yield break;
        foreach (var form in TopLevelForms(state.Ast))
            switch (form)
            {
                case AstNode.Define d when d.FnName == name:
                    yield return FromParams(d.Params);
                    break;
                case AstNode.DefineAsync d when d.FnName == name:
                    yield return FromParams(d.Params);
                    break;
            }
    }

    private static IEnumerable<ResolvedParams> IndexedDefines(
        WorkspaceIndex? index,
        string? qualifiedKey,
        string bareName
    )
    {
        if (index is null)
            return [];
        return index
            .ResolveDefinition(qualifiedKey, bareName)
            .Where(d => d.Kind == SymbolKind.Function && d.ParamNames is not null)
            .Select(d => new ResolvedParams(d.ParamNames!, d.IsVariadic));
    }

    private static IEnumerable<AstNode> TopLevelForms(AstNode.Program program)
    {
        foreach (var form in program.TopLevelForms)
            if (form is AstNode.ModuleDecl mod)
                foreach (var bodyForm in mod.Body)
                    yield return bodyForm;
            else
                yield return form;
    }

    private static ResolvedParams FromParams(IReadOnlyList<Param> params_)
    {
        return new ResolvedParams(
            [.. params_.Select(p => p.Name)],
            params_.Any(p => p.IsVariadic)
        );
    }

    private static bool ParamArityMatches(ResolvedParams p, int argCount)
    {
        return p.IsVariadic ? argCount >= p.Names.Count - 1 : p.Names.Count == argCount;
    }

    private static bool FuncArityMatches(ZType.ZFuncType ft, int argCount)
    {
        return ft.IsVariadic ? argCount >= ft.Params.Count - 1 : ft.Params.Count == argCount;
    }

    private static ZType.ZFuncType? UnwrapFunc(ZType? type)
    {
        return type switch
        {
            ZType.ZFuncType f => f,
            ZType.ZForAllType { Body: ZType.ZFuncType f } => f,
            _ => null,
        };
    }
}
