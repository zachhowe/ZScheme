using System.Reflection;
using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

// LetrecLifter turns a recursive binding group into top-level static functions. These tests pin
// what the rest of the pipeline depends on: no IrNode.LetRec survives; a reference to a member —
// at the site or inside a lifted body — becomes a direct call with the captures prepended, so a
// member that is only called gets no site binding at all; a member used as a *value* becomes an
// IrNode.Closure; and a lifted function whose signature mentions type variables declares them as
// its own type parameters, carrying over any constraint the enclosing function put on them.
public class LetrecLifterTests
{
    private static IrNode.Var V(string name, ZType? type = null)
    {
        return new IrNode.Var(name) { Type = type ?? ZType.Int };
    }

    private static IrNode.FuncDef Lambda(string name, IrParam[] parms, IrNode body)
    {
        return new IrNode.FuncDef(name, parms, ZType.Int, body, false)
        {
            Type = new ZType.ZFuncType([.. parms.Select(p => p.Type)], ZType.Int),
        };
    }

    private static IrNode.LetRec Group(
        (string Name, IrNode Value)[] bindings,
        IrNode body,
        SourceSpan span = default
    )
    {
        return new IrNode.LetRec(
            [.. bindings.Select(b => new IrNode.LetRecBinding(b.Name, b.Value))],
            body
        )
        {
            Type = ZType.Int,
            Span = span,
        };
    }

    // Wraps the group in an outer function whose params become enclosing locals, mirroring how a
    // letrec actually appears inside a `define`. Returns the rewritten body and the lifter.
    private static (IrNode Body, LetrecLifter Lifter, DiagnosticBag Diagnostics) LiftInside(
        IrNode.LetRec group,
        params (string Name, ZType Type)[] outerParams
    )
    {
        var outer = new IrNode.FuncDef(
            "outer",
            [.. outerParams.Select(p => new IrParam(p.Name, p.Type))],
            ZType.Int,
            group,
            false
        )
        {
            Type = ZType.Int,
        };
        var diagnostics = new DiagnosticBag();
        var lifter = new LetrecLifter(diagnostics);
        var result = Assert.IsType<IrNode.FuncDef>(lifter.Lift(outer));
        return (result.Body, lifter, diagnostics);
    }

    // Flattens a let spine into (name, value) pairs plus the innermost body.
    private static (List<(string Name, IrNode Value)> Bindings, IrNode Body) Spine(IrNode node)
    {
        var bindings = new List<(string, IrNode)>();
        while (node is IrNode.Let let)
        {
            bindings.Add((let.VarName, let.Value));
            node = let.Body;
        }

        return (bindings, node);
    }

    [Fact]
    public void SelfRecursiveGroup_LiftsToTopLevelFunction()
    {
        // (letrec ([f (lambda (n) (f n))]) (f 1))
        var group = Group(
            [("f", Lambda("f", [new IrParam("n", ZType.Int)], new IrNode.Call(V("f"), [V("n")])))],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var (body, lifter, diagnostics) = LiftInside(group);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        var lifted = Assert.Single(lifter.LiftedFunctions);
        Assert.Equal("__letrec_0_f", lifted.Name);

        // The self-call now names the lifted function, which is exactly what TailCallLowering
        // matches on — that is where TCO for a recursive letrec function comes from.
        var selfCall = Assert.IsType<IrNode.Call>(lifted.Body);
        Assert.Equal("__letrec_0_f", Assert.IsType<IrNode.Var>(selfCall.Function).Name);

        // `f` is only ever called, so the site keeps no binding for it — the call is retargeted
        // at the lifted name directly rather than going through a delegate.
        var (bindings, siteBody) = Spine(body);
        Assert.Empty(bindings);
        var siteCall = Assert.IsType<IrNode.Call>(siteBody);
        Assert.Equal("__letrec_0_f", Assert.IsType<IrNode.Var>(siteCall.Function).Name);
    }

    [Fact]
    public void MutuallyRecursiveGroup_LiftsBothAndCrossCallsDirectly()
    {
        // Neither can capture the other by value, so both must become direct sibling calls.
        var group = Group(
            [
                (
                    "even",
                    Lambda(
                        "even",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.Call(V("odd"), [V("n")])
                    )
                ),
                (
                    "odd",
                    Lambda(
                        "odd",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.Call(V("even"), [V("n")])
                    )
                ),
            ],
            new IrNode.Call(V("even"), [new IrNode.IntConst(1)])
        );

        var (_, lifter, diagnostics) = LiftInside(group);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        Assert.Equal(
            ["__letrec_0_even", "__letrec_0_odd"],
            lifter.LiftedFunctions.Select(f => f.Name)
        );

        var evenCall = Assert.IsType<IrNode.Call>(lifter.LiftedFunctions[0].Body);
        Assert.Equal("__letrec_0_odd", Assert.IsType<IrNode.Var>(evenCall.Function).Name);
        var oddCall = Assert.IsType<IrNode.Call>(lifter.LiftedFunctions[1].Body);
        Assert.Equal("__letrec_0_even", Assert.IsType<IrNode.Var>(oddCall.Function).Name);
    }

    [Fact]
    public void EnclosingLocal_BecomesLeadingCaptureParameter()
    {
        // `x` is a parameter of the enclosing function, so it is a capture rather than a global.
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp("+", V("n"), V("x")) { Type = ZType.Int }
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var (body, lifter, _) = LiftInside(group, ("x", ZType.Int));

        var lifted = Assert.Single(lifter.LiftedFunctions);
        Assert.Equal(["x", "n"], lifted.Params.Select(p => p.Name));

        // ...and the site's call passes the enclosing local in as the leading argument.
        var (bindings, siteBody) = Spine(body);
        Assert.Empty(bindings);
        var siteCall = Assert.IsType<IrNode.Call>(siteBody);
        Assert.Equal("__letrec_0_f", Assert.IsType<IrNode.Var>(siteCall.Function).Name);
        Assert.Equal("x", Assert.IsType<IrNode.Var>(siteCall.Args[0]).Name);
        Assert.Equal(1, Assert.IsType<IrNode.IntConst>(siteCall.Args[1]).Value);
    }

    [Fact]
    public void GlobalReference_IsNotCaptured()
    {
        // Nothing binds `g` locally, so it stays a free reference resolved at top level —
        // matching ClosureConverter, which never captures a global either.
        var group = Group(
            [("f", Lambda("f", [new IrParam("n", ZType.Int)], new IrNode.Call(V("g"), [V("n")])))],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var (_, lifter, _) = LiftInside(group);

        var lifted = Assert.Single(lifter.LiftedFunctions);
        Assert.Equal(["n"], lifted.Params.Select(p => p.Name));
    }

    [Fact]
    public void SiblingCapture_IsInheritedTransitively()
    {
        // `f` calls `g`, and `g` captures `x`. `f` must also carry `x` so that it can supply it
        // when it calls `g` — without the transitive step the sibling call would be unsatisfiable.
        var group = Group(
            [
                (
                    "f",
                    Lambda("f", [new IrParam("n", ZType.Int)], new IrNode.Call(V("g"), [V("n")]))
                ),
                (
                    "g",
                    Lambda(
                        "g",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp("+", V("n"), V("x")) { Type = ZType.Int }
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var (_, lifter, _) = LiftInside(group, ("x", ZType.Int));

        Assert.Equal(["x", "n"], lifter.LiftedFunctions[0].Params.Select(p => p.Name));
        Assert.Equal(["x", "n"], lifter.LiftedFunctions[1].Params.Select(p => p.Name));

        // The sibling call forwards the capture ahead of the original argument.
        var call = Assert.IsType<IrNode.Call>(lifter.LiftedFunctions[0].Body);
        Assert.Equal("__letrec_0_g", Assert.IsType<IrNode.Var>(call.Function).Name);
        Assert.Equal(2, call.Args.Count);
        Assert.Equal("x", Assert.IsType<IrNode.Var>(call.Args[0]).Name);
    }

    [Fact]
    public void UnrelatedSibling_DoesNotInheritCaptures()
    {
        // `f` never mentions `g`, so it must not be forced to carry `g`'s capture. Sharing one
        // union capture set across the group would create a false ordering constraint at the site.
        var group = Group(
            [
                ("f", Lambda("f", [new IrParam("n", ZType.Int)], V("n"))),
                (
                    "g",
                    Lambda(
                        "g",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp("+", V("n"), V("x")) { Type = ZType.Int }
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var (_, lifter, _) = LiftInside(group, ("x", ZType.Int));

        Assert.Equal(["n"], lifter.LiftedFunctions[0].Params.Select(p => p.Name));
        Assert.Equal(["x", "n"], lifter.LiftedFunctions[1].Params.Select(p => p.Name));
    }

    [Fact]
    public void SiblingInValuePosition_BecomesAClosure()
    {
        // Passing a sibling as a value cannot become a direct call, so the lifted body has to
        // rebuild its closure from the captures it already holds as parameters.
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.Call(V("apply"), [V("g")])
                    )
                ),
                (
                    "g",
                    Lambda(
                        "g",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp("+", V("n"), V("x")) { Type = ZType.Int }
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var (_, lifter, _) = LiftInside(group, ("x", ZType.Int));

        var call = Assert.IsType<IrNode.Call>(lifter.LiftedFunctions[0].Body);
        var closure = Assert.IsType<IrNode.Closure>(Assert.Single(call.Args));
        Assert.Equal("__letrec_0_g", closure.LiftedFuncName);
        Assert.Equal("x", Assert.IsType<IrNode.Var>(Assert.Single(closure.CapturedValues)).Name);
    }

    [Fact]
    public void NonFunctionBinding_StaysAnOrdinaryLet()
    {
        var group = Group(
            [
                ("a", new IrNode.IntConst(1) { Type = ZType.Int }),
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp("+", V("n"), V("a")) { Type = ZType.Int }
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var (body, lifter, _) = LiftInside(group);

        var (bindings, _) = Spine(body);
        // Only the value binding survives at the site; `f` is reached through the lifted name, so
        // `a` reaches it as a capture parameter rather than through a closure over a local.
        Assert.Equal(["a"], bindings.Select(b => b.Name));
        Assert.IsType<IrNode.IntConst>(bindings[0].Value);
        Assert.Equal(["a", "n"], Assert.Single(lifter.LiftedFunctions).Params.Select(p => p.Name));
    }

    [Fact]
    public void FunctionBinding_ProducesNoSiteLet()
    {
        // `r` calls `f`. With the substitution live at the site that is a direct call to the
        // lifted name, so there is nothing to materialize and nothing to order — the whole
        // closure-before-first-use dance is gone.
        var group = Group(
            [
                ("f", Lambda("f", [new IrParam("n", ZType.Int)], V("n"))),
                ("r", new IrNode.Call(V("f"), [new IrNode.IntConst(1)]) { Type = ZType.Int }),
            ],
            V("r")
        );

        var (body, _, _) = LiftInside(group);

        var (bindings, _) = Spine(body);
        Assert.Equal(["r"], bindings.Select(b => b.Name));
        var call = Assert.IsType<IrNode.Call>(bindings[0].Value);
        Assert.Equal("__letrec_0_f", Assert.IsType<IrNode.Var>(call.Function).Name);
    }

    [Fact]
    public void GroupWithNoFunctionBindings_LiftsNothing()
    {
        var group = Group(
            [
                ("a", new IrNode.IntConst(1) { Type = ZType.Int }),
                ("b", new IrNode.BinOp("+", V("a"), new IrNode.IntConst(1)) { Type = ZType.Int }),
            ],
            V("b")
        );

        var (body, lifter, diagnostics) = LiftInside(group);

        Assert.Empty(lifter.LiftedFunctions);
        Assert.False(diagnostics.HasErrors);
        var (bindings, _) = Spine(body);
        Assert.Equal(["a", "b"], bindings.Select(b => b.Name));
    }

    [Fact]
    public void NestedGroups_GetDistinctLiftedNames()
    {
        var inner = Group(
            [("g", Lambda("g", [new IrParam("n", ZType.Int)], new IrNode.Call(V("g"), [V("n")])))],
            new IrNode.Call(V("g"), [new IrNode.IntConst(1)])
        );
        var outer = Group(
            [("f", Lambda("f", [new IrParam("n", ZType.Int)], inner))],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var (_, lifter, diagnostics) = LiftInside(outer);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        Assert.Equal(["__letrec_1_g", "__letrec_0_f"], lifter.LiftedFunctions.Select(f => f.Name));
    }

    [Fact]
    public void NoLetRecNodeSurvives()
    {
        // The whole design rests on this: every other pass and both backends hand-roll switches
        // that fall through silently on an unknown node, so a surviving LetRec would miscompile
        // quietly rather than fail.
        var group = Group(
            [
                ("a", new IrNode.IntConst(1) { Type = ZType.Int }),
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.Call(
                            V("f"),
                            [new IrNode.BinOp("+", V("n"), V("a")) { Type = ZType.Int }]
                        )
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var (body, lifter, _) = LiftInside(group);

        Assert.DoesNotContain(Walk(body), n => n is IrNode.LetRec);
        foreach (var lifted in lifter.LiftedFunctions)
            Assert.DoesNotContain(Walk(lifted), n => n is IrNode.LetRec);
    }

    [Fact]
    public void GroupInsideAClassConstructor_IsStillLifted()
    {
        // Regression (found by the differential fuzzer): the ClassDecl case only walked
        // Methods, so a group in a constructor's super-args, field initializers or body
        // survived to codegen and both backends failed with "emission not implemented for
        // LetRec". Every expression position inside a class has to be reached.
        var group = Group(
            [("f", Lambda("f", [new IrParam("n", ZType.Int)], new IrNode.Call(V("f"), [V("n")])))],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var classDecl = new IrNode.ClassDecl(
            "C",
            [],
            [],
            [new IrField("state", ZType.Int)],
            [],
            Constructor: new IrConstructor([], [group], [("state", group)], [group])
        );

        var diagnostics = new DiagnosticBag();
        var lifter = new LetrecLifter(diagnostics);
        var result = Assert.IsType<IrNode.ClassDecl>(lifter.Lift(classDecl));

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        Assert.DoesNotContain(Walk(result.Constructor!.SuperArgs![0]), n => n is IrNode.LetRec);
        Assert.DoesNotContain(Walk(result.Constructor.FieldSets[0].Value), n => n is IrNode.LetRec);
        Assert.DoesNotContain(Walk(result.Constructor.BodyExprs[0]), n => n is IrNode.LetRec);
        Assert.Equal(3, lifter.LiftedFunctions.Count);
    }

    [Fact]
    public void GroupReadingAnImmutableClassField_CapturesItByValue()
    {
        // A field that cannot change after construction is captured like any enclosing local:
        // the site reads it through `this` (it is inside the method) and the lifted function
        // takes it as a leading parameter. That is exactly what the refusal here used to tell
        // the author to do by hand, so doing it for them costs no new machinery at all.
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp("+", V("state"), new IrNode.Call(V("f"), [V("n")]))
                        {
                            Type = ZType.Int,
                        }
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var classDecl = new IrNode.ClassDecl(
            "C",
            [],
            [],
            [new IrField("state", ZType.Int)],
            [new IrObjectMethod("M", [], ZType.Int, group)]
        );

        var diagnostics = new DiagnosticBag();
        var lifter = new LetrecLifter(diagnostics);
        lifter.Lift(classDecl);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        var lifted = Assert.Single(lifter.LiftedFunctions);
        Assert.Equal("state", lifted.Params[0].Name);
        Assert.Equal("n", lifted.Params[1].Name);
    }

    [Fact]
    public void GroupReadingAMutableClassField_IsHostedOnTheClass()
    {
        // A `#:mutable` field cannot be captured by value — that would freeze what the loop
        // sees while the source can still observe a write through `this`. Hosting the group on
        // the class reads it through `this` on every iteration instead, which is what the
        // source says.
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp("+", V("state"), new IrNode.Call(V("f"), [V("n")]))
                        {
                            Type = ZType.Int,
                        }
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var classDecl = new IrNode.ClassDecl(
            "C",
            [],
            [],
            [new IrField("state", ZType.Int, IsMutable: true)],
            [new IrObjectMethod("M", [], ZType.Int, group)]
        );

        var diagnostics = new DiagnosticBag();
        var lifter = new LetrecLifter(diagnostics);
        var result = Assert.IsType<IrNode.ClassDecl>(lifter.Lift(classDecl));

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        Assert.Empty(lifter.LiftedFunctions);
        var helper = result.Methods[^1];
        Assert.True(helper.IsSynthesizedHelper);
        // Read through `this`, not frozen into a parameter.
        Assert.DoesNotContain(helper.Params, p => p.Name == "state");
        Assert.Contains(Walk(helper.Body), n => n is IrNode.Var { Name: "state" });
    }

    [Fact]
    public void GroupNeedingAnInstanceOutsideAnyClass_ReportsError()
    {
        // The refusal only stands when there is no class to host the group on. Here the
        // sibling-method name is not a method of anything.
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.Seq(
                            [
                                new IrNode.SetField("state", V("n")),
                                new IrNode.Call(V("f"), [V("n")]),
                            ]
                        )
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var classDecl = new IrNode.ClassDecl(
            "C",
            [],
            [],
            [new IrField("state", ZType.Int, IsMutable: true)],
            [],
            // In a constructor: fields are not in bare-name scope, and neither emitter has the
            // class's method map live while emitting one, so a helper is unavailable here.
            Constructor: new IrConstructor([], null, [], [group])
        );

        var diagnostics = new DiagnosticBag();
        new LetrecLifter(diagnostics).Lift(classDecl);

        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.Diagnostics, d => d.Message.Contains("assigns a field"));
    }

    [Fact]
    public void GroupCallingASiblingMethod_IsHostedOnTheClass()
    {
        // TypeInferer puts sibling methods in scope by bare name, so `(Twice n)` here type
        // checks and arrives as Call(Var("Twice"), …). A static has no receiver to make that
        // call with, but a private method of the same class does — and the call needs no
        // rewriting at all, since a bare name in a method body is what both emitters already
        // resolve to `this.Twice`.
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp(
                            "+",
                            new IrNode.Call(V("Twice"), [V("n")]) { Type = ZType.Int },
                            new IrNode.Call(V("f"), [V("n")])
                        )
                        {
                            Type = ZType.Int,
                        }
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var classDecl = new IrNode.ClassDecl(
            "C",
            [],
            [],
            [],
            [
                new IrObjectMethod("Twice", [new IrParam("n", ZType.Int)], ZType.Int, V("n")),
                new IrObjectMethod("M", [], ZType.Int, group),
            ]
        );

        var diagnostics = new DiagnosticBag();
        var lifter = new LetrecLifter(diagnostics);
        var result = Assert.IsType<IrNode.ClassDecl>(lifter.Lift(classDecl));

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        Assert.Empty(lifter.LiftedFunctions);
        Assert.Equal(3, result.Methods.Count);
        var helper = result.Methods[^1];
        Assert.True(helper.IsSynthesizedHelper);
        Assert.StartsWith("__letrec_", helper.Name);
    }

    [Fact]
    public void GroupAssigningAField_IsHostedOnTheClass()
    {
        // A `set!` names its target implicitly — IrNode.SetField carries the field name, not a
        // Var — so only the dedicated scan sees it. Hosted on the class, the write needs no
        // rewriting: SetField's implicit receiver is the `this` the method already has.
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.Seq(
                            [
                                new IrNode.SetField("state", V("n")),
                                new IrNode.Call(V("f"), [V("n")]),
                            ]
                        )
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var classDecl = new IrNode.ClassDecl(
            "C",
            [],
            [],
            [new IrField("state", ZType.Int, IsMutable: true)],
            [new IrObjectMethod("M", [], ZType.Int, group)]
        );

        var diagnostics = new DiagnosticBag();
        var lifter = new LetrecLifter(diagnostics);
        var result = Assert.IsType<IrNode.ClassDecl>(lifter.Lift(classDecl));

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        Assert.Empty(lifter.LiftedFunctions);
        Assert.True(result.Methods[^1].IsSynthesizedHelper);
        Assert.Contains(Walk(result.Methods[^1].Body), n => n is IrNode.SetField);
    }

    [Fact]
    public void GroupOutsideAClass_IsUnaffectedByTheInstanceChecks()
    {
        // The two checks above key off the enclosing class's members. A group at top level has
        // none, so a local named like some class's method must still lift untouched.
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.Call(V("f"), [V("n")])
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var (body, lifter, diagnostics) = LiftInside(group);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        Assert.Single(lifter.LiftedFunctions);
        Assert.DoesNotContain(Walk(body), n => n is IrNode.LetRec);
    }

    [Fact]
    public void GroupInAConstructorNamingAField_IsStillLifted()
    {
        // A constructor's scope binds only its own parameters, so a bare `state` there is the
        // module-level function of that name, not this.State — which is how both emitters
        // resolve it. Carrying the field set into the constructor blamed the group for reading
        // something the source never named, refusing a group that lifts perfectly well.
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp("+", V("state"), new IrNode.Call(V("f"), [V("n")]))
                        {
                            Type = ZType.Int,
                        }
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var classDecl = new IrNode.ClassDecl(
            "C",
            [],
            [],
            [new IrField("state", ZType.Int)],
            [],
            Constructor: new IrConstructor([], null, [], [group])
        );

        var diagnostics = new DiagnosticBag();
        var lifter = new LetrecLifter(diagnostics);
        var result = Assert.IsType<IrNode.ClassDecl>(lifter.Lift(classDecl));

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        Assert.Single(lifter.LiftedFunctions);
        Assert.DoesNotContain(Walk(result.Constructor!.BodyExprs[0]), n => n is IrNode.LetRec);
    }

    [Fact]
    public void ObjectLiftedGroupReadingAnInheritedField_IsStillLifted()
    {
        // An `(object ...)` body does not bring the base class's fields into bare-name scope,
        // so `inherited` here is a module-level function reference. Walking the base chain for
        // an object-lifted class counted it as instance state and refused the group; both
        // emitters draw the line the other way (the IsObjectLifted guard on the field set).
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp("+", V("inherited"), new IrNode.Call(V("f"), [V("n")]))
                        {
                            Type = ZType.Int,
                        }
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var baseClass = new IrNode.ClassDecl(
            "Base",
            [],
            [],
            [new IrField("inherited", ZType.Int)],
            [],
            IsOpen: true
        );
        var lifted = new IrNode.ClassDecl(
            "__Object_0",
            [],
            [],
            [new IrField("captured", ZType.Int)],
            [new IrObjectMethod("M", [], ZType.Int, group)],
            BaseClassName: "Base",
            IsObjectLifted: true
        );

        var diagnostics = new DiagnosticBag();
        var lifter = new LetrecLifter(diagnostics);
        lifter.Lift(new IrNode.Seq([baseClass, lifted]));

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        Assert.Single(lifter.LiftedFunctions);
    }

    [Fact]
    public void ObjectLiftedGroupReadingItsOwnField_CapturesItByValue()
    {
        // Every field of an object-lifted class stands for a captured local, so it is immutable
        // by construction — which makes this the shape that benefits most from capturing rather
        // than refusing. ObjectLifter itself has to see the reference for the field to exist at
        // all; see ObjectLifterTests.CapturesAVariableReadOnlyInsideANestedDefine.
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp("+", V("captured"), new IrNode.Call(V("f"), [V("n")]))
                        {
                            Type = ZType.Int,
                        }
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var lifted = new IrNode.ClassDecl(
            "__Object_0",
            [],
            [],
            [new IrField("captured", ZType.Int)],
            [new IrObjectMethod("M", [], ZType.Int, group)],
            IsObjectLifted: true
        );

        var diagnostics = new DiagnosticBag();
        var lifter = new LetrecLifter(diagnostics);
        lifter.Lift(lifted);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        var liftedFunc = Assert.Single(lifter.LiftedFunctions);
        Assert.Equal("captured", liftedFunc.Params[0].Name);
    }

    [Fact]
    public void InstanceScan_CoversEveryIrNodeThatCanHoldOne()
    {
        // TouchesInstanceImplicitly answers "no" by default, which is only sound while every
        // node kind that can *contain* a SetField or SuperMethodCall has an arm. The IR has no
        // shared visitor, so a node kind added later would silently escape the scan and
        // reinstate the miscompile GroupAssigningAField_ReportsError pins. Reflection over the
        // hierarchy is what keeps that from going unnoticed.
        var scan = typeof(LetrecLifter).GetMethod(
            "TouchesInstanceImplicitly",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(scan);


        var setField = new IrNode.SetField("state", new IrNode.IntConst(1) { Type = ZType.Int });

        var missing = new List<string>();
        foreach (var nodeType in typeof(IrNode).Assembly.GetTypes())
        {
            if (!nodeType.IsAssignableTo(typeof(IrNode)) || nodeType.IsAbstract)
                continue;

            // Only node kinds with at least one IrNode-typed child can hide a SetField.
            var carriers = nodeType
                .GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Where(p =>
                    p.ParameterType.IsAssignableTo(typeof(IrNode))
                    || (
                        p.ParameterType.IsGenericType
                        && p.ParameterType.GetGenericArguments()
                            .Any(a => a.IsAssignableTo(typeof(IrNode)))
                    )
                )
                .ToList();
            if (carriers.Count == 0)
                continue;

            if (!ScanHasArmFor(nodeType))
                missing.Add(nodeType.Name);
        }

        Assert.True(
            missing.Count == 0,
            "TouchesInstanceImplicitly has no arm for: " + string.Join(", ", missing)
        );

        // And the scan really does find one, so the reflection above is not vacuous.
        Assert.True((bool)scan.Invoke(null, [setField])!);
        Assert.False((bool)scan.Invoke(null, [new IrNode.IntConst(1) { Type = ZType.Int }])!);

        static bool ScanHasArmFor(Type nodeType)
        {
            // The switch is source, not metadata, so the arm list is read from the file the
            // scan lives in — the same trick SpanPreservationTests uses to avoid a hand-copied
            // list drifting from the thing it describes.
            var source = File.ReadAllText(LetrecLifterSourcePath());
            var body = source[source.IndexOf(
                "private static bool TouchesInstanceImplicitly",
                StringComparison.Ordinal
            )..];
            body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];
            return body.Contains($"IrNode.{nodeType.Name} ", StringComparison.Ordinal)
                || body.Contains($"IrNode.{nodeType.Name}$", StringComparison.Ordinal)
                || body.Contains($"IrNode.{nodeType.Name} =>", StringComparison.Ordinal);
        }
    }

    private static string LetrecLifterSourcePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(
            dir.FullName,
            "src",
            "ZScheme.Compiler",
            "Ir",
            "LetrecLifter.cs"
        );
    }

    [Fact]
    public void PreservesSourceSpans()
    {
        // Rewriting passes that drop spans have silently broken coverage instrumentation here
        // before; the spine this pass builds must keep pointing at the original form.
        var span = new SourceSpan("test.zs", 7, 3, 12);
        var group = Group(
            [
                ("a", new IrNode.IntConst(1) { Type = ZType.Int }),
                ("f", Lambda("f", [new IrParam("n", ZType.Int)], V("n"))),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)]),
            span
        );

        var (body, _, _) = LiftInside(group);

        var let = Assert.IsType<IrNode.Let>(body);
        Assert.Equal(span, let.Span);
    }

    // The site body above only reaches the let spine. This one spans every input node and then
    // checks the whole output — site *and* lifted bodies — because the two Var nodes this pass
    // synthesizes (the retargeted callee and the rebuilt capture arguments) live inside a lifted
    // body, where a root-only assertion never looks.
    [Fact]
    public void PreservesSourceSpansThroughoutLiftedFunctions()
    {
        var span = new SourceSpan("test.zs", 11, 5, 20);

        IrNode.Var Spanned(string name) => new(name) { Type = ZType.Int, Span = span };

        // (letrec ([f (lambda (n) (+ factor (f n)))]) (f 1)), with `factor` an enclosing local.
        // The self-call inside the lifted body drives the sibling-call retarget, and capturing
        // `factor` drives the rebuilt capture-argument references.
        var lambdaBody = new IrNode.BinOp(
            "+",
            Spanned("factor"),
            new IrNode.Call(Spanned("f"), [Spanned("n")]) { Type = ZType.Int, Span = span }
        )
        {
            Type = ZType.Int,
            Span = span,
        };
        var lambda = new IrNode.FuncDef(
            "f",
            [new IrParam("n", ZType.Int)],
            ZType.Int,
            lambdaBody,
            IsSelfRecursive: true
        )
        {
            Type = new ZType.ZFuncType([ZType.Int], ZType.Int),
            Span = span,
        };
        var group = Group(
            [("f", lambda)],
            new IrNode.Call(
                Spanned("f"),
                [new IrNode.IntConst(1) { Type = ZType.Int, Span = span }]
            )
            {
                Type = ZType.Int,
                Span = span,
            },
            span
        );

        var (body, lifter, diagnostics) = LiftInside(group, ("factor", ZType.Int));

        Assert.False(diagnostics.HasErrors);
        Assert.NotEmpty(lifter.LiftedFunctions);

        // Every input node carried a span, so anything missing one now was introduced here.
        foreach (var node in lifter.LiftedFunctions.Cast<IrNode>().Prepend(body))
            Assert.All(
                IrWalker.DescendantsAndSelf(node),
                n => Assert.False(IrWalker.HasNoSpan(n.Span), $"{n.GetType().Name} lost its span")
            );
    }

    [Fact]
    public void NestedGroup_CallsAnOuterSiblingDirectly()
    {
        // The inner group's lifted body has to keep the outer group's substitution: `outer` is no
        // longer a local at the site, so treating it as a capture would reference a name that
        // does not exist. It has to become a direct call to the outer lifted function.
        var inner = Group(
            [
                (
                    "g",
                    Lambda(
                        "g",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.Call(V("outer"), [V("n")])
                    )
                ),
            ],
            new IrNode.Call(V("g"), [new IrNode.IntConst(1)])
        );
        var outer = Group(
            [("outer", Lambda("outer", [new IrParam("k", ZType.Int)], V("k"))), ("r", inner)],
            V("r")
        );

        var (_, lifter, diagnostics) = LiftInside(outer);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        var innerLifted = Assert.Single(lifter.LiftedFunctions, f => f.Name == "__letrec_1_g");
        var call = Assert.IsType<IrNode.Call>(innerLifted.Body);
        Assert.Equal("__letrec_0_outer", Assert.IsType<IrNode.Var>(call.Function).Name);
        // `outer` takes no captures, so nothing is prepended and `g` carries no capture param.
        Assert.Equal(["n"], innerLifted.Params.Select(p => p.Name));
    }

    [Fact]
    public void NestedGroup_InheritsTheCapturesOfTheOuterMemberItCalls()
    {
        // Regression (found by the differential fuzzer): `g` references `outer`, which is no longer
        // a value it can capture — the call is retargeted at `__letrec_0_outer` and has to supply
        // that function's capture `x`. So `x` has to become a capture of `g` too, or the rewritten
        // call names a variable that is not in scope inside the lifted body ("Variable 'x' not
        // found for AsmResolver IL emission").
        var inner = Group(
            [
                (
                    "g",
                    Lambda(
                        "g",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.Call(V("outer"), [V("n")])
                    )
                ),
            ],
            new IrNode.Call(V("g"), [new IrNode.IntConst(1)])
        );
        var outer = Group(
            [
                (
                    "outer",
                    Lambda(
                        "outer",
                        [new IrParam("k", ZType.Int)],
                        new IrNode.BinOp("+", V("k"), V("x")) { Type = ZType.Int }
                    )
                ),
                ("r", inner),
            ],
            V("r")
        );

        var (_, lifter, diagnostics) = LiftInside(outer, ("x", ZType.Int));

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        var innerLifted = Assert.Single(lifter.LiftedFunctions, f => f.Name == "__letrec_1_g");
        Assert.Equal(["x", "n"], innerLifted.Params.Select(p => p.Name));
        // And the retargeted call forwards it as the outer function's leading argument.
        var call = Assert.IsType<IrNode.Call>(innerLifted.Body);
        Assert.Equal("__letrec_0_outer", Assert.IsType<IrNode.Var>(call.Function).Name);
        Assert.Equal("x", Assert.IsType<IrNode.Var>(call.Args[0]).Name);
    }

    [Fact]
    public void GenericGroup_LiftedFunctionDeclaresItsOwnTypeParams()
    {
        // A group inside a generic function used to be rejected outright. The lifted function is
        // instead made generic over the type variables its own signature mentions — both backends
        // already instantiate a generic call site explicitly.
        var typeVar = new ZType.ZTypeVar(1);
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.Call(V("f"), [V("x", typeVar)])
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var (_, lifter, diagnostics) = LiftInside(group, ("x", typeVar));

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        var lifted = Assert.Single(lifter.LiftedFunctions);
        // The capture `x : ^1` is prepended, so the lifted signature mentions one type variable.
        Assert.Equal(["x", "n"], lifted.Params.Select(p => p.Name));
        Assert.Equal(["T0"], lifted.TypeParams);
    }

    [Fact]
    public void GenericGroup_RemapsEnclosingConstraintIndices()
    {
        // The enclosing function's T1 and the lifted function's T0 are the same type variable at
        // different indices, because the lifted signature mentions only a subset. Routing the
        // constraint through the type-var id is what keeps them lined up.
        var used = new ZType.ZTypeVar(9);
        var unused = new ZType.ZTypeVar(3);
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.Call(V("f"), [V("b", used)])
                    )
                ),
            ],
            new IrNode.Call(V("f"), [new IrNode.IntConst(1)])
        );

        var enclosing = new IrNode.FuncDef(
            "outer",
            [new IrParam("a", unused), new IrParam("b", used)],
            ZType.Int,
            group,
            false,
            TypeParams: ["T0", "T1"],
            TypeParamConstraints: new Dictionary<string, GenericConstraintKind>
            {
                ["T1"] = GenericConstraintKind.Unmanaged,
            }
        )
        {
            // T0 is the smaller id (3, `a`); T1 is id 9 (`b`), the one the group uses.
            Type = new ZType.ZFuncType([unused, used], ZType.Int),
        };

        var diagnostics = new DiagnosticBag();
        var lifter = new LetrecLifter(diagnostics);
        lifter.Lift(enclosing);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Diagnostics));
        var lifted = Assert.Single(lifter.LiftedFunctions);
        Assert.Equal(["T0"], lifted.TypeParams);
        Assert.NotNull(lifted.TypeParamConstraints);
        Assert.Equal(
            GenericConstraintKind.Unmanaged,
            Assert.Contains("T0", lifted.TypeParamConstraints)
        );
    }

    [Fact]
    public void GenericGroupMemberInValuePosition_ReportsError()
    {
        // A generic lifted function cannot become a delegate: IrNode.Closure has nowhere to put
        // the type arguments. Only value position is affected — a direct call is fine.
        var typeVar = new ZType.ZTypeVar(1);
        var group = Group(
            [
                (
                    "f",
                    Lambda(
                        "f",
                        [new IrParam("n", ZType.Int)],
                        new IrNode.BinOp("+", V("n"), V("x", typeVar)) { Type = ZType.Int }
                    )
                ),
            ],
            new IrNode.Call(V("apply"), [V("f")])
        );

        var (_, _, diagnostics) = LiftInside(group, ("x", typeVar));

        Assert.True(diagnostics.HasErrors);
        Assert.Contains(
            diagnostics.Diagnostics,
            d => d.Message.Contains("cannot be turned into a delegate")
        );
    }

    private static IEnumerable<IrNode> Walk(IrNode node)
    {
        yield return node;
        foreach (var child in Children(node))
        foreach (var descendant in Walk(child))
            yield return descendant;
    }

    private static IEnumerable<IrNode> Children(IrNode node)
    {
        return node switch
        {
            IrNode.Seq s => s.Nodes,
            IrNode.Let l => [l.Value, l.Body],
            IrNode.LetRec lr => [.. lr.Bindings.Select(b => b.Value), lr.Body],
            IrNode.Use u => [u.Value, u.Body],
            IrNode.If i => [i.Condition, i.Then, i.Else],
            IrNode.Call c => [c.Function, .. c.Args],
            IrNode.BinOp b => [b.Left, b.Right],
            IrNode.UnaryOp u => [u.Operand],
            IrNode.FuncDef f => [f.Body],
            IrNode.Closure c => c.CapturedValues,
            IrNode.Match m => [m.Scrutinee, .. m.Arms.Select(a => a.Body)],
            _ => [],
        };
    }
}
