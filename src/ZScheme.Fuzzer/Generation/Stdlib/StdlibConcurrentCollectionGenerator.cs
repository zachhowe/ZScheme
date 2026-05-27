using System.Globalization;

namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over the four `stdlib/concurrent/*` modules:
// queue, stack, bag, dictionary. Element type is pinned to Int by always
// performing >=1 enqueue/push/add/put before any read; dictionary keys are
// pinned to Int (value type → satisfies the `^k notnull` constraint).
public sealed class StdlibConcurrentCollectionGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibConcurrentCollectionGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool QueueImported()
    {
        return _ctx.Imports.Contains(StdlibImport.ConcurrentQueue);
    }

    public bool StackImported()
    {
        return _ctx.Imports.Contains(StdlibImport.ConcurrentStack);
    }

    public bool BagImported()
    {
        return _ctx.Imports.Contains(StdlibImport.ConcurrentBag);
    }

    public bool DictionaryImported()
    {
        return _ctx.Imports.Contains(StdlibImport.ConcurrentDictionary);
    }

    // (let [q (concurrent-queue/new)] (begin (concurrent-queue/enqueue! q E1) ... (concurrent-queue/count q)))
    public string QueueCountToInt(Scope scope, int depth)
    {
        return BuildAndReduce(scope, depth, "concurrent-queue/new", "concurrent-queue/enqueue!",
            "concurrent-queue/count");
    }

    // (value/1 (concurrent-queue/try-dequeue! q)) — enqueue >=1 first to keep the bool true.
    public string QueueTryDequeueToInt(Scope scope, int depth)
    {
        return BuildAndReduceTryRead(scope, depth, "concurrent-queue/new", "concurrent-queue/enqueue!",
            "concurrent-queue/try-dequeue!");
    }

    public string StackCountToInt(Scope scope, int depth)
    {
        return BuildAndReduce(scope, depth, "concurrent-stack/new", "concurrent-stack/push!", "concurrent-stack/count");
    }

    public string StackTryPopToInt(Scope scope, int depth)
    {
        return BuildAndReduceTryRead(scope, depth, "concurrent-stack/new", "concurrent-stack/push!",
            "concurrent-stack/try-pop!");
    }

    public string BagCountToInt(Scope scope, int depth)
    {
        return BuildAndReduce(scope, depth, "concurrent-bag/new", "concurrent-bag/add!", "concurrent-bag/count");
    }

    public string BagTryTakeToInt(Scope scope, int depth)
    {
        return BuildAndReduceTryRead(scope, depth, "concurrent-bag/new", "concurrent-bag/add!",
            "concurrent-bag/try-take!");
    }

    // Dictionary: (let [d (concurrent-dictionary/new)] (begin (put! d k1 v1) ... (count d)))
    public string DictionaryCountToInt(Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var n = 1 + _ctx.Rng.Next(3);
        var puts = new List<string>(n);
        for (var i = 0; i < n; i++)
        {
            var key = i.ToString(CultureInfo.InvariantCulture);
            var val = _exprs.GenInt(scope, depth - 1);
            puts.Add($"(concurrent-dictionary/put! {v} {key} {val})");
        }

        return
            $"(let [{v} (concurrent-dictionary/new)] (begin {string.Join(" ", puts)} (concurrent-dictionary/count {v})))";
    }

    // (value/1 (concurrent-dictionary/try-remove! d 0)) — populate key 0 first.
    public string DictionaryTryRemoveToInt(Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var seedVal = _exprs.GenInt(scope, depth - 1);
        return
            $"(let [{v} (concurrent-dictionary/new)] (begin (concurrent-dictionary/put! {v} 0 {seedVal}) (value/1 (concurrent-dictionary/try-remove! {v} 0))))";
    }

    private string BuildAndReduce(Scope scope, int depth, string newFn, string addFn, string readFn)
    {
        var v = _ctx.Fresh();
        var n = 1 + _ctx.Rng.Next(3); // 1..3
        var adds = new List<string>(n);
        for (var i = 0; i < n; i++)
        {
            var elem = _exprs.GenInt(scope, depth - 1);
            adds.Add($"({addFn} {v} {elem})");
        }

        return $"(let [{v} ({newFn})] (begin {string.Join(" ", adds)} ({readFn} {v})))";
    }

    // Try-read variant: pin element type via a seeded add, then read via value/1
    // of the (Bool, Int) tuple. Always seed to keep the bool true so codegen of
    // the tuple-return path is exercised on success.
    private string BuildAndReduceTryRead(Scope scope, int depth, string newFn, string addFn, string tryFn)
    {
        var v = _ctx.Fresh();
        var seed = _exprs.GenInt(scope, depth - 1);
        return $"(let [{v} ({newFn})] (begin ({addFn} {v} {seed}) (value/1 ({tryFn} {v}))))";
    }
}
