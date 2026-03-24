using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Pipeline;
using ZScript.Compiler.Syntax;

namespace ZScript.Compiler.Package;

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
            diagnostics.Warning("Extra top-level forms after (package ...) are ignored", sexprs[1].Span);

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
        PackageDependencies? deps = null;
        BuildConfig? build = null;
        SourcePaths? sources = null;

        for (var i = 1; i < items.Count; i++)
        {
            if (items[i] is not SExpr.SList { Items: var sectionItems } section || sectionItems.Count == 0)
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
                case "dependencies":
                    deps = ParseDependencies(section);
                    break;
                case "build":
                    build = ParseBuildConfig(section);
                    break;
                case "sources":
                    sources = ParseSourcePaths(section);
                    break;
                default:
                    diagnostics.Warning($"Unknown package field: '{keyword.Text}'", keyword.Token.Span);
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
            name, version, entry, importPrefix, defaultModule,
            deps ?? new PackageDependencies([], []),
            build ?? new BuildConfig(null, null, null, null, []),
            sources, expr.Span);
    }

    private PackageDependencies ParseDependencies(SExpr.SList section)
    {
        var nuget = new List<NuGetDependency>();
        var zscript = new List<ZScriptDependency>();

        for (var i = 1; i < section.Items.Count; i++)
        {
            if (section.Items[i] is not SExpr.SList { Items: var subItems } sub || subItems.Count == 0)
            {
                diagnostics.Warning("Expected (nuget ...) or (zscript ...) section", section.Items[i].Span);
                continue;
            }

            if (subItems[0] is not SExpr.Atom { Kind: TokenKind.Symbol } keyword)
            {
                diagnostics.Warning("Expected 'nuget' or 'zscript'", subItems[0].Span);
                continue;
            }

            switch (keyword.Text)
            {
                case "nuget":
                    ParseNuGetDeps(sub, nuget);
                    break;
                case "zscript":
                    ParseZScriptDeps(sub, zscript);
                    break;
                default:
                    diagnostics.Warning($"Unknown dependency section: '{keyword.Text}'", keyword.Token.Span);
                    break;
            }
        }

        return new PackageDependencies(zscript, nuget);
    }

    private void ParseNuGetDeps(SExpr.SList section, List<NuGetDependency> result)
    {
        for (var i = 1; i < section.Items.Count; i++)
        {
            if (section.Items[i] is not SExpr.BracketList { Items: var items } bracket)
            {
                diagnostics.Error("Expected [PackageId \"version\"] for NuGet dependency", section.Items[i].Span);
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

    private void ParseZScriptDeps(SExpr.SList section, List<ZScriptDependency> result)
    {
        for (var i = 1; i < section.Items.Count; i++)
        {
            if (section.Items[i] is not SExpr.BracketList { Items: var items } bracket)
            {
                diagnostics.Error("Expected [name :source ...] for ZScript dependency", section.Items[i].Span);
                continue;
            }

            if (items.Count < 3)
            {
                diagnostics.Error("ZScript dependency must be [name :source args...]", bracket.Span);
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
                diagnostics.Error("Expected ':' before source type (e.g., :git or :local)", items[1].Span);
                continue;
            }

            if (items.Count < 4 || items[2] is not SExpr.Atom { Kind: TokenKind.Symbol } sourceTypeAtom)
            {
                diagnostics.Error("Expected source type after ':' (git or local)", bracket.Span);
                continue;
            }

            ZScriptDependencySource? source = sourceTypeAtom.Text switch
            {
                "git" => ParseGitSource(items, bracket.Span),
                "local" => ParseLocalSource(items, bracket.Span),
                _ => null
            };

            if (source is null)
            {
                if (sourceTypeAtom.Text is not "git" and not "local")
                    diagnostics.Error(
                        $"Unknown dependency source type: '{sourceTypeAtom.Text}' (expected 'git' or 'local')",
                        sourceTypeAtom.Token.Span);
                continue;
            }

            result.Add(new ZScriptDependency(nameAtom.Text, source, bracket.Span));
        }
    }

    private ZScriptDependencySource.Git? ParseGitSource(IReadOnlyList<SExpr> items, SourceSpan span)
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

        return new ZScriptDependencySource.Git(urlAtom.Text, versionAtom.Text);
    }

    private ZScriptDependencySource.Local? ParseLocalSource(IReadOnlyList<SExpr> items, SourceSpan span)
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

        return new ZScriptDependencySource.Local(pathAtom.Text);
    }

    private SourcePaths ParseSourcePaths(SExpr.SList section)
    {
        string? main = null;
        string? test = null;

        for (var i = 1; i < section.Items.Count; i++)
        {
            if (section.Items[i] is not SExpr.SList { Items: var fieldItems } field || fieldItems.Count < 2)
            {
                diagnostics.Warning("Expected (key \"value\") in sources section", section.Items[i].Span);
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
                    diagnostics.Warning($"Unknown sources field: '{keyword.Text}'", keyword.Token.Span);
                    break;
            }
        }

        return new SourcePaths(main, test);
    }

    private BuildConfig ParseBuildConfig(SExpr.SList section)
    {
        string? outputPath = null;
        OutputMode? backend = null;
        string? ns = null;
        string? stdlibPath = null;
        var refPaths = new List<string>();

        for (var i = 1; i < section.Items.Count; i++)
        {
            if (section.Items[i] is not SExpr.SList { Items: var fieldItems } field || fieldItems.Count < 2)
            {
                diagnostics.Warning("Expected (key \"value\") in build section", section.Items[i].Span);
                continue;
            }

            if (fieldItems[0] is not SExpr.Atom { Kind: TokenKind.Symbol } keyword)
            {
                diagnostics.Warning("Expected build field keyword", fieldItems[0].Span);
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
                        backend = backendStr == "il" ? OutputMode.IL : OutputMode.CSharp;
                    break;
                case "namespace":
                    ns = ExpectStringField(field, "namespace");
                    break;
                case "stdlib":
                    stdlibPath = ExpectStringField(field, "stdlib");
                    break;
                case "ref":
                    var refPath = ExpectStringField(field, "ref");
                    if (refPath is not null)
                        refPaths.Add(refPath);
                    break;
                default:
                    diagnostics.Warning($"Unknown build field: '{keyword.Text}'", keyword.Token.Span);
                    break;
            }
        }

        return new BuildConfig(outputPath, backend, ns, stdlibPath, refPaths);
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

        diagnostics.Error($"Expected a string value for build field '{fieldName}'", section.Items[1].Span);
        return null;
    }

    private static bool IsSymbol(SExpr expr, string name)
    {
        return expr is SExpr.Atom { Kind: TokenKind.Symbol } atom && atom.Text == name;
    }
}
