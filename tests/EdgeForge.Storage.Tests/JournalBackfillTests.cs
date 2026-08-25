using EdgeForge.Decision;
using Xunit;

namespace EdgeForge.Storage.Tests;

/// <summary>
/// Importing <c>meals.jsonl</c> into the database.
/// </summary>
/// <remarks>
/// Uses a real journal file rather than a stub, because the behaviour under test
/// is the pairing of two real parsers: what the journal chooses to skip and what
/// the store chooses to deduplicate.
/// </remarks>
public class JournalBackfillTests : IDisposable
{
    private readonly StoreFixture _fixture = new();
    private readonly string _directory;

    public JournalBackfillTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "edgeforge-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _fixture.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file must not fail an otherwise passing test.
        }

        GC.SuppressFinalize(this);
    }

    private MealJournal JournalWith(params MealEvent[] meals)
    {
        var journal = new MealJournal(Path.Combine(_directory, "meals.jsonl"));
        foreach (var meal in meals) journal.Append(meal);
        return journal;
    }

    private static MealEvent Meal(string startedUtc) =>
        new(
            DateTimeOffset.Parse(startedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(startedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind)
                .AddMinutes(3),
            1,
            420);

    [Fact]
    public async Task ImportsEveryLine()
    {
        var journal = JournalWith(
            Meal("2026-08-19T10:00:00+00:00"),
            Meal("2026-08-20T10:00:00+00:00"),
            Meal("2026-08-21T10:00:00+00:00"));

        var store = _fixture.Store();

        Assert.Equal(3, await JournalBackfill.RunAsync(journal, store));
        Assert.Equal(3, await store.CountAsync());
    }

    [Fact]
    public async Task RunningTwiceIsANoOp()
    {
        // The whole reason this runs on every start without a marker file.
        var journal = JournalWith(
            Meal("2026-08-19T10:00:00+00:00"),
            Meal("2026-08-20T10:00:00+00:00"));

        var store = _fixture.Store();

        Assert.Equal(2, await JournalBackfill.RunAsync(journal, store));
        Assert.Equal(0, await JournalBackfill.RunAsync(journal, store));
        Assert.Equal(2, await store.CountAsync());
    }

    [Fact]
    public async Task SkipsAnUnparseableLine()
    {
        // A power cut truncates the last line. Losing that meal is acceptable;
        // refusing to import the other two is not.
        var journal = JournalWith(
            Meal("2026-08-19T10:00:00+00:00"),
            Meal("2026-08-20T10:00:00+00:00"));

        File.AppendAllText(journal.Path, "{\"Started\":\"2026-08-21T10:00:00" + Environment.NewLine);

        var store = _fixture.Store();

        Assert.Equal(2, await JournalBackfill.RunAsync(journal, store));
    }

    [Fact]
    public async Task AMissingJournalIsNotAnError()
    {
        // First run on a new machine: there is nothing to import yet.
        var journal = new MealJournal(Path.Combine(_directory, "absent.jsonl"));

        Assert.Equal(0, await JournalBackfill.RunAsync(journal, _fixture.Store()));
    }

    [Fact]
    public async Task ImportsOnTopOfMealsAlreadyStored()
    {
        // The restart case: today's meals reached the database live, then the
        // backfill re-reads the same journal on the next start.
        var store = _fixture.Store();
        var alreadyStored = Meal("2026-08-20T10:00:00+00:00");
        await store.AppendAsync(alreadyStored);

        var journal = JournalWith(alreadyStored, Meal("2026-08-20T14:00:00+00:00"));

        Assert.Equal(1, await JournalBackfill.RunAsync(journal, store));
        Assert.Equal(2, await store.CountAsync());
    }

    [Fact]
    public async Task ImportedMealsSurviveTheJournalRoundTrip()
    {
        // The journal rounds duration to one decimal on write. What matters is
        // that the timestamps - which the unique index and every query depend on
        // - come back as the same instants.
        var meal = Meal("2026-08-20T10:00:00+00:00");
        var journal = JournalWith(meal);
        var store = _fixture.Store();

        await JournalBackfill.RunAsync(journal, store);
        var stored = Assert.Single(await store.QueryAsync(null, null));

        Assert.Equal(meal.Started, stored.Started);
        Assert.Equal(meal.Ended, stored.Ended);
        Assert.Equal(meal.ObservedFrames, stored.ObservedFrames);
    }
}
