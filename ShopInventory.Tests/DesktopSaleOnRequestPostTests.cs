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
/// The two differ in exactly one respect and it has to stay exactly one: the attempt cap. Everything
/// that prevents a second invoice — the claim, the SAP lookup, the unresolved-post grace window —
/// applies identically, because those exist to stop a duplicate rather than to ration retries.
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

    private async Task<DesktopSaleEntity> GivenSaleAsync(
        string source = SaleSourceSystems.ShopTill,
        int attempts = 0)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = "KEFSHOP-01-20260910-000123",
            SourceSystem = source,
            CardCode = "KEFSHOP-BP",
            DocDate = TradingDate.Date,
            TotalAmount = 25m,
            VatAmount = 3.26m,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            WarehouseCode = "KEFSHOP",
            PostingAttempts = attempts,
            // Zero on purpose: the settlement is a separate step with its own guards, and leaving it
            // out keeps these tests about the invoice.
            AmountPaid = 0m,
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

    private DesktopSalePostingService Service(SapCircuitBreakerState? circuit = null)
        => new(
            _context,
            _sap.Client,
            circuit ?? new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            SaleBatchAllocators.Holding(),
            SalePostGuards.Backed(_connection),
            DesktopCreditPosters.Idle(_context),
            Options.Create(new DesktopSalePostingSettings()),
            Options.Create(new SAPSettings()),
            NullLogger<DesktopSalePostingService>.Instance);

    private sealed class RecordingSapClient
    {
        public List<CreateInvoiceRequest> Created { get; } = [];
        public Dictionary<string, Invoice> ExistingByVanSaleOrder { get; } = new(StringComparer.OrdinalIgnoreCase);

        private int _nextDocNum = 5000;

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) =>
                (object)Task.FromResult(
                    ExistingByVanSaleOrder.TryGetValue((string)args![0]!, out var invoice) ? invoice : null),

            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => Create((CreateInvoiceRequest)args![0]!),

            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        private Task<Invoice> Create(CreateInvoiceRequest request)
        {
            Created.Add(request);
            var docNum = _nextDocNum++;
            return Task.FromResult(new Invoice { DocEntry = docNum, DocNum = docNum });
        }
    }
}
