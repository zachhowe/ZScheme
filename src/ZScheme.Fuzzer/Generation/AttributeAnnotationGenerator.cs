namespace ZScheme.Fuzzer.Generation;

public enum AttributeTarget
{
    Function,
    Record, // record / struct
    Union,
    Class,
    Interface,
}

// Emits `(@ AttrName ...)` lines from a fixed safe palette of real .NET
// attributes. Per IlEmitter.Emit.cs:3648 the attribute name must resolve to a
// real .NET type at codegen time (with an optional "Attribute" suffix), so the
// palette is hardcoded and intentionally narrow.
//
// Each call to MaybeEmitFor(target) returns either an empty string or a
// trailing-newline-terminated `(@ AttrName arg...)\n` snippet that callers
// splice immediately above the target definition. The decision is gated by a
// per-call probability so most definitions remain unannotated.
public sealed class AttributeAnnotationGenerator
{
    private const double DefaultEmitProbability = 0.10;

    private readonly GeneratorContext _ctx;

    public AttributeAnnotationGenerator(GeneratorContext ctx)
    {
        _ctx = ctx;
    }

    public string MaybeEmitFor(AttributeTarget target, double? probability = null)
    {
        var p = probability ?? DefaultEmitProbability;
        if (_ctx.Rng.NextDouble() >= p)
            return "";

        return target switch
        {
            AttributeTarget.Function => PickFunctionAttr(),
            AttributeTarget.Record => PickValueOrClassAttr(),
            AttributeTarget.Union => PickValueOrClassAttr(),
            AttributeTarget.Class => PickValueOrClassAttr(),
            AttributeTarget.Interface => PickInterfaceAttr(),
            _ => "",
        };
    }

    // Named-arg variant `[DiagnosticId "FUZZ001"]` — a settable string property
    // on ObsoleteAttribute. Note: the IL backend currently drops named args
    // silently (BuildCustomAttribute never populates them) and no oracle reads
    // attribute metadata, so this is parse/emit path coverage only.
    private string ObsoleteAttr()
    {
        return _ctx.Rng.NextDouble() < 0.3
            ? "(@ System.ObsoleteAttribute \"fuzz-deprecated\" [DiagnosticId \"FUZZ001\"])\n"
            : "(@ System.ObsoleteAttribute \"fuzz-deprecated\")\n";
    }

    private string PickFunctionAttr()
    {
        // Methods/functions accept Obsolete and DebuggerStepThrough.
        var pick = _ctx.Rng.Next(2);
        return pick == 0
            ? ObsoleteAttr()
            : "(@ System.Diagnostics.DebuggerStepThroughAttribute)\n";
    }

    private string PickValueOrClassAttr()
    {
        // class / struct / record / union: Serializable + Obsolete are both valid.
        var pick = _ctx.Rng.Next(2);
        return pick == 0 ? "(@ System.SerializableAttribute)\n" : ObsoleteAttr();
    }

    private string PickInterfaceAttr()
    {
        // Interfaces don't accept Serializable (CS0592). Only Obsolete is safe
        // from the marker palette.
        return ObsoleteAttr();
    }
}
