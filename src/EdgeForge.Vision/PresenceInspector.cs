using EdgeForge.Core;

namespace EdgeForge.Vision;

/// <summary>
/// Reports whether the target class is present, without judging how many.
/// </summary>
/// <remarks>
/// At the bowls the normal state is an empty frame. <see cref="CountInspector"/>
/// treats any count other than the expected one as Nok, which over a day means
/// almost every frame is a fault and the event log fills with the animals' own
/// absence — the rule inverted, reporting the ordinary as the exception.
///
/// Presence is the honest reading of this scene: zero cats is fine, cats at the
/// bowls is fine, and neither is a failure. What matters for counting meals is
/// the transition between the two, which belongs to the decision layer with
/// tracking. Until then this keeps the raw detection visible without drowning it.
///
/// <see cref="CountInspector"/> stays: a coop with a known flock size is exactly
/// the case where an unexpected count IS the fault.
/// </remarks>
public sealed class PresenceInspector : IInspector
{
    private readonly string _className;

    public PresenceInspector(string className = "cat")
    {
        _className = className;
    }

    public InspectionResult Inspect(
        Frame frame,
        IReadOnlyList<Detection> detections,
        TimeSpan processingTime)
    {
        var count = detections.Count(d => d.ClassName == _className);

        // Ok either way; the count travels in the reason so the log still shows
        // what was seen, and nothing has to be inferred from a status flag.
        var reason = count == 0 ? null : $"{count} {_className}";

        return new InspectionResult(
            frame.Id, InspectionStatus.Ok, reason, detections, processingTime);
    }
}
