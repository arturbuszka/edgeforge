using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeForge.Decision;

/// <summary>
/// The current state of the day, as one flat JSON document.
/// </summary>
/// <remarks>
/// Deliberately flat and denormalised. The original consumer is a dozen lines of
/// JavaScript in an OBS Browser Source, and every nested object there becomes
/// another line of parsing that can break live on stream.
///
/// <para><b>This type is the contract.</b> It is what <see cref="StatsFile"/> writes
/// to stats.json and what the optional web view serves from /api/stats. Both go
/// through <see cref="Build"/> and <see cref="SerializerOptions"/> so the file and
/// the endpoint cannot drift apart. The six original properties are read by
/// overlay/meals.html: they may gain siblings, but they must never be renamed,
/// retyped or reordered out of existence.</para>
///
/// <para>The optional properties are omitted when null, so stats.json - which never
/// sets them - stays byte-identical to what it was before the web view existed.</para>
///
/// <para>It carries counts and timestamps only. No frames, no boxes, no image data.
/// Nor the camera source: that URL can hold credentials, and this document is
/// served over the network.</para>
/// </remarks>
public sealed record StatsSnapshot(
    [property: JsonPropertyName("updatedUtc")] string UpdatedUtc,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("mealsToday")] int MealsToday,
    [property: JsonPropertyName("lastMealEndedUtc")] string? LastMealEndedUtc,
    [property: JsonPropertyName("currentlyFeeding")] bool CurrentlyFeeding,
    [property: JsonPropertyName("totalFeedingSecondsToday")] int TotalFeedingSecondsToday)
{
    /// <summary>When a meal is in progress, when it started. Null otherwise.</summary>
    [property: JsonPropertyName("currentMealStartedUtc")]
    public string? CurrentMealStartedUtc { get; init; }

    /// <summary>Animals seen in the most recent frame. Null when nothing is reading frames.</summary>
    [property: JsonPropertyName("catsOnScreen")]
    public int? CatsOnScreen { get; init; }

    /// <summary>Whether the capture loop is running. Null when the caller cannot tell.</summary>
    [property: JsonPropertyName("pipelineRunning")]
    public bool? PipelineRunning { get; init; }

    /// <summary>Today's completed meals, newest first. Null when not requested.</summary>
    [property: JsonPropertyName("meals")]
    public IReadOnlyList<MealSummary>? Meals { get; init; }

    /// <summary>
    /// The exact options <see cref="StatsFile"/> writes with, so anything serialising
    /// this elsewhere produces the same document.
    /// </summary>
    /// <remarks>
    /// Relaxed escaping so a timestamp reads "+00:00" rather than the escaped form the
    /// default encoder produces for a plus sign. The default encoder escapes '+' as a
    /// defence against HTML injection; this document is read as JSON by an OBS Browser
    /// Source and by fetch(), never interpolated into markup.
    /// </remarks>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Builds the six-field snapshot that stats.json has always carried.</summary>
    public static StatsSnapshot Build(DailyMealLog log, bool currentlyFeeding, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(log);

        return new StatsSnapshot(
            Utc(now),
            log.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            log.Meals,
            log.LastMealEnded is { } ended ? Utc(ended) : null,
            currentlyFeeding,
            (int)Math.Round(log.TotalDuration.TotalSeconds));
    }

    /// <summary>Round-trippable UTC, the format every consumer here parses.</summary>
    public static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>One completed meal, for the live view's list.</summary>
    public sealed record MealSummary(
        [property: JsonPropertyName("startedUtc")] string StartedUtc,
        [property: JsonPropertyName("endedUtc")] string EndedUtc,
        [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
        [property: JsonPropertyName("peakCount")] int PeakCount)
    {
        public static MealSummary From(MealEvent meal)
        {
            ArgumentNullException.ThrowIfNull(meal);

            return new MealSummary(
                Utc(meal.Started),
                Utc(meal.Ended),
                Math.Round(meal.Duration.TotalSeconds, 1),
                meal.PeakCount);
        }
    }
}
