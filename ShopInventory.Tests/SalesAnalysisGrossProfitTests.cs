using System.Net;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesAnalysis;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;
using WebAnalysis = ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the gross profit on the Shop &amp; Vending and van sales analyses' item rows: SAP's booked gross
/// profit on the invoices the sales were posted as, per item and per line of business, a loss as a
/// negative figure, and nothing at all when it was not asked for or SAP could not be read.
/// </summary>
public sealed class SalesAnalysisGrossProfitTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 9, 1);
    private const string Shop = "KEFSHOP";
    private const string Depot = "VENDDEP";
    private const string Van = "VAN010";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly FakeCostReader _costs = new();

    public SalesAnalysisGrossProfitTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = _adminId,
            Username = "admin",
            PasswordHash = "x",
            Role = ApplicationRoles.Admin,
            IsActive = true,
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Each_item_carries_SAPs_booked_profit_and_a_loss_is_negative()
    {
        _context.DesktopSales.AddRange(
            CounterSale("S1", 501, [("BREAD", 10, 100m), ("MILK", 4, 40m)]),
            CounterSale("S2", 502, [("BREAD", 5, 50m)]),
            // Not yet in SAP: sold, but nothing to cost it against.
            CounterSale("S3", null, [("BREAD", 3, 30m)]));
        await _context.SaveChangesAsync();

        _costs.Lines.Add(new SaleInvoiceLineCost(501, "USD", "BREAD", Shop, 10m, 100m, 30m));
        _costs.Lines.Add(new SaleInvoiceLineCost(501, "USD", "MILK", Shop, 4m, 40m, -6m));
        _costs.Lines.Add(new SaleInvoiceLineCost(502, "USD", "BREAD", Shop, 5m, 50m, 12m));
        // Invoiced out of the same warehouse straight from SAP: not one of these sales.
        _costs.Lines.Add(new SaleInvoiceLineCost(999, "USD", "BREAD", Shop, 50m, 500m, 400m));

        var result = await AnalyseCountersAsync(includeMargin: true);
        var usd = result.Currencies.Single();

        Assert.True(result.Margin!.Available);

        var bread = usd.TopItems.Single(item => item.ItemCode == "BREAD");
        Assert.Equal(180m, bread.NetAmount);
        Assert.Equal(150m, bread.CostedNetAmount);
        Assert.Equal(42m, bread.GrossProfit);
        Assert.Equal(28.0m, bread.MarginPercent);

        var milk = usd.TopItems.Single(item => item.ItemCode == "MILK");
        Assert.Equal(-6m, milk.GrossProfit);
        Assert.Equal(-15.0m, milk.MarginPercent);

        var shops = usd.ItemsByBusiness.Single();
        Assert.Equal(36m, shops.GrossProfit);
        Assert.Equal(190m, shops.CostedNetAmount);
    }

    [Fact]
    public async Task An_item_two_businesses_sell_is_costed_inside_each()
    {
        _context.DesktopSales.AddRange(
            CounterSale("S1", 501, [("BREAD", 10, 100m)]),
            CounterSale("V1", 601, [("BREAD", 20, 160m)], source: SaleSourceSystems.Vending, warehouse: Depot));
        await _context.SaveChangesAsync();

        _costs.Lines.Add(new SaleInvoiceLineCost(501, "USD", "BREAD", Shop, 10m, 100m, 30m));
        _costs.Lines.Add(new SaleInvoiceLineCost(601, "USD", "BREAD", Depot, 20m, 160m, 8m));

        var usd = (await AnalyseCountersAsync(includeMargin: true)).Currencies.Single();

        Assert.Equal(30m, usd.TopItems.Single(item => item.Business == SaleBusinesses.Shops).GrossProfit);
        Assert.Equal(8m, usd.TopItems.Single(item => item.Business == SaleBusinesses.Vending).GrossProfit);
    }

    [Fact]
    public async Task Margin_is_not_read_unless_asked_for()
    {
        _context.DesktopSales.Add(CounterSale("S1", 501, [("BREAD", 10, 100m)]));
        await _context.SaveChangesAsync();

        var result = await AnalyseCountersAsync(includeMargin: false);

        Assert.Null(result.Margin);
        Assert.Null(result.Currencies.Single().TopItems.Single().GrossProfit);
        Assert.Equal(0, _costs.Reads);
    }

    [Fact]
    public async Task SAP_failing_leaves_the_takings_and_says_margin_is_unavailable()
    {
        _context.DesktopSales.Add(CounterSale("S1", 501, [("BREAD", 10, 100m)]));
        await _context.SaveChangesAsync();
        _costs.Failure = new HttpRequestException("Service Layer down");

        var result = await AnalyseCountersAsync(includeMargin: true);
        var bread = result.Currencies.Single().TopItems.Single();

        Assert.False(result.Margin!.Available);
        Assert.Null(bread.GrossProfit);
        Assert.Null(bread.MarginPercent);
        Assert.Equal(100m, bread.NetAmount);
    }

    /// <summary>
    /// An online van sale leaves no desktop sale, so its invoice is known only from its reservation — the
    /// case the van analysis exists for, and the one a desktop-sales-only margin would miss.
    /// </summary>
    [Fact]
    public async Task Van_items_are_costed_on_both_online_and_offline_invoices()
    {
        _context.DesktopSales.Add(CounterSale(
            "OFF-1", 701, [("CHE011", 2, 20m)], source: SaleSourceSystems.VanSales, warehouse: Van,
            createdBy: _adminId));
        _context.StockReservations.Add(OnlineVanSale("ON-1", 702, "CHE011", 3, 30m));
        _context.StockReservations.Add(OnlineVanSale("ON-2", 703, "YOG500", 1, 5m));
        await _context.SaveChangesAsync();

        _costs.Lines.Add(new SaleInvoiceLineCost(701, "USD", "CHE011", Van, 2m, 20m, 4m));
        _costs.Lines.Add(new SaleInvoiceLineCost(702, "USD", "CHE011", Van, 3m, 30m, 6m));
        _costs.Lines.Add(new SaleInvoiceLineCost(703, "USD", "YOG500", Van, 1m, 5m, -1m));

        var result = await new GetVanSalesAnalysisHandler(
                _context, Options.Create(new TaxSettings { VatRate = 0m }), _costs)
            .Handle(new GetVanSalesAnalysisQuery(Day, Day, IncludeMargin: true), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var usd = result.Value.Currencies.Single();

        var cheese = usd.TopItems.Single(item => item.ItemCode == "CHE011");
        Assert.Equal(10m, cheese.GrossProfit);
        Assert.Equal(20.0m, cheese.MarginPercent);
        Assert.Equal(-1m, usd.TopItems.Single(item => item.ItemCode == "YOG500").GrossProfit);
        Assert.Equal(9m, usd.ItemsByBusiness.Single().GrossProfit);
        Assert.Equal([Van], _costs.RequestedWarehouses);
    }

    /// <summary>
    /// The portal mirrors the API's records by hand, so a nullable that does not match reads as nothing.
    /// This carries the API's own JSON through the Web handler, the way the page receives it, and on into
    /// the workbook.
    /// </summary>
    [Fact]
    public async Task Gross_profit_reaches_the_portal_and_the_workbook()
    {
        _context.DesktopSales.AddRange(
            CounterSale("S1", 501, [("BREAD", 10, 100m), ("MILK", 4, 40m)]),
            CounterSale("S2", null, [("JAM", 1, 9m)]));
        await _context.SaveChangesAsync();
        _costs.Lines.Add(new SaleInvoiceLineCost(501, "USD", "BREAD", Shop, 10m, 100m, 30m));
        _costs.Lines.Add(new SaleInvoiceLineCost(501, "USD", "MILK", Shop, 4m, 40m, -6m));

        var json = JsonSerializer.Serialize(
            await AnalyseCountersAsync(includeMargin: true), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var webConnection = new SqliteConnection("DataSource=:memory:");
        webConnection.Open();
        var webOptions = new DbContextOptionsBuilder<WebAppDbContext>().UseSqlite(webConnection).Options;
        await using (var web = new WebAppDbContext(webOptions))
        {
            web.Database.EnsureCreated();
        }

        var requested = new List<string>();
        var result = await new WebAnalysis.GetDesktopSalesAnalysisHandler(
                new HttpClient(new JsonHandler(json, requested)) { BaseAddress = new Uri("http://api.test/") },
                new WebDbContextFactory(webOptions),
                NullLogger<WebAnalysis.GetDesktopSalesAnalysisHandler>.Instance)
            .Handle(new WebAnalysis.GetDesktopSalesAnalysisQuery(Day, Day, null, IncludeMargin: true), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Contains("includeMargin=true", requested.Single());
        Assert.True(result.Value.Margin!.Available);

        var items = result.Value.Currencies.Single().TopItems;
        Assert.Equal((30m, 30.0m), (items.Single(i => i.ItemCode == "BREAD").GrossProfit, items.Single(i => i.ItemCode == "BREAD").MarginPercent));
        Assert.Equal(-6m, items.Single(i => i.ItemCode == "MILK").GrossProfit);
        Assert.Null(items.Single(i => i.ItemCode == "JAM").GrossProfit);
        Assert.Equal(24m, result.Value.Currencies.Single().ItemsByBusiness.Single().GrossProfit);

        using var workbook = new XLWorkbook(new MemoryStream(
            new ReportExportService().ExportDesktopSalesAnalysisToExcel(result.Value)));
        var sheet = workbook.Worksheet("Best Sellers");
        var header = sheet.RowsUsed().First(row => row.Cell(1).GetString() == "Currency");
        var gpCol = header.CellsUsed().Single(cell => cell.GetString() == "Gross Profit").Address.ColumnNumber;
        Assert.Equal("Margin", header.Cell(gpCol + 1).GetString());
        Assert.Equal("Profit / Loss", header.Cell(gpCol + 2).GetString());

        IXLRow Row(string code) => sheet.RowsUsed().Single(row => row.Cell(4).GetString() == code);
        Assert.Equal(30m, Row("BREAD").Cell(gpCol).GetValue<decimal>());
        Assert.Equal("Profit", Row("BREAD").Cell(gpCol + 2).GetString());
        Assert.Equal(-6m, Row("MILK").Cell(gpCol).GetValue<decimal>());
        Assert.Equal(-0.15m, Row("MILK").Cell(gpCol + 1).GetValue<decimal>());
        Assert.Equal("Loss", Row("MILK").Cell(gpCol + 2).GetString());
        Assert.Equal("Not in SAP yet", Row("JAM").Cell(gpCol).GetString());
    }

    private async Task<DesktopSalesAnalysisResult> AnalyseCountersAsync(bool includeMargin)
    {
        var result = await new GetDesktopSalesAnalysisHandler(_context, new RecordingAuditService(), _costs)
            .Handle(new GetDesktopSalesAnalysisQuery(_adminId, Day, Day, IncludeMargin: includeMargin), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private DesktopSaleEntity CounterSale(
        string reference,
        int? sapDocEntry,
        (string Code, int Quantity, decimal Net)[] lines,
        string source = SaleSourceSystems.ShopTill,
        string warehouse = Shop,
        Guid? createdBy = null) => new()
        {
            ExternalReferenceId = reference,
            SourceSystem = source,
            CardCode = "CIS006",
            CardName = "Counter",
            WarehouseCode = warehouse,
            DocDate = Day,
            TotalAmount = lines.Sum(line => line.Net),
            AmountPaid = lines.Sum(line => line.Net),
            Currency = "USD",
            PaymentMethod = "Cash",
            CreatedBy = (createdBy ?? _adminId).ToString(),
            CreatedAt = DateTime.SpecifyKind(Day.AddHours(8), DateTimeKind.Utc),
            SapDocEntry = sapDocEntry,
            Lines = lines
                .Select((line, index) => new DesktopSaleLineEntity
                {
                    LineNum = index,
                    ItemCode = line.Code,
                    ItemDescription = line.Code,
                    Quantity = line.Quantity,
                    UnitPrice = line.Net / line.Quantity,
                    LineTotal = line.Net,
                    WarehouseCode = warehouse,
                })
                .ToList(),
        };

    private StockReservationEntity OnlineVanSale(string reference, int sapDocEntry, string item, int quantity, decimal net)
    {
        var at = DateTime.SpecifyKind(Day.AddHours(8), DateTimeKind.Utc);
        return new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = Van,
            TotalValue = net,
            Currency = "USD",
            PaymentMethod = "Cash",
            Status = ReservationStatus.Confirmed,
            CreatedAt = at,
            ExpiresAt = at.AddHours(1),
            ConfirmedAt = at,
            SAPDocEntry = sapDocEntry,
            CreatedBy = _adminId.ToString(),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = item,
                    ItemDescription = item,
                    OriginalQuantity = quantity,
                    ReservedQuantity = quantity,
                    UoMCode = "EA",
                    WarehouseCode = Van,
                    UnitPrice = net / quantity,
                    LineTotal = net
                }
            ]
        };
    }

    private sealed class JsonHandler(string json, List<string> requested) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class WebDbContextFactory(DbContextOptions<WebAppDbContext> options)
        : IDbContextFactory<WebAppDbContext>
    {
        public WebAppDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeCostReader : ISaleInvoiceCostReader
    {
        public List<SaleInvoiceLineCost> Lines { get; } = [];

        public Exception? Failure { get; set; }

        public int Reads { get; private set; }

        public List<string>? RequestedWarehouses { get; private set; }

        public Task<IReadOnlyList<SaleInvoiceLineCost>> ReadAsync(
            IReadOnlyCollection<string> warehouseCodes, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
        {
            Reads++;
            RequestedWarehouses = warehouseCodes.ToList();
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult<IReadOnlyList<SaleInvoiceLineCost>>(
                Lines.Where(line => warehouseCodes.Contains(line.WarehouseCode, StringComparer.OrdinalIgnoreCase)).ToList());
        }
    }
}
