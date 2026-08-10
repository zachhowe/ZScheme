using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Codegen;

// Reflection reports every corelib type as living in System.Private.CoreLib, so the IL backend
// used to scope its imports there. The runtime binds that spelling happily, which is why nothing
// noticed until a C# project referenced an emitted package assembly and Roslyn reported CS0012 on
// every public signature naming a corelib type — no reference pack contains that assembly.
//
// The fix resolves the owning reference assembly per type rather than redirecting the whole
// implementation assembly to one facade. That distinction is the point of ScopesThreadToItsOwnFacade
// below: a blanket redirect to the module's corlib produces metadata C# accepts but breaks
// System.Threading.Thread at load time, because System.Runtime does not forward it.
public class CorLibFacadeTests
{
    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(CorLibFacadeTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    /// <summary>Every corelib type the source below reaches, paired with the assembly its
    ///     <c>TypeRef</c> is scoped to.</summary>
    private static Dictionary<string, string> ScopesOf(string source)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var bytes = ((CompilationResult.IlOutputResult)result).OutputBytes;
        using var stream = new MemoryStream(bytes);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();

        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in metadata.TypeReferences)
        {
            var reference = metadata.GetTypeReference(handle);
            if (reference.ResolutionScope.Kind is not HandleKind.AssemblyReference)
                continue;

            var scope = metadata.GetAssemblyReference(
                (AssemblyReferenceHandle)reference.ResolutionScope
            );
            var space = metadata.GetString(reference.Namespace);
            var name = metadata.GetString(reference.Name);
            scopes[space.Length == 0 ? name : space + "." + name] = metadata.GetString(scope.Name);
        }

        return scopes;
    }

    [Fact]
    public void NothingIsScopedToTheImplementationCorLib()
    {
        var scopes = ScopesOf(
            @"(module facades)
(import-clr
  [sleep System.Threading.Thread/Sleep]
  [writeln System.Console/WriteLine])

(define (go) : Unit
  (let ([_sb (new System.Text.StringBuilder ""hi"")])
    (sleep 0)
    (writeln ""done"")))"
        );

        Assert.DoesNotContain(CorLibFacadeMap.ImplementationAssembly, scopes.Values);
    }

    [Theory]
    // System.Runtime forwards this one, so a blanket redirect would have got it right too.
    [InlineData("System.Text.StringBuilder", "System.Runtime")]
    // …and this one it does not forward: Thread lives behind its own same-named facade, and
    // pointing it at System.Runtime makes the emitted assembly fail to load.
    [InlineData("System.Threading.Thread", "System.Threading.Thread")]
    [InlineData("System.Console", "System.Console")]
    public void ScopesEachCorLibTypeToItsOwnReferenceAssembly(string type, string expected)
    {
        var scopes = ScopesOf(
            @"(module facades)
(import-clr
  [sleep System.Threading.Thread/Sleep]
  [writeln System.Console/WriteLine])

(define (go) : Unit
  (let ([_sb (new System.Text.StringBuilder ""hi"")])
    (sleep 0)
    (writeln ""done"")))"
        );

        Assert.Equal(expected, scopes.GetValueOrDefault(type));
    }

    [Fact]
    public void FacadeMapResolvesTypesSplitAcrossReferenceAssemblies()
    {
        // Dictionary belongs to System.Collections rather than the corlib, which is the case that
        // makes a per-type map necessary in the first place.
        Assert.Equal(
            "System.Collections",
            CorLibFacadeMap.FacadeFor("System.Collections.Generic.Dictionary`2")?.Name
        );
        Assert.Equal("System.Runtime", CorLibFacadeMap.FacadeFor("System.String")?.Name);
        Assert.Equal(
            "System.Threading.Thread",
            CorLibFacadeMap.FacadeFor("System.Threading.Thread")?.Name
        );
    }

    [Theory]
    [InlineData(typeof(int[]), "System.Int32")]
    [InlineData(typeof(Dictionary<string, int>), "System.Collections.Generic.Dictionary`2")]
    [InlineData(
        typeof(Dictionary<string, int>.KeyCollection),
        "System.Collections.Generic.Dictionary`2"
    )]
    public void ScopeOwnerUnwrapsToTheTypeThatCarriesTheScope(Type type, string expected)
    {
        Assert.Equal(expected, CorLibFacadeMap.ScopeOwner(type));
    }
}
