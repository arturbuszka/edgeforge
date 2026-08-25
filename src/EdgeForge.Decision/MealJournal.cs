using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EdgeForge.Decision;

/// <summary>
/// Append-only history of completed meals, one JSON object per line.
/// </summary>
/// <remarks>
/// The persisted event history the roadmap calls for, in the form the working
/// agreement sanctions as the first answer: a flat file, not a database. It earns
/// its place in this first version rather than a later one for two reasons.
///
/// First, restart. A counter held only in memory resets to zero when the process
/// dies mid-day, and the overlay then shows a number that is quietly wrong for the
/// rest of the day. <see cref="ReadDay"/> rebuilds it.
///
/// Second, tuning. <see cref="MealThresholds"/> were chosen from how a cat is
/// believed to eat. The only honest way to confirm them is to read back what
/// actually happened over a few days, which requires having written it down.
///
/// JSON Lines rather than a JSON array: appending a line is one seek to the end,
/// needs no rewrite of what came before, and a file truncated by a power cut costs
/// at most the last line instead of the whole document.
/// </remarks>
public sealed class MealJournal
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,

        // Same reasoning as StatsFile: the default encoder escapes '+' against HTML
        // injection, and a timestamp on a line meant to be read back and grepped
        // should say "+00:00".
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// UTF-8 without a byte order mark. <see cref="Encoding.UTF8"/> emits one, and
    /// File.AppendAllText writes it when it creates the file - which puts three
    /// stray bytes in front of the first JSON object and makes strict parsers
    /// reject line one. Every later line would be fine, which is the kind of bug
    /// that surfaces months later on the oldest entry in the history.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;

    /// <param name="path">File to append to. Created on first write, with its directory.</param>
    public MealJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path => _path;

    /// <summary>Appends one completed meal.</summary>
    /// <exception cref="IOException">The line could not be written.</exception>
    public void Append(MealEvent meal)
    {
        ArgumentNullException.ThrowIfNull(meal);

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var line = JsonSerializer.Serialize(MealLine.From(meal), Json);

        File.AppendAllText(_path, line + Environment.NewLine, Utf8NoBom);
    }

    /// <summary>
    /// Reads back the meals that started on <paramref name="day"/>, in the local
    /// zone of <paramref name="time"/>. Returns nothing when the file does not
    /// exist yet - a first run is not an error.
    /// </summary>
    /// <remarks>
    /// Unparseable lines are skipped rather than thrown on. A half-written last
    /// line after a power cut must not stop the day's count from being rebuilt;
    /// losing one meal is better than losing the counter.
    /// </remarks>
    public IReadOnlyList<MealEvent> ReadDay(DateOnly day, TimeProvider? time = null)
    {
        var zone = (time ?? TimeProvider.System).LocalTimeZone;
        var meals = new List<MealEvent>();

        foreach (var meal in ReadAll())
        {
            if (LocalDayOf(meal, zone) == day) meals.Add(meal);
        }

        return meals;
    }

    /// <summary>
    /// Reads every meal in the file, oldest first. Returns nothing when the file
    /// does not exist yet - a first run is not an error.
    /// </summary>
    /// <remarks>
    /// Exists for the SQLite mirror, which has to import the whole history rather
    /// than one day of it. It stays here, rather than the storage layer reparsing
    /// the file, because the line format and the skip-don't-throw discipline below
    /// belong to the class that owns the format: a second parser would be a second
    /// place to get the timestamp round-trip wrong.
    ///
    /// Unparseable lines are skipped rather than thrown on. A half-written last
    /// line after a power cut must not stop the day's count from being rebuilt;
    /// losing one meal is better than losing the counter.
    /// </remarks>
    public IReadOnlyList<MealEvent> ReadAll()
    {
        var meals = new List<MealEvent>();

        if (!File.Exists(_path)) return meals;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            MealLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<MealLine>(line, Json);
            }
            catch (JsonException)
            {
                continue;
            }

            var meal = parsed?.ToEvent();
            if (meal is null) continue;

            meals.Add(meal);
        }

        return meals;
    }

    /// <summary>
    /// The calendar day a meal is attributed to: the local day it STARTED on.
    /// </summary>
    /// <remarks>
    /// The one definition of "today" in the system, and the reason it is public.
    /// The SQLite mirror denormalises this into a column, and if it computed the
    /// day by its own rule the two would disagree across midnight and across a
    /// DST boundary - which surfaces as the OBS overlay and the web HUD showing
    /// different meal counts, with nothing obviously wrong in either.
    /// </remarks>
    public static DateOnly LocalDayOf(MealEvent meal, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(meal);
        ArgumentNullException.ThrowIfNull(zone);

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(meal.Started, zone).DateTime);
    }

    /// <summary>
    /// The on-disk shape of one line. Separate from <see cref="MealEvent"/> so the
    /// domain record can change without silently invalidating months of history.
    /// </summary>
    private sealed record MealLine(
        string Started,
        string Ended,
        double DurationSeconds,
        int PeakCount,
        long ObservedFrames)
    {
        public static MealLine From(MealEvent meal) => new(
            meal.Started.ToString("O", CultureInfo.InvariantCulture),
            meal.Ended.ToString("O", CultureInfo.InvariantCulture),
            Math.Round(meal.Duration.TotalSeconds, 1),
            meal.PeakCount,
            meal.ObservedFrames);

        public MealEvent? ToEvent()
        {
            if (!DateTimeOffset.TryParse(
                    Started, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var started))
            {
                return null;
            }

            if (!DateTimeOffset.TryParse(
                    Ended, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var ended))
            {
                return null;
            }

            return new MealEvent(started, ended, PeakCount, ObservedFrames);
        }
    }
}
