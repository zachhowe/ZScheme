namespace ZScheme.Runtime;

/// <summary>
///     Code-coverage counters and metadata for a single compiled ZScheme program. The IL backend
///     imports <see cref="Hit" /> (and the <see cref="Hits" />/<see cref="Meta" /> fields) into
///     each instrumented assembly rather than synthesizing an equivalent type per compilation; a
///     module initializer in the compiled assembly sizes <see cref="Hits" /> and sets
///     <see cref="Meta" /> before any probe fires. Since each compiled assembly loads its own copy
///     of this type (see <c>ZScheme.Compiler.Package.PackageTester</c>, which runs each test DLL in
///     its own collectible <c>AssemblyLoadContext</c>), this static state does not leak across
///     independently loaded programs.
/// </summary>
public static class ZSchemeCoverage
{
    public static int[] Hits = [];
    public static string Meta = "";

    public static void Hit(int id) => Hits[id] = Hits[id] + 1;
}
