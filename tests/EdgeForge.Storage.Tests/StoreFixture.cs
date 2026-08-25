using EdgeForge.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EdgeForge.Storage.Tests;

/// <summary>
/// A throwaway database per test, plus the context factory the store expects.
/// </summary>
/// <remarks>
/// Backed by an in-memory SQLite connection rather than a temp file: the schema
/// and the SQL are the real ones - which is the whole point, since the bugs being
/// guarded against are SQL-level - while nothing survives the test to leak into
/// the next one.
///
/// The open connection is load-bearing. An in-memory SQLite database exists only
/// as long as a connection to it is held; letting EF open and close per operation
/// would drop the schema between calls and surface as "no such table: Meals".
/// </remarks>
public sealed class StoreFixture : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    public StoreFixture()
    {
        _keepAlive = new SqliteConnection("Data Source=:memory:");
        _keepAlive.Open();

        Factory = new Factories(_keepAlive);

        using var db = Factory.CreateDbContext();
        db.Database.Migrate();
    }

    public IDbContextFactory<MealDbContext> Factory { get; }

    /// <summary>
    /// A fixed non-UTC zone, so "which local day is this" is a property of the
    /// test rather than of the machine running it. +02:00 with no DST keeps the
    /// arithmetic readable while still being an offset that can move a late
    /// evening UTC timestamp into the next day.
    /// </summary>
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.CreateCustomTimeZone(
        "EdgeForge.Test", TimeSpan.FromHours(2), "EdgeForge Test +02:00", "EdgeForge Test +02:00");

    public SqliteMealStore Store() => new(Factory, Zone);

    public void Dispose() => _keepAlive.Dispose();

    private sealed class Factories(SqliteConnection connection) : IDbContextFactory<MealDbContext>
    {
        public MealDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<MealDbContext>().UseSqlite(connection).Options);
    }
}
