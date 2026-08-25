using EdgeForge.Decision;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The counter behind the overlay. Midnight is the interesting case: the number
/// has to reset with the household day, and it has to survive meals being replayed
/// out of a file on startup.
/// </summary>
public class DailyMealLogTests
{
    /// <summary>
    /// A clock the test moves by hand, so crossing midnight costs no wall time.
    /// Local time is pinned to UTC here to keep the assertions about the rollover
    /// rather than about the machine the tests run on.
    /// </summary>
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly DateTimeOffset Noon =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static MealEvent AMeal(DateTimeOffset started, double minutes = 1) =>
        new(started, started + TimeSpan.FromMinutes(minutes), PeakCount: 1, ObservedFrames: 100);

    [Fact]
    public void CountsMealsAddedToday()
    {
        var log = new DailyMealLog(new FakeClock(Noon));

        Assert.True(log.Add(AMeal(Noon)));
        Assert.True(log.Add(AMeal(Noon.AddHours(2))));

        Assert.Equal(2, log.Meals);
    }

    [Fact]
    public void SumsTimeSpentAtTheBowls()
    {
        var log = new DailyMealLog(new FakeClock(Noon));

        log.Add(AMeal(Noon, minutes: 1.5));
        log.Add(AMeal(Noon.AddHours(1), minutes: 2.5));

        Assert.Equal(TimeSpan.FromMinutes(4), log.TotalDuration);
    }

    [Fact]
    public void ResetsAtLocalMidnight()
    {
        var clock = new FakeClock(Noon);
        var log = new DailyMealLog(clock);

        log.Add(AMeal(Noon));
        log.Add(AMeal(Noon.AddHours(1)));
        Assert.Equal(2, log.Meals);

        // 13:00 -> next day 01:00.
        clock.Advance(TimeSpan.FromHours(12));

        Assert.Equal(0, log.Meals);
        Assert.Equal(TimeSpan.Zero, log.TotalDuration);
        Assert.Null(log.LastMealEnded);
        Assert.Equal(new DateOnly(2026, 8, 23), log.Day);
    }

    [Fact]
    public void MealsFromAnotherDayAreNotCounted()
    {
        // This is what makes replaying meals.jsonl on startup safe: yesterday's
        // lines are read, recognised as not-today, and ignored.
        var log = new DailyMealLog(new FakeClock(Noon));

        Assert.False(log.Add(AMeal(Noon.AddDays(-1))));
        Assert.Equal(0, log.Meals);
    }

    [Fact]
    public void ReplayingTodaysMealsRebuildsTheCount()
    {
        // The restart case: the process died mid-day, and the counter is rebuilt
        // from the file rather than starting the day over at zero.
        var log = new DailyMealLog(new FakeClock(Noon));

        var replayed = new[]
        {
            AMeal(Noon.AddHours(-4)),
            AMeal(Noon.AddHours(-2)),
            AMeal(Noon.AddHours(-1))
        };

        foreach (var meal in replayed) log.Add(meal);

        Assert.Equal(3, log.Meals);
        Assert.Equal(Noon.AddHours(-1).AddMinutes(1), log.LastMealEnded);
    }

    [Fact]
    public void LastMealEndedIsTheLatest_NotTheLastAdded()
    {
        // A file need not hand meals over in order; "last" means latest in time.
        var log = new DailyMealLog(new FakeClock(Noon));

        log.Add(AMeal(Noon.AddHours(3)));
        log.Add(AMeal(Noon.AddHours(1)));

        Assert.Equal(Noon.AddHours(3).AddMinutes(1), log.LastMealEnded);
    }

    [Fact]
    public void AMealCrossingMidnightBelongsToTheDayItStarted()
    {
        // One visit is never split in two. Attributed by start, not by end.
        var justBeforeMidnight = new DateTimeOffset(2026, 8, 22, 23, 59, 0, TimeSpan.Zero);
        var log = new DailyMealLog(new FakeClock(justBeforeMidnight));

        Assert.True(log.Add(AMeal(justBeforeMidnight, minutes: 5)));
        Assert.Equal(1, log.Meals);
    }
}
