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

        SetHighlightedText(
            BeforePane,
            _viewModel.BeforeText,
            _viewModel.BeforeHighlight,
            "RedexHighlightBrush"
        );
        SetHighlightedText(
            AfterPane,
            _viewModel.AfterText,
            _viewModel.AfterHighlight,
            "ResultHighlightBrush"
        );
    }

    private void SetHighlightedText(
        SelectableTextBlock block,
        string text,
        SExprPrinter.TextSpan? highlight,
        string brushKey
    )
    {
        var inlines = block.Inlines ??= new InlineCollection();
        inlines.Clear();

        if (
            highlight is { Length: > 0 } span
            && span.Start >= 0
            && span.Start + span.Length <= text.Length
            && this.TryFindResource(brushKey, out var resource)
            && resource is IBrush brush
        )
        {
            if (span.Start > 0)
                inlines.Add(new Run(text[..span.Start]));
            inlines.Add(new Run(text.Substring(span.Start, span.Length)) { Background = brush });
            if (span.Start + span.Length < text.Length)
                inlines.Add(new Run(text[(span.Start + span.Length)..]));
        }
        else
        {
            inlines.Add(new Run(text));
        }
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
