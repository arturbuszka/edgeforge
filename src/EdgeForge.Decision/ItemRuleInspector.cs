using EdgeForge.Core;

namespace EdgeForge.Decision;

/// <summary>
/// The per-frame reading for the line: how many items are in view, and whether
/// any of them looks out of spec in this frame.
/// </summary>
/// <remarks>
/// This is NOT the verdict. An item is judged once, at the decision line, from
/// everything seen of it (<see cref="LineCounter"/>). This reading is the raw
/// frame-by-frame truth, kept undebounced for the same reason the bowls keep
/// <c>PresenceInspector</c> raw: it is what shows how stable the measurement is.
/// <para>
/// An empty belt is OK, not UNKNOWN. Gaps between items are the normal state of a
/// line, and the question "was this item checked" is answered per item, where
/// UNKNOWN does mean something.
/// </para>
/// </remarks>
public sealed class ItemRuleInspector : IInspector
{
    private readonly ItemRule _rule;

    public ItemRuleInspector(ItemRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rule = rule;
    }

    public InspectionResult Inspect(Frame frame, IReadOnlyList<Detection> detections, TimeSpan processingTime)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(detections);

        if (detections.Count == 0)
        {
            return new InspectionResult(frame.Id, InspectionStatus.Ok, null, detections, processingTime);
        }

        var outOfSpec = 0;
        string? firstReason = null;

        foreach (var detection in detections)
        {
            var judgement = _rule.Judge(detection);
            if (judgement.Status != InspectionStatus.Nok) continue;

            outOfSpec++;
            firstReason ??= judgement.Reason;
        }

        return outOfSpec > 0
            ? new InspectionResult(
                frame.Id, InspectionStatus.Nok,
                $"{outOfSpec}/{detections.Count} out of spec: {firstReason}",
                detections, processingTime)
            : new InspectionResult(
                frame.Id, InspectionStatus.Ok,
                $"{detections.Count} in view",
                detections, processingTime);
    }
}
