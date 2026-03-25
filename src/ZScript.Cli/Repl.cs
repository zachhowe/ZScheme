using ZScript.Compiler.Ast;
using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

namespace ZScript.Cli;

public sealed class Repl
{
    private readonly DiagnosticBag _diagnostics = new();
    private readonly TypeEnv _env = TypeEnv.CreateRoot();
    private readonly TypeInferer _inferer;

    public Repl()
    {
        _inferer = new TypeInferer(_diagnostics);
    }

    public void Run()
    {
        Console.WriteLine("ZScript REPL (type :quit to exit)");
        Console.WriteLine();

        while (true)
        {
            Console.Write("zs> ");
            var input = Console.ReadLine();

            if (input is null or ":quit" or ":q")
                break;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input == ":env")
            {
                Console.WriteLine("(environment listing not yet implemented)");
                continue;
            }

            Evaluate(input);
        }
    }

    private void Evaluate(string input)
    {
        var diag = new DiagnosticBag();

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

            // Parse
            var parser = new SExprParser(tokens, diag);
            var sexprs = parser.ParseAll();
            if (diag.HasErrors)
            {
                PrintDiagnostics(diag);
                return;
            }

            // Build AST
            var builder = new AstBuilder(diag);
            var program = builder.BuildProgram(sexprs);
            if (diag.HasErrors)
            {
                PrintDiagnostics(diag);
                return;
            }

            // Type check (using persistent env)
            foreach (var form in program.TopLevelForms)
            {
                var type = _inferer.Infer(form, _env);
                _inferer.Resolve(form);
                var resolved = _inferer.Substitution.Apply(type);

                // Lower and emit
                var lowering = new IrLowering(diag);
                var ir = lowering.Lower(form);
                var emitter = new CSharpEmitter(diag);
                var cs = emitter.Emit(ir);

                // Print the type and generated code
                Console.WriteLine($"  : {resolved}");

                if (form is AstNode.Define def)
                    Console.WriteLine($"  defined {def.FnName}");
                else if (form is AstNode.DefineValue dv)
                    Console.WriteLine($"  defined {dv.VarName}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
        }
    }

    private static void PrintDiagnostics(DiagnosticBag diag)
    {
        foreach (var d in diag.Diagnostics)
            Console.Error.WriteLine($"  {d}");
    }
}
