namespace EdgeForge.Core;

/// <summary>
/// A source of frames. Real cameras are streams rather than one-shot requests,
/// so the contract is an async sequence: it expresses end-of-stream (video EOF),
/// backpressure, and leaves ownership of any capture thread to the implementation.
/// </summary>
/// <remarks>
/// Frame ownership: the consumer owns each yielded frame and MUST dispose
/// <see cref="Frame.Image"/> once it is done with it. Frames the source drops or
/// drains internally are disposed by the source.
/// </remarks>
public interface IFrameSource
{
    IAsyncEnumerable<Frame> ReadFramesAsync(
        CancellationToken cancellationToken = default);
}
