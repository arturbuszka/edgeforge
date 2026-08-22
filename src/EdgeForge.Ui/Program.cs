using Avalonia;
using EdgeForge.Core;
using Microsoft.Extensions.Configuration;

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
    /// Same shape as the headless demo: a camera URL and its credentials belong in
    /// configuration, not in shell history.
    /// </summary>
    private static EdgeForgeOptions LoadOptions()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("EDGEFORGE_")
            .Build();

        var options = configuration.Get<EdgeForgeOptions>() ?? new EdgeForgeOptions();

        if (options.Camera.Capacity < 1)
        {
            throw new InvalidOperationException(
                $"Camera:Capacity must be at least 1, got {options.Camera.Capacity}.");
        }

        return options;
    }
}
