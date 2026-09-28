using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.SalesOrders.Queries.GetAllSalesOrders;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The source-filtered sales order list — what the mobile app calls — must be answered from the
/// local tables alone.
/// </summary>
/// <remarks>
/// It used to run a repair sweep first: up to 100 sequential
/// <c>GetSalesOrderByOrderNumberAsync</c> calls, each an unindexed scan of ORDR on the
/// U_OrderNumber UDF, before returning data that was entirely local anyway. Worse, the candidates
/// were by definition the orders that had not resolved, so the steady state was the same hundred
/// scans on every page load, forever. Relinking is owned by SalesOrderReconciliationJob; these
/// tests exist so nothing puts SAP back on this path.
/// </remarks>
public sealed class MobileSalesOrderListTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public MobileSalesOrderListTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Source_filtered_list_makes_no_sap_calls()
    {
        await GivenOrder("MOB-1", SalesOrderStatus.Pending, sapDocNum: null, isSynced: false);
        await GivenOrder("MOB-2", SalesOrderStatus.Approved, sapDocNum: 4711, isSynced: true);

        var result = await HandleAsync(Query(status: null));

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.TotalCount);
    }

    [Fact]
    public async Task Filtering_to_approved_makes_no_sap_calls_either()
    {
        // Status=Approved on a mobile source was the exact combination that armed the old sweep.
        await GivenOrder("MOB-1", SalesOrderStatus.Pending, sapDocNum: null, isSynced: false);
        await GivenOrder("MOB-2", SalesOrderStatus.Approved, sapDocNum: 4711, isSynced: true);

        var result = await HandleAsync(Query(status: SalesOrderStatus.Approved));

        Assert.False(result.IsError);
        var order = Assert.Single(result.Value.Orders);
        Assert.Equal("MOB-2", order.OrderNumber);
    }

    [Fact]
    public async Task Unlinked_mobile_orders_are_returned_rather_than_withheld_pending_a_lookup()
    {
        // The rows still have to come back with whatever local metadata they carry; dropping the
        // sweep must not turn an unlinked order into an invisible one. Relinking it is
        // SalesOrderReconciliationJob's job, and happens whether or not anyone opens this list.
        await GivenOrder("MOB-1", SalesOrderStatus.Pending, sapDocNum: null, isSynced: false);

        var result = await HandleAsync(Query(status: null));

        Assert.False(result.IsError);
        var order = Assert.Single(result.Value.Orders);
        Assert.Equal("MOB-1", order.OrderNumber);
        Assert.Null(order.SAPDocNum);
    }

    [Fact]
    public async Task Open_only_returns_every_order_still_waiting_on_someone_however_old()
    {
        // The Mobile Orders page loads the last 90 days plus these, so an order waiting on review is
        // never hidden by its age. An approved order that has not reached SAP counts: the list shows
        // it as Pending.
        var old = DateTime.UtcNow.Date.AddDays(-400);
        await GivenOrder("MOB-DRAFT", SalesOrderStatus.Draft, sapDocNum: null, isSynced: false, orderDate: old);
        await GivenOrder("MOB-PENDING", SalesOrderStatus.Pending, sapDocNum: null, isSynced: false, orderDate: old);
        await GivenOrder("MOB-HELD", SalesOrderStatus.OnHold, sapDocNum: null, isSynced: false, orderDate: old);
        await GivenOrder("MOB-UNPOSTED", SalesOrderStatus.Approved, sapDocNum: null, isSynced: false, orderDate: old);
        await GivenOrder("MOB-POSTED", SalesOrderStatus.Approved, sapDocNum: 4711, isSynced: true, orderDate: old);
        await GivenOrder("MOB-CANCELLED", SalesOrderStatus.Cancelled, sapDocNum: null, isSynced: false, orderDate: old);
        await GivenOrder("MOB-INVOICED", SalesOrderStatus.Fulfilled, sapDocNum: 4712, isSynced: true);

        var result = await HandleAsync(Query(status: null) with { OpenOnly = true });

        Assert.False(result.IsError);
        Assert.Equal(
            ["MOB-DRAFT", "MOB-HELD", "MOB-PENDING", "MOB-UNPOSTED"],
            result.Value.Orders.Select(order => order.OrderNumber).Order().ToArray());
    }

    [Fact]
    public async Task The_summary_counts_all_time_by_the_status_the_list_shows()
    {
        var now = DateTime.UtcNow;
        await GivenOrder("MOB-1", SalesOrderStatus.Draft, sapDocNum: null, isSynced: false);
        await GivenOrder("MOB-2", SalesOrderStatus.Pending, sapDocNum: null, isSynced: false, createdAt: now.AddDays(-5));
        await GivenOrder("MOB-3", SalesOrderStatus.Approved, sapDocNum: null, isSynced: false, createdAt: now.AddDays(-2));
        await GivenOrder("MOB-4", SalesOrderStatus.Approved, sapDocNum: 4711, isSynced: true);
        await GivenOrder("MOB-5", SalesOrderStatus.Cancelled, sapDocNum: null, isSynced: false);

        // The date filter narrows the orders returned, not the counts.
        var result = await HandleAsync(Query(status: null) with
        {
            FromDate = now.Date.AddDays(1),
            IncludeSummary = true
        });

        Assert.False(result.IsError);
        Assert.Empty(result.Value.Orders);
        var summary = result.Value.Summary!;
        Assert.Equal(5, summary.Total);
        Assert.Equal(1, summary.Draft);
        Assert.Equal(2, summary.Pending);
        Assert.Equal(1, summary.Approved);
        Assert.Equal(now.AddDays(-5), summary.OldestPendingCreatedAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task The_summary_is_left_out_unless_asked_for()
    {
        await GivenOrder("MOB-1", SalesOrderStatus.Pending, sapDocNum: null, isSynced: false);

        var result = await HandleAsync(Query(status: null));

        Assert.Null(result.Value.Summary);
    }

    [Fact]
    public async Task The_new_queries_translate_for_PostgreSQL()
    {
        // This class runs on SQLite. A conditional count or minimum it accepts can still be refused by
        // Npgsql, so both new queries are compiled against the real provider. Nothing listens on port 1:
        // a query that translates gets as far as the driver trying to connect.
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=translation_only;Username=none;Password=none;Timeout=1")
                .Options);

        Assert.Contains("GROUP BY", GetAllSalesOrdersHandler.SummaryQuery(context.SalesOrders).ToQueryString());

        var handler = new GetAllSalesOrdersHandler(
            context, UnreachableSapClient.Create(), NullLogger<GetAllSalesOrdersHandler>.Instance);
        var thrown = await Record.ExceptionAsync(() =>
            handler.Handle(Query(status: null) with { OpenOnly = true }, CancellationToken.None));

        Assert.True(
            thrown is Npgsql.NpgsqlException || thrown?.InnerException is Npgsql.NpgsqlException,
            "The open-only list never reached the driver:" + Environment.NewLine + thrown);
    }

    private static GetAllSalesOrdersQuery Query(SalesOrderStatus? status) => new(
        Page: 1,
        PageSize: 50,
        Status: status,
        CardCode: null,
        FromDate: null,
        ToDate: null,
        Source: SalesOrderSource.Mobile);

    private async Task<ErrorOr.ErrorOr<ShopInventory.DTOs.SalesOrderListResponseDto>> HandleAsync(
        GetAllSalesOrdersQuery query)
    {
        var handler = new GetAllSalesOrdersHandler(
            _context,
            UnreachableSapClient.Create(),
            NullLogger<GetAllSalesOrdersHandler>.Instance);

        return await handler.Handle(query, CancellationToken.None);
    }

    private async Task GivenOrder(
        string orderNumber,
        SalesOrderStatus status,
        int? sapDocNum,
        bool isSynced,
        DateTime? orderDate = null,
        DateTime? createdAt = null)
    {
        // SaveChanges now refuses an Approved order with no SAP number, so one exists only in rows from
        // before that rule. It is written the way such a row still can be: straight to the table.
        var legacyUnposted = status == SalesOrderStatus.Approved && sapDocNum is null;

        _context.SalesOrders.Add(new SalesOrderEntity
        {
            OrderNumber = orderNumber,
            CardCode = "TMP119",
            CardName = "Test customer",
            OrderDate = orderDate ?? DateTime.UtcNow.Date,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Source = SalesOrderSource.Mobile,
            Status = legacyUnposted ? SalesOrderStatus.Pending : status,
            SAPDocNum = sapDocNum,
            IsSynced = isSynced,
            RowVersion = BitConverter.GetBytes(1L)
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        if (legacyUnposted)
        {
            await _context.SalesOrders
                .Where(order => order.OrderNumber == orderNumber)
                .ExecuteUpdateAsync(set => set.SetProperty(order => order.Status, SalesOrderStatus.Approved));
        }
    }

    /// <summary>
    /// <see cref="SalesOrderEntity.RowVersion"/> is <c>[Timestamp]</c>, which Npgsql maps to the
    /// store-generated <c>xmin</c> system column. SQLite has no equivalent, so EF leaves the column
    /// out of the INSERT and the NOT NULL constraint fails. Making it an ordinary property lets the
    /// fixture supply one; nothing under test reads it except the DTO projection.
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

    /// <summary>
    /// An <see cref="ISAPServiceLayerClient"/> that fails the test on any call. The interface has
    /// well over a hundred members, so it is generated rather than hand-stubbed.
    /// </summary>
    public class UnreachableSapClient : DispatchProxy
    {
        public static ISAPServiceLayerClient Create() =>
            Create<ISAPServiceLayerClient, UnreachableSapClient>()!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"The local sales order list must not call SAP, but it called {targetMethod?.Name}.");
    }
}
