using EdgeForge.Decision;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The debounce rule. The case that matters most is the one that motivated the
/// whole decision layer: a long stretch of detected frames is ONE event, not one
/// event per frame.
/// </summary>
/// <remarks>
/// Every test drives synthetic timestamps rather than sleeping, which is the
/// point of <see cref="MealDetector"/> taking a timestamp instead of reading a
/// clock: a simulated hour of footage runs in microseconds.
/// </remarks>
public class MealDetectorTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Feeds frames at a realistic 23 fps for <paramref name="seconds"/>, all with
    /// the same count, and returns every meal that closed while doing so.
    /// </summary>
    private static List<MealEvent> Feed(
        MealDetector detector,
        ref DateTimeOffset now,
        double seconds,
        int count)
    {
        var meals = new List<MealEvent>();
        var step = TimeSpan.FromMilliseconds(1000.0 / 23);
        var until = now + TimeSpan.FromSeconds(seconds);

        while (now < until)
        {
            var meal = detector.Observe(now, count);
            if (meal is not null) meals.Add(meal);
            now += step;
        }

        return meals;
    }

    [Fact]
    public void SixtySecondsOfDetections_IsOneMeal_NotOneEventPerFrame()
    {
        // The bug this layer exists to fix: 1400 detected frames used to read as
        // 1400 meals. One visit, one event, regardless of frame rate.
        var detector = new MealDetector();
        var now = Origin;

        var duringMeal = Feed(detector, ref now, seconds: 60, count: 1);
        var afterLeaving = Feed(detector, ref now, seconds: 90, count: 0);

        Assert.Empty(duringMeal);
        var meal = Assert.Single(afterLeaving);
        Assert.Equal(60, meal.Duration.TotalSeconds, tolerance: 1.0);
        Assert.True(meal.ObservedFrames > 1000, $"expected ~1380 frames, got {meal.ObservedFrames}");
    }

    [Fact]
    public void WalkingPastTheBowls_IsNotAMeal()
    {
        // Two seconds of cat crossing the frame, under MinPresenceToStart.
        var detector = new MealDetector();
        var now = Origin;

        Feed(detector, ref now, seconds: 2, count: 1);
        var meals = Feed(detector, ref now, seconds: 90, count: 0);

        Assert.Empty(meals);
    }

    [Fact]
    public void APauseMidMealDoesNotSplitIt()
    {
        // Eats, wanders off for 20 s, comes back. One meal - this is the behaviour
        // MaxAbsenceToEnd is set to 60 s for.
        var detector = new MealDetector();
        var now = Origin;

        Feed(detector, ref now, seconds: 40, count: 1);
        var duringPause = Feed(detector, ref now, seconds: 20, count: 0);
        Feed(detector, ref now, seconds: 30, count: 1);
        var afterLeaving = Feed(detector, ref now, seconds: 90, count: 0);

        Assert.Empty(duringPause);
        var meal = Assert.Single(afterLeaving);
        Assert.Equal(90, meal.Duration.TotalSeconds, tolerance: 1.0);
    }

    [Fact]
    public void ALongGapSplitsIntoTwoMeals()
    {
        var detector = new MealDetector();
        var now = Origin;

        Feed(detector, ref now, seconds: 30, count: 1);
        var afterFirst = Feed(detector, ref now, seconds: 120, count: 0);
        Feed(detector, ref now, seconds: 30, count: 1);
        var afterSecond = Feed(detector, ref now, seconds: 120, count: 0);

        Assert.Single(afterFirst);
        Assert.Single(afterSecond);
    }

    [Fact]
    public void SevenVisitsProduceSevenEvents()
    {
        // The headline number, end to end: a day of seven visits reads as seven.
        var detector = new MealDetector();
        var now = Origin;
        var meals = new List<MealEvent>();

        for (var i = 0; i < 7; i++)
        {
            Feed(detector, ref now, seconds: 45, count: 1);
            meals.AddRange(Feed(detector, ref now, seconds: 300, count: 0));
        }

        Assert.Equal(7, meals.Count);
    }

    [Fact]
    public void AMealShorterThanTheMinimumIsDiscarded()
    {
        // Past MinPresenceToStart (5 s) but under MinMealDuration (10 s): lingered,
        // did not eat.
        var detector = new MealDetector();
        var now = Origin;

        Feed(detector, ref now, seconds: 7, count: 1);
        var meals = Feed(detector, ref now, seconds: 90, count: 0);

        Assert.Empty(meals);
    }

    [Fact]
    public void AStalledPipelineDoesNotEndAMeal()
    {
        // Absence is measured in time, not in empty frames. A gap in delivery -
        // dropped frames, a hiccup - must not look like the animal leaving.
        var detector = new MealDetector();
        var now = Origin;

        Feed(detector, ref now, seconds: 30, count: 1);

        // One lone empty frame, then the stream resumes with the cat still there.
        Assert.Null(detector.Observe(now, 0));
        now += TimeSpan.FromSeconds(5);

        Feed(detector, ref now, seconds: 30, count: 1);
        var meals = Feed(detector, ref now, seconds: 90, count: 0);

        var meal = Assert.Single(meals);
        Assert.Equal(65, meal.Duration.TotalSeconds, tolerance: 1.5);
    }

    [Fact]
    public void PeakCountIsTheMostSeenAtOnce()
    {
        // Two cats at two bowls is one meal with a peak of 2, not two meals.
        var detector = new MealDetector();
        var now = Origin;

        Feed(detector, ref now, seconds: 20, count: 1);
        Feed(detector, ref now, seconds: 20, count: 3);
        Feed(detector, ref now, seconds: 20, count: 2);
        var meals = Feed(detector, ref now, seconds: 90, count: 0);

        Assert.Equal(3, Assert.Single(meals).PeakCount);
    }

    [Fact]
    public void DurationIsBackdatedToTheFirstSighting()
    {
        // Not to the moment MinPresenceToStart elapsed: the animal was already
        // eating during those five seconds, and dating from the threshold would
        // understate every meal by it.
        var detector = new MealDetector();
        var now = Origin;
        var firstSighting = now;

        Feed(detector, ref now, seconds: 30, count: 1);
        var meals = Feed(detector, ref now, seconds: 90, count: 0);

        Assert.Equal(firstSighting, Assert.Single(meals).Started, TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void FlushClosesAMealStillInProgress()
    {
        // End of a video file, or shutdown. Without this the meal is lost and the
        // count for the day is short by one.
        var detector = new MealDetector();
        var now = Origin;

        Feed(detector, ref now, seconds: 40, count: 1);

        var meal = detector.Flush();

        Assert.NotNull(meal);
        Assert.Equal(40, meal.Duration.TotalSeconds, tolerance: 1.0);
        Assert.False(detector.IsFeeding);
    }

    [Fact]
    public void FlushWithNothingInProgressReturnsNull()
    {
        Assert.Null(new MealDetector().Flush());
    }

    [Fact]
    public void IsFeedingTracksTheMealInProgress()
    {
        var detector = new MealDetector();
        var now = Origin;

        Assert.False(detector.IsFeeding);

        Feed(detector, ref now, seconds: 2, count: 1);
        Assert.False(detector.IsFeeding);   // seen, but under the start threshold

        Feed(detector, ref now, seconds: 10, count: 1);
        Assert.True(detector.IsFeeding);

        Feed(detector, ref now, seconds: 90, count: 0);
        Assert.False(detector.IsFeeding);
    }

    [Fact]
    public void ThresholdsAreConfigurable()
    {
        // The coop will not eat like a cat. A visit that is a meal under tight
        // thresholds is nothing under the defaults.
        var thresholds = new MealThresholds
        {
            MinPresenceToStart = TimeSpan.FromSeconds(1),
            MaxAbsenceToEnd = TimeSpan.FromSeconds(5),
            MinMealDuration = TimeSpan.FromSeconds(2)
        };

        var detector = new MealDetector(thresholds);
        var now = Origin;

        Feed(detector, ref now, seconds: 3, count: 1);
        var meals = Feed(detector, ref now, seconds: 10, count: 0);

        Assert.Single(meals);
    }

    [Fact]
    public void NegativeCountIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MealDetector().Observe(Origin, -1));
    }
}
