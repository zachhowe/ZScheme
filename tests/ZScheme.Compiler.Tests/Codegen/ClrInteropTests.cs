using System.Reflection;
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
        Assert.Equal(ZType.Long, new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(long)));
        Assert.Equal(ZType.Float, new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(float)));
        Assert.Equal(ZType.Double, new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(double)));
        Assert.Equal(ZType.Byte, new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(byte)));
        Assert.Equal(ZType.Char, new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(char)));
        Assert.Equal(ZType.Bool, new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(bool)));
        Assert.Equal(ZType.String, new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(string)));
        Assert.Equal(ZType.Unit, new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(void)));
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
        var result = new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(byte[]));
        var named = Assert.IsType<ZType.ZNamedType>(result);
        Assert.Equal("Mutable-Vector", named.Name);
        Assert.Single(named.TypeArgs);
        Assert.Equal(ZType.Byte, named.TypeArgs[0]);
    }

    [Fact]
    public void MapClrTypeToZType_MapsStringMutableVectorCorrectly()
    {
        var result = new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(string[]));
        var named = Assert.IsType<ZType.ZNamedType>(result);
        Assert.Equal("Mutable-Vector", named.Name);
        Assert.Single(named.TypeArgs);
        Assert.Equal(ZType.String, named.TypeArgs[0]);
    }

    [Fact]
    public void MapClrTypeToZType_MapsMutableTreeListCorrectly()
    {
        var result = new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(List<int>));
        var named = Assert.IsType<ZType.ZNamedType>(result);
        Assert.Equal("Mutable-TreeList", named.Name);
        Assert.Single(named.TypeArgs);
        Assert.Equal(ZType.Int, named.TypeArgs[0]);
    }

    [Fact]
    public void MapClrTypeToZType_MapsMutableHashCorrectly()
    {
        var result = new ClrInterop(new DiagnosticBag()).MapClrTypeToZType(typeof(Dictionary<string, int>));
        var named = Assert.IsType<ZType.ZNamedType>(result);
        Assert.Equal("Mutable-Hash", named.Name);
        Assert.Equal(2, named.TypeArgs.Count);
        Assert.Equal(ZType.String, named.TypeArgs[0]);
        Assert.Equal(ZType.Int, named.TypeArgs[1]);
    }

  [Fact]
    public void MethodInfoToZFuncTypeWithOutParams_DetectsOutParams()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);
        var method = typeof(int).GetMethod("TryParse", [typeof(string), typeof(int).MakeByRefType()])!;
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

        var outParams = interop.DetectOutParams("System.Int32/TryParse", SourceSpan.None,
            BindingFlags.Public | BindingFlags.Static);

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
}
