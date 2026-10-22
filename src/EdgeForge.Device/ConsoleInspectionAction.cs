using EdgeForge.Core;

namespace EdgeForge.Device;

/// <summary>
/// Stands in for an actuator by writing what it would do to the console.
/// </summary>
/// <remarks>
/// Writes on a change of state, not on every frame. At 25 fps a line per frame
/// buries everything else on the console - including the item verdicts, which are
/// the events that matter - and the console write itself shows up in ActionLatency.
/// A real output (a relay, a PLC bit) is level-driven too: it is set when the state
/// changes and held, not pulsed once per frame.
/// </remarks>
public sealed class ConsoleInspectionAction : IInspectionAction
{
    private InspectionStatus? _last;

    public Task ExecuteAsync(
        InspectionResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (result.Status == _last) return Task.CompletedTask;
        _last = result.Status;

        var action = result.Status switch
        {
            InspectionStatus.Ok => "PASS",
            InspectionStatus.Nok => "REJECT",
            _ => "HOLD"
        };

        Console.WriteLine($"Action : {action}");
        return Task.CompletedTask;
    }
}
