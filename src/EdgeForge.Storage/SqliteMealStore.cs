using EdgeForge.Decision;
using Microsoft.EntityFrameworkCore;

namespace EdgeForge.Storage;

/// <summary>
/// <see cref="IMealStore"/> over SQLite.
/// </summary>
/// <remarks>
/// Takes a context factory rather than a context. The pipeline writes from a
/// background service while HTTP handlers read, and a single DbContext is not
/// thread-safe: sharing one would surface as sporadic "A second operation was
/// started on this context" exceptions under exactly the load the live view
/// creates.
/// </remarks>
public sealed class SqliteMealStore(IDbContextFactory<MealDbContext> factory) : IMealStore
{
    private readonly IDbContextFactory<MealDbContext> _factory =
        factory ?? throw new ArgumentNullException(nameof(factory));

    private readonly TimeZoneInfo _zone = TimeZoneInfo.Local;

    /// <param name="factory">Creates a short-lived context per operation.</param>
    /// <param name="zone">
    /// Zone used to derive <see cref="MealRecord.Day"/>. Defaults to local; tests
    /// pass a fixed zone so "which day is this" is not a property of the machine.
    /// </param>
    public SqliteMealStore(IDbContextFactory<MealDbContext> factory, TimeZoneInfo zone)
        : this(factory)
    {
        ArgumentNullException.ThrowIfNull(zone);
        _zone = zone;
    }

    public async Task AppendAsync(MealEvent meal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meal);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        db.Meals.Add(new MealRecord
        {
            StartedUtc = meal.Started,
            StartedTicks = meal.Started.UtcTicks,
            EndedUtc = meal.Ended,
            DurationSeconds = meal.Duration.TotalSeconds,
            PeakCount = meal.PeakCount,
            ObservedFrames = meal.ObservedFrames,

            // Deliberately the journal's own rule rather than a local
            // reimplementation. If these two ever diverge, the OBS overlay and
            // the web HUD report different counts for the same day.
            Day = MealJournal.LocalDayOf(meal, _zone)
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Probably the unique index on StartedTicks: the backfill runs on every
            // start and a restart re-reads today from the journal, so re-offering
            // a stored meal is the expected path rather than an error.
            //
            // Re-checked rather than assumed, and rethrown when the row is not
            // actually there, so a full disk or a schema mismatch still surfaces
            // instead of being silently counted as "already stored". The check
            // cannot live in a `when` filter - await is not allowed in one.
            if (!await ExistsAsync(meal.Started, cancellationToken)) throw;
        }
    }

    public async Task<IReadOnlyList<MealEvent>> QueryAsync(
        DateOnly? from,
        DateOnly? to,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var query = db.Meals.AsNoTracking().AsQueryable();

        // Inclusive on both ends: a caller asking for "2026-08-01 to 2026-08-07"
        // means seven days, and silently dropping the last one is the off-by-one
        // that makes a weekly total quietly wrong.
        if (from is { } start) query = query.Where(m => m.Day >= start);
        if (to is { } end) query = query.Where(m => m.Day <= end);

        var rows = await query
            .OrderByDescending(m => m.StartedTicks)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return Map(rows);
    }

    public async Task<IReadOnlyList<MealEvent>> DayAsync(
        DateOnly day,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var rows = await db.Meals.AsNoTracking()
            .Where(m => m.Day == day)
            .OrderBy(m => m.StartedTicks)
            .ToListAsync(cancellationToken);

        return Map(rows);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        return await db.Meals.CountAsync(cancellationToken);
    }

    private async Task<bool> ExistsAsync(DateTimeOffset started, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var ticks = started.UtcTicks;

        return await db.Meals.AsNoTracking().AnyAsync(m => m.StartedTicks == ticks, cancellationToken);
    }

    private static IReadOnlyList<MealEvent> Map(List<MealRecord> rows)
    {
        var meals = new MealEvent[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            meals[i] = new MealEvent(row.StartedUtc, row.EndedUtc, row.PeakCount, row.ObservedFrames);
        }

        return meals;
    }
}
