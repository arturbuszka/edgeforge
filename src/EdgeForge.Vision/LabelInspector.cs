using EdgeForge.Core;

namespace EdgeForge.Vision;

public sealed class LabelInspector : IInspector
{
    public InspectionResult Inspect(
        Frame frame,
        IReadOnlyList<Detection> detections,
        TimeSpan processingTime)
    {
        if (detections.Any(d => d.ClassName == "wrong_label"))
        {
            return new InspectionResult(
                frame.Id, InspectionStatus.Nok, "WrongLabel",
                detections, processingTime);
        }

        if (detections.Any(d => d.ClassName == "correct_label"))
        {
            return new InspectionResult(
                frame.Id, InspectionStatus.Ok, null,
                detections, processingTime);
        }

        return new InspectionResult(
            frame.Id, InspectionStatus.Unknown, "NoLabelDetected",
            detections, processingTime);
    }
}
