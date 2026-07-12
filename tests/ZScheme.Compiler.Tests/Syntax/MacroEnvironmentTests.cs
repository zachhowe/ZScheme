using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Syntax;

public class MacroEnvironmentTests
{
    private static MacroDefinition Macro(string name)
    {
        return new MacroDefinition(name, [], [], SourceSpan.None);
    }

    [Fact]
    public void DefineThenLookupRoundTrips()
    {
        var env = new MacroEnvironment();
        var def = Macro("m");
        env.Define("m", def);

        Assert.Same(def, env.Lookup("m"));
    }

    [Fact]
    public void LookupMissReturnsNull()
    {
        var env = new MacroEnvironment();
        Assert.Null(env.Lookup("missing"));
    }

    [Fact]
    public void ChildFallsBackToParent()
    {
        var parent = new MacroEnvironment();
        var def = Macro("m");
        parent.Define("m", def);
        var child = new MacroEnvironment(parent);

        Assert.Same(def, child.Lookup("m"));
    }

    [Fact]
    public void ChildDefinitionShadowsParent()
    {
        var parent = new MacroEnvironment();
        var parentDef = Macro("m");
        parent.Define("m", parentDef);

        var child = new MacroEnvironment(parent);
        var childDef = Macro("m");
        child.Define("m", childDef);

        Assert.Same(childDef, child.Lookup("m"));
        Assert.Same(parentDef, parent.Lookup("m"));
    }

    [Fact]
    public void RedefineReplacesExistingDefinition()
    {
        var env = new MacroEnvironment();
        env.Define("m", Macro("m"));
        var replacement = Macro("m");
        env.Define("m", replacement);

        Assert.Same(replacement, env.Lookup("m"));
        Assert.Single(env.OwnMacros);
    }

    [Fact]
    public void OwnMacrosExcludesParentEntries()
    {
        var parent = new MacroEnvironment();
        parent.Define("inherited", Macro("inherited"));
        var child = new MacroEnvironment(parent);
        child.Define("own", Macro("own"));

        var entry = Assert.Single(child.OwnMacros);
        Assert.Equal("own", entry.Key);
    }

    [Fact]
    public void DefaultEnvironmentIsEmpty()
    {
        Assert.Empty(MacroEnvironment.Default().OwnMacros);
    }
}
