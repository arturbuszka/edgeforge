using EdgeForge.Core;

namespace EdgeForge.Runtime;

/// <summary>
/// One frame encoded for transport, plus the metadata a consumer needs about it.
/// </summary>
/// <remarks>
/// Carries bytes rather than a Mat on purpose. The encoded array is immutable and
/// owned by nobody in particular, so it can cross a thread boundary and sit in an
/// async HTTP write for as long as a slow client needs - which a Mat cannot do,
/// because the capture loop frees it as soon as the observer returns.
///
/// It also happens to be what a future HLS encoder wants to hand to ffmpeg on
/// stdin, so no second encode is needed to add one.
/// </remarks>
public sealed record EncodedFrame(
    long Sequence,
    byte[] Jpeg,
    int Width,
    int Height,
    IReadOnlyList<Detection> Detections,
    DateTimeOffset CapturedUtc);
