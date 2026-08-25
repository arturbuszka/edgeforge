namespace EdgeForge.Decision;

/// <summary>
/// One completed visit to the bowls, as the decision layer understands it:
/// a stretch of time during which the target class was present often enough,
/// and long enough, to count as eating.
/// </summary>
/// <remarks>
/// A meal is a bowl-level event, not a per-animal one. Four cats are not yet
/// distinguishable from one another - that needs tracking - so two cats eating
/// side by side is one meal with <see cref="PeakCount"/> 2, not two meals. The
/// name says "meal at the bowls" everywhere in the UI for exactly that reason:
/// promising per-cat attribution we cannot deliver would make the counter a lie
/// rather than a coarse truth.
/// </remarks>
/// <param name="Started">
/// When presence was first observed - NOT when the start threshold was crossed.
/// Backdating matters: the animal was already eating during those first seconds,
/// and a duration that began at the threshold would understate every meal by it.
/// </param>
/// <param name="Ended">
/// Last moment the animal was seen, again NOT the moment the absence threshold
/// expired. The gap that ends a meal is evidence about the past, not part of it.
/// </param>
/// <param name="PeakCount">Most animals seen at once during the meal.</param>
/// <param name="ObservedFrames">Frames in which at least one animal was seen.</param>
public sealed record MealEvent(
    DateTimeOffset Started,
    DateTimeOffset Ended,
    int PeakCount,
    long ObservedFrames)
{
    /// <summary>
    /// Wall-clock span from first to last sighting, pauses included. A cat that
    /// eats, wanders off for twenty seconds and comes back had one meal, and its
    /// duration covers the pause - that is what "how long was it at the bowls"
    /// means to a human looking at the log.
    /// </summary>
    public TimeSpan Duration => Ended - Started;
}
