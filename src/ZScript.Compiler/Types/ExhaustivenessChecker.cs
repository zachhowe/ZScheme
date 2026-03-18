namespace ZScript.Compiler.Types;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Diagnostics;

/// <summary>
/// Checks pattern match completeness using a simplified Maranget algorithm.
/// </summary>
public sealed class ExhaustivenessChecker(DiagnosticBag diagnostics, TypeEnv env)
{
    private readonly TypeEnv _env = env;

    // Known union cases: union name -> list of case names
    private readonly Dictionary<string, List<string>> _unionCases = new();

    public void RegisterUnion(string unionName, IReadOnlyList<string> caseNames)
    {
        _unionCases[unionName] = caseNames.ToList();
    }

    public void Check(AstNode.Match match, string? scrutineeTypeName)
    {
        var patterns = match.Arms.Select(a => a.Pattern).ToList();

        // Check for wildcard/variable pattern (always exhaustive)
        if (patterns.Any(IsIrrefutable))
            return;

        // For union types, check that all cases are covered
        if (scrutineeTypeName is not null && _unionCases.TryGetValue(scrutineeTypeName, out var cases))
        {
            var coveredCases = new HashSet<string>();
            foreach (var pattern in patterns)
            {
                if (pattern is Pattern.Constructor ctor)
                    coveredCases.Add(ctor.Name);
                else if (IsIrrefutable(pattern))
                    return; // wildcard covers everything
            }

            var missingCases = cases.Where(c => !coveredCases.Contains(c)).ToList();
            if (missingCases.Count > 0)
            {
                var missing = string.Join(", ", missingCases);
                diagnostics.Error(
                    $"Non-exhaustive match: missing cases {missing}",
                    match.Span);
            }
            return;
        }

        // For bool, check true/false coverage
        if (scrutineeTypeName is null && patterns.All(p => p is Pattern.Literal { Value: bool }))
        {
            var boolValues = patterns.OfType<Pattern.Literal>()
                .Where(l => l.Value is bool)
                .Select(l => (bool)l.Value)
                .ToHashSet();

            if (!boolValues.Contains(true) || !boolValues.Contains(false))
            {
                diagnostics.Warning("Non-exhaustive match on Bool", match.Span);
            }
            return;
        }

        // For int/string/float literals without a wildcard, we can't guarantee exhaustiveness
        if (patterns.All(p => p is Pattern.Literal) && !patterns.Any(IsIrrefutable))
        {
            diagnostics.Warning(
                "Match on literals without a wildcard/default case may not be exhaustive",
                match.Span);
        }
    }

    private static bool IsIrrefutable(Pattern pattern) => pattern switch
    {
        Pattern.Wildcard => true,
        Pattern.Variable => true,
        _ => false
    };
}
