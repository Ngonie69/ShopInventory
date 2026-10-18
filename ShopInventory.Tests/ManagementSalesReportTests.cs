using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the management sales report: that every figure is set against the same days of the period
/// before, that a depot, a vendor and an item that stopped trading still appear, that margin is SAP's
/// booked profit on these sales' own invoices and nothing else in the warehouse, and that posting and
/// fiscalisation are counted the way the posting job sees them.
/// </summary>
/// <remarks>
/// SQLite rather than an in-memory provider, as the till analysis is tested, so a grouping that does not
/// translate fails here rather than being evaluated row by row in production.
/// </remarks>
public sealed class ManagementSalesReportTests : IDisposable
{
    // A three-day period and the three days before it.
    private static readonly DateTime From = new(2026, 9, 4);
    private static readonly DateTime To = new(2026, 9, 6);
    private static readonly DateTime PreviousDay = new(2026, 9, 2);

    private const string Depot = "DEPOT1";
    private const string DepotWarehouse = "KEFGRC";
    private const string ShopWarehouse = "KEFSHOP";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly FakeCostReader _costs = new();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _cashierId = Guid.NewGuid();
    private readonly int _vendorA;
    private readonly int _vendorB;
    private int _reference;

    public ManagementSalesReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _context.Users.AddRange(
            new User { Id = _adminId, Username = "admin", PasswordHash = "x", Role = ApplicationRoles.Admin, IsActive = true },
            new User { Id = _cashierId, Username = "depot1-cashier", FirstName = "Rudo", LastName = "Moyo", PasswordHash = "x", Role = ApplicationRoles.CartVendor, IsActive = true });
        _context.Shops.Add(new ShopEntity { Code = "FARM", Name = "Farm", BusinessPartnerCode = "FARM-BP", WarehouseCode = ShopWarehouse, IsActive = true });

        var vendorA = new RouteCustomerEntity { AssignedBusinessPartnerCode = Depot, Code = "TENDAI", Name = "Tendai", Surname = "Moyo", IsActive = true, CreatedAt = DateTime.UtcNow };
        var vendorB = new RouteCustomerEntity { AssignedBusinessPartnerCode = Depot, Code = "CHIPO", Name = "Chipo", IsActive = true, CreatedAt = DateTime.UtcNow };
        _context.RouteCustomers.AddRange(vendorA, vendorB);
        _context.SaveChanges();

        _vendorA = vendorA.Id;
        _vendorB = vendorB.Id;
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- The comparison ---------------------------------------------------------------------------

    [Fact]
    public async Task The_period_is_set_against_the_same_number_of_days_just_before_it()
    {
        Add(Vend(100m, From));
        Add(Vend(50m, To));
        Add(Vend(60m, PreviousDay));

        var report = await RunAsync();
        var usd = Dollars(report);

        Assert.Equal(new DateTime(2026, 9, 1), report.PreviousFromDate);
        Assert.Equal(new DateTime(2026, 9, 3), report.PreviousToDate);
        Assert.Equal(150m, usd.Summary.TotalAmount);
        Assert.Equal(60m, usd.Summary.PreviousTotalAmount);
        Assert.Equal(150m, usd.Summary.TotalChangePercent);

        // Every day of the period, zeros included, beside the day in the same position before it.
        Assert.Equal(3, usd.ByDay.Count);
        var middle = usd.ByDay[1];
        Assert.Equal(new DateTime(2026, 9, 5), middle.Date);
        Assert.Equal(0m, middle.TotalAmount);
        Assert.Equal(PreviousDay, middle.ComparedDate);
        Assert.Equal(60m, middle.ComparedTotalAmount);
    }

    [Fact]
    public async Task A_change_from_nothing_is_not_a_percentage()
    {
        Add(Vend(100m, From));

        var usd = Dollars(await RunAsync());

        Assert.Null(usd.Summary.TotalChangePercent);
    }

    [Fact]
    public async Task Currencies_are_reported_apart_and_never_added()
    {
        Add(Vend(100m, From));
        Add(Vend(4000m, From, currency: "ZWG"));

        var report = await RunAsync();

        Assert.Equal(["USD", "ZWG"], report.Currencies.Select(c => c.Currency));
        Assert.Equal(100m, report.Currencies[0].Summary.TotalAmount);
        Assert.Equal(4000m, report.Currencies[1].Summary.TotalAmount);
    }

    // ---- The breakdowns ---------------------------------------------------------------------------

    [Fact]
    public async Task A_shop_is_named_and_a_vending_depot_is_its_warehouse_under_its_partner()
    {
        Add(Vend(100m, From));
        Add(Sale(40m, From, SaleSourceSystems.ShopTill, ShopWarehouse, "FARM-BP"));

        var depots = Dollars(await RunAsync()).ByDepot.ToDictionary(row => row.Key);

        Assert.Equal("Farm", depots[ShopWarehouse].Label);
        Assert.Equal($"FARM · {ShopWarehouse}", depots[ShopWarehouse].Hint);
        Assert.Equal(DepotWarehouse, depots[DepotWarehouse].Label);
        Assert.Equal(Depot, depots[DepotWarehouse].Hint);
    }

    [Fact]
    public async Task A_depot_that_stopped_trading_still_has_its_row()
    {
        Add(Vend(100m, From));
        Add(Sale(40m, PreviousDay, SaleSourceSystems.ShopTill, ShopWarehouse, "FARM-BP"));

        var farm = Dollars(await RunAsync()).ByDepot.Single(row => row.Key == ShopWarehouse);

        Assert.Equal(0m, farm.TotalAmount);
        Assert.Equal(40m, farm.PreviousTotalAmount);
        Assert.Equal(-100m, farm.ChangePercent);
    }

    [Fact]
    public async Task Vendors_are_ranked_and_a_vendor_who_stopped_buying_is_listed()
    {
        Add(Vend(100m, From, vendor: _vendorA));
        Add(Vend(30m, PreviousDay, vendor: _vendorA));
        Add(Vend(70m, PreviousDay, vendor: _vendorB));
        Add(Sale(40m, From, SaleSourceSystems.ShopTill, ShopWarehouse, "FARM-BP"));

        var usd = Dollars(await RunAsync());

        var tendai = usd.ByVendor.First();
        Assert.Equal("Tendai Moyo", tendai.Label);
        Assert.Equal($"TENDAI · {Depot}", tendai.Hint);
        Assert.Equal(100m, tendai.TotalAmount);
        Assert.Equal(30m, tendai.PreviousTotalAmount);
        Assert.Equal(1, usd.Summary.VendorsServed);
        Assert.Equal(2, usd.Summary.PreviousVendorsServed);

        // The walk-in till sale carries no vendor, so it is not a vendor row.
        Assert.DoesNotContain(usd.ByVendor, row => row.TotalAmount == 40m);

        var lapsed = Assert.Single(usd.LapsedVendors);
        Assert.Equal("Chipo", lapsed.Label);
        Assert.Equal(70m, lapsed.PreviousTotalAmount);
        Assert.Equal(PreviousDay, lapsed.LastSaleDate);
    }

    [Fact]
    public async Task Channels_cost_centres_and_operators_are_broken_down()
    {
        Add(Vend(100m, From, costCentre: "CC-VEND"));
        Add(Sale(40m, From, SaleSourceSystems.ShopTill, ShopWarehouse, "FARM-BP"));

        var usd = Dollars(await RunAsync());

        Assert.Equal(["Vending", "Shop till"], usd.ByChannel.Select(row => row.Label));
        Assert.Equal(71.4m, usd.ByChannel[0].ShareOfValuePercent);
        Assert.Equal(["CC-VEND", "Not recorded"], usd.ByCostCentre.Select(row => row.Label));
        Assert.Equal("Rudo Moyo", Assert.Single(usd.ByOperator).Label);
    }

    [Fact]
    public async Task Every_item_is_listed_including_one_that_stopped_selling()
    {
        for (var i = 0; i < 30; i++)
        {
            Add(Vend(10m, From, lines: [($"ITEM{i:00}", 1, 10m)]));
        }

        Add(Vend(25m, PreviousDay, lines: [("GONE", 5, 25m)]));

        var items = Dollars(await RunAsync()).ByItem;

        Assert.Equal(31, items.Count);
        var gone = items.Single(item => item.ItemCode == "GONE");
        Assert.Equal(0m, gone.NetAmount);
        Assert.Equal(25m, gone.PreviousNetAmount);
        Assert.Equal(-100m, gone.ChangePercent);
    }

    // ---- Health -----------------------------------------------------------------------------------

    [Fact]
    public async Task Posting_is_counted_the_way_the_posting_job_sees_it()
    {
        Add(Vend(100m, From, docEntry: 501));
        Add(Vend(20m, From, fiscal: DesktopSaleFiscalizationStatus.Pending));
        var failing = Vend(30m, To);
        failing.PostingAttempts = 2;
        failing.LastPostingError = "Quantity falls into negative inventory";
        Add(failing);

        // A van sale is not a counter's, so it is not in this report's health at all.
        Add(Sale(90m, From, SaleSourceSystems.VanSales, "VAN001", "VAN-BP"));

        var health = (await RunAsync()).Health;

        Assert.Equal(3, health.SalesCount);
        Assert.Equal(3, health.PostingSalesCount);
        Assert.Equal(1, health.Posted.SalesCount);
        Assert.Equal(1, health.PostingWaiting.SalesCount);
        Assert.Equal(1, health.PostingFailing.SalesCount);
        Assert.Equal(30m, health.PostingFailing.Value.Single().Amount);
        Assert.Equal(From, health.OldestUnpostedDate);
        Assert.Equal("Quantity falls into negative inventory", health.LatestPostingError);
        Assert.Equal(1, health.FiscalPending.SalesCount);
        Assert.Equal(2, health.FiscalSucceeded.SalesCount);

        // Asked for by name it is read, and still not as "never posted": the van workflow posts it.
        var vans = (await RunAsync(source: SaleSourceSystems.VanSales)).Health;

        Assert.Equal(1, vans.SalesCount);
        Assert.Equal(0, vans.PostingSalesCount);
    }

    // ---- Margin -----------------------------------------------------------------------------------

    [Fact]
    public async Task Margin_is_SAPs_booked_profit_on_these_sales_own_invoices_only()
    {
        Add(Vend(115m, From, vendor: _vendorA, docEntry: 501, lines: [("BREAD", 10, 100m)]));
        Add(Vend(23m, From, vendor: _vendorB)); // not posted yet: no margin, still in takings

        _costs.Lines.Add(new SaleInvoiceLineCost(501, "BREAD", DepotWarehouse, 10m, 100m, 25m));
        // An invoice raised in SAP directly out of the same warehouse. Not a desktop sale.
        _costs.Lines.Add(new SaleInvoiceLineCost(999, "BREAD", DepotWarehouse, 50m, 500m, 400m));

        var report = await RunAsync();
        var usd = Dollars(report);

        Assert.True(report.Margin.Available);
        Assert.Equal([DepotWarehouse], _costs.RequestedWarehouses);
        // SAP is asked for the invoices the sales were posted as, not for a date window.
        Assert.Equal([501], _costs.RequestedDocEntries);

        Assert.Equal(25m, usd.Summary.GrossProfit);
        Assert.Equal(25m, usd.Summary.MarginPercent);
        Assert.Equal(100m, usd.Summary.CostedNetAmount);
        Assert.Equal(1, usd.Summary.CostedSalesCount);

        Assert.Equal(25m, usd.ByItem.Single(i => i.ItemCode == "BREAD").GrossProfit);
        Assert.Equal(25m, usd.ByDepot.Single().GrossProfit);
        Assert.Equal(25m, usd.ByVendor.Single(v => v.Label == "Tendai Moyo").MarginPercent);
        Assert.Null(usd.ByVendor.Single(v => v.Label == "Chipo").GrossProfit);
        Assert.All(usd.ByPaymentMethod, row => Assert.Null(row.GrossProfit));
    }

    [Fact]
    public async Task A_consolidated_invoice_carries_margin_by_item_and_depot_only()
    {
        var consolidation = new SaleConsolidationEntity
        {
            CardCode = Depot,
            ConsolidationDate = From,
            WarehouseCode = DepotWarehouse,
            SapDocEntry = 777,
            Status = ConsolidationStatus.Posted,
            SaleCount = 2,
            TotalAmount = 120m,
            TotalVat = 15.6m,
        };
        _context.SaleConsolidations.Add(consolidation);
        await _context.SaveChangesAsync();

        foreach (var total in new[] { 50m, 70m })
        {
            var sale = Vend(total, From, vendor: _vendorA);
            sale.ConsolidationId = consolidation.Id;
            sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
            Add(sale);
        }

        _costs.Lines.Add(new SaleInvoiceLineCost(777, "MILK", DepotWarehouse, 12m, 100m, 20m));

        var usd = Dollars(await RunAsync());

        Assert.Equal(2, usd.Summary.CostedSalesCount);
        Assert.Equal(20m, usd.Summary.GrossProfit);
        Assert.Equal(20m, usd.ByDepot.Single().GrossProfit);
        // Consolidation groups by CardCode, so the invoice belongs to exactly one partner.
        Assert.Equal(20m, usd.ByPartner.Single().GrossProfit);
        Assert.Null(usd.ByVendor.Single().GrossProfit);
    }

    [Fact]
    public async Task When_SAP_cannot_be_read_the_margin_is_unavailable_and_the_rest_stands()
    {
        Add(Vend(115m, From, docEntry: 501));
        _costs.Failure = new HttpRequestException("No route to host");

        var report = await RunAsync();
        var usd = Dollars(report);

        Assert.False(report.Margin.Available);
        Assert.Contains("unavailable", report.Margin.Detail);
        Assert.Null(usd.Summary.GrossProfit);
        Assert.Equal(115m, usd.Summary.TotalAmount);
        Assert.Equal(1, report.Health.Posted.SalesCount);
    }

    [Fact]
    public async Task With_nothing_posted_SAP_is_not_asked()
    {
        Add(Vend(115m, From));

        var report = await RunAsync();

        Assert.True(report.Margin.Available);
        Assert.Null(_costs.RequestedWarehouses);
        Assert.Null(Dollars(report).Summary.GrossProfit);
    }

    // ---- Scope ------------------------------------------------------------------------------------

    [Fact]
    public async Task Van_sales_are_left_out_whichever_way_they_arrived()
    {
        Add(Vend(100m, From, lines: [("BREAD", 10, 80m)]));
        Add(Sale(60m, From, SaleSourceSystems.VanSales, "VAN001", "VAN-BP", lines: [("BREAD", 6, 50m)]));
        Add(Sale(90m, From, SaleSourceSystems.VanSalesOnline, "VAN001", "VAN-BP"));
        Add(Sale(40m, PreviousDay, SaleSourceSystems.VanSales, "VAN001", "VAN-BP"));

        var report = await RunAsync();
        var usd = Dollars(report);

        Assert.Equal(100m, usd.Summary.TotalAmount);
        Assert.Equal(0m, usd.Summary.PreviousTotalAmount);
        Assert.Equal(["Vending"], usd.ByChannel.Select(row => row.Label));
        Assert.Equal([DepotWarehouse], usd.ByDepot.Select(row => row.Key));
        Assert.Equal([Depot], usd.ByPartner.Select(row => row.Key));
        Assert.Equal(80m, usd.ByItem.Single(i => i.ItemCode == "BREAD").NetAmount);
        Assert.Equal([Depot], report.Partners.Select(p => p.CardCode));
        Assert.Equal(1, report.Health.SalesCount);

        Assert.Equal(80m, (await AnalyseItemAsync("BREAD")).Currencies.Single().Summary.NetAmount);

        // Either van source is still read when it is the one asked for.
        Assert.Equal(60m, Dollars(await RunAsync(source: SaleSourceSystems.VanSales)).Summary.TotalAmount);
        Assert.Equal(90m, Dollars(await RunAsync(source: SaleSourceSystems.VanSalesOnline)).Summary.TotalAmount);
    }

    [Fact]
    public async Task A_warehouse_narrows_every_figure()
    {
        Add(Vend(100m, From));
        Add(Sale(40m, From, SaleSourceSystems.ShopTill, ShopWarehouse, "FARM-BP"));

        var report = await RunAsync(warehouse: ShopWarehouse);

        Assert.Equal(40m, Dollars(report).Summary.TotalAmount);
        Assert.Equal(1, report.Health.SalesCount);
    }

    // ---- Business partners ------------------------------------------------------------------------

    private const string OtherPartner = "DEPOT2-BP";

    /// <summary>A second account that sells out of the depot's warehouse under its own partner.</summary>
    private DesktopSaleEntity OtherPartnerOffDepot(decimal total, DateTime day, int? docEntry = null, (string Code, int Quantity, decimal Net)[]? lines = null)
    {
        var sale = Sale(total, day, SaleSourceSystems.Vending, DepotWarehouse, OtherPartner, lines: lines);
        sale.CardName = "Depot Two";
        sale.SapDocEntry = docEntry;
        return sale;
    }

    [Fact]
    public async Task Two_partners_sharing_a_warehouse_are_two_rows_not_one()
    {
        var depotSale = Vend(100m, From, docEntry: 601, lines: [("BREAD", 10, 80m)]);
        depotSale.CardName = "Depot One";
        Add(depotSale);
        Add(OtherPartnerOffDepot(50m, From, docEntry: 602, lines: [("BREAD", 5, 40m)]));

        _costs.Lines.Add(new SaleInvoiceLineCost(601, "BREAD", DepotWarehouse, 10m, 80m, 20m));
        _costs.Lines.Add(new SaleInvoiceLineCost(602, "BREAD", DepotWarehouse, 5m, 40m, 4m));

        var report = await RunAsync();
        var usd = Dollars(report);

        // The warehouse view cannot tell them apart; the partner view can.
        Assert.Single(usd.ByDepot);
        var partners = usd.ByPartner.ToDictionary(row => row.Key);
        Assert.Equal(2, partners.Count);
        Assert.Equal("Depot One", partners[Depot].Label);
        Assert.Equal($"{Depot} · {DepotWarehouse}", partners[Depot].Hint);
        Assert.Equal(100m, partners[Depot].TotalAmount);
        Assert.Equal(20m, partners[Depot].GrossProfit);
        Assert.Equal("Depot Two", partners[OtherPartner].Label);
        Assert.Equal(50m, partners[OtherPartner].TotalAmount);
        Assert.Equal(4m, partners[OtherPartner].GrossProfit);

        Assert.Equal(
            [(Depot, 80m), (OtherPartner, 40m)],
            usd.ItemPartnerMatrix.Where(c => c.ItemCode == "BREAD").OrderBy(c => c.CardCode).Select(c => (c.CardCode, c.NetAmount)));

        Assert.Equal(["Depot One", "Depot Two"], report.Partners.Select(p => p.CardName));
        Assert.All(report.Partners, p => Assert.Equal([DepotWarehouse], p.Warehouses));
    }

    [Fact]
    public async Task A_partner_narrows_every_figure_but_not_the_list_it_was_chosen_from()
    {
        Add(Vend(100m, From, lines: [("BREAD", 10, 80m)]));
        Add(OtherPartnerOffDepot(50m, From, lines: [("BREAD", 5, 40m)]));
        Add(OtherPartnerOffDepot(30m, PreviousDay));

        var report = await RunAsync(cardCode: OtherPartner);
        var usd = Dollars(report);

        Assert.Equal(OtherPartner, report.CardCode);
        Assert.Equal(50m, usd.Summary.TotalAmount);
        Assert.Equal(30m, usd.Summary.PreviousTotalAmount);
        Assert.Equal(1, report.Health.SalesCount);
        Assert.Equal([OtherPartner], usd.ByPartner.Select(row => row.Key));
        Assert.Equal(40m, usd.ByItem.Single(i => i.ItemCode == "BREAD").NetAmount);
        Assert.Equal([Depot, OtherPartner], report.Partners.Select(p => p.CardCode).Order());

        var item = (await AnalyseItemAsync("BREAD", OtherPartner)).Currencies.Single();
        Assert.Equal(40m, item.Summary.NetAmount);
        Assert.Equal([OtherPartner], item.ByPartner.Select(row => row.Key));
    }

    [Fact]
    public async Task The_item_drill_down_splits_a_shared_warehouse_by_partner()
    {
        Add(Vend(100m, From, lines: [("BREAD", 10, 80m)]));
        Add(OtherPartnerOffDepot(50m, From, lines: [("BREAD", 5, 40m)]));

        var item = (await AnalyseItemAsync("BREAD")).Currencies.Single();

        Assert.Single(item.ByDepot);
        Assert.Equal(
            [(OtherPartner, "Depot Two", 40m), (Depot, Depot, 80m)],
            item.ByPartner.OrderBy(row => row.NetAmount).Select(row => (row.Key, row.Label, row.NetAmount)));
    }

    [Fact]
    public void The_period_is_capped()
    {
        var validator = new GetManagementSalesReportValidator();

        Assert.False(validator.Validate(new GetManagementSalesReportQuery(_adminId, From, From.AddDays(GetManagementSalesReportValidator.MaxDays))).IsValid);
        Assert.True(validator.Validate(new GetManagementSalesReportQuery(_adminId, From, To)).IsValid);
        Assert.False(validator.Validate(new GetManagementSalesReportQuery(_adminId, To, From)).IsValid);
    }

    [Fact]
    public void A_foreign_currency_invoice_line_is_read_in_its_own_currency()
    {
        // A dollar invoice in a company that books in another currency carries its dollars in the FC
        // columns; a local-currency invoice leaves them zero.
        var dollars = SAPServiceLayerClient.ReadInvoiceLineCost(new Dictionary<string, object?>
        {
            ["DocEntry"] = 501L, ["ItemCode"] = "BREAD", ["WhsCode"] = DepotWarehouse,
            ["Quantity"] = 10L, ["LineTotal"] = 3000m, ["TotalFrgn"] = 100m, ["GrssProfit"] = 750m, ["GrssProfFC"] = 25m,
        });
        var local = SAPServiceLayerClient.ReadInvoiceLineCost(new Dictionary<string, object?>
        {
            ["DocEntry"] = 502L, ["ItemCode"] = "BREAD", ["WhsCode"] = DepotWarehouse,
            ["Quantity"] = 10L, ["LineTotal"] = 3000m, ["TotalFrgn"] = 0L, ["GrssProfit"] = 750m, ["GrssProfFC"] = 0L,
        });

        Assert.Equal((501, 100m, 25m), (dollars.DocEntry, dollars.Revenue, dollars.GrossProfit));
        Assert.Equal((502, 3000m, 750m), (local.DocEntry, local.Revenue, local.GrossProfit));
    }

    // ---- Items ------------------------------------------------------------------------------------

    [Fact]
    public async Task An_item_states_its_price_what_a_customer_takes_and_what_discount_gave_away()
    {
        // 10 loaves listed at 1.20 sold at 10% off: 12.00 at list, 10.80 charged.
        Add(Discounted(From, "BREAD", quantity: 10, unitPrice: 1.20m, discountPercent: 10m));
        Add(Discounted(To, "BREAD", quantity: 6, unitPrice: 1.20m, discountPercent: 0m));
        Add(Discounted(PreviousDay, "BREAD", quantity: 20, unitPrice: 1.00m, discountPercent: 0m));

        var bread = Dollars(await RunAsync()).ByItem.Single(item => item.ItemCode == "BREAD");

        Assert.Equal(16m, bread.Quantity);
        Assert.Equal(18.00m, bread.NetAmount);
        Assert.Equal(1.125m, bread.AverageUnitPrice);
        Assert.Equal(1.00m, bread.PreviousAverageUnitPrice);
        Assert.Equal(12.5m, bread.PriceChangePercent);
        Assert.Equal(-20m, bread.QuantityChangePercent);
        Assert.Equal(8m, bread.UnitsPerSale);
        Assert.Equal(1.20m, bread.DiscountAmount);
    }

    [Fact]
    public async Task Items_roll_up_by_their_SAP_group_and_an_unknown_item_is_ungrouped()
    {
        _context.Products.AddRange(
            new ProductEntity { ItemCode = "BREAD", ItemsGroupCode = 101 },
            new ProductEntity { ItemCode = "BUNS", ItemsGroupCode = 101 },
            new ProductEntity { ItemCode = "MILK", ItemsGroupCode = 202 });
        _context.SaveChanges();

        Add(Vend(115m, From, docEntry: 501, lines: [("BREAD", 10, 60m), ("BUNS", 5, 40m)]));
        Add(Vend(34.5m, From, lines: [("MILK", 3, 30m)]));
        Add(Vend(11.5m, From, lines: [("RETIRED", 1, 10m)]));
        _costs.Lines.Add(new SaleInvoiceLineCost(501, "BREAD", DepotWarehouse, 10m, 60m, 15m));
        _costs.Lines.Add(new SaleInvoiceLineCost(501, "BUNS", DepotWarehouse, 5m, 40m, 5m));

        var usd = Dollars(await RunAsync());
        var groups = usd.ByItemGroup.ToDictionary(group => group.ItemsGroupCode ?? -1);

        Assert.Equal(101, usd.ByItem.Single(i => i.ItemCode == "BREAD").ItemsGroupCode);
        Assert.Null(usd.ByItem.Single(i => i.ItemCode == "RETIRED").ItemsGroupCode);

        Assert.Equal(2, groups[101].ItemCount);
        Assert.Equal(100m, groups[101].NetAmount);
        Assert.Equal(20m, groups[101].GrossProfit);
        Assert.Equal(20m, groups[101].MarginPercent);
        Assert.Null(groups[202].GrossProfit);
        Assert.Equal(10m, groups[-1].NetAmount);
    }

    [Fact]
    public async Task The_item_depot_matrix_has_a_cell_for_each_item_where_it_sold()
    {
        Add(Vend(23m, From, lines: [("BREAD", 10, 20m)]));
        Add(Vend(11.5m, To, lines: [("BREAD", 4, 10m)]));
        Add(Sale(34.5m, From, SaleSourceSystems.ShopTill, ShopWarehouse, "FARM-BP", lines: [("BREAD", 3, 6m), ("MILK", 2, 24m)]));

        var matrix = Dollars(await RunAsync()).ItemDepotMatrix;

        Assert.Equal(3, matrix.Count);
        var breadAtDepot = matrix.Single(cell => cell.ItemCode == "BREAD" && cell.WarehouseCode == DepotWarehouse);
        Assert.Equal(14m, breadAtDepot.Quantity);
        Assert.Equal(30m, breadAtDepot.NetAmount);
        Assert.DoesNotContain(matrix, cell => cell.ItemCode == "MILK" && cell.WarehouseCode == DepotWarehouse);
    }

    [Fact]
    public async Task The_item_drill_down_takes_one_item_apart_and_agrees_with_its_report_row()
    {
        Add(Vend(115m, From, vendor: _vendorA, docEntry: 501, lines: [("BREAD", 10, 60m), ("MILK", 2, 40m)]));
        Add(Vend(23m, To, vendor: _vendorB, lines: [("BREAD", 5, 20m)]));
        Add(Sale(11.5m, From, SaleSourceSystems.ShopTill, ShopWarehouse, "FARM-BP", lines: [("BREAD", 2, 10m)]));
        Add(Vend(46m, PreviousDay, vendor: _vendorA, lines: [("BREAD", 20, 40m)]));
        _costs.Lines.Add(new SaleInvoiceLineCost(501, "BREAD", DepotWarehouse, 10m, 60m, 18m));
        _costs.Lines.Add(new SaleInvoiceLineCost(501, "MILK", DepotWarehouse, 2m, 40m, 2m));

        // Vending and the shop both sell BREAD, so it is two rows, and each is opened on its own.
        var rows = Dollars(await RunAsync()).ByItem.Where(item => item.ItemCode == "BREAD").ToDictionary(item => item.Business);
        Assert.Equal([SaleBusinesses.Shops, SaleBusinesses.Vending], rows.Keys.Order());

        var reportRow = rows[SaleBusinesses.Vending];
        var analysis = await AnalyseItemAsync("BREAD", business: SaleBusinesses.Vending);
        var usd = analysis.Currencies.Single();

        // The drill-down and the report row are the same numbers.
        Assert.Equal(reportRow.Quantity, usd.Summary.Quantity);
        Assert.Equal(reportRow.NetAmount, usd.Summary.NetAmount);
        Assert.Equal(reportRow.GrossProfit, usd.Summary.GrossProfit);
        Assert.Equal(reportRow.AverageUnitPrice, usd.Summary.AverageUnitPrice);

        // Only this item's lines: the milk on the same invoice adds nothing, value or margin. And only
        // vending's: the loaves the shop sold are the shop's row.
        Assert.Equal(80m, usd.Summary.NetAmount);
        Assert.Equal(18m, usd.Summary.GrossProfit);
        Assert.Equal(40m, usd.Summary.PreviousNetAmount);

        var depot = Assert.Single(usd.ByDepot);
        Assert.Equal(DepotWarehouse, depot.Key);
        Assert.Equal(80m, depot.NetAmount);
        Assert.Equal(15m, depot.Quantity);

        var tendai = usd.ByVendor.Single(row => row.Label == "Tendai Moyo");
        Assert.Equal(60m, tendai.NetAmount);
        Assert.Equal(40m, tendai.PreviousNetAmount);
        Assert.Equal(30m, tendai.MarginPercent);

        Assert.Equal(["Vending"], usd.ByChannel.Select(row => row.Label));
        Assert.Equal(10m, usd.ByDay[0].Quantity);
        Assert.Equal(20m, usd.ByDay[1].ComparedQuantity);

        // The shop's loaves, opened from the shop's row.
        var shop = (await AnalyseItemAsync("BREAD", business: SaleBusinesses.Shops)).Currencies.Single();
        Assert.Equal(rows[SaleBusinesses.Shops].NetAmount, shop.Summary.NetAmount);
        Assert.Equal(10m, shop.Summary.NetAmount);
        Assert.Equal(0m, shop.Summary.PreviousNetAmount);
        Assert.Equal("Farm", Assert.Single(shop.ByDepot).Label);
        Assert.Equal(["Shop till"], shop.ByChannel.Select(row => row.Label));
    }

    /// <summary>
    /// Shops and vending sell different ranges. An item both of them sell is ranked, shared, compared and
    /// costed inside each, and nothing on a product row adds the two together.
    /// </summary>
    [Fact]
    public async Task Each_line_of_business_has_its_own_product_rows()
    {
        Add(Vend(460m, From, docEntry: 701, lines: [("BREAD", 100, 300m), ("LOLLY", 200, 100m)]));
        Add(Vend(230m, PreviousDay, lines: [("BREAD", 80, 200m)]));
        Add(Sale(46m, From, SaleSourceSystems.ShopTill, ShopWarehouse, "FARM-BP", lines: [("BREAD", 4, 20m), ("CHEESE", 1, 20m)]));
        Add(Sale(23m, From, SaleSourceSystems.LegacyDesktop, ShopWarehouse, "FARM-BP", lines: [("BREAD", 2, 10m)]));
        var costed = Sale(11.5m, To, SaleSourceSystems.ShopTill, ShopWarehouse, "FARM-BP", lines: [("BREAD", 2, 10m)]);
        costed.SapDocEntry = 702;
        Add(costed);

        _costs.Lines.Add(new SaleInvoiceLineCost(701, "BREAD", DepotWarehouse, 100m, 300m, 30m));
        _costs.Lines.Add(new SaleInvoiceLineCost(702, "BREAD", ShopWarehouse, 2m, 10m, 4m));

        var usd = Dollars(await RunAsync());

        // Shops first, then vending, each ranked by its own value.
        Assert.Equal(
            [(SaleBusinesses.Shops, "BREAD"), (SaleBusinesses.Shops, "CHEESE"), (SaleBusinesses.Vending, "BREAD"), (SaleBusinesses.Vending, "LOLLY")],
            usd.ByItem.Select(item => (item.Business, item.ItemCode)));

        // The shops' bread is the till's and the legacy desktop's together, and nothing of vending's.
        var shopBread = usd.ByItem.Single(item => item.Business == SaleBusinesses.Shops && item.ItemCode == "BREAD");
        Assert.Equal((8m, 40m, 3), (shopBread.Quantity, shopBread.NetAmount, shopBread.SalesCount));
        Assert.Equal(66.7m, shopBread.ShareOfNetPercent);
        Assert.Equal(0m, shopBread.PreviousNetAmount);
        Assert.Equal(4m, shopBread.GrossProfit);

        var vendingBread = usd.ByItem.Single(item => item.Business == SaleBusinesses.Vending && item.ItemCode == "BREAD");
        Assert.Equal((100m, 300m, 1), (vendingBread.Quantity, vendingBread.NetAmount, vendingBread.SalesCount));
        Assert.Equal(75m, vendingBread.ShareOfNetPercent);
        Assert.Equal(200m, vendingBread.PreviousNetAmount);
        Assert.Equal(50m, vendingBread.ChangePercent);
        Assert.Equal(30m, vendingBread.GrossProfit);

        // Item groups roll up inside a business too: one ungrouped row each, each a whole.
        Assert.Equal(
            [(SaleBusinesses.Shops, 60m, 100m), (SaleBusinesses.Vending, 400m, 100m)],
            usd.ByItemGroup.Select(group => (group.Business, group.NetAmount, group.ShareOfNetPercent)));
        Assert.Equal(4m, usd.ByItemGroup[0].GrossProfit);
        Assert.Equal(30m, usd.ByItemGroup[1].GrossProfit);

        // And so do both grids.
        Assert.Equal(
            [(SaleBusinesses.Vending, DepotWarehouse, 300m), (SaleBusinesses.Shops, ShopWarehouse, 40m)],
            usd.ItemDepotMatrix.Where(cell => cell.ItemCode == "BREAD").Select(cell => (cell.Business, cell.WarehouseCode, cell.NetAmount)));
        Assert.Equal(
            [(SaleBusinesses.Vending, Depot, 300m), (SaleBusinesses.Shops, "FARM-BP", 40m)],
            usd.ItemPartnerMatrix.Where(cell => cell.ItemCode == "BREAD").Select(cell => (cell.Business, cell.CardCode, cell.NetAmount)));

        // The headline is still everything: takings are money, and money adds across channels.
        Assert.Equal(540.5m, usd.Summary.TotalAmount);
    }

    [Fact]
    public async Task The_item_drill_down_is_scoped_as_the_report_is()
    {
        var cashier = Guid.NewGuid();
        var farm = _context.Shops.Single();
        _context.Users.Add(new User { Id = cashier, Username = "farm-cashier", PasswordHash = "x", Role = ApplicationRoles.Cashier, IsActive = true, ShopId = farm.Id });
        _context.SaveChanges();

        var refused = await new GetManagementItemAnalysisHandler(_context, new RecordingAuditService(), _costs, NullLogger<GetManagementItemAnalysisHandler>.Instance)
            .Handle(new GetManagementItemAnalysisQuery(cashier, "BREAD", From, To, DepotWarehouse), CancellationToken.None);

        Assert.True(refused.IsError);
    }

    // ---- What was credited back -------------------------------------------------------------------
    //
    // The report counted a sale at what it was rung up for even after the office had credited it back.
    // Every sold figure still does — takings, quantities, shares, the change on the period before and
    // SAP's margin — and what was credited this period is stated beside each, with what is left.

    /// <summary>
    /// A vendor's 115.00 sale of ten loaves on the first day, two of them credited back for 23.00; a
    /// 50.00 sale of milk on the last day nobody credited; and 70.00 in the period before.
    /// </summary>
    private async Task<int> SellAndCreditTwoLoaves()
    {
        var bread = Vend(115m, From, vendor: _vendorA, lines: [("BREAD", 10, 100m)]);
        Add(bread);
        Add(Vend(50m, To, vendor: _vendorB, lines: [("MILK", 5, 40m)]));
        Add(Vend(70m, PreviousDay, vendor: _vendorB));
        await SaleCreditSeed.TillCreditAsync(_context, bread.Id, 23m, lines: (ReceiptLine: 1, Quantity: 2m, UnitPrice: 11.50m));
        return bread.Id;
    }

    [Fact]
    public async Task What_was_sold_stands_and_what_was_credited_is_stated_beside_it()
    {
        await SellAndCreditTwoLoaves();

        var usd = Dollars(await RunAsync());
        var summary = usd.Summary;

        // Sold, and set against the period before as sold.
        Assert.Equal((165m, 70m), (summary.TotalAmount, summary.PreviousTotalAmount));
        Assert.Equal(135.7m, summary.TotalChangePercent);

        Assert.Equal((23m, 142m, 1, 0, 2m), (
            summary.CreditedAmount, summary.NetOfCredits, summary.CreditedSalesCount, summary.FullyCreditedSalesCount, summary.CreditedQuantity));

        // On the day of the sale, though the credit was raised weeks later.
        Assert.Equal(
            new[] { (From, 23m), (From.AddDays(1), 0m), (To, 0m) },
            usd.ByDay.Select(day => (day.Date, day.CreditedAmount)));

        Assert.Equal((23m, 92m), Credit(usd.ByVendor.Single(row => row.Key == _vendorA.ToString())));
        Assert.Equal((0m, 50m), Credit(usd.ByVendor.Single(row => row.Key == _vendorB.ToString())));
        Assert.Equal((23m, 142m), Credit(usd.ByPartner.Single(row => row.Key == Depot)));
        Assert.Equal((23m, 142m), Credit(usd.ByDepot.Single(row => row.Key == DepotWarehouse)));
        Assert.Equal((23m, 142m), Credit(usd.ByChannel.Single(row => row.Key == SaleSourceSystems.Vending)));
        Assert.Equal((23m, 142m), Credit(usd.ByPaymentMethod.Single(row => row.Key == TenderTypes.Cash)));
        Assert.Equal((23m, 142m), Credit(usd.ByOperator.Single()));

        // A vendor who bought only last period is lapsed, credits or not; nothing of this period is on it.
        Assert.Empty(usd.LapsedVendors);
    }

    [Fact]
    public async Task An_item_states_what_was_credited_back_of_it_in_every_place_it_sold()
    {
        await SellAndCreditTwoLoaves();

        var usd = Dollars(await RunAsync());

        // Two of the ten loaves, valued as the line sold them — 10.00 each before VAT, not the 11.50 the
        // credit gave back.
        var bread = usd.ByItem.Single(item => item.ItemCode == "BREAD");
        Assert.Equal(SaleBusinesses.Vending, bread.Business);
        Assert.Equal((10m, 2m, 8m), (bread.Quantity, bread.CreditedQuantity, bread.QuantityAfterCredits));
        Assert.Equal((100m, 20m, 80m), (bread.NetAmount, bread.CreditedNetAmount, bread.NetAmountAfterCredits));

        // Ranked and shared on what sold, as before.
        Assert.Equal(new[] { "BREAD", "MILK" }, usd.ByItem.Select(item => item.ItemCode));
        Assert.Equal(71.4m, bread.ShareOfNetPercent);

        var milk = usd.ByItem.Single(item => item.ItemCode == "MILK");
        Assert.Equal((0m, 0m, 40m), (milk.CreditedQuantity, milk.CreditedNetAmount, milk.NetAmountAfterCredits));

        var group = Assert.Single(usd.ByItemGroup);
        Assert.Equal((2m, 20m, 120m), (group.CreditedQuantity, group.CreditedNetAmount, group.NetAmountAfterCredits));

        var atDepot = usd.ItemDepotMatrix.Single(cell => cell.ItemCode == "BREAD");
        Assert.Equal((DepotWarehouse, 2m, 20m), (atDepot.WarehouseCode, atDepot.CreditedQuantity, atDepot.CreditedNetAmount));
        var asPartner = usd.ItemPartnerMatrix.Single(cell => cell.ItemCode == "BREAD");
        Assert.Equal((Depot, 2m, 20m), (asPartner.CardCode, asPartner.CreditedQuantity, asPartner.CreditedNetAmount));
        Assert.Equal(0m, usd.ItemDepotMatrix.Single(cell => cell.ItemCode == "MILK").CreditedQuantity);
    }

    [Fact]
    public async Task The_item_drill_down_states_the_same_credit_as_the_items_row()
    {
        await SellAndCreditTwoLoaves();

        var row = Dollars(await RunAsync()).ByItem.Single(item => item.ItemCode == "BREAD");
        var bread = (await AnalyseItemAsync("BREAD", business: SaleBusinesses.Vending)).Currencies.Single();

        Assert.Equal((row.CreditedQuantity, row.CreditedNetAmount), (bread.Summary.CreditedQuantity, bread.Summary.CreditedNetAmount));
        Assert.Equal((8m, 80m), (bread.Summary.QuantityAfterCredits, bread.Summary.NetAmountAfterCredits));
        Assert.Equal(2m, bread.ByDay.Single(day => day.Date == From).CreditedQuantity);
        Assert.Equal((2m, 20m), (bread.ByVendor.Single().CreditedQuantity, bread.ByVendor.Single().CreditedNetAmount));
        Assert.Equal(2m, bread.ByDepot.Single().CreditedQuantity);
        Assert.Equal(2m, bread.ByPartner.Single().CreditedQuantity);

        // An item nothing was credited on reads zero, not another item's credit.
        Assert.Equal(0m, (await AnalyseItemAsync("MILK")).Currencies.Single().Summary.CreditedQuantity);
    }

    [Fact]
    public async Task A_credit_on_a_sale_of_the_period_before_is_not_this_periods()
    {
        var before = Vend(70m, PreviousDay, vendor: _vendorB, lines: [("BREAD", 7, 60m)]);
        Add(before);
        Add(Vend(115m, From, vendor: _vendorA, lines: [("BREAD", 10, 100m)]));
        await SaleCreditSeed.TillCreditAsync(_context, before.Id, 70m, lines: (ReceiptLine: 1, Quantity: 7m, UnitPrice: 10m));

        var usd = Dollars(await RunAsync());

        Assert.Equal((0m, 115m), (usd.Summary.CreditedAmount, usd.Summary.NetOfCredits));
        Assert.Equal(70m, usd.Summary.PreviousTotalAmount);
        Assert.Equal(0m, usd.ByItem.Single().CreditedQuantity);
    }

    [Fact]
    public async Task The_workbook_closes_each_sheet_with_what_was_credited()
    {
        await SellAndCreditTwoLoaves();

        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var read = System.Text.Json.JsonSerializer.Deserialize<ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport.ManagementSalesReportResult>(
            System.Text.Json.JsonSerializer.Serialize(await RunAsync(), web), web)!;

        using var workbook = new ClosedXML.Excel.XLWorkbook(new MemoryStream(
            new ShopInventory.Web.Services.ReportExportService().ExportManagementSalesReportToExcel(read)));

        static ClosedXML.Excel.IXLRow Header(ClosedXML.Excel.IXLWorksheet sheet) =>
            sheet.RowsUsed().First(row => row.Cell(1).GetString() == "Currency");

        static List<string> Headings(ClosedXML.Excel.IXLWorksheet sheet) =>
            Header(sheet).CellsUsed().Select(cell => cell.GetString()).ToList();

        static decimal Value(ClosedXML.Excel.IXLWorksheet sheet, Func<ClosedXML.Excel.IXLRow, bool> which, string column)
        {
            var at = Header(sheet).CellsUsed().First(cell => cell.GetString() == column).Address.ColumnNumber;
            return sheet.RowsUsed().First(which).Cell(at).GetValue<decimal>();
        }

        // Last on every sheet, so no column a reader already knows the place of has moved.
        foreach (var name in new[] { "By Day", "By Partner", "By Vendor", "By Payment" })
            Assert.Equal(new[] { "Credited", "Net of Credits" }, Headings(workbook.Worksheet(name)).TakeLast(2));
        foreach (var name in new[] { "Products", "Item Groups", "Item x Partner", "Item x Warehouse" })
            Assert.Equal(
                new[] { "Credited Qty", "Qty After Credits", "Credited Value", "Value After Credits" },
                Headings(workbook.Worksheet(name)).TakeLast(4));

        var products = workbook.Worksheet("Products");
        bool Bread(ClosedXML.Excel.IXLRow row) => row.Cell(3).GetString() == "BREAD";
        Assert.Equal(100m, Value(products, Bread, "Value before VAT"));
        Assert.Equal(2m, Value(products, Bread, "Credited Qty"));
        Assert.Equal(8m, Value(products, Bread, "Qty After Credits"));
        Assert.Equal(20m, Value(products, Bread, "Credited Value"));
        Assert.Equal(80m, Value(products, Bread, "Value After Credits"));

        var vendors = workbook.Worksheet("By Vendor");
        Assert.Equal(115m, Value(vendors, row => row.Cell(2).GetString() == "Tendai Moyo", "Takings"));
        Assert.Equal(23m, Value(vendors, row => row.Cell(2).GetString() == "Tendai Moyo", "Credited"));
        Assert.Equal(92m, Value(vendors, row => row.Cell(2).GetString() == "Tendai Moyo", "Net of Credits"));

        var summary = workbook.Worksheet("Summary");
        decimal Measure(string measure) =>
            summary.RowsUsed().First(row => row.Cell(2).GetString() == measure).Cell(3).GetValue<decimal>();
        Assert.Equal(165m, Measure("Takings"));
        Assert.Equal(23m, Measure("Credited back"));
        Assert.Equal(142m, Measure("Takings net of credits"));
        Assert.Equal(1m, Measure("Sales credited back"));
        Assert.Equal(0m, Measure("Sales credited in full"));
    }

    private static (decimal Credited, decimal Net) Credit(ManagementBreakdownRow row) => (row.CreditedAmount, row.NetOfCredits);

    // ---- The web side -----------------------------------------------------------------------------

    /// <summary>
    /// The web reads this report through its own copies of the records, and a property renamed on one side
    /// only deserialises as a silent zero. So the handler's real output is sent over the wire the way
    /// ASP.NET sends it, read back into the web models, and exported — and the figures checked at the end.
    /// </summary>
    [Fact]
    public async Task The_web_reads_every_figure_the_API_sends_and_exports_it()
    {
        Add(Vend(115m, From, vendor: _vendorA, docEntry: 501, lines: [("BREAD", 10, 100m)]));
        Add(Vend(70m, PreviousDay, vendor: _vendorB));
        _costs.Lines.Add(new SaleInvoiceLineCost(501, "BREAD", DepotWarehouse, 10m, 100m, 25m));

        var sent = await RunAsync();
        var json = System.Text.Json.JsonSerializer.Serialize(sent, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var read = System.Text.Json.JsonSerializer.Deserialize<ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport.ManagementSalesReportResult>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        // Every property on the web side has a counterpart in what the API sent.
        AssertEveryPropertyArrives(typeof(ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport.ManagementSalesReportResult), json);

        var usd = read.Currencies.Single();
        Assert.Equal(115m, usd.Summary.TotalAmount);
        Assert.Equal(25m, usd.Summary.MarginPercent);
        Assert.Equal("Chipo", usd.LapsedVendors.Single().Label);
        Assert.Equal(1, read.Health.Posted.SalesCount);
        Assert.True(read.Margin.Available);

        using var workbook = new ClosedXML.Excel.XLWorkbook(new MemoryStream(
            new ShopInventory.Web.Services.ReportExportService().ExportManagementSalesReportToExcel(read)));

        Assert.Equal(
            ["Summary", "Posting & Fiscal", "By Day", "By Partner", "By Channel", "By Warehouse", "By Vendor", "By Cost Centre",
             "By Operator", "By Payment", "Lapsed Vendors", "Products", "Item Groups", "Item x Partner", "Item x Warehouse"],
            workbook.Worksheets.Select(sheet => sheet.Name));

        var byPartner = workbook.Worksheet("By Partner");
        Assert.Contains(byPartner.RowsUsed(), row => row.Cell(2).GetString() == Depot && row.Cell(3).GetString() == $"{Depot} · {DepotWarehouse}");
        Assert.Contains(workbook.Worksheet("Item x Partner").RowsUsed(),
            row => row.Cell(2).GetString() == "Vending" && row.Cell(3).GetString() == "BREAD" && row.Cell(4).GetString() == Depot);

        // Every product sheet leads with the channel its rows belong to.
        var products = workbook.Worksheet("Products");
        var header = products.RowsUsed().First(row => row.Cell(3).GetString() == "Item Code");
        Assert.Equal("Channel", header.Cell(2).GetString());
        int Column(string name) => header.CellsUsed().First(cell => cell.GetString() == name).Address.ColumnNumber;
        var bread = products.RowsUsed().First(row => row.Cell(3).GetString() == "BREAD");
        Assert.Equal("Vending", bread.Cell(2).GetString());
        Assert.Equal("Vending", workbook.Worksheet("Item Groups").RowsUsed()
            .First(row => row.Cell(3).GetString() == "Not in the product master").Cell(2).GetString());
        Assert.Equal(100m, bread.Cell(Column("Value before VAT")).GetValue<decimal>());
        Assert.Equal(10m, bread.Cell(Column("Avg Price")).GetValue<decimal>());
        Assert.Equal(25m, bread.Cell(Column("Gross Profit")).GetValue<decimal>());
        Assert.Equal(0.25m, bread.Cell(Column("Margin")).GetValue<decimal>());

        var matrixSheet = workbook.Worksheet("Item x Warehouse");
        var breadAtDepot = matrixSheet.RowsUsed().First(row => row.Cell(3).GetString() == "BREAD");
        Assert.Equal("Vending", breadAtDepot.Cell(2).GetString());
        Assert.Equal(DepotWarehouse, breadAtDepot.Cell(4).GetString());
        Assert.Equal(10m, breadAtDepot.Cell(5).GetValue<decimal>());

        // The drill-down's contract, the same way: every property the web reads is one the API sends.
        var analysis = await AnalyseItemAsync("BREAD");
        var analysisJson = System.Text.Json.JsonSerializer.Serialize(analysis, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        AssertEveryPropertyArrives(typeof(ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport.ManagementItemAnalysisResult), analysisJson);
        var readAnalysis = System.Text.Json.JsonSerializer.Deserialize<ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport.ManagementItemAnalysisResult>(
            analysisJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(25m, readAnalysis.Currencies.Single().Summary.GrossProfit);
        Assert.Equal("Tendai Moyo", readAnalysis.Currencies.Single().ByVendor.Single().Label);

        var vendors = workbook.Worksheet("By Vendor");
        var chipo = vendors.RowsUsed().First(row => row.Cell(2).GetString() == "Chipo");
        Assert.Equal(-1m, chipo.Cell(10).GetValue<decimal>());
        Assert.True(chipo.Cell(11).IsEmpty(), "an uncosted vendor's gross profit is blank, not zero");
    }

    private static void AssertEveryPropertyArrives(Type webType, string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(document.RootElement, names);

        foreach (var property in AllProperties(webType, []))
        {
            Assert.True(names.Contains(property), $"The API sends no '{property}' for the web model to read.");
        }

        static void Collect(System.Text.Json.JsonElement element, HashSet<string> names)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    names.Add(property.Name);
                    Collect(property.Value, names);
                }
            }
            else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, names);
                }
            }
        }

        static IEnumerable<string> AllProperties(Type type, HashSet<Type> seen)
        {
            if (!seen.Add(type))
            {
                yield break;
            }

            foreach (var property in type.GetProperties())
            {
                yield return property.Name;

                var inner = property.PropertyType.IsGenericType
                    ? property.PropertyType.GetGenericArguments()[0]
                    : property.PropertyType;
                if (inner.Namespace == type.Namespace)
                {
                    foreach (var nested in AllProperties(inner, seen))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    // ---- Harness ----------------------------------------------------------------------------------

    private void Add(DesktopSaleEntity sale)
    {
        _context.DesktopSales.Add(sale);
        _context.SaveChanges();
    }

    private DesktopSaleEntity Discounted(DateTime day, string item, int quantity, decimal unitPrice, decimal discountPercent)
    {
        var net = Math.Round(quantity * unitPrice * (1 - discountPercent / 100m), 2);
        var sale = Vend(Math.Round(net * 1.15m, 2), day);
        sale.Lines.Add(new DesktopSaleLineEntity
        {
            LineNum = 1,
            ItemCode = item,
            ItemDescription = item,
            Quantity = quantity,
            UnitPrice = unitPrice,
            DiscountPercent = discountPercent,
            LineTotal = net,
            WarehouseCode = DepotWarehouse,
        });
        return sale;
    }

    private async Task<ManagementItemAnalysis> AnalyseItemAsync(string itemCode, string? cardCode = null, string? business = null)
    {
        _context.ChangeTracker.Clear();
        var result = await new GetManagementItemAnalysisHandler(
                _context,
                new RecordingAuditService(),
                _costs,
                NullLogger<GetManagementItemAnalysisHandler>.Instance)
            .Handle(new GetManagementItemAnalysisQuery(_adminId, itemCode, From, To, CardCode: cardCode, Business: business), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private DesktopSaleEntity Vend(
        decimal total,
        DateTime day,
        int? vendor = null,
        int? docEntry = null,
        string currency = "USD",
        string? costCentre = null,
        DesktopSaleFiscalizationStatus fiscal = DesktopSaleFiscalizationStatus.Success,
        (string Code, int Quantity, decimal Net)[]? lines = null)
    {
        var sale = Sale(total, day, SaleSourceSystems.Vending, DepotWarehouse, Depot, currency, lines);
        sale.RouteCustomerId = vendor;
        sale.SapDocEntry = docEntry;
        sale.CostCentreCode = costCentre;
        sale.FiscalizationStatus = fiscal;
        return sale;
    }

    private DesktopSaleEntity Sale(
        decimal total,
        DateTime day,
        string source,
        string warehouse,
        string cardCode,
        string currency = "USD",
        (string Code, int Quantity, decimal Net)[]? lines = null) => new()
        {
            ExternalReferenceId = $"REF-{++_reference}",
            SourceSystem = source,
            CardCode = cardCode,
            WarehouseCode = warehouse,
            DocDate = day,
            TotalAmount = total,
            VatAmount = Math.Round(total * 0.13m, 2),
            AmountPaid = total,
            Currency = currency,
            PaymentMethod = TenderTypes.Cash,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            CreatedBy = _cashierId.ToString(),
            CreatedAt = DateTime.SpecifyKind(day.AddHours(8), DateTimeKind.Utc),
            Lines = (lines ?? [])
                .Select((line, index) => new DesktopSaleLineEntity
                {
                    LineNum = index + 1,
                    ItemCode = line.Code,
                    ItemDescription = line.Code,
                    Quantity = line.Quantity,
                    UnitPrice = Math.Round(line.Net / line.Quantity, 2),
                    LineTotal = line.Net,
                    WarehouseCode = warehouse,
                })
                .ToList(),
        };

    private async Task<ManagementSalesReport> RunAsync(string? warehouse = null, string? cardCode = null, string? source = null)
    {
        _context.ChangeTracker.Clear();
        var result = await new GetManagementSalesReportHandler(
                _context,
                new RecordingAuditService(),
                _costs,
                NullLogger<GetManagementSalesReportHandler>.Instance)
            .Handle(new GetManagementSalesReportQuery(_adminId, From, To, warehouse, source, cardCode), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private static ManagementCurrencySection Dollars(ManagementSalesReport report) =>
        report.Currencies.Single(c => c.Currency == "USD");

    private sealed class FakeCostReader : ISaleInvoiceCostReader
    {
        public List<SaleInvoiceLineCost> Lines { get; } = [];
        public Exception? Failure { get; set; }
        public List<string>? RequestedWarehouses { get; private set; }
        public List<int>? RequestedDocEntries { get; private set; }

        public Task<IReadOnlyList<SaleInvoiceLineCost>> ReadAsync(
            IReadOnlyCollection<string> warehouseCodes, IReadOnlyCollection<int> docEntries, CancellationToken cancellationToken)
        {
            RequestedWarehouses = warehouseCodes.ToList();
            RequestedDocEntries = docEntries.ToList();
            return Failure is not null
                ? Task.FromException<IReadOnlyList<SaleInvoiceLineCost>>(Failure)
                : Task.FromResult<IReadOnlyList<SaleInvoiceLineCost>>(Lines);
        }
    }
}
