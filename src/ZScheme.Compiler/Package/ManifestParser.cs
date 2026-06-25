using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Package;

public sealed class ManifestParser(DiagnosticBag diagnostics)
{
    public PackageManifest? Parse(string source, string fileName = "package.zspkg")
    {
        var lexer = new Lexer(source, fileName, diagnostics);
        var tokens = lexer.Tokenize();
        if (diagnostics.HasErrors)
            return null;

        var parser = new SExprParser(tokens, diagnostics);
        var sexprs = parser.ParseAll();
        if (diagnostics.HasErrors)
            return null;

        if (sexprs.Count == 0)
        {
            diagnostics.Error("Expected (package ...) form", SourceSpan.None);
            return null;
        }

        if (sexprs.Count > 1)
            diagnostics.Warning(
                "Extra top-level forms after (package ...) are ignored",
                sexprs[1].Span
            );

        return ParsePackage(sexprs[0]);
    }

    private PackageManifest? ParsePackage(SExpr expr)
    {
        if (expr is not SExpr.SList { Items: var items })
        {
            diagnostics.Error("Expected (package ...) form", expr.Span);
            return null;
        }

        if (items.Count == 0 || !IsSymbol(items[0], "package"))
        {
            diagnostics.Error("Expected (package ...) form", expr.Span);
            return null;
        }

        string? name = null;
        string? version = null;
        string? entry = null;
        string? importPrefix = null;
        string? defaultModule = null;
        string? description = null;
        string? license = null;
        PackageDependencies? deps = null;
        PackageDependencies? testDeps = null;
        BuildConfig? build = null;
        SourcePaths? sources = null;
        var bundleSource = false;

        for (var i = 1; i < items.Count; i++)
        {
            if (
                items[i] is not SExpr.SList { Items: var sectionItems } section
                || sectionItems.Count == 0
            )
            {
                diagnostics.Warning("Expected a section form like (name ...)", items[i].Span);
                continue;
            }

            if (sectionItems[0] is not SExpr.Atom { Kind: TokenKind.Symbol } keyword)
            {
                diagnostics.Warning("Expected a keyword", sectionItems[0].Span);
                continue;
            }

            switch (keyword.Text)
            {
                case "name":
                    name = ExpectString(section, "name");
                    break;
                case "version":
                    version = ExpectString(section, "version");
                    break;
                case "entry":
                    entry = ExpectString(section, "entry");
                    break;
                case "import-prefix":
                    importPrefix = ExpectString(section, "import-prefix");
                    break;
                case "default-module":
                    defaultModule = ExpectString(section, "default-module");
                    break;
                case "description":
                    description = ExpectString(section, "description");
                    break;
                case "license":
                    license = ExpectString(section, "license");
                    break;
                case "dependencies":
                    deps = ParseDependencies(section);
                    break;
                case "test-dependencies":
                    testDeps = ParseDependencies(section);
                    break;
                case "build":
                    build = ParseBuildConfig(section);
                    break;
                case "sources":
                    sources = ParseSourcePaths(section);
                    break;
                case "bundle-source":
                    bundleSource = ExpectBool(section, "bundle-source") ?? false;
                    break;
                default:
                    diagnostics.Warning(
                        $"Unknown package field: '{keyword.Text}'",
                        keyword.Token.Span
                    );
                    break;
            }
        }

        if (name is null)
        {
            diagnostics.Error("Missing required field: name", expr.Span);
            return null;
        }

        if (version is null)
        {
            diagnostics.Error("Missing required field: version", expr.Span);
            return null;
        }

        return new PackageManifest(
            name,
            version,
            entry,
            importPrefix,
            defaultModule,
            description,
            license,
            deps ?? new PackageDependencies([], []),
            testDeps ?? new PackageDependencies([], []),
            build ?? new BuildConfig(null, null),
            sources,
            expr.Span,
            bundleSource
        );
    }

    private PackageDependencies ParseDependencies(SExpr.SList section)
    {
        var nuget = new List<NuGetDependency>();
        var zscheme = new List<ZSchemeDependency>();
        var frameworks = new List<FrameworkDependency>();

        for (var i = 1; i < section.Items.Count; i++)
        {
            if (
                section.Items[i] is not SExpr.SList { Items: var subItems } sub
                || subItems.Count == 0
            )
            {
                diagnostics.Warning(
                    "Expected (nuget ...), (zscheme ...), or (framework ...) section",
                    section.Items[i].Span
                );
                continue;
            }

            if (subItems[0] is not SExpr.Atom { Kind: TokenKind.Symbol } keyword)
            {
                diagnostics.Warning(
                    "Expected 'nuget', 'zscheme', or 'framework'",
                    subItems[0].Span
                );
                continue;
            }

            switch (keyword.Text)
            {
                case "nuget":
                    ParseNuGetDeps(sub, nuget);
                    break;
                case "zscheme":
                    ParseZSchemeDeps(sub, zscheme);
                    break;
                case "framework":
                    ParseFrameworkDeps(sub, frameworks);
                    break;
                default:
                    diagnostics.Warning(
                        $"Unknown dependency section: '{keyword.Text}'",
                        keyword.Token.Span
                    );
                    break;
            }
        }

        return new PackageDependencies(zscheme, nuget, frameworks);
    }

    private void ParseFrameworkDeps(SExpr.SList section, List<FrameworkDependency> result)
    {
        // Accepts (framework Microsoft.AspNetCore.App Microsoft.WindowsDesktop.App ...)
        // — one symbol per framework id.
        for (var i = 1; i < section.Items.Count; i++)
        {
            if (section.Items[i] is not SExpr.Atom { Kind: TokenKind.Symbol } idAtom)
            {
                diagnostics.Error(
                    "Expected a framework id symbol (e.g. Microsoft.AspNetCore.App)",
                    section.Items[i].Span
                );
                continue;
            }

            result.Add(new FrameworkDependency(idAtom.Text, idAtom.Token.Span));
        }
    }

    private void ParseNuGetDeps(SExpr.SList section, List<NuGetDependency> result)
    {
        for (var i = 1; i < section.Items.Count; i++)
        {
            if (section.Items[i] is not SExpr.BracketList { Items: var items } bracket)
            {
                diagnostics.Error(
                    "Expected [PackageId \"version\"] for NuGet dependency",
                    section.Items[i].Span
                );
                continue;
            }

            if (items.Count != 2)
            {
                diagnostics.Error("NuGet dependency must be [PackageId \"version\"]", bracket.Span);
                continue;
            }

            if (items[0] is not SExpr.Atom { Kind: TokenKind.Symbol } pkgAtom)
            {
                diagnostics.Error("Expected package ID symbol", items[0].Span);
                continue;
            }

            if (items[1] is not SExpr.Atom { Kind: TokenKind.StringLit } versionAtom)
            {
                diagnostics.Error("Expected version string", items[1].Span);
                continue;
            }

            result.Add(new NuGetDependency(pkgAtom.Text, versionAtom.Text, bracket.Span));
        }
    }

    private void ParseZSchemeDeps(SExpr.SList section, List<ZSchemeDependency> result)
    {
        for (var i = 1; i < section.Items.Count; i++)
        {
            if (section.Items[i] is not SExpr.BracketList { Items: var items } bracket)
            {
                diagnostics.Error(
                    "Expected [name :source ...] for ZScheme dependency",
                    section.Items[i].Span
                );
                continue;
            }

            if (items.Count < 3)
            {
                diagnostics.Error(
                    "ZScheme dependency must be [name :source args...]",
                    bracket.Span
                );
                continue;
            }

            if (items[0] is not SExpr.Atom { Kind: TokenKind.Symbol } nameAtom)
            {
                diagnostics.Error("Expected dependency name symbol", items[0].Span);
                continue;
            }

            // Expect colon token followed by source type symbol
            if (items[1] is not SExpr.Atom { Kind: TokenKind.Colon })
            {
                diagnostics.Error(
                    "Expected ':' before source type (e.g., :git or :local)",
                    items[1].Span
                );
                continue;
            }

            if (
                items.Count < 4
                || items[2] is not SExpr.Atom { Kind: TokenKind.Symbol } sourceTypeAtom
            )
            {
                diagnostics.Error("Expected source type after ':' (git or local)", bracket.Span);
                continue;
            }

            ZSchemeDependencySource? source = sourceTypeAtom.Text switch
            {
                "git" => ParseGitSource(items, bracket.Span),
                "local" => ParseLocalSource(items, bracket.Span),
                _ => null,
            };

            if (source is null)
            {
                if (sourceTypeAtom.Text is not "git" and not "local")
                    diagnostics.Error(
                        $"Unknown dependency source type: '{sourceTypeAtom.Text}' (expected 'git' or 'local')",
                        sourceTypeAtom.Token.Span
                    );
                continue;
            }

            result.Add(new ZSchemeDependency(nameAtom.Text, source, bracket.Span));
        }
    }

    private ZSchemeDependencySource.Git? ParseGitSource(IReadOnlyList<SExpr> items, SourceSpan span)
    {
        // [name :git "url" "version"]
        if (items.Count < 5)
        {
            diagnostics.Error("Git dependency must be [name :git \"url\" \"version\"]", span);
            return null;
        }

        if (items[3] is not SExpr.Atom { Kind: TokenKind.StringLit } urlAtom)
        {
            diagnostics.Error("Expected URL string for git dependency", items[3].Span);
            return null;
        }

        if (items[4] is not SExpr.Atom { Kind: TokenKind.StringLit } versionAtom)
        {
            diagnostics.Error("Expected version string for git dependency", items[4].Span);
            return null;
        }

        return new ZSchemeDependencySource.Git(urlAtom.Text, versionAtom.Text);
    }

    private ZSchemeDependencySource.Local? ParseLocalSource(
        IReadOnlyList<SExpr> items,
        SourceSpan span
    )
    {
        // [name :local "path"]
        if (items.Count < 4)
        {
            diagnostics.Error("Local dependency must be [name :local \"path\"]", span);
            return null;
        }

        if (items[3] is not SExpr.Atom { Kind: TokenKind.StringLit } pathAtom)
        {
            diagnostics.Error("Expected path string for local dependency", items[3].Span);
            return null;
        }

        return new ZSchemeDependencySource.Local(pathAtom.Text);
    }

    private SourcePaths ParseSourcePaths(SExpr.SList section)
    {
        string? main = null;
        string? test = null;

        for (var i = 1; i < section.Items.Count; i++)
        {
            if (
                section.Items[i] is not SExpr.SList { Items: var fieldItems } field
                || fieldItems.Count < 2
            )
            {
                diagnostics.Warning(
                    "Expected (key \"value\") in sources section",
                    section.Items[i].Span
                );
                continue;
            }

            if (fieldItems[0] is not SExpr.Atom { Kind: TokenKind.Symbol } keyword)
            {
                diagnostics.Warning("Expected sources field keyword", fieldItems[0].Span);
                continue;
            }

            switch (keyword.Text)
            {
                case "main":
                    main = ExpectStringField(field, "main");
                    break;
                case "test":
                    test = ExpectStringField(field, "test");
                    break;
                default:
                    diagnostics.Warning(
                        $"Unknown sources field: '{keyword.Text}'",
                        keyword.Token.Span
                    );
                    break;
            }
        }

        return new SourcePaths(main, test);
    }

    private BuildConfig ParseBuildConfig(SExpr.SList section)
    {
        MainBuildConfig? main = null;
        TestBuildConfig? test = null;

        for (var i = 1; i < section.Items.Count; i++)
        {
            if (
                section.Items[i] is not SExpr.SList { Items: var subItems } sub
                || subItems.Count == 0
            )
            {
                diagnostics.Warning(
                    "Expected (main ...) or (test ...) subsection in build section",
                    section.Items[i].Span
                );
                continue;
            }

            if (subItems[0] is not SExpr.Atom { Kind: TokenKind.Symbol } keyword)
            {
                diagnostics.Warning("Expected 'main' or 'test'", subItems[0].Span);
                continue;
            }

            switch (keyword.Text)
            {
                case "main":
                    main = ParseMainBuildConfig(sub);
                    break;
                case "test":
                    test = ParseTestBuildConfig(sub);
                    break;
                case "output":
                case "backend":
                case "namespace":
                case "ref":
                case "stdlib":
                case "sdk":
                case "output-type":
                    diagnostics.Error(
                        $"Build field '{keyword.Text}' must be nested under (main ...) or (test ...)",
                        keyword.Token.Span
                    );
                    break;
                default:
                    diagnostics.Warning(
                        $"Unknown build subsection: '{keyword.Text}'",
                        keyword.Token.Span
                    );
                    break;
            }
        }

        return new BuildConfig(main, test);
    }

    private MainBuildConfig ParseMainBuildConfig(SExpr.SList section)
    {
        string? outputPath = null;
        OutputMode? backend = null;
        string? ns = null;
        var refPaths = new List<string>();
        string? sdk = null;
        string? outputType = null;
        bool? warnUnusedParams = null;
        bool? warnUnloopedRecursion = null;

        for (var i = 1; i < section.Items.Count; i++)
        {
            if (
                section.Items[i] is not SExpr.SList { Items: var fieldItems } field
                || fieldItems.Count < 2
            )
            {
                diagnostics.Warning(
                    "Expected (key \"value\") in main build section",
                    section.Items[i].Span
                );
                continue;
            }

            if (fieldItems[0] is not SExpr.Atom { Kind: TokenKind.Symbol } keyword)
            {
                diagnostics.Warning("Expected main build field keyword", fieldItems[0].Span);
                continue;
            }

            switch (keyword.Text)
            {
                case "output":
                    outputPath = ExpectStringField(field, "output");
                    break;
                case "backend":
                    var backendStr = ExpectStringField(field, "backend");
                    if (backendStr is not null)
                        backend = backendStr == "il" ? OutputMode.Il : OutputMode.CSharp;
                    break;
                case "namespace":
                    ns = ExpectStringField(field, "namespace");
                    break;
                case "stdlib":
                    diagnostics.Warning(
                        "The (stdlib ...) build field is deprecated; use --package-path instead",
                        keyword.Token.Span
                    );
                    break;
                case "ref":
                    var refPath = ExpectStringField(field, "ref");
                    if (refPath is not null)
                        refPaths.Add(refPath);
                    break;
                case "sdk":
                    sdk = ExpectStringField(field, "sdk");
                    break;
                case "output-type":
                    outputType = ExpectStringField(field, "output-type");
                    break;
                case "warn-unused-params":
                    var warnStr = ExpectStringField(field, "warn-unused-params");
                    if (warnStr is not null)
                    {
                        if (warnStr is "true" or "false")
                            warnUnusedParams = warnStr == "true";
                        else
                            diagnostics.Warning(
                                "warn-unused-params must be \"true\" or \"false\"",
                                field.Span
                            );
                    }

                    break;
                case "warn-unlooped-recursion":
                    var warnRecStr = ExpectStringField(field, "warn-unlooped-recursion");
                    if (warnRecStr is not null)
                    {
                        if (warnRecStr is "true" or "false")
                            warnUnloopedRecursion = warnRecStr == "true";
                        else
                            diagnostics.Warning(
                                "warn-unlooped-recursion must be \"true\" or \"false\"",
                                field.Span
                            );
                    }

                    break;
                default:
                    diagnostics.Warning(
                        $"Unknown main build field: '{keyword.Text}'",
                        keyword.Token.Span
                    );
                    break;
            }
        }

        return new MainBuildConfig(
            outputPath,
            backend,
            ns,
            refPaths,
            sdk,
            outputType,
            warnUnusedParams,
            warnUnloopedRecursion
        );
    }

    private TestBuildConfig ParseTestBuildConfig(SExpr.SList section)
    {
        string? outputPath = null;
        string? ns = null;
        var refPaths = new List<string>();

        for (var i = 1; i < section.Items.Count; i++)
        {
            if (
                section.Items[i] is not SExpr.SList { Items: var fieldItems } field
                || fieldItems.Count < 2
            )
            {
                diagnostics.Warning(
                    "Expected (key \"value\") in test build section",
                    section.Items[i].Span
                );
                continue;
            }

            if (fieldItems[0] is not SExpr.Atom { Kind: TokenKind.Symbol } keyword)
            {
                diagnostics.Warning("Expected test build field keyword", fieldItems[0].Span);
                continue;
            }

            switch (keyword.Text)
            {
                case "output":
                    outputPath = ExpectStringField(field, "output");
                    break;
                case "namespace":
                    ns = ExpectStringField(field, "namespace");
                    break;
                case "ref":
                    var refPath = ExpectStringField(field, "ref");
                    if (refPath is not null)
                        refPaths.Add(refPath);
                    break;
                case "backend":
                    diagnostics.Warning(
                        "'backend' is not supported in test build config; tests are always compiled as IL",
                        keyword.Token.Span
                    );
                    break;
                default:
                    diagnostics.Warning(
                        $"Unknown test build field: '{keyword.Text}'",
                        keyword.Token.Span
                    );
                    break;
            }
        }

        return new TestBuildConfig(outputPath, ns, refPaths);
    }

    private string? ExpectString(SExpr.SList section, string fieldName)
    {
        if (section.Items.Count < 2)
        {
            diagnostics.Error($"Expected a value for '{fieldName}'", section.Span);
            return null;
        }

        if (section.Items[1] is SExpr.Atom { Kind: TokenKind.StringLit } strAtom)
            return strAtom.Text;

        diagnostics.Error($"Expected a string value for '{fieldName}'", section.Items[1].Span);
        return null;
    }

    private string? ExpectStringField(SExpr.SList section, string fieldName)
    {
        if (section.Items.Count < 2)
        {
            diagnostics.Error($"Expected a value for build field '{fieldName}'", section.Span);
            return null;
        }

        if (section.Items[1] is SExpr.Atom { Kind: TokenKind.StringLit } strAtom)
            return strAtom.Text;

        diagnostics.Error(
            $"Expected a string value for build field '{fieldName}'",
            section.Items[1].Span
        );
        return null;
    }

    private bool? ExpectBool(SExpr.SList section, string fieldName)
    {
        if (section.Items.Count < 2)
        {
            diagnostics.Error($"Expected a value for '{fieldName}'", section.Span);
            return null;
        }

        if (section.Items[1] is SExpr.Atom { Kind: TokenKind.Symbol } sym)
        {
            if (sym.Text is "true" or "#t")
                return true;
            if (sym.Text is "false" or "#f")
                return false;
        }

        diagnostics.Error($"Expected 'true' or 'false' for '{fieldName}'", section.Items[1].Span);
        return null;
    }

    private static bool IsSymbol(SExpr expr, string name)
    {
        return expr is SExpr.Atom { Kind: TokenKind.Symbol } atom && atom.Text == name;
    }
}
