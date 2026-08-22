namespace EdgeForge.Core;

public readonly record struct BoundingBox(double X, double Y, double Width, double Height);

/// <summary>
/// Opaque handle to frame pixels. Implementations live in adapter layers;
/// the core domain never sees the underlying image SDK type.
/// </summary>
public interface IFrameImage : IDisposable
{
    int Width { get; }
    int Height { get; }
}

public sealed record Frame(
    string Id,
    DateTimeOffset Timestamp,
    int Width,
    int Height,
    IFrameImage? Image = null);

public sealed record Detection(
    string ClassName,
    double Confidence,
    BoundingBox BoundingBox);

public enum InspectionStatus
{
    Ok,
    Nok,
    Unknown
}

public sealed record InspectionResult(
    string FrameId,
    InspectionStatus Status,
    string? Reason,
    IReadOnlyList<Detection> Detections,
    TimeSpan ProcessingTime);

public interface IVisionDetector
{
    Task<IReadOnlyList<Detection>> DetectAsync(
        Frame frame,
        CancellationToken cancellationToken = default);
}

public interface IInspector
{
    InspectionResult Inspect(
        Frame frame,
        IReadOnlyList<Detection> detections,
        TimeSpan processingTime);
}

public interface IInspectionAction
{
    Task ExecuteAsync(
        InspectionResult result,
        CancellationToken cancellationToken = default);
}
