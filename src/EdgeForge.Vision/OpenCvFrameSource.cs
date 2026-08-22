using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EdgeForge.Core;
using OpenCvSharp;

namespace EdgeForge.Vision;

/// <summary>
/// Captures frames on a dedicated thread and hands them to the consumer through a
/// bounded channel, so a slow inspection loop cannot back up the camera pipeline.
/// </summary>
/// <remarks>
/// FRAME OWNERSHIP — the whole design rests on this:
/// the producer owns a Mat until TryWrite succeeds, the channel owns it between
/// write and read, and the consumer owns it after read and MUST dispose it.
///
/// Consequences, each a native memory leak if violated:
/// 1. A fresh Mat per Read() — reusing one would let the consumer read pixels the
///    producer is concurrently overwriting.
/// 2. BoundedChannelFullMode.DropOldest is unusable here: it discards the evicted
///    item silently with no eviction callback, leaking at camera framerate. We use
///    FullMode.Wait plus manual eviction so every dropped frame is disposed.
/// 3. Whatever is still queued at shutdown is drained and disposed.
/// </remarks>
public sealed class OpenCvFrameSource : IFrameSource
{
    private const int FailureBackoffMs = 20;
    private const int MaxConsecutiveFailures = 100; // ~2s of empty reads => treat as disconnected

    private readonly FrameSourceSpec _spec;
    private readonly int _capacity;
    private long _droppedFrames;

    public OpenCvFrameSource(FrameSourceSpec spec, int capacity = 1)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));

        _spec = spec;
        _capacity = capacity;
    }

    /// <summary>Frames captured but evicted before the consumer could read them.</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public async IAsyncEnumerable<Frame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<Frame>(
            new BoundedChannelOptions(_capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

        // VideoCapture.Read blocks; a pooled thread parked here indefinitely would
        // starve the thread pool, hence LongRunning (a dedicated thread).
        var producer = Task.Factory.StartNew(
            () => Produce(channel.Writer, channel.Reader, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }
        finally
        {
            // Covers cancellation AND a consumer that breaks out of the await foreach.
            while (channel.Reader.TryRead(out var leftover))
            {
                leftover.Image?.Dispose();
            }

            // Observe the producer so its faults are not swallowed.
            await producer.ConfigureAwait(false);
        }
    }

    private void Produce(
        ChannelWriter<Frame> writer,
        ChannelReader<Frame> reader,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        VideoCapture? capture = null;

        try
        {
            if (_spec.Kind == FrameSourceKind.Rtsp)
            {
                // Reolink over UDP drops packets and yields torn/green frames.
                // NOTE: this environment variable is process-global.
                Environment.SetEnvironmentVariable(
                    "OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp");
            }

            capture = _spec.Kind == FrameSourceKind.UsbDevice
                ? new VideoCapture(_spec.DeviceIndex, _spec.Api)
                : new VideoCapture(_spec.Target, _spec.Api);

            if (!capture.IsOpened())
            {
                throw new InvalidOperationException(
                    $"Could not open frame source '{_spec.Target}' ({_spec.Kind}, {_spec.Api}).");
            }

            if (_spec.IsLive)
            {
                // Best effort — many backends ignore it, which is exactly why the
                // explicit eviction below is still required.
                capture.Set(VideoCaptureProperties.BufferSize, 1);
            }

            var sequence = 0L;
            var consecutiveFailures = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var mat = new Mat();

                if (!capture.Read(mat) || mat.Empty())
                {
                    mat.Dispose();

                    // A file simply ended: that is a clean stop, not an error.
                    if (_spec.Kind == FrameSourceKind.File) break;

                    if (++consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        throw new InvalidOperationException(
                            $"Frame source '{_spec.Target}' stopped producing frames " +
                            $"({MaxConsecutiveFailures} consecutive empty reads) — assuming disconnect.");
                    }

                    // Back off rather than spinning hot on a dead camera.
                    Thread.Sleep(FailureBackoffMs);
                    continue;
                }

                consecutiveFailures = 0;

                var frame = new Frame(
                    $"CAM-{sequence++:000000}",
                    DateTimeOffset.UtcNow,
                    mat.Width,
                    mat.Height,
                    new MatFrameImage(mat));

                Write(writer, reader, frame, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            writer.TryComplete(failure);
            capture?.Release();
            capture?.Dispose();
        }
    }

    /// <summary>
    /// Live sources are latest-frame-wins: when the channel is full, evict and
    /// dispose the stale frame to make room (disposing is what DropOldest would
    /// not do for us). Non-live sources must NOT drop — a file replay has to stay
    /// deterministic — so the producer blocks until the consumer catches up.
    /// </summary>
    private void Write(
        ChannelWriter<Frame> writer,
        ChannelReader<Frame> reader,
        Frame frame,
        CancellationToken cancellationToken)
    {
        if (!_spec.IsLive)
        {
            try
            {
                // Applies backpressure to the decoder instead of losing frames.
                writer.WriteAsync(frame, cancellationToken).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                frame.Image?.Dispose();
            }

            return;
        }

        while (!writer.TryWrite(frame))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                frame.Image?.Dispose();
                return;
            }

            if (reader.TryRead(out var stale))
            {
                stale.Image?.Dispose();
                Interlocked.Increment(ref _droppedFrames);
            }
            else
            {
                // The consumer drained it between our two calls; retry.
                Thread.Yield();
            }
        }
    }
}
