using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Types;

/// <summary>
///     Checks pattern match completeness using a simplified Maranget algorithm.
/// </summary>
public sealed class ExhaustivenessChecker(DiagnosticBag diagnostics)
{
    // Known union cases: union name -> cases (name + field count, the arity tooling
    // needs to synthesize a fix pattern like (Some _)).
    private readonly Dictionary<string, List<(string Name, int Arity)>> _unionCases = new();

    public void RegisterUnion(string unionName, IReadOnlyList<(string Name, int Arity)> cases)
    {
        _unionCases[unionName] = cases.ToList();
    }

    public void Check(AstNode.Match match, string? scrutineeTypeName)
    {
        var patterns = match.Arms.Select(a => a.Pattern).ToList();

        // Check for wildcard/variable pattern (always exhaustive)
        if (patterns.Any(IsIrrefutable))
            return;

        // For union types, check that all cases are covered
        if (
            scrutineeTypeName is not null
            && _unionCases.TryGetValue(scrutineeTypeName, out var cases)
        )
        {
            var coveredCases = new HashSet<string>();
            foreach (var pattern in patterns)
                if (pattern is Pattern.Constructor ctor)
                    coveredCases.Add(ctor.Name);
                else if (IsIrrefutable(pattern))
                    return; // wildcard covers everything

            var missingCases = cases.Where(c => !coveredCases.Contains(c.Name)).ToList();
            if (missingCases.Count > 0)
            {
                var missing = string.Join(", ", missingCases.Select(c => c.Name));
                // An Error (not a Warning): union-case coverage is sound, unlike the
                // Bool/literal heuristics below, and the ecosystem is verified clean.
                diagnostics.Error(
                    $"Non-exhaustive match: missing cases {missing}",
                    match.Span,
                    DiagnosticCodes.NonExhaustiveMatch,
                    [.. missingCases.Select(c => $"{c.Name}/{c.Arity}")],
                    [.. match.Arms.Select(a => new DiagnosticRelatedInfo(a.Span, "existing arm here"))]
                );
            }

            return;
        }

        // For bool, check true/false coverage
        if (
            scrutineeTypeName is null or "Bool"
            && patterns.All(p => p is Pattern.Literal { Value: bool })
            && patterns.Count > 0
        )
        {
            var boolValues = patterns
                .OfType<Pattern.Literal>()
                .Where(l => l.Value is bool)
                .Select(l => (bool)l.Value)
                .ToHashSet();

            if (!boolValues.Contains(true) || !boolValues.Contains(false))
                diagnostics.Warning("Non-exhaustive match on Bool", match.Span);
            return;
        }

        // For int/string/float literals without a wildcard, we can't guarantee exhaustiveness
        if (patterns.All(p => p is Pattern.Literal) && !patterns.Any(IsIrrefutable))
            diagnostics.Warning(
                "Match on literals without a wildcard/default case may not be exhaustive",
                match.Span
            );
    }

    private static bool IsIrrefutable(Pattern pattern)
    {
        return pattern switch
        {
            Pattern.Wildcard => true,
            Pattern.Variable => true,
            Pattern.Tuple t => t.Elements.All(IsIrrefutable),
            _ => false,
        };
    }
}
