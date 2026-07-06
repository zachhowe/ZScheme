using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Code-coverage instrumentation for the IL backend. When enabled, a stack-neutral probe
///     (<c>ldc.i4 &lt;id&gt;; call ZScheme.Runtime.ZSchemeCoverage.Hit(int)</c>) is woven before
///     each executable and branch IR node. <c>ZSchemeCoverage</c>'s <c>Hit</c> method and its
///     <c>Hits</c>/<c>Meta</c> fields are imported from <c>ZScheme.Runtime.dll</c> (the same way
///     <c>ZSymbol.Intern</c> is imported elsewhere) rather than synthesized per compilation; a
///     module-initializer <c>.cctor</c> added to this assembly's <c>&lt;Module&gt;</c> type sizes
///     <c>Hits</c> and sets <c>Meta</c> for this specific program before any probe fires. The
///     <c>zs</c> toolchain reads the counters and metadata back via reflection to produce a
///     Cobertura report.
/// </summary>
public sealed partial class IlEmitter
{
    private readonly Dictionary<
        (SourceSpan Span, CoverageKind Kind, int Ordinal),
        int
    > _coveragePointIds = new();
    private readonly List<CoveragePoint> _coveragePoints = [];

    private IFieldDescriptor? _coverageHitsField;
    private IMethodDescriptor? _coverageHitMethod;
    private IFieldDescriptor? _coverageMetaField;
    private string[]? _coverageNormalizedPrefixes;

    private bool CoverageEnabled => _coverage is { Enabled: true };

    /// <summary>
    ///     Imports <c>ZScheme.Runtime.ZSchemeCoverage</c>'s <c>Hit(int)</c> method and its
    ///     <c>Hits</c>/<c>Meta</c> fields up-front, so probes can reference <c>Hit</c> while bodies
    ///     are still being emitted. The array size and metadata string are filled in by
    ///     <see cref="FinalizeCoverage" /> once every point is known.
    /// </summary>
    private void InitCoverage()
    {
        if (!CoverageEnabled)
            return;

        _coverageHitsField = _module.DefaultImporter.ImportField(
            typeof(Runtime.ZSchemeCoverage).GetField(nameof(Runtime.ZSchemeCoverage.Hits))!
        );
        _coverageMetaField = _module.DefaultImporter.ImportField(
            typeof(Runtime.ZSchemeCoverage).GetField(nameof(Runtime.ZSchemeCoverage.Meta))!
        );
        _coverageHitMethod = _module.DefaultImporter.ImportMethod(
            typeof(Runtime.ZSchemeCoverage).GetMethod(
                nameof(Runtime.ZSchemeCoverage.Hit),
                [typeof(int)]
            )!
        );
    }

    /// <summary>
    ///     Adds a <c>.cctor</c> to this module's <c>&lt;Module&gt;</c> type (the IL-level
    ///     equivalent of a C# <c>[ModuleInitializer]</c>) that allocates <c>ZSchemeCoverage.Hits</c>
    ///     sized to the final point count and stores the packed metadata string in
    ///     <c>ZSchemeCoverage.Meta</c>. The CLR runs a module's <c>&lt;Module&gt;</c> static
    ///     constructor before any other method in that module executes, so this always runs before
    ///     the first probe call, regardless of which function in the program runs first. Called
    ///     just before the module is serialized, once every coverage point is known.
    /// </summary>
    private void FinalizeCoverage()
    {
        if (!CoverageEnabled)
            return;

        var moduleType = _module.GetOrCreateModuleType();

        var cctor = new MethodDefinition(
            ".cctor",
            MethodAttributes.Static
                | MethodAttributes.Private
                | MethodAttributes.HideBySig
                | MethodAttributes.SpecialName
                | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateStatic(_module.CorLibTypeFactory.Void)
        );
        moduleType.Methods.Add(cctor);

        var body = new CilMethodBody();
        cctor.MethodBody = body;
        var il = body.Instructions;
        il.Add(CilOpCodes.Ldc_I4, _coveragePoints.Count);
        il.Add(CilOpCodes.Newarr, _module.CorLibTypeFactory.Int32.ToTypeDefOrRef());
        il.Add(CilOpCodes.Stsfld, _coverageHitsField!);
        il.Add(CilOpCodes.Ldstr, CoverageContract.SerializeMeta(_coveragePoints));
        il.Add(CilOpCodes.Stsfld, _coverageMetaField!);
        il.Add(CilOpCodes.Ret);
    }

    /// <summary>
    ///     IR nodes that represent a meaningful executable statement/expression and so receive a
    ///     line probe at the top of <see cref="EmitNode" />. Literals, plain variable reads,
    ///     sequences, closures, and decision-tree internals are excluded — their enclosing nodes
    ///     (or the per-line OR at report time) already account for them.
    /// </summary>
    private static bool IsLineProbeNode(IrNode node) =>
        node switch
        {
            IrNode.Call
            or IrNode.ClrCall
            or IrNode.MethodCall
            or IrNode.SuperMethodCall
            or IrNode.BinOp
            or IrNode.UnaryOp
            or IrNode.If
            or IrNode.Match
            or IrNode.Let
            or IrNode.Use
            or IrNode.Throw
            or IrNode.Await
            or IrNode.RecordNew
            or IrNode.UnionCaseNew
            or IrNode.TupleNew
            or IrNode.RecordWith
            or IrNode.FieldGet
            or IrNode.SetField
            or IrNode.MutableArrayNew
            or IrNode.ClrNew => true,
            _ => false,
        };

    /// <summary>
    ///     Prepends a stack-neutral coverage probe for <paramref name="span" /> to the current
    ///     instruction stream. The probe pushes the point id and immediately consumes it via the
    ///     <c>void Hit(int)</c> call, so it is safe to insert at any position — even mid-expression
    ///     with other values already on the stack.
    /// </summary>
    private void EmitCoverageProbe(
        SourceSpan span,
        CoverageKind kind,
        int ordinal,
        CilInstructionCollection il
    )
    {
        if (!CoverageEnabled || !CoverageInScope(span))
            return;

        var key = (span, kind, ordinal);
        if (!_coveragePointIds.TryGetValue(key, out var id))
        {
            id = _coveragePoints.Count;
            _coveragePoints.Add(
                new CoveragePoint(span.File, span.Line, span.Column, span.Length, kind, ordinal)
            );
            _coveragePointIds[key] = id;
        }

        il.Add(CilOpCodes.Ldc_I4, id);
        il.Add(CilOpCodes.Call, _coverageHitMethod!);
    }

    private bool CoverageInScope(SourceSpan span)
    {
        if (string.IsNullOrEmpty(span.File) || span.Line <= 0)
            return false;

        _coverageNormalizedPrefixes ??= (_coverage!.IncludePathPrefixes ?? [])
            .Select(p =>
            {
                string full;
                try
                {
                    full = Path.GetFullPath(p);
                }
                catch
                {
                    full = p;
                }

                return full.EndsWith(Path.DirectorySeparatorChar)
                    ? full
                    : full + Path.DirectorySeparatorChar;
            })
            .ToArray();

        if (_coverageNormalizedPrefixes.Length == 0)
            return true;

        string file;
        try
        {
            file = Path.GetFullPath(span.File);
        }
        catch
        {
            file = span.File;
        }

        return _coverageNormalizedPrefixes.Any(p =>
            file.StartsWith(p, StringComparison.OrdinalIgnoreCase)
        );
    }
}
