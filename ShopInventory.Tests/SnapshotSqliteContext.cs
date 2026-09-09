using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// An <see cref="ApplicationDbContext"/> that can hold daily stock snapshot rows on SQLite.
/// </summary>
/// <remarks>
/// <see cref="DailyStockSnapshotItemEntity.Version"/> is <c>[Timestamp]</c>, which Npgsql maps to
/// the store-generated <c>xmin</c> system column. SQLite has no equivalent, so EF leaves the column
/// out of the INSERT and its NOT NULL constraint fails. Making it an ordinary property lets a
/// fixture supply one.
///
/// <para>
/// Worth knowing rather than working around silently: the same mapping is what gives the stock
/// ledger its optimistic concurrency on PostgreSQL, and it is exactly what SQLite cannot reproduce.
/// So <c>StockLedger</c>'s retry-on-conflict path is not reachable from this suite — the conflict
/// never happens here — and it is covered by the database, not by these tests.
/// </para>
/// </remarks>
public sealed class SnapshotSqliteContext(DbContextOptions<ApplicationDbContext> options)
    : ApplicationDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DailyStockSnapshotItemEntity>()
            .Property(item => item.Version)
            .ValueGeneratedNever()
            .IsConcurrencyToken(false);
    }
}
