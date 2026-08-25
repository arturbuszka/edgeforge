using EdgeForge.Api;
using EdgeForge.Decision;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The snapshot the API serves and pushes.
/// </summary>
/// <remarks>
/// Tested here rather than through the HTTP surface deliberately. Booting the app
/// boots the capture loop, which opens an ONNX session and a camera; making that
/// testable would mean injecting a fake detector through the container, which is
/// the DI-heavy shape the working agreement resists. Keeping the logic in a pure
/// static gets the coverage without the infrastructure.
///
/// The four optional properties are what distinguish this document from the one
/// StatsFile writes. StatsSnapshotTests guards the other side of that: they must
/// stay absent from stats.json, or the OBS overlay starts reading a richer
/// document than it was written against.
/// </remarks>
public class BuildStatsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 20, 14, 30, 0, TimeSpan.Zero);

    private static MealsSnapshot Meals(
        int mealsToday = 3,
        bool isFeeding = false,
        int totalSeconds = 640,
        DateTimeOffset? lastEnded = null,
        DateTimeOffset? currentStarted = null) =>
        new(
            mealsToday,
            isFeeding,
            TimeSpan.FromSeconds(totalSeconds),
            lastEnded,
            currentStarted,
            new DateOnly(2026, 8, 20));

    [Fact]
    public void CarriesTheMealTally()
    {
        var stats = ApiEndpoints.BuildStats(Meals(), catsOnScreen: 1, pipelineRunning: true, Now);

        Assert.Equal(3, stats.MealsToday);
        Assert.Equal(640, stats.TotalFeedingSecondsToday);
        Assert.Equal("2026-08-20", stats.Date);
    }

    [Fact]
    public void ReportsAnEmptyDayWhenMealsAreOff()
    {
        // Meals disabled is a configuration, not a fault. The document still has
        // to be well formed, or the HUD shows an error where it should show zero.
        var stats = ApiEndpoints.BuildStats(null, catsOnScreen: 0, pipelineRunning: true, Now);

        Assert.Equal(0, stats.MealsToday);
        Assert.False(stats.CurrentlyFeeding);
        Assert.Null(stats.LastMealEndedUtc);
        Assert.Null(stats.CurrentMealStartedUtc);
    }

    [Fact]
    public void StillReportsPipelineStateWhenMealsAreOff()
    {
        // The half of the document that does not depend on the decision layer has
        // to survive it being switched off, or the HUD cannot tell "no meals" from
        // "nothing is running".
        var stats = ApiEndpoints.BuildStats(null, catsOnScreen: 2, pipelineRunning: true, Now);

        Assert.Equal(2, stats.CatsOnScreen);
        Assert.True(stats.PipelineRunning);
    }

    [Fact]
    public void DistinguishesNoAnimalsFromNothingWatching()
    {
        // The distinction the HUD renders differently: zero cats is a reading,
        // null is the absence of one. Collapsing them would show a confident "0"
        // while the camera is disconnected.
        var seen = ApiEndpoints.BuildStats(Meals(), catsOnScreen: 0, pipelineRunning: true, Now);
        var unseen = ApiEndpoints.BuildStats(Meals(), catsOnScreen: null, pipelineRunning: false, Now);

        Assert.Equal(0, seen.CatsOnScreen);
        Assert.Null(unseen.CatsOnScreen);
    }

    [Fact]
    public void ReportsTheCurrentMealWhileFeeding()
    {
        var started = Now.AddMinutes(-4);

        var stats = ApiEndpoints.BuildStats(
            Meals(isFeeding: true, currentStarted: started),
            catsOnScreen: 1,
            pipelineRunning: true,
            Now);

        Assert.True(stats.CurrentlyFeeding);
        Assert.Equal(StatsSnapshot.Utc(started), stats.CurrentMealStartedUtc);
    }

    [Fact]
    public void OmitsTheCurrentMealWhenIdle()
    {
        var stats = ApiEndpoints.BuildStats(
            Meals(isFeeding: false, currentStarted: null),
            catsOnScreen: 0,
            pipelineRunning: true,
            Now);

        Assert.Null(stats.CurrentMealStartedUtc);
    }

    [Fact]
    public void FormatsTimestampsAsRoundTrippableUtc()
    {
        // The front-end parses these with Date.parse; a non-UTC or non-round-trip
        // format is the kind of thing that works locally and drifts by an hour on
        // a machine in another zone.
        var ended = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.FromHours(2));

        var stats = ApiEndpoints.BuildStats(
            Meals(lastEnded: ended),
            catsOnScreen: null,
            pipelineRunning: true,
            Now);

        Assert.Equal(StatsSnapshot.Utc(ended), stats.LastMealEndedUtc);

        // Round-trip "O" with a zero offset, not a "Z" suffix. Spelled out
        // because stats.json has always carried this exact shape and the OBS
        // overlay parses it; "tidying" it to Z would be a silent format change
        // for a file this repository does not control the readers of.
        Assert.EndsWith("+00:00", stats.LastMealEndedUtc!, StringComparison.Ordinal);

        // 12:00+02:00 is 10:00 UTC. The conversion, not just the formatting.
        Assert.StartsWith("2026-08-20T10:00:00", stats.LastMealEndedUtc!, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundsFeedingSecondsToAWholeNumber()
    {
        var stats = ApiEndpoints.BuildStats(
            Meals(totalSeconds: 0) with { TotalDurationToday = TimeSpan.FromSeconds(12.6) },
            catsOnScreen: null,
            pipelineRunning: true,
            Now);

        Assert.Equal(13, stats.TotalFeedingSecondsToday);
    }

    [Fact]
    public void StampsTheDocumentWithTheGivenTime()
    {
        var stats = ApiEndpoints.BuildStats(Meals(), catsOnScreen: null, pipelineRunning: true, Now);

        Assert.Equal(StatsSnapshot.Utc(Now), stats.UpdatedUtc);
    }
}
