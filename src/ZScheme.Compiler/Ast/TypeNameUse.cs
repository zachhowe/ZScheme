using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Ast;

/// <summary>
///     A type name written in a type position, recorded on <see cref="AstNode.Program" />
///     for type-name navigation. <paramref name="Name" /> is the atom text with a trailing
///     <c>?</c> stripped (the nullable suffix is not part of the name);
///     <paramref name="Arity" /> is the type-argument count, which is what
///     <c>TypeNameCanonicalizer.Canonical</c> needs to pick between <c>Foo</c> and
///     <c>Foo`n</c>.
/// </summary>
public sealed record TypeNameUse(string Name, SourceSpan Span, int Arity);
