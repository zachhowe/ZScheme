using System.Collections.Immutable;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Codegen;

public class IlTypeMapperTests
{
    /// <summary>
    ///     Builds a registry with the stdlib aliases (collections, Pair, and Concurrent-*) so
    ///     unit tests for the type mapper can exercise alias-resolved paths without spinning
    ///     up the full compilation pipeline.
    /// </summary>
    private static TypeAliasRegistry BuildStdlibRegistry()
    {
        var reg = new TypeAliasRegistry();
        reg.TryAdd(new TypeAliasInfo("List", ["^a"],
            "System.Collections.Immutable.ImmutableList", "System.Collections.Immutable",
            TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        reg.TryAdd(new TypeAliasInfo("Vector", ["^a"],
            "System.Collections.Immutable.ImmutableArray", "System.Collections.Immutable",
            TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        reg.TryAdd(new TypeAliasInfo("Hash", ["^k", "^v"],
            "System.Collections.Immutable.ImmutableDictionary", "System.Collections.Immutable",
            TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        reg.TryAdd(new TypeAliasInfo("Mutable-List", ["^a"],
            "System.Collections.Generic.List", null,
            TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        reg.TryAdd(new TypeAliasInfo("Mutable-Hash", ["^k", "^v"],
            "System.Collections.Generic.Dictionary", null,
            TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        reg.TryAdd(new TypeAliasInfo("Mutable-Vector", ["^a"], "", null,
            TypeAliasKind.SzArray, SourceSpan.None), out _);
        reg.TryAdd(new TypeAliasInfo("Pair", ["^k", "^v"],
            "System.Collections.Generic.KeyValuePair", "System.Collections.Generic",
            TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        reg.TryAdd(new TypeAliasInfo("Concurrent-Bag", ["^a"],
            "System.Collections.Concurrent.ConcurrentBag", "System.Collections.Concurrent",
            TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        reg.TryAdd(new TypeAliasInfo("Concurrent-Queue", ["^a"],
            "System.Collections.Concurrent.ConcurrentQueue", "System.Collections.Concurrent",
            TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        reg.TryAdd(new TypeAliasInfo("Concurrent-Stack", ["^a"],
            "System.Collections.Concurrent.ConcurrentStack", "System.Collections.Concurrent",
            TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        reg.TryAdd(new TypeAliasInfo("Concurrent-Dictionary", ["^k", "^v"],
            "System.Collections.Concurrent.ConcurrentDictionary", "System.Collections.Concurrent",
            TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        return reg;
    }
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
        Assert.Equal(typeof(ImmutableList<int>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
    }

    [Fact]
    public void MapToClr_VectorOfInt_ReturnsImmutableArray()
    {
        var zType = new ZType.ZNamedType("Vector", [ZType.Int]);
        Assert.Equal(typeof(ImmutableArray<int>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
    }

    [Fact]
    public void MapToClr_MutableVectorOfInt_ReturnsClrArray()
    {
        var zType = new ZType.ZNamedType("Mutable-Vector", [ZType.Int]);
        Assert.Equal(typeof(int[]),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
    }

    [Fact]
    public void MapToClr_MutableListOfInt_ReturnsList()
    {
        var zType = new ZType.ZNamedType("Mutable-List", [ZType.Int]);
        Assert.Equal(typeof(List<int>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
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
    public void MapToClr_HashOfStringInt_ReturnsImmutableDictionary()
    {
        var zType = new ZType.ZNamedType("Hash", [ZType.String, ZType.Int]);
        Assert.Equal(typeof(ImmutableDictionary<string, int>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
    }

    [Fact]
    public void MapToClr_MutableHashOfStringInt_ReturnsDictionary()
    {
        var zType = new ZType.ZNamedType("Mutable-Hash", [ZType.String, ZType.Int]);
        Assert.Equal(typeof(Dictionary<string, int>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
    }

    [Fact]
    public void MapToClr_PairOfStringInt_ReturnsKeyValuePair()
    {
        var zType = new ZType.ZNamedType("Pair", [ZType.String, ZType.Int]);
        Assert.Equal(typeof(KeyValuePair<string, int>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
    }

    // ─── Nested Generic Types ─────────────────────────────────

    [Fact]
    public void MapToClr_ListOfListOfInt_ReturnsNestedImmutableList()
    {
        var zType = new ZType.ZNamedType("List", [new ZType.ZNamedType("List", [ZType.Int])]);
        Assert.Equal(typeof(ImmutableList<ImmutableList<int>>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
    }

    [Fact]
    public void MapToClr_HashOfStringListOfInt_ReturnsNestedGenericType()
    {
        var zType = new ZType.ZNamedType("Hash",
            [ZType.String, new ZType.ZNamedType("List", [ZType.Int])]);
        Assert.Equal(
            typeof(ImmutableDictionary<string, ImmutableList<int>>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
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

    // ─── User-Types Overload (precompiled-assembly path) ──────

    // Stand-in for an open generic union loaded from a precompiled assembly,
    // e.g. SList<T> with a Cons<T> case.
    private abstract class FakeUnion<T>;

    private sealed class FakeUnionCase<T> : FakeUnion<T>;

    private sealed class FakePoint;

    [Fact]
    public void MapToClr_UserType_NonGeneric_ResolvesFromUserTypes()
    {
        var userTypes = new Dictionary<string, Type> { ["Point"] = typeof(FakePoint) };
        var zType = new ZType.ZNamedType("Point", []);

        Assert.Equal(typeof(FakePoint), IlTypeMapper.MapToClr(zType, userTypes));
    }

    [Fact]
    public void MapToClr_UserType_GenericWithTypeArg_ResolvesAndInstantiates()
    {
        var userTypes = new Dictionary<string, Type> { ["SList"] = typeof(FakeUnion<>) };
        var zType = new ZType.ZNamedType("SList", [ZType.Int]);

        Assert.Equal(typeof(FakeUnion<int>), IlTypeMapper.MapToClr(zType, userTypes));
    }

    [Fact]
    public void MapToClr_UserType_GenericCaseWithTypeArg_ResolvesAndInstantiates()
    {
        // Mirrors the bug: SList<T>.Cons<T> as a sealed case of an open generic union.
        var userTypes = new Dictionary<string, Type> { ["Cons"] = typeof(FakeUnionCase<>) };
        var zType = new ZType.ZNamedType("Cons", [ZType.String]);

        Assert.Equal(typeof(FakeUnionCase<string>), IlTypeMapper.MapToClr(zType, userTypes));
    }

    [Fact]
    public void MapToClr_UserType_NotInDictionary_FallsBackToObjectWithWarning()
    {
        var userTypes = new Dictionary<string, Type>();
        var zType = new ZType.ZNamedType("Unknown", []);
        var diagnostics = new DiagnosticBag();

        Assert.Equal(typeof(object), IlTypeMapper.MapToClr(zType, userTypes, diagnostics: diagnostics));
        Assert.NotEmpty(diagnostics.Diagnostics);
    }

    [Fact]
    public void MapToClr_UserType_NestedInsideBuiltinGeneric_ResolvesInner()
    {
        // List<SList<int>> — exercises the recursive descent into TypeArgs.
        var userTypes = new Dictionary<string, Type> { ["SList"] = typeof(FakeUnion<>) };
        var zType = new ZType.ZNamedType("List",
            [new ZType.ZNamedType("SList", [ZType.Int])]);

        var aliases = new TypeAliasRegistry();
        aliases.TryAdd(new TypeAliasInfo("List", ["a"],
            "System.Collections.Immutable.ImmutableList",
            "System.Collections.Immutable",
            TypeAliasKind.GenericClrType,
            default), out _);

        Assert.Equal(typeof(ImmutableList<FakeUnion<int>>),
            IlTypeMapper.MapToClr(zType, userTypes, typeAliases: aliases));
    }

    [Fact]
    public void MapToClr_UserTypesOverload_NullableOfReferenceType_ReturnsInnerType()
    {
        // Regression: the overload used to unconditionally wrap in Nullable<>, which
        // throws for reference types. Should now mirror the public overload.
        var userTypes = new Dictionary<string, Type>();
        var zType = new ZType.ZNullableType(ZType.String);

        Assert.Equal(typeof(string), IlTypeMapper.MapToClr(zType, userTypes));
    }

    [Fact]
    public void MapToClr_UserTypesOverload_NullableOfValueType_ReturnsNullable()
    {
        var userTypes = new Dictionary<string, Type>();
        var zType = new ZType.ZNullableType(ZType.Int);

        Assert.Equal(typeof(int?), IlTypeMapper.MapToClr(zType, userTypes));
    }

    // ─── Concurrent Type Aliases ────────────────────────────────

    [Fact]
    public void MapToClr_ConcurrentBagOfInt_ReturnsConcurrentBag()
    {
        var zType = new ZType.ZNamedType("Concurrent-Bag", [ZType.Int]);
        Assert.Equal(typeof(System.Collections.Concurrent.ConcurrentBag<int>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
    }

    [Fact]
    public void MapToClr_ConcurrentQueueOfInt_ReturnsConcurrentQueue()
    {
        var zType = new ZType.ZNamedType("Concurrent-Queue", [ZType.Int]);
        Assert.Equal(typeof(System.Collections.Concurrent.ConcurrentQueue<int>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
    }

    [Fact]
    public void MapToClr_ConcurrentStackOfInt_ReturnsConcurrentStack()
    {
        var zType = new ZType.ZNamedType("Concurrent-Stack", [ZType.Int]);
        Assert.Equal(typeof(System.Collections.Concurrent.ConcurrentStack<int>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
    }

    [Fact]
    public void MapToClr_ConcurrentDictionaryOfStringInt_ReturnsConcurrentDictionary()
    {
        var zType = new ZType.ZNamedType("Concurrent-Dictionary", [ZType.String, ZType.Int]);
        Assert.Equal(typeof(System.Collections.Concurrent.ConcurrentDictionary<string, int>),
            IlTypeMapper.MapToClr(zType, typeAliases: BuildStdlibRegistry()));
    }

    // ─── Type Alias Equivalence ─────────────────────────────────
    // These tests verify that ZScheme type aliases resolve to the same CLR types
    // as the underlying CLR types they alias.

    [Fact]
    public void MapToClr_MutableHash_ResolvesToDictionary()
    {
        var aliases = BuildStdlibRegistry();
        var mutableHashType = new ZType.ZNamedType("Mutable-Hash", [ZType.String, ZType.Int]);
        var clr = IlTypeMapper.MapToClr(mutableHashType, typeAliases: aliases);
        Assert.Equal(typeof(Dictionary<string, int>), clr);
    }

    [Fact]
    public void MapToClr_MutableList_ResolvesToList()
    {
        var aliases = BuildStdlibRegistry();
        var mutableListType = new ZType.ZNamedType("Mutable-List", [ZType.Int]);
        var clr = IlTypeMapper.MapToClr(mutableListType, typeAliases: aliases);
        Assert.Equal(typeof(List<int>), clr);
    }

    [Fact]
    public void MapToClr_MutableVector_ResolvesToArray()
    {
        var aliases = BuildStdlibRegistry();
        var mutableVectorType = new ZType.ZNamedType("Mutable-Vector", [ZType.Int]);
        var clr = IlTypeMapper.MapToClr(mutableVectorType, typeAliases: aliases);
        Assert.Equal(typeof(int[]), clr);
    }

    [Fact]
    public void MapToClr_CustomArrayAlias_ResolvesToArray()
    {
        var aliases = new TypeAliasRegistry();
        aliases.TryAdd(new TypeAliasInfo("Custom-Array", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        var customArrayType = new ZType.ZNamedType("Custom-Array", [ZType.Int]);
        var clr = IlTypeMapper.MapToClr(customArrayType, typeAliases: aliases);
        Assert.Equal(typeof(int[]), clr);
    }

    [Fact]
    public void MapToClr_CustomArrayAlias_StringElement_ResolvesToArray()
    {
        var aliases = new TypeAliasRegistry();
        aliases.TryAdd(new TypeAliasInfo("Custom-Array", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        var customArrayType = new ZType.ZNamedType("Custom-Array", [ZType.String]);
        var clr = IlTypeMapper.MapToClr(customArrayType, typeAliases: aliases);
        Assert.Equal(typeof(string[]), clr);
    }

    [Fact]
    public void MapToClr_Hash_ResolvesToImmutableDictionary()
    {
        var aliases = BuildStdlibRegistry();
        var hashType = new ZType.ZNamedType("Hash", [ZType.String, ZType.Int]);
        var clr = IlTypeMapper.MapToClr(hashType, typeAliases: aliases);
        Assert.Equal(typeof(ImmutableDictionary<string, int>), clr);
    }

    [Fact]
    public void MapToClr_Vector_ResolvesToImmutableArray()
    {
        var aliases = BuildStdlibRegistry();
        var vectorType = new ZType.ZNamedType("Vector", [ZType.Int]);
        var clr = IlTypeMapper.MapToClr(vectorType, typeAliases: aliases);
        Assert.Equal(typeof(ImmutableArray<int>), clr);
    }

    [Fact]
    public void MapToClr_Pair_ResolvesToKeyValuePair()
    {
        var aliases = BuildStdlibRegistry();
        var pairType = new ZType.ZNamedType("Pair", [ZType.String, ZType.Int]);
        var clr = IlTypeMapper.MapToClr(pairType, typeAliases: aliases);
        Assert.Equal(typeof(KeyValuePair<string, int>), clr);
    }

    [Fact]
    public void MapToClr_ConcurrentQueue_ResolvesToConcurrentQueue()
    {
        var aliases = BuildStdlibRegistry();
        var queueType = new ZType.ZNamedType("Concurrent-Queue", [ZType.Int]);
        var clr = IlTypeMapper.MapToClr(queueType, typeAliases: aliases);
        Assert.Equal(typeof(System.Collections.Concurrent.ConcurrentQueue<int>), clr);
    }

    [Fact]
    public void MapToClr_ConcurrentBag_ResolvesToConcurrentBag()
    {
        var aliases = BuildStdlibRegistry();
        var bagType = new ZType.ZNamedType("Concurrent-Bag", [ZType.Int]);
        var clr = IlTypeMapper.MapToClr(bagType, typeAliases: aliases);
        Assert.Equal(typeof(System.Collections.Concurrent.ConcurrentBag<int>), clr);
    }

    [Fact]
    public void MapToClr_ConcurrentStack_ResolvesToConcurrentStack()
    {
        var aliases = BuildStdlibRegistry();
        var stackType = new ZType.ZNamedType("Concurrent-Stack", [ZType.Int]);
        var clr = IlTypeMapper.MapToClr(stackType, typeAliases: aliases);
        Assert.Equal(typeof(System.Collections.Concurrent.ConcurrentStack<int>), clr);
    }

    [Fact]
    public void MapToClr_ConcurrentDictionary_ResolvesToConcurrentDictionary()
    {
        var aliases = BuildStdlibRegistry();
        var dictType = new ZType.ZNamedType("Concurrent-Dictionary", [ZType.String, ZType.Int]);
        var clr = IlTypeMapper.MapToClr(dictType, typeAliases: aliases);
        Assert.Equal(typeof(System.Collections.Concurrent.ConcurrentDictionary<string, int>), clr);
    }

    [Fact]
    public void MapToClr_List_ResolvesToImmutableList()
    {
        var aliases = BuildStdlibRegistry();
        var listType = new ZType.ZNamedType("List", [ZType.Int]);
        var clr = IlTypeMapper.MapToClr(listType, typeAliases: aliases);
        Assert.Equal(typeof(ImmutableList<int>), clr);
    }

    // ─── Nested Alias Types ─────────────────────────────────────

    [Fact]
    public void MapToClr_MutableHashOfMutableList_ResolvesNestedAliases()
    {
        var aliases = BuildStdlibRegistry();
        var zType = new ZType.ZNamedType("Mutable-Hash", [ZType.String, new ZType.ZNamedType("Mutable-List", [ZType.Int])]);
        var clr = IlTypeMapper.MapToClr(zType, typeAliases: aliases);
        Assert.Equal(typeof(Dictionary<string, List<int>>), clr);
    }

    [Fact]
    public void MapToClr_VectorOfMutableVector_ResolvesNestedAliases()
    {
        var aliases = BuildStdlibRegistry();
        var zType = new ZType.ZNamedType("Vector", [new ZType.ZNamedType("Mutable-Vector", [ZType.Int])]);
        var clr = IlTypeMapper.MapToClr(zType, typeAliases: aliases);
        Assert.Equal(typeof(ImmutableArray<int[]>), clr);
    }

    [Fact]
    public void MapToClr_ConcurrentDictionaryOfHash_ResolvesNestedAliases()
    {
        var aliases = BuildStdlibRegistry();
        var zType = new ZType.ZNamedType("Concurrent-Dictionary", [ZType.String, new ZType.ZNamedType("Hash", [ZType.String, ZType.Int])]);
        var clr = IlTypeMapper.MapToClr(zType, typeAliases: aliases);
        Assert.Equal(typeof(System.Collections.Concurrent.ConcurrentDictionary<string, ImmutableDictionary<string, int>>), clr);
    }
}
