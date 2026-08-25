using Microsoft.EntityFrameworkCore;

namespace EdgeForge.Storage;

/// <summary>
/// The SQLite mirror of <c>meals.jsonl</c>.
/// </summary>
/// <remarks>
/// One table. The journal remains the durable append-only record and the source
/// of truth; this database exists only because a flat file cannot answer "meals
/// between these two dates" without reading all of it. Deleting the file and
/// restarting rebuilds it from the journal, which is the property that keeps
/// adding a database from also adding something irreplaceable.
/// </remarks>
public sealed class MealDbContext(DbContextOptions<MealDbContext> options) : DbContext(options)
{
    public DbSet<MealRecord> Meals => Set<MealRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var meal = modelBuilder.Entity<MealRecord>();

        meal.HasKey(m => m.Id);

        // Every range query filters on the day.
        meal.HasIndex(m => m.Day);

        // The dedupe key, and the reason the journal backfill can run on every
        // start without a marker file: two meals cannot begin at the same instant,
        // so re-importing a line that is already stored violates this index and is
        // swallowed as "already there" rather than duplicating history.
        //
        // On StartedTicks rather than StartedUtc: SQLite compares the latter as
        // TEXT, so the same instant written with two different offsets would be
        // two distinct keys and the backfill would duplicate it.
        meal.HasIndex(m => m.StartedTicks).IsUnique();

        // Round-trip format, so the file stays greppable and sorts lexically in
        // the same order it sorts chronologically. EF's SQLite provider stores
        // DateTimeOffset as TEXT either way; being explicit documents that the
        // offset survives, which is what the round-trip test asserts.
        // TEXT keeps the file greppable and preserves the original offset, which
        // is what the round-trip test asserts. Ordering and uniqueness go through
        // StartedTicks instead - see MealRecord for why.
        meal.Property(m => m.StartedUtc).HasColumnType("TEXT");
        meal.Property(m => m.EndedUtc).HasColumnType("TEXT");

        base.OnModelCreating(modelBuilder);
    }
}
