using System.Collections.Immutable;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Codegen;

public class IlTypeMapperTests
{
    // ─── Primitive Types ──────────────────────────────────────

    [Fact]
    public void MapToClr_Int_ReturnsInt()
    {
        Assert.Equal(typeof(int), IlTypeMapper.MapToClr(ZType.Int));
    }

    [Fact]
    public void MapToClr_Long_ReturnsLong()
    {
        Assert.Equal(typeof(long), IlTypeMapper.MapToClr(ZType.Long));
    }

    [Fact]
    public void MapToClr_Float_ReturnsFloat()
    {
        Assert.Equal(typeof(float), IlTypeMapper.MapToClr(ZType.Float));
    }

    [Fact]
    public void MapToClr_Double_ReturnsDouble()
    {
        Assert.Equal(typeof(double), IlTypeMapper.MapToClr(ZType.Double));
    }

    [Fact]
    public void MapToClr_Byte_ReturnsByte()
    {
        Assert.Equal(typeof(byte), IlTypeMapper.MapToClr(ZType.Byte));
    }

    [Fact]
    public void MapToClr_Char_ReturnsChar()
    {
        Assert.Equal(typeof(char), IlTypeMapper.MapToClr(ZType.Char));
    }

    [Fact]
    public void MapToClr_Bool_ReturnsBool()
    {
        Assert.Equal(typeof(bool), IlTypeMapper.MapToClr(ZType.Bool));
    }

    [Fact]
    public void MapToClr_String_ReturnsString()
    {
        Assert.Equal(typeof(string), IlTypeMapper.MapToClr(ZType.String));
    }

    [Fact]
    public void MapToClr_Unit_ReturnsValueTuple()
    {
        Assert.Equal(typeof(ValueTuple), IlTypeMapper.MapToClr(ZType.Unit));
    }

    // ─── Collection Types (Single Type Arg) ───────────────────

    [Fact]
    public void MapToClr_ListOfInt_ReturnsImmutableList()
    {
        var zType = new ZType.ZNamedType("List", [ZType.Int]);
        Assert.Equal(typeof(ImmutableList<int>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_ArrayOfInt_ReturnsImmutableArray()
    {
        var zType = new ZType.ZNamedType("Array", [ZType.Int]);
        Assert.Equal(typeof(ImmutableArray<int>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_MutableArrayOfInt_ReturnsClrArray()
    {
        var zType = new ZType.ZNamedType("Mutable-Array", [ZType.Int]);
        Assert.Equal(typeof(int[]), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_MutableListOfInt_ReturnsList()
    {
        var zType = new ZType.ZNamedType("Mutable-List", [ZType.Int]);
        Assert.Equal(typeof(List<int>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_TaskNoArgs_ReturnsTask()
    {
        var zType = new ZType.ZNamedType("Task", []);
        Assert.Equal(typeof(Task), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_TaskOfInt_ReturnsGenericTask()
    {
        var zType = new ZType.ZNamedType("Task", [ZType.Int]);
        Assert.Equal(typeof(Task<int>), IlTypeMapper.MapToClr(zType));
    }

    // ─── Collection Types (Two Type Args) ─────────────────────

    [Fact]
    public void MapToClr_MapOfStringInt_ReturnsImmutableDictionary()
    {
        var zType = new ZType.ZNamedType("Map", [ZType.String, ZType.Int]);
        Assert.Equal(typeof(ImmutableDictionary<string, int>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_MutableMapOfStringInt_ReturnsDictionary()
    {
        var zType = new ZType.ZNamedType("Mutable-Map", [ZType.String, ZType.Int]);
        Assert.Equal(typeof(Dictionary<string, int>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_PairOfStringInt_ReturnsKeyValuePair()
    {
        var zType = new ZType.ZNamedType("Pair", [ZType.String, ZType.Int]);
        Assert.Equal(typeof(KeyValuePair<string, int>), IlTypeMapper.MapToClr(zType));
    }

    // ─── Nested Generic Types ─────────────────────────────────

    [Fact]
    public void MapToClr_ListOfListOfInt_ReturnsNestedImmutableList()
    {
        var zType = new ZType.ZNamedType("List", [new ZType.ZNamedType("List", [ZType.Int])]);
        Assert.Equal(typeof(ImmutableList<ImmutableList<int>>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_MapOfStringListOfInt_ReturnsNestedGenericType()
    {
        var zType = new ZType.ZNamedType("Map",
            [ZType.String, new ZType.ZNamedType("List", [ZType.Int])]);
        Assert.Equal(
            typeof(ImmutableDictionary<string, ImmutableList<int>>),
            IlTypeMapper.MapToClr(zType));
    }

    // ─── Function Types — Action (Unit Return) ────────────────

    [Fact]
    public void MapToClr_FuncZeroParamsUnitReturn_ReturnsAction()
    {
        var zType = new ZType.ZFuncType([], ZType.Unit);
        Assert.Equal(typeof(Action), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_FuncOneParamUnitReturn_ReturnsActionOfT()
    {
        var zType = new ZType.ZFuncType([ZType.Int], ZType.Unit);
        Assert.Equal(typeof(Action<int>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_FuncTwoParamsUnitReturn_ReturnsActionOfTT()
    {
        var zType = new ZType.ZFuncType([ZType.Int, ZType.String], ZType.Unit);
        Assert.Equal(typeof(Action<int, string>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_FuncThreeParamsUnitReturn_ReturnsActionOfTTT()
    {
        var zType = new ZType.ZFuncType([ZType.Int, ZType.String, ZType.Bool], ZType.Unit);
        Assert.Equal(typeof(Action<int, string, bool>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_FuncFourParamsUnitReturn_ReturnsActionOfTTTT()
    {
        var zType = new ZType.ZFuncType([ZType.Int, ZType.String, ZType.Bool, ZType.Double], ZType.Unit);
        Assert.Equal(typeof(Action<int, string, bool, double>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_FuncFiveParamsUnitReturn_FallsBackToObject()
    {
        var zType = new ZType.ZFuncType(
            [ZType.Int, ZType.String, ZType.Bool, ZType.Double, ZType.Long], ZType.Unit);
        var diagnostics = new DiagnosticBag();
        Assert.Equal(typeof(object), IlTypeMapper.MapToClr(zType, diagnostics));
        var warning = Assert.Single(diagnostics.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Action delegate with 5 parameters exceeds maximum of 4", warning.Message);
    }

    // ─── Function Types — Func (Non-Unit Return) ──────────────

    [Fact]
    public void MapToClr_FuncZeroParamsIntReturn_ReturnsFuncOfT()
    {
        var zType = new ZType.ZFuncType([], ZType.Int);
        Assert.Equal(typeof(Func<int>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_FuncOneParamIntReturn_ReturnsFuncOfTT()
    {
        var zType = new ZType.ZFuncType([ZType.String], ZType.Int);
        Assert.Equal(typeof(Func<string, int>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_FuncTwoParamsIntReturn_ReturnsFuncOfTTT()
    {
        var zType = new ZType.ZFuncType([ZType.Int, ZType.String], ZType.Bool);
        Assert.Equal(typeof(Func<int, string, bool>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_FuncThreeParamsIntReturn_ReturnsFuncOfTTTT()
    {
        var zType = new ZType.ZFuncType([ZType.Int, ZType.String, ZType.Bool], ZType.Double);
        Assert.Equal(typeof(Func<int, string, bool, double>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_FuncFourParamsIntReturn_ReturnsFuncOfTTTTT()
    {
        var zType = new ZType.ZFuncType([ZType.Int, ZType.String, ZType.Bool, ZType.Double], ZType.Int);
        Assert.Equal(typeof(Func<int, string, bool, double, int>), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_FuncFiveParamsIntReturn_FallsBackToObject()
    {
        var zType = new ZType.ZFuncType(
            [ZType.Int, ZType.String, ZType.Bool, ZType.Double, ZType.Long], ZType.Int);
        var diagnostics = new DiagnosticBag();
        Assert.Equal(typeof(object), IlTypeMapper.MapToClr(zType, diagnostics));
        var warning = Assert.Single(diagnostics.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Func delegate with 6 type arguments exceeds maximum of 5", warning.Message);
    }

    // ─── Fallback / Default Cases ─────────────────────────────

    [Fact]
    public void MapToClr_TypeVar_FallsBackToObject()
    {
        var zType = new ZType.ZTypeVar(0);
        var diagnostics = new DiagnosticBag();
        Assert.Equal(typeof(object), IlTypeMapper.MapToClr(zType, diagnostics));
        var warning = Assert.Single(diagnostics.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Cannot map type", warning.Message);
    }

    [Fact]
    public void MapToClr_ConstrainedVar_FallsBackToObject()
    {
        var zType = new ZType.ZConstrainedVar(0, new HashSet<PrimitiveKind> { PrimitiveKind.Int });
        var diagnostics = new DiagnosticBag();
        Assert.Equal(typeof(object), IlTypeMapper.MapToClr(zType, diagnostics));
        var warning = Assert.Single(diagnostics.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Cannot map type", warning.Message);
    }

    [Fact]
    public void MapToClr_ForAllType_FallsBackToObject()
    {
        var zType = new ZType.ZForAllType([0], ZType.Int);
        var diagnostics = new DiagnosticBag();
        Assert.Equal(typeof(object), IlTypeMapper.MapToClr(zType, diagnostics));
        var warning = Assert.Single(diagnostics.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Cannot map type", warning.Message);
    }

    [Fact]
    public void MapToClr_UnknownNamedType_FallsBackToObject()
    {
        var zType = new ZType.ZNamedType("SomeUserType", []);
        var diagnostics = new DiagnosticBag();
        Assert.Equal(typeof(object), IlTypeMapper.MapToClr(zType, diagnostics));
        var warning = Assert.Single(diagnostics.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Cannot map type", warning.Message);
    }

    // ─── Fully-Qualified Task Types ───────────────────────────

    [Fact]
    public void MapToClr_FullyQualifiedTaskNoArgs_ReturnsTask()
    {
        var zType = new ZType.ZNamedType("System.Threading.Tasks.Task", []);
        Assert.Equal(typeof(Task), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_FullyQualifiedTaskOfInt_ReturnsGenericTask()
    {
        var zType = new ZType.ZNamedType("System.Threading.Tasks.Task", [ZType.Int]);
        Assert.Equal(typeof(Task<int>), IlTypeMapper.MapToClr(zType));
    }

    // ─── Nullable Types ───────────────────────────────────────

    [Fact]
    public void MapToClr_NullableOfValueType_ReturnsNullable()
    {
        var zType = new ZType.ZNullableType(ZType.Int);
        Assert.Equal(typeof(int?), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_NullableOfFloat_ReturnsNullableFloat()
    {
        var zType = new ZType.ZNullableType(ZType.Float);
        Assert.Equal(typeof(float?), IlTypeMapper.MapToClr(zType));
    }

    [Fact]
    public void MapToClr_NullableOfReferenceType_ReturnsInnerType()
    {
        var zType = new ZType.ZNullableType(ZType.String);
        Assert.Equal(typeof(string), IlTypeMapper.MapToClr(zType));
    }
}
