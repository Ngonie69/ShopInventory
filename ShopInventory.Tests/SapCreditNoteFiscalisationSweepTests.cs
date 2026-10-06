using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNotes;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The scheduled pass that files SAP credit memos nothing else filed — the memos keyed straight into B1,
/// which reach ZIMRA only if someone prints them.
/// </summary>
/// <remarks>
/// What matters is the set it takes. Every memo it should leave alone is one a second receipt would be
/// filed for, and a fiscal receipt cannot be withdrawn.
/// </remarks>
public sealed class SapCreditNoteFiscalisationSweepTests : IDisposable
{
    private static readonly DateTime Yesterday = AuditService.ToCAT(DateTime.UtcNow).Date.AddDays(-2);
    private static readonly DateTime LongSettled = DateTime.UtcNow.AddHours(-2);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public SapCreditNoteFiscalisationSweepTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_memo_keyed_in_SAP_with_no_receipt_is_filed_against_its_base_invoice_and_recorded()
    {
        await AddMemo(60103);
        var fiscal = new RecordingFiscalisation { Result = new FiscalizationResult { Success = true, ReceiptGlobalNo = "9911" } };

        var result = await Sweep(fiscal).FiscaliseOutstandingAsync(CancellationToken.None);

        Assert.Equal(1, result.Fiscalised);
        var call = Assert.Single(fiscal.Calls);
        Assert.Equal(60103, call.Document.DocNum);
        // The receipt being reversed is filed under the base invoice's DocNum, not its DocEntry.
        Assert.Equal("778423", call.OriginalInvoiceNumber);

        var recorded = Assert.Single(fiscal.Recorded).Request;
        Assert.Equal(SapCreditNoteFiscalisationSweep.SourceSystem, recorded.SourceSystem);
        Assert.Equal("CreditNote", recorded.DocumentType);
        Assert.Equal("Success", recorded.Status);
        Assert.Equal(9911, recorded.ReceiptGlobalNo);
        Assert.Empty(_context.ExceptionCenterIncidents);
    }

    [Fact]
    public async Task Memos_that_hold_a_receipt_or_must_not_be_sent_are_left_alone()
    {
        await AddMemo(1, cancelled: true);
        await AddMemo(2, syncedAtUtc: DateTime.UtcNow.AddMinutes(-2)); // still inside the grace period
        await AddMemo(3, docDate: Yesterday.AddDays(-30));             // outside the lookback
        await AddMemo(4, cardCode: "EXP047");                          // exports have a route of their own
        await AddMemo(5);
        await AddTransaction(5, "Fiscalised", source: "CreditNoteApprovalAdd");
        await AddMemo(6);
        await AddTransaction(6, "Failed", receiptGlobalNo: 4411);      // a receipt number is evidence
        await AddMemo(7);
        await AddTransaction(7, "Failed", message: "The fiscal outcome is unresolved. Check the receipt first.");
        await AddMemo(8);
        await AddTransaction(8, "Failed", source: SapCreditNoteFiscalisationSweep.SourceSystem);
        await AddTransaction(8, "Failed", source: SapCreditNoteFiscalisationSweep.SourceSystem);
        await AddTransaction(8, "Failed", source: SapCreditNoteFiscalisationSweep.SourceSystem);
        await AddMemo(9);
        await AddFiscalisedTillCredit(sapDocNum: 9);                    // filed at the till before SAP
        await AddMemo(10);
        await AddTransaction(10, "Not Fiscalised", source: "CreditNote");
        await AddTransaction(10, "Failed", source: SapCreditNoteFiscalisationSweep.SourceSystem);

        var candidates = await Sweep(new RecordingFiscalisation()).FindCandidatesAsync(DateTime.UtcNow, CancellationToken.None);

        var only = Assert.Single(candidates);
        Assert.Equal(10, only.DocNum);
        Assert.Equal(1, only.PriorAttempts);
    }

    [Fact]
    public async Task Only_the_last_attempt_raises_an_incident()
    {
        await AddMemo(60045);
        await AddTransaction(60045, "Failed", source: SapCreditNoteFiscalisationSweep.SourceSystem);
        var fiscal = new RecordingFiscalisation { Result = new FiscalizationResult { Success = false, Message = "RCPT032" } };

        await Sweep(fiscal, maxAttempts: 3).FiscaliseOutstandingAsync(CancellationToken.None);
        Assert.Empty(_context.ExceptionCenterIncidents);

        await AddTransaction(60045, "Failed", source: SapCreditNoteFiscalisationSweep.SourceSystem);
        await Sweep(fiscal, maxAttempts: 3).FiscaliseOutstandingAsync(CancellationToken.None);

        var incident = Assert.Single(_context.ExceptionCenterIncidents);
        Assert.Equal("SAP-CN-60045", incident.Reference);
    }

    [Fact]
    public async Task When_REVMax_cannot_be_asked_nothing_is_recorded_and_the_pass_stops()
    {
        await AddMemo(59606);
        await AddMemo(59655);
        var fiscal = new RecordingFiscalisation
        {
            Result = new FiscalizationResult
            {
                Success = false,
                ErrorCode = RevmaxHistoryFiscalizationService.HistoryUnavailableErrorCode,
                Message = "REVMax could not be asked."
            }
        };

        var result = await Sweep(fiscal).FiscaliseOutstandingAsync(CancellationToken.None);

        Assert.Single(fiscal.Calls);
        Assert.Empty(fiscal.Recorded);
        Assert.NotNull(result.StoppedBecause);
    }

    [Fact]
    public async Task A_dry_run_is_neither_recorded_as_fiscalised_nor_counted_as_an_attempt()
    {
        await AddMemo(60011);
        var fiscal = new RecordingFiscalisation
        {
            Result = new FiscalizationResult { Success = false, Skipped = true, ErrorCode = "DryRun", Message = "dry-run mode" }
        };

        var result = await Sweep(fiscal).FiscaliseOutstandingAsync(CancellationToken.None);

        Assert.Empty(fiscal.Recorded);
        Assert.NotNull(result.StoppedBecause);
        Assert.Equal("Failed", SapCreditNoteFiscaliser.StatusOf(fiscal.Result));
    }

    [Fact]
    public async Task A_memo_with_no_base_invoice_is_refused_and_recorded_so_the_console_lists_it()
    {
        await AddMemo(60200);
        var sap = new FakeSap { BaseEntry = null };
        var fiscal = new RecordingFiscalisation();

        var result = await Sweep(fiscal, sap: sap).FiscaliseOutstandingAsync(CancellationToken.None);

        Assert.Equal(1, result.Refused);
        Assert.Empty(fiscal.Calls);
        var recorded = Assert.Single(fiscal.Recorded).Request;
        Assert.Equal("Failed", recorded.Status);
        Assert.Contains("not based on an invoice", recorded.Message);
    }

    [Fact]
    public async Task Nothing_is_sent_while_the_provider_is_switched_off()
    {
        await AddMemo(60103);
        var fiscal = new RecordingFiscalisation();

        await Sweep(fiscal, fiscalisation: new FiscalisationSettings { Provider = FiscalisationProvider.Platform, Enabled = false })
            .FiscaliseOutstandingAsync(CancellationToken.None);

        Assert.Empty(fiscal.Calls);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private SapCreditNoteFiscalisationSweep Sweep(
        RecordingFiscalisation fiscal,
        int maxAttempts = 3,
        FakeSap? sap = null,
        FiscalisationSettings? fiscalisation = null)
    {
        var client = (sap ?? new FakeSap()).AsClient();
        var fiscalisationOptions = Options.Create(fiscalisation ?? new FiscalisationSettings { Provider = FiscalisationProvider.Platform });

        return new SapCreditNoteFiscalisationSweep(
            _context,
            client,
            new SapCreditNoteFiscaliser(
                _context,
                client,
                fiscal.AsService(),
                fiscal.AsSender(),
                fiscalisationOptions,
                NullLogger<SapCreditNoteFiscaliser>.Instance),
            Options.Create(new CreditNoteFiscalisationSettings { MaxAttempts = maxAttempts }),
            fiscalisationOptions,
            Options.Create(new RevmaxSettings()),
            NullLogger<SapCreditNoteFiscalisationSweep>.Instance);
    }

    private async Task AddMemo(
        int docNum,
        bool cancelled = false,
        DateTime? syncedAtUtc = null,
        DateTime? docDate = null,
        string cardCode = "VAN001")
    {
        _context.SapCreditNoteSnapshots.Add(new SapCreditNoteSnapshotEntity
        {
            SapDocEntry = 100_000 + docNum,
            SapDocNum = docNum,
            DocDate = docDate ?? Yesterday,
            CardCode = cardCode,
            CardName = "Van Sales",
            DocCurrency = "USD",
            DocTotal = 10m,
            IsCancelled = cancelled,
            LastSeenInSapAtUtc = syncedAtUtc ?? LongSettled,
            SyncedAtUtc = syncedAtUtc ?? LongSettled
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task AddTransaction(
        int docNum, string status, string source = "CreditNote", int? receiptGlobalNo = null, string? message = null)
    {
        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = Guid.NewGuid().ToString("N"),
            DocumentType = "CreditNote",
            DocNum = docNum,
            Status = status,
            ReceiptGlobalNo = receiptGlobalNo,
            Message = message,
            SourceSystem = source
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task AddFiscalisedTillCredit(int sapDocNum)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = $"GRC-FAC-{sapDocNum}",
            SourceSystem = "KefalosShopTill",
            CardCode = "KEFGRS",
            WarehouseCode = "KEFGRS",
            DocDate = Yesterday,
            TotalAmount = 10m,
            Currency = "USD",
            CreatedAt = LongSettled
        };
        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();

        _context.DesktopCreditNotes.Add(new DesktopCreditNoteEntity
        {
            Id = Guid.NewGuid(),
            SaleId = sale.Id,
            RequestKey = Guid.NewGuid().ToString("N"),
            Number = $"DCN-{sapDocNum}",
            OriginalFiscalNumber = sale.ExternalReferenceId,
            Reason = "Damaged",
            Currency = "USD",
            Amount = 10m,
            Status = DesktopCreditStatuses.Fiscalised,
            SapStatus = DesktopCreditSapStatuses.Posted,
            SapDocNum = sapDocNum,
            PlanJson = string.Empty,
            CreatedAtUtc = LongSettled
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>SAP as the sweep reads it: each memo is based on van invoice DocEntry 2350001, DocNum 778423.</summary>
    private sealed class FakeSap
    {
        public int? BaseEntry { get; init; } = 2350001;

        public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetCreditNoteByDocEntryAsync) => Task.FromResult<SAPCreditNote?>(Memo((int)args![0]!)),
            nameof(ISAPServiceLayerClient.GetInvoiceByDocEntryAsync)
                => Task.FromResult<Invoice?>((int)args![0]! == 2350001 ? new Invoice { DocEntry = 2350001, DocNum = 778423 } : null),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        private SAPCreditNote Memo(int docEntry) => new()
        {
            DocEntry = docEntry,
            DocNum = docEntry - 100_000,
            CardCode = "VAN001",
            CardName = "Van Sales",
            DocTotal = 10m,
            DocCurrency = "USD",
            Cancelled = SapYesNo.No,
            DocumentLines =
            [
                new SAPCreditNoteLine { LineNum = 0, ItemCode = "CHE011", Quantity = 1, LineTotal = 10m, BaseType = BaseEntry is null ? null : 13, BaseEntry = BaseEntry }
            ]
        };
    }

    private sealed class RecordingFiscalisation
    {
        public FiscalizationResult Result { get; init; } = new() { Success = true, Message = "ok" };
        public List<(InvoiceDto Document, string OriginalInvoiceNumber)> Calls { get; } = [];
        public List<SyncFiscalTransactionCommand> Recorded { get; } = [];

        public IFiscalizationService AsService() => StubProxy.For<IFiscalizationService>((method, args) =>
        {
            if (method.Name != nameof(IFiscalizationService.FiscalizeCreditNoteAsync))
            {
                throw new InvalidOperationException($"{method.Name} was not expected.");
            }

            Calls.Add(((InvoiceDto)args![0]!, (string)args[1]!));
            return Task.FromResult(Result);
        });

        public ISender AsSender() => StubProxy.For<ISender>((method, args) =>
        {
            if (method.Name != nameof(ISender.Send) || args?[0] is not SyncFiscalTransactionCommand command)
            {
                throw new InvalidOperationException($"{method.Name} was not expected.");
            }

            Recorded.Add(command);
            return Task.FromResult<ErrorOr<FiscalTransactionLogItemDto>>(new FiscalTransactionLogItemDto());
        });
    }
}
