using EdgeForge.Core;
using Microsoft.Extensions.Configuration;

namespace EdgeForge.Runtime;

/// <summary>
/// Loads <see cref="EdgeForgeOptions"/> the same way for every host.
/// </summary>
/// <remarks>
/// Layers, later ones winning:
/// <list type="number">
/// <item><c>appsettings.json</c> beside the binary - the host's defaults.</item>
/// <item><c>profiles/{name}.json</c> when a profile is selected - what this
/// installation inspects: scenario, detector, calibration, limits.</item>
/// <item><c>appsettings.Local.json</c> - gitignored machine overrides and camera
/// credentials.</item>
/// <item><c>EDGEFORGE_*</c> environment variables.</item>
/// </list>
/// A profile is how the system is pointed at a different product without an
/// edit to code or to the tracked defaults: <c>EDGEFORGE_PROFILE=eggs</c>.
/// <para>
/// This was three copies, one per host, and each was about to grow profile
/// handling; one copy it is.
/// </para>
/// </remarks>
public static class EdgeForgeConfiguration
{
    /// <summary>Environment variable naming the profile to load.</summary>
    public const string ProfileVariable = "EDGEFORGE_PROFILE";

    /// <summary>Folder beside the binary holding the profiles.</summary>
    public const string ProfileFolder = "profiles";

    /// <summary>
    /// Loads and validates the options every host needs. Host-specific sections
    /// (Api, Storage) are left to the host.
    /// </summary>
    /// <param name="profile">
    /// Profile name, or null to read <see cref="ProfileVariable"/>. Empty means none.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A value is not usable, or the named profile does not exist.
    /// </exception>
    public static EdgeForgeOptions Load(string baseDirectory, string? profile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        profile ??= Environment.GetEnvironmentVariable(ProfileVariable);

        var builder = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        if (!string.IsNullOrWhiteSpace(profile))
        {
            builder.AddJsonFile(ProfilePath(baseDirectory, profile), optional: false, reloadOnChange: false);
        }

        var configuration = builder
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("EDGEFORGE_")
            .Build();

        var options = configuration.Get<EdgeForgeOptions>() ?? new EdgeForgeOptions();

        if (options.Camera.Capacity < 1)
        {
            throw new InvalidOperationException(
                $"Camera:Capacity must be at least 1, got {options.Camera.Capacity}.");
        }

        options.ValidateScenario();

        return options;
    }

    /// <summary>The profiles shipped beside the binary, by name.</summary>
    public static IReadOnlyList<string> AvailableProfiles(string baseDirectory)
    {
        var folder = Path.Combine(baseDirectory, ProfileFolder);
        if (!Directory.Exists(folder)) return [];

        return Directory.GetFiles(folder, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <exception cref="InvalidOperationException">The name is malformed or the file is missing.</exception>
    private static string ProfilePath(string baseDirectory, string profile)
    {
        // A name, not a path: "../appsettings.Local" must not be a way to load an
        // arbitrary file as configuration.
        if (!profile.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new InvalidOperationException(
                $"Profile name '{profile}' may contain only letters, digits, '-' and '_'.");
        }

        var relative = Path.Combine(ProfileFolder, profile + ".json");

        if (!File.Exists(Path.Combine(baseDirectory, relative)))
        {
            var available = AvailableProfiles(baseDirectory);
            throw new InvalidOperationException(
                $"Profile '{profile}' not found. Available: " +
                (available.Count == 0 ? "none" : string.Join(", ", available)) + ".");
        }

        return relative;
    }
}
