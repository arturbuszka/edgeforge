using EdgeForge.Core;

namespace EdgeForge.Device;

public sealed class ConsoleInspectionAction : IInspectionAction
{
    public Task ExecuteAsync(
        InspectionResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
