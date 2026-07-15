using System.Collections.Immutable;
using ZScheme.Compiler.Codegen;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     Makes codegen identifier assignment injective. <see cref="NameConverter" /> is
///     lossy: distinct ZScheme names (e.g. <c>this-function</c> and <c>ThisFunction</c>)
///     sanitize to the same C#/IL identifier and would collide in the emitted assembly.
///     This pass rewrites the IR so colliding definitions resolve to distinct emitted
///     identifiers, consistently for both backends.
///
///     Three strategies, by kind:
///     <list type="bullet">
///         <item>
///             <b>Module-level</b> functions and values keep their original
///             <c>Name</c>/<c>VarName</c> (cross-module references and exported metadata
///             key on it) but get a disambiguated <c>EmitName</c> stamped on the
///             definition and on every reference; the later collider gets a
///             <c>_fn</c>/<c>_fn2</c> suffix (also subsuming the old func-vs-nested-type
///             rename). The emitters read <c>EmitName</c> when set.
///         </item>
///         <item>
///             <b>Type</b> names (records, unions and their cases, classes, interfaces)
///             likewise keep their raw <c>Name</c> but get a disambiguated <c>EmitName</c>
///             stamped on the <em>declaration only</em>; the later collider gets a
///             <c>_type</c>/<c>_type2</c> suffix. References need no rewriting because both
///             backends resolve a type reference through a chokepoint keyed by the raw name
///             (C# <c>QualifyType</c>; IL's <c>_userTypes</c>/<c>_unionCaseTypes</c>
///             registries), which the emitters point at the renamed declaration.
///         </item>
///         <item>
///             <b>Local</b> bindings (let/use/lambda params/match/catch vars) never cross
///             a module boundary, so a collider is simply alpha-renamed: its raw name and
///             every in-scope reference are rewritten to a fresh name that sanitizes
///             uniquely, so the emitters' existing local-naming paths produce distinct
///             identifiers with no further change. Only genuine collisions between
///             <em>different</em> raw names are renamed; plain same-name shadowing is left
///             untouched so the emitters' shadowing logic is undisturbed.
///         </item>
///     </list>
/// </summary>
internal static class EmitNameResolver
{
    internal sealed record ResolveResult(
        IrNode CurrentIr,
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)> ImportedModules,
        // className -> (originalName -> emittedName) for renamed module-level *value*
        // symbols (functions/values). Lets the library compiler persist exported renames
        // into module metadata.
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ModuleRenames,
        // className -> (rawTypeName -> emittedName) for renamed *type* names (records,
        // unions + their cases, classes, interfaces). Kept separate from ModuleRenames
        // because a type and a value can share a source name yet need different emitted
        // identifiers. Persisted into module metadata so a consumer of a precompiled DLL
        // references a renamed type by the name baked into it.
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ModuleTypeRenames
    );

    /// <param name="precompiledRenamesByModuleName">
    ///     For each precompiled module (keyed by its ZScheme module name, as carried on
    ///     <see cref="IrNode.Var.ModuleName" />), the original→emitted renames loaded from
    ///     that module's metadata. References into a precompiled module are stamped from
    ///     this so they match the names baked into the DLL. Null/absent ⇒ no renames
    ///     (references fall back to plain sanitization, the historical behavior).
    /// </param>
    internal static ResolveResult Resolve(
        string currentModuleClass,
        IrNode currentIr,
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? importedModules,
        IReadOnlyDictionary<
            string,
            IReadOnlyDictionary<string, string>
        >? precompiledRenamesByModuleName = null
    )
    {
        importedModules ??= [];

        // 1. Per-module top-level allocation (renames only).
        var moduleRenames = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var moduleTypeRenames = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var globalOwner = new Dictionary<string, string>();

        void Index(string moduleClass, IReadOnlyList<IrNode> defs)
        {
            var alloc = BuildModuleAllocation(defs);
            moduleRenames[moduleClass] = alloc.ValueRenames;
            moduleTypeRenames[moduleClass] = alloc.TypeRenames;
            foreach (var name in TopLevelValueNames(defs))
                globalOwner[name] = moduleClass; // last writer wins (mirrors _funcToModuleClass)
        }

        // Imported modules first, current module last, so a current-module definition
        // wins a bare-name lookup over a same-named import.
        foreach (var (cls, defs) in importedModules)
            Index(cls, defs);
        var currentDefs = TopLevelDefs(currentIr);
        Index(currentModuleClass, currentDefs);

        var rewriter = new Rewriter(
            moduleRenames,
            moduleTypeRenames,
            globalOwner,
            precompiledRenamesByModuleName
        );

        var newCurrent = rewriter.RewriteTopLevel(currentModuleClass, currentIr);
        var newImported = importedModules
            .Select(m =>
                (
                    m.ClassName,
                    (IReadOnlyList<IrNode>)
                        m.Definitions.Select(d => rewriter.RewriteTopLevel(m.ClassName, d)).ToList()
                )
            )
            .ToList();

        return new ResolveResult(newCurrent, newImported, moduleRenames, moduleTypeRenames);
    }

    // ---- top-level name collection (mirrors Compilation.IrCollection.CollectAllIrDefs) ----

    private static List<IrNode> TopLevelDefs(IrNode node)
    {
        var result = new List<IrNode>();
        Collect(node, result);
        return result;

        static void Collect(IrNode n, List<IrNode> acc)
        {
            while (true)
                switch (n)
                {
                    case IrNode.Seq seq:
                        foreach (var child in seq.Nodes)
                            Collect(child, acc);
                        return;
                    case IrNode.FuncDef
                    or IrNode.UnionDecl
                    or IrNode.RecordDecl
                    or IrNode.ClassDecl
                    or IrNode.InterfaceDecl
                    or IrNode.TypeAliasDecl:
                        acc.Add(n);
                        return;
                    case IrNode.Let let:
                        acc.Add(let);
                        n = let.Body;
                        continue;
                    default:
                        return;
                }
        }
    }

    private static IEnumerable<string> TopLevelValueNames(IReadOnlyList<IrNode> defs)
    {
        foreach (var def in defs)
            switch (def)
            {
                case IrNode.FuncDef f:
                    yield return f.Name;
                    break;
                case IrNode.Let l:
                    yield return l.VarName;
                    break;
            }
    }

    /// The emitted-name allocation for one module class. Renames are split by kind because
    /// a type and a value may share a source name yet need different emitted identifiers.
    private readonly record struct ModuleAllocation(
        IReadOnlyDictionary<string, string> ValueRenames,
        IReadOnlyDictionary<string, string> TypeRenames
    );

    /// Allocates emitted names for one module class so sanitization is injective. `main`
    /// is reserved first and never renamed (the entry point references it verbatim). Type
    /// names (records, unions and their cases, classes, interfaces) then claim their
    /// sanitized name in source order; a collider takes the first free `_type`/`_type2`/….
    /// `type-alias` declarations are reserved but emit no code, so they are never renamed.
    /// Functions and module-level values claim their names last; a collider takes the first
    /// free `_fn`/`_fn2`/…. Types are allocated before values, so a value colliding with a
    /// type still yields to it. Returns only the entries (per kind) that were renamed.
    private static ModuleAllocation BuildModuleAllocation(IReadOnlyList<IrNode> defs)
    {
        var used = new HashSet<string>();

        // `main` is the entry point; both backends reference it verbatim, so reserve it
        // before any type/value so a colliding type or function yields to it.
        foreach (var name in TopLevelValueNames(defs))
            if (name == "main")
                used.Add(NameConverter.SanitizeIdentifier(name));

        var typeRenames = new Dictionary<string, string>();

        void ClaimType(string name)
        {
            var baseName = NameConverter.SanitizeIdentifier(name);
            if (used.Add(baseName))
                return; // first claimant keeps the base name

            var suffix = 1;
            string renamed;
            do
            {
                renamed = suffix == 1 ? $"{baseName}_type" : $"{baseName}_type{suffix}";
                suffix++;
            } while (!used.Add(renamed));

            typeRenames[name] = renamed;
        }

        foreach (var def in defs)
            switch (def)
            {
                case IrNode.RecordDecl r:
                    ClaimType(r.Name);
                    break;
                case IrNode.ClassDecl c:
                    ClaimType(c.Name);
                    break;
                case IrNode.InterfaceDecl i:
                    ClaimType(i.Name);
                    break;
                case IrNode.TypeAliasDecl t:
                    used.Add(NameConverter.SanitizeIdentifier(t.Name)); // reserve only; emits no code
                    break;
                case IrNode.UnionDecl u:
                    ClaimType(u.Name);
                    foreach (var uc in u.Cases)
                        ClaimType(uc.Name);
                    break;
            }

        var valueRenames = new Dictionary<string, string>();
        foreach (var name in TopLevelValueNames(defs))
        {
            if (name == "main")
                continue;

            var baseName = NameConverter.SanitizeIdentifier(name);
            if (used.Add(baseName))
                continue; // first claimant keeps the base name

            var suffix = 1;
            string renamed;
            do
            {
                renamed = suffix == 1 ? $"{baseName}_fn" : $"{baseName}_fn{suffix}";
                suffix++;
            } while (!used.Add(renamed));

            valueRenames[name] = renamed;
        }

        return new ModuleAllocation(valueRenames, typeRenames);
    }

    // ---- tree rewriter ----

    private sealed class Rewriter
    {
        private readonly IReadOnlyDictionary<
            string,
            IReadOnlyDictionary<string, string>
        > _moduleRenames;
        private readonly IReadOnlyDictionary<
            string,
            IReadOnlyDictionary<string, string>
        > _moduleTypeRenames;
        private readonly IReadOnlyDictionary<string, string> _globalOwner;
        private readonly IReadOnlyDictionary<
            string,
            IReadOnlyDictionary<string, string>
        >? _precompiledRenames;

        // Flattened precompiled renames (originalName -> emittedName) for resolving
        // *bare* (unqualified) references to a renamed precompiled symbol — the common
        // case, since most imported references carry no ModuleName.
        private readonly Dictionary<string, string> _precompiledByName = new();

        private int _fresh;

        public Rewriter(
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> moduleRenames,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> moduleTypeRenames,
            IReadOnlyDictionary<string, string> globalOwner,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? precompiledRenames
        )
        {
            _moduleRenames = moduleRenames;
            _moduleTypeRenames = moduleTypeRenames;
            _globalOwner = globalOwner;
            _precompiledRenames = precompiledRenames;
            if (precompiledRenames is not null)
                foreach (var (_, map) in precompiledRenames)
                foreach (var (original, emitted) in map)
                    _precompiledByName[original] = emitted; // last writer wins
        }

        /// A persistent map of in-scope local bindings. `Effective` maps a binding's raw
        /// name to the (possibly alpha-renamed) raw name codegen should use; `Owner`
        /// records which raw name currently occupies each sanitized identifier so we only
        /// rename on a genuine cross-name collision, not on plain same-name shadowing.
        private sealed record Scope(
            ImmutableDictionary<string, string> Effective,
            ImmutableDictionary<string, string> Owner
        )
        {
            public static readonly Scope Empty = new(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, string>.Empty
            );
        }

        private Scope Bind(Scope scope, string raw, out string effective)
        {
            if (raw == "_")
            {
                effective = raw;
                return scope;
            }

            var san = NameConverter.SanitizeParameter(raw);
            if (!scope.Owner.TryGetValue(san, out var owner))
            {
                effective = raw;
                return scope with
                {
                    Effective = scope.Effective.SetItem(raw, raw),
                    Owner = scope.Owner.SetItem(san, raw),
                };
            }

            if (owner == raw)
            {
                // Plain shadowing (same raw name) — leave to the emitters' existing
                // shadowing handling; do not alpha-rename.
                effective = raw;
                return scope with { Effective = scope.Effective.SetItem(raw, raw) };
            }

            // Genuine collision: a different raw name already owns this identifier.
            string candidate,
                csan;
            do
            {
                candidate = $"{raw}__u{_fresh++}";
                csan = NameConverter.SanitizeParameter(candidate);
            } while (scope.Owner.ContainsKey(csan));

            effective = candidate;
            return scope with
            {
                Effective = scope.Effective.SetItem(raw, candidate),
                Owner = scope.Owner.SetItem(csan, candidate),
            };
        }

        // ---- top-level ----

        public IrNode RewriteTopLevel(string moduleClass, IrNode node)
        {
            switch (node)
            {
                case IrNode.Seq seq:
                    return seq with
                    {
                        Nodes = seq.Nodes.Select(n => RewriteTopLevel(moduleClass, n)).ToList(),
                    };

                case IrNode.FuncDef f:
                    return RewriteFuncDef(moduleClass, f, isTopLevel: true);

                case IrNode.Let let:
                    // Module-level value: emitted as a static field named via the module
                    // allocation. Its initializer is an ordinary expression (module-init).
                    var emit = LookupModuleRename(moduleClass, let.VarName);
                    return let with
                    {
                        EmitName = emit,
                        Value = RewriteExpr(let.Value, Scope.Empty),
                        Body = RewriteTopLevel(moduleClass, let.Body),
                    };

                case IrNode.ClassDecl cls:
                    return RewriteClassDecl(moduleClass, cls);

                case IrNode.RecordDecl rec:
                    return rec with { EmitName = LookupTypeRename(moduleClass, rec.Name) };

                case IrNode.InterfaceDecl iface:
                    return iface with { EmitName = LookupTypeRename(moduleClass, iface.Name) };

                case IrNode.UnionDecl union:
                    return union with
                    {
                        EmitName = LookupTypeRename(moduleClass, union.Name),
                        Cases = union
                            .Cases.Select(c =>
                                c with
                                {
                                    EmitName = LookupTypeRename(moduleClass, c.Name),
                                }
                            )
                            .ToList(),
                    };

                case IrNode.TypeAliasDecl:
                    // Reserved in the allocation but emits no code, so never renamed.
                    return node;

                default:
                    // Module-init statement (ClrCall/Call/Throw/Await/ObjectExpr/…).
                    return RewriteExpr(node, Scope.Empty);
            }
        }

        private string? LookupModuleRename(string moduleClass, string name) =>
            _moduleRenames.TryGetValue(moduleClass, out var m) && m.TryGetValue(name, out var e)
                ? e
                : null;

        private string? LookupTypeRename(string moduleClass, string name) =>
            _moduleTypeRenames.TryGetValue(moduleClass, out var m) && m.TryGetValue(name, out var e)
                ? e
                : null;

        private IrNode.FuncDef RewriteFuncDef(string moduleClass, IrNode.FuncDef f, bool isTopLevel)
        {
            var scope = Scope.Empty;
            var newParams = new List<IrParam>(f.Params.Count);
            foreach (var p in f.Params)
            {
                scope = Bind(scope, p.Name, out var eff);
                newParams.Add(eff == p.Name ? p : p with { Name = eff });
            }

            return f with
            {
                EmitName = isTopLevel ? LookupModuleRename(moduleClass, f.Name) : f.EmitName,
                Params = newParams,
                Body = RewriteExpr(f.Body, scope),
            };
        }

        private IrNode RewriteClassDecl(string moduleClass, IrNode.ClassDecl cls) =>
            cls with
            {
                EmitName = LookupTypeRename(moduleClass, cls.Name),
                Methods = cls.Methods.Select(RewriteObjectMethod).ToList(),
                Constructor = cls.Constructor is { } c ? RewriteConstructor(c) : null,
            };

        private IrObjectMethod RewriteObjectMethod(IrObjectMethod m)
        {
            var scope = Scope.Empty;
            var newParams = new List<IrParam>(m.Params.Count);
            foreach (var p in m.Params)
            {
                scope = Bind(scope, p.Name, out var eff);
                newParams.Add(eff == p.Name ? p : p with { Name = eff });
            }

            return m with
            {
                Params = newParams,
                Body = RewriteExpr(m.Body, scope),
            };
        }

        private IrConstructor RewriteConstructor(IrConstructor c)
        {
            var scope = Scope.Empty;
            var newParams = new List<IrParam>(c.Params.Count);
            foreach (var p in c.Params)
            {
                scope = Bind(scope, p.Name, out var eff);
                newParams.Add(eff == p.Name ? p : p with { Name = eff });
            }

            return c with
            {
                Params = newParams,
                SuperArgs = c.SuperArgs?.Select(a => RewriteExpr(a, scope)).ToList(),
                FieldSets = c
                    .FieldSets.Select(fs => (fs.FieldName, RewriteExpr(fs.Value, scope)))
                    .ToList(),
                BodyExprs = c.BodyExprs.Select(b => RewriteExpr(b, scope)).ToList(),
            };
        }

        // ---- expressions ----

        private IrNode RewriteExpr(IrNode node, Scope scope)
        {
            switch (node)
            {
                case IrNode.Var v:
                    return RewriteVar(v, scope);

                case IrNode.Let let:
                {
                    var value = RewriteExpr(let.Value, scope); // non-recursive: outer scope
                    var inner = Bind(scope, let.VarName, out var eff);
                    return let with
                    {
                        VarName = eff,
                        Value = value,
                        Body = RewriteExpr(let.Body, inner),
                    };
                }

                case IrNode.Use use:
                {
                    var value = RewriteExpr(use.Value, scope);
                    var inner = Bind(scope, use.VarName, out var eff);
                    return use with
                    {
                        VarName = eff,
                        Value = value,
                        Body = RewriteExpr(use.Body, inner),
                    };
                }

                case IrNode.FuncDef lambda:
                    // Lambda in expression position: a nested scope of its own. Not a
                    // module-level definition, so no EmitName (its synthetic name is
                    // unique by source position).
                    return RewriteLambda(lambda, scope);

                case IrNode.If i:
                    return i with
                    {
                        Condition = RewriteExpr(i.Condition, scope),
                        Then = RewriteExpr(i.Then, scope),
                        Else = RewriteExpr(i.Else, scope),
                    };

                case IrNode.Call c:
                    return c with
                    {
                        Function = RewriteExpr(c.Function, scope),
                        Args = c.Args.Select(a => RewriteExpr(a, scope)).ToList(),
                    };

                case IrNode.BinOp b:
                    return b with
                    {
                        Left = RewriteExpr(b.Left, scope),
                        Right = RewriteExpr(b.Right, scope),
                    };

                case IrNode.UnaryOp u:
                    return u with { Operand = RewriteExpr(u.Operand, scope) };

                case IrNode.Closure cl:
                    return cl with
                    {
                        CapturedValues = cl
                            .CapturedValues.Select(a => RewriteExpr(a, scope))
                            .ToList(),
                    };

                case IrNode.RecordNew rn:
                    return rn with
                    {
                        Fields = rn
                            .Fields.Select(fld => (fld.FieldName, RewriteExpr(fld.Value, scope)))
                            .ToList(),
                    };

                case IrNode.TupleNew tn:
                    return tn with
                    {
                        Elements = tn.Elements.Select(e => RewriteExpr(e, scope)).ToList(),
                    };

                case IrNode.FieldGet fg:
                    return fg with { Record = RewriteExpr(fg.Record, scope) };

                case IrNode.RecordWith rw:
                    return rw with
                    {
                        Record = RewriteExpr(rw.Record, scope),
                        Updates = rw
                            .Updates.Select(up => (up.FieldName, RewriteExpr(up.Value, scope)))
                            .ToList(),
                    };

                case IrNode.UnionCaseNew uc:
                    return uc with { Args = uc.Args.Select(a => RewriteExpr(a, scope)).ToList() };

                case IrNode.Match m:
                    return m with
                    {
                        Scrutinee = RewriteExpr(m.Scrutinee, scope),
                        Arms = m.Arms.Select(arm => RewriteArm(arm, scope)).ToList(),
                    };

                case IrNode.Seq seq:
                    return seq with
                    {
                        Nodes = seq.Nodes.Select(n => RewriteExpr(n, scope)).ToList(),
                    };

                case IrNode.MutableArrayNew ma:
                    return ma with
                    {
                        Elements = ma.Elements.Select(e => RewriteExpr(e, scope)).ToList(),
                    };

                case IrNode.ClrNew cn:
                    return cn with { Args = cn.Args.Select(a => RewriteExpr(a, scope)).ToList() };

                case IrNode.ClrCall cc:
                    return cc with { Args = cc.Args.Select(a => RewriteExpr(a, scope)).ToList() };

                case IrNode.Throw t:
                    return t with { Expr = RewriteExpr(t.Expr, scope) };

                case IrNode.Await aw:
                    return aw with { Expr = RewriteExpr(aw.Expr, scope) };

                case IrNode.WithHandlers wh:
                    return wh with
                    {
                        Body = RewriteExpr(wh.Body, scope),
                        Handlers = wh.Handlers.Select(h => RewriteHandler(h, scope)).ToList(),
                    };

                case IrNode.ObjectExpr oe:
                    return oe with
                    {
                        Methods = oe.Methods.Select(RewriteObjectMethod).ToList(),
                        Constructor = oe.Constructor is { } ctor ? RewriteConstructor(ctor) : null,
                    };

                case IrNode.SuperMethodCall sm:
                    return sm with { Args = sm.Args.Select(a => RewriteExpr(a, scope)).ToList() };

                case IrNode.SetField sf:
                    return sf with { Value = RewriteExpr(sf.Value, scope) };

                case IrNode.MethodCall mc:
                    return mc with
                    {
                        Receiver = RewriteExpr(mc.Receiver, scope),
                        Args = mc.Args.Select(a => RewriteExpr(a, scope)).ToList(),
                    };

                // Leaves / nodes with no rewritable children.
                default:
                    return node;
            }
        }

        private IrNode RewriteVar(IrNode.Var v, Scope scope)
        {
            // Explicit module qualification is never a local.
            if (v.ModuleName is null && scope.Effective.TryGetValue(v.Name, out var effective))
                return effective == v.Name ? v : v with { Name = effective };

            // Module-level reference: stamp EmitName if the target was renamed.
            string? emit = null;
            if (v.ModuleName is { } modName)
            {
                if (
                    _precompiledRenames is not null
                    && _precompiledRenames.TryGetValue(modName, out var pm)
                    && pm.TryGetValue(v.Name, out var pe)
                )
                    emit = pe;
                else
                    emit = LookupModuleRename(
                        NameConverter.ClassNameFromModuleName(modName),
                        v.Name
                    );
            }
            else if (_globalOwner.TryGetValue(v.Name, out var ownerClass))
            {
                // Source module owns this bare name.
                emit = LookupModuleRename(ownerClass, v.Name);
            }
            else if (_precompiledByName.TryGetValue(v.Name, out var pe2))
            {
                // Bare reference to a renamed precompiled symbol.
                emit = pe2;
            }

            return emit is null ? v : v with { EmitName = emit };
        }

        private IrNode.FuncDef RewriteLambda(IrNode.FuncDef lambda, Scope scope)
        {
            var inner = scope;
            var newParams = new List<IrParam>(lambda.Params.Count);
            foreach (var p in lambda.Params)
            {
                inner = Bind(inner, p.Name, out var eff);
                newParams.Add(eff == p.Name ? p : p with { Name = eff });
            }

            return lambda with
            {
                Params = newParams,
                Body = RewriteExpr(lambda.Body, inner),
            };
        }

        private IrMatchArm RewriteArm(IrMatchArm arm, Scope scope)
        {
            var inner = scope;
            var pattern = RewritePattern(arm.Pattern, ref inner);
            return arm with { Pattern = pattern, Body = RewriteExpr(arm.Body, inner) };
        }

        private IrPattern RewritePattern(IrPattern pattern, ref Scope scope)
        {
            switch (pattern)
            {
                case IrPattern.Variable v:
                    scope = Bind(scope, v.Name, out var eff);
                    return eff == v.Name ? v : v with { Name = eff };

                case IrPattern.Constructor c:
                {
                    var fields = new List<IrPattern>(c.Fields.Count);
                    foreach (var f in c.Fields)
                        fields.Add(RewritePattern(f, ref scope));
                    return c with { Fields = fields };
                }

                case IrPattern.Tuple t:
                {
                    var elems = new List<IrPattern>(t.Elements.Count);
                    foreach (var e in t.Elements)
                        elems.Add(RewritePattern(e, ref scope));
                    return t with { Elements = elems };
                }

                default:
                    return pattern; // Wildcard, Literal
            }
        }

        private IrHandlerClause RewriteHandler(IrHandlerClause h, Scope scope)
        {
            var inner = Bind(scope, h.BindingVarName, out var eff);
            return h with { BindingVarName = eff, HandlerBody = RewriteExpr(h.HandlerBody, inner) };
        }
    }
}
