using EdgeForge.Runtime;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// Profiles are how the system is pointed at a different product, so loading one
/// has to be predictable and refusing a bad one has to be loud.
/// </summary>
public sealed class ConfigurationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "edgeforge-config-" + Guid.NewGuid().ToString("N"));

    public ConfigurationTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, EdgeForgeConfiguration.ProfileFolder));

        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """
            { "Camera": { "Source": "0" }, "Detector": { "ClassName": "cat" } }
            """);

        File.WriteAllText(Path.Combine(_directory, "profiles", "eggs.json"), """
            {
              "Scenario": "line",
              "Detector": { "Kind": "blob" },
              "Blob": { "MmPerPixel": 0.25 },
              "Line": {
                "Direction": "RightToLeft",
                "Limits": [ { "Measurement": "width_mm", "Min": 42, "Reason": "Undersize" } ]
              }
            }
            """);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void WithoutAProfile_TheDefaultsAreTheBowls()
    {
        var options = EdgeForgeConfiguration.Load(_directory, profile: "");

        Assert.Equal(ScenarioNames.Bowls, options.Scenario);
        Assert.Equal("0", options.Camera.Source);
    }

    [Fact]
    public void AProfile_LayersOverTheDefaults()
    {
        var options = EdgeForgeConfiguration.Load(_directory, profile: "eggs");

        Assert.Equal(ScenarioNames.Line, options.Scenario);
        Assert.Equal(DetectorKinds.Blob, options.Detector.Kind);
        Assert.Equal(0.25, options.Blob.MmPerPixel);
        Assert.Equal(LineDirection.RightToLeft, options.Line.Direction);

        var limit = Assert.Single(options.Line.Limits);
        Assert.Equal(42, limit.Min);
        Assert.Null(limit.Max);

        // Untouched by the profile, so still the default file's value.
        Assert.Equal("0", options.Camera.Source);
    }

    [Fact]
    public void AMissingProfile_NamesTheOnesThatExist()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => EdgeForgeConfiguration.Load(_directory, profile: "bottles"));

        Assert.Contains("eggs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileNameIsNotAPath()
    {
        Assert.Throws<InvalidOperationException>(
            () => EdgeForgeConfiguration.Load(_directory, profile: "../appsettings"));
    }

    [Fact]
    public void AnUnknownScenario_IsRefused()
    {
        var options = new EdgeForgeOptions { Scenario = "coop" };

        var error = Assert.Throws<InvalidOperationException>(options.ValidateScenario);
        Assert.Contains("Scenario", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownDetectorKind_IsRefused()
    {
        var options = new EdgeForgeOptions { Detector = new DetectorOptions { Kind = "sam" } };

        Assert.Throws<InvalidOperationException>(options.ValidateScenario);
    }

    [Fact]
    public void AnUnusedSection_IsNotValidated()
    {
        // A half-edited Line block must not stop the bowls from running.
        var options = new EdgeForgeOptions { Line = new LineOptions { DecisionLine = 7 } };

        options.ValidateScenario();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void ADecisionLineOnTheFrameEdge_IsRefused(double line)
    {
        var options = new EdgeForgeOptions
        {
            Scenario = ScenarioNames.Line,
            Line = new LineOptions { DecisionLine = line }
        };

        Assert.Throws<InvalidOperationException>(options.ValidateScenario);
    }

    [Fact]
    public void ALimitWithNoBounds_IsRefused()
    {
        var options = new LineOptions { Limits = [new LimitOptions { Measurement = "width_mm" }] };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void AnEvenBlurKernel_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(new BlobOptions { BlurKernel = 4 }.Validate);
    }
}
