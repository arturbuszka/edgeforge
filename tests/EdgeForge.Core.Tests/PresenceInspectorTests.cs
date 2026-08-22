using EdgeForge.Core;
using EdgeForge.Vision;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The rule that replaced CountInspector at the bowls. The case that matters is
/// the empty frame: it is the normal state for most of the day, and calling it a
/// fault is what made the event log unusable.
/// </summary>
public class PresenceInspectorTests
{
    private static Frame AFrame() => new("CAM-000001", DateTimeOffset.UtcNow, 640, 480);

    private static Detection ACat(string className = "cat") =>
        new(className, 0.9f, new BoundingBox(0.1, 0.1, 0.2, 0.2));

    [Fact]
    public void EmptyFrameIsOk_NotAFault()
    {
        var result = new PresenceInspector().Inspect(AFrame(), [], TimeSpan.Zero);

        Assert.Equal(InspectionStatus.Ok, result.Status);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void OneCatIsOk_AndSaysSo()
    {
        var result = new PresenceInspector().Inspect(AFrame(), [ACat()], TimeSpan.Zero);

        Assert.Equal(InspectionStatus.Ok, result.Status);
        Assert.Equal("1 cat", result.Reason);
    }

    [Fact]
    public void MoreCatsThanExpectedIsStillOk()
    {
        // Four cats at two bowls is a normal evening, not an anomaly.
        var result = new PresenceInspector()
            .Inspect(AFrame(), [ACat(), ACat(), ACat(), ACat()], TimeSpan.Zero);

        Assert.Equal(InspectionStatus.Ok, result.Status);
        Assert.Equal("4 cat", result.Reason);
    }

    [Fact]
    public void OtherClassesAreNotCounted()
    {
        var result = new PresenceInspector()
            .Inspect(AFrame(), [ACat("dog"), ACat("bird")], TimeSpan.Zero);

        Assert.Null(result.Reason);
    }

    [Fact]
    public void DetectionsAreCarriedThroughUntouched()
    {
        // The overlay draws from these; presence must not filter what is seen.
        var detections = new[] { ACat(), ACat("dog") };

        var result = new PresenceInspector().Inspect(AFrame(), detections, TimeSpan.Zero);

        Assert.Equal(2, result.Detections.Count);
    }
}
