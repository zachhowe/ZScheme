namespace ZScheme.Compiler.Types;

public sealed record OverloadCandidate(string QualifiedName, ZType Type);

public sealed class OverloadSet
{
    public List<OverloadCandidate> Candidates { get; } = new();

    public IEnumerable<string> QualifiedNames => Candidates.Select(c => c.QualifiedName);

    public void Add(OverloadCandidate candidate)
    {
        if (!Candidates.Any(c => c.QualifiedName == candidate.QualifiedName))
            Candidates.Add(candidate);
    }

    public void AddOrReplace(OverloadCandidate candidate)
    {
        var idx = Candidates.FindIndex(c => c.QualifiedName == candidate.QualifiedName);
        if (idx >= 0)
            Candidates[idx] = candidate;
        else
            Candidates.Add(candidate);
    }
}
