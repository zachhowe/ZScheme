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
///     <see cref="Header" /> concatenated with every <see cref="CSharpEmitUnit.Body" /> in
///     order reproduces <see cref="CSharpEmitter.Emit" />'s single-file output byte for
///     byte — the units are slices of one emitter pass, not separate emissions.
/// </remarks>
public sealed record CSharpEmitUnits(string Header, IReadOnlyList<CSharpEmitUnit> Units);
