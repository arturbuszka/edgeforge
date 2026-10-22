namespace EdgeForge.Core;

/// <summary>
/// Box in image-relative coordinates: X, Y, Width and Height are fractions of the
/// frame in the range 0..1, with the origin at the top-left corner. Normalised so
/// a detection stays meaningful when the frame is resized, and so decision rules
/// can be written once against any camera resolution.
/// </summary>
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

/// <param name="Timestamp">
/// Wall-clock moment of capture. What capture latency is measured against, so it
/// is always "now" at the source, for a file as much as for a camera.
/// </param>
public sealed record Frame(
    string Id,
    DateTimeOffset Timestamp,
    int Width,
    int Height,
    IFrameImage? Image = null)
{
    /// <summary>
    /// When this frame happened in the scene, if that differs from
    /// <see cref="Timestamp"/>. Null for a live source, where the two are the same.
    /// </summary>
    /// <remarks>
    /// A video file decodes as fast as the pipeline consumes it, so its wall-clock
    /// timestamps compress or stretch the scene's own time. The decision layer
    /// measures durations and speeds; it needs the file's time, or a replay
    /// produces different events from the same footage.
    /// </remarks>
    public DateTimeOffset? SceneTime { get; init; }
}

/// <param name="Measurements">
/// Physical quantities the detector measured on this object, keyed by name
/// (see <see cref="MeasurementNames"/>). Null when the detector measures nothing,
/// which is the case for a plain object detector. A key that is absent means
/// "not measured in this frame" - an object cut by the frame edge, for instance -
/// and never "zero".
/// </param>
public sealed record Detection(
    string ClassName,
    double Confidence,
    BoundingBox BoundingBox,
    IReadOnlyDictionary<string, double>? Measurements = null);

/// <summary>
/// The measurement keys detectors in this repository emit. Strings rather than an
/// enum because rules name them in configuration, and a new detector should be
/// able to add one without a change to the core.
/// </summary>
public static class MeasurementNames
{
    /// <summary>Long axis of the fitted ellipse, in millimetres.</summary>
    public const string LengthMm = "length_mm";

    /// <summary>Short axis of the fitted ellipse, in millimetres.</summary>
    public const string WidthMm = "width_mm";

    /// <summary>Long axis in source pixels, for an uncalibrated camera.</summary>
    public const string LengthPx = "length_px";

    /// <summary>Short axis in source pixels, for an uncalibrated camera.</summary>
    public const string WidthPx = "width_px";
}

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
    TimeSpan ProcessingTime)
{
    /// <summary>
    /// Scene time of the frame this result describes: <see cref="Frame.SceneTime"/>
    /// when the source has one, otherwise the capture timestamp. Stamped by the
    /// pipeline, so inspectors need not remember to.
    /// </summary>
    /// <remarks>
    /// Decision logic downstream should use this rather than the wall clock: it is
    /// what lets a recorded file replay to exactly the events it produced live.
    /// </remarks>
    public DateTimeOffset Timestamp { get; init; }
}

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
