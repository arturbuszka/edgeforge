using System.Text.Json;
using EdgeForge.Decision;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The stats.json contract. overlay/meals.html reads this document from local disk
/// and the optional web view serves the same shape over HTTP, so these tests exist
/// to stop the two from drifting apart - a rename here breaks an overlay that is
/// live on stream, with no compiler to catch it.
/// </summary>
public class StatsSnapshotTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Local time pinned to UTC, matching DailyMealLogTests: the day boundary is a
    /// local-midnight question, and the assertions here are about the document shape
    /// rather than about the machine's time zone.
    /// </summary>
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static DailyMealLog LogWith(params MealEvent[] meals)
    {
        var log = new DailyMealLog(new FakeClock(Noon));

        foreach (var meal in meals) log.Add(meal);

        return log;
    }

    private static MealEvent Meal(int startMinute, int durationMinutes, int peak = 1) =>
        new(
            Noon.AddMinutes(startMinute),
            Noon.AddMinutes(startMinute + durationMinutes),
            peak,
            durationMinutes * 60);

    [Fact]
    public void MatchesTheOverlayContract()
    {
        // These six names and types are what overlay/meals.html parses. Adding
        // siblings is fine; renaming or retyping any of these is not.
        var snapshot = StatsSnapshot.Build(LogWith(Meal(0, 5)), currentlyFeeding: true, Noon);

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(snapshot, StatsSnapshot.SerializerOptions));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.String, root.GetProperty("updatedUtc").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("date").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("mealsToday").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("lastMealEndedUtc").ValueKind);
        Assert.Equal(JsonValueKind.True, root.GetProperty("currentlyFeeding").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("totalFeedingSecondsToday").ValueKind);
    }

    [Fact]
    public void SerialisesTimestampsUnescaped()
    {
        // The default encoder escapes '+' as \u002B. A human opening stats.json
        // to check the overlay should see a timestamp, not an escape, which is why
        // SerializerOptions uses the relaxed encoder.
        var snapshot = StatsSnapshot.Build(LogWith(), currentlyFeeding: false, Noon);

        var json = JsonSerializer.Serialize(snapshot, StatsSnapshot.SerializerOptions);

        Assert.Contains("+00:00", json);
        Assert.DoesNotContain(@"\u002B", json);
    }

    [Fact]
    public void NoLastMealIsAbsentRatherThanAnEmptyString()
    {
        // WhenWritingNull omits the property entirely, which reads back as undefined
        // in the overlay's JavaScript and is falsy there just as null would be. What
        // matters is that it is never an empty string: that would be truthy and would
        // put a blank time on the stream.
        var snapshot = StatsSnapshot.Build(LogWith(), currentlyFeeding: false, Noon);

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(snapshot, StatsSnapshot.SerializerOptions));

        Assert.False(document.RootElement.TryGetProperty("lastMealEndedUtc", out _));
    }

    [Fact]
    public void ALastMealSerialisesAsARoundTrippableTimestamp()
    {
        var snapshot = StatsSnapshot.Build(LogWith(Meal(0, 5)), currentlyFeeding: false, Noon);

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(snapshot, StatsSnapshot.SerializerOptions));

        var ended = document.RootElement.GetProperty("lastMealEndedUtc").GetString();

        Assert.Equal(Noon.AddMinutes(5), DateTimeOffset.Parse(ended!));
    }

    [Fact]
    public void OptionalFieldsAreOmittedWhenUnset()
    {
        // stats.json never sets them, so the file on disk must be unchanged by the
        // web view's existence.
        var snapshot = StatsSnapshot.Build(LogWith(Meal(0, 3)), currentlyFeeding: false, Noon);

        var json = JsonSerializer.Serialize(snapshot, StatsSnapshot.SerializerOptions);

        Assert.DoesNotContain("currentMealStartedUtc", json);
        Assert.DoesNotContain("catsOnScreen", json);
        Assert.DoesNotContain("pipelineRunning", json);
        Assert.DoesNotContain("\"meals\"", json);
    }

    [Fact]
    public void WrittenFileAndBuiltSnapshotAgree()
    {
        // The point of lifting this type out of StatsFile: one shape, one writer.
        var log = LogWith(Meal(0, 4), Meal(10, 6, peak: 2));
        var path = Path.Combine(Path.GetTempPath(), $"edgeforge-{Guid.NewGuid():N}.json");

        try
        {
            new StatsFile(path).Write(log, currentlyFeeding: true, Noon);

            var onDisk = File.ReadAllText(path);
            var built = JsonSerializer.Serialize(
                StatsSnapshot.Build(log, currentlyFeeding: true, Noon),
                StatsSnapshot.SerializerOptions);

            Assert.Equal(built, onDisk);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExtraFieldsSerialiseWhenTheWebViewSetsThem()
    {
        var snapshot = StatsSnapshot.Build(LogWith(Meal(0, 2)), currentlyFeeding: true, Noon) with
        {
            CurrentMealStartedUtc = StatsSnapshot.Utc(Noon),
            CatsOnScreen = 2,
            PipelineRunning = true,
            Meals = [StatsSnapshot.MealSummary.From(Meal(0, 2, peak: 2))]
        };

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(snapshot, StatsSnapshot.SerializerOptions));
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("catsOnScreen").GetInt32());
        Assert.True(root.GetProperty("pipelineRunning").GetBoolean());

        var meal = Assert.Single(root.GetProperty("meals").EnumerateArray());
        Assert.Equal(120, meal.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(2, meal.GetProperty("peakCount").GetInt32());
    }
}
