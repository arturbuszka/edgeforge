using Avalonia;
using EdgeForge.Core;
using EdgeForge.Runtime;

namespace EdgeForge.Ui;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        EdgeForgeOptions options;
        try
        {
            options = LoadOptions();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine($"Invalid configuration: {ex.Message}");
            return 2;
        }

        BuildAvaloniaApp(options).StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Called by the XAML previewer, which cannot pass arguments.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(new EdgeForgeOptions());

    private static AppBuilder BuildAvaloniaApp(EdgeForgeOptions options) =>
        AppBuilder.Configure(() => new App(options))
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// The shared loader: appsettings.json, then the profile named by
    /// EDGEFORGE_PROFILE, then local overrides and environment.
    /// </summary>
    private static EdgeForgeOptions LoadOptions() =>
        EdgeForgeConfiguration.Load(AppContext.BaseDirectory);
}
