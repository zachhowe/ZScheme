namespace ZScheme.MacroDebugger.ViewModels;

/// <summary>
///     One outermost macro call written in source (a Depth == 0 step). The site dropdown
///     jumps to <see cref="FirstStepIndex" />; the steps that follow it (until the next
///     site's first step) are the cascading rewrites it triggered.
/// </summary>
public sealed record ExpansionSite(int Ordinal, int FirstStepIndex, string Label);
