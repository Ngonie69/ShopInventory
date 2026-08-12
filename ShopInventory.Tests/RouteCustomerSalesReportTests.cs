using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerProductMix;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSales;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSalesSummary;
using ShopInventory.Features.VanSalesCompatibility.Commands.IngestVanSalesOfflineSales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A van whose customers are captured on the road invoices its own SAP business partner, because the
/// individual shops are not business partners. That makes <c>CardCode</c> the same on every sale the van
/// makes, and until the route customer was recorded alongside it there was nothing to report per shop.
///
/// These cover the two halves of that: the write paths recording who bought, and the reports reading it
/// back — including the two answers that are easy to get wrong, which are a customer who has never bought
/// and a van that traded in two currencies.
/// </summary>
public sealed class RouteCustomerSalesReportTests : IDisposable
{
    private const string RouteCode = "VAN010";
    private static readonly Guid VanUser = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private int _tuckShopId;
    private int _cornerStoreId;

    public RouteCustomerSalesReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        // Role and assigned business partner together are what put a van on the route-customer scope.
        _context.Users.Add(new User
        {
            Id = VanUser,
            Username = "van010",
            Email = "van010@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = RouteCode,
            AssignedCostCentreCode = "CC010",
            AssignedBusinessPartnerCode = RouteCode
        });

        var tuckShop = new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = RouteCode,
            Code = "TUCK01",
            Name = "Tuck Shop",
            IsActive = true,
            CreatedAt = new DateTime(2026, 5, 26, 12, 51, 0, DateTimeKind.Utc)
        };

        var cornerStore = new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = RouteCode,
            Code = "CORNER1",
            Name = "Corner Store",
            IsActive = true,
            CreatedAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc)
        };

        _context.RouteCustomers.AddRange(tuckShop, cornerStore);
        _context.SaveChanges();

        _tuckShopId = tuckShop.Id;
        _cornerStoreId = cornerStore.Id;
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- The write path ---

    /// <summary>
    /// The whole feature rests on this. The handset names the shop, the invoice still posts against the
    /// van's business partner, and both survive on the row.
    /// </summary>
    [Fact]
    public async Task An_uploaded_sale_records_the_shop_and_still_posts_against_the_van()
    {
        await IngestAsync(BuildUpload("VAN010-INV-1", "TUCK01"));

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(RouteCode, sale.CardCode);
        Assert.Equal(_tuckShopId, sale.RouteCustomerId);
        Assert.Equal("TUCK01", sale.RouteCustomerCode);
        Assert.Equal("Tuck Shop", sale.RouteCustomerName);
    }

    /// <summary>
    /// The route customer's own code used to be rejected outright — only the posting account was
    /// accepted — which stranded the takings of any handset that reported the shop it actually sold to.
    /// </summary>
    [Fact]
    public async Task A_sale_naming_the_route_customer_is_accepted_rather_than_rejected()
    {
        var response = await IngestAsync(BuildUpload("VAN010-INV-1", "TUCK01"));

        Assert.Equal(1, response.Accepted);
        Assert.Equal(0, response.Rejected);
    }

    /// <summary>
    /// An older handset reports the posting account instead, and the name it carries belongs to no shop
    /// on the route — so neither route in can answer. The money is still real and must be held, but
    /// nothing may be invented about which shop it came from.
    /// </summary>
    [Fact]
    public async Task A_sale_naming_the_posting_account_is_accepted_but_left_unattributed()
    {
        var upload = BuildUpload("VAN010-INV-1", RouteCode);
        upload.CustomerName = "Somebody";

        var response = await IngestAsync(upload);

        Assert.Equal(1, response.Accepted);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Null(sale.RouteCustomerId);
        Assert.Null(sale.RouteCustomerCode);

        // Not "Somebody". A name with no code reads to the report as a customer that was deleted, and no
        // customer was ever identified here.
        Assert.Null(sale.RouteCustomerName);
        Assert.Equal("Somebody", sale.CardName);
    }

    // --- The name fallback, for handsets that still send the posting account ---

    /// <summary>
    /// A handset on an older build reports the van's business partner in customer_code but still carries
    /// the shop's name. Without this the whole route's history arrives as one undivided figure.
    /// </summary>
    [Fact]
    public async Task A_sale_naming_the_posting_account_is_attributed_by_customer_name()
    {
        var upload = BuildUpload("VAN010-INV-1", RouteCode);
        upload.CustomerName = "Tuck Shop";

        await IngestAsync(upload);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(_tuckShopId, sale.RouteCustomerId);
        Assert.Equal("TUCK01", sale.RouteCustomerCode);

        // The posting account is untouched — the fallback decides who bought, never who is billed.
        Assert.Equal(RouteCode, sale.CardCode);
    }

    /// <summary>
    /// Two shops on one route answering to the same name. Choosing between them would be a coin toss
    /// recorded as fact, and a sale credited to the wrong shop is worse than one credited to none —
    /// nobody goes looking for a figure that is already there.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_name_is_left_unattributed_rather_than_guessed()
    {
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = RouteCode,
            Code = "TUCK02",
            Name = "Tuck Shop",
            IsActive = true
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var upload = BuildUpload("VAN010-INV-1", RouteCode);
        upload.CustomerName = "Tuck Shop";

        var response = await IngestAsync(upload);

        Assert.Equal(1, response.Accepted);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Null(sale.RouteCustomerId);
        Assert.Null(sale.RouteCustomerCode);
    }

    /// <summary>
    /// The code wins when it names a shop. The name is a fallback for when the code cannot answer, never
    /// a second opinion that could overrule it.
    /// </summary>
    [Fact]
    public async Task The_code_decides_even_when_the_name_points_elsewhere()
    {
        var upload = BuildUpload("VAN010-INV-1", "TUCK01");
        upload.CustomerName = "Corner Store";

        await IngestAsync(upload);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(_tuckShopId, sale.RouteCustomerId);
    }

    /// <summary>
    /// The fallback never reaches outside the van's own route. A shop on another route is not a match
    /// even when the name is exact — that is the same rule the code path enforces.
    /// </summary>
    [Fact]
    public async Task A_name_matching_a_shop_on_another_route_is_not_used()
    {
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = "VAN011",
            Code = "OTHER1",
            Name = "Someone Elses Shop",
            IsActive = true
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var upload = BuildUpload("VAN010-INV-1", RouteCode);
        upload.CustomerName = "Someone Elses Shop";

        await IngestAsync(upload);

        Assert.Null((await _context.DesktopSales.SingleAsync()).RouteCustomerId);
    }

    /// <summary>A van still may not sell to a shop on somebody else's route.</summary>
    [Fact]
    public async Task A_sale_naming_a_shop_on_another_route_is_rejected()
    {
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = "VAN011",
            Code = "OTHER1",
            Name = "Someone Else's Shop",
            IsActive = true
        });
        await _context.SaveChangesAsync();

        var response = await IngestAsync(BuildUpload("VAN010-INV-1", "OTHER1"));

        Assert.Equal(1, response.Rejected);
        Assert.Contains("not assigned", response.Results.Single().Message);
    }

    // --- The drill-down ---

    [Fact]
    public async Task The_drill_down_returns_only_that_customers_sales()
    {
        await IngestAsync(
            BuildUpload("VAN010-INV-1", "TUCK01"),
            BuildUpload("VAN010-INV-2", "TUCK01", receiptGlobalNo: 502),
            BuildUpload("VAN010-INV-3", "CORNER1", receiptGlobalNo: 503));

        var detail = await DrillDownAsync(_tuckShopId);

        Assert.Equal(2, detail.SaleCount);
        Assert.All(detail.Sales, sale => Assert.StartsWith("VAN010-INV-", sale.Reference));
        Assert.DoesNotContain(detail.Sales, sale => sale.Reference == "VAN010-INV-3");
    }

    /// <summary>
    /// A van can take USD and ZWG on the same day. One combined figure over both is not a rounding
    /// error, it is a number that does not exist, so the totals stay split by currency.
    /// </summary>
    [Fact]
    public async Task Totals_are_reported_per_currency_and_never_added_together()
    {
        var inUsd = BuildUpload("VAN010-INV-1", "TUCK01");
        inUsd.Currency = "USD";
        inUsd.Total = 40m;

        var inZwg = BuildUpload("VAN010-INV-2", "TUCK01", receiptGlobalNo: 502);
        inZwg.Currency = "ZWG";
        inZwg.Total = 900m;

        await IngestAsync(inUsd, inZwg);

        var detail = await DrillDownAsync(_tuckShopId);

        Assert.Equal(2, detail.TotalsByCurrency.Count);
        Assert.Equal(900m, detail.TotalsByCurrency.Single(total => total.Currency == "ZWG").Gross);
        Assert.Equal(40m, detail.TotalsByCurrency.Single(total => total.Currency == "USD").Gross);
    }

    [Fact]
    public async Task The_drill_down_summarises_what_the_customer_buys()
    {
        await IngestAsync(
            BuildUpload("VAN010-INV-1", "TUCK01"),
            BuildUpload("VAN010-INV-2", "TUCK01", receiptGlobalNo: 502));

        var detail = await DrillDownAsync(_tuckShopId);

        var item = Assert.Single(detail.ProductMix);
        Assert.Equal("CHE011", item.ItemCode);
        Assert.Equal(4m, item.Quantity);
        Assert.Equal(2, item.LineCount);
    }

    /// <summary>Sales outside the window are not this window's takings.</summary>
    [Fact]
    public async Task The_drill_down_honours_the_date_window()
    {
        var older = BuildUpload("VAN010-INV-1", "TUCK01");
        older.SoldAt = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Unspecified);

        var newer = BuildUpload("VAN010-INV-2", "TUCK01", receiptGlobalNo: 502);
        newer.SoldAt = new DateTime(2026, 3, 20, 10, 0, 0, DateTimeKind.Unspecified);

        await IngestAsync(older, newer);

        var detail = await DrillDownAsync(
            _tuckShopId,
            from: new DateTime(2026, 3, 1),
            to: new DateTime(2026, 3, 31));

        Assert.Equal(1, detail.SaleCount);
        Assert.Equal("VAN010-INV-2", detail.Sales.Single().Reference);
    }

    // --- The route summary and dormancy ---

    /// <summary>
    /// A customer who has never bought has to appear, or the report can only ever describe the shops that
    /// are already working. They are the ones worth a visit.
    /// </summary>
    [Fact]
    public async Task A_customer_who_has_never_bought_is_reported_rather_than_omitted()
    {
        await IngestAsync(BuildUpload("VAN010-INV-1", "TUCK01"));

        var summary = await SummaryAsync();
        var route = Assert.Single(summary.Routes);

        Assert.Equal(2, route.CustomerCount);
        Assert.Equal(1, route.NeverBoughtCount);

        var cornerStore = route.Customers.Single(customer => customer.Code == "CORNER1");
        Assert.Equal(0, cornerStore.SaleCount);

        // Null, not a large number. Never bought and lapsed are different findings and need different
        // conversations.
        Assert.Null(cornerStore.DaysSinceLastSale);
    }

    [Fact]
    public async Task A_customer_whose_last_sale_is_older_than_the_threshold_is_dormant()
    {
        var stale = BuildUpload("VAN010-INV-1", "TUCK01");
        stale.SoldAt = DateTime.UtcNow.Date.AddDays(-60);

        var fresh = BuildUpload("VAN010-INV-2", "CORNER1", receiptGlobalNo: 502);
        fresh.SoldAt = DateTime.UtcNow.Date.AddDays(-2);

        await IngestAsync(stale, fresh);

        var summary = await SummaryAsync(dormantDays: 30);
        var route = Assert.Single(summary.Routes);

        Assert.Equal(1, route.DormantCount);
        Assert.True(route.Customers.Single(customer => customer.Code == "TUCK01").DaysSinceLastSale > 30);
        Assert.True(route.Customers.Single(customer => customer.Code == "CORNER1").DaysSinceLastSale < 30);
    }

    /// <summary>
    /// A deleted route customer nulls the link on its sales but leaves the snapshots. Those takings
    /// happened and still belong to the route, so they get a row of their own rather than disappearing
    /// out of the total.
    /// </summary>
    [Fact]
    public async Task Sales_whose_customer_was_deleted_still_appear_in_the_route_total()
    {
        await IngestAsync(BuildUpload("VAN010-INV-1", "TUCK01"));

        _context.RouteCustomers.Remove(await _context.RouteCustomers.SingleAsync(c => c.Id == _tuckShopId));
        await _context.SaveChangesAsync();

        var summary = await SummaryAsync();
        var route = Assert.Single(summary.Routes);

        var orphan = route.Customers.Single(customer => customer.IsDeleted);
        Assert.Equal("TUCK01", orphan.Code);
        Assert.Equal("Tuck Shop", orphan.Name);
        Assert.Equal(1, orphan.SaleCount);
        Assert.Equal(100m, route.TotalsByCurrency.Single().Gross);
    }

    [Fact]
    public async Task The_route_summary_counts_lines_and_sales_per_customer()
    {
        await IngestAsync(
            BuildUpload("VAN010-INV-1", "TUCK01"),
            BuildUpload("VAN010-INV-2", "TUCK01", receiptGlobalNo: 502));

        var summary = await SummaryAsync();
        var tuckShop = summary.Routes.Single().Customers.Single(customer => customer.Code == "TUCK01");

        Assert.Equal(2, tuckShop.SaleCount);
        Assert.Equal(2, tuckShop.LineCount);
        Assert.Equal(200m, tuckShop.TotalsByCurrency.Single().Gross);
    }

    // --- The online sale, which lives in a different table ---

    /// <summary>
    /// A van that had signal posts straight to SAP and leaves no DesktopSale behind — the confirmed
    /// stock reservation is its only local record. A drill-down that read only the offline table would
    /// under-report the customer without ever looking wrong.
    /// </summary>
    [Fact]
    public async Task An_online_sale_appears_alongside_the_uploaded_ones()
    {
        await IngestAsync(BuildUpload("VAN010-INV-1", "TUCK01"));
        AddOnlineSale("VAN010-ONL-1", _tuckShopId, 250m, ReservationStatus.Confirmed);

        var detail = await DrillDownAsync(_tuckShopId);

        Assert.Equal(2, detail.SaleCount);
        Assert.Contains(detail.Sales, sale => sale.Source == RouteCustomerSaleSource.OnlineInvoice);
        Assert.Equal(350m, detail.TotalsByCurrency.Single().Gross);
    }

    /// <summary>
    /// A pending reservation is stock held for a sale that has not happened, and a cancelled one is a
    /// sale that never did. Counting either would invent takings the van never had.
    /// </summary>
    [Theory]
    [InlineData(ReservationStatus.Pending)]
    [InlineData(ReservationStatus.Cancelled)]
    [InlineData(ReservationStatus.Expired)]
    public async Task A_reservation_that_never_posted_is_not_counted_as_a_sale(string status)
    {
        AddOnlineSale("VAN010-ONL-1", _tuckShopId, 250m, status);

        var detail = await DrillDownAsync(_tuckShopId);

        Assert.Equal(0, detail.SaleCount);
        Assert.Empty(detail.TotalsByCurrency);
    }

    [Fact]
    public async Task The_route_summary_includes_online_sales()
    {
        AddOnlineSale("VAN010-ONL-1", _tuckShopId, 250m, ReservationStatus.Confirmed);

        var summary = await SummaryAsync();
        var tuckShop = summary.Routes.Single().Customers.Single(customer => customer.Code == "TUCK01");

        Assert.Equal(1, tuckShop.SaleCount);
        Assert.Equal(1, tuckShop.LineCount);
        Assert.Equal(250m, tuckShop.TotalsByCurrency.Single().Gross);
    }

    // --- Product mix across a route ---

    [Fact]
    public async Task The_product_mix_counts_how_many_customers_bought_each_item()
    {
        await IngestAsync(
            BuildUpload("VAN010-INV-1", "TUCK01"),
            BuildUpload("VAN010-INV-2", "CORNER1", receiptGlobalNo: 502));

        var result = await new GetRouteCustomerProductMixHandler(_context).Handle(
            new GetRouteCustomerProductMixQuery(RouteCode, null, null, null, 0),
            CancellationToken.None);

        Assert.False(result.IsError);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal("CHE011", item.ItemCode);
        Assert.Equal(2, item.CustomerCount);
        Assert.Equal(4m, item.Quantity);
    }

    // --- Fixtures ---

    private async Task<VanSalesOfflineSaleBatchResponse> IngestAsync(params VanSalesOfflineSaleRequest[] sales)
    {
        var result = await new IngestVanSalesOfflineSalesHandler(
                _context, new NoOpAuditService(), NullLogger<IngestVanSalesOfflineSalesHandler>.Instance)
            .Handle(
                new IngestVanSalesOfflineSalesCommand(
                    new VanSalesOfflineSaleBatchRequest { Sales = [.. sales] }, VanUser),
                CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        _context.ChangeTracker.Clear();
        return result.Value;
    }

    private async Task<RouteCustomerSalesDetailDto> DrillDownAsync(int routeCustomerId, DateTime? from = null, DateTime? to = null)
    {
        var result = await new GetRouteCustomerSalesHandler(_context).Handle(
            new GetRouteCustomerSalesQuery(routeCustomerId, from, to),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task<RouteCustomerSalesSummaryDto> SummaryAsync(int? dormantDays = null)
    {
        var result = await new GetRouteCustomerSalesSummaryHandler(_context).Handle(
            new GetRouteCustomerSalesSummaryQuery(RouteCode, null, null, dormantDays, true),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    /// <summary>
    /// A van sale made online: it posts to SAP immediately and the confirmed reservation is all that is
    /// left locally. CardCode is the van's business partner here too — the shop is only in the route
    /// customer columns.
    /// </summary>
    private void AddOnlineSale(string reference, int routeCustomerId, decimal total, string status)
    {
        var customer = _context.RouteCustomers.AsNoTracking().Single(entity => entity.Id == routeCustomerId);

        _context.StockReservations.Add(new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = reference,
            SourceSystem = "KefalosVanSales",
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = RouteCode,
            RouteCustomerId = customer.Id,
            RouteCustomerCode = customer.Code,
            RouteCustomerName = customer.Name,
            Currency = "USD",
            TotalValue = total,
            Status = status,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            ExpiresAt = DateTime.UtcNow.AddDays(-1).AddHours(1),
            ConfirmedAt = status == ReservationStatus.Confirmed ? DateTime.UtcNow.AddDays(-1) : null,
            SAPDocEntry = status == ReservationStatus.Confirmed ? 9001 : null,
            SAPDocNum = status == ReservationStatus.Confirmed ? 40128 : null,
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheddar 1kg",
                    ReservedQuantity = 5m,
                    OriginalQuantity = 5m,
                    WarehouseCode = RouteCode,
                    UnitPrice = total / 5m,
                    LineTotal = total
                }
            ]
        });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private static VanSalesOfflineSaleRequest BuildUpload(
        string reference,
        string customerCode,
        int receiptGlobalNo = 501) => new()
    {
        VanOrder = reference,
        CustomerCode = customerCode,
        CustomerName = null,
        SoldAt = DateTime.UtcNow.Date.AddDays(-1),
        Currency = "USD",
        Total = 100m,
        VatAmount = 13.04m,
        AmountPaid = 100m,
        PaymentMethod = "Cash",
        FiscalDeviceId = "35410",
        FiscalDayNo = 19,
        ReceiptGlobalNo = receiptGlobalNo,
        ReceiptCounter = 4,
        ReceiptDate = DateTime.UtcNow.Date.AddDays(-1).AddHours(11),
        FiscalDayOpenedAt = DateTime.UtcNow.Date.AddDays(-1).AddHours(6),
        DeviceSignatureHash = "aGFzaC1vZi10aGUtcGF5bG9hZA==",
        DeviceSignatureValue = "c2lnbmF0dXJlLW92ZXItdGhlLXBheWxvYWQ=",
        Items =
        [
            new VanSalesOfflineSaleItemRequest
            {
                Code = "CHE011",
                Description = "Cheddar 1kg",
                Quantity = 2m,
                Price = 50m,
                TaxCode = "O8",
                TaxId = 517,
                TaxPercent = 15m
            }
        ]
    };

    /// <summary>
    /// These tests are about what the report reads back, not about the audit trail the ingest writes —
    /// <see cref="VanSalesOfflineIngestTests"/> owns that.
    /// </summary>
    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) => Task.CompletedTask;
    }
}
