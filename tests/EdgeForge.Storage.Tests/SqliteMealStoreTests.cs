using EdgeForge.Decision;
using Xunit;

namespace EdgeForge.Storage.Tests;

/// <summary>
/// The SQLite mirror of the meal journal.
/// </summary>
/// <remarks>
/// The load-bearing test here is <see cref="DayIsTheLocalDayNotTheUtcOne"/>. The
/// journal and this table must agree about which day a meal belongs to; when they
/// drift, the OBS overlay and the web HUD report different counts for the same
/// day and neither looks broken.
/// </remarks>
public class SqliteMealStoreTests : IDisposable
{
    private readonly StoreFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    private static MealEvent Meal(string startedUtc, double seconds = 120, int peak = 1, long frames = 300) =>
        new(
            DateTimeOffset.Parse(startedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(startedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind)
                .AddSeconds(seconds),
            peak,
            frames);

    [Fact]
    public async Task RoundTripsAMeal()
    {
        var store = _fixture.Store();
        var meal = Meal("2026-08-20T09:15:00+00:00", seconds: 244, peak: 2, frames: 5612);

        await store.AppendAsync(meal);
        var stored = Assert.Single(await store.QueryAsync(null, null));

        // Instant equality, not string equality: DateTimeOffset comparison is what
        // the application cares about, and SQLite storing TEXT must not change it.
        Assert.Equal(meal.Started, stored.Started);
        Assert.Equal(meal.Ended, stored.Ended);
        Assert.Equal(meal.PeakCount, stored.PeakCount);
        Assert.Equal(meal.ObservedFrames, stored.ObservedFrames);
        Assert.Equal(meal.Duration, stored.Duration);
    }

    [Fact]
    public async Task AppendingTheSameMealTwiceStoresOneRow()
    {
        // The property the backfill depends on: it runs on every start with no
        // marker file, so re-offering stored meals must be a no-op.
        var store = _fixture.Store();
        var meal = Meal("2026-08-20T09:15:00+00:00");

        await store.AppendAsync(meal);
        await store.AppendAsync(meal);

        Assert.Equal(1, await store.CountAsync());
    }

    [Fact]
    public async Task DayIsTheLocalDayNotTheUtcOne()
    {
        // 23:30 UTC is 01:30 the NEXT day at +02:00. The meal belongs to that
        // later local day, which is what MealJournal.ReadDay would also say.
        var store = _fixture.Store();
        var meal = Meal("2026-08-20T23:30:00+00:00");

        await store.AppendAsync(meal);

        Assert.Empty(await store.DayAsync(new DateOnly(2026, 8, 20)));
        Assert.Single(await store.DayAsync(new DateOnly(2026, 8, 21)));
    }

    [Fact]
    public async Task DayAgreesWithTheJournal()
    {
        // Stated directly rather than inferred from the case above: the two must
        // not merely both be "local", they must be the same local.
        var store = _fixture.Store();
        var meal = Meal("2026-08-20T23:30:00+00:00");

        await store.AppendAsync(meal);

        var journalDay = MealJournal.LocalDayOf(meal, StoreFixture.Zone);

        Assert.Single(await store.DayAsync(journalDay));
    }

    [Fact]
    public async Task QueryFiltersByDayRangeInclusively()
    {
        // Both bounds inclusive. Dropping the last day is the off-by-one that
        // makes a weekly total quietly short by a day.
        var store = _fixture.Store();
        await store.AppendAsync(Meal("2026-08-19T10:00:00+00:00"));
        await store.AppendAsync(Meal("2026-08-20T10:00:00+00:00"));
        await store.AppendAsync(Meal("2026-08-21T10:00:00+00:00"));
        await store.AppendAsync(Meal("2026-08-22T10:00:00+00:00"));

        var range = await store.QueryAsync(new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 21));

        Assert.Equal(2, range.Count);
    }

    [Fact]
    public async Task QueryWithOpenBoundsReturnsEverything()
    {
        var store = _fixture.Store();
        await store.AppendAsync(Meal("2026-08-19T10:00:00+00:00"));
        await store.AppendAsync(Meal("2026-08-22T10:00:00+00:00"));

        Assert.Equal(2, (await store.QueryAsync(null, null)).Count);
        Assert.Equal(2, (await store.QueryAsync(new DateOnly(2026, 1, 1), null)).Count);
        Assert.Single(await store.QueryAsync(null, new DateOnly(2026, 8, 19)));
    }

    [Fact]
    public async Task QueryReturnsNewestFirst()
    {
        // The web view lists recent meals; oldest-first would put the meal a
        // reader cares about at the bottom of a long page.
        var store = _fixture.Store();
        await store.AppendAsync(Meal("2026-08-20T08:00:00+00:00"));
        await store.AppendAsync(Meal("2026-08-20T18:00:00+00:00"));

        var meals = await store.QueryAsync(null, null);

        Assert.True(meals[0].Started > meals[1].Started);
    }

    [Fact]
    public async Task QueryRespectsTheLimit()
    {
        var store = _fixture.Store();
        for (var hour = 0; hour < 5; hour++)
        {
            await store.AppendAsync(Meal($"2026-08-20T0{hour}:00:00+00:00"));
        }

        Assert.Equal(2, (await store.QueryAsync(null, null, limit: 2)).Count);
    }

    [Fact]
    public async Task DayReturnsOldestFirst()
    {
        // The opposite order from QueryAsync, and deliberately so: a day view
        // reads as a timeline of what happened, in the order it happened.
        var store = _fixture.Store();
        await store.AppendAsync(Meal("2026-08-20T18:00:00+00:00"));
        await store.AppendAsync(Meal("2026-08-20T08:00:00+00:00"));

        var meals = await store.DayAsync(new DateOnly(2026, 8, 20));

        Assert.True(meals[0].Started < meals[1].Started);
    }

    [Fact]
    public async Task CountIsZeroOnAFreshDatabase()
    {
        Assert.Equal(0, await _fixture.Store().CountAsync());
    }
}
