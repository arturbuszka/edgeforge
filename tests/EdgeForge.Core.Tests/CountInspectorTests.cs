using EdgeForge.Vision;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The count rule is the decision layer, so these are process-behaviour tests:
/// the right population is Ok, any other population is Nok and says so in terms
/// a human can read off the log.
/// </summary>
public class CountInspectorTests
{
    private static Frame CreateFrame() =>
        new("TEST-001", DateTimeOffset.UtcNow, 1920, 1080);

    private static Detection[] Detections(params string[] classNames) =>
        classNames
            .Select(n => new Detection(n, 0.9, new BoundingBox(0.1, 0.1, 0.2, 0.2)))
            .ToArray();

    [Fact]
    public void ExpectedCountPresent_IsOk()
    {
        var inspector = new CountInspector(expectedCount: 2, className: "cat");

        var result = inspector.Inspect(
            CreateFrame(),
            Detections("cat", "cat"),
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(InspectionStatus.Ok, result.Status);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void FewerThanExpected_IsNokAndReportsBothCounts()
    {
        var inspector = new CountInspector(expectedCount: 2, className: "cat");

        var result = inspector.Inspect(
            CreateFrame(),
            Detections("cat"),
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(InspectionStatus.Nok, result.Status);
        Assert.Equal("Count 1/2", result.Reason);
    }

    [Fact]
    public void MoreThanExpected_IsNokAndReportsBothCounts()
    {
        var inspector = new CountInspector(expectedCount: 2, className: "cat");

        var result = inspector.Inspect(
            CreateFrame(),
            Detections("cat", "cat", "cat"),
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(InspectionStatus.Nok, result.Status);
        Assert.Equal("Count 3/2", result.Reason);
    }

    [Fact]
    public void NothingDetected_IsNok()
    {
        var inspector = new CountInspector(expectedCount: 2, className: "cat");

        var result = inspector.Inspect(
            CreateFrame(),
            Array.Empty<Detection>(),
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(InspectionStatus.Nok, result.Status);
        Assert.Equal("Count 0/2", result.Reason);
    }

    [Fact]
    public void OtherClassesAreNotCounted()
    {
        var inspector = new CountInspector(expectedCount: 2, className: "cat");

        var result = inspector.Inspect(
            CreateFrame(),
            Detections("cat", "dog", "person", "cat"),
            TimeSpan.FromMilliseconds(5));

        // Four detections, but only two of the class we are counting.
        Assert.Equal(InspectionStatus.Ok, result.Status);
        Assert.Equal(4, result.Detections.Count);
    }

    [Fact]
    public void ClassNameIsAParameter_NotACatConstant()
    {
        // Retargeting the coop must be configuration, not a code change.
        var inspector = new CountInspector(expectedCount: 9, className: "hen");

        var result = inspector.Inspect(
            CreateFrame(),
            Detections(Enumerable.Repeat("hen", 9).ToArray()),
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(InspectionStatus.Ok, result.Status);
    }
}
