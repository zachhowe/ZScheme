namespace ZScript.Compiler.Tests.Codegen;

using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Types;
using Xunit;

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
        Assert.Equal(ZType.Int, ClrInterop.MapClrTypeToZType(typeof(int)));
        Assert.Equal(ZType.Long, ClrInterop.MapClrTypeToZType(typeof(long)));
        Assert.Equal(ZType.Float, ClrInterop.MapClrTypeToZType(typeof(float)));
        Assert.Equal(ZType.Double, ClrInterop.MapClrTypeToZType(typeof(double)));
        Assert.Equal(ZType.Byte, ClrInterop.MapClrTypeToZType(typeof(byte)));
        Assert.Equal(ZType.Char, ClrInterop.MapClrTypeToZType(typeof(char)));
        Assert.Equal(ZType.Bool, ClrInterop.MapClrTypeToZType(typeof(bool)));
        Assert.Equal(ZType.String, ClrInterop.MapClrTypeToZType(typeof(string)));
        Assert.Equal(ZType.Unit, ClrInterop.MapClrTypeToZType(typeof(void)));
    }

    [Fact]
    public void MethodInfoToZFuncType_ReturnsCorrectType()
    {
        var diag = new DiagnosticBag();
        var interop = new ClrInterop(diag);
        var method = interop.Resolve("System.Math/Sqrt", SourceSpan.None);
        Assert.NotNull(method);

        var funcType = ClrInterop.MethodInfoToZFuncType(method!);
        var ft = Assert.IsType<ZType.ZFuncType>(funcType);
        Assert.Single(ft.Params);
        Assert.Equal(ZType.Double, ft.Params[0]);
        Assert.Equal(ZType.Double, ft.Return);
    }
}
