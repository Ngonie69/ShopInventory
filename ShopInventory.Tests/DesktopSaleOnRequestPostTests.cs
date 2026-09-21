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
/// Pins what pressing "Post to SAP" does to a till or vending sale, as against what the background
/// pass does to the same row.
///
/// The two differ in what they ration, and in nothing that prevents a second invoice: the manual post
/// ignores the attempt cap, and it asks SAP about a sale whose post is still held where the pass
/// leaves that sale out until the hold ends. The claim, the lookup before a post and the hold on
/// posting again apply identically, because those exist to stop a duplicate rather than to ration
/// SAP's time.
/// </summary>
public sealed class DesktopSaleOnRequestPostTests : IDisposable
{
    private static readonly DateTime TradingDate = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingSapClient _sap = new();

    public DesktopSaleOnRequestPostTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_sale_the_automatic_pass_has_given_up_on_still_posts_on_request()
    {
        // The case the button exists for. A sale that has spent its attempts is invisible to every
        // future pass, so if the manual post honoured the cap too there would be no way to send it
        // at all — and the usual reason somebody is pressing the button is that they have just fixed
        // whatever SAP was refusing.
        var sale = await GivenSaleAsync(attempts: 99);

        var result = await Service().PostSaleAsync(sale.Id);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Posted);
        Assert.Single(_sap.Created);

        var posted = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, posted.ConsolidationStatus);
        Assert.NotNull(posted.SapDocNum);
    }

    [Fact]
    public async Task A_sale_sap_already_holds_is_adopted_rather_than_invoiced_again()
    {
        var sale = await GivenSaleAsync();
        _sap.ExistingByVanSaleOrder[sale.ExternalReferenceId] = new Invoice { DocEntry = 77, DocNum = 77 };

        var result = await Service().PostSaleAsync(sale.Id);

        Assert.Equal(1, result!.Adopted);
        Assert.Equal(0, result.Posted);
        Assert.Empty(_sap.Created);

        var posted = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(77, posted.SapDocNum);
    }

    [Fact]
    public async Task A_van_sale_is_not_this_routes_to_post()
    {
        // Both services read the same table with the same Pending status, and only the source system
        // keeps them apart. A sale claimed by both is fiscalised once and invoiced twice.
        var sale = await GivenSaleAsync(source: SaleSourceSystems.VanSales);

        Assert.Null(await Service().PostSaleAsync(sale.Id));
        Assert.Empty(_sap.Created);
    }

    [Fact]
    public async Task A_sale_that_does_not_exist_is_answered_with_nothing_rather_than_a_failure()
    {
        Assert.Null(await Service().PostSaleAsync(4242));
    }

    [Fact]
    public async Task Nothing_is_sent_while_the_sap_circuit_is_open()
    {
        var sale = await GivenSaleAsync();

        // Tripped the way a real outage does, one recorded failure at a time.
        var circuit = new SapCircuitBreakerState(Options.Create(new SAPSettings()));
        for (var failure = 0; failure < new SAPSettings().CircuitFailureThreshold; failure++)
        {
            circuit.RecordFailure("SAP did not respond.");
        }

        Assert.True(circuit.IsOpen);

        var result = await Service(circuit).PostSaleAsync(sale.Id);

        Assert.Equal(1, result!.Failed);
        Assert.Empty(_sap.Created);

        // The sale is left exactly as it was. Writing the circuit's message here would overwrite
        // whatever SAP last said about this sale, which is the thing the operator opened it to read.
        var untouched = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Null(untouched.LastPostingError);
        Assert.Equal(0, untouched.PostingAttempts);
    }

    [Fact]
    public async Task A_paid_sale_is_invoiced_and_its_invoice_left_open()
    {
        // No sale is settled on its own: the customer's daily incoming payment closes the invoice. A
        // payment sent from here would close it the moment it arrived.
        var sale = await GivenSaleAsync(amountPaid: 25m);

        var result = await Service().PostSaleAsync(sale.Id);

        Assert.Equal(1, result!.Posted);
        Assert.Single(_sap.Created);
        Assert.Empty(_sap.Payments);

        var posted = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Null(posted.PaymentStatus);
        Assert.Null(posted.PaymentSapDocNum);
    }

    [Fact]
    public async Task A_payment_that_failed_earlier_is_left_to_the_daily_payment()
    {
        // The posting pass used to come back for these. The daily payment picks them up instead, so a
        // pass here would only risk a second payment for the same invoice.
        var sale = await GivenSaleAsync(amountPaid: 25m);
        // Inside the pass's lookback window, or the row is skipped for its date alone.
        sale.DocDate = DateTime.UtcNow.Date;
        sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
        sale.SapDocEntry = 900;
        sale.SapDocNum = 900;
        sale.PaymentStatus = DesktopSalePaymentStatuses.Failed;
        await _context.SaveChangesAsync();

        var result = await Service().PostPendingSalesAsync();

        Assert.Equal(0, result.Total);
        Assert.Empty(_sap.Payments);
    }

    // ---------------------------------------------------------------
    // What the background pass does with a sale whose post is held
    // ---------------------------------------------------------------

    [Fact]
    public async Task The_pass_does_not_ask_sap_about_a_sale_whose_post_is_still_held()
    {
        // A post went out two minutes ago and got no clear answer. Every pass used to load the sale,
        // ask SAP whether it held the invoice, and then not post it because the hold said the answer
        // could not be trusted yet: fifteen lookups per hold, each a scan of every invoice for one
        // UDF. The pass now leaves the sale out until the hold ends.
        var sale = await GivenSaleAsync(docDate: DateTime.UtcNow, postIssuedAtUtc: DateTime.UtcNow.AddMinutes(-2));

        var result = await Service().PostPendingSalesAsync();

        Assert.Equal(0, result.Total);
        Assert.Empty(_sap.LookedUp);
        Assert.Empty(_sap.Created);

        var held = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(DesktopSaleConsolidationStatus.Pending, held.ConsolidationStatus);
        Assert.NotNull(held.PostIssuedAtUtc);
    }

    [Fact]
    public async Task Once_the_hold_has_lapsed_the_pass_asks_sap_once_and_adopts_what_it_finds()
    {
        // The post from twenty minutes ago did land. One lookup, no second invoice.
        var sale = await GivenSaleAsync(docDate: DateTime.UtcNow, postIssuedAtUtc: DateTime.UtcNow.AddMinutes(-20));
        _sap.ExistingByVanSaleOrder[sale.ExternalReferenceId] = new Invoice { DocEntry = 88, DocNum = 88 };

        var result = await Service().PostPendingSalesAsync();

        Assert.Equal(1, result.Adopted);
        Assert.Equal(0, result.Posted);
        Assert.Single(_sap.LookedUp);
        Assert.Empty(_sap.Created);

        var adopted = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(88, adopted.SapDocNum);
    }

    [Fact]
    public async Task A_held_sale_does_not_keep_the_sales_behind_it_out_of_the_batch()
    {
        // Twenty-five held sales after an outage used to be the whole batch, and nothing behind them
        // posted until they cleared.
        var held = await GivenSaleAsync(
            reference: "KEFSHOP-01-20260921-000001",
            docDate: DateTime.UtcNow,
            postIssuedAtUtc: DateTime.UtcNow.AddMinutes(-1));
        var behind = await GivenSaleAsync(reference: "KEFSHOP-01-20260921-000002", docDate: DateTime.UtcNow);

        var result = await Service(settings: new DesktopSalePostingSettings { BatchSize = 1 }).PostPendingSalesAsync();

        Assert.Equal(1, result.Posted);
        Assert.Equal(new[] { behind.ExternalReferenceId }, _sap.LookedUp);

        var rows = await _context.DesktopSales.AsNoTracking().ToDictionaryAsync(s => s.Id);
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, rows[behind.Id].ConsolidationStatus);
        Assert.Equal(DesktopSaleConsolidationStatus.Pending, rows[held.Id].ConsolidationStatus);
    }

    [Fact]
    public async Task A_person_posting_a_held_sale_still_gets_sap_asked()
    {
        // The hold rations the background pass, not a person. Pressing Post asks SAP straight away,
        // so an invoice that has since become visible is adopted on the spot.
        var sale = await GivenSaleAsync(postIssuedAtUtc: DateTime.UtcNow.AddMinutes(-2));
        _sap.ExistingByVanSaleOrder[sale.ExternalReferenceId] = new Invoice { DocEntry = 91, DocNum = 91 };

        var result = await Service().PostSaleAsync(sale.Id);

        Assert.Equal(1, result!.Adopted);
        Assert.Single(_sap.LookedUp);
        Assert.Empty(_sap.Created);
    }

    private async Task<DesktopSaleEntity> GivenSaleAsync(
        string source = SaleSourceSystems.ShopTill,
        int attempts = 0,
        decimal amountPaid = 0m,
        string reference = "KEFSHOP-01-20260910-000123",
        DateTime? docDate = null,
        DateTime? postIssuedAtUtc = null)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = source,
            CardCode = "KEFSHOP-BP",
            DocDate = (docDate ?? TradingDate).Date,
            TotalAmount = 25m,
            VatAmount = 3.26m,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            WarehouseCode = "KEFSHOP",
            PostingAttempts = attempts,
            PostIssuedAtUtc = postIssuedAtUtc,
            // Zero by default: the settlement is a separate step with its own guards, and leaving it
            // out keeps most of these tests about the invoice.
            AmountPaid = amountPaid,
            PaymentMethod = TenderTypes.Cash,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    Quantity = 1m,
                    UnitPrice = 25m,
                    LineTotal = 25m,
                    WarehouseCode = "KEFSHOP"
                }
            ]
        };

        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        return sale;
    }

    private DesktopSalePostingService Service(
        SapCircuitBreakerState? circuit = null,
        DesktopSalePostingSettings? settings = null)
        => new(
            _context,
            _sap.Client,
            circuit ?? new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            SaleBatchAllocators.Holding(),
            SalePostGuards.Backed(_connection),
            DesktopCreditPosters.Idle(_context),
            Options.Create(settings ?? new DesktopSalePostingSettings()),
            NullLogger<DesktopSalePostingService>.Instance);

    private sealed class RecordingSapClient
    {
        public List<CreateInvoiceRequest> Created { get; } = [];
        public List<CreateIncomingPaymentRequest> Payments { get; } = [];
        public Dictionary<string, Invoice> ExistingByVanSaleOrder { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every reference SAP was asked about, in order: the cost the hold exists to bound.</summary>
        public List<string> LookedUp { get; } = [];

        private int _nextDocNum = 5000;

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) => (object)LookUp((string)args![0]!),

            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => Create((CreateInvoiceRequest)args![0]!),

            nameof(ISAPServiceLayerClient.CreateIncomingPaymentAsync) => Pay((CreateIncomingPaymentRequest)args![0]!),

            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        private Task<Invoice?> LookUp(string reference)
        {
            LookedUp.Add(reference);
            return Task.FromResult(ExistingByVanSaleOrder.TryGetValue(reference, out var invoice) ? invoice : null);
        }

        private Task<Invoice> Create(CreateInvoiceRequest request)
        {
            Created.Add(request);
            var docNum = _nextDocNum++;
            return Task.FromResult(new Invoice { DocEntry = docNum, DocNum = docNum });
        }

        private Task<IncomingPayment> Pay(CreateIncomingPaymentRequest request)
        {
            Payments.Add(request);
            var docNum = _nextDocNum++;
            return Task.FromResult(new IncomingPayment { DocEntry = docNum, DocNum = docNum });
        }
    }
}
