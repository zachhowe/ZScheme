namespace ZScript.Compiler.Pipeline;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

public sealed class Compilation
{
    private readonly CompilerOptions _options;
    private readonly DiagnosticBag _diagnostics = new();

    public Compilation(CompilerOptions? options = null)
    {
        _options = options ?? new CompilerOptions();
    }

    public DiagnosticBag Diagnostics => _diagnostics;

    public CompilationResult Compile(string source, string fileName = "input.zs")
    {
        // Stage 1: Lex
        var lexer = new Lexer(source, fileName, _diagnostics);
        var tokens = lexer.Tokenize();
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Stage 2: Parse S-expressions
        var parser = new SExprParser(tokens, _diagnostics);
        var sexprs = parser.ParseAll();
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Stage 3: Build AST
        var astBuilder = new AstBuilder(_diagnostics);
        var program = astBuilder.BuildProgram(sexprs);
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Extract namespace directive (if present) — source overrides options
        var nsDecls = program.TopLevelForms.OfType<AstNode.NamespaceDecl>().ToList();
        if (nsDecls.Count > 1)
            _diagnostics.Warning("Multiple namespace declarations; using the first one", nsDecls[1].Span);
        if (nsDecls.Count > 0)
            _options.Namespace = nsDecls[0].NsName;

        // Stage 4: Type inference
        var env = TypeEnv.CreateRoot();
        var inferer = new TypeInferer(_diagnostics);
        inferer.Infer(program, env);
        inferer.Resolve(program);
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Stage 5: Lower to IR
        var lowering = new IrLowering(_diagnostics);
        var ir = lowering.Lower(program);
        if (_diagnostics.HasErrors)
            return new CompilationResult(null, _diagnostics);

        // Stage 6: Code generation
        if (_options.OutputMode == OutputMode.CSharp)
        {
            var emitter = new CSharpEmitter(_options.Namespace);
            var csCode = emitter.Emit(ir);
            var typeDecls = emitter.EmitTypeDeclarations(ir);
            return new CompilationResult(typeDecls + csCode, _diagnostics);
        }

        // IL backend will go here
        return new CompilationResult(null, _diagnostics);
    }
}

public sealed record CompilationResult(string? Output, DiagnosticBag Diagnostics)
{
    public bool Success => !Diagnostics.HasErrors && Output is not null;
}
