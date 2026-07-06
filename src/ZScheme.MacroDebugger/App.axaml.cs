using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ZScheme.MacroDebugger.ViewModels;
using ZScheme.MacroDebugger.Views;

namespace ZScheme.MacroDebugger;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            if (desktop.Args is [var filePath, ..])
                _ = viewModel.LoadFileAsync(filePath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
