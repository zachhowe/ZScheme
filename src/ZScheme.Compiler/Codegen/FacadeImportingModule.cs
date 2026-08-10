using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     The module <see cref="IlEmitter" /> emits into. Identical to a plain
///     <see cref="ModuleDefinition" /> except that its default importer spells corelib types the
///     way a reference pack does — see <see cref="CorLibFacadeMap" /> for why that matters and
///     <see cref="FacadeReferenceImporter" /> for how it is done.
///     <para>
///         Overriding <see cref="GetDefaultImporter" /> is what keeps this to one line at the
///         emitter: <c>ModuleDefinition.DefaultImporter</c> is read-only, so every one of the
///         emitter's import sites would otherwise have to be routed through an importer of its
///         own.
///     </para>
/// </summary>
internal sealed class FacadeImportingModule(string name, AssemblyReference corLib)
    : ModuleDefinition(name, corLib)
{
    protected override ReferenceImporter GetDefaultImporter()
    {
        return new FacadeReferenceImporter(this);
    }
}

/// <summary>
///     Imports references the way a reference pack spells them: a type that reflection reports as
///     living in <c>System.Private.CoreLib</c> is scoped to the facade that owns it instead, so
///     the emitted metadata can be consumed from C#.
///     <para>
///         The interception point is <see cref="ImportAssembly" />, which every scope creation
///         funnels through but which is handed only an assembly identity. The type it is being
///         asked for is recovered from the enclosing import: AsmResolver reaches an assembly scope
///         only from inside <see cref="ImportTypeSignature(Type)" /> (for reflection types) or
///         <see cref="ImportType(TypeReference)" /> (for references read out of another module),
///         and both recurse into their component types before creating any scope — so the
///         innermost one in flight is always the type the scope belongs to.
///     </para>
/// </summary>
internal sealed class FacadeReferenceImporter(ModuleDefinition module) : ReferenceImporter(module)
{
    private readonly Dictionary<string, AssemblyReference> _facades = new(StringComparer.Ordinal);

    /// <summary>Full name of the type whose scope <see cref="ImportAssembly" /> would be creating
    ///     right now, or null outside any type import.</summary>
    private string? _scopeOwner;

    public override TypeSignature ImportTypeSignature(Type type)
    {
        var enclosing = _scopeOwner;
        _scopeOwner = CorLibFacadeMap.ScopeOwner(type) ?? enclosing;
        try
        {
            return base.ImportTypeSignature(type);
        }
        finally
        {
            _scopeOwner = enclosing;
        }
    }

    /// <summary>
    ///     Covers types imported from another module rather than from reflection — chiefly the
    ///     precompiled assemblies a package build references. Without it, consuming a package that
    ///     was itself emitted before this fix would copy its <c>System.Private.CoreLib</c>
    ///     references straight into the new assembly.
    /// </summary>
    protected override ITypeDefOrRef ImportType(TypeReference type)
    {
        var outermost = type;
        while (outermost.DeclaringType is TypeReference declaring)
            outermost = declaring;

        var enclosing = _scopeOwner;
        _scopeOwner = outermost.FullName ?? enclosing;
        try
        {
            return base.ImportType(type);
        }
        finally
        {
            _scopeOwner = enclosing;
        }
    }

    protected override AssemblyReference ImportAssembly(AssemblyDescriptor assembly)
    {
        if (assembly.Name != CorLibFacadeMap.ImplementationAssembly || _scopeOwner is null)
            return base.ImportAssembly(assembly);

        var facade = CorLibFacadeMap.FacadeFor(_scopeOwner);
        if (facade?.Name is null)
            return base.ImportAssembly(assembly);

        if (_facades.TryGetValue(facade.Name, out var imported))
            return imported;

        imported = base.ImportAssembly(
            new AssemblyReference(facade.Name, facade.Version ?? new Version())
            {
                PublicKeyOrToken = facade.GetPublicKeyToken(),
            }
        );
        _facades[facade.Name] = imported;
        return imported;
    }
}
