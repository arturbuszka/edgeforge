using EdgeForge.Core;

namespace EdgeForge.Vision;

/// <summary>
/// Counts detections of one class against an expected population.
/// </summary>
/// <remarks>
/// Deliberately without debouncing. Debouncing a momentary count dip is a
/// decision-layer concern that belongs with tracking; at this stage we want the
/// raw, unsmoothed truth about detector quality, frame by frame.
/// </remarks>
public sealed class CountInspector : IInspector
{
    private readonly int _expectedCount;
    private readonly string _className;

    public CountInspector(int expectedCount = 2, string className = "cat")
    {
        _expectedCount = expectedCount;
        _className = className;
    }

    public InspectionResult Inspect(
        Frame frame,
        IReadOnlyList<Detection> detections,
        TimeSpan processingTime)
    {
        var count = detections.Count(d => d.ClassName == _className);

        return count == _expectedCount
            ? new InspectionResult(
                frame.Id, InspectionStatus.Ok, null, detections, processingTime)
            : new InspectionResult(
                frame.Id, InspectionStatus.Nok, $"Count {count}/{_expectedCount}",
                detections, processingTime);
    }
}
