using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Serilog;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Repl;

public sealed class Repl
{
    private const string ReplModuleName = "__repl";
    private const string ReplClassName = "__replModule";
    private const string ReplNamespace = "ZSchemeGenerated";

    private static readonly ILogger Log = Serilog.Log.ForContext<Repl>();

    private readonly IReplConsole _console;
    private readonly List<string> _sessionSnippets = [];
    private int _resultCounter;

    public Repl()
        : this(new SystemConsole()) { }

    public Repl(IReplConsole console)
    {
        _console = console;
    }

    public void Run()
    {
        Log.Debug("REPL session started");
        _console.WriteLine("ZScheme REPL (type :quit to exit)");
        _console.WriteLine();

        while (true)
        {
            _console.Write("zs> ");
            var input = _console.ReadLine();

            if (input is null or ":quit" or ":q")
                break;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input == ":env")
            {
                _console.WriteLine("(environment listing not yet implemented)");
                continue;
            }

            Evaluate(input);
        }
    }

    private void Evaluate(string input)
    {
        var sw = Stopwatch.StartNew();
        Log.Debug("REPL: evaluating input ({Length} chars)", input.Length);

        try
        {
            // Pre-parse the input alone to identify top-level forms and decide which
            // to wrap as `(define __replResultN ...)` so we can read their value back.
            var parseDiag = new DiagnosticBag();
            var lexer = new Lexer(input, "<repl>", parseDiag);
            var tokens = lexer.Tokenize();
            if (parseDiag.HasErrors)
            {
                PrintDiagnostics(parseDiag);
                return;
            }

            var parser = new SExprParser(tokens, parseDiag);
            var sexprs = parser.ParseAll();
            if (parseDiag.HasErrors)
            {
                PrintDiagnostics(parseDiag);
                return;
            }

            var astBuilder = new AstBuilder(parseDiag);
            var program = astBuilder.BuildProgram(sexprs);
            if (parseDiag.HasErrors)
            {
                PrintDiagnostics(parseDiag);
                return;
            }

            if (program.TopLevelForms.Count == 0)
                return;

            // Canonicalize each top-level form: definitions kept verbatim, expressions
            // wrapped as `(define __replResultN <expr>)`. Track per-form info so we know
            // what to print after running the cctor.
            var newSnippets = new List<string>();
            var formInfos = new List<FormInfo>();

            foreach (var form in program.TopLevelForms)
            {
                var formText = SliceFormText(input, form.Span);
                if (IsDefinitionForm(form))
                {
                    newSnippets.Add(formText);
                    formInfos.Add(MakeDefinitionInfo(form));
                }
                else
                {
                    _resultCounter++;
                    var resultName = $"__replResult{_resultCounter}";
                    newSnippets.Add($"(define {resultName} {formText})");
                    formInfos.Add(new FormInfo(FormKind.Expression, resultName, null));
                }
            }

            // Build full program source: module header + prior session + new snippets.
            var fullSource = string.Join(
                "\n",
                new[] { $"(module {ReplModuleName})" }.Concat(_sessionSnippets).Concat(newSnippets)
            );

            Log.Debug(
                "REPL: compiling session ({Lines} lines, {Chars} chars)",
                _sessionSnippets.Count + newSnippets.Count,
                fullSource.Length
            );

            // Compile
            var compilation = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.Il,
                    Namespace = ReplNamespace,
                    SuppressVersionPreamble = true,
                }
            );
            var result = compilation.Compile(fullSource, "<repl>");

            if (result is not CompilationResult.IlOutputResult ilResult || !result.Success)
            {
                foreach (var d in result.Diagnostics.Diagnostics)
                    _console.WriteErrorLine($"  {d}");
                return;
            }

            Log.Debug(
                "REPL: compile succeeded in {ElapsedMs}ms ({Bytes} bytes)",
                sw.ElapsedMilliseconds,
                ilResult.OutputBytes.Length
            );

            // Load and run
            var asm = Assembly.Load(ilResult.OutputBytes);
            var replType = asm.GetType($"{ReplNamespace}.{ReplClassName}");
            if (replType is null)
            {
                _console.WriteErrorLine(
                    $"  REPL: could not locate generated type {ReplNamespace}.{ReplClassName}"
                );
                return;
            }

            try
            {
                RuntimeHelpers.RunClassConstructor(replType.TypeHandle);
            }
            catch (TypeInitializationException ex)
            {
                var inner = ex.InnerException ?? ex;
                _console.WriteErrorLine(
                    $"  Runtime error: {inner.GetType().Name}: {inner.Message}"
                );
                return;
            }

            // Print results for the new forms
            foreach (var info in formInfos)
                PrintFormResult(replType, info);

            // Commit session state (only on full success)
            _sessionSnippets.AddRange(newSnippets);
        }
        catch (Exception ex)
        {
            _console.WriteErrorLine($"Error: {ex.Message}");
        }
    }

    private void PrintFormResult(Type replType, FormInfo info)
    {
        switch (info.Kind)
        {
            case FormKind.Expression:
            {
                var value = ReadStaticField(replType, info.FieldName!);
                var (text, type) = FormatField(replType, info.FieldName!, value);
                if (type is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
                    return;
                _console.WriteLine($"  {text}");
                break;
            }
            case FormKind.DefineValue:
            {
                _console.WriteLine($"  defined {info.DisplayName}");
                var value = ReadStaticField(replType, info.FieldName!);
                if (value is not null)
                {
                    var (text, _) = FormatField(replType, info.FieldName!, value);
                    _console.WriteLine($"  {text}");
                }

                break;
            }
            case FormKind.DefineFunction:
                _console.WriteLine($"  defined {info.DisplayName}");
                break;
            case FormKind.DefineType:
                _console.WriteLine($"  defined {info.DisplayName}");
                break;
            case FormKind.Other:
                // Imports, module decls, namespace decls — nothing to print
                break;
        }
    }

    private static (string Text, ZType? Type) FormatField(
        Type replType,
        string fieldName,
        object? value
    )
    {
        // The field type carries the resolved CLR type; we don't have direct ZType info
        // post-emission, so we infer Unit from runtime type and otherwise format generically.
        var field = replType.GetField(
            fieldName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic
        );
        if (field is null)
            return (ReplValueFormatter.Format(value), null);

        var clrType = field.FieldType;
        ZType? zType = null;
        // Unit in ZScheme maps to System.ValueTuple.
        if (clrType == typeof(ValueTuple))
            zType = ZType.Unit;

        return (ReplValueFormatter.Format(value, zType), zType);
    }

    private static object? ReadStaticField(Type replType, string fieldName)
    {
        var field = replType.GetField(
            fieldName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic
        );
        return field?.GetValue(null);
    }

    private static FormInfo MakeDefinitionInfo(AstNode form)
    {
        return form switch
        {
            AstNode.Define def => new FormInfo(FormKind.DefineFunction, null, def.FnName),
            AstNode.DefineAsync da => new FormInfo(FormKind.DefineFunction, null, da.FnName),
            AstNode.DefineValue dv =>
            // The emitted static field name is the sanitized VarName (matching the
            // module class emitted by the backend); the display name stays raw.
            new FormInfo(
                FormKind.DefineValue,
                NameConverter.SanitizeIdentifier(dv.VarName),
                dv.VarName
            ),
            AstNode.RecordDecl rd => new FormInfo(FormKind.DefineType, null, rd.RecordName),
            AstNode.UnionDecl ud => new FormInfo(FormKind.DefineType, null, ud.UnionName),
            AstNode.ClassDecl cd => new FormInfo(FormKind.DefineType, null, cd.ClassName),
            AstNode.InterfaceDecl id => new FormInfo(FormKind.DefineType, null, id.InterfaceName),
            AstNode.TypeAliasDecl ad => new FormInfo(FormKind.DefineType, null, ad.AliasName),
            _ => new FormInfo(FormKind.Other, null, null),
        };
    }

    private static bool IsDefinitionForm(AstNode form)
    {
        return form
            is AstNode.Define
                or AstNode.DefineValue
                or AstNode.DefineAsync
                or AstNode.RecordDecl
                or AstNode.UnionDecl
                or AstNode.ClassDecl
                or AstNode.InterfaceDecl
                or AstNode.TypeAliasDecl
                or AstNode.Import
                or AstNode.ImportClr
                or AstNode.ModuleDecl
                or AstNode.NamespaceDecl
                or AstNode.Export;
    }

    // Slice the source text covered by a top-level form's span. The lexer/parser
    // emit Length spans only for single-line forms reliably; for multi-line forms
    // we fall back to taking everything from the form's start to end of input,
    // which is still correct for the typical "one form per REPL line" case.
    private static string SliceFormText(string input, SourceSpan span)
    {
        var startOffset = LineColumnToOffset(input, span.Line, span.Column);
        if (startOffset < 0)
            return input; // span out of range — return whole input as fallback

        // Try to use the Length field if it produces a sensible end offset.
        if (span.Length > 0 && startOffset + span.Length <= input.Length)
        {
            var candidate = input.Substring(startOffset, span.Length);
            // Only trust Length if it parses to a balanced form. For multi-line
            // forms the parser computes Length via column arithmetic which
            // underflows; in that case prefer "rest of input".
            if (LooksBalanced(candidate))
                return candidate;
        }

        return input.Substring(startOffset);
    }

    private static int LineColumnToOffset(string source, int line, int column)
    {
        if (line < 1 || column < 1)
            return -1;
        var currentLine = 1;
        var currentCol = 1;
        for (var i = 0; i < source.Length; i++)
        {
            if (currentLine == line && currentCol == column)
                return i;
            if (source[i] == '\n')
            {
                currentLine++;
                currentCol = 1;
            }
            else
            {
                currentCol++;
            }
        }

        if (currentLine == line && currentCol == column)
            return source.Length;
        return -1;
    }

    private static bool LooksBalanced(string text)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    i++;
                    continue;
                }

                if (c == '"')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    if (depth < 0)
                        return false;
                    break;
            }
        }

        return depth == 0 && !inString;
    }

    private void PrintDiagnostics(DiagnosticBag diag)
    {
        foreach (var d in diag.Diagnostics)
            _console.WriteErrorLine($"  {d}");
    }

    private enum FormKind
    {
        Expression,
        DefineValue,
        DefineFunction,
        DefineType,
        Other,
    }

    private sealed record FormInfo(FormKind Kind, string? FieldName, string? DisplayName);
}
