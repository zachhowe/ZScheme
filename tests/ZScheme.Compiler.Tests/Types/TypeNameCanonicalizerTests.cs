using Xunit;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

public class TypeNameCanonicalizerTests
{
    private static TypeNameCanonicalizer Create(
        string[]? namespaces = null,
        TypeAliasRegistry? aliases = null,
        Func<string, bool>? isUserDeclaredType = null
    )
    {
        return new TypeNameCanonicalizer(
            namespaces ?? ["System.Text"],
            aliases,
            null,
            isUserDeclaredType
        );
    }

    [Fact]
    public void ShortName_ResolvesThroughNamespaceHint()
    {
        var c = Create();
        Assert.Equal("System.Text.StringBuilder", c.Canonical("StringBuilder", 0));
    }

    [Fact]
    public void FullyQualifiedName_IsUnchanged()
    {
        var c = Create();
        Assert.Equal("System.Text.StringBuilder", c.Canonical("System.Text.StringBuilder", 0));
    }

    /// <summary>The whole point: both spellings collapse to one, so ZType's structural
    ///     equality makes them the same type.</summary>
    [Fact]
    public void ShortAndFullyQualified_CanonicalizeToTheSameZType()
    {
        var c = Create();
        var shortForm = c.Canonicalize(new ZType.ZNamedType("StringBuilder", []));
        var longForm = c.Canonicalize(new ZType.ZNamedType("System.Text.StringBuilder", []));
        Assert.Equal(shortForm, longForm);
    }

    [Fact]
    public void ShortName_WithoutAMatchingHint_IsUnchanged()
    {
        var c = Create(["System.IO"]);
        Assert.Equal("StringBuilder", c.Canonical("StringBuilder", 0));
    }

    [Fact]
    public void UnresolvableName_IsUnchanged()
    {
        var c = Create();
        Assert.Equal("Totally.Bogus.Type", c.Canonical("Totally.Bogus.Type", 0));
    }

    [Fact]
    public void UserDeclaredType_IsNeverPromotedToASameNamedClrType()
    {
        // A ZScheme record named Point must not become System.Drawing.Point just because
        // that namespace happens to be hinted.
        var c = Create(["System.Drawing"], isUserDeclaredType: name => name == "Point");
        Assert.Equal("Point", c.Canonical("Point", 0));
    }

    [Fact]
    public void TypeVariablesAndTypeParameters_AreUnchanged()
    {
        var c = Create();
        Assert.Equal("^a", c.Canonical("^a", 0));
        Assert.Equal("a", c.Canonical("a", 0));
    }

    [Theory]
    [InlineData("Object")]
    [InlineData("System.Object")]
    [InlineData("Task")]
    [InlineData("ValueTuple")]
    public void NamesMatchedInBothSpellingsElsewhere_AreLeftAlone(string name)
    {
        // Unifier and TypeMapperCore already accept these in either spelling; rewriting them
        // would only churn rendered types.
        var c = Create(["System", "System.Threading.Tasks"]);
        Assert.Equal(name, c.Canonical(name, 0));
    }

    [Fact]
    public void RegisteredAlias_IsLeftAlone()
    {
        var aliases = new TypeAliasRegistry();
        aliases.RegisterBuiltIn(
            new TypeAliasInfo(
                "Seq",
                ["^a"],
                "System.Collections.Generic.IEnumerable",
                "System.Private.CoreLib",
                TypeAliasKind.GenericClrType,
                default
            )
        );
        var c = Create(["System.Collections.Generic"], aliases);
        Assert.Equal("Seq", c.Canonical("Seq", 1));
    }

    /// <summary>A generic type is backed by <c>Foo`n</c>, but ZScheme keeps the arity in
    ///     TypeArgs — so the canonical name must not carry the suffix.</summary>
    [Fact]
    public void GenericName_ResolvesByArityAndDropsTheBacktickSuffix()
    {
        var c = Create(["System.Collections.Generic"]);
        Assert.Equal("System.Collections.Generic.List", c.Canonical("List", 1));
    }

    [Fact]
    public void ClosedGenericName_IsLeftAlone()
    {
        // Its FullName encodes the arguments after the arity suffix; stripping that suffix
        // would drop them, leaving a bare System.Func.
        var c = Create(["System"]);
        Assert.Equal("System.Func<int,int>", c.Canonical("System.Func<int,int>", 0));
    }

    [Fact]
    public void DelegateTypes_AreLeftAlone()
    {
        var c = Create(["System"]);
        var dt = new ZType.ZDelegateType("System.Func<int,int>");
        Assert.Same(dt, c.Canonicalize(dt));
    }

    [Fact]
    public void Canonicalize_RewritesNestedPositions()
    {
        var c = Create();
        var func = new ZType.ZFuncType(
            [new ZType.ZNullableType(new ZType.ZNamedType("StringBuilder", []))],
            new ZType.ZNamedType("Task", [new ZType.ZNamedType("StringBuilder", [])])
        );

        var canonical = Assert.IsType<ZType.ZFuncType>(c.Canonicalize(func));
        var param = Assert.IsType<ZType.ZNullableType>(canonical.Params[0]);
        Assert.Equal(
            "System.Text.StringBuilder",
            Assert.IsType<ZType.ZNamedType>(param.Inner).Name
        );
        var ret = Assert.IsType<ZType.ZNamedType>(canonical.Return);
        Assert.Equal("Task", ret.Name);
        Assert.Equal(
            "System.Text.StringBuilder",
            Assert.IsType<ZType.ZNamedType>(ret.TypeArgs[0]).Name
        );
    }

    [Fact]
    public void Canonicalize_ReturnsTheSameInstanceWhenNothingChanges()
    {
        var c = Create();
        var t = new ZType.ZNamedType("System.Text.StringBuilder", []);
        Assert.Same(t, c.Canonicalize(t));
    }

    [Fact]
    public void CanonicalizeNames_RewritesOnlyTheEntriesThatChange()
    {
        var c = Create();
        var names = new[] { "StringBuilder", "Totally.Bogus.Type" };
        Assert.Equal(
            new[] { "System.Text.StringBuilder", "Totally.Bogus.Type" },
            c.CanonicalizeNames(names)
        );
    }

    [Fact]
    public void CanonicalizeNames_ReturnsTheSameInstanceWhenNothingChanges()
    {
        var c = Create();
        var names = new[] { "System.Text.StringBuilder" };
        Assert.Same(names, c.CanonicalizeNames(names));
    }

    // ---- CanonicalImportTypeName: the type half of an import-clr member path ----

    [Fact]
    public void CanonicalImportTypeName_ResolvesAShortNameThroughANamespaceHint()
    {
        var c = Create();
        Assert.Equal("System.Text.StringBuilder", c.CanonicalImportTypeName("StringBuilder"));
    }

    /// <summary>Why the helper exists: a member path names its type without an arity, and
    ///     <c>ICollection</c> is backed only by <c>ICollection`1</c>.</summary>
    [Fact]
    public void CanonicalImportTypeName_ResolvesAGenericShortNameThatHasNoArity()
    {
        var c = Create(["System.Collections.Generic"]);

        Assert.Equal("ICollection", c.Canonical("ICollection", 0));
        Assert.Equal(
            "System.Collections.Generic.ICollection",
            c.CanonicalImportTypeName("ICollection")
        );
    }

    [Fact]
    public void CanonicalImportTypeName_LeavesAQualifiedGenericNameAlone()
    {
        var c = Create(["System.Collections.Generic"]);
        // Notably without the `2 suffix a probe at arity 2 would otherwise introduce.
        Assert.Equal(
            "System.Collections.Generic.Dictionary",
            c.CanonicalImportTypeName("System.Collections.Generic.Dictionary")
        );
    }

    [Fact]
    public void CanonicalImportTypeName_LeavesAnUnresolvableNameAlone()
    {
        var c = Create();
        Assert.Equal("Widget", c.CanonicalImportTypeName("Widget"));
    }

    [Fact]
    public void CanonicalImportTypeName_LeavesAUserDeclaredTypeAlone()
    {
        var c = Create(["System.Drawing"], isUserDeclaredType: n => n == "Point");
        Assert.Equal("Point", c.CanonicalImportTypeName("Point"));
    }

    [Fact]
    public void CanonicalImportTypeName_ResolvesANestedType()
    {
        var c = Create(["System"]);
        Assert.Equal(
            "System.Environment+SpecialFolder",
            c.CanonicalImportTypeName("Environment+SpecialFolder")
        );
    }
}
