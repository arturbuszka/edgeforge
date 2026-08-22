using Xunit;
using EdgeForge.Core;
using EdgeForge.Vision;

namespace EdgeForge.Core.Tests;

public class LabelInspectorTests
{
    private readonly LabelInspector _inspector = new();

    [Fact]
    public void CorrectLabel_IsOk()
    {
        var result = _inspector.Inspect(
            CreateFrame(),
            new[]
            {
                new Detection("correct_label", 0.99, new BoundingBox(0, 0, 1, 1))
            },
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(InspectionStatus.Ok, result.Status);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void WrongLabel_IsNok()
    {
        var result = _inspector.Inspect(
            CreateFrame(),
            new[]
            {
                new Detection("wrong_label", 0.95, new BoundingBox(0, 0, 1, 1))
            },
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(InspectionStatus.Nok, result.Status);
        Assert.Equal("WrongLabel", result.Reason);
    }

    [Fact]
    public void NoLabel_IsUnknown()
    {
        var result = _inspector.Inspect(
            CreateFrame(),
            Array.Empty<Detection>(),
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(InspectionStatus.Unknown, result.Status);
        Assert.Equal("NoLabelDetected", result.Reason);
    }

    private static Frame CreateFrame() =>
        new("TEST-001", DateTimeOffset.UtcNow, 1920, 1080);
}
