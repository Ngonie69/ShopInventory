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
/// Pins the guard that stops one sale being invoiced twice when two posts run at once.
///
/// The sale's older defences — asking SAP for <c>U_Van_saleorder</c> first, and the
/// <c>PostIssuedAtUtc</c> marker — make a <i>sequence</i> of attempts safe. They say nothing at all
/// about two attempts overlapping, and both would pass their checks before either wrote anything.
/// That was survivable while the only writer was one background pass. It stopped being survivable the
/// moment a person could press "Post to SAP": the pass fires every minute, so a manual post lands
/// inside one roughly whenever it is pressed.
///
/// The cost of getting this wrong is not an untidy record. The customer is already holding a ZIMRA
/// receipt, so a second SAP invoice for the same sale can only be undone by a manual credit note, and
/// the ledger disagrees with the fiscal record until somebody raises one. Five sales were invoiced
/// twice on the live company between 25 August and 8 September 2026, and one pair was paid twice.
/// </summary>
public sealed class DesktopSalePostGuardTests : IDisposable
{
    private static readonly DateTime TradingDate = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
    private const string Reference = "VAN006-INV-20260910-AAA111";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly IDesktopSalePostGuard _guard;

    public DesktopSalePostGuardTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();

        // One guard shared by every service the tests build, because it is one store: the claim is a
        // row in Postgres in production precisely so that two web instances collide on it.
        _guard = SalePostGuards.Backed(_connection);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_second_post_while_the_first_is_inside_sap_creates_no_second_invoice()
    {
        await GivenSaleAsync();

        var sap = new BlockingSapClient();

        // The first post is held exactly where the real one waits: inside the Service Layer call,
        // after the local marker was written and before any reply came back.
        var first = Service(_context, sap).PostSaleAsync(1);
        await sap.EnteredSap.Task;

        // A different context, as a second web instance would have.
        using var other = new ApplicationDbContext(_options);
        var second = await Service(other, sap).PostSaleAsync(1);

        Assert.NotNull(second);
        Assert.Equal(1, second!.InFlight);
        Assert.Equal(0, second.Posted);
        Assert.Equal(0, second.Failed);

        sap.ReleaseSap.SetResult();
        var completed = await first;

        Assert.Equal(1, completed!.Posted);

        // The assertion the whole mechanism exists for.
        Assert.Equal(1, sap.InvoicesCreated);
    }

    [Fact]
    public async Task A_sale_refused_by_the_guard_is_not_marked_as_having_failed()
    {
        await GivenSaleAsync();

        var sap = new BlockingSapClient();
        var first = Service(_context, sap).PostSaleAsync(1);
        await sap.EnteredSap.Task;

        using var other = new ApplicationDbContext(_options);
        await Service(other, sap).PostSaleAsync(1);

        // Read on the second context so the first service's tracked entity cannot answer for it.
        using var reader = new ApplicationDbContext(_options);
        var sale = await reader.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == 1);

        // Nothing about being refused belongs on the sale. Writing here would spend an attempt the
        // sale did not make, and overwrite whatever SAP last said about it — which is the one thing
        // an operator opening this row is looking for.
        Assert.Null(sale.LastPostingError);
        Assert.Equal(0, sale.PostingAttempts);

        sap.ReleaseSap.SetResult();
        await first;
    }

    [Fact]
    public async Task A_post_that_already_completed_is_answered_with_its_invoice_rather_than_posting_again()
    {
        await GivenSaleAsync();

        var sap = new BlockingSapClient();
        sap.ReleaseSap.SetResult();

        var first = await Service(_context, sap).PostSaleAsync(1);
        Assert.Equal(1, first!.Posted);

        // Put the sale back the way a lost save would leave it: SAP holds the invoice, this side has
        // no record of it. Only the claim knows, which is the case the replay exists for.
        var sale = await _context.DesktopSales.FirstAsync(s => s.Id == 1);
        sale.SapDocEntry = null;
        sale.SapDocNum = null;
        sale.PostedAt = null;
        sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Pending;
        await _context.SaveChangesAsync();

        // SAP is deliberately made to forget it too, so that adopting can only have come from the
        // claim. In production the U_Van_saleorder lookup would also answer; here it must not, or
        // the test would pass without the replay ever running.
        sap.ForgetLookups = true;

        var again = await Service(_context, sap).PostSaleAsync(1);

        Assert.Equal(1, again!.Adopted);
        Assert.Equal(0, again.Posted);
        Assert.Equal(1, sap.InvoicesCreated);

        var restored = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == 1);
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, restored.ConsolidationStatus);
        Assert.NotNull(restored.SapDocNum);
    }

    [Fact]
    public async Task A_post_sap_refuses_gives_the_claim_back_so_the_sale_can_be_posted_again()
    {
        await GivenSaleAsync();

        var sap = new BlockingSapClient { RefuseNextPost = true };
        sap.ReleaseSap.SetResult();

        var refused = await Service(_context, sap).PostSaleAsync(1);
        Assert.Equal(1, refused!.Failed);
        Assert.Equal(0, sap.InvoicesCreated);

        // The point: a refusal is usually something an operator fixes and retries within the hour,
        // which is well inside the claim's expiry. Holding the claim would leave them pressing a
        // button that answers "already being posted" over a post that ended long ago.
        sap.RefuseNextPost = false;
        var retried = await Service(_context, sap).PostSaleAsync(1);

        Assert.Equal(1, retried!.Posted);
        Assert.Equal(1, sap.InvoicesCreated);
    }

    private async Task GivenSaleAsync()
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            Id = 1,
            ExternalReferenceId = Reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "SIM001",
            DocDate = TradingDate.Date,
            TotalAmount = 100m,
            VatAmount = 13.04m,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ReceiptGlobalNo = 501,
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
        });

        await _context.SaveChangesAsync();
    }

    /// <remarks>
    /// The van route rather than the till route, because it posts an invoice and nothing else — no
    /// incoming payment to settle. The claim is the same object on both, and what is being pinned
    /// here is the claim.
    /// </remarks>
    private VanSalesEndOfDayPostingService Service(ApplicationDbContext context, BlockingSapClient sap)
        => new(
            context,
            sap.Client,
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            SaleBatchAllocators.Holding(),
            new StockLedger(context, Options.Create(new DailyStockSettings()), NullLogger<StockLedger>.Instance),
            _guard,
            Options.Create(new VanSalesPostingSettings()),
            NullLogger<VanSalesEndOfDayPostingService>.Instance);

    /// <summary>
    /// A SAP client that can be held inside <c>CreateInvoiceAsync</c>, which is the only window in
    /// which two posts can overlap.
    /// </summary>
    private sealed class BlockingSapClient
    {
        public TaskCompletionSource EnteredSap { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSap { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvoicesCreated;

        /// <summary>Answers every lookup "not there", whatever it has already created.</summary>
        public bool ForgetLookups { get; set; }

        public bool RefuseNextPost { get; set; }

        private readonly Dictionary<string, Invoice> _posted = new(StringComparer.OrdinalIgnoreCase);
        private int _nextDocNum = 4000;

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) =>
                (object)Task.FromResult(Lookup((string)args![0]!)),

            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => CreateAsync((CreateInvoiceRequest)args![0]!),

            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        private Invoice? Lookup(string reference)
            => !ForgetLookups && _posted.TryGetValue(reference, out var invoice) ? invoice : null;

        private async Task<Invoice> CreateAsync(CreateInvoiceRequest request)
        {
            EnteredSap.TrySetResult();
            await ReleaseSap.Task;

            if (RefuseNextPost)
            {
                // The shape the real client raises when SAP answers a post with a refusal. The
                // posting services read this type as proof no document was created.
                throw new SapRequestRejectedException(
                    "create the invoice",
                    System.Net.HttpStatusCode.BadRequest,
                    "item is blocked for sale");
            }

            var docNum = Interlocked.Increment(ref _nextDocNum);
            Interlocked.Increment(ref InvoicesCreated);

            var invoice = new Invoice { DocEntry = docNum, DocNum = docNum };
            _posted[request.U_Van_saleorder ?? string.Empty] = invoice;
            return invoice;
        }
    }
}
