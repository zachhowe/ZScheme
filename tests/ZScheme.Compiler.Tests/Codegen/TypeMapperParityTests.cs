using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Codegen;

/// <summary>
///     Guards that the two type-mapper backends — <see cref="IlTypeMapper" /> (reflection
///     <see cref="Type" />) and <see cref="AsmResolverTypeMapper" /> (AsmResolver
///     <see cref="TypeSignature" />) — map a given <see cref="ZType" /> to the same CLR type. Both
///     delegate to the shared <c>TypeMapperCore</c>, so this is the structural proof that the IL and
///     reflection paths agree (the same property the differential fuzzer enforces end-to-end).
/// </summary>
public class TypeMapperParityTests
{
    public static TheoryData<string, ZType> Corpus()
    {
        var data = new TheoryData<string, ZType>();

        // Primitives.
        data.Add("int", ZType.Int);
        data.Add("long", ZType.Long);
        data.Add("float", ZType.Float);
        data.Add("double", ZType.Double);
        data.Add("byte", ZType.Byte);
        data.Add("char", ZType.Char);
        data.Add("bool", ZType.Bool);
        data.Add("string", ZType.String);
        data.Add("unit", ZType.Unit);

        // Task / Task<T> (recognised by literal name).
        data.Add("Task", new ZType.ZNamedType("Task", []));
        data.Add("Task<int>", new ZType.ZNamedType("Task", [ZType.Int]));

        // ValueTuple (recognised by literal name), incl. nesting and overflow → object.
        data.Add(
            "ValueTuple<int,string>",
            new ZType.ZNamedType("ValueTuple", [ZType.Int, ZType.String])
        );
        data.Add(
            "ValueTuple<int,ValueTuple<string,bool>>",
            new ZType.ZNamedType(
                "ValueTuple",
                [ZType.Int, new ZType.ZNamedType("ValueTuple", [ZType.String, ZType.Bool])]
            )
        );
        data.Add(
            "ValueTuple-overflow-8",
            new ZType.ZNamedType(
                "ValueTuple",
                [
                    ZType.Int,
                    ZType.Int,
                    ZType.Int,
                    ZType.Int,
                    ZType.Int,
                    ZType.Int,
                    ZType.Int,
                    ZType.Int,
                ]
            )
        );

        // Action / Func arities, incl. overflow → object.
        data.Add("Action", new ZType.ZFuncType([], ZType.Unit));
        data.Add("Action<int>", new ZType.ZFuncType([ZType.Int], ZType.Unit));
        data.Add(
            "Action<int,string,bool,double>",
            new ZType.ZFuncType([ZType.Int, ZType.String, ZType.Bool, ZType.Double], ZType.Unit)
        );
        data.Add(
            "Action-overflow-5",
            new ZType.ZFuncType([ZType.Int, ZType.Int, ZType.Int, ZType.Int, ZType.Int], ZType.Unit)
        );
        data.Add("Func<int>", new ZType.ZFuncType([], ZType.Int));
        data.Add("Func<string,int>", new ZType.ZFuncType([ZType.String], ZType.Int));
        data.Add(
            "Func<int,string,bool,double,int>",
            new ZType.ZFuncType([ZType.Int, ZType.String, ZType.Bool, ZType.Double], ZType.Int)
        );
        data.Add(
            "Func-overflow-6",
            new ZType.ZFuncType([ZType.Int, ZType.Int, ZType.Int, ZType.Int, ZType.Int], ZType.Int)
        );

        // Nullable: value type → Nullable<T>; reference type → unchanged.
        data.Add("int?", new ZType.ZNullableType(ZType.Int));
        data.Add("float?", new ZType.ZNullableType(ZType.Float));
        data.Add("string?", new ZType.ZNullableType(ZType.String));

        // Aliases (generic CLR, KeyValuePair, and array).
        data.Add("List<int>", new ZType.ZNamedType("List", [ZType.Int]));
        data.Add("Hash<string,int>", new ZType.ZNamedType("Hash", [ZType.String, ZType.Int]));
        data.Add("Pair<string,int>", new ZType.ZNamedType("Pair", [ZType.String, ZType.Int]));
        data.Add("Mutable-Vector<int>", new ZType.ZNamedType("Mutable-Vector", [ZType.Int]));

        // Fully-qualified CLR named types (dot-qualified path).
        data.Add("System.DateTime", new ZType.ZNamedType("System.DateTime", []));
        data.Add(
            "System.Text.StringBuilder",
            new ZType.ZNamedType("System.Text.StringBuilder", [])
        );
        data.Add(
            "System.Collections.Generic.List<int>",
            new ZType.ZNamedType("System.Collections.Generic.List", [ZType.Int])
        );

        // Unresolvable / unmappable → object on both backends.
        data.Add("unknown-fallback", new ZType.ZNamedType("Totally.Bogus.Type", []));

        return data;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Reflection_and_AsmResolver_backends_agree(string _, ZType type)
    {
        var registry = BuildStdlibRegistry();
        var module = NewModule();
        var unitType = module.DefaultImporter.ImportType(typeof(ValueTuple)).ToTypeSignature(true);

        var reflectionType = IlTypeMapper.MapToClr(type, typeAliases: registry);
        var signature = AsmResolverTypeMapper.MapToClr(
            type,
            module,
            unitType,
            typeAliases: registry
        );

        Assert.Equal(Describe(reflectionType), Describe(signature));
    }

    // ─── Canonical structural description ───────────────────────
    // Reflection Type.FullName and AsmResolver TypeSignature.FullName format generics differently
    // (assembly-qualified args vs. `Namespace.Name`N<arg,...>`), so reduce both to one canonical
    // shape and compare that.

    private static string Describe(Type t)
    {
        if (t.IsArray)
            return Describe(t.GetElementType()!) + "[]";
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments().Select(Describe);
            return $"{def.FullName}<{string.Join(",", args)}>";
        }

        return t.FullName ?? t.Name;
    }

    private static string Describe(TypeSignature s)
    {
        switch (s)
        {
            case SzArrayTypeSignature arr:
                return Describe(arr.BaseType) + "[]";
            case GenericInstanceTypeSignature gi:
                var args = gi.TypeArguments.Select(Describe);
                return $"{gi.GenericType.FullName}<{string.Join(",", args)}>";
            default:
                return s.FullName;
        }
    }

    private static ModuleDefinition NewModule()
    {
        var sysRuntimeAsm = Assembly.Load("System.Runtime");
        var corLib = new AssemblyReference("System.Runtime", sysRuntimeAsm.GetName().Version!)
        {
            PublicKeyOrToken = sysRuntimeAsm.GetName().GetPublicKeyToken(),
        };
        return new ModuleDefinition("TypeMapperParity.dll", corLib);
    }

    private static TypeAliasRegistry BuildStdlibRegistry()
    {
        var reg = new TypeAliasRegistry();
        reg.TryAdd(
            new TypeAliasInfo(
                "List",
                ["^a"],
                "System.Collections.Immutable.ImmutableList",
                "System.Collections.Immutable",
                TypeAliasKind.GenericClrType,
                SourceSpan.None
            ),
            out _
        );
        reg.TryAdd(
            new TypeAliasInfo(
                "Hash",
                ["^k", "^v"],
                "System.Collections.Immutable.ImmutableDictionary",
                "System.Collections.Immutable",
                TypeAliasKind.GenericClrType,
                SourceSpan.None
            ),
            out _
        );
        reg.TryAdd(
            new TypeAliasInfo(
                "Pair",
                ["^k", "^v"],
                "System.Collections.Generic.KeyValuePair",
                "System.Collections.Generic",
                TypeAliasKind.GenericClrType,
                SourceSpan.None
            ),
            out _
        );
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
        return reg;
    }
}
