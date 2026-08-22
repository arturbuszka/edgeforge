namespace EdgeForge.Runtime;

/// <summary>
/// Latency breakdown for one trip through the pipeline. Measuring the complete
/// loop matters more than model inference alone.
/// </summary>
/// <param name="CaptureLatency">
/// Age of the frame when inspection began (now - frame timestamp). This is the
/// capture-to-inspection queue delay, and it is the number that reveals a
/// backed-up channel — not the cost of grabbing the frame itself.
/// </param>
public sealed record InspectionMetrics(
    TimeSpan CaptureLatency,
    TimeSpan InferenceLatency,
    TimeSpan DecisionLatency,
    TimeSpan ActionLatency,
    TimeSpan TotalLatency);
