# 0.4.0 (unreleased)

In development since 2026-08-11.

## Added — language

- **`letrec` — recursive local bindings.** `let` and `let*` evaluate a binding's value in the
  enclosing scope, so neither can express a local recursive function: the binder does not exist
  yet. `letrec` binds every name in the group before any value is evaluated, which is what makes
  local self- and mutual recursion writable at all.
  - Evaluation stays strict and left to right, so scope alone is not enough to keep a group
    sound. `LetrecInitializationChecker` enforces the rest: a binding whose value is not a
    `lambda` may only reach names bound *earlier* in the group, transitively through the values
    of anything it mentions. A lambda value is unconstrained — building a closure reads nothing,
    and by the time it can be called the group exists. Rejecting the remainder is what keeps the
    backends in agreement, since C# refuses a use-before-initialization local outright (CS0165)
    while IL would silently observe a default.
  - `LetrecLifter` eliminates the form in the IR by lambda-lifting each function binding to a
    top-level static with its captures prepended. Strict by-value capture cannot express mutual
    recursion — `f` would have to capture `g` while `g` captures `f` — so intra-group references
    become direct calls on stable lifted names instead. TCO falls out for free: a lifted
    self-call names the function itself, exactly what `TailCallLowering` already rewrites into a
    loop. Capture sets are per-binding and closed transitively over the group's call graph, so a
    mutually-recursive cycle shares one set while an unrelated sibling does not inherit captures
    it never needs. The pass runs before every other IR pass, so none of them needs a `LetRec`
    case.
  - Two shapes are reported rather than miscompiled: a group reading a class field (the lifted
    static has no `this`), and a generic member used as a *value* rather than called
    (`IrNode.Closure` has nowhere to carry type arguments). Both mirror refusals
    `ClosureConverter` already makes; the difference is that a plain lambda has a fallback path
    and a recursive group does not.
- **A `define` may nest inside a body.** A run of *adjacent* body-level `define`s becomes one
  `letrec` group whose body is the rest of the sequence. Grouping the whole run is what lets
  neighbouring definitions call each other; making the body the rest of the sequence is what
  gives "visible for the remainder of the body". Because it desugars to a node that already
  exists, inference, lifting, and both backends need no nested-definition case.
  - A nested definition can close over the enclosing function's parameters instead of taking
    them as arguments; it may be generic, including in the enclosing function's type parameters;
    and it loops under TCO and takes `#:recursive` exactly as a top-level `define` does.
  - Only `define` nests. `define-async` and the type-declaration forms stay top-level, a
    `:where` clause has no home on a group binding, and a body may not *end* with a definition.
  - Redefining a name in a later group of the same body is legal but almost always a mistake, so
    it warns and points at both definitions — skipped for bindings with no `NameSpan` or a `_`
    prefix, matching `UnusedBindingAnalyzer`.
- **A nested definition inside a class or `object` method may use the instance.** A loop helper
  serving one method is the natural way to write an accumulator loop without widening the
  method's signature, and it was refused outright the moment it named a field. Two routes now
  carry it, chosen by what the group actually needs. A field that cannot change after
  construction joins the capture set like any enclosing local — the site reads it, and the site
  is inside the method, where the bare name already resolves to `this.Field` — which costs no
  new IR node, emitter path or traversal, and reads the field once per call rather than once per
  iteration. Every field of a class lifted from an `(object …)` stands for a captured local and
  so takes this route. Anything else that needs a real `this` — a `#:mutable` field, a sibling
  method call, a `super/` call — makes the group's members private methods of the class instead
  of top-level statics, where the call sites again need no rewriting because a bare name in a
  method body is already `this.M` on both backends.
  - They loop under TCO like anything else, including on an `#:open` class: `TailCallLowering`
    used to skip an open class wholesale, which was exact for the methods the source wrote
    (virtual, so a self-call may reach a subclass override) but not for a synthesized helper
    (private and non-virtual, so nothing can override it or name it). The test is now per
    method rather than per class.
  - Still refused: a group needing the instance from a *constructor*, whose scope binds only its
    own parameters and whose emission has no class-method map live; a member used as a *value*
    when it is generic or hosted on the class, since `IrNode.Closure` names a top-level static
    and has neither a type-argument nor a receiver slot; and a group that needs the instance and
    is generic, since a method has nowhere to declare type parameters.
- **A class or `object` method body is sequenced like every other body.** It was a single
  expression, so a nested definition there needed a `let` wrapper no other body asks for, and
  trailing forms were dropped. All bodies now share `BuildExprSequence`.
- `examples/letrec.zs`, and `letrec` added to all four editor grammars (IntelliJ, Sublime,
  VS Code, Zed).

## Changed

- **`let`, `let*`, `letrec`, `use` and `use*` share one body builder.** Each had folded its own
  body by hand, so a `define` in any of them was silently dropped; all five now go through
  `BuildExprSequence`.
- **`LetrecLifter`'s site substitutions are live at the group's original site**, not only inside
  lifted bodies, so a member that is only ever called gets no site `let` and allocates no
  delegate.
- **A group inside a generic function is lifted rather than rejected.** The lifted static
  declares its own type parameters, with constraints remapped through type-var IDs since
  prepending captures shifts the indices.
- **A nested group referencing an outer group's member inherits that member's captures** instead
  of capturing the member, which no longer exists as a value. Lifted names carry the module,
  because group ids restart per module while the inline-module path puts several in one assembly.
- **`InferLambda` seeds its type-var scope from the enclosing scope**, so a `^a` annotation on a
  nested lambda means the enclosing `^a` rather than a fresh variable that unified with it by
  luck. This applies to every lambda, not only the ones a nested `define` desugars to.
- **`ZS0005` judges a nested definition by its body, not its nesting.** The analyzer walks node
  kinds that predate the letrec desugar, so nested defines would have lost the warning entirely
  and silently — precisely the case it exists for, and the one shape where the author cannot tell
  from the generated code. Letrec function bindings are now candidates in their own right, with
  no container veto, and `Walk` gained a `Letrec` case so a tail call to the *enclosing* function
  from after a nested definition still reads as a back-edge. `LetrecBinding` carries
  `AllowsUnloopedRecursion`, so `#:recursive` survives the desugar. `not-top-level` stays
  reachable only via nested `define-async`, already an error in its own right; its wording says
  so.
- **The language server understands the new forms** — scope analysis, symbol collection,
  navigation, completion, inlay hints, semantic tokens and code actions all handle `letrec`
  groups and nested definitions.
- **Five stdlib modules dogfood nested definitions** (`list`, `vector`, `treelist`, and the
  mutable `vector`/`treelist`): a loop helper serving exactly one public function now lives
  inside it and captures that function's arguments instead of threading them through every
  recursive call.

## Fixed

- **The IL main-module emitter could not resolve a call between two top-level methods.** It
  registered and emitted each function body in one pass; it now registers every signature first,
  as the imported-module path already did.
- **`LetrecLifter` dropped a `ClrCall`'s `StaticMember` flag.** The pass rebuilt every `ClrCall`
  from the seven positional parameters the record had when it was written, so once an eighth
  arrived — the flag marking a "call" that actually names a static property or field — any
  `ClrCall` reaching a letrec body came out with the flag reset and both backends emitted
  `DateTime.Now()` instead of `DateTime.Now`. Rewriting with `cc with { Args = … }` copies every
  parameter present or future, which is what the surrounding `MethodCall` case already did.
- **IR rewrites synthesized nodes with no source span.** `IrNode.Span` is an `init` property with
  a default, making the IR the only layer where a span can vanish silently — and the only one
  rewritten repeatedly. Both hoisters restored the span in `Rewrite`, but only on the outermost
  node they got back: the `Let` spine `Anf` builds, the rebuilt core node and the synthesized
  `Var`s were all left at `None`, as were `LetrecLifter`'s retargeted callee and rebuilt capture
  arguments. `Anf` now stamps the whole spine with the parent's span, a synthesized `Var`
  inherits the span of the argument it stands in for, and the lifter keeps the span of the
  reference it replaces. Coverage output is unchanged — the skipped probes always landed on a
  line a sibling covered, confirmed by measuring identical point sets both ways — so the win is
  codegen diagnostics, which stop reporting `(0:0)` for these nodes.

## Tooling

- **`SpanPreservationTests`** walks the IR by reflection — the IR has no shared visitor, so a
  hand-rolled walker would quietly stop covering node kinds added later — and asserts across
  twelve source shapes that no line-probe node reaches codegen without a usable span. It asserts
  against `IlEmitter.IsLineProbeNode` itself rather than a copy that could drift, and reuses
  `CoverageInScope`'s own predicate, since `default(SourceSpan)` has a null `File` while
  `SourceSpan.None` has an empty one. The three existing span tests each asserted only the root
  node, passing even with the interior of the very tree they built left spanless; they now walk
  the whole result. `Compilation.LoweredIr` exposes stage 5's output the way `TypedProgram` and
  `ExpandedSExprs` already expose theirs.
- **A `letrec` fuzzer generator** covering the five lowering paths. It found four bugs fixed
  here: the class-field case, an untraversed `ClassDecl.Constructor` that let a `LetRec` reach
  codegen, a field/local precedence inversion that rejected legitimate captures, and unbounded
  recursion when a binding was applied through `GenIntFnApply`. Bounding lives inside the
  generated functions rather than at the call site, because an overflow kills the fuzzer process
  instead of being reported as a case failure.
- **A nested-define fuzzer generator** with seven shapes, probing what `letrec` cannot: the
  desugar itself — which forms land in which group, and what each group's body is. Both choices
  are invisible in the surface syntax and fail quietly. It found the enclosing-group capture bug
  above.
- The `issues/` backlog records an IL-backend bug the new generator surfaced; a baseline run with
  nested defines disabled reproduces it, so it is pre-existing and left alone.
