namespace EdgeForge.Decision;

/// <summary>
/// Today's tally: how many meals, how much time at the bowls, when the last one ended.
/// </summary>
/// <remarks>
/// Rolls over at LOCAL midnight, not UTC. "How many times did they eat today" is a
/// question about the household's day, and a counter that resets at 01:00 or 02:00
/// local would be wrong on the overlay in a way nobody would think to check.
///
/// A meal is attributed to the day it STARTED. One that runs across midnight
/// belongs to the day it began, so a single visit is never split into two.
/// </remarks>
public sealed class DailyMealLog
{
    private readonly TimeProvider _time;

    private DateOnly _day;
    private int _meals;
    private TimeSpan _totalDuration;
    private DateTimeOffset? _lastMealEnded;

    /// <param name="time">
    /// Injected so tests can cross midnight without waiting for it. Defaults to the
    /// system clock.
    /// </param>
    public DailyMealLog(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _day = Today();
    }

    public DateOnly Day
    {
        get
        {
            RollIfNeeded();
            return _day;
        }
    }

    public int Meals
    {
        get
        {
            RollIfNeeded();
            return _meals;
        }
    }

    public TimeSpan TotalDuration
    {
        get
        {
            RollIfNeeded();
            return _totalDuration;
        }
    }

    public DateTimeOffset? LastMealEnded
    {
        get
        {
            RollIfNeeded();
            return _lastMealEnded;
        }
    }

    /// <summary>
    /// Adds a completed meal. Meals from an earlier day are ignored rather than
    /// counted against today - which is what makes replaying <c>meals.jsonl</c> on
    /// startup safe: yesterday's lines cannot inflate today's number.
    /// </summary>
    /// <returns>True if the meal counted toward today.</returns>
    public bool Add(MealEvent meal)
    {
        ArgumentNullException.ThrowIfNull(meal);

        RollIfNeeded();

        if (LocalDateOf(meal.Started) != _day) return false;

        _meals++;
        _totalDuration += meal.Duration;

        // Guarded rather than assigned: replaying a file need not hand meals over
        // in order, and "last" must mean latest in time, not last seen.
        if (_lastMealEnded is null || meal.Ended > _lastMealEnded) _lastMealEnded = meal.Ended;

        return true;
    }

    private void RollIfNeeded()
    {
        var today = Today();
        if (today == _day) return;

        _day = today;
        _meals = 0;
        _totalDuration = TimeSpan.Zero;
        _lastMealEnded = null;
    }

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <summary>
    /// The local calendar date of an instant, resolved through the injected
    /// <see cref="TimeProvider"/> rather than through <see cref="DateTimeOffset.ToLocalTime"/>.
    /// The latter reads the machine time zone, which would silently disagree with
    /// the zone the day boundary is computed in - a mismatch that shows up only
    /// around midnight, which is exactly when the counter must be right.
    /// </summary>
    private DateOnly LocalDateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(instant, _time.LocalTimeZone).DateTime);
}
