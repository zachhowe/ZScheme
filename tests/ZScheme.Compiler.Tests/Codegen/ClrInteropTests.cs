using System.Reflection;
using System.Reflection.Emit;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Codegen;

public class ClrInteropTests
{
    [Fact]
    public void Resolve_SystemMathSqrt_ReturnsMethodInfo()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var method = interop.Resolve("System.Math/Sqrt", SourceSpan.None);

        Assert.NotNull(method);
        Assert.Equal("Sqrt", method!.Name);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void FindType_CSharpStyleGenericDelegate_ResolvesClosedType()
    {
        // (delegate System.Func<int,int>) stores its CLR name in C# generic syntax,
        // which Type.GetType cannot parse. FindType must convert it to the reflection
        // form and resolve the closed Func<int,int>. Regression: when this returned
        // null the unifier silently skipped its arity check on delegate parameters,
        // letting an arity-mismatched lambda through and producing invalid IL (a
        // DelegateCtor verification failure). Found by the fuzzer.
        var interop = new ClrInterop(new DiagnosticBag());

        var type = interop.FindType("System.Func<int,int>");

        Assert.NotNull(type);
        Assert.Equal(typeof(Func<int, int>), type);
    }

    [Fact]
    public void Resolve_InvalidFormat_ReportsError()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var method = interop.Resolve("NoSlashHere", SourceSpan.None);

        Assert.Null(method);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Expected Type/Method"));
    }

    [Fact]
    public void Resolve_NonexistentType_ReportsError()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var method = interop.Resolve("System.FakeType/Method", SourceSpan.None);

        Assert.Null(method);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("CLR type not found"));
    }

    [Fact]
    public void Resolve_NonexistentMethod_ReportsError()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var method = interop.Resolve("System.Math/NonexistentMethod", SourceSpan.None);

        Assert.Null(method);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("CLR method not found"));
    }

    [Fact]
    public void MapClrTypeToZType_MapsPrimitivesCorrectly()
    {
        Assert.Equal(ZType.Int, new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(int)));
        Assert.Equal(
            ZType.Long,
            new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(long))
        );
        Assert.Equal(
            ZType.Float,
            new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(float))
        );
        Assert.Equal(
            ZType.Double,
            new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(double))
        );
        Assert.Equal(
            ZType.Byte,
            new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(byte))
        );
        Assert.Equal(
            ZType.Char,
            new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(char))
        );
        Assert.Equal(
            ZType.Bool,
            new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(bool))
        );
        Assert.Equal(
            ZType.String,
            new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(string))
        );
        Assert.Equal(
            ZType.Unit,
            new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(void))
        );
    }

    [Fact]
    public void MethodInfoToZFuncType_ReturnsCorrectType()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);
        var method = interop.Resolve("System.Math/Sqrt", SourceSpan.None);
        Assert.NotNull(method);

        var funcType = interop.MethodInfoToZFuncType(method!);
        var ft = Assert.IsType<ZType.ZFuncType>(funcType);
        Assert.Single(ft.Params);
        Assert.Equal(ZType.Double, ft.Params[0]);
        Assert.Equal(ZType.Double, ft.Return);
    }

    [Fact]
    public void MapClrTypeToZType_MapsMutableVectorCorrectly()
    {
        var reg = new TypeAliasRegistry();
        reg.TryAdd(
            new TypeAliasInfo(
                "Mutable-Vector",
                ["^a"],
                "",
                null,
                TypeAliasKind.SzArray,
                SourceSpan.None
            ),
            out _
        );
        var result = new ClrInterop(new DiagnosticBag(), null, reg).MapClrTypeToZType(
            typeof(byte[])
        );
        var named = Assert.IsType<ZType.ZNamedType>(result);
        Assert.Equal("Mutable-Vector", named.Name);
        Assert.Single(named.TypeArgs);
        Assert.Equal(ZType.Byte, named.TypeArgs[0]);
    }

    [Fact]
    public void MapClrTypeToZType_MapsStringMutableVectorCorrectly()
    {
        var reg = new TypeAliasRegistry();
        reg.TryAdd(
            new TypeAliasInfo(
                "Mutable-Vector",
                ["^a"],
                "",
                null,
                TypeAliasKind.SzArray,
                SourceSpan.None
            ),
            out _
        );
        var result = new ClrInterop(new DiagnosticBag(), null, reg).MapClrTypeToZType(
            typeof(string[])
        );
        var named = Assert.IsType<ZType.ZNamedType>(result);
        Assert.Equal("Mutable-Vector", named.Name);
        Assert.Single(named.TypeArgs);
        Assert.Equal(ZType.String, named.TypeArgs[0]);
    }

    [Fact]
    public void MapClrTypeToZType_WithCustomArrayAlias_UsesCustomAlias()
    {
        var reg = new TypeAliasRegistry();
        reg.TryAdd(
            new TypeAliasInfo(
                "Custom-Array",
                ["^a"],
                "",
                null,
                TypeAliasKind.SzArray,
                SourceSpan.None
            ),
            out _
        );
        var result = new ClrInterop(new DiagnosticBag(), null, reg).MapClrTypeToZType(
            typeof(int[])
        );
        var named = Assert.IsType<ZType.ZNamedType>(result);
        Assert.Equal("Custom-Array", named.Name);
        Assert.Single(named.TypeArgs);
        Assert.Equal(ZType.Int, named.TypeArgs[0]);
    }

    [Fact]
    public void MapClrTypeToZType_WithCustomArrayAlias_StringArrayAlsoUsesCustomAlias()
    {
        var reg = new TypeAliasRegistry();
        reg.TryAdd(
            new TypeAliasInfo(
                "Custom-Array",
                ["^a"],
                "",
                null,
                TypeAliasKind.SzArray,
                SourceSpan.None
            ),
            out _
        );
        var result = new ClrInterop(new DiagnosticBag(), null, reg).MapClrTypeToZType(
            typeof(string[])
        );
        var named = Assert.IsType<ZType.ZNamedType>(result);
        Assert.Equal("Custom-Array", named.Name);
        Assert.Single(named.TypeArgs);
        Assert.Equal(ZType.String, named.TypeArgs[0]);
    }

    [Fact]
    public void MethodInfoToZFuncTypeWithOutParams_DetectsOutParams()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);
        var method = typeof(int).GetMethod(
            "TryParse",
            [typeof(string), typeof(int).MakeByRefType()]
        )!;
        var (funcType, outParams) = interop.MethodInfoToZFuncTypeWithOutParams(method);

        // Out param should be removed from visible params, only string remains
        var ft = Assert.IsType<ZType.ZFuncType>(funcType);
        Assert.Single(ft.Params);
        Assert.Equal(ZType.String, ft.Params[0]);

        // Return type should be ValueTuple<bool, int>
        var retType = Assert.IsType<ZType.ZNamedType>(ft.Return);
        Assert.Equal("ValueTuple", retType.Name);
        Assert.Equal(2, retType.TypeArgs.Count);
        Assert.Equal(ZType.Bool, retType.TypeArgs[0]);
        Assert.Equal(ZType.Int, retType.TypeArgs[1]);

        // One out param at original index 1
        Assert.Single(outParams);
        Assert.Equal(1, outParams[0].OriginalIndex);
        Assert.Equal(ZType.Int, outParams[0].ElementType);
    }

    [Fact]
    public void DetectOutParams_StaticMethod_FindsOutParam()
    {
        // Regression: a static CLR import like `System.Int32/TryParse` annotated with
        // its visible-out-stripped signature must still detect the out parameter so
        // the IL/C# emitters know to allocate a local and pack the ValueTuple result.
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var outParams = interop.DetectOutParams(
            "System.Int32/TryParse",
            SourceSpan.None,
            BindingFlags.Public | BindingFlags.Static
        );

        Assert.Single(outParams);
        Assert.Equal(1, outParams[0].OriginalIndex);
        Assert.Equal(ZType.Int, outParams[0].ElementType);
    }

    [Fact]
    public void DetectOutParams_DefaultFlags_OnlyInstance()
    {
        // The default flags target instance methods — TryParse is static, so the
        // default lookup should not find it. This exercises the flags-driven scoping
        // and protects callers that intentionally restrict to instance lookups.
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var outParams = interop.DetectOutParams("System.Int32/TryParse", SourceSpan.None);

        Assert.Empty(outParams);
    }

    [Fact]
    public void MethodInfoToZFuncTypeWithOutParams_NoOutParams_SameAsRegular()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);
        var method = typeof(Math).GetMethod("Sqrt")!;
        var (funcType, outParams) = interop.MethodInfoToZFuncTypeWithOutParams(method);
        var regular = interop.MethodInfoToZFuncType(method);

        Assert.Empty(outParams);
        Assert.Equal(regular.ToString(), funcType.ToString());
    }

    [Fact]
    public void ResolveOverloadCallSite_PicksMatchingSignature()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        // System.Math.Max has two overloads: (double, double) -> double and (int, int) -> int
        // Create a ZFuncType that matches (int, int) -> int
        var funcType = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int);
        var method = interop.ResolveOverloadCallSite(
            "System.Math",
            "Max",
            funcType,
            SourceSpan.None
        );

        Assert.NotNull(method);
        Assert.Equal("Max", method!.Name);
        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(int), parameters[0].ParameterType);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void ResolveOverloadCallSite_ReturnsNullForNonMethods()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        // String.Empty is a field, not a method
        var funcType = new ZType.ZFuncType([], ZType.String);
        var method = interop.ResolveOverloadCallSite(
            "System.String",
            "Empty",
            funcType,
            SourceSpan.None
        );

        Assert.Null(method);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void ResolveOverloadCallSite_ReturnsNullForNonExistentType()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var funcType = new ZType.ZFuncType([ZType.Int], ZType.Int);
        var method = interop.ResolveOverloadCallSite(
            "System.NonExistentType",
            "Method",
            funcType,
            SourceSpan.None
        );

        Assert.Null(method);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void ResolveOverloadCallSite_ReturnsNullForNonExistentMethod()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var funcType = new ZType.ZFuncType([ZType.Int], ZType.Int);
        var method = interop.ResolveOverloadCallSite(
            "System.Math",
            "NonExistentMethod",
            funcType,
            SourceSpan.None
        );

        Assert.Null(method);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void ResolveOverloadCallSite_FallsBackToHeuristicsWhenNoSignatureMatch()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        // Create a function type that won't match any Math method signatures
        // The fallback should use PickBestOverload heuristics
        var funcType = new ZType.ZFuncType([ZType.String, ZType.Bool], ZType.String);
        // Math doesn't have a (string, bool) -> string method, so it should fall back
        var method = interop.ResolveOverloadCallSite(
            "System.Math",
            "Max",
            funcType,
            SourceSpan.None
        );

        // Should return null since no overload matches and heuristics don't help here
        Assert.Null(method);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void FuncTypeMatchesDelegate_RejectsBaseDelegateTypes()
    {
        var interop = new ClrInterop(new DiagnosticBag());
        var funcType = new ZType.ZFuncType([ZType.Int], ZType.String);

        Assert.False(interop.FuncTypeMatchesDelegate(funcType, typeof(Delegate), SourceSpan.None));
        Assert.False(
            interop.FuncTypeMatchesDelegate(funcType, typeof(MulticastDelegate), SourceSpan.None)
        );
    }

    [Fact]
    public void FuncTypeMatchesDelegate_AcceptsConcreteMatchingDelegate()
    {
        var interop = new ClrInterop(new DiagnosticBag());
        // OverloadDelegateFixture.MyHandler is `delegate string (int)`
        var funcType = new ZType.ZFuncType([ZType.Int], ZType.String);

        Assert.True(
            interop.FuncTypeMatchesDelegate(
                funcType,
                typeof(OverloadDelegateFixture.MyHandler),
                SourceSpan.None
            )
        );
    }

    [Fact]
    public void FuncTypeMatchesDelegate_RejectsArityMismatch()
    {
        var interop = new ClrInterop(new DiagnosticBag());
        var funcType = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.String);

        Assert.False(
            interop.FuncTypeMatchesDelegate(
                funcType,
                typeof(OverloadDelegateFixture.MyHandler),
                SourceSpan.None
            )
        );
    }

    [Fact]
    public void FuncTypeMatchesDelegate_RejectsElementMismatch()
    {
        var interop = new ClrInterop(new DiagnosticBag());
        // MyHandler takes int, not string
        var funcType = new ZType.ZFuncType([ZType.String], ZType.String);

        Assert.False(
            interop.FuncTypeMatchesDelegate(
                funcType,
                typeof(OverloadDelegateFixture.MyHandler),
                SourceSpan.None
            )
        );
    }

    [Fact]
    public void ResolveOverloadCallSite_PrefersConcreteDelegateOverBaseDelegate()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        // OverloadDelegateFixture.Run has two same-arity overloads:
        //   (string, System.Delegate) -> int      [base-delegate / minimal-API analogue]
        //   (string, MyHandler)       -> string   [concrete delegate]
        // The handler arg is a function (Int -> String). Without the delegate-specificity
        // tie-break this would be ambiguous (different return types); with it, the concrete
        // MyHandler overload wins.
        var argFunc = new ZType.ZFuncType([ZType.Int], ZType.String);
        var callType = new ZType.ZFuncType([ZType.String, argFunc], new ZType.ZTypeVar(9999));

        var method = interop.ResolveOverloadCallSite(
            typeof(OverloadDelegateFixture).FullName!,
            "Run",
            callType,
            SourceSpan.None
        );

        Assert.NotNull(method);
        Assert.False(diag.HasErrors);
        var ps = method!.GetParameters();
        Assert.Equal(typeof(OverloadDelegateFixture.MyHandler), ps[1].ParameterType);
    }

    [Fact]
    public void ResolveOverloadCallSite_MatchesWhenTrailingParamIsOptional()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        // WithOptional(int, int = 0) called with a single int argument.
        var funcType = new ZType.ZFuncType([ZType.Int], ZType.Int);
        var method = interop.ResolveOverloadCallSite(
            typeof(OverloadResolutionFixture).FullName!,
            "WithOptional",
            funcType,
            SourceSpan.None
        );

        Assert.NotNull(method);
        Assert.Equal("WithOptional", method!.Name);
        Assert.Equal(2, method.GetParameters().Length);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void ResolveOverloadCallSite_MatchesAllArgumentsWhenNoneOptional()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var funcType = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int);
        var method = interop.ResolveOverloadCallSite(
            typeof(OverloadResolutionFixture).FullName!,
            "WithOptional",
            funcType,
            SourceSpan.None
        );

        Assert.NotNull(method);
        Assert.Equal("WithOptional", method!.Name);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void ResolveOverloadCallSite_NullableParamAcceptsUnderlyingType()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        // WithNullable(int?) called with a plain int argument.
        var funcType = new ZType.ZFuncType([ZType.Int], ZType.Int);
        var method = interop.ResolveOverloadCallSite(
            typeof(OverloadResolutionFixture).FullName!,
            "WithNullable",
            funcType,
            SourceSpan.None
        );

        Assert.NotNull(method);
        Assert.Equal("WithNullable", method!.Name);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void ResolveOverloadCallSite_ReturnsNullWhenArgCountExceedsParams()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        // WithNullable takes one parameter; three args cannot bind.
        var funcType = new ZType.ZFuncType([ZType.Int, ZType.Int, ZType.Int], ZType.Int);
        var method = interop.ResolveOverloadCallSite(
            typeof(OverloadResolutionFixture).FullName!,
            "WithNullable",
            funcType,
            SourceSpan.None
        );

        Assert.Null(method);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void ResolveInstanceOverloadCallSite_PicksByArgumentType()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var intCall = new ZType.ZFuncType([ZType.Int], ZType.Int);
        var mInt = interop.ResolveInstanceOverloadCallSite(
            typeof(InstanceOverloadFixture),
            "M",
            intCall,
            SourceSpan.None
        );
        Assert.NotNull(mInt);
        Assert.Equal(typeof(int), mInt!.GetParameters()[0].ParameterType);

        var strCall = new ZType.ZFuncType([ZType.String], ZType.String);
        var mStr = interop.ResolveInstanceOverloadCallSite(
            typeof(InstanceOverloadFixture),
            "M",
            strCall,
            SourceSpan.None
        );
        Assert.NotNull(mStr);
        Assert.Equal(typeof(string), mStr!.GetParameters()[0].ParameterType);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void ResolveInstanceOverloadCallSite_HonorsOptionalTrailingParam()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var call = new ZType.ZFuncType([ZType.Int], ZType.Int);
        var method = interop.ResolveInstanceOverloadCallSite(
            typeof(InstanceOverloadFixture),
            "N",
            call,
            SourceSpan.None
        );

        Assert.NotNull(method);
        Assert.Equal("N", method!.Name);
        Assert.Equal(2, method.GetParameters().Length);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void ResolveInstanceOverloadCallSite_ReturnsNullForUnknownMethod()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);

        var call = new ZType.ZFuncType([ZType.Int], ZType.Int);
        var method = interop.ResolveInstanceOverloadCallSite(
            typeof(InstanceOverloadFixture),
            "DoesNotExist",
            call,
            SourceSpan.None
        );

        Assert.Null(method);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void FindTypeForMember_DisambiguatesSameNamedTypesByMember()
    {
        // Two loaded assemblies can declare a type with the SAME full name — e.g.
        // Microsoft.Extensions.Logging.LoggingBuilderExtensions ships in both
        // Microsoft.Extensions.Logging.dll (ClearProviders) and
        // Microsoft.Extensions.Logging.Configuration.dll (AddConfiguration). A plain
        // FindType returns whichever loaded first, which may lack the called member;
        // FindTypeForMember must instead pick the one that actually declares it.
        // Reproduced deterministically with two dynamic assemblies. Regression: without
        // this, a cross-package ClrCall into such a collision failed to emit ("method not
        // found") when the wrong same-named type happened to be loaded first.
        EmitCollisionType("ClrInteropCollisionA", "Alpha");
        EmitCollisionType("ClrInteropCollisionB", "Beta");

        var interop = new ClrInterop(new DiagnosticBag());

        var alphaType = interop.FindTypeForMember("ClrInteropCollision.Widget", "Alpha");
        var betaType = interop.FindTypeForMember("ClrInteropCollision.Widget", "Beta");

        Assert.NotNull(alphaType);
        Assert.NotNull(betaType);
        Assert.NotNull(alphaType!.GetMethod("Alpha", BindingFlags.Public | BindingFlags.Static));
        Assert.NotNull(betaType!.GetMethod("Beta", BindingFlags.Public | BindingFlags.Static));
        // Each resolved to the assembly that declares its member — disambiguation happened.
        Assert.NotEqual(alphaType.Assembly, betaType.Assembly);
    }

    private static void EmitCollisionType(string assemblyName, string methodName)
    {
        var asm = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName(assemblyName),
            AssemblyBuilderAccess.Run
        );
        var module = asm.DefineDynamicModule(assemblyName);
        var typeBuilder = module.DefineType("ClrInteropCollision.Widget", TypeAttributes.Public);
        var methodBuilder = typeBuilder.DefineMethod(
            methodName,
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            Type.EmptyTypes
        );
        methodBuilder.GetILGenerator().Emit(OpCodes.Ret);
        typeBuilder.CreateType();
    }
}

public static class OverloadDelegateFixture
{
    public delegate string MyHandler(int x);

    public static int Run(string pattern, Delegate handler) => 1;

    public static string Run(string pattern, MyHandler handler) => "concrete";
}

public static class OverloadResolutionFixture
{
    // Trailing optional parameter — callable with 1 or 2 arguments.
    public static int WithOptional(int x, int y = 0) => x + y;

    // Nullable parameter — callable with a plain int argument.
    public static int WithNullable(int? x) => x ?? 0;
}

public class InstanceOverloadFixture
{
    public int M(int x) => x;

    public string M(string s) => s;

    public int N(int x, int y = 0) => x + y;
}
