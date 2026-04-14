using Xunit;
using ZScheme.Compiler.Codegen;

namespace ZScheme.Compiler.Tests.Codegen;

public class CSharpSolutionGeneratorTests
{
    [Fact]
    public void GenerateSlnx_EmptyProjects_EmitsSolutionShell()
    {
        var slnx = CSharpSolutionGenerator.GenerateSlnx([]);

        Assert.StartsWith("<Solution>", slnx);
        Assert.EndsWith("</Solution>", slnx);
        Assert.DoesNotContain("<Folder", slnx);
    }

    [Fact]
    public void GenerateSlnx_MainAndTestProject_GroupsByFolder()
    {
        var projects = new List<SolutionProjectEntry>
        {
            new("src", "Foo/Foo.csproj"),
            new("tests", "Foo.Tests/Foo.Tests.csproj")
        };
        var slnx = CSharpSolutionGenerator.GenerateSlnx(projects);

        Assert.Contains("<Folder Name=\"/src/\">", slnx);
        Assert.Contains("<Project Path=\"Foo/Foo.csproj\" />", slnx);
        Assert.Contains("<Folder Name=\"/tests/\">", slnx);
        Assert.Contains("<Project Path=\"Foo.Tests/Foo.Tests.csproj\" />", slnx);
    }

    [Fact]
    public void GenerateSlnx_MultipleProjectsInSameFolder_SharesFolderElement()
    {
        var projects = new List<SolutionProjectEntry>
        {
            new("src", "A/A.csproj"),
            new("src", "B/B.csproj")
        };
        var slnx = CSharpSolutionGenerator.GenerateSlnx(projects);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(slnx, "<Folder Name=\"/src/\">"));
        Assert.Contains("<Project Path=\"A/A.csproj\" />", slnx);
        Assert.Contains("<Project Path=\"B/B.csproj\" />", slnx);
    }

    [Fact]
    public void WriteSlnx_CreatesFileWithContent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"zs-slnx-{Guid.NewGuid():N}");
        var slnxPath = Path.Combine(tempDir, "Test.slnx");
        try
        {
            CSharpSolutionGenerator.WriteSlnx(slnxPath, [new("src", "X/X.csproj")]);
            Assert.True(File.Exists(slnxPath));
            var content = File.ReadAllText(slnxPath);
            Assert.Contains("<Project Path=\"X/X.csproj\" />", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
