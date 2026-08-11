using AsmResolver.DotNet;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

public sealed partial class IlEmitter
{
    private ITypeDefOrRef? ResolveInterfaceType(string name)
    {
        if (_userTypes.TryGetValue(name, out var userType))
            return userType;

        var clrType = _clrInterop.FindType(name);
        if (clrType is not null)
            return _module.DefaultImporter.ImportType(clrType);

        foreach (var ns in ClrUsings)
        {
            clrType = _clrInterop.FindType(ns + "." + name);
            if (clrType is not null)
                return _module.DefaultImporter.ImportType(clrType);
        }

        return null;
    }

    /// <summary>
    ///     Resolves a ZType to a CLR System.Type, checking user-defined types first. Every caller
    ///     uses the result to look a member up (overload resolution, a receiver's methods and
    ///     properties), never to name a type in emitted metadata, so it maps through
    ///     <see cref="MapToReflectionClrForLookup" />: a record or union this module is defining
    ///     has no loaded <see cref="Type" /> and legitimately arrives here as <c>object</c>.
    /// </summary>
    private Type ResolveClrType(ZType type)
    {
        while (true)
        {
            // Unwrap nullable types — resolve the inner type for property/method lookup
            if (type is ZType.ZNullableType nullable)
            {
                type = nullable.Inner;
                continue;
            }

            if (type is not ZType.ZNamedType named)
                return MapToReflectionClrForLookup(type);
            if (_userTypes.TryGetValue(named.Name, out var typeRef))
            {
                var resolved = ResolveClrTypeForTypeRef(typeRef);
                if (resolved is not null)
                    return resolved;
            }

            // Try resolving as a CLR type for fully-qualified names
            if (!named.Name.Contains('.'))
                return MapToReflectionClrForLookup(type);

            // A parameterized name must prefer the arity-suffixed generic definition — a
            // same-named non-generic companion (e.g. the static System.Nullable class
            // shadowing Nullable`1) would otherwise lose the value-type flag and break
            // struct-receiver call emission (callvirt on a struct value is invalid IL).
            if (
                named.TypeArgs.Count > 0
                && _clrInterop.FindType($"{named.Name}`{named.TypeArgs.Count}") is { } openGeneric
            )
                try
                {
                    return openGeneric.MakeGenericType(
                        named.TypeArgs.Select(MapToReflectionClrForLookup).ToArray()
                    );
                }
                catch
                {
                    // Unresolvable type args (e.g. free type vars violating constraints) —
                    // fall through to the non-generic lookups below.
                }

            var clrType = _clrInterop.FindType(named.Name);
            return clrType ?? MapToReflectionClrForLookup(type);
        }
    }

    /// <summary>
    ///     Resolves an AsmResolver ITypeDefOrRef to a CLR System.Type via reflection.
    /// </summary>
    private static Type? ResolveClrTypeForTypeRef(ITypeDefOrRef typeRef)
    {
        var fullName = typeRef.FullName.Replace('/', '+');
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType(fullName);
            if (type is not null)
                return type;
        }

        // Retry without backtick arity suffixes — ZScheme union types are defined without
        // the backtick convention but ImportTypeWithGenericArity adds it for correct IL metadata
        var stripped = StripBacktickArity(fullName);
        return stripped == fullName
            ? null
            : AppDomain
                .CurrentDomain.GetAssemblies()
                .Select(asm => asm.GetType(stripped))
                .OfType<Type>()
                .FirstOrDefault();
    }

    private static (Type ClrType, object Value) ResolveAttributeArgValue(object arg)
    {
        return arg switch
        {
            SymbolRef sym => ResolveSymbolRef(sym),
            int i => (typeof(int), i),
            long l => (typeof(long), l),
            float f => (typeof(float), f),
            double d => (typeof(double), d),
            string s => (typeof(string), s),
            bool b => (typeof(bool), b),
            _ => (typeof(string), arg.ToString() ?? ""),
        };
    }

    private static (Type ClrType, object Value) ResolveSymbolRef(SymbolRef sym)
    {
        // Try to resolve as a fully-qualified enum value (e.g. System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)
        var name = sym.Name;
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0)
        {
            var typeName = name[..lastDot];
            var memberName = name[(lastDot + 1)..];
            var enumType =
                Type.GetType(typeName)
                ?? AppDomain
                    .CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(typeName))
                    .FirstOrDefault(t => t is not null);
            if (enumType is not null && enumType.IsEnum)
            {
                var enumValue = Enum.Parse(enumType, memberName);
                return (enumType, enumValue);
            }
        }

        // Fall back to string
        return (typeof(string), name);
    }

    private IMethodDescriptor ResolveAsmBaseConstructor(TypeDefinition? baseTypeDef, int paramCount)
    {
        if (baseTypeDef is not null)
        {
            var baseCtor = baseTypeDef.Methods.FirstOrDefault(m =>
                m is { IsConstructor: true, IsStatic: false } && m.Parameters.Count == paramCount
            );
            if (baseCtor is not null)
                return baseCtor;

            var defaultCtor = baseTypeDef.Methods.FirstOrDefault(m =>
                m is { IsConstructor: true, IsStatic: false, Parameters.Count: 0 }
            );
            if (defaultCtor is not null)
                return defaultCtor;
        }

        return _module.DefaultImporter.ImportMethod(
            typeof(object).GetConstructor(Type.EmptyTypes)!
        );
    }
}
