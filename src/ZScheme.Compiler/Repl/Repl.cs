using System.Diagnostics;
using Serilog;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Repl;

public sealed class Repl
{
    private readonly IReplConsole _console;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly TypeEnv _env = TypeEnv.CreateRoot();
    private readonly TypeInferer _inferer;

    public Repl() : this(new SystemConsole())
    {
    }

    public Repl(IReplConsole console)
    {
        _console = console;
        _inferer = new TypeInferer(_diagnostics);
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
        var diag = new DiagnosticBag();
        var sw = Stopwatch.StartNew();
        Log.Debug("REPL: evaluating input ({Length} chars)", input.Length);

        try
        {
            // Lex
            var lexer = new Lexer(input, "<repl>", diag);
            var tokens = lexer.Tokenize();
            if (diag.HasErrors)
            {
                PrintDiagnostics(diag);
                return;
            }

            Log.Debug("REPL lex: {TokenCount} tokens in {ElapsedMs}ms", tokens.Count, sw.ElapsedMilliseconds);

            // Parse
            sw.Restart();
            var parser = new SExprParser(tokens, diag);
            var sexprs = parser.ParseAll();
            if (diag.HasErrors)
            {
                PrintDiagnostics(diag);
                return;
            }

            Log.Debug("REPL parse: {SExprCount} s-expressions in {ElapsedMs}ms", sexprs.Count, sw.ElapsedMilliseconds);

            // Build AST
            sw.Restart();
            var builder = new AstBuilder(diag);
            var program = builder.BuildProgram(sexprs);
            if (diag.HasErrors)
            {
                PrintDiagnostics(diag);
                return;
            }

            Log.Debug("REPL AST: {FormCount} top-level forms in {ElapsedMs}ms", program.TopLevelForms.Count,
                sw.ElapsedMilliseconds);

            // Type check (using persistent env)
            foreach (var form in program.TopLevelForms)
            {
                var type = _inferer.Infer(form, _env);
                _inferer.Resolve(form);
                var resolved = _inferer.Substitution.Apply(type);

                // Lower and emit
                var lowering = new IrLowering(diag);
                var ir = lowering.Lower(form);
                var emitter = new CSharpEmitter(diag, "Repl", "ReplClass");
                var cs = emitter.Emit(ir);

                Log.Debug("REPL emit: type={ResolvedType}", resolved);

                // Print the type and generated code
                _console.WriteLine($"  : {resolved}");

                if (form is AstNode.Define def)
                    _console.WriteLine($"  defined {def.FnName}");
                else if (form is AstNode.DefineValue dv)
                    _console.WriteLine($"  defined {dv.VarName}");
            }
        }
        catch (Exception ex)
        {
            _console.WriteErrorLine($"Error: {ex.Message}");
        }
    }

    private void PrintDiagnostics(DiagnosticBag diag)
    {
        foreach (var d in diag.Diagnostics)
            _console.WriteErrorLine($"  {d}");
    }
}
