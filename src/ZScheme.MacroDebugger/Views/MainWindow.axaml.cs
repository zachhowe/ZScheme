using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ZScheme.Compiler.Syntax;
using ZScheme.MacroDebugger.ViewModels;

namespace ZScheme.MacroDebugger.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.OpenFileInteraction = null;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.OpenFileInteraction = () => _ = OpenFileAsync();
            RenderPanes();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // BeforeText/AfterText (and the highlights) always change together; AfterText is
        // raised last, so rendering on it repaints both panes exactly once per step change.
        if (e.PropertyName == nameof(MainWindowViewModel.AfterText))
            RenderPanes();
    }

    private void RenderPanes()
    {
        if (_viewModel is null)
            return;

        RenderPane(
            BeforePane,
            BeforeScroll,
            _viewModel.BeforeText,
            _viewModel.BeforeFocus,
            _viewModel.BeforeHighlight,
            "RedexHighlightBrush"
        );
        RenderPane(
            AfterPane,
            AfterScroll,
            _viewModel.AfterText,
            _viewModel.AfterFocus,
            _viewModel.AfterHighlight,
            "ResultHighlightBrush"
        );
    }

    /// <summary>
    ///     Renders the full-file text as up to five runs: dimmed context above the focused
    ///     form, the form itself with its redex/result highlighted, and dimmed context below.
    ///     A missing/invalid focus treats the whole text as focused (the zero-step fallback
    ///     view), reducing to plain text with an optional highlight.
    /// </summary>
    private void RenderPane(
        SelectableTextBlock block,
        ScrollViewer viewer,
        string text,
        SExprPrinter.TextSpan? focus,
        SExprPrinter.TextSpan? highlight,
        string brushKey
    )
    {
        var inlines = block.Inlines ??= new InlineCollection();
        inlines.Clear();

        var focusSpan =
            focus is { } f && f.Start >= 0 && f.Start + f.Length <= text.Length
                ? f
                : new SExprPrinter.TextSpan(0, text.Length);
        var focusEnd = focusSpan.Start + focusSpan.Length;

        SExprPrinter.TextSpan? highlightSpan =
            highlight is { Length: > 0 } h && h.Start >= focusSpan.Start && h.Start + h.Length <= focusEnd
                ? h
                : null;

        var dimBrush =
            this.TryFindResource("DimContextBrush", out var dimResource)
            && dimResource is IBrush dim
                ? dim
                : null;
        var highlightBrush =
            this.TryFindResource(brushKey, out var resource) && resource is IBrush brush
                ? brush
                : null;
        if (highlightBrush is null)
            highlightSpan = null;

        void AddRun(int start, int end, IBrush? foreground = null, IBrush? background = null)
        {
            if (end <= start)
                return;
            var run = new Run(text[start..end]);
            if (foreground is not null)
                run.Foreground = foreground;
            if (background is not null)
                run.Background = background;
            inlines.Add(run);
        }

        AddRun(0, focusSpan.Start, foreground: dimBrush);
        if (highlightSpan is { } hl)
        {
            AddRun(focusSpan.Start, hl.Start);
            AddRun(hl.Start, hl.Start + hl.Length, background: highlightBrush);
            AddRun(hl.Start + hl.Length, focusEnd);
        }
        else
        {
            AddRun(focusSpan.Start, focusEnd);
        }
        AddRun(focusEnd, text.Length, foreground: dimBrush);

        if (focus is not null)
            ScrollToOffset(viewer, block, highlightSpan?.Start ?? focusSpan.Start);
    }

    /// <summary>
    ///     Vertically centers the given character offset in the pane on the next layout pass —
    ///     the earliest moment the new inlines are measured and the viewer's extent/viewport
    ///     reflect them (a dispatcher post can run before that and see a stale extent).
    /// </summary>
    private static void ScrollToOffset(ScrollViewer viewer, SelectableTextBlock pane, int charOffset)
    {
        EventHandler? onLayoutUpdated = null;
        onLayoutUpdated = (_, _) =>
        {
            pane.LayoutUpdated -= onLayoutUpdated;
            if (pane.TextLayout is not { } layout)
                return;
            var lineTop = layout.HitTestTextPosition(charOffset).Y;
            var targetY = lineTop + pane.Margin.Top - viewer.Viewport.Height / 2;
            var maxY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            viewer.Offset = viewer.Offset.WithY(Math.Clamp(targetY, 0, maxY));
        };
        pane.LayoutUpdated += onLayoutUpdated;
    }

    private async Task OpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open ZScheme source",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("ZScheme source") { Patterns = ["*.zs"] },
                    FilePickerFileTypes.All,
                ],
            }
        );

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is not null && _viewModel is not null)
            await _viewModel.LoadFileAsync(path);
    }
}
