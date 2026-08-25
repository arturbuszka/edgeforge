namespace EdgeForge.Storage;

/// <summary>
/// One completed meal, in the shape the database stores it.
/// </summary>
/// <remarks>
/// Separate from <c>MealEvent</c> for the same reason <c>MealJournal.MealLine</c>
/// is: the domain record must stay free to change without invalidating months of
/// stored history.
///
/// <see cref="Day"/> and <see cref="DurationSeconds"/> are denormalised because
/// they are what every query filters and sums on. Recomputing them per row turns
/// "meals last month" into a table scan with arithmetic, and - worse for
/// <see cref="Day"/> - would need the local time zone inside a SQL translation,
/// which is where a UTC/local mix-up would hide.
/// </remarks>
public sealed class MealRecord
{
    public long Id { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset EndedUtc { get; set; }

    /// <summary>
    /// <see cref="StartedUtc"/> as UTC ticks, for ordering and for the uniqueness
    /// constraint.
    /// </summary>
    /// <remarks>
    /// Not redundancy for its own sake. SQLite has no date type and stores a
    /// DateTimeOffset as TEXT, and EF refuses to translate an ORDER BY over one -
    /// "SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY
    /// clauses". Ordering on the TEXT column instead would compare "+02:00" and
    /// "+00:00" strings lexically, which puts a 09:00+02:00 meal after an
    /// 08:00+00:00 one that actually happened later.
    ///
    /// An INTEGER of UTC ticks sorts natively, sorts by instant rather than by
    /// how the instant was written down, and gives the unique index something
    /// SQLite can compare cheaply.
    /// </remarks>
    public long StartedTicks { get; set; }

    public double DurationSeconds { get; set; }

    public int PeakCount { get; set; }

    public long ObservedFrames { get; set; }

    /// <summary>
    /// The local calendar day the meal started on - what "meals today" means.
    /// Computed by <c>MealJournal.LocalDayOf</c> so the journal and this table
    /// can never disagree about which day a meal belongs to.
    /// </summary>
    public DateOnly Day { get; set; }
}
