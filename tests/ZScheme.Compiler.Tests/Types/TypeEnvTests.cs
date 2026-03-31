using Xunit;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

public class TypeEnvTests
{
    [Fact]
    public void DefineAndLookup_RoundTrip()
    {
        var env = new TypeEnv();
        env.Define("x", ZType.Int);
        Assert.Equal(ZType.Int, env.Lookup("x"));
    }

    [Fact]
    public void Lookup_InChild_FindsParentBinding()
    {
        var parent = new TypeEnv();
        parent.Define("x", ZType.Bool);
        var child = parent.CreateChild();
        Assert.Equal(ZType.Bool, child.Lookup("x"));
    }

    [Fact]
    public void ChildBinding_ShadowsParent()
    {
        var parent = new TypeEnv();
        parent.Define("x", ZType.Int);
        var child = parent.CreateChild();
        child.Define("x", ZType.String);
        Assert.Equal(ZType.String, child.Lookup("x"));
        Assert.Equal(ZType.Int, parent.Lookup("x"));
    }

    [Fact]
    public void Lookup_ReturnsNull_ForUndefinedName()
    {
        var env = new TypeEnv();
        Assert.Null(env.Lookup("nonexistent"));
    }

    [Fact]
    public void Contains_ReturnsTrueAndFalse()
    {
        var env = new TypeEnv();
        env.Define("x", ZType.Int);
        Assert.True(env.Contains("x"));
        Assert.False(env.Contains("y"));
    }

    [Fact]
    public void CreateRoot_HasBuiltinOperators()
    {
        var env = TypeEnv.CreateRoot();

        // Arithmetic
        Assert.NotNull(env.Lookup("+"));
        Assert.NotNull(env.Lookup("-"));
        Assert.NotNull(env.Lookup("*"));

        // Comparison
        Assert.NotNull(env.Lookup("="));
        Assert.NotNull(env.Lookup("<"));
        Assert.NotNull(env.Lookup(">="));

        // Boolean
        Assert.NotNull(env.Lookup("and"));
        Assert.NotNull(env.Lookup("or"));
        Assert.NotNull(env.Lookup("not"));

        // Built-in constructors (now provided by prelude modules, no longer in root env)
    }

    [Fact]
    public void DefineBuiltinCtor_AndLookupBuiltinCtor_RoundTrip()
    {
        var env = new TypeEnv();
        var info = new BuiltinCtorInfo("ZsResult", "Ok");
        env.DefineBuiltinCtor("Ok", info);

        var result = env.LookupBuiltinCtor("Ok");
        Assert.NotNull(result);
        Assert.Equal("ZsResult", result!.RuntimeType);
        Assert.Equal("Ok", result.CaseName);
    }

    [Fact]
    public void LookupBuiltinCtor_ReturnsNull_ForUndefined()
    {
        var env = new TypeEnv();
        Assert.Null(env.LookupBuiltinCtor("Nope"));
    }
}
