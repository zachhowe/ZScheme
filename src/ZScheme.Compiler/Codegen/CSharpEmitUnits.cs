namespace ZScheme.Compiler.Codegen;

/// <summary>
///     One independently emittable piece of a C# compilation: the main program's class
///     (<see cref="ModuleClassName" /> null) or one module class. A unit is a complete
///     namespace-scoped declaration that starts on its first line and ends with the blank
///     line separating it from the next, so prefixing it with
///     <see cref="CSharpEmitUnits.Header" /> yields a valid standalone source file.
/// </summary>
public sealed record CSharpEmitUnit(string? ModuleClassName, string Body);

/// <summary>
///     A C# emission split into the file preamble every unit needs (the auto-generated
///     marker, <c>#nullable enable</c>, the <c>using</c> directives and the file-scoped
///     <c>namespace</c>) and the units themselves.
/// </summary>
/// <remarks>
///     The units are slices of one emitter pass, not separate emissions, so
///     <see cref="ToSingleFile" /> reproduces <see cref="CSharpEmitter.Emit" />'s output
///     byte for byte.
/// </remarks>
public sealed record CSharpEmitUnits(string Header, IReadOnlyList<CSharpEmitUnit> Units)
{
    /// <summary>
    ///     <see cref="Header" /> followed by every <see cref="CSharpEmitUnit.Body" /> in
    ///     order: the whole program as one C# file.
    /// </summary>
    public string ToSingleFile()
    {
        return Header + string.Concat(Units.Select(u => u.Body));
    }

    /// <summary>
    ///     <see cref="Header" /> followed by one unit's <see cref="CSharpEmitUnit.Body" />:
    ///     that unit as a standalone C# file. The other half of the contract behind
    ///     <see cref="ToSingleFile" /> — the per-module files a project is split into are
    ///     assembled here so the boundary between header and body lives in one place.
    /// </summary>
    public string ToFile(CSharpEmitUnit unit)
    {
        return Header + unit.Body;
    }
}
