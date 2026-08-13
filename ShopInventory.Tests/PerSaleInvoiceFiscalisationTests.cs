using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the guard against fiscalising a till, vending or van sale twice.
///
/// The sibling of <see cref="ConsolidatedInvoiceFiscalisationTests"/>, for the routes that post one
/// SAP invoice per sale instead of one per customer per day. The exposure is the same shape and just
/// as irreversible, but it hides better: the receipt was signed under the sale's own external
/// reference, while every lookup downstream is keyed on the SAP DocNum. So the platform is asked
/// about a number it has never seen, answers "not fiscalised", and the backfill writes that down as
/// fact. The invoice then positively asserts it is unfiscalised, the Fiscalise button appears, and one
/// click sends a sale the customer is already holding a receipt for to FDMS a second time. FDMS's own
/// duplicate guard is keyed on (TaxPayerTIN, ReceiptType, InvoiceNo) and cannot see it either, because
/// the two invoice numbers differ.
///
/// This applied to van sales before the till route existed, so the guard is keyed on the sale row
/// rather than on anything the till route owns, and covers all three.
/// </summary>
public sealed class PerSaleInvoiceFiscalisationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public PerSaleInvoiceFiscalisationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
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

    private void SeedSale(
        int sapDocNum,
        string externalReference,
        string sourceSystem = SaleSourceSystems.ShopTill,
        DesktopSaleFiscalizationStatus fiscalisation = DesktopSaleFiscalizationStatus.Success)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = externalReference,
            SourceSystem = sourceSystem,
            CardCode = "KEFSHOP-BP",
            DocDate = new DateTime(2026, 8, 13),
            WarehouseCode = "KEFSHOP",
            Currency = "USD",
            TotalAmount = 25m,
            AmountPaid = 25m,
            PaymentMethod = TenderTypes.Cash,
            FiscalizationStatus = fiscalisation,
            FiscalReceiptNumber = fiscalisation == DesktopSaleFiscalizationStatus.Success ? "R-771" : null,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            SapDocNum = sapDocNum,
            SapDocEntry = sapDocNum + 1000,
            CreatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// The behaviour the whole guard exists for. Queueing is gated on the "Unknown" status, so an
    /// invoice reporting anything else can never reach the backfill that would declare it unfiscalised.
    /// </summary>
    [Fact]
    public async Task An_invoice_recording_a_fiscalised_sale_is_never_queued_for_fiscalisation()
    {
        SeedSale(sapDocNum: 6001, externalReference: "KEFSHOP-01-20260813-000123");
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var page = new List<InvoiceDto>
        {
            new() { DocNum = 6001, DocEntry = 7001 },
            new() { DocNum = 6002, DocEntry = 7002 }
        };

        await FiscalDocumentStatusProjector.EnrichInvoicesAsync(_context, page, CancellationToken.None);

        Assert.True(page[0].IsFiscalized);
        Assert.Equal("Fiscalised", page[0].FiscalizationStatus);
        // The ordinary invoice beside it is untouched, so the gate is the sale and not the page.
        Assert.Equal("Unknown", page[1].FiscalizationStatus);

        var queue = new InvoiceFiscalStatusBackfillQueue();
        var queued = InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill(page, queue);

        Assert.Equal(1, queued);
        Assert.Equal(6002, (await queue.DequeueAsync(CancellationToken.None)).DocNum);
    }

    [Theory]
    [InlineData(SaleSourceSystems.ShopTill)]
    [InlineData(SaleSourceSystems.Vending)]
    [InlineData(SaleSourceSystems.VanSales)]
    public async Task Every_one_invoice_per_sale_route_is_covered(string sourceSystem)
    {
        // Van sales carried this exposure in production before the till route existed, so the guard is
        // keyed on the sale row rather than on anything only a till writes.
        SeedSale(sapDocNum: 6100, externalReference: $"REF-{sourceSystem}", sourceSystem: sourceSystem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var invoice = new InvoiceDto { DocNum = 6100, DocEntry = 7100 };
        await FiscalDocumentStatusProjector.EnrichInvoiceAsync(_context, invoice, CancellationToken.None);

        Assert.True(invoice.IsFiscalized);
        Assert.Equal("Fiscalised", invoice.FiscalizationStatus);
    }

    /// <summary>
    /// The fiscal transaction row is written after the SAP post has already committed, so it is
    /// best-effort. The sale row is not — its DocNum is written in the same SaveChanges as the post —
    /// which is why the guard reads that instead.
    /// </summary>
    [Fact]
    public async Task A_stale_not_fiscalised_row_does_not_reopen_the_hole()
    {
        SeedSale(sapDocNum: 6003, externalReference: "KEFSHOP-01-20260813-000125");
        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = "stale-lookup-6003",
            DocNum = 6003,
            DocumentType = "Invoice",
            Status = "Not Fiscalised",
            Message = "Invoice 6003 is not fiscalised.",
            TimestampUtc = DateTime.UtcNow,
            LastSyncedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var invoice = new InvoiceDto { DocNum = 6003, DocEntry = 7003 };
        await FiscalDocumentStatusProjector.EnrichInvoiceAsync(_context, invoice, CancellationToken.None);

        // The lookup that produced that row asked FDMS about DocNum 6003, which it has never seen —
        // the receipt is under the sale's external reference. Believing it is what sends the sale to
        // FDMS a second time.
        Assert.True(invoice.IsFiscalized);
        Assert.Equal("Fiscalised", invoice.FiscalizationStatus);
    }

    [Fact]
    public async Task A_sale_that_never_fiscalised_leaves_its_invoice_fiscalisable()
    {
        // Nothing to protect here, and blocking it would take away the legitimate remedy: this sale
        // genuinely has no receipt, so a human should be able to fiscalise its invoice.
        SeedSale(
            sapDocNum: 6004,
            externalReference: "KEFSHOP-01-20260813-000126",
            fiscalisation: DesktopSaleFiscalizationStatus.Failed);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var invoice = new InvoiceDto { DocNum = 6004, DocEntry = 7004 };
        await FiscalDocumentStatusProjector.EnrichInvoiceAsync(_context, invoice, CancellationToken.None);

        Assert.NotEqual("Fiscalised", invoice.FiscalizationStatus);
    }

    [Fact]
    public async Task The_registry_finds_the_sale_behind_a_document_number()
    {
        SeedSale(sapDocNum: 6005, externalReference: "KEFSHOP-01-20260813-000127");
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var found = await PerSaleInvoiceRegistry.FindByDocNumAsync(_context, 6005, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("KEFSHOP-01-20260813-000127", found.ExternalReferenceId);
        Assert.Equal("R-771", found.FiscalReceiptNumber);

        // A document number no sale produced must not match, or every ordinary SAP invoice would be
        // reported as already fiscalised.
        Assert.Null(await PerSaleInvoiceRegistry.FindByDocNumAsync(_context, 6006, CancellationToken.None));
        Assert.Null(await PerSaleInvoiceRegistry.FindByDocNumAsync(_context, 0, CancellationToken.None));
    }
}
