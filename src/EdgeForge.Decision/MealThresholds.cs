namespace EdgeForge.Decision;

/// <summary>
/// The three numbers that turn a stream of detections into meal events.
/// </summary>
/// <remarks>
/// Chosen from how a cat actually eats: a visit is tens of seconds long and
/// contains pauses. They are deliberately configuration rather than constants,
/// because the honest way to set them is to run the detector for a few days,
/// read <c>meals.jsonl</c>, and adjust - not to guess twice.
/// </remarks>
public sealed record MealThresholds
{
    /// <summary>
    /// How long the animal must be present before a meal is considered started.
    /// </summary>
    /// <remarks>
    /// Rejects an animal walking past the bowls. Five seconds is ~115 frames of
    /// evidence at 23 fps, and short enough that a 30-second meal is not mostly
    /// spent waiting for the threshold. Raising it trades missed short visits for
    /// fewer false starts.
    /// </remarks>
    public TimeSpan MinPresenceToStart { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the animal must be gone before the meal is considered over.
    /// </summary>
    /// <remarks>
    /// The number most worth tuning against real data. Cats leave the bowl and
    /// come back; a gap under a minute is one meal with a pause, not two meals.
    /// Too low and one meal is logged as five; too high and two genuine visits an
    /// hour apart merge into one.
    /// </remarks>
    public TimeSpan MaxAbsenceToEnd { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Shortest meal worth logging, measured first sighting to last.
    /// </summary>
    /// <remarks>
    /// A second filter behind <see cref="MinPresenceToStart"/>: an animal that
    /// lingers just past the start threshold and then leaves did not eat. Applied
    /// when the meal closes, because only then is the full duration known.
    /// </remarks>
    public TimeSpan MinMealDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <exception cref="ArgumentOutOfRangeException">Any threshold is negative or zero.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MinPresenceToStart, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxAbsenceToEnd, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinMealDuration, TimeSpan.Zero);
    }
}
