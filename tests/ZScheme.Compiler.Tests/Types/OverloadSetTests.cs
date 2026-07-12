using Xunit;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

public class OverloadSetTests
{
    private static OverloadCandidate Candidate(string name, ZType? type = null)
    {
        return new OverloadCandidate(name, type ?? ZType.Int);
    }

    [Fact]
    public void AddAppendsCandidate()
    {
        var set = new OverloadSet();
        set.Add(Candidate("m/f"));

        Assert.Single(set.Candidates);
        Assert.Equal(["m/f"], set.QualifiedNames);
    }

    [Fact]
    public void AddWithDuplicateQualifiedNameIsNoOp()
    {
        var set = new OverloadSet();
        set.Add(Candidate("m/f", ZType.Int));
        set.Add(Candidate("m/f", ZType.String));

        var candidate = Assert.Single(set.Candidates);
        Assert.Equal(ZType.Int, candidate.Type);
    }

    [Fact]
    public void AddOrReplaceAppendsWhenNameIsNew()
    {
        var set = new OverloadSet();
        set.Add(Candidate("m/f"));
        set.AddOrReplace(Candidate("m/g"));

        Assert.Equal(["m/f", "m/g"], set.QualifiedNames);
    }

    [Fact]
    public void AddOrReplaceReplacesInPlacePreservingOrder()
    {
        var set = new OverloadSet();
        set.Add(Candidate("m/f", ZType.Int));
        set.Add(Candidate("m/g", ZType.Int));
        set.AddOrReplace(Candidate("m/f", ZType.String));

        Assert.Equal(["m/f", "m/g"], set.QualifiedNames);
        Assert.Equal(ZType.String, set.Candidates[0].Type);
        Assert.Equal(ZType.Int, set.Candidates[1].Type);
    }

    [Fact]
    public void InsertionOrderPreservedAcrossMixedAdds()
    {
        var set = new OverloadSet();
        set.Add(Candidate("m/a"));
        set.AddOrReplace(Candidate("m/b"));
        set.Add(Candidate("m/c"));
        set.Add(Candidate("m/b", ZType.String)); // duplicate: no-op
        set.AddOrReplace(Candidate("m/a", ZType.String)); // replace in place

        Assert.Equal(["m/a", "m/b", "m/c"], set.QualifiedNames);
        Assert.Equal(ZType.String, set.Candidates[0].Type);
        Assert.Equal(ZType.Int, set.Candidates[1].Type);
    }
}
