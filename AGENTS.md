# ZScheme agent guide

ZScheme is a Scheme-like functional language that compiles to .NET. It uses
S-expressions, Hindley-Milner type inference, immutable collections, pattern
matching, tail-call optimization, `Result`/`Option`, and CLR interoperability.

## Repository rules

- Do not run any Git operation unless the user explicitly asks for it. When
  asked to commit, commit directly to the current branch, including `master`;
  do not create a branch unless asked.
- Do not run `format-all-cs.ps1`; it reformats much of the repository. Normal
  formatting happens at commit time.
- Do not repeatedly rerun build or test commands merely to search their output.
  Save one run's output to a temporary file and inspect that file as needed.
- The compiler and every package share one version. Use `bump-version.ps1` for
  normal version bumps; do not use `bump-package-version.ps1` except to repair
  a manifest that has already drifted.

## Verification

Use the narrowest relevant test first. Common commands are:

```powershell
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~MethodName"
pwsh ./run-all-tests.ps1
```

For compiler changes also run `pwsh ./run-package-tests.ps1`,
`pwsh ./run-package-csharp-tests.ps1`, and `pwsh ./build-examples.ps1` when
proportionate to the change. Package-test artifacts are in the gitignored
`packages/out/` directory.

## Architecture and conventions

`src/ZScheme.Compiler/Pipeline/Compilation.cs` orchestrates lexing, S-expression
parsing, AST building, module resolution, Hindley-Milner inference, IR lowering,
and C#/IL code generation. Read `docs/COMPILER-PIPELINE.md` before changing the
pipeline, and update it if the documented behavior changes.

- AST, IR, types, S-expressions, and tokens are sealed records.
- Use switch expressions with type patterns for node dispatch.
- Accumulate errors in `DiagnosticBag`; every AST/IR node has a `SourceSpan`.
- Keep `docs/TOOLCHAIN.md` current for toolchain/zsup changes, and
  `docs/FUZZER.md` current for fuzzer changes.
- Follow `docs/MOCKS.md` for mocks: no logic, only call recording,
  configurable results, event triggering, and `ClearTracking()`.

## Layout

- `src/ZScheme.Cli/`: CLI and REPL
- `src/ZScheme.Compiler/`: compiler implementation
- `src/ZScheme.Runtime/`: runtime support
- `src/ZScheme.Toolchain/` and `src/ZScheme.Zsup/`: toolchain management
- `src/ZScheme.Fuzzer/`: differential fuzzer
- `tests/ZScheme.Compiler.Tests/`: xUnit tests
- `packages/`: stdlib and packages; `examples/`: example programs

Project-specific workflows live in `.agents/skills/`.
