using EdgeForge.Core;

namespace EdgeForge.Ui;

/// <summary>
/// Pipeline action that reports into the viewer's event log instead of the console.
/// </summary>
/// <remarks>
/// Throttled the same way the headless demo throttles its console writes: at 25fps
/// an unfiltered log is unreadable and the allocation churn shows up in the very
/// latency figures the viewer exists to measure. Rejects always get through.
/// </remarks>
public sealed class UiInspectionAction : IInspectionAction
{
    private const int HeartbeatFrames = 60;

    private readonly Action<string> _log;
    private long _seen;

    public UiInspectionAction(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public Task ExecuteAsync(InspectionResult result, CancellationToken cancellationToken = default)
    {
        _seen++;

        var interesting = result.Status != InspectionStatus.Ok;

        if (interesting || _seen % HeartbeatFrames == 1)
        {
            var reason = result.Reason is null ? string.Empty : $" — {result.Reason}";
            _log($"{result.Status}: {result.Detections.Count} det{reason}");
        }

        return Task.CompletedTask;
    }
}
