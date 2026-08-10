using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocEntry;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the guard against fiscalising an end-of-day consolidated invoice twice.
/// </summary>
/// <remarks>
/// Desktop and POS sales are fiscalised before they reach SAP, each under its own external
/// reference. Consolidation then posts one SAP invoice covering all of them, with its own DocNum.
/// Fiscalising that invoice sends the same economic sales to FDMS a second time, and the platform
/// cannot refuse it: the duplicate guard is keyed on (TaxPayerTIN, ReceiptType, InvoiceNo) and the
/// two invoice numbers differ. Submission is irreversible; the only remedy is a manual credit note.
///
/// REVMax used to give this a weak accidental guard — the status lookup would find nothing under
/// the consolidated DocNum, and a failed lookup left the invoice alone. That guard went with the
/// migration to the fiscalisation platform.
/// </remarks>
public sealed class ConsolidatedInvoiceFiscalisationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ConsolidatedInvoiceFiscalisationTests()
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

    /// <summary>
    /// The behaviour this whole change exists for. Queueing is gated on the "Unknown" status, so a
    /// consolidated invoice reporting anything else can never reach the backfill.
    /// </summary>
    [Fact]
    public async Task A_consolidated_invoice_is_never_queued_for_fiscalisation()
    {
        SeedConsolidation(sapDocNum: 5001, saleCount: 3);
        await _context.SaveChangesAsync();

        var page = new List<InvoiceDto>
        {
            new() { DocNum = 5001, DocEntry = 91 },
            new() { DocNum = 5002, DocEntry = 92 }
        };

        await FiscalDocumentStatusProjector.EnrichInvoicesAsync(_context, page, CancellationToken.None);

        Assert.Equal("Fiscalised", page[0].FiscalizationStatus);
        Assert.True(page[0].IsFiscalized);
        // The ordinary invoice beside it is untouched, so the gate is the consolidation and not the page.
        Assert.Equal("Unknown", page[1].FiscalizationStatus);

        var queue = new InvoiceFiscalStatusBackfillQueue();
        var queued = InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill(page, queue);

        // One accepted, and it is the ordinary invoice — the consolidated one never entered.
        Assert.Equal(1, queued);
        Assert.Equal(5002, (await queue.DequeueAsync(CancellationToken.None)).DocNum);
    }

    /// <summary>
    /// The fiscal transaction row is written after the SAP post has already committed, so it is
    /// best-effort — the consolidation marker is not. An invoice consolidated before that row
    /// existed, or one whose row failed to write, must still be kept away from the queue.
    /// </summary>
    [Fact]
    public async Task A_consolidated_invoice_stays_out_of_the_queue_without_a_fiscal_transaction_row()
    {
        SeedConsolidation(sapDocNum: 5003, saleCount: 2);
        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = "stale-lookup-5003",
            DocNum = 5003,
            DocumentType = "Invoice",
            Status = "Not Fiscalised",
            Message = "Invoice 5003 is not fiscalised on REVMax.",
            TimestampUtc = DateTime.UtcNow,
            LastSyncedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var invoice = new InvoiceDto { DocNum = 5003, DocEntry = 93 };
        await FiscalDocumentStatusProjector.EnrichInvoiceAsync(_context, invoice, CancellationToken.None);

        // A lookup that found nothing does not make it fiscalisable — nothing was ever going to be
        // found under the consolidated DocNum. "Not Fiscalised" here would leave the Fiscalise
        // button showing, which is the click this change exists to prevent.
        Assert.Equal("Fiscalised", invoice.FiscalizationStatus);
        Assert.True(invoice.IsFiscalized);
        Assert.Equal(
            0,
            InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill([invoice], new InvoiceFiscalStatusBackfillQueue()));
    }

    [Fact]
    public async Task Consolidation_records_the_invoice_as_fiscalised_and_names_the_receipts_it_replaced()
    {
        var consolidation = SeedConsolidation(sapDocNum: 5004, saleCount: 2);
        await _context.SaveChangesAsync();

        ConsolidatedFiscalReceipt[] receipts =
        [
            new("DS-0001", "41", "9911", 60m),
            new("POS-77", "POS-77", "9912", 40m)
        ];

        var recorded = await InvoiceFiscalTransactionSync.RecordConsolidatedInvoiceAsync(
            SenderFor(new SyncFiscalTransactionHandler(_context, NullLogger<SyncFiscalTransactionHandler>.Instance)),
            consolidation,
            receipts,
            "ZWG",
            NullLogger.Instance,
            CancellationToken.None);

        Assert.True(recorded);
        _context.ChangeTracker.Clear();

        var row = await _context.DesktopFiscalTransactions.SingleAsync(CancellationToken.None);
        Assert.Equal(5004, row.DocNum);
        Assert.Equal("Fiscalised", row.Status);
        Assert.Equal("DesktopSaleConsolidation", row.SourceSystem);
        Assert.Contains("fiscalised before SAP", row.Message);
        Assert.Contains("41 (receipt 9911)", row.Message);
        Assert.Contains("POS-77 (receipt 9912)", row.Message);
        Assert.Contains("Do not fiscalise invoice 5004", row.Message);

        // The full list survives on the row, however many receipts the message named.
        var stored = JsonSerializer.Deserialize<List<ConsolidatedFiscalReceipt>>(row.RawResponse!);
        Assert.Equal(2, stored!.Count);
        Assert.Contains(stored, receipt => receipt.ReceiptGlobalNo == "9911" && receipt.FiscalInvoiceNumber == "41");
        Assert.Contains(stored, receipt => receipt.ReceiptGlobalNo == "9912" && receipt.Reference == "POS-77");
    }

    [Fact]
    public async Task The_fiscalise_button_refuses_a_consolidated_invoice_without_reaching_the_platform()
    {
        SeedConsolidation(sapDocNum: 5005, saleCount: 4);
        await _context.SaveChangesAsync();

        var result = await CreateFiscalizeHandler(new InvoiceDto { DocEntry = 94, DocNum = 5005 })
            .Handle(new FiscalizeInvoiceCommand(94, null, "operator"), CancellationToken.None);

        Assert.True(result.IsError);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Invoice.AlreadyFiscalisedAsConsolidatedSales", error.Code);
        Assert.Equal(ErrorType.Conflict, error.Type);
        Assert.Contains("4 till sale(s)", error.Description);
        Assert.Contains("cannot be reversed", error.Description);
    }

    /// <summary>
    /// The refusal has to be about the consolidation, not about the page being out of date: an
    /// invoice that has never been fiscalised anywhere is still fiscalisable.
    /// </summary>
    [Fact]
    public async Task The_fiscalise_button_still_reaches_the_platform_for_an_ordinary_invoice()
    {
        SeedConsolidation(sapDocNum: 5005, saleCount: 4);
        await _context.SaveChangesAsync();

        var fiscalized = new List<int>();
        var handler = CreateFiscalizeHandler(
            new InvoiceDto { DocEntry = 95, DocNum = 5006 },
            invoice =>
            {
                fiscalized.Add(invoice.DocNum);
                return new FiscalizationResult { Success = true, InvoiceNumber = invoice.DocNum.ToString() };
            });

        var result = await handler.Handle(
            new FiscalizeInvoiceCommand(95, null, "operator"),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal([5006], fiscalized);
    }

    private SaleConsolidationEntity SeedConsolidation(int sapDocNum, int saleCount)
    {
        var consolidation = new SaleConsolidationEntity
        {
            CardCode = "C001",
            CardName = "Till customer",
            ConsolidationDate = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
            SapDocEntry = sapDocNum - 4900,
            SapDocNum = sapDocNum,
            PostedAt = DateTime.UtcNow,
            Status = ConsolidationStatus.Posted,
            TotalAmount = 100m,
            TotalVat = 15m,
            SaleCount = saleCount,
            CreatedAt = DateTime.UtcNow
        };

        _context.SaleConsolidations.Add(consolidation);
        return consolidation;
    }

    private FiscalizeInvoiceHandler CreateFiscalizeHandler(
        InvoiceDto invoice,
        Func<InvoiceDto, FiscalizationResult>? fiscalize = null)
        => new(
            _context,
            SenderFor(
                new SyncFiscalTransactionHandler(_context, NullLogger<SyncFiscalTransactionHandler>.Instance),
                invoice),
            FiscalizationServiceFor(fiscalize),
            AuditSink(),
            NullLogger<FiscalizeInvoiceHandler>.Instance);

    /// <summary>
    /// An <see cref="ISender"/> that answers the two requests these handlers send: the fiscal
    /// transaction upsert, dispatched to the real handler over the SQLite context, and the invoice
    /// lookup, which would otherwise go to SAP.
    /// </summary>
    private static ISender SenderFor(SyncFiscalTransactionHandler syncHandler, InvoiceDto? invoice = null)
        => StubProxy.For<ISender>((method, args) => args?[0] switch
        {
            SyncFiscalTransactionCommand command => syncHandler.Handle(command, CancellationToken.None),
            GetInvoiceByDocEntryQuery when invoice is not null => Task.FromResult<ErrorOr<InvoiceDto>>(invoice),
            var request => throw new InvalidOperationException(
                $"ISender.{method.Name} was called with an unexpected request: {request?.GetType().Name ?? "null"}.")
        });

    private static IFiscalizationService FiscalizationServiceFor(Func<InvoiceDto, FiscalizationResult>? fiscalize)
        => fiscalize is null
            ? StubProxy.Unused<IFiscalizationService>()
            : StubProxy.For<IFiscalizationService>((method, args) => method.Name switch
            {
                nameof(IFiscalizationService.FiscalizeInvoiceAsync) =>
                    Task.FromResult(fiscalize((InvoiceDto)args![0]!)),
                _ => throw new InvalidOperationException($"IFiscalizationService.{method.Name} was not expected.")
            });

    private static IAuditService AuditSink()
        => StubProxy.For<IAuditService>((_, _) => Task.CompletedTask);
}
