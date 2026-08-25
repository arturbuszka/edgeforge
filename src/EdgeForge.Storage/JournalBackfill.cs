using EdgeForge.Decision;

namespace EdgeForge.Storage;

/// <summary>
/// Copies <c>meals.jsonl</c> into the database.
/// </summary>
/// <remarks>
/// Idempotent, via the unique index on <c>StartedUtc</c>, so running it on every
/// start is safe and needs no marker file to remember that it already ran. That
/// matters more than the small cost: it means a lost or corrupted database heals
/// itself on the next start rather than needing a tool nobody remembers exists.
///
/// The journal stays the durable record; this database is a view over the same
/// events, never a replacement for them.
/// </remarks>
public static class JournalBackfill
{
    /// <summary>Imports every meal the journal holds. Returns rows actually added.</summary>
    public static async Task<int> RunAsync(
        MealJournal journal,
        IMealStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(store);

        var before = await store.CountAsync(cancellationToken);

        foreach (var meal in journal.ReadAll())
        {
            cancellationToken.ThrowIfCancellationRequested();

            await store.AppendAsync(meal, cancellationToken);
        }

        var after = await store.CountAsync(cancellationToken);

        return after - before;
    }
}
