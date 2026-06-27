using System.Reflection;
using Xunit;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

// Regression tests for binding an `import-clr :instance-property` to a property the receiver
// interface only *inherits* from a base interface. Type.GetProperty on an interface does not
// surface inherited members, so the IL backend used to emit `ldc.i4.0` after already pushing
// the receiver — a stack imbalance that crashed code generation (it surfaced binding
// IServiceCollection.Count, inherited from ICollection<ServiceDescriptor>). The C# backend was
// always fine (it emits `receiver.Count` and lets the C# compiler resolve inheritance), so both
// backends are exercised and must agree.
public class InheritedInterfacePropertyTests
{
    // NOTE on the closed-generic case (a non-generic interface inheriting a property from a
    // *generic* base interface, e.g. IServiceCollection.Count from ICollection<ServiceDescriptor>
    // — the original crash, which exercises ImportClosedGenericMethod): it can't be reproduced
    // from BCL types here. It needs a value typed as such an interface, and the only BCL way to
    // get one is a concrete→generic-interface upcast (e.g. HashSet<int> → ISet<int>), which
    // ZScheme inference independently rejects. That branch is covered end-to-end by the
    // di-abstractions package test (real IServiceCollection.Count) and, at the validation layer,
    // by ClrImportValidationTests.InheritedInterfaceProperty_NoDiagnostic (IList<int>.Count,
    // resolved through the closed generic ICollection<int>).

    // System.Collections.IList.Count is inherited from the NON-GENERIC base System.Collections
    // .ICollection; exercises the plain-import branch of the accessor import. ArrayList.Add
    // returns the insert index (Int); we discard it.
    private const string NonGenericBaseSource =
        "(module test)\n"
        + "(import-clr System.Collections\n"
        + "  [al-add System.Collections.ArrayList.Add\n"
        + "    :instance : (System.Collections.ArrayList System.Object -> Int)]\n"
        + "  [ilist-count System.Collections.IList.Count\n"
        + "    :instance-property : (System.Collections.IList -> Int)])\n"
        + "(define (make-list) : System.Collections.IList\n"
        + "  (let ([al : System.Collections.ArrayList (new System.Collections.ArrayList)])\n"
        + "    (al-add al 10)\n"
        + "    (al-add al 20)\n"
        + "    al))\n"
        + "(define (Compute) : Int (ilist-count (make-list)))\n";

    [Fact]
    public void InheritedNonGenericInterfaceProperty_Il() =>
        Assert.Equal(2, CompileIlAndRunInt(NonGenericBaseSource));

    [Fact]
    public void InheritedNonGenericInterfaceProperty_CSharp() =>
        Assert.Equal(2, CompileCSharpAndRunInt(NonGenericBaseSource));

    // ─── Dual-backend compile/run harness (mirrors Integration/UseFormTests) ───

    private static CompilationResult CompileWith(string source, OutputMode mode)
    {
        // No stdlib: these sources bind only BCL types via import-clr. Loading stdlib would
        // alias `System.Collections.Generic.List` to its `Mutable-TreeList` and break the test.
        var compilation = new Compilation(
            new CompilerOptions { OutputMode = mode, AllowsImplicitModuleName = true }
        );
        return compilation.Compile(source);
    }

    private static string CompileCSharp(string source)
    {
        var result = CompileWith(source, OutputMode.CSharp);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return ((CompilationResult.CSharpOutputResult)result).CsOutput;
    }

    private static int CompileIlAndRunInt(string source, string methodName = "Compute")
    {
        var result = CompileWith(source, OutputMode.Il);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        var ilResult = (CompilationResult.IlOutputResult)result;
        return InvokeInt(Assembly.Load(ilResult.OutputBytes), methodName);
    }

    private static int CompileCSharpAndRunInt(string source, string methodName = "Compute") =>
        InvokeInt(RoslynCompile(CompileCSharp(source)), methodName);

    private static Assembly RoslynCompile(string cs)
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        Assert.False(string.IsNullOrEmpty(tpa), "TRUSTED_PLATFORM_ASSEMBLIES unavailable");
        var references = tpa!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(p =>
                (Microsoft.CodeAnalysis.MetadataReference)
                    Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p)
            )
            .ToList();

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(cs);
        var options = new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
            Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release,
            allowUnsafe: true,
            nullableContextOptions: Microsoft.CodeAnalysis.NullableContextOptions.Enable
        );
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "ZSchemeInheritedPropExec",
            [tree],
            references,
            options
        );

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(
            emit.Success,
            "Roslyn emit failed:\n"
                + string.Join(
                    "\n",
                    emit.Diagnostics.Where(d =>
                        d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                    )
                )
        );
        return Assembly.Load(ms.ToArray());
    }

    private static int InvokeInt(Assembly asm, string methodName)
    {
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        try
        {
            return (int)method.Invoke(null, null)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }
}
