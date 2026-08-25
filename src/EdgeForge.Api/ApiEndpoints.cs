using System.Text.Json;
using EdgeForge.Core;
using EdgeForge.Decision;
using EdgeForge.Storage;

namespace EdgeForge.Api;

/// <summary>
/// The REST surface: counts and timestamps, never frames.
/// </summary>
/// <remarks>
/// Shaped after the endpoints its predecessor served, minus everything to do with
/// video. MediaMTX delivers the picture now, so what remains is the event side of
/// the split - which is why "only events leave the device" holds again for the
/// .NET process without a carve-out.
/// </remarks>
public static class ApiEndpoints
{
    /// <summary>Ceiling on a range query, so a wide date range cannot stream a year.</summary>
    private const int MaxRows = 1000;

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Deliberately touches nothing. This is what gets curled to prove the
        // process is listening, and it must answer even when the camera does not.
        app.MapGet("/healthz", () => Results.Text("ok"));

        app.MapGet("/api/stats", (PipelineHost pipeline, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";

            return Results.Text(
                JsonSerializer.Serialize(pipeline.Current, StatsSnapshot.SerializerOptions),
                "application/json");
        });

        app.MapGet("/api/meals/today", (PipelineHost pipeline) =>
            Results.Json(Summaries(pipeline.MealsToday()), StatsSnapshot.SerializerOptions));

        app.MapGet("/api/meals", async (
            IMealStore store,
            DateOnly? from,
            DateOnly? to,
            CancellationToken cancellationToken) =>
        {
            // A caller with no range wants "recently", not "everything since the
            // camera was installed".
            var start = from ?? DateOnly.FromDateTime(DateTime.Now).AddDays(-7);

            var meals = await store.QueryAsync(start, to, MaxRows, cancellationToken);

            return Results.Json(
                new
                {
                    from = start,
                    to,
                    // Says so rather than leaving the caller to guess whether a
                    // round 1000 is the data or the ceiling.
                    truncated = meals.Count >= MaxRows,
                    meals = Summaries(meals)
                },
                StatsSnapshot.SerializerOptions);
        });

        app.MapGet("/api/config", (EdgeForgeOptions options) => Results.Json(new
        {
            // The WHEP endpoint on MediaMTX, handed over at runtime so the built
            // bundle carries no host-specific URL and the same files work from a
            // phone or over a tailnet.
            streamUrl = options.Api.StreamUrl,
            hlsUrl = options.Api.HlsUrl,
            className = options.Detector.ClassName

            // Camera:Source is deliberately NOT here, and must never be added.
            // That URL can hold the camera's credentials, and this response goes
            // to every browser that loads the page.
        }));
    }

    /// <summary>
    /// Builds the snapshot the API serves and pushes.
    /// </summary>
    /// <remarks>
    /// Pure and static so it can be tested without a host: booting the app boots
    /// the capture loop, which wants an ONNX session and a camera. The branches
    /// that can actually be wrong - a null meal sink, the difference between "no
    /// animals" and "nothing is watching" - are all here.
    ///
    /// The four optional properties are what separate this document from the one
    /// StatsFile writes. They stay null-by-omission there, so stats.json remains
    /// byte-identical and the OBS overlay keeps working.
    /// </remarks>
    /// <param name="meals">The decision layer's tally, or null when meals are off.</param>
    /// <param name="catsOnScreen">Animals in the latest frame, or null if no frame was seen.</param>
    /// <param name="pipelineRunning">Whether the capture loop is running.</param>
    /// <param name="now">Stamp for the document.</param>
    public static StatsSnapshot BuildStats(
        MealsSnapshot? meals,
        int? catsOnScreen,
        bool pipelineRunning,
        DateTimeOffset now)
    {
        if (meals is null)
        {
            // Meals are switched off. The document still has to be well formed and
            // parseable, so it reports an empty day rather than an error - the HUD
            // shows zeros, which is the truth.
            return new StatsSnapshot(
                StatsSnapshot.Utc(now),
                DateOnly.FromDateTime(now.ToLocalTime().DateTime).ToString("yyyy-MM-dd"),
                0,
                null,
                false,
                0)
            {
                CatsOnScreen = catsOnScreen,
                PipelineRunning = pipelineRunning
            };
        }

        return new StatsSnapshot(
            StatsSnapshot.Utc(now),
            meals.Day.ToString("yyyy-MM-dd"),
            meals.MealsToday,
            meals.LastMealEnded is { } ended ? StatsSnapshot.Utc(ended) : null,
            meals.IsFeeding,
            (int)Math.Round(meals.TotalDurationToday.TotalSeconds))
        {
            CurrentMealStartedUtc = meals.CurrentMealStarted is { } started
                ? StatsSnapshot.Utc(started)
                : null,
            CatsOnScreen = catsOnScreen,
            PipelineRunning = pipelineRunning
        };
    }

    private static StatsSnapshot.MealSummary[] Summaries(IReadOnlyList<MealEvent> meals)
    {
        var summaries = new StatsSnapshot.MealSummary[meals.Count];

        for (var i = 0; i < meals.Count; i++)
        {
            summaries[i] = StatsSnapshot.MealSummary.From(meals[i]);
        }

        return summaries;
    }
}
