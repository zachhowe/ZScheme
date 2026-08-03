using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ast;

/// <summary>
///     Rejects <c>letrec</c> groups whose bindings could read each other before they are
///     assigned.
///     <para>
///         A letrec binds every name in every value, but ZScheme evaluates strictly and in
///         source order, so the scope rule alone is not enough to keep the group sound. The
///         rule enforced here is:
///     </para>
///     <para>
///         If a binding's value is not syntactically a <c>lambda</c>, then every letrec name
///         reachable from that value — including through lambdas nested inside it, and
///         transitively through the values of any group member it mentions — must be bound
///         <em>earlier</em> in the group.
///     </para>
///     <para>
///         A <c>lambda</c> value is unconstrained: building a closure reads nothing, and by the
///         time it can be called the whole group is initialized. That is what lets the
///         mutually-recursive shape — the reason the form exists — through.
///     </para>
///     <para>
///         The transitive step also covers aliasing: <c>[g f]</c> merely copies a closure, but
///         whatever <c>g</c> is later applied to can read everything <c>f</c> reads, so
///         <c>f</c>'s references are folded in. That is conservative — a group can be rejected
///         even though no call actually happens during initialization — but the alternative is
///         an escape analysis, and being conservative here is what keeps the two backends in
///         agreement: C# rejects a use-before-initialization local outright (CS0165) while IL
///         would silently observe a default value.
///     </para>
/// </summary>
internal static class LetrecInitializationChecker
{
    /// <param name="formName">The surface form the group came from, so a nested <c>define</c> run
    ///     is not blamed on a <c>letrec</c> the user never wrote.</param>
    public static void Check(
        IReadOnlyList<AstNode.LetrecBinding> bindings,
        DiagnosticBag diagnostics,
        string formName = "letrec"
    )
    {
        // references[i] = indices of group members whose name occurs free in bindings[i].Value.
        var references = new List<HashSet<int>>(bindings.Count);
        for (var i = 0; i < bindings.Count; i++)
        {
            var refs = new HashSet<int>();
            for (var j = 0; j < bindings.Count; j++)
                if (UnusedBindingAnalyzer.IsUsed(bindings[i].Value, bindings[j].Name))
                    refs.Add(j);
            references.Add(refs);
        }

        for (var i = 0; i < bindings.Count; i++)
        {
            if (bindings[i].Value is AstNode.Lambda)
                continue;

            var offender = FirstUninitialized(i, references);
            if (offender is null)
                continue;

            // One diagnostic per binding: a group where several names are out of order would
            // otherwise report the same mistake once per reference.
            diagnostics.Error(
                $"'{formName}' binding '{bindings[i].Name}' uses "
                    + $"'{bindings[offender.Value].Name}' before it is initialized",
                bindings[i].Value.Span
            );
        }
    }

    /// <summary>The lowest-indexed group member reachable from binding <paramref name="start" />
    ///     that is not bound before it, or null when the binding is well-ordered.</summary>
    private static int? FirstUninitialized(int start, List<HashSet<int>> references)
    {
        var seen = new HashSet<int>();
        var queue = new Queue<int>(references[start]);
        int? offender = null;

        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (!seen.Add(next))
                continue;
            if (next >= start && (offender is null || next < offender))
                offender = next;
            foreach (var further in references[next])
                queue.Enqueue(further);
        }

        return offender;
    }
}
