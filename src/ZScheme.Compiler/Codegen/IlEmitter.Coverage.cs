using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Code-coverage instrumentation for the IL backend. When enabled, a stack-neutral probe
///     (<c>ldc.i4 &lt;id&gt;; call __ZSchemeCoverage.Hit(int)</c>) is woven before each executable
///     and branch IR node, and a self-contained <c>__ZSchemeCoverage</c> class (a hit-count
///     <c>int[]</c>, a packed metadata <c>string</c>, and the <c>Hit</c> method) is baked into the
///     same assembly. No external runtime library is referenced — the <c>zs</c> toolchain reads
///     the counters and metadata back via reflection to produce a Cobertura report.
/// </summary>
public sealed partial class IlEmitter
{
    private readonly Dictionary<
        (SourceSpan Span, CoverageKind Kind, int Ordinal),
        int
    > _coveragePointIds = new();
    private readonly List<CoveragePoint> _coveragePoints = [];

    private FieldDefinition? _coverageHitsField;
    private MethodDefinition? _coverageHitMethod;
    private FieldDefinition? _coverageMetaField;
    private string[]? _coverageNormalizedPrefixes;
    private TypeDefinition? _coverageType;

    private bool CoverageEnabled => _coverage is { Enabled: true };

    /// <summary>
    ///     Creates the <c>__ZSchemeCoverage</c> type with its <c>Hits</c>/<c>Meta</c> fields and the
    ///     <c>Hit(int)</c> probe method up-front, so probes can reference <c>Hit</c> while bodies are
    ///     still being emitted. The array size and metadata string are filled in by
    ///     <see cref="FinalizeCoverage" /> once every point is known.
    /// </summary>
    private void InitCoverage()
    {
        if (!CoverageEnabled)
            return;

        var intArray = _module.CorLibTypeFactory.Int32.MakeSzArrayType();

        _coverageType = new TypeDefinition(
            _ilNamespace,
            CoverageContract.TypeName,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
        )
        {
            BaseType = _module.CorLibTypeFactory.Object.ToTypeDefOrRef(),
        };
        _module.TopLevelTypes.Add(_coverageType);

        _coverageHitsField = new FieldDefinition(
            CoverageContract.HitsField,
            FieldAttributes.Public | FieldAttributes.Static,
            new FieldSignature(intArray)
        );
        _coverageType.Fields.Add(_coverageHitsField);

        _coverageMetaField = new FieldDefinition(
            CoverageContract.MetaField,
            FieldAttributes.Public | FieldAttributes.Static,
            new FieldSignature(_module.CorLibTypeFactory.String)
        );
        _coverageType.Fields.Add(_coverageMetaField);

        _coverageHitMethod = new MethodDefinition(
            CoverageContract.HitMethod,
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(
                _module.CorLibTypeFactory.Void,
                [_module.CorLibTypeFactory.Int32]
            )
        );
        _coverageHitMethod.ParameterDefinitions.Add(new ParameterDefinition(1, "id", 0));
        _coverageType.Methods.Add(_coverageHitMethod);

        // void Hit(int id) { Hits[id] = Hits[id] + 1; }
        var body = new CilMethodBody();
        _coverageHitMethod.MethodBody = body;
        var il = body.Instructions;
        il.Add(CilOpCodes.Ldsfld, _coverageHitsField);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldsfld, _coverageHitsField);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldelem_I4);
        il.Add(CilOpCodes.Ldc_I4_1);
        il.Add(CilOpCodes.Add);
        il.Add(CilOpCodes.Stelem_I4);
        il.Add(CilOpCodes.Ret);
    }

    /// <summary>
    ///     Emits the <c>__ZSchemeCoverage</c> static constructor that allocates the hit array sized
    ///     to the final point count and stores the packed metadata string. Called just before the
    ///     module is serialized.
    /// </summary>
    private void FinalizeCoverage()
    {
        if (!CoverageEnabled || _coverageType is null)
            return;

        var cctor = new MethodDefinition(
            ".cctor",
            MethodAttributes.Static
                | MethodAttributes.Private
                | MethodAttributes.HideBySig
                | MethodAttributes.SpecialName
                | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateStatic(_module.CorLibTypeFactory.Void)
        );
        _coverageType.Methods.Add(cctor);

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
