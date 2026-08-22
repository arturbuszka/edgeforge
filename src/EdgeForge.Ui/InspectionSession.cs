using EdgeForge.Core;
using EdgeForge.Runtime;
using EdgeForge.Vision;

namespace EdgeForge.Ui;

/// <summary>
/// Runs capture and inference on a worker thread and publishes the newest result
/// to whoever is listening.
/// </summary>
/// <remarks>
/// Nothing here touches Avalonia. The session hands out snapshots on its own
/// thread; marshalling onto the UI thread is the view-model's job, which keeps
/// the loop testable and keeps a slow repaint from stalling inference.
///
/// Delivery is latest-wins. Rendering every frame would queue work on the
/// dispatcher faster than it drains on a fast source, and the operator only ever
/// wants to see the current state of the bowls anyway.
/// </remarks>
public sealed class InspectionSession : IDisposable
{
    private readonly InspectionPipeline _pipeline;
    private readonly FrameWriter _writer;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private FrameSnapshot? _pending;
    private Frame? _latestFrame;
    private IReadOnlyList<Detection> _latestDetections = [];
    private readonly Lock _snapshotGate = new();

    public InspectionSession(InspectionPipeline pipeline, FrameWriter writer)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(writer);

        _pipeline = pipeline;
        _writer = writer;
    }

    /// <summary>Raised on the capture thread whenever a newer snapshot exists.</summary>
    public event Action? SnapshotAvailable;

    /// <summary>Raised on the capture thread when the loop ends, with a reason or null.</summary>
    public event Action<string?>? Stopped;

    /// <summary>Raised on the capture thread for anything worth showing in the log.</summary>
    public event Action<string>? Notified;

    public bool IsRunning => _loop is { IsCompleted: false };

    public bool AutoSaveEnabled
    {
        get => _writer.AutoSaveEnabled;
        set => _writer.AutoSaveEnabled = value;
    }

    /// <summary>
    /// Takes the newest snapshot, or null when nothing new has arrived since the
    /// last call. Called from the UI thread.
    /// </summary>
    public FrameSnapshot? TakeSnapshot() => Interlocked.Exchange(ref _pending, null);

    /// <summary>
    /// Starts the loop. Validates the source synchronously so a typo comes back as
    /// an exception the caller can show, rather than a background fault.
    /// </summary>
    /// <exception cref="ArgumentException">The source string is not a valid spec.</exception>
    /// <exception cref="InvalidOperationException">A session is already running.</exception>
    public void Start(string source, int capacity)
    {
        if (IsRunning) throw new InvalidOperationException("Session is already running.");

        var spec = FrameSourceSpec.Parse(source);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _loop = Task.Run(() => RunAsync(spec, capacity, token), CancellationToken.None);
    }

    /// <summary>Stops the loop and waits for the capture thread to unwind.</summary>
    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;

        if (cts is null || loop is null) return;

        await cts.CancelAsync();

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        cts.Dispose();
        _cts = null;
        _loop = null;

        DiscardPending();
    }

    /// <summary>
    /// Writes the frame currently on screen to disk. Returns the path, or null if
    /// there is no frame yet or the write failed.
    /// </summary>
    /// <remarks>
    /// Runs on the UI thread against <see cref="_latestFrame"/>, which the capture
    /// loop keeps alive under <see cref="_snapshotGate"/> for exactly this reason —
    /// without the lock the Mat could be disposed mid-encode.
    /// </remarks>
    public string? SaveSnapshot()
    {
        lock (_snapshotGate)
        {
            var frame = _latestFrame;
            if (frame is null) return null;

            // Encoding happens inside the gate: the capture loop cannot reach the
            // dispose in its finally until we are done reading the Mat.
            return _writer.SaveManual(frame, _latestDetections);
        }
    }

    private async Task RunAsync(FrameSourceSpec spec, int capacity, CancellationToken token)
    {
        string? failure = null;
        var frameSource = new OpenCvFrameSource(spec, capacity);

        Notified?.Invoke($"Source: {spec.Kind} '{spec.Target}' via {spec.Api}");

        try
        {
            await foreach (var frame in frameSource.ReadFramesAsync(token))
            {
                try
                {
                    var (result, metrics) = await _pipeline.ProcessAsync(frame, token);

                    Publish(frame, result, metrics, frameSource.DroppedFrames);

                    var saved = _writer.TryAutoSave(frame, result.Detections, DateTimeOffset.UtcNow);
                    if (saved is not null)
                    {
                        Notified?.Invoke($"Saved {Path.GetFileName(saved)}");
                    }
                }
                finally
                {
                    // The consumer owns the frame after read. Both the clear and the
                    // dispose happen under the gate, because SaveSnapshot encodes the
                    // Mat while holding it — freeing outside would race that read.
                    lock (_snapshotGate)
                    {
                        _latestFrame = null;
                        _latestDetections = [];
                        frame.Image?.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() was called.
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            failure = ex.Message;
        }
        finally
        {
            DiscardPending();
            Stopped?.Invoke(failure);
        }
    }

    private void Publish(
        Frame frame,
        InspectionResult result,
        InspectionMetrics metrics,
        long dropped)
    {
        var size = BitmapBridge.RequiredBufferSize(frame);
        var buffer = size > 0 ? new byte[size] : [];
        var hasPixels = size > 0 && BitmapBridge.TryCopyBgra(frame, buffer, out _, out _);

        var snapshot = new FrameSnapshot(
            frame.Id,
            frame.Width,
            frame.Height,
            buffer,
            hasPixels,
            result.Detections,
            result.Status,
            result.Reason,
            metrics,
            dropped);

        // Latest wins: whatever the UI has not picked up yet is simply replaced.
        Interlocked.Exchange(ref _pending, snapshot);

        lock (_snapshotGate)
        {
            _latestFrame = frame;
            _latestDetections = result.Detections;
        }

        SnapshotAvailable?.Invoke();
    }

    private void DiscardPending() => Interlocked.Exchange(ref _pending, null);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
