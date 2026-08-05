using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.SalesOrders.Commands.BackfillWebOrderTax;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how the web sales order tax backfill treats each group of affected orders.
/// </summary>
/// <remarks>
/// Web orders were persisted with zero-rate lines because the create form sent no tax rate and the
/// API substituted the configured rate for mobile orders only. The repair cannot be one rule: SAP
/// prices tax from each item's own tax code, so a posted order's real tax is knowable only from its
/// SAP document, while an order SAP has never seen has nothing but configuration to go on. These
/// tests exist so a later simplification cannot collapse the two into a blanket recompute, which
/// would quietly invent a standard rate for anything zero-rated or exempt.
/// </remarks>
public sealed class WebOrderTaxBackfillTests : IDisposable
{
    private const decimal ConfiguredVatRate = 0.155m;
    private const decimal ConfiguredVatPercent = 15.5m;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public WebOrderTaxBackfillTests()
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
    public async Task An_unposted_web_order_is_recomputed_at_the_configured_rate()
    {
        await GivenOrder("SO-WEB-1", SalesOrderSource.Web, SalesOrderStatus.Draft, lineTotal: 6.26m);

        var result = await BackfillAsync();

        Assert.False(result.IsError);
        Assert.Equal(1, result.Value.UnpostedOrdersUpdated);
        Assert.Equal(1, result.Value.UnpostedLinesUpdated);
        Assert.Equal(ConfiguredVatPercent, result.Value.ConfiguredTaxPercent);

        var order = await ReloadAsync("SO-WEB-1");
        Assert.Equal(ConfiguredVatPercent, order.Lines.Single().TaxPercent);
        Assert.Equal(0.97m, order.TaxAmount);
        Assert.Equal(7.23m, order.DocTotal);
    }

    [Fact]
    public async Task A_dry_run_reports_the_population_without_writing()
    {
        await GivenOrder("SO-WEB-1", SalesOrderSource.Web, SalesOrderStatus.Draft, lineTotal: 6.26m);

        var result = await BackfillAsync(dryRun: true);

        Assert.False(result.IsError);
        Assert.Equal(1, result.Value.OrdersAffected);
        Assert.Equal(1, result.Value.UnpostedOrdersUpdated);
        Assert.True(result.Value.DryRun);

        var order = await ReloadAsync("SO-WEB-1");
        Assert.Equal(0m, order.Lines.Single().TaxPercent);
        Assert.Equal(0m, order.TaxAmount);
        Assert.Equal(6.26m, order.DocTotal);
    }

    /// <summary>
    /// The whole point of the split. A posted order's tax is whatever SAP charged, which for a
    /// zero-rated line is nothing — recomputing at 15.5% would overstate a document that is
    /// already correct in the only system that bills from it.
    /// </summary>
    [Fact]
    public async Task A_posted_web_order_takes_its_numbers_from_sap_not_from_configuration()
    {
        await GivenOrder(
            "SO-WEB-POSTED",
            SalesOrderSource.Web,
            SalesOrderStatus.Approved,
            lineTotal: 100m,
            sapDocEntry: 4711,
            sapDocNum: 4711,
            isSynced: true);

        // SAP charged 5% on this document, not the configured 15.5%.
        var result = await BackfillAsync(sapOrder: new SAPSalesOrder
        {
            DocEntry = 4711,
            DocNum = 4711,
            DocTotal = 105m,
            VatSum = 5m
        });

        Assert.False(result.IsError);
        Assert.Equal(0, result.Value.UnpostedOrdersUpdated);
        Assert.Equal(1, result.Value.PostedOrdersRepaired);
        Assert.Equal(1, result.Value.PostedLinesRepaired);

        var order = await ReloadAsync("SO-WEB-POSTED");
        Assert.Equal(5m, order.TaxAmount);
        Assert.Equal(105m, order.DocTotal);
        Assert.Equal(5m, order.Lines.Single().TaxPercent);
    }

    /// <summary>
    /// A document SAP itself prices at zero tax is not a broken row, so it must keep its zero
    /// rather than be handed the configured rate as a consolation.
    /// </summary>
    [Fact]
    public async Task A_posted_order_that_sap_prices_at_zero_tax_keeps_zero()
    {
        await GivenOrder(
            "SO-WEB-EXEMPT",
            SalesOrderSource.Web,
            SalesOrderStatus.Approved,
            lineTotal: 100m,
            sapDocEntry: 4712,
            sapDocNum: 4712,
            isSynced: true);

        var result = await BackfillAsync(sapOrder: new SAPSalesOrder
        {
            DocEntry = 4712,
            DocNum = 4712,
            DocTotal = 100m,
            VatSum = 0m
        });

        Assert.False(result.IsError);
        Assert.Equal(0, result.Value.PostedLinesRepaired);

        var order = await ReloadAsync("SO-WEB-EXEMPT");
        Assert.Equal(0m, order.TaxAmount);
        Assert.Equal(0m, order.Lines.Single().TaxPercent);
    }

    [Fact]
    public async Task A_posted_order_sap_cannot_be_read_for_is_left_alone_and_counted()
    {
        await GivenOrder(
            "SO-WEB-UNREADABLE",
            SalesOrderSource.Web,
            SalesOrderStatus.Approved,
            lineTotal: 100m,
            sapDocEntry: 4713,
            sapDocNum: 4713,
            isSynced: true);

        var result = await BackfillAsync(sapOrder: null);

        Assert.False(result.IsError);
        Assert.Equal(1, result.Value.PostedOrdersUnresolved);
        Assert.Equal(0, result.Value.PostedOrdersRepaired);

        var order = await ReloadAsync("SO-WEB-UNREADABLE");
        Assert.Equal(0m, order.TaxAmount);
        Assert.Equal(0m, order.Lines.Single().TaxPercent);
    }

    [Fact]
    public async Task Mobile_orders_are_not_this_backfills_business()
    {
        await GivenOrder("MOB-1", SalesOrderSource.Mobile, SalesOrderStatus.Draft, lineTotal: 6.26m);

        var result = await BackfillAsync();

        Assert.False(result.IsError);
        Assert.Equal(0, result.Value.OrdersAffected);
        Assert.Equal(0m, (await ReloadAsync("MOB-1")).Lines.Single().TaxPercent);
    }

    [Theory]
    [InlineData(SalesOrderStatus.Cancelled)]
    [InlineData(SalesOrderStatus.Rejected)]
    public async Task Closed_orders_keep_the_totals_they_were_recorded_with(SalesOrderStatus status)
    {
        await GivenOrder("SO-WEB-CLOSED", SalesOrderSource.Web, status, lineTotal: 6.26m);

        var result = await BackfillAsync();

        Assert.False(result.IsError);
        Assert.Equal(0, result.Value.OrdersAffected);
        Assert.Equal(6.26m, (await ReloadAsync("SO-WEB-CLOSED")).DocTotal);
    }

    [Fact]
    public async Task A_line_that_already_carries_a_rate_is_not_overwritten()
    {
        await GivenOrder(
            "SO-WEB-MIXED",
            SalesOrderSource.Web,
            SalesOrderStatus.Draft,
            lineTotal: 100m,
            extraLineTotal: 50m,
            extraLineTaxPercent: 5m);

        var result = await BackfillAsync();

        Assert.False(result.IsError);
        Assert.Equal(1, result.Value.UnpostedLinesUpdated);

        var order = await ReloadAsync("SO-WEB-MIXED");
        Assert.Equal(ConfiguredVatPercent, order.Lines.Single(line => line.LineTotal == 100m).TaxPercent);
        Assert.Equal(5m, order.Lines.Single(line => line.LineTotal == 50m).TaxPercent);
        Assert.Equal(18m, order.TaxAmount);
    }

    [Fact]
    public async Task The_posted_cap_leaves_the_rest_for_a_later_run()
    {
        await GivenOrder("SO-P1", SalesOrderSource.Web, SalesOrderStatus.Approved, 100m, sapDocEntry: 1, sapDocNum: 1, isSynced: true);
        await GivenOrder("SO-P2", SalesOrderSource.Web, SalesOrderStatus.Approved, 100m, sapDocEntry: 2, sapDocNum: 2, isSynced: true);

        var result = await BackfillAsync(
            maxPostedOrders: 1,
            sapOrder: new SAPSalesOrder { DocEntry = 1, DocNum = 1, DocTotal = 115.5m, VatSum = 15.5m });

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.PostedOrdersFound);
        Assert.Equal(1, result.Value.PostedOrdersQueried);
        Assert.Equal(1, result.Value.PostedOrdersRemaining);
    }

    private async Task<ErrorOr.ErrorOr<BackfillWebOrderTaxResult>> BackfillAsync(
        bool dryRun = false,
        int maxPostedOrders = 200,
        SAPSalesOrder? sapOrder = null)
    {
        var handler = new BackfillWebOrderTaxHandler(
            _context,
            CreateSalesOrderService(sapOrder),
            Options.Create(new RevmaxSettings { VatRate = ConfiguredVatRate }),
            NullLogger<BackfillWebOrderTaxHandler>.Instance);

        return await handler.Handle(
            new BackfillWebOrderTaxCommand(dryRun, maxPostedOrders),
            CancellationToken.None);
    }

    /// <summary>
    /// A real <see cref="SalesOrderService"/> over the same context, so the posted-order path under
    /// test is the production one rather than a restatement of it. Only the document read is stubbed.
    /// </summary>
    private SalesOrderService CreateSalesOrderService(SAPSalesOrder? sapOrder)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
        {
            // The repair reads every document it needs in one batch, so the stub answers the set
            // read rather than the single one. A null sapOrder stands for a DocEntry SAP does not
            // hold, which the batch expresses by that key simply being absent.
            if (method.Name == nameof(ISAPServiceLayerClient.GetSalesOrderFinancialsByDocEntriesAsync))
            {
                IReadOnlyDictionary<int, SAPSalesOrder> resolved = sapOrder is null
                    ? new Dictionary<int, SAPSalesOrder>()
                    : new Dictionary<int, SAPSalesOrder> { [sapOrder.DocEntry] = sapOrder };
                return Task.FromResult(resolved);
            }

            if (method.Name == nameof(ISAPServiceLayerClient.GetSalesOrderByDocEntryAsync))
            {
                return Task.FromResult(sapOrder);
            }

            throw new InvalidOperationException($"Unexpected call to {method.Name}");
        });

        return new SalesOrderService(
            _context,
            sap,
            NullLogger<SalesOrderService>.Instance,
            StubProxy.Unused<INotificationService>(),
            StubProxy.Unused<IBusinessPartnerService>(),
            StubProxy.Unused<ILocalPriceCatalogService>(),
            StubProxy.Unused<ShopInventory.Common.Idempotency.IIdempotencyRequestStore>(),
            StubProxy.Unused<ICreditLimitService>(),
            Options.Create(new RevmaxSettings { VatRate = ConfiguredVatRate }));
    }

    private async Task GivenOrder(
        string orderNumber,
        SalesOrderSource source,
        SalesOrderStatus status,
        decimal lineTotal,
        int? sapDocEntry = null,
        int? sapDocNum = null,
        bool isSynced = false,
        decimal? extraLineTotal = null,
        decimal extraLineTaxPercent = 0m)
    {
        var order = new SalesOrderEntity
        {
            OrderNumber = orderNumber,
            CardCode = "TMP113",
            CardName = "Test Customer",
            Status = status,
            Source = source,
            OrderDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            SAPDocEntry = sapDocEntry,
            SAPDocNum = sapDocNum,
            IsSynced = isSynced,
            SubTotal = lineTotal + (extraLineTotal ?? 0m),
            TaxAmount = 0m,
            DiscountAmount = 0m,
            DocTotal = lineTotal + (extraLineTotal ?? 0m),
            RowVersion = BitConverter.GetBytes(1L),
            Lines =
            [
                new SalesOrderLineEntity
                {
                    LineNum = 0,
                    ItemCode = "ITEM-1",
                    Quantity = 1,
                    UnitPrice = lineTotal,
                    LineTotal = lineTotal,
                    TaxPercent = 0m
                }
            ]
        };

        if (extraLineTotal.HasValue)
        {
            order.Lines.Add(new SalesOrderLineEntity
            {
                LineNum = 1,
                ItemCode = "ITEM-2",
                Quantity = 1,
                UnitPrice = extraLineTotal.Value,
                LineTotal = extraLineTotal.Value,
                TaxPercent = extraLineTaxPercent
            });
        }

        _context.SalesOrders.Add(order);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<SalesOrderEntity> ReloadAsync(string orderNumber)
    {
        _context.ChangeTracker.Clear();

        return await _context.SalesOrders
            .AsNoTracking()
            .Include(order => order.Lines)
            .SingleAsync(order => order.OrderNumber == orderNumber);
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
