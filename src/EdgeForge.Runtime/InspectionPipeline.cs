using System.Diagnostics;
using EdgeForge.Core;

namespace EdgeForge.Runtime;

public sealed class InspectionPipeline
{
    private readonly IVisionDetector _detector;
    private readonly IInspector _inspector;
    private readonly IInspectionAction _action;

    public InspectionPipeline(
        IVisionDetector detector,
        IInspector inspector,
        IInspectionAction action)
    {
        _detector = detector;
        _inspector = inspector;
        _action = action;
    }

    /// <summary>
    /// Runs detection, decision and action for one frame.
    /// Does NOT dispose the frame: the caller owns it, so there is exactly one
    /// owner at every point in the flow.
    /// </summary>
    public async Task<(InspectionResult Result, InspectionMetrics Metrics)> ProcessAsync(
        Frame frame,
        CancellationToken cancellationToken = default)
    {
        // How long the frame waited between capture and inspection.
        var captureLatency = DateTimeOffset.UtcNow - frame.Timestamp;
        if (captureLatency < TimeSpan.Zero) captureLatency = TimeSpan.Zero;

        var total = Stopwatch.StartNew();

        var stopwatch = Stopwatch.StartNew();
        var detections = await _detector.DetectAsync(
            frame, cancellationToken);
        stopwatch.Stop();
        var inferenceLatency = stopwatch.Elapsed;

        stopwatch.Restart();
        var result = _inspector.Inspect(
            frame, detections, inferenceLatency) with
        {
            Timestamp = frame.SceneTime ?? frame.Timestamp
        };
        stopwatch.Stop();
        var decisionLatency = stopwatch.Elapsed;

        stopwatch.Restart();
        await _action.ExecuteAsync(result, cancellationToken);
        stopwatch.Stop();
        var actionLatency = stopwatch.Elapsed;

        total.Stop();

        var metrics = new InspectionMetrics(
            captureLatency,
            inferenceLatency,
            decisionLatency,
            actionLatency,
            total.Elapsed);

        return (result, metrics);
    }
}
