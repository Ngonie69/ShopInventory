using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.SalesOrders.Queries.GetAllSalesOrders;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Mobile Orders filters, sorts and pages on the API rather than holding every order in the session.
/// </summary>
/// <remarks>
/// The page used to load the last 90 days and every open order, up to ten thousand of each, and hold
/// them for as long as the tab stayed open so it could filter, sort and page in memory. Each tab now
/// holds one page, so the column filters and sorts it applied are answered here instead, with the
/// same meaning: text anywhere in the value ignoring case, dates by calendar day, and a missing
/// delivery date sorting as the earliest.
/// </remarks>
public sealed class MobileOrderServerPagingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    public MobileOrderServerPagingTests()
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
    public async Task The_recent_window_keeps_every_open_order_however_old()
    {
        var old = Today.AddDays(-400);
        await GivenOrder("MOB-RECENT", SalesOrderStatus.Fulfilled, sapDocNum: 10, orderDate: Today.AddDays(-3));
        await GivenOrder("MOB-OLD-PENDING", SalesOrderStatus.Pending, orderDate: old);
        await GivenOrder("MOB-OLD-UNPOSTED", SalesOrderStatus.Approved, orderDate: old);
        await GivenOrder("MOB-OLD-POSTED", SalesOrderStatus.Approved, sapDocNum: 11, orderDate: old);
        await GivenOrder("MOB-OLD-CANCELLED", SalesOrderStatus.Cancelled, orderDate: old);

        var kept = await OrderNumbersAsync(Query() with { FromDate = Today.AddDays(-90), KeepOpenOrders = true });
        var windowOnly = await OrderNumbersAsync(Query() with { FromDate = Today.AddDays(-90) });

        Assert.Equal(["MOB-OLD-PENDING", "MOB-OLD-UNPOSTED", "MOB-RECENT"], kept.Order());
        Assert.Equal(["MOB-RECENT"], windowOnly);
    }

    [Fact]
    public async Task A_status_tab_returns_the_orders_that_read_as_that_status()
    {
        // An approved order that never reached SAP reads as Pending, so it belongs under Pending.
        await GivenOrder("MOB-PENDING", SalesOrderStatus.Pending);
        await GivenOrder("MOB-UNPOSTED", SalesOrderStatus.Approved);
        await GivenOrder("MOB-POSTED", SalesOrderStatus.Approved, sapDocNum: 4711);

        Assert.Equal(["MOB-PENDING", "MOB-UNPOSTED"], (await OrderNumbersAsync(Query(SalesOrderStatus.Pending))).Order());
        Assert.Equal(["MOB-POSTED"], await OrderNumbersAsync(Query(SalesOrderStatus.Approved)));
    }

    [Fact]
    public async Task Each_column_filter_matches_the_way_the_page_did()
    {
        await GivenOrder("MOB-1001", SalesOrderStatus.Pending, orderDate: Today.AddDays(-1), deliveryDate: Today.AddDays(2),
            currency: "USD", docTotal: 1234.50m);
        await GivenOrder("MOB-2002", SalesOrderStatus.Approved, sapDocNum: 774411, orderDate: Today, deliveryDate: null,
            currency: "ZIG", docTotal: 99m);

        Assert.Equal(["MOB-1001"], await OrderNumbersAsync(Query() with { Columns = new(OrderNumber: "mob-10") }));
        Assert.Equal(["MOB-1001"], await OrderNumbersAsync(Query() with { Columns = new(OrderDate: Today.AddDays(-1)) }));
        Assert.Equal(["MOB-1001"], await OrderNumbersAsync(Query() with { Columns = new(DeliveryDate: Today.AddDays(2)) }));
        Assert.Equal(["MOB-2002"], await OrderNumbersAsync(Query() with { Columns = new(Currency: " zig ") }));
        // The page showed "1,234.50" and matched either that or "1234.5".
        Assert.Equal(["MOB-1001"], await OrderNumbersAsync(Query() with { Columns = new(Total: "1,234.5") }));
        Assert.Equal(["MOB-2002"], await OrderNumbersAsync(Query() with { Columns = new(SapDocNum: "4411") }));
        Assert.Empty(await OrderNumbersAsync(Query() with { Columns = new(OrderNumber: "MOB-1001", Currency: "ZIG") }));
    }

    [Theory]
    [InlineData(SalesOrderListSort.Number, false, new[] { "A-1", "B-2", "C-3" })]
    [InlineData(SalesOrderListSort.Number, true, new[] { "C-3", "B-2", "A-1" })]
    [InlineData(SalesOrderListSort.Customer, false, new[] { "C-3", "A-1", "B-2" })]
    [InlineData(SalesOrderListSort.Total, true, new[] { "B-2", "C-3", "A-1" })]
    [InlineData(SalesOrderListSort.SapDoc, true, new[] { "C-3", "A-1", "B-2" })]
    [InlineData(SalesOrderListSort.Status, false, new[] { "B-2", "A-1", "C-3" })]
    [InlineData(SalesOrderListSort.Ordered, true, new[] { "A-1", "C-3", "B-2" })]
    // No delivery date sorts as the earliest: last when descending, first when ascending.
    [InlineData(SalesOrderListSort.Delivery, true, new[] { "C-3", "A-1", "B-2" })]
    [InlineData(SalesOrderListSort.Delivery, false, new[] { "B-2", "A-1", "C-3" })]
    public async Task Each_sort_orders_the_whole_list_before_it_is_paged(
        SalesOrderListSort sort, bool descending, string[] expected)
    {
        // A-1: posted (Approved), B-2: Pending, C-3: Fulfilled.
        await GivenOrder("A-1", SalesOrderStatus.Approved, sapDocNum: 500, cardName: "Mbare Stores",
            orderDate: Today, deliveryDate: Today.AddDays(1), docTotal: 10m);
        await GivenOrder("B-2", SalesOrderStatus.Pending, cardName: "Zesa Canteen",
            orderDate: Today.AddDays(-2), deliveryDate: null, docTotal: 300m);
        await GivenOrder("C-3", SalesOrderStatus.Fulfilled, sapDocNum: 900, cardName: "Avondale Spar",
            orderDate: Today.AddDays(-1), deliveryDate: Today.AddDays(5), docTotal: 20m);

        var firstTwo = await OrderNumbersAsync(Query() with { Sort = sort, SortDescending = descending, PageSize = 2 });
        var last = await OrderNumbersAsync(Query() with { Sort = sort, SortDescending = descending, PageSize = 2, Page = 2 });

        Assert.Equal(expected, firstTwo.Concat(last));
    }

    [Fact]
    public async Task Equal_keys_keep_one_order_across_pages()
    {
        for (var i = 1; i <= 5; i++)
            await GivenOrder($"MOB-{i}", SalesOrderStatus.Pending, docTotal: 50m);

        var pages = new List<string>();
        for (var page = 1; page <= 3; page++)
            pages.AddRange(await OrderNumbersAsync(Query() with { Sort = SalesOrderListSort.Total, PageSize = 2, Page = page }));

        Assert.Equal(["MOB-5", "MOB-4", "MOB-3", "MOB-2", "MOB-1"], pages);
    }

    [Fact]
    public async Task The_summary_names_every_currency_and_status_whatever_the_filters()
    {
        await GivenOrder("MOB-1", SalesOrderStatus.Pending, currency: "USD");
        await GivenOrder("MOB-2", SalesOrderStatus.Approved, currency: "usd");
        await GivenOrder("MOB-3", SalesOrderStatus.Approved, sapDocNum: 7, currency: "ZIG");
        await GivenOrder("MOB-4", SalesOrderStatus.Cancelled, currency: null);

        var result = await HandleAsync(Query() with
        {
            IncludeSummary = true,
            Columns = new(OrderNumber: "nothing-matches")
        });

        Assert.Empty(result.Value.Orders);
        var summary = result.Value.Summary!;
        Assert.Equal(["USD", "ZIG"], summary.Currencies.Select(c => c.ToUpperInvariant()));
        Assert.Equal([SalesOrderStatus.Pending, SalesOrderStatus.Approved, SalesOrderStatus.Cancelled], summary.Statuses);
    }

    [Fact]
    public async Task The_filters_and_sorts_translate_for_PostgreSQL()
    {
        // SQLite runs the tests above; Npgsql can still refuse a ToString, ToLower or CASE it accepts.
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=translation_only;Username=none;Password=none;Timeout=1")
                .Options);

        var allColumns = new SalesOrderColumnFilters("SO", Today, Today, "USD", "1,234", "77");
        foreach (var sort in Enum.GetValues<SalesOrderListSort>())
        {
            foreach (var descending in new[] { true, false })
            {
                var sql = GetAllSalesOrdersHandler
                    .ApplySort(GetAllSalesOrdersHandler.ApplyColumnFilters(context.SalesOrders, allColumns), sort, descending)
                    .ToQueryString();
                Assert.Contains("ORDER BY", sql);
            }
        }
    }

    private static GetAllSalesOrdersQuery Query(SalesOrderStatus? status = null) => new(
        Page: 1,
        PageSize: 50,
        Status: status,
        CardCode: null,
        FromDate: null,
        ToDate: null,
        Source: SalesOrderSource.Mobile);

    private async Task<List<string>> OrderNumbersAsync(GetAllSalesOrdersQuery query)
    {
        var result = await HandleAsync(query);
        Assert.False(result.IsError);
        return result.Value.Orders.Select(order => order.OrderNumber).ToList();
    }

    private async Task<ErrorOr.ErrorOr<ShopInventory.DTOs.SalesOrderListResponseDto>> HandleAsync(GetAllSalesOrdersQuery query)
    {
        var handler = new GetAllSalesOrdersHandler(
            _context,
            MobileSalesOrderListTests.UnreachableSapClient.Create(),
            NullLogger<GetAllSalesOrdersHandler>.Instance);

        return await handler.Handle(query, CancellationToken.None);
    }

    private async Task GivenOrder(
        string orderNumber,
        SalesOrderStatus status,
        int? sapDocNum = null,
        DateTime? orderDate = null,
        DateTime? deliveryDate = null,
        string? cardName = "Test customer",
        string? currency = "USD",
        decimal docTotal = 0m)
    {
        // SaveChanges refuses an Approved order with no SAP number, so one exists only in rows from before
        // that rule. It is written the way such a row still can be: straight to the table.
        var legacyUnposted = status == SalesOrderStatus.Approved && sapDocNum is null;

        _context.SalesOrders.Add(new SalesOrderEntity
        {
            OrderNumber = orderNumber,
            CardCode = "TMP119",
            CardName = cardName,
            OrderDate = orderDate ?? Today,
            DeliveryDate = deliveryDate,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Source = SalesOrderSource.Mobile,
            Status = legacyUnposted ? SalesOrderStatus.Pending : status,
            SAPDocNum = sapDocNum,
            IsSynced = sapDocNum is not null,
            Currency = currency,
            DocTotal = docTotal,
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

    /// <summary>Lets the fixture supply <see cref="SalesOrderEntity.RowVersion"/>, which Npgsql maps to xmin.</summary>
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
