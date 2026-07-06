using System.Text;
using ZScheme.Compiler.Syntax;
using ZScheme.MacroDebugger.Services;

namespace ZScheme.MacroDebugger.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private int _currentIndex = -1;
    private string _diagnosticsText = "";

    private string _fallbackAfterText = "";

    private string _fallbackBeforeText = "";
    private string? _filePath;
    private bool _hasDiagnostics;
    private bool _isBusy;
    private ExpansionSite? _selectedSite;
    private IReadOnlyList<ExpansionSite> _sites = [];
    private string _statusText = "Open a .zs file to begin (Ctrl+O)";
    private IReadOnlyList<StepViewModel> _steps = [];
    private bool _syncingSite;

    public MainWindowViewModel()
    {
        FirstCommand = new RelayCommand(() => CurrentIndex = 0, () => CurrentIndex > 0);
        PrevCommand = new RelayCommand(() => CurrentIndex -= 1, () => CurrentIndex > 0);
        NextCommand = new RelayCommand(
            () => CurrentIndex += 1,
            () => CurrentIndex >= 0 && CurrentIndex < Steps.Count - 1
        );
        LastCommand = new RelayCommand(
            () => CurrentIndex = Steps.Count - 1,
            () => CurrentIndex >= 0 && CurrentIndex < Steps.Count - 1
        );
        ReloadCommand = new RelayCommand(
            () =>
            {
                if (FilePath is not null)
                    _ = LoadFileAsync(FilePath);
            },
            () => FilePath is not null && !IsBusy
        );
        OpenCommand = new RelayCommand(() => OpenFileInteraction?.Invoke());
    }

    /// <summary>Set by the view: shows the file picker (needs the window's StorageProvider,
    ///     which the view model must not reference).</summary>
    public Action? OpenFileInteraction { get; set; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand FirstCommand { get; }
    public RelayCommand PrevCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand LastCommand { get; }
    public RelayCommand ReloadCommand { get; }

    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (SetField(ref _filePath, value))
                OnPropertyChanged(nameof(WindowTitle));
        }
    }

    public string WindowTitle =>
        FilePath is null
            ? "ZScheme Macro Stepper"
            : $"ZScheme Macro Stepper — {Path.GetFileName(FilePath)}";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                RaiseCommandStates();
        }
    }

    public IReadOnlyList<StepViewModel> Steps
    {
        get => _steps;
        private set => SetField(ref _steps, value);
    }

    /// <summary>Index into <see cref="Steps" />; -1 when there are no steps.</summary>
    public int CurrentIndex
    {
        get => _currentIndex;
        set
        {
            var clamped = Steps.Count == 0 ? -1 : Math.Clamp(value, 0, Steps.Count - 1);
            if (SetField(ref _currentIndex, clamped))
                NotifyStepChanged();
        }
    }

    public StepViewModel? CurrentStep =>
        CurrentIndex >= 0 && CurrentIndex < Steps.Count ? Steps[CurrentIndex] : null;

    /// <summary>One entry per outermost macro call (Depth == 0 step), in step order.</summary>
    public IReadOnlyList<ExpansionSite> Sites
    {
        get => _sites;
        private set
        {
            if (SetField(ref _sites, value))
                OnPropertyChanged(nameof(HasSites));
        }
    }

    public bool HasSites => Sites.Count > 0;

    /// <summary>
    ///     The site containing the current step. Setting it (from the dropdown) jumps to the
    ///     site's first step; stepping re-syncs it via <see cref="SyncSelectedSite" />, whose
    ///     guard prevents the sync-assignment from snapping the index back.
    /// </summary>
    public ExpansionSite? SelectedSite
    {
        get => _selectedSite;
        set
        {
            if (!SetField(ref _selectedSite, value))
                return;
            if (!_syncingSite && value is not null)
                CurrentIndex = value.FirstStepIndex;
        }
    }

    public string Header =>
        CurrentStep?.Header
        ?? (Steps.Count == 0 && _fallbackAfterText.Length > 0 ? "No macro expansion steps" : "");

    public string RuleText => CurrentStep?.RuleText ?? "";

    public string BeforeText => CurrentStep?.BeforeText ?? _fallbackBeforeText;
    public string AfterText => CurrentStep?.AfterText ?? _fallbackAfterText;
    public SExprPrinter.TextSpan? BeforeFocus => CurrentStep?.BeforeFocus;
    public SExprPrinter.TextSpan? AfterFocus => CurrentStep?.AfterFocus;
    public SExprPrinter.TextSpan? BeforeHighlight => CurrentStep?.BeforeHighlight;
    public SExprPrinter.TextSpan? AfterHighlight => CurrentStep?.AfterHighlight;

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        private set => SetField(ref _diagnosticsText, value);
    }

    public bool HasDiagnostics
    {
        get => _hasDiagnostics;
        private set => SetField(ref _hasDiagnostics, value);
    }

    public async Task LoadFileAsync(string path)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = $"Expanding {path}…";
        try
        {
            var result = await Task.Run(() => ExpansionSession.Run(path));
            Apply(result);
        }
        catch (Exception ex)
        {
            FilePath = path;
            Steps = [];
            Sites = [];
            _fallbackBeforeText = "";
            _fallbackAfterText = "";
            DiagnosticsText = ex.Message;
            HasDiagnostics = true;
            StatusText = $"Failed to load {path}";
            _currentIndex = -1;
            NotifyStepChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Applies an expansion result to the view state. Public so tests can drive the
    ///     view model without touching the filesystem.</summary>
    public void Apply(ExpansionResult result)
    {
        FilePath = result.FilePath;
        var context = DocumentContext.Build(
            result.RawForms,
            result.ExpandedByRawIndex,
            StepViewModel.PrintWidth
        );
        Steps = result
            .Steps.Select(s => new StepViewModel(s, result.Steps.Count, context))
            .ToList();
        Sites = BuildSites(result.Steps);

        _fallbackBeforeText = PrintForms(result.RawForms);
        _fallbackAfterText = PrintForms(result.ExpandedForms);

        DiagnosticsText = string.Join(
            "\n",
            result.Diagnostics.Diagnostics.Select(d => d.ToString())
        );
        HasDiagnostics = result.Diagnostics.Diagnostics.Count > 0;

        StatusText = (result.ExpansionRan, Steps.Count, result.Diagnostics.HasErrors) switch
        {
            (false, _, _) => "Compilation failed before macro expansion",
            (true, 0, false) => "No macro expansion steps in this file",
            (true, var n, false) => $"{n} macro expansion step{(n == 1 ? "" : "s")}",
            (true, var n, true) =>
                $"{n} macro expansion step{(n == 1 ? "" : "s")} — expansion reported errors",
        };

        _currentIndex = Steps.Count > 0 ? 0 : -1;
        NotifyStepChanged();
    }

    private static string PrintForms(IReadOnlyList<SExpr>? forms)
    {
        if (forms is null || forms.Count == 0)
            return "";

        var sb = new StringBuilder();
        foreach (var form in forms)
        {
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append(SExprPrinter.Print(form, null, StepViewModel.PrintWidth).Text);
        }
        return sb.ToString();
    }

    private static List<ExpansionSite> BuildSites(IReadOnlyList<MacroStep> steps)
    {
        var sites = new List<ExpansionSite>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Depth != 0)
                continue;
            var line = step.Redex.Span.Line;
            var label =
                line > 0
                    ? $"{sites.Count + 1}. {step.Macro.Name} — line {line}"
                    : $"{sites.Count + 1}. {step.Macro.Name}";
            sites.Add(new ExpansionSite(sites.Count + 1, i, label));
        }
        return sites;
    }

    /// <summary>Selects the site containing the current step: the last site whose first step
    ///     is at or before <see cref="CurrentIndex" />.</summary>
    private void SyncSelectedSite()
    {
        ExpansionSite? found = null;
        if (CurrentIndex >= 0)
            foreach (var site in Sites)
            {
                if (site.FirstStepIndex > CurrentIndex)
                    break;
                found = site;
            }

        _syncingSite = true;
        try
        {
            SelectedSite = found;
        }
        finally
        {
            _syncingSite = false;
        }
    }

    private void NotifyStepChanged()
    {
        SyncSelectedSite();
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(RuleText));
        OnPropertyChanged(nameof(BeforeFocus));
        OnPropertyChanged(nameof(AfterFocus));
        OnPropertyChanged(nameof(BeforeHighlight));
        OnPropertyChanged(nameof(AfterHighlight));
        OnPropertyChanged(nameof(BeforeText));
        // The view repaints both panes on AfterText; it must stay the last raise here.
        OnPropertyChanged(nameof(AfterText));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        FirstCommand.RaiseCanExecuteChanged();
        PrevCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        LastCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
    }
}
