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
        bool isSynced)
    {
        _context.SalesOrders.Add(new SalesOrderEntity
        {
            OrderNumber = orderNumber,
            CardCode = "TMP119",
            CardName = "Test customer",
            OrderDate = DateTime.UtcNow.Date,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Source = SalesOrderSource.Mobile,
            Status = status,
            SAPDocNum = sapDocNum,
            IsSynced = isSynced,
            RowVersion = BitConverter.GetBytes(1L)
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
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
