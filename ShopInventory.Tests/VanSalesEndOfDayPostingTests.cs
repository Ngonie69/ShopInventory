using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Van sales arrive already stamped with a ZIMRA receipt and are posted to SAP one invoice per receipt
/// at end of day, with a mop-up an hour and a half later for vans that were still out of coverage.
///
/// Every test here guards the same failure: a sale reaching SAP twice. The customer already holds a
/// fiscal receipt, so a second invoice cannot be quietly deleted — it has to be reversed with a manual
/// credit note, and the fiscal record and the ledger disagree until someone does.
/// </summary>
public sealed class VanSalesEndOfDayPostingTests : IDisposable
{
    private static readonly DateTime TradingDate = new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingSapClient _sap = new();

    public VanSalesEndOfDayPostingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Defaults to the configured lookback rather than a value chosen here, so the tests that do not
    /// care about the window exercise what production actually runs with.
    /// </summary>
    private VanSalesEndOfDayPostingService BuildService(int? lookbackDays = null)
    {
        var settings = new VanSalesPostingSettings();
        if (lookbackDays.HasValue)
        {
            settings.LookbackDays = lookbackDays.Value;
        }

        return new VanSalesEndOfDayPostingService(
            _context,
            _sap.Client,
            Options.Create(settings),
            NullLogger<VanSalesEndOfDayPostingService>.Instance);
    }

    [Fact]
    public async Task Each_van_sale_posts_as_its_own_sap_invoice()
    {
        AddSale("VAN006-INV-20260810-AAA111", receiptGlobalNo: 501);
        AddSale("VAN006-INV-20260810-BBB222", receiptGlobalNo: 502);
        await _context.SaveChangesAsync();

        var result = await BuildService().PostPendingSalesAsync(TradingDate);

        Assert.Equal(2, result.Posted);
        Assert.Equal(2, _sap.Created.Count);

        // One invoice per receipt is the point: it is the only join SAP↔FDMS reconciliation has.
        Assert.Equal(
            ["VAN006-INV-20260810-AAA111", "VAN006-INV-20260810-BBB222"],
            _sap.Created.Select(c => c.U_Van_saleorder ?? string.Empty).Order().ToArray());
    }

    /// <summary>
    /// The handset's van_order travels through to SAP as U_Van_saleorder. That is what the mop-up asks
    /// SAP about before posting, so it has to be the sale's own reference and not a per-customer or
    /// per-day key.
    /// </summary>
    [Fact]
    public async Task The_sale_reference_is_carried_into_sap_as_the_duplicate_key()
    {
        AddSale("VAN006-INV-20260810-AAA111", receiptGlobalNo: 501);
        await _context.SaveChangesAsync();

        await BuildService().PostPendingSalesAsync(TradingDate);

        var created = Assert.Single(_sap.Created);
        Assert.Equal("VAN006-INV-20260810-AAA111", created.U_Van_saleorder);
        Assert.Equal("VAN006-INV-20260810-AAA111", created.NumAtCard);
        Assert.Equal("VAN006-INV-20260810-AAA111", created.ClientRequestId);
    }

    /// <summary>
    /// The 19:30 mop-up runs over the same day the 18:00 run just handled. It must post only what is
    /// still outstanding — this is the exact scenario that makes ConsolidateDailySales unusable here,
    /// since its CONSOL-{date}-{cardCode} key is identical on both passes.
    /// </summary>
    [Fact]
    public async Task The_mop_up_posts_only_sales_that_arrived_after_the_main_run()
    {
        AddSale("VAN006-INV-20260810-AAA111", receiptGlobalNo: 501);
        await _context.SaveChangesAsync();

        var service = BuildService();
        await service.PostPendingSalesAsync(TradingDate);
        Assert.Single(_sap.Created);

        // A van regains signal at 18:40 and uploads the rest of its day.
        AddSale("VAN006-INV-20260810-CCC333", receiptGlobalNo: 503);
        await _context.SaveChangesAsync();

        var mopUp = await service.PostPendingSalesAsync(TradingDate);

        Assert.Equal(1, mopUp.Posted);
        Assert.Equal(2, _sap.Created.Count);
        Assert.Equal("VAN006-INV-20260810-CCC333", _sap.Created[1].U_Van_saleorder);
    }

    /// <summary>
    /// Re-running over a fully posted day must be a no-op, not a second set of invoices. Someone will
    /// eventually trigger this by hand after a failure.
    /// </summary>
    [Fact]
    public async Task Re_running_over_an_already_posted_day_posts_nothing()
    {
        AddSale("VAN006-INV-20260810-AAA111", receiptGlobalNo: 501);
        await _context.SaveChangesAsync();

        var service = BuildService();
        await service.PostPendingSalesAsync(TradingDate);

        var second = await service.PostPendingSalesAsync(TradingDate);

        Assert.Equal(0, second.Total);
        Assert.Single(_sap.Created);
    }

    /// <summary>
    /// The dangerous case: the 18:00 run reached SAP and then lost the connection before recording the
    /// document number, so the sale still looks Pending. Asking SAP for the reference first is what turns
    /// a would-be duplicate into an adoption.
    /// </summary>
    [Fact]
    public async Task A_sale_sap_already_holds_is_adopted_rather_than_posted_again()
    {
        AddSale("VAN006-INV-20260810-AAA111", receiptGlobalNo: 501);
        await _context.SaveChangesAsync();

        _sap.ExistingByVanSaleOrder["VAN006-INV-20260810-AAA111"] = new Invoice { DocEntry = 77, DocNum = 9001 };

        var result = await BuildService().PostPendingSalesAsync(TradingDate);

        Assert.Equal(1, result.Adopted);
        Assert.Equal(0, result.Posted);
        Assert.Empty(_sap.Created);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(9001, sale.SapDocNum);
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, sale.ConsolidationStatus);
    }

    /// <summary>
    /// A van's evening is a day's takings. One sale SAP refuses — a blocked item, a closed period — must
    /// not hold back the rest, or a single bad line strands the whole van.
    /// </summary>
    [Fact]
    public async Task One_failing_sale_does_not_stop_the_rest_of_the_day()
    {
        AddSale("VAN006-INV-20260810-AAA111", receiptGlobalNo: 501);
        AddSale("VAN006-INV-20260810-BBB222", receiptGlobalNo: 502);
        AddSale("VAN006-INV-20260810-CCC333", receiptGlobalNo: 503);
        await _context.SaveChangesAsync();

        _sap.FailFor.Add("VAN006-INV-20260810-BBB222");

        var result = await BuildService().PostPendingSalesAsync(TradingDate);

        Assert.Equal(2, result.Posted);
        Assert.Equal(1, result.Failed);

        var failed = await _context.DesktopSales
            .SingleAsync(s => s.ExternalReferenceId == "VAN006-INV-20260810-BBB222");

        // Still Pending, so the mop-up offers it again, and the reason is recorded rather than lost.
        Assert.Equal(DesktopSaleConsolidationStatus.Pending, failed.ConsolidationStatus);
        Assert.Equal(1, failed.PostingAttempts);
        Assert.NotNull(failed.LastPostingError);
    }

    /// <summary>
    /// SAP being unreachable is not the sale's fault and must not spend its budget. The pass runs every
    /// half hour, so an outage counted against the budget would exhaust every van sale of the day inside
    /// three hours and hand a whole day's takings to a human over a blip that healed itself.
    /// </summary>
    [Fact]
    public async Task An_unreachable_sap_does_not_spend_a_sales_attempts()
    {
        AddSale("VAN006-INV-20260810-AAA111", receiptGlobalNo: 501);
        await _context.SaveChangesAsync();

        _sap.UnreachableFor.Add("VAN006-INV-20260810-AAA111");

        var service = BuildService();

        // Four hours of half-hourly passes against a SAP that is down.
        for (var pass = 0; pass < 8; pass++)
        {
            var outage = await service.PostPendingSalesAsync(TradingDate);
            Assert.Equal(1, outage.Failed);
        }

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(0, sale.PostingAttempts);

        // The reason is still recorded, so the sale is visible while the outage lasts.
        Assert.NotNull(sale.LastPostingError);

        // And when SAP comes back it posts, with its whole budget intact.
        _sap.UnreachableFor.Clear();
        var recovered = await service.PostPendingSalesAsync(TradingDate);

        Assert.Equal(1, recovered.Posted);
        Assert.Single(_sap.Created);
    }

    /// <summary>
    /// A rejection is the sale's fault and does spend one. This is the distinction the cap was always
    /// about: SAP refusing a blocked item will refuse it again in half an hour.
    /// </summary>
    [Fact]
    public async Task A_sale_sap_refuses_does_spend_an_attempt()
    {
        AddSale("VAN006-INV-20260810-AAA111", receiptGlobalNo: 501);
        await _context.SaveChangesAsync();

        _sap.FailFor.Add("VAN006-INV-20260810-AAA111");

        var service = BuildService();
        await service.PostPendingSalesAsync(TradingDate);
        await service.PostPendingSalesAsync(TradingDate);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(2, sale.PostingAttempts);
    }

    /// <summary>
    /// A sale that fails every night must eventually stop being retried, or it raises the same alarm
    /// twice a day forever and buries the sales that could still be rescued.
    /// </summary>
    [Fact]
    public async Task A_sale_that_keeps_failing_stops_being_retried()
    {
        var sale = AddSale("VAN006-INV-20260810-AAA111", receiptGlobalNo: 501);
        sale.PostingAttempts = 6;
        await _context.SaveChangesAsync();

        var result = await BuildService().PostPendingSalesAsync(TradingDate);

        Assert.Equal(0, result.Total);
        Assert.Empty(_sap.Created);
    }

    /// <summary>
    /// The trading day is the handset's, not the upload's: a sale made on Monday and uploaded Tuesday
    /// morning is stored against Monday, and must still reach SAP against Monday.
    ///
    /// Every trigger asks for today, so when this route matched the day exactly that sale was offered
    /// once — the evening before it was uploaded — and never again. It sat Pending with no attempts
    /// recorded, which is worse than a failure: a failure at least writes down a reason.
    /// </summary>
    [Fact]
    public async Task A_sale_uploaded_the_day_after_it_was_sold_still_posts()
    {
        // The van was out of coverage overnight; this arrives with the morning's first bar of signal.
        var overnight = AddSale("VAN006-INV-20260809-ZZZ999", receiptGlobalNo: 499);
        overnight.DocDate = TradingDate.AddDays(-1).Date;
        await _context.SaveChangesAsync();

        var result = await BuildService().PostPendingSalesAsync(TradingDate);

        Assert.Equal(1, result.Posted);

        var created = Assert.Single(_sap.Created);
        Assert.Equal("VAN006-INV-20260809-ZZZ999", created.U_Van_saleorder);

        // Booked on the day it was sold, not the day it was found. Its ZIMRA receipt is stamped into
        // that fiscal day, and the SAP↔FDMS reconciliation joins the two.
        Assert.Equal("2026-08-09", created.DocDate);
    }

    /// <summary>
    /// The window has a floor. A sale older than the lookback has stopped being a late upload and become
    /// something a person needs to look at; going on offering it every half hour would bury that.
    /// </summary>
    [Fact]
    public async Task A_sale_older_than_the_lookback_window_is_left_alone()
    {
        var onTheEdge = AddSale("VAN006-INV-20260807-EEE555", receiptGlobalNo: 490);
        onTheEdge.DocDate = TradingDate.AddDays(-3).Date;

        var pastIt = AddSale("VAN006-INV-20260806-FFF666", receiptGlobalNo: 480);
        pastIt.DocDate = TradingDate.AddDays(-4).Date;

        await _context.SaveChangesAsync();

        var result = await BuildService(lookbackDays: 3).PostPendingSalesAsync(TradingDate);

        Assert.Equal(1, result.Posted);
        Assert.Equal("VAN006-INV-20260807-EEE555", Assert.Single(_sap.Created).U_Van_saleorder);
    }

    /// <summary>
    /// The window's other end, and why it is allowed to be a bound at all. A handset whose clock runs
    /// ahead dates its sales into the future; those wait rather than posting now. Nothing is stranded by
    /// that, because a run for their day is still coming — which is exactly what a sale behind the run
    /// date does not have, and the whole reason the lookback exists.
    /// </summary>
    [Fact]
    public async Task A_sale_dated_ahead_of_the_run_waits_for_its_own_day()
    {
        var ahead = AddSale("VAN006-INV-20260811-GGG777", receiptGlobalNo: 510);
        ahead.DocDate = TradingDate.AddDays(1).Date;
        await _context.SaveChangesAsync();

        var service = BuildService();

        var today = await service.PostPendingSalesAsync(TradingDate);
        Assert.Equal(0, today.Total);
        Assert.Empty(_sap.Created);

        var tomorrow = await service.PostPendingSalesAsync(TradingDate.AddDays(1));

        Assert.Equal(1, tomorrow.Posted);
        Assert.Equal("VAN006-INV-20260811-GGG777", Assert.Single(_sap.Created).U_Van_saleorder);
    }

    /// <summary>
    /// Desktop sales share this table and reach SAP inside a consolidated invoice instead. Posting one
    /// here as well would invoice it twice.
    /// </summary>
    [Fact]
    public async Task Desktop_sales_are_left_for_the_consolidation_route()
    {
        var desktop = AddSale("DESKTOP-1", receiptGlobalNo: 601);
        desktop.SourceSystem = "KefalosDesktop";
        await _context.SaveChangesAsync();

        var result = await BuildService().PostPendingSalesAsync(TradingDate);

        Assert.Equal(0, result.Total);
        Assert.Empty(_sap.Created);
    }

    private DesktopSaleEntity AddSale(string reference, int receiptGlobalNo)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "SIM001",
            DocDate = TradingDate.Date,
            NumAtCard = reference,
            TotalAmount = 100m,
            VatAmount = 13.04m,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ReceiptGlobalNo = receiptGlobalNo,
            ReceiptCounter = 1,
            FiscalDayNo = "19",
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            WarehouseCode = "VAN006",
            CostCentreCode = "CC006",
            AmountPaid = 100m,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    Quantity = 2m,
                    UnitPrice = 50m,
                    LineTotal = 100m,
                    WarehouseCode = "VAN006"
                }
            ]
        };

        _context.DesktopSales.Add(sale);
        return sale;
    }

    /// <summary>
    /// Records what would have gone to SAP. Built on <see cref="StubProxy"/> because
    /// <see cref="ISAPServiceLayerClient"/> has well over a hundred members; anything the posting service
    /// calls beyond the two answered here throws, so an unexpected SAP call fails the test loudly.
    /// </summary>
    private sealed class RecordingSapClient
    {
        public List<CreateInvoiceRequest> Created { get; } = [];
        public Dictionary<string, Invoice> ExistingByVanSaleOrder { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>References SAP cannot be reached for, as opposed to ones it refuses.</summary>
        public HashSet<string> UnreachableFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        private int _nextDocNum = 1000;

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) =>
                (object)Task.FromResult<Invoice?>(
                    ExistingByVanSaleOrder.TryGetValue((string)args![0]!, out var invoice) ? invoice : null),

            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => CreateInvoice((CreateInvoiceRequest)args![0]!),

            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        private Task<Invoice> CreateInvoice(CreateInvoiceRequest request)
        {
            if (UnreachableFor.Contains(request.U_Van_saleorder ?? string.Empty))
            {
                throw new TimeoutException("The SAP Service Layer did not respond in time.");
            }

            if (FailFor.Contains(request.U_Van_saleorder ?? string.Empty))
            {
                throw new InvalidOperationException("SAP rejected the invoice: item is blocked for sale.");
            }

            Created.Add(request);
            var docNum = _nextDocNum++;
            return Task.FromResult(new Invoice { DocEntry = docNum, DocNum = docNum });
        }
    }
}
