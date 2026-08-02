using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins which orders the SAP reconciliation sweep will probe.
/// </summary>
/// <remarks>
/// The sweep once matched bare <see cref="SalesOrderStatus.Pending"/>, which made every unapproved
/// mobile order a candidate. Those can never resolve — nothing was posted — so the sweep ran 86
/// consecutive times on 2026-08-02 resolving 0 of 22-25 while the genuine limbo cases waited behind
/// them at the batch cap. The distinction the filter has to keep is "was SAP ever asked", not
/// "what status is it in".
/// </remarks>
public class SalesOrderReconciliationCandidateTests
{
    private static readonly DateTime Cutoff = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static bool IsCandidate(SalesOrderEntity order) =>
        SalesOrderService.UnlinkedSapOrderCandidateFilter(Cutoff).Compile()(order);

    private static SalesOrderEntity Order(
        SalesOrderStatus status,
        int? sapDocNum = null,
        string? syncError = null,
        string orderNumber = "SO-20260802-0001",
        DateTime? createdAt = null) =>
        new()
        {
            Status = status,
            SAPDocNum = sapDocNum,
            SyncError = syncError,
            OrderNumber = orderNumber,
            CardCode = "TMP113",
            CreatedAt = createdAt ?? Cutoff.AddHours(1),
            RowVersion = BitConverter.GetBytes(1L)
        };

    [Fact]
    public void Approved_without_a_doc_num_is_a_candidate()
    {
        Assert.True(IsCandidate(Order(SalesOrderStatus.Approved)));
    }

    [Theory]
    [InlineData(SalesOrderStatus.Pending)]
    [InlineData(SalesOrderStatus.Draft)]
    public void A_rolled_back_posting_attempt_is_a_candidate(SalesOrderStatus status)
    {
        Assert.True(IsCandidate(Order(status, syncError: "SAP posting failed")));
    }

    [Theory]
    [InlineData(SalesOrderStatus.Pending)]
    [InlineData(SalesOrderStatus.Draft)]
    public void An_order_that_was_never_posted_is_not_a_candidate(SalesOrderStatus status)
    {
        // The regression: a mobile order sitting in the approval queue. No SyncError means no
        // posting was ever attempted, so SAP cannot be holding a document for it.
        Assert.False(IsCandidate(Order(status, syncError: null)));
    }

    [Theory]
    [InlineData(SalesOrderStatus.Cancelled)]
    [InlineData(SalesOrderStatus.Rejected)]
    [InlineData(SalesOrderStatus.OnHold)]
    [InlineData(SalesOrderStatus.Fulfilled)]
    [InlineData(SalesOrderStatus.PartiallyFulfilled)]
    public void Statuses_outside_the_posting_path_are_never_candidates(SalesOrderStatus status)
    {
        Assert.False(IsCandidate(Order(status, syncError: "SAP posting failed")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_doc_num_still_counts_as_unlinked(int sapDocNum)
    {
        Assert.True(IsCandidate(Order(SalesOrderStatus.Approved, sapDocNum: sapDocNum)));
    }

    [Fact]
    public void An_order_already_linked_to_SAP_is_not_a_candidate()
    {
        Assert.False(IsCandidate(Order(SalesOrderStatus.Approved, sapDocNum: 79958)));
    }

    [Fact]
    public void Orders_older_than_the_cutoff_are_not_candidates()
    {
        Assert.False(IsCandidate(Order(SalesOrderStatus.Approved, createdAt: Cutoff.AddSeconds(-1))));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_order_without_an_order_number_cannot_be_probed(string? orderNumber)
    {
        // U_OrderNumber is the only key the sweep has; without one there is nothing to match on.
        Assert.False(IsCandidate(Order(SalesOrderStatus.Approved, orderNumber: orderNumber!)));
    }

    /// <summary>
    /// The filter runs inside an EF query, so it has to reach the database rather than being
    /// evaluated client-side over every sales order ever created.
    /// </summary>
    /// <remarks>
    /// The Approved-with-no-DocNum row goes in through raw SQL because
    /// <c>ApplicationDbContext.EnsureApprovedSalesOrdersHaveSapDocNum</c> refuses to save that state
    /// — which is exactly why a live stuck order shows up as Pending/Draft carrying a SyncError
    /// instead. The Approved clause survives in the filter only for rows written before that guard
    /// existed, so this covers it the only way such a row can now be produced.
    /// </remarks>
    [Fact]
    public async Task The_filter_translates_to_SQL_and_selects_the_same_rows()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();

        context.SalesOrders.AddRange(
            Order(SalesOrderStatus.Pending, orderNumber: "SO-LEGACY-LIMBO"),
            Order(SalesOrderStatus.Pending, syncError: "SAP posting failed", orderNumber: "SO-FAILED"),
            Order(SalesOrderStatus.Pending, orderNumber: "SO-AWAITING-APPROVAL"),
            Order(SalesOrderStatus.Approved, sapDocNum: 79958, orderNumber: "SO-LINKED"));
        await context.SaveChangesAsync();

        await context.Database.ExecuteSqlRawAsync(
            $"UPDATE \"SalesOrders\" SET \"Status\" = {(int)SalesOrderStatus.Approved} WHERE \"OrderNumber\" = 'SO-LEGACY-LIMBO'");

        var matched = await context.SalesOrders
            .AsNoTracking()
            .Where(SalesOrderService.UnlinkedSapOrderCandidateFilter(Cutoff))
            .Select(o => o.OrderNumber)
            .ToListAsync();

        Assert.Equal(["SO-FAILED", "SO-LEGACY-LIMBO"], matched.Order());
    }

    /// <summary>
    /// <see cref="SalesOrderEntity.RowVersion"/> is <c>[Timestamp]</c>, which Npgsql maps to the
    /// store-generated <c>xmin</c> system column. SQLite has no equivalent, so EF leaves the column
    /// out of the INSERT and the NOT NULL constraint fails.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SalesOrderEntity>()
                .Property(order => order.RowVersion)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }
}
