using System.Text.Json;
using EdgeForge.Decision;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The two files that leave the device: the append-only history, and the small
/// snapshot the OBS overlay reads. Both are event data - counts and timestamps,
/// never frames.
/// </summary>
public class MealPersistenceTests : IDisposable
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

    private string PathTo(string name) => Path.Combine(_directory, name);

    private static MealEvent AMeal(DateTimeOffset started, double minutes = 2) =>
        new(started, started + TimeSpan.FromMinutes(minutes), PeakCount: 2, ObservedFrames: 1400);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void JournalAppendsOneLinePerMeal()
    {
        var journal = new MealJournal(PathTo("meals.jsonl"));

        journal.Append(AMeal(Noon));
        journal.Append(AMeal(Noon.AddHours(1)));

        var lines = File.ReadAllLines(journal.Path);

        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void JournalRoundTripsAMeal()
    {
        var journal = new MealJournal(PathTo("meals.jsonl"));
        var original = AMeal(Noon);

        journal.Append(original);
        var meal = Assert.Single(journal.ReadDay(new DateOnly(2026, 8, 22), new UtcClock(Noon)));

        Assert.Equal(original.Started, meal.Started);
        Assert.Equal(original.Ended, meal.Ended);
        Assert.Equal(original.PeakCount, meal.PeakCount);
        Assert.Equal(original.ObservedFrames, meal.ObservedFrames);
    }

    [Fact]
    public void JournalReadsOnlyTheRequestedDay()
    {
        var journal = new MealJournal(PathTo("meals.jsonl"));

        journal.Append(AMeal(Noon.AddDays(-1)));
        journal.Append(AMeal(Noon));
        journal.Append(AMeal(Noon.AddHours(3)));

        var today = journal.ReadDay(new DateOnly(2026, 8, 22), new UtcClock(Noon));

        Assert.Equal(2, today.Count);
    }

    [Fact]
    public void JournalOnAMissingFileIsEmpty_NotAnError()
    {
        // A first run has no history, which is not a failure.
        var journal = new MealJournal(PathTo("nothing-here.jsonl"));

        Assert.Empty(journal.ReadDay(new DateOnly(2026, 8, 22), new UtcClock(Noon)));
    }

    [Fact]
    public void JournalSkipsATruncatedLastLine()
    {
        // What a power cut leaves behind. Losing one meal beats losing the counter.
        var journal = new MealJournal(PathTo("meals.jsonl"));
        journal.Append(AMeal(Noon));

        File.AppendAllText(journal.Path, "{\"Started\":\"2026-08-22T13:0");

        Assert.Single(journal.ReadDay(new DateOnly(2026, 8, 22), new UtcClock(Noon)));
    }

    [Fact]
    public void JournalStartsWithJson_NotAByteOrderMark()
    {
        // File.AppendAllText with Encoding.UTF8 prepends a BOM when it creates the
        // file, which puts three stray bytes in front of the first object and makes
        // strict parsers reject line one - and only line one, months later.
        var journal = new MealJournal(PathTo("meals.jsonl"));
        journal.Append(AMeal(Noon));

        Assert.Equal((byte)'{', File.ReadAllBytes(journal.Path)[0]);
    }

    [Fact]
    public void TimestampsAreNotEscaped()
    {
        // The overlay and a human both read these. "+00:00" beats the escaped form
        // the default JSON encoder produces for a plus sign.
        var journal = new MealJournal(PathTo("meals.jsonl"));
        journal.Append(AMeal(Noon));

        var text = File.ReadAllText(journal.Path);

        // Verbatim string, so this is the six literal characters an escaped plus
        // would appear as. As a normal literal it would compile to a single plus -
        // and a plus is exactly what the timestamp is supposed to contain.
        Assert.DoesNotContain(@"\u002B", text);
        Assert.Contains("+00:00", text);
    }

    [Fact]
    public void StatsFileCarriesTheNumbersTheOverlayNeeds()
    {
        var log = new DailyMealLog(new UtcClock(Noon));
        log.Add(AMeal(Noon.AddHours(-2)));
        log.Add(AMeal(Noon.AddHours(-1)));

        var stats = new StatsFile(PathTo("stats.json"));
        stats.Write(log, currentlyFeeding: true, Noon);

        using var document = JsonDocument.Parse(File.ReadAllText(stats.Path));
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("mealsToday").GetInt32());
        Assert.Equal("2026-08-22", root.GetProperty("date").GetString());
        Assert.True(root.GetProperty("currentlyFeeding").GetBoolean());
        Assert.Equal(240, root.GetProperty("totalFeedingSecondsToday").GetInt32());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("lastMealEndedUtc").GetString()));
    }

    [Fact]
    public void StatsFileLeavesNoTemporaryFileBehind()
    {
        // The atomic write must not litter the directory OBS is watching.
        var stats = new StatsFile(PathTo("stats.json"));
        stats.Write(new DailyMealLog(new UtcClock(Noon)), currentlyFeeding: false, Noon);

        Assert.False(File.Exists(stats.Path + ".tmp"));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void StatsFileOverwritesInPlace()
    {
        var log = new DailyMealLog(new UtcClock(Noon));
        var stats = new StatsFile(PathTo("stats.json"));

        stats.Write(log, currentlyFeeding: false, Noon);
        log.Add(AMeal(Noon));
        stats.Write(log, currentlyFeeding: false, Noon);

        using var document = JsonDocument.Parse(File.ReadAllText(stats.Path));

        Assert.Equal(1, document.RootElement.GetProperty("mealsToday").GetInt32());
    }

    [Fact]
    public void StatsFileContainsNoImageData()
    {
        // The product principle, asserted rather than assumed: only events leave
        // the device. If a field ever carries pixels, this fails.
        var log = new DailyMealLog(new UtcClock(Noon));
        log.Add(AMeal(Noon));

        var stats = new StatsFile(PathTo("stats.json"));
        stats.Write(log, currentlyFeeding: false, Noon);

        using var document = JsonDocument.Parse(File.ReadAllText(stats.Path));

        var allowed = new[]
        {
            "updatedUtc", "date", "mealsToday", "lastMealEndedUtc",
            "currentlyFeeding", "totalFeedingSecondsToday"
        };

        foreach (var property in document.RootElement.EnumerateObject())
        {
            Assert.Contains(property.Name, allowed);
        }
    }
}
