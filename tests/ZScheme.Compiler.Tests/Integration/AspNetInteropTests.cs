using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

/// <summary>
///     End-to-end verification that ZScheme can bind directly to the real ASP.NET Core
///     <c>EndpointRouteBuilderExtensions.MapGet</c> extension method via <c>import-clr</c>,
///     selecting the <c>RequestDelegate</c> overload over the base <c>Delegate</c>
///     (minimal-API) overload and coercing a named handler function into a
///     <c>RequestDelegate</c>. This is the gap documented in packages/aspnet/KNOWN_GAPS.md.
/// </summary>
public class AspNetInteropTests
{
    // Binds straight to the framework extension method (no bridge). The (delegate ...) param
    // annotation names the concrete delegate; a named handler is passed by reference.
    private const string Source = """
        (module aspnettest)
        (import-clr
          Microsoft.AspNetCore.Builder
          Microsoft.AspNetCore.Http
          Microsoft.AspNetCore.Routing
          System.Threading.Tasks

          [task-delay System.Threading.Tasks.Task/Delay : (Int -> Task)]

          [map-get Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions/MapGet
            : (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder
               String
               (delegate Microsoft.AspNetCore.Http.RequestDelegate)
               -> Microsoft.AspNetCore.Builder.IEndpointConventionBuilder)])

        (define (handler [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
          (task-delay 0))

        (define (configure [app : Microsoft.AspNetCore.Routing.IEndpointRouteBuilder])
          : Microsoft.AspNetCore.Builder.IEndpointConventionBuilder
          (map-get app "/" handler))
        """;

    [Fact]
    public void DirectMapGetBinding_CSharp_CoercesHandlerToRequestDelegate()
    {
        EnsureAspNetLoaded();
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                AllowsImplicitModuleName = true,
                SuppressVersionPreamble = true,
                DisablePrelude = true,
                AssemblySearchPaths = [AspNetRuntimePath()],
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );

        var result = compilation.Compile(Source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));

        var cs = ((CompilationResult.CSharpOutputResult)result).CsOutput;

        // The named handler is wrapped in an adapter lambda cast to RequestDelegate so Roslyn
        // selects the RequestDelegate overload rather than treating it as a JSON-body Delegate.
        Assert.Contains("Microsoft.AspNetCore.Http.RequestDelegate)((arg0) =>", cs);
        Assert.Contains("EndpointRouteBuilderExtensions.MapGet(", cs);
    }

    [Fact]
    public void DirectMapGetBinding_Il_SelectsRequestDelegateOverloadAndConstructsDelegate()
    {
        EnsureAspNetLoaded();
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                AssemblySearchPaths = [AspNetRuntimePath()],
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );

        var result = compilation.Compile(Source);
        // If resolution had picked the base System.Delegate overload, IL emission would throw
        // constructing its (nonexistent) public ctor — success already implies a concrete delegate.
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));

        var bytes = ((CompilationResult.IlOutputResult)result).OutputBytes;
        var module = ModuleDefinition.FromBytes(bytes);

        var configure = module
            .GetAllTypes()
            .SelectMany(t => t.Methods)
            .First(m => m.Name == "Configure");

        var instrs = configure.CilMethodBody!.Instructions;

        // A RequestDelegate instance is constructed (newobj on RequestDelegate::.ctor)...
        var newobjConcreteDelegate = instrs.Any(i =>
            i.OpCode.Code == CilCode.Newobj
            && i.Operand is IMethodDescriptor ctor
            && (ctor.DeclaringType?.FullName?.Contains("RequestDelegate") ?? false)
        );
        Assert.True(newobjConcreteDelegate, "Expected a newobj constructing a RequestDelegate.");

        // ...and the MapGet overload taking a RequestDelegate is the one called.
        var callsRequestDelegateOverload = instrs.Any(i =>
            (i.OpCode.Code == CilCode.Call || i.OpCode.Code == CilCode.Callvirt)
            && i.Operand is IMethodDescriptor m
            && m.Name == "MapGet"
            && (
                m.Signature?.ParameterTypes.Any(p =>
                    p.FullName?.Contains("RequestDelegate") ?? false
                )
                ?? false
            )
        );
        Assert.True(
            callsRequestDelegateOverload,
            "Expected the MapGet(IEndpointRouteBuilder, string, RequestDelegate) overload."
        );

        // Negative control: the base System.Delegate overload must NOT be selected.
        var callsDelegateOverload = instrs.Any(i =>
            (i.OpCode.Code == CilCode.Call || i.OpCode.Code == CilCode.Callvirt)
            && i.Operand is IMethodDescriptor m
            && m.Name == "MapGet"
            && (m.Signature?.ParameterTypes.Any(p => p.FullName == "System.Delegate") ?? false)
        );
        Assert.False(callsDelegateOverload, "Must not bind the base System.Delegate overload.");
    }

    // Some ASP.NET Core types (e.g. EndpointRouteBuilderExtensions, IEndpointConventionBuilder)
    // live in the Microsoft.AspNetCore.Builder namespace but ship in assemblies named
    // Routing / Http.Abstractions, so ClrInterop's filename-prefix probe can't locate them.
    // Loading those assemblies into the AppDomain lets FindType's loaded-assembly scan resolve them.
    private static void EnsureAspNetLoaded()
    {
        var runtimePath = AspNetRuntimePath();
        foreach (
            var name in new[]
            {
                "Microsoft.AspNetCore.Routing",
                "Microsoft.AspNetCore.Http.Abstractions",
                "Microsoft.AspNetCore.Http",
            }
        )
        {
            var dll = Path.Combine(runtimePath, name + ".dll");
            if (!File.Exists(dll))
                continue;
            try
            {
                System.Reflection.Assembly.LoadFrom(dll);
            }
            catch
            {
                // best-effort; FindType's directory probe / ClrInterop resolver covers the rest
            }
        }
    }

    private static string AspNetRuntimePath()
    {
        foreach (var baseDir in CandidateAspNetBaseDirs())
        {
            if (!Directory.Exists(baseDir))
                continue;
            var newest = Directory
                .GetDirectories(baseDir)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (newest is not null)
                return newest;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Microsoft.AspNetCore.App shared framework for this test."
        );
    }

    private static IEnumerable<string> CandidateAspNetBaseDirs()
    {
        // Sibling of the running Microsoft.NETCore.App runtime directory.
        var netcore = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var shared = Path.GetFullPath(Path.Combine(netcore, "..", "..", ".."));
        yield return Path.Combine(shared, "shared", "Microsoft.AspNetCore.App");

        var sharedParent = Path.GetFullPath(Path.Combine(netcore, "..", ".."));
        yield return Path.Combine(sharedParent, "Microsoft.AspNetCore.App");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet",
            "shared",
            "Microsoft.AspNetCore.App"
        );

        yield return "/usr/share/dotnet/shared/Microsoft.AspNetCore.App";
        yield return "/usr/lib/dotnet/shared/Microsoft.AspNetCore.App";
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(AspNetInteropTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }
}
