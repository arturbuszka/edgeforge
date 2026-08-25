using EdgeForge.Decision;

namespace EdgeForge.Storage;

/// <summary>
/// Queryable meal history. The interface exists so the API layer depends on the
/// questions it asks rather than on EF Core.
/// </summary>
/// <remarks>
/// Everything here speaks <c>MealEvent</c>, the domain record, not
/// <see cref="MealRecord"/>. That keeps the mapping at the boundary and means
/// <c>StatsSnapshot.MealSummary.From</c> works on query results unchanged.
/// </remarks>
public interface IMealStore
{
    /// <summary>
    /// Stores one completed meal. Storing the same meal twice is a no-op, not an
    /// error - see the unique index in <see cref="MealDbContext"/>.
    /// </summary>
    Task AppendAsync(MealEvent meal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Meals whose local start day falls in the inclusive range, newest first.
    /// Null bounds are open ended.
    /// </summary>
    Task<IReadOnlyList<MealEvent>> QueryAsync(
        DateOnly? from,
        DateOnly? to,
        int limit = 1000,
        CancellationToken cancellationToken = default);

    /// <summary>Meals that started on one local day, oldest first.</summary>
    Task<IReadOnlyList<MealEvent>> DayAsync(DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>Rows currently stored. For the backfill's before/after check.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
