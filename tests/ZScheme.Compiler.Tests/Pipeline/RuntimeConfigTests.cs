using System.Text.Json;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Pipeline;

public class RuntimeConfigTests
{
    private static string ExpectedTfm =>
        $"net{Environment.Version.Major}.{Environment.Version.Minor}";

    private static string ExpectedVersion =>
        $"{Environment.Version.Major}.{Environment.Version.Minor}.0";

    [Fact]
    public void NoFrameworksEmitsSingleNetCoreAppFramework()
    {
        using var doc = JsonDocument.Parse(RuntimeConfig.Generate([]));
        var options = doc.RootElement.GetProperty("runtimeOptions");

        Assert.Equal(ExpectedTfm, options.GetProperty("tfm").GetString());
        var framework = options.GetProperty("framework");
        Assert.Equal("Microsoft.NETCore.App", framework.GetProperty("name").GetString());
        Assert.Equal(ExpectedVersion, framework.GetProperty("version").GetString());
        Assert.False(options.TryGetProperty("frameworks", out _));
    }

    [Fact]
    public void DeclaredFrameworksEmitFrameworksArray()
    {
        using var doc = JsonDocument.Parse(
            RuntimeConfig.Generate(["Microsoft.AspNetCore.App"])
        );
        var options = doc.RootElement.GetProperty("runtimeOptions");

        var frameworks = options.GetProperty("frameworks");
        var entry = Assert.Single(frameworks.EnumerateArray());
        Assert.Equal("Microsoft.AspNetCore.App", entry.GetProperty("name").GetString());
        Assert.Equal(ExpectedVersion, entry.GetProperty("version").GetString());
        Assert.False(options.TryGetProperty("framework", out _));
    }

    [Fact]
    public void MultipleFrameworksAreEmittedInOrder()
    {
        using var doc = JsonDocument.Parse(
            RuntimeConfig.Generate(["Microsoft.NETCore.App", "Microsoft.AspNetCore.App"])
        );
        var names = doc.RootElement.GetProperty("runtimeOptions")
            .GetProperty("frameworks")
            .EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(["Microsoft.NETCore.App", "Microsoft.AspNetCore.App"], names);
    }

    [Fact]
    public void DuplicateFrameworksAreDeduplicated()
    {
        using var doc = JsonDocument.Parse(
            RuntimeConfig.Generate(["Microsoft.AspNetCore.App", "Microsoft.AspNetCore.App"])
        );
        var frameworks = doc.RootElement.GetProperty("runtimeOptions").GetProperty("frameworks");
        Assert.Single(frameworks.EnumerateArray());
    }
}
