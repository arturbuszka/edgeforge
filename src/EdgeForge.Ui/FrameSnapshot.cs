using EdgeForge.Core;
using EdgeForge.Runtime;

namespace EdgeForge.Ui;

/// <summary>
/// Everything the UI needs about one processed frame, with the pixels copied out.
/// </summary>
/// <remarks>
/// The copy is the whole point: <see cref="IFrameSource"/> makes the consumer the
/// owner of <c>Frame.Image</c>, and the session disposes it the moment the pipeline
/// returns. A snapshot holding the Mat instead would be reading freed native memory
/// by the time the dispatcher got round to painting it.
/// </remarks>
public sealed record FrameSnapshot(
    string FrameId,
    int Width,
    int Height,
    byte[] Bgra,
    bool HasPixels,
    IReadOnlyList<Detection> Detections,
    InspectionStatus Status,
    string? Reason,
    InspectionMetrics Metrics,
    long DroppedFrames);
