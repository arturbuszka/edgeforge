namespace EdgeForge.Decision;

/// <summary>
/// Today's meal tally and the current state, as one consistent reading.
/// </summary>
/// <remarks>
/// A record rather than a tuple because there are now six values and a caller
/// reading them one property at a time could otherwise see a count from before a
/// meal completed next to a duration from after it. Taken under one lock, this is
/// a single coherent answer.
/// </remarks>
public sealed record MealsSnapshot(
    int MealsToday,
    bool IsFeeding,
    TimeSpan TotalDurationToday,
    DateTimeOffset? LastMealEnded,
    DateTimeOffset? CurrentMealStarted,
    DateOnly Day);
