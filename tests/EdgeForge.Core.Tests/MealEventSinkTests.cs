using EdgeForge.Core;
using EdgeForge.Decision;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The wiring between the pipeline and the decision layer. The state machine is
/// covered by <see cref="MealDetectorTests"/>; what matters here is that the sink
/// decorates rather than replaces, survives a failing disk, and rebuilds the day
/// after a restart.
/// </summary>
public class MealEventSinkTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "edgeforge-tests", Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Noon =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class UtcClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Records that the inner action still runs, and how often.</summary>
    private sealed class CountingAction : IInspectionAction
    {
        public int Calls { get; private set; }

        public Task ExecuteAsync(InspectionResult result, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private string PathTo(string name) => Path.Combine(_directory, name);

    private static InspectionResult AResult(int cats)
    {
        var detections = Enumerable.Range(0, cats)
            .Select(_ => new Detection("cat", 0.76, new BoundingBox(0.1, 0.1, 0.2, 0.2)))
            .ToArray();

        return new InspectionResult(
            "CAM-000001", InspectionStatus.Ok, null, detections, TimeSpan.Zero);
    }

    private static MealEvent AMeal(DateTimeOffset started, double minutes = 2) =>
        new(started, started + TimeSpan.FromMinutes(minutes), PeakCount: 1, ObservedFrames: 500);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task TheInnerActionStillRuns()
    {
        // Decorating, not replacing: the console writer and the viewer log must
        // keep working exactly as before.
        var inner = new CountingAction();
        var sink = new MealEventSink(inner, new MealDetector(), new DailyMealLog(new UtcClock(Noon)));

        await sink.ExecuteAsync(AResult(1));
        await sink.ExecuteAsync(AResult(0));

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task OnlyTheConfiguredClassCounts()
    {
        var log = new DailyMealLog(new UtcClock(Noon));
        var sink = new MealEventSink(
            new CountingAction(),
            new MealDetector(new MealThresholds
            {
                MinPresenceToStart = TimeSpan.Zero.Add(TimeSpan.FromMilliseconds(1)),
                MaxAbsenceToEnd = TimeSpan.FromMilliseconds(1),
                MinMealDuration = TimeSpan.Zero
            }),
            log,
            className: "bird");

        var cats = new InspectionResult(
            "CAM-000001", InspectionStatus.Ok, null,
            [new Detection("cat", 0.9, new BoundingBox(0, 0, 0.1, 0.1))], TimeSpan.Zero);

        await sink.ExecuteAsync(cats);

        Assert.False(sink.Snapshot.IsFeeding);
    }

    [Fact]
    public async Task WritesStatsAndHistoryForACompletedMeal()
    {
        // Thresholds shrunk to milliseconds so a meal completes inside a test.
        var thresholds = new MealThresholds
        {
            MinPresenceToStart = TimeSpan.FromMilliseconds(1),
            MaxAbsenceToEnd = TimeSpan.FromMilliseconds(1),
            MinMealDuration = TimeSpan.Zero
        };

        var journal = new MealJournal(PathTo("meals.jsonl"));
        var stats = new StatsFile(PathTo("stats.json"));

        var sink = new MealEventSink(
            new CountingAction(),
            new MealDetector(thresholds),
            new DailyMealLog(),
            journal,
            stats);

        await sink.ExecuteAsync(AResult(1));
        await Task.Delay(20);
        await sink.ExecuteAsync(AResult(1));
        await Task.Delay(20);
        await sink.ExecuteAsync(AResult(0));
        await Task.Delay(20);
        await sink.ExecuteAsync(AResult(0));

        Assert.Equal(1, sink.Snapshot.MealsToday);
        Assert.True(File.Exists(stats.Path));
        Assert.Single(File.ReadAllLines(journal.Path));
    }

    [Fact]
    public async Task FlushClosesAMealStillInProgress()
    {
        var thresholds = new MealThresholds
        {
            MinPresenceToStart = TimeSpan.FromMilliseconds(1),
            MaxAbsenceToEnd = TimeSpan.FromHours(1),
            MinMealDuration = TimeSpan.Zero
        };

        var sink = new MealEventSink(
            new CountingAction(), new MealDetector(thresholds), new DailyMealLog());

        await sink.ExecuteAsync(AResult(1));
        await Task.Delay(20);
        await sink.ExecuteAsync(AResult(1));

        Assert.Equal(0, sink.Snapshot.MealsToday);

        sink.Flush();

        Assert.Equal(1, sink.Snapshot.MealsToday);
        Assert.False(sink.Snapshot.IsFeeding);
    }

    [Fact]
    public void RestoreRebuildsTodaysCountAfterARestart()
    {
        // The failure this exists to prevent: a restart at noon showing zero meals
        // on the overlay for the rest of the day.
        var journal = new MealJournal(PathTo("meals.jsonl"));
        journal.Append(AMeal(Noon.AddHours(-3)));
        journal.Append(AMeal(Noon.AddHours(-2)));
        journal.Append(AMeal(Noon.AddDays(-1)));

        var clock = new UtcClock(Noon);
        var sink = new MealEventSink(
            new CountingAction(), new MealDetector(), new DailyMealLog(clock), journal);

        sink.Restore(clock);

        // Two from today; yesterday's line is ignored.
        Assert.Equal(2, sink.Snapshot.MealsToday);
    }

    [Fact]
    public void RestoreWithNoHistoryStartsAtZero()
    {
        var sink = new MealEventSink(
            new CountingAction(),
            new MealDetector(),
            new DailyMealLog(new UtcClock(Noon)),
            new MealJournal(PathTo("meals.jsonl")));

        sink.Restore(new UtcClock(Noon));

        Assert.Equal(0, sink.Snapshot.MealsToday);
    }

    [Fact]
    public async Task AnUnwritableJournalDoesNotStopThePipeline()
    {
        // A full or read-only disk must degrade to counting in memory, not take
        // the capture loop down with it.
        var thresholds = new MealThresholds
        {
            MinPresenceToStart = TimeSpan.FromMilliseconds(1),
            MaxAbsenceToEnd = TimeSpan.FromMilliseconds(1),
            MinMealDuration = TimeSpan.Zero
        };

        // A directory where the file should be: every write fails.
        var blocked = PathTo("meals.jsonl");
        Directory.CreateDirectory(blocked);

        var notes = new List<string>();
        var sink = new MealEventSink(
            new CountingAction(),
            new MealDetector(thresholds),
            new DailyMealLog(),
            new MealJournal(blocked),
            notify: notes.Add);

        await sink.ExecuteAsync(AResult(1));
        await Task.Delay(20);
        await sink.ExecuteAsync(AResult(1));
        await Task.Delay(20);
        await sink.ExecuteAsync(AResult(0));
        await Task.Delay(20);
        await sink.ExecuteAsync(AResult(0));

        // Counted in memory even though it could not be persisted.
        Assert.Equal(1, sink.Snapshot.MealsToday);
        Assert.Contains(notes, n => n.Contains("Could not write meal history"));
    }

    [Fact]
    public async Task ReportsAMealInTheLog()
    {
        var thresholds = new MealThresholds
        {
            MinPresenceToStart = TimeSpan.FromMilliseconds(1),
            MaxAbsenceToEnd = TimeSpan.FromMilliseconds(1),
            MinMealDuration = TimeSpan.Zero
        };

        var notes = new List<string>();
        var sink = new MealEventSink(
            new CountingAction(), new MealDetector(thresholds), new DailyMealLog(),
            notify: notes.Add);

        await sink.ExecuteAsync(AResult(2));
        await Task.Delay(20);
        await sink.ExecuteAsync(AResult(2));
        await Task.Delay(20);
        await sink.ExecuteAsync(AResult(0));
        await Task.Delay(20);
        await sink.ExecuteAsync(AResult(0));

        Assert.Contains(notes, n => n.StartsWith("Meal #1 ended"));
    }

    [Fact]
    public void SnapshotCarriesTodaysTotalsNotJustTheCount()
    {
        // The web view shows total time and the last meal beside the count. They
        // come from one lock so they cannot disagree with each other.
        var journal = new MealJournal(PathTo("meals.jsonl"));
        journal.Append(AMeal(Noon.AddHours(-3), minutes: 4));
        journal.Append(AMeal(Noon.AddHours(-2), minutes: 6));

        var clock = new UtcClock(Noon);
        var sink = new MealEventSink(
            new CountingAction(), new MealDetector(), new DailyMealLog(clock), journal);

        sink.Restore(clock);

        var snapshot = sink.Snapshot;

        Assert.Equal(2, snapshot.MealsToday);
        Assert.False(snapshot.IsFeeding);
        Assert.Equal(TimeSpan.FromMinutes(10), snapshot.TotalDurationToday);
        Assert.Equal(Noon.AddHours(-2).AddMinutes(6), snapshot.LastMealEnded);
        Assert.Null(snapshot.CurrentMealStarted);
        Assert.Equal(DateOnly.FromDateTime(Noon.UtcDateTime), snapshot.Day);
    }

    [Fact]
    public void MealsTodayExcludesOtherDays()
    {
        // The journal holds history for every day. Restoring must not let yesterday's
        // meals into today's list, which would put them on the page under "today".
        var journal = new MealJournal(PathTo("meals.jsonl"));
        journal.Append(AMeal(Noon.AddDays(-1)));
        journal.Append(AMeal(Noon.AddHours(-2)));

        var clock = new UtcClock(Noon);
        var sink = new MealEventSink(
            new CountingAction(), new MealDetector(), new DailyMealLog(clock), journal);

        sink.Restore(clock);

        var meals = sink.MealsToday();

        Assert.Equal(sink.Snapshot.MealsToday, meals.Count);
        Assert.All(meals, meal => Assert.Equal(clock.GetUtcNow().Date, meal.Started.Date));
    }

    [Fact]
    public void MealsTodayReturnsNewestFirstAndHonoursTheLimit()
    {
        var journal = new MealJournal(PathTo("meals.jsonl"));
        journal.Append(AMeal(Noon.AddHours(-5)));
        journal.Append(AMeal(Noon.AddHours(-3)));
        journal.Append(AMeal(Noon.AddHours(-1)));

        var clock = new UtcClock(Noon);
        var sink = new MealEventSink(
            new CountingAction(), new MealDetector(), new DailyMealLog(clock), journal);

        sink.Restore(clock);

        var all = sink.MealsToday();
        Assert.Equal(3, all.Count);
        Assert.Equal(Noon.AddHours(-1), all[0].Started);
        Assert.Equal(Noon.AddHours(-5), all[2].Started);

        var limited = sink.MealsToday(limit: 2);
        Assert.Equal(2, limited.Count);
        Assert.Equal(Noon.AddHours(-1), limited[0].Started);
    }

    [Fact]
    public void MealsTodayWithoutAJournalIsEmptyRatherThanAFailure()
    {
        var sink = new MealEventSink(
            new CountingAction(), new MealDetector(), new DailyMealLog(new UtcClock(Noon)));

        sink.Restore(new UtcClock(Noon));

        Assert.Empty(sink.MealsToday());
    }
}
