using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EdgeForge.Core;

namespace EdgeForge.Ui;

public partial class App : Application
{
    private readonly EdgeForgeOptions _options;

    // Parameterless ctor exists for the XAML previewer only.
    public App() : this(new EdgeForgeOptions())
    {
    }

    public App(EdgeForgeOptions options) => _options = options;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(new MainWindowViewModel(_options));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
