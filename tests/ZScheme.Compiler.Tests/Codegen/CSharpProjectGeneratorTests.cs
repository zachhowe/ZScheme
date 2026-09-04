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
            FrameworkReferences = ["Microsoft.AspNetCore.App"],
            NuGetPackages = [("Serilog", "4.0.0")],
        };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />", csproj);
        Assert.Contains("<PackageReference Include=\"Serilog\" Version=\"4.0.0\" />", csproj);
        Assert.Single(Regex.Matches(csproj, "<ItemGroup>"));
    }

    /// The Web SDK already references Microsoft.AspNetCore.App, and restating it warns
    /// (NETSDK1086) — which a consumer building with warnings-as-errors turns into a failure.
    [Fact]
    public void GenerateCsproj_FrameworkReferenceImpliedBySdk_IsNotRestated()
    {
        var options = new CSharpProjectOptions
        {
            Sdk = "Microsoft.NET.Sdk.Web",
            FrameworkReferences = ["Microsoft.AspNetCore.App"],
        };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.DoesNotContain("<FrameworkReference", csproj);
    }

    [Fact]
    public void GenerateCsproj_AliasedAssemblies_EmitReferencePathAliasTarget()
    {
        var options = new CSharpProjectOptions
        {
            AliasedAssemblies = ["Microsoft.Extensions.Logging.Configuration"],
        };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("AfterTargets=\"ResolveReferences\"", csproj);
        Assert.Contains("'%(FileName)' == 'Microsoft.Extensions.Logging.Configuration'", csproj);
        Assert.Contains("<Aliases>zs_Microsoft_Extensions_Logging_Configuration</Aliases>", csproj);
    }

    [Fact]
    public void GenerateIsolatingDirectoryBuildProps_DisablesInheritedStrictness()
    {
        var props = CSharpProjectGenerator.GenerateIsolatingDirectoryBuildProps();

        Assert.Contains("<TreatWarningsAsErrors>false</TreatWarningsAsErrors>", props);
        Assert.Contains(
            "<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>",
            props
        );
    }

    [Fact]
    public void GenerateCsproj_NoFrameworkReferences_DoesNotEmitItem()
    {
        var csproj = CSharpProjectGenerator.GenerateCsproj(new CSharpProjectOptions());
        Assert.DoesNotContain("<FrameworkReference", csproj);
    }

    /// <summary>
    ///     Naming the sources is what makes a stray .cs in the output directory inert: with
    ///     the default glob a module's old file, or a hand-written source, would compile
    ///     into a duplicate definition.
    /// </summary>
    [Fact]
    public void GenerateCsproj_CompileItems_ReplaceTheDefaultGlob()
    {
        var options = new CSharpProjectOptions
        {
            CompileItems = ["Lib.cs", "mutable/vector.cs", "a&b.cs"],
        };
        var csproj = CSharpProjectGenerator.GenerateCsproj(options);

        Assert.Contains("<EnableDefaultCompileItems>false</EnableDefaultCompileItems>", csproj);
        Assert.Contains("<Compile Include=\"Lib.cs\" />", csproj);
        Assert.Contains("<Compile Include=\"mutable/vector.cs\" />", csproj);
        Assert.Contains("<Compile Include=\"a&amp;b.cs\" />", csproj);
    }

    /// <summary>
    ///     <c>generate-project</c> with no manifest writes a csproj and nothing else; the
    ///     user's own sources are found by the glob.
    /// </summary>
    [Fact]
    public void GenerateCsproj_NoCompileItems_KeepsTheDefaultGlob()
    {
        var csproj = CSharpProjectGenerator.GenerateCsproj(new CSharpProjectOptions());
        Assert.DoesNotContain("EnableDefaultCompileItems", csproj);
        Assert.DoesNotContain("<Compile ", csproj);
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
                ("nested/Other.cs", "// more generated code"),
            };

            CSharpProjectGenerator.WriteProjectDirectory(
                tempDir,
                "TestProject",
                csFiles,
                options,
                pruneStaleGeneratedFiles: false
            );

            Assert.True(File.Exists(Path.Combine(tempDir, "TestProject.csproj")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Example.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "nested", "Other.cs")));

            var csproj = File.ReadAllText(Path.Combine(tempDir, "TestProject.csproj"));
            Assert.Contains("<OutputType>Library</OutputType>", csproj);
            Assert.Contains("<PackageReference Include=\"xunit\" Version=\"2.9.3\" />", csproj);

            // Exactly the files written are the project's sources.
            Assert.Contains("<EnableDefaultCompileItems>false</EnableDefaultCompileItems>", csproj);
            Assert.Contains("<Compile Include=\"Example.cs\" />", csproj);
            Assert.Contains("<Compile Include=\"nested/Other.cs\" />", csproj);

            var cs = File.ReadAllText(Path.Combine(tempDir, "Example.cs"));
            Assert.Equal("// generated code", cs);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    ///     The csproj names its sources, so a stale file would not compile — but it would
    ///     sit in the tree looking like part of the project. Pruning keeps a renamed
    ///     module's old file from lingering.
    /// </summary>
    [Fact]
    public void WriteProjectDirectory_Pruning_RemovesOnlyItsOwnStaleFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"zs-test-{Guid.NewGuid():N}");
        try
        {
            const string marker = "// <auto-generated by ZScheme compiler 0.0.0>";
            Directory.CreateDirectory(Path.Combine(tempDir, "nested"));
            Directory.CreateDirectory(Path.Combine(tempDir, "obj"));
            Directory.CreateDirectory(Path.Combine(tempDir, "bin", "Debug"));

            File.WriteAllText(Path.Combine(tempDir, "stale.cs"), marker + "\npublic class Stale {}");
            File.WriteAllText(
                Path.Combine(tempDir, "nested", "stale.cs"),
                marker + "\npublic class NestedStale {}"
            );
            File.WriteAllText(Path.Combine(tempDir, "HandWritten.cs"), "public class Mine {}");
            File.WriteAllText(Path.Combine(tempDir, "obj", "generated.cs"), marker + "\n");
            File.WriteAllText(Path.Combine(tempDir, "bin", "Debug", "leftover.cs"), marker + "\n");

            CSharpProjectGenerator.WriteProjectDirectory(
                tempDir,
                "TestProject",
                [("fresh.cs", marker + "\npublic class Fresh {}")],
                new CSharpProjectOptions(),
                pruneStaleGeneratedFiles: true
            );

            Assert.False(File.Exists(Path.Combine(tempDir, "stale.cs")));
            Assert.False(File.Exists(Path.Combine(tempDir, "nested", "stale.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "fresh.cs")));

            // Not ours to delete: a hand-written source, and the build output trees the SDK
            // excludes from the compilation anyway.
            Assert.True(File.Exists(Path.Combine(tempDir, "HandWritten.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "obj", "generated.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "bin", "Debug", "leftover.cs")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    ///     A directory the prune cannot read may still hold a stale generated file. Skipping
    ///     it would let the command report success with that file left behind, so the prune
    ///     fails instead. Unix only: Windows has no mode bits to take away, and root reads
    ///     everything regardless.
    /// </summary>
    [Fact]
    public void WriteProjectDirectory_Pruning_FailsOnAnUnreadableDirectory()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"zs-test-{Guid.NewGuid():N}");
        var lockedDir = Path.Combine(tempDir, "locked");
        try
        {
            const string marker = "// <auto-generated by ZScheme compiler 0.0.0>";
            Directory.CreateDirectory(lockedDir);
            File.WriteAllText(Path.Combine(lockedDir, "stale.cs"), marker + "\n");
            File.SetUnixFileMode(lockedDir, UnixFileMode.None);

            Assert.Throws<UnauthorizedAccessException>(() =>
                CSharpProjectGenerator.WriteProjectDirectory(
                    tempDir,
                    "TestProject",
                    [("fresh.cs", marker + "\npublic class Fresh {}")],
                    new CSharpProjectOptions(),
                    pruneStaleGeneratedFiles: true
                )
            );
        }
        finally
        {
            if (Directory.Exists(lockedDir))
                File.SetUnixFileMode(
                    lockedDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    ///     A single-file write owns nothing but its own file: the directory it lands in may
    ///     hold another compile's output, which is not stale and not this writer's to remove.
    /// </summary>
    [Fact]
    public void WriteProjectDirectory_WithoutPruning_LeavesOtherGeneratedFilesAlone()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"zs-test-{Guid.NewGuid():N}");
        try
        {
            const string marker = "// <auto-generated by ZScheme compiler 0.0.0>";
            Directory.CreateDirectory(Path.Combine(tempDir, "lib"));
            File.WriteAllText(Path.Combine(tempDir, "lib.cs"), marker + "\npublic class Lib {}");
            File.WriteAllText(
                Path.Combine(tempDir, "lib", "lib.cs"),
                marker + "\npublic class Lib {}"
            );

            CSharpProjectGenerator.WriteProjectDirectory(
                tempDir,
                "app",
                [("app.cs", marker + "\npublic class App {}")],
                new CSharpProjectOptions(),
                pruneStaleGeneratedFiles: false
            );

            Assert.True(File.Exists(Path.Combine(tempDir, "lib.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "lib", "lib.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "app.cs")));

            // And the leftovers are not part of the project either.
            var csproj = File.ReadAllText(Path.Combine(tempDir, "app.csproj"));
            Assert.Contains("<Compile Include=\"app.cs\" />", csproj);
            Assert.DoesNotContain("lib.cs", csproj);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    ///     The output directory is user-named, so a link inside it can point anywhere. The
    ///     prune must not follow it: the linked tree's generated files are not this
    ///     project's, and a link back into the tree would otherwise loop.
    /// </summary>
    [Fact]
    public void WriteProjectDirectory_Pruning_DoesNotFollowDirectoryLinks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"zs-test-{Guid.NewGuid():N}");
        var otherDir = Path.Combine(Path.GetTempPath(), $"zs-test-{Guid.NewGuid():N}");
        try
        {
            const string marker = "// <auto-generated by ZScheme compiler 0.0.0>";
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(otherDir);
            File.WriteAllText(Path.Combine(otherDir, "theirs.cs"), marker + "\n");

            try
            {
                Directory.CreateSymbolicLink(Path.Combine(tempDir, "link"), otherDir);
                Directory.CreateSymbolicLink(Path.Combine(tempDir, "self"), tempDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows needs Developer Mode or elevation to create a symlink.
                return;
            }

            CSharpProjectGenerator.WriteProjectDirectory(
                tempDir,
                "TestProject",
                [("fresh.cs", marker + "\npublic class Fresh {}")],
                new CSharpProjectOptions(),
                pruneStaleGeneratedFiles: true
            );

            Assert.True(File.Exists(Path.Combine(otherDir, "theirs.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "fresh.cs")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            if (Directory.Exists(otherDir))
                Directory.Delete(otherDir, true);
        }
    }
}
