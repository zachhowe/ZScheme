using System.Text.RegularExpressions;
using Xunit;
using ZScheme.Compiler.Codegen;

namespace ZScheme.Compiler.Tests.Codegen;

public class CSharpProjectGeneratorTests
{
    [Fact]
    public void GenerateCsproj_DefaultOptions_ProducesExeProject()
    {
        var options = new CSharpProjectOptions();
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<OutputType>Exe</OutputType>", csproj);
        Assert.Contains("<Nullable>enable</Nullable>", csproj);
        Assert.Contains("<TargetFramework>net", csproj);
        Assert.DoesNotContain("<LangVersion>", csproj);
        Assert.DoesNotContain("<CopyLocalLockFileAssemblies>", csproj);
    }

    [Fact]
    public void GenerateCsproj_LibraryOutputType()
    {
        var options = new CSharpProjectOptions { OutputType = "Library" };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<OutputType>Library</OutputType>", csproj);
    }

    [Fact]
    public void GenerateCsproj_WithLangVersion()
    {
        var options = new CSharpProjectOptions { LangVersion = "preview" };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<LangVersion>preview</LangVersion>", csproj);
    }

    [Fact]
    public void GenerateCsproj_WithAssemblyReferences()
    {
        var options = new CSharpProjectOptions
        {
            AssemblyReferences = ["/path/to/MyLib.dll", "/other/Util.dll"],
        };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<Reference Include=\"MyLib\">", csproj);
        Assert.Contains("<HintPath>/path/to/MyLib.dll</HintPath>", csproj);
        Assert.Contains("<Reference Include=\"Util\">", csproj);
        Assert.Contains("<HintPath>/other/Util.dll</HintPath>", csproj);
        Assert.Contains("<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>", csproj);
    }

    [Fact]
    public void GenerateCsproj_WithNuGetPackages()
    {
        var options = new CSharpProjectOptions
        {
            NuGetPackages = [("xunit", "2.9.3"), ("Newtonsoft.Json", "13.0.1")],
        };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<PackageReference Include=\"xunit\" Version=\"2.9.3\" />", csproj);
        Assert.Contains(
            "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />",
            csproj
        );
        Assert.Contains("<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>", csproj);
    }

    [Fact]
    public void GenerateCsproj_WithAssemblyRefsAndNuGet_EmitsBothInItemGroup()
    {
        var options = new CSharpProjectOptions
        {
            AssemblyReferences = ["/path/to/MyLib.dll"],
            NuGetPackages = [("xunit", "2.9.3")],
        };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<Reference Include=\"MyLib\">", csproj);
        Assert.Contains("<PackageReference Include=\"xunit\" Version=\"2.9.3\" />", csproj);
    }

    [Fact]
    public void GenerateCsproj_WithProjectReferences()
    {
        var options = new CSharpProjectOptions { ProjectReferences = ["../Main/Main.csproj"] };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<ProjectReference Include=\"../Main/Main.csproj\" />", csproj);
        Assert.Contains("<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>", csproj);
    }

    [Fact]
    public void GenerateCsproj_WithAssemblyRefsNuGetAndProjectRefs_EmitsAllInItemGroup()
    {
        var options = new CSharpProjectOptions
        {
            AssemblyReferences = ["/path/to/MyLib.dll"],
            NuGetPackages = [("xunit", "2.9.3")],
            ProjectReferences = ["../Main/Main.csproj"],
        };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<Reference Include=\"MyLib\">", csproj);
        Assert.Contains("<PackageReference Include=\"xunit\" Version=\"2.9.3\" />", csproj);
        Assert.Contains("<ProjectReference Include=\"../Main/Main.csproj\" />", csproj);
        // All items share a single ItemGroup
        Assert.Single(Regex.Matches(csproj, "<ItemGroup>"));
    }

    [Fact]
    public void GenerateCsproj_DefaultSdk_IsMicrosoftNetSdk()
    {
        var csproj = CSharpProjectGenerator.GenerateCsproj(new CSharpProjectOptions());
        Assert.Contains("<Project Sdk=\"Microsoft.NET.Sdk\">", csproj);
    }

    [Fact]
    public void GenerateCsproj_CustomSdk_OverridesDefault()
    {
        var options = new CSharpProjectOptions { Sdk = "Microsoft.NET.Sdk.Web" };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);
        Assert.Contains("<Project Sdk=\"Microsoft.NET.Sdk.Web\">", csproj);
    }

    [Fact]
    public void GenerateCsproj_FrameworkReferences_EmittedInItemGroup()
    {
        var options = new CSharpProjectOptions
        {
            Sdk = "Microsoft.NET.Sdk.Web",
            FrameworkReferences = ["Microsoft.AspNetCore.App"],
        };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />", csproj);
        Assert.Contains("<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>", csproj);
        Assert.Single(Regex.Matches(csproj, "<ItemGroup>"));
    }

    [Fact]
    public void GenerateCsproj_FrameworkReferencesAndNuGet_ShareItemGroup()
    {
        var options = new CSharpProjectOptions
        {
            Sdk = "Microsoft.NET.Sdk.Web",
            FrameworkReferences = ["Microsoft.AspNetCore.App"],
            NuGetPackages = [("Serilog", "4.0.0")],
        };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />", csproj);
        Assert.Contains("<PackageReference Include=\"Serilog\" Version=\"4.0.0\" />", csproj);
        Assert.Single(Regex.Matches(csproj, "<ItemGroup>"));
    }

    [Fact]
    public void GenerateCsproj_NoFrameworkReferences_DoesNotEmitItem()
    {
        var csproj = CSharpProjectGenerator.GenerateCsproj(new CSharpProjectOptions());
        Assert.DoesNotContain("<FrameworkReference", csproj);
    }

    [Fact]
    public void WriteProjectDirectory_CreatesExpectedFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"zs-test-{Guid.NewGuid():N}");
        try
        {
            var options = new CSharpProjectOptions
            {
                OutputType = "Library",
                NuGetPackages = [("xunit", "2.9.3")],
            };
            var csFiles = new List<(string FileName, string Content)>
            {
                ("Example.cs", "// generated code"),
            };

            CSharpProjectGenerator.WriteProjectDirectory(tempDir, "TestProject", csFiles, options);

            Assert.True(File.Exists(Path.Combine(tempDir, "TestProject.csproj")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Example.cs")));

            var csproj = File.ReadAllText(Path.Combine(tempDir, "TestProject.csproj"));
            Assert.Contains("<OutputType>Library</OutputType>", csproj);
            Assert.Contains("<PackageReference Include=\"xunit\" Version=\"2.9.3\" />", csproj);

            var cs = File.ReadAllText(Path.Combine(tempDir, "Example.cs"));
            Assert.Equal("// generated code", cs);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
