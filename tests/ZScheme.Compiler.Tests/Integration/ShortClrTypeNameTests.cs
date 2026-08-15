using System.Reflection;
using Xunit;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

// A CLR type must mean the same thing whether it is written short (`StringBuilder`) or fully
// qualified (`System.Text.StringBuilder`). A short name resolves only through a namespace
// declared by an `(import-clr Ns ...)` form — including the file's own, which used to be
// invisible to that file's type inference because the hints were not collected until IR
// lowering, a stage later. Both spellings canonicalize to Type.FullName, so they become the
// same ZType and unify. See TypeNameCanonicalizer.
public class ShortClrTypeNameTests
{
    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(ShortClrTypeNameTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static CompilationResult CompileWith(string source, OutputMode mode)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = mode,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
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

    private static Assembly CompileIl(string source)
    {
        var result = CompileWith(source, OutputMode.Il);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return Assembly.Load(((CompilationResult.IlOutputResult)result).OutputBytes);
    }

    private static MethodInfo FindMethod(Assembly asm, string methodName)
    {
        return asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
    }

    private static T Invoke<T>(Assembly asm, string methodName)
    {
        try
        {
            return (T)FindMethod(asm, methodName).Invoke(null, null)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    private static string InvokeString(Assembly asm, string methodName)
    {
        return Invoke<string>(asm, methodName);
    }

    /// <summary>
    ///     The parameter is annotated short and the return type fully qualified (and the reverse
    ///     in the second function), so the two spellings must unify in both directions. `new` is
    ///     covered too — it names its type as a bare string rather than a ZType.
    /// </summary>
    private const string MixedSpellings = """
        (module mixed)

        (import-clr
          System.Text
          [sb-append System.Text.StringBuilder.Append
            :instance : (System.Text.StringBuilder String -> System.Text.StringBuilder)]
          [sb-str System.Text.StringBuilder.ToString
            :instance : (StringBuilder -> String)])

        (define (grow [b : StringBuilder]) : System.Text.StringBuilder
          (sb-append b "x"))

        (define (grow2 [b : System.Text.StringBuilder]) : StringBuilder
          (grow b))

        (define (compute) : String
          (sb-str (grow2 (grow (new StringBuilder)))))
        """;

    [Fact]
    public void MixedSpellings_Compile_Il()
    {
        Assert.Equal("xx", InvokeString(CompileIl(MixedSpellings), "Compute"));
    }

    [Fact]
    public void MixedSpellings_Compile_CSharp()
    {
        var cs = CompileCSharp(MixedSpellings);
        // Every occurrence is emitted fully qualified — the short spellings resolved rather than
        // degrading to `object`, which is what the `Name.Contains('.')` guard in TypeMapperCore
        // used to produce for a short CLR name.
        Assert.DoesNotContain("(object b)", cs);
        Assert.Contains("System.Text.StringBuilder Grow(System.Text.StringBuilder b)", cs);
        Assert.Contains("new System.Text.StringBuilder()", cs);
    }

    [Fact]
    public void ShortNameWithoutANamespaceHint_IsStillAnError()
    {
        // Resolution is via import-clr hints only; there is deliberately no blanket assembly
        // scan, which would make two same-named types in different namespaces ambiguous.
        var result = CompileWith(
            """
            (module nohint)

            (import-clr
              [sb-str System.Text.StringBuilder.ToString
                :instance : (System.Text.StringBuilder -> String)])

            (define (compute) : String
              (sb-str (new StringBuilder)))
            """,
            OutputMode.Il
        );
        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("CLR type not found: 'StringBuilder'")
        );
    }

    /// <summary>
    ///     A <c>with-handlers</c> clause names its exception type as a bare string that never
    ///     becomes a ZType, so it needs the same treatment as an annotation — and the IL backend
    ///     resolves that name by reflection with no namespace hints of its own.
    /// </summary>
    [Fact]
    public void WithHandlers_AcceptsAShortExceptionTypeName()
    {
        var source = """
            (module handlers)

            (import-clr System)

            (define (compute) : String
              (with-handlers ([InvalidOperationException _] "caught")
                (raise (new InvalidOperationException "boom"))))
            """;
        Assert.Equal("caught", InvokeString(CompileIl(source), "Compute"));
    }

    /// <summary>
    ///     A ZScheme type keeps its short name even when a hinted namespace happens to contain a
    ///     CLR type with the same simple name — otherwise `Point` here would silently become
    ///     System.Drawing.Point.
    /// </summary>
    [Fact]
    public void ZSchemeTypeIsNotShadowedByASameNamedClrTypeInAHintedNamespace()
    {
        var source = """
            (module shadow)

            (import-clr
              System.Text
              [sb-str System.Text.StringBuilder.ToString
                :instance : (StringBuilder -> String)])

            (define-record StringBuilder [tag : String])

            (define (compute) : String
              (StringBuilder/tag (StringBuilder "mine")))
            """;
        Assert.Equal("mine", InvokeString(CompileIl(source), "Compute"));
    }

    /// <summary>
    ///     The flip side of the test above: mixing the two really is an error, but at arity 0 the
    ///     unifier's CLR-subtype fallback used to complete the short name through the hint, find
    ///     <c>System.Text.StringBuilder</c> and accept it — so this compiled clean and emitted a
    ///     ZScheme record where the CLR type was required. It is now rejected, and the message says
    ///     which side the ZScheme declaration owns.
    /// </summary>
    [Fact]
    public void MixingAZSchemeTypeWithTheClrTypeItShadows_IsRejectedAndExplained()
    {
        var result = CompileWith(
            """
            (module shadowmix)

            (import-clr System.Text)

            (define-record StringBuilder [tag : String])

            (define (widen [b : StringBuilder]) : System.Text.StringBuilder b)
            """,
            OutputMode.Il
        );

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("'StringBuilder' is a ZScheme type declared in this file")
        );
    }

    /// <summary>
    ///     Why the note names the declaring module rather than just saying "a ZScheme type":
    ///     <c>List</c> is stdlib's union, in scope through the prelude, so the user never wrote the
    ///     declaration that owns the name and has nothing to search for without it.
    /// </summary>
    [Fact]
    public void MixingStdlibListWithTheClrList_NamesTheModuleThatDeclaresIt()
    {
        var result = CompileWith(
            """
            (module shadowlist)

            (import-clr System.Collections.Generic)

            (define (f [xs : (List Int)]) : (System.Collections.Generic.List Int) xs)
            """,
            OutputMode.Il
        );

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("'List' is a ZScheme type declared in stdlib/list")
        );
    }

    /// <summary>
    ///     A short name in <c>new</c> position resolves through the namespace hints even when a
    ///     <c>define-type-alias</c> stands between the expression and its annotation.
    ///     <para>
    ///         IR lowering used to recover the full name by reading the name type inference
    ///         resolved and checking it ends with the written one. That only holds when
    ///         inference lands on the CLR type's own name: here it resolves to the alias
    ///         <c>Mutable-Hash</c>, the suffix test fails, and the bare <c>Dictionary</c>
    ///         reached the emitter as <c>CLR type 'Dictionary' not found</c>. Lowering now
    ///         canonicalizes the written name at its generic arity instead — the arity matters,
    ///         since a generic is backed by <c>Foo`n</c>.
    ///     </para>
    /// </summary>
    [Fact]
    public void ShortGenericNameInNew_ResolvesBehindATypeAlias()
    {
        var source = """
            (module aliasnew)

            (import-clr
              System.Collections.Generic
              [d-count System.Collections.Generic.Dictionary.Count
                :instance-property : ((Mutable-Hash ^k ^v) -> Int)]
              [int-str System.Convert/ToString : (Int -> String)])

            (define-type-alias (Mutable-Hash ^k ^v) System.Collections.Generic.Dictionary)

            (define (make) : (Mutable-Hash ^k ^v)
              :where (^k notnull)
              (new (Dictionary ^k ^v)))

            (define (compute) : String
              (int-str (d-count (make))))
            """;
        Assert.Equal("0", InvokeString(CompileIl(source), "Compute"));
    }

    // ---- Short type names in an import-clr member path ----
    //
    // The type half of `[alias Type/Member]` and `[alias Type.Member]` is a plain string that
    // ClrInterop reflects on and both emitters consume verbatim, so it used to need the full
    // spelling regardless of the namespaces the form declares. TypeInferer and IrLowering now
    // canonicalize it at the split, through the same hints an annotation resolves by.

    private const string ShortStaticMemberPath = """
        (module shortstatic)

        (import-clr System [int-str Convert/ToString : (Int -> String)])

        (define (compute) : String
          (int-str 42))
        """;

    [Fact]
    public void ShortStaticMemberPath_Compiles_Il()
    {
        Assert.Equal("42", InvokeString(CompileIl(ShortStaticMemberPath), "Compute"));
    }

    [Fact]
    public void ShortStaticMemberPath_EmitsTheFullNameInCSharp()
    {
        // Generated C# carries no `using`s, so a short name reaching the emitter would compile
        // to `Convert.ToString(...)` and fail in Roslyn.
        Assert.Contains("System.Convert.ToString(", CompileCSharp(ShortStaticMemberPath));
    }

    [Fact]
    public void ShortInstanceMemberPath_Compiles_Il()
    {
        var source = """
            (module shortinstance)

            (import-clr
              System.Text
              [sb-append StringBuilder.Append
                :instance : (StringBuilder String -> StringBuilder)]
              [sb-str StringBuilder.ToString
                :instance : (StringBuilder -> String)])

            (define (compute) : String
              (sb-str (sb-append (new StringBuilder) "hi")))
            """;
        Assert.Equal("hi", InvokeString(CompileIl(source), "Compute"));
    }

    /// <summary>
    ///     A generic member path names its type without an arity — <c>ICollection.Add</c> means
    ///     <c>ICollection`1</c> — so the type half is probed across arities rather than resolved
    ///     at 0, which would miss both the short and the qualified spelling.
    /// </summary>
    [Fact]
    public void ShortGenericInstanceMemberPath_Compiles_Il()
    {
        // Annotated through stdlib's Mutable-Hash alias, as the type-alias test above does:
        // `new` infers the alias, so naming Dictionary on both sides would fail to unify for
        // reasons unrelated to the member path.
        var source = """
            (module shortgeneric)

            (import-clr
              System.Collections.Generic
              System
              [d-count Dictionary.Count
                :instance-property : ((Mutable-Hash String Int) -> Int)]
              [int-str Convert/ToString : (Int -> String)])

            (define (compute) : String
              (int-str (d-count (new (Dictionary String Int)))))
            """;
        Assert.Equal("0", InvokeString(CompileIl(source), "Compute"));
    }

    /// <summary>
    ///     An unannotated <c>^a</c> import derives its signature by reflecting on the type half
    ///     itself, and reports a hard "CLR type not found" when it cannot — so that path needs
    ///     the same canonicalization. Declaring the import is the whole test: the error fires
    ///     during type inference, before any call site. (An annotated <c>^a</c> import takes the
    ///     annotation branch instead and never reaches here.)
    /// </summary>
    [Fact]
    public void ShortStaticMemberPathWithTypeParamsAndNoAnnotation_Compiles_Il()
    {
        var source = """
            (module shortgenericstatic)

            (import-clr System.Linq [xs-at Enumerable/ElementAt ^a])

            (define (compute) : Int 0)
            """;
        var result = CompileWith(source, OutputMode.Il);

        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
    }

    /// <summary>
    ///     Resolving a short type half restores the annotation cross-check, which used to be
    ///     skipped silently for any member path the reflection lookup could not find.
    /// </summary>
    [Fact]
    public void ShortMemberPathWithAWrongAnnotation_IsReported()
    {
        // The arity is right, so the member resolves and the return type is actually compared;
        // a wrong arity would just fail to resolve, which is silent by design.
        var result = CompileWith(
            """
            (module wrongannotation)

            (import-clr
              System.Text
              [sb-str StringBuilder.ToString
                :instance : (StringBuilder -> Int)])
            """,
            OutputMode.Il
        );

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("does not match the CLR member")
        );
    }

    /// <summary>A member path onto a class this compilation declares still resolves from the
    ///     AST's own symbol table, never reflection — canonicalization happens past that
    ///     branch precisely so a stale same-named type cannot be picked up.</summary>
    [Fact]
    public void MemberPathOntoALocalClass_IsUnaffected()
    {
        var source = """
            (module localclass)

            (import-clr
              System
              [int-str Convert/ToString : (Int -> String)]
              [counter-value Counter/Value :instance : (Counter -> Int)])

            (define-class Counter
              (define (Value) : Int 7))

            (define (compute) : String
              (int-str (counter-value (new Counter))))
            """;
        Assert.Equal("7", InvokeString(CompileIl(source), "Compute"));
    }

    // ---- A short type name as the (Task T) argument of an async function ----
    //
    // IR lowering unwraps a `define-async`'s annotation to the inner result type, and that unwrap
    // read the annotation as written where every other position reads a canonicalized type. The
    // short name then reached the IL backend, whose lookup is fully-qualified-only, and mapped to
    // `object`: the state machine's AsyncTaskMethodBuilder<> was closed over `object` while the
    // body returned the value unboxed. ilverify accepts the result in both severities, so these
    // tests must actually run the method and read the emitted signature.

    private const string AsyncShortTaskArgument = """
        (module asynctaskarg)

        (import-clr
          [new-guid System.Guid/NewGuid]
          [guid-cmp System.Guid.CompareTo :instance : (System.Guid System.Guid -> Int)]
          System
          System.Threading.Tasks)

        (define-async (bare-result) : (Task Guid) (new-guid))

        (define-async (compute) : (Task Int)
          (let ([g (await (bare-result))])
            (+ 7 (guid-cmp g g))))
        """;

    /// <summary>A value-typed result is the fatal half: the unboxed <c>Guid</c> returned into a
    ///     builder closed over <c>object</c> is an <c>InvalidProgramException</c> at JIT time.</summary>
    [Fact]
    public async Task ShortNameAsAnAsyncTaskArgument_Runs_Il()
    {
        var task = Invoke<Task<int>>(CompileIl(AsyncShortTaskArgument), "Compute");
        Assert.Equal(7, await task);
    }

    /// <summary>The quieter half: a reference-typed result survives unboxed, so only the emitted
    ///     signature shows the erasure — and it is what a C# consumer or an interface conformance
    ///     check sees.</summary>
    [Fact]
    public void ShortNameAsAnAsyncTaskArgument_EmitsTaskOfThatType_Il()
    {
        var method = FindMethod(CompileIl(AsyncShortTaskArgument), "BareResult");
        Assert.Equal(typeof(Task<Guid>), method.ReturnType);
    }
}
