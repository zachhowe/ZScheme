using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Backend-specific construction surface used by <see cref="TypeMapperCore" />. The core owns
///     all of the <see cref="ZType" /> traversal and decision logic (alias resolution, name munging,
///     func/tuple/Task arity); a factory only knows how to <em>build</em> a result of type
///     <typeparamref name="T" /> — reflection <see cref="System.Type" /> for the runtime-reflection
///     backend, or an AsmResolver <c>TypeSignature</c> for IL emission.
/// </summary>
/// <typeparam name="T">The backend result type (reflection <c>Type</c> or AsmResolver <c>TypeSignature</c>).</typeparam>
internal interface ITypeFactory<T>
{
    /// <summary>Maps a primitive kind, including <see cref="PrimitiveKind.Unit" />.</summary>
    T Primitive(PrimitiveKind kind);

    /// <summary>The backend's representation of <c>System.Object</c>, used as the fallback type.</summary>
    T Object { get; }

    /// <summary>True if the mapped type is a value type (used for Nullable&lt;T&gt; selection).</summary>
    bool IsValueType(T t);

    /// <summary>True if the mapped type is an open generic type definition (used to guard user-type closing).</summary>
    bool IsGenericDefinition(T t);

    /// <summary>Builds a single-dimension array type from a mapped element type.</summary>
    T MakeArray(T element);

    /// <summary>
    ///     Imports a resolved, non-generic CLR <see cref="System.Type" />. When
    ///     <paramref name="corLibAware" /> is true, corlib types (Action, Task, etc.) are routed
    ///     through the module's configured corlib scope rather than System.Private.CoreLib.
    /// </summary>
    T FromClrType(Type clrType, bool corLibAware);

    /// <summary>
    ///     Closes an open generic CLR <see cref="System.Type" /> (e.g. <c>Func&lt;,&gt;</c>,
    ///     <c>Task&lt;&gt;</c>, <c>Nullable&lt;&gt;</c>, an alias target) over already-mapped arguments.
    ///     Corlib-aware on the IL backend.
    /// </summary>
    T CloseClrGeneric(Type openClrType, T[] args);

    /// <summary>Closes an already-mapped open generic user type over already-mapped arguments.</summary>
    T CloseMappedGeneric(T openMapped, T[] args);

    /// <summary>Reports a non-fatal mapping fallback (routes to diagnostics on the reflection backend).</summary>
    void Warn(string message);
}
