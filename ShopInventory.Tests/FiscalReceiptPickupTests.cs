using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Features.FiscalReceipts.Commands.PickUpFailedFiscalReceipts;
using ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;
using ShopInventory.Features.Invoices.Commands.RetryFailedInvoiceFiscalisations;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// The scheduled pass that reads back the receipts of SAP invoices and credit notes whose fiscalisation failed, so a
/// receipt the device went on to hold is recorded without anyone pressing Fiscalise.
/// </summary>
/// <remarks>
/// The fiscal transaction log, the status projector the lists read and the invoice retry pass's candidate query are
/// all real; only the device is stubbed. What a reader of the log shows is the point, not what the pass returns.
/// </remarks>
public sealed class FiscalReceiptPickupTests : IDisposable
{
    private const string Invoice = "Invoice";
    private const string CreditNote = "CreditNote";
    private const string Background = InvoiceFiscalizationBackgroundService.SourceSystem;
    private const string Manual = FiscalizeInvoiceHandler.SourceSystem;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly FiscalReceiptPickupSchedule _schedule = new();
    private readonly StubDevice _device = new();
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private readonly MovableClock _clock;

    public FiscalReceiptPickupTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();
        _clock = new MovableClock(_start);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_receipt_the_device_holds_for_a_failed_invoice_is_recorded_and_the_invoice_reads_fiscalised()
    {
        await AddRow(Invoice, 790001, "Failed", minutesAgo: 30, message: "The request to the fiscalisation platform timed out.");
        _device.Holds(ReceiptType.FiscalInvoice, 790001, receiptGlobalNo: 6201);

        var result = await Pass();

        Assert.Equal(1, result.PickedUp);
        Assert.Null(result.StoppedBecause);
        Assert.Equal([(ReceiptType.FiscalInvoice, 790001)], _device.Asked);

        // What the invoice list and PDF read.
        var invoice = new InvoiceDto { DocNum = 790001 };
        await FiscalDocumentStatusProjector.EnrichInvoiceAsync(_context, invoice, CancellationToken.None);
        Assert.True(invoice.IsFiscalized);
        Assert.Equal("Fiscalised", invoice.FiscalizationStatus);
        Assert.Equal(6201, invoice.FiscalReceiptGlobalNo);
        Assert.Equal("https://fdms.example/qr/6201", invoice.FiscalQrCode);
        Assert.Equal("ABCD-1234", invoice.FiscalVerificationCode);

        var recorded = await Latest(Invoice, 790001);
        Assert.Equal(PickUpFailedFiscalReceiptsHandler.SourceSystem, recorded.SourceSystem);
        Assert.Equal("C001", recorded.CardCode);
        Assert.Equal(10m, recorded.DocTotal);

        // Settled: neither this pass nor the retry pass takes it again.
        _device.Asked.Clear();
        Assert.Equal(0, (await Pass(minutesFromNow: 120)).Total);
        Assert.Empty(_device.Asked);
        Assert.Empty(await RetryCandidates());
    }

    [Fact]
    public async Task A_persons_failed_press_and_an_unresolved_outcome_are_looked_up_though_never_resent()
    {
        // The two the retry pass must not send again, and so the two that waited for a person before.
        await AddRow(Invoice, 790011, "Failed", minutesAgo: 30, source: Manual, message: "FDMS_UNREACHABLE: ZIMRA did not answer.");
        await AddRow(Invoice, 790012, "Failed", minutesAgo: 30,
            message: FiscalOutcomeMessages.MarkUnresolved("The platform accepted the request but sent no receipt."));
        Assert.Empty(await RetryCandidates());

        _device.Holds(ReceiptType.FiscalInvoice, 790011, receiptGlobalNo: 6211);
        _device.Holds(ReceiptType.FiscalInvoice, 790012, receiptGlobalNo: 6212);

        var result = await Pass();

        Assert.Equal(2, result.PickedUp);
        Assert.Equal(6211, (await Latest(Invoice, 790011)).ReceiptGlobalNo);
        Assert.Equal(6212, (await Latest(Invoice, 790012)).ReceiptGlobalNo);
    }

    [Fact]
    public async Task An_invoice_raised_in_SAP_and_backfilled_not_fiscalised_is_picked_up_once_printed_from_B1()
    {
        // The status backfill's one read, when the invoice list first showed it: nothing had filed it yet.
        await AddRow(Invoice, 790071, "Not Fiscalised", minutesAgo: 30, source: "InvoiceFiscalisationBackfill",
            message: "Invoice 790071 is not fiscalised.");
        // Printed from B1 since, and the print add-on filed it with the platform directly.
        _device.Holds(ReceiptType.FiscalInvoice, 790071, receiptGlobalNo: 6271);

        var result = await Pass();

        Assert.Equal(1, result.PickedUp);
        var invoice = new InvoiceDto { DocNum = 790071 };
        await FiscalDocumentStatusProjector.EnrichInvoiceAsync(_context, invoice, CancellationToken.None);
        Assert.True(invoice.IsFiscalized);
        Assert.Equal(6271, invoice.FiscalReceiptGlobalNo);
        Assert.Contains("recorded as Not Fiscalised", (await Latest(Invoice, 790071)).Message);
    }

    [Fact]
    public async Task Invoices_first_seen_before_InvoicesFrom_are_never_asked_about_but_credit_notes_are()
    {
        // Yesterday in CAT, so the test holds whatever the hour.
        var from = _start.UtcDateTime.AddHours(2).Date.AddDays(-1);
        var threeDays = 3 * 24 * 60;

        // First seen three days ago, failed again half an hour ago: before the date, however recent the failure.
        await AddRow(Invoice, 790081, "Not Fiscalised", minutesAgo: threeDays, source: "InvoiceFiscalisationBackfill");
        await AddRow(Invoice, 790081, "Failed", minutesAgo: 30, source: Manual);
        // First seen half an hour ago.
        await AddRow(Invoice, 790082, "Failed", minutesAgo: 30);
        // A credit note as old as the first invoice: the date is for invoices.
        await AddRow(CreditNote, 880081, "Failed", minutesAgo: threeDays);
        await AddRow(CreditNote, 880081, "Failed", minutesAgo: 30);
        _device.Holds(ReceiptType.FiscalInvoice, 790081, receiptGlobalNo: 6281, signedAt: _start.UtcDateTime.AddMinutes(-20));
        _device.Holds(ReceiptType.FiscalInvoice, 790082, receiptGlobalNo: 6282);
        _device.Holds(ReceiptType.CreditNote, 880081, receiptGlobalNo: 9181, signedAt: _start.UtcDateTime.AddDays(-2));

        _clock.Now = _start;
        var result = await Handler(new FiscalReceiptPickupSettings { InvoicesFrom = from })
            .Handle(new PickUpFailedFiscalReceiptsCommand(), CancellationToken.None);

        Assert.Equal(2, result.Value.PickedUp);
        Assert.DoesNotContain((ReceiptType.FiscalInvoice, 790081), _device.Asked);
        Assert.Contains((ReceiptType.FiscalInvoice, 790082), _device.Asked);
        Assert.Contains((ReceiptType.CreditNote, 880081), _device.Asked);
        Assert.Equal(2, (await Rows(Invoice, 790081)).Count);
    }

    [Fact]
    public async Task A_failed_credit_note_is_looked_up_as_a_credit_note_and_reads_fiscalised()
    {
        // The create route records the read-back after its attempt, so a failure there reads "Not Fiscalised";
        // the credit memo sweep records "Failed", and an unresolved one it never sends again.
        await AddRow(CreditNote, 880001, "Not Fiscalised", minutesAgo: 30, source: "CreditNote",
            message: "Credit note 880001 is not fiscalised.");
        await AddRow(CreditNote, 880002, "Failed", minutesAgo: 30, source: SapCreditNoteFiscalisationSweep.SourceSystem,
            message: FiscalOutcomeMessages.MarkUnresolved("The platform did not answer."));
        _device.Holds(ReceiptType.CreditNote, 880001, receiptGlobalNo: 9101);
        _device.Holds(ReceiptType.CreditNote, 880002, receiptGlobalNo: 9102);
        // An invoice of the same number: never what a credit note is matched to.
        _device.Holds(ReceiptType.FiscalInvoice, 880001, receiptGlobalNo: 1);

        var result = await Pass();

        Assert.Equal(2, result.PickedUp);
        Assert.All(_device.Asked, asked => Assert.Equal(ReceiptType.CreditNote, asked.Type));

        var creditNote = new CreditNoteDto { SAPDocNum = 880001 };
        await FiscalDocumentStatusProjector.EnrichCreditNoteAsync(_context, creditNote, CancellationToken.None);
        Assert.True(creditNote.IsFiscalized);
        Assert.Equal(9101, creditNote.FiscalReceiptGlobalNo);
        Assert.Equal("https://fdms.example/qr/9101", creditNote.FiscalQrCode);
        Assert.Equal(9102, (await Latest(CreditNote, 880002)).ReceiptGlobalNo);

        // The invoice log is untouched.
        Assert.Empty(await Rows(Invoice, 880001));
    }

    [Fact]
    public async Task Credit_notes_are_left_out_when_switched_off()
    {
        await AddRow(CreditNote, 880011, "Failed", minutesAgo: 30);
        _device.Holds(ReceiptType.CreditNote, 880011, receiptGlobalNo: 9111);

        var result = await Handler(new FiscalReceiptPickupSettings { IncludeCreditNotes = false })
            .Handle(new PickUpFailedFiscalReceiptsCommand(), CancellationToken.None);

        Assert.Equal(0, result.Value.Total);
        Assert.Empty(_device.Asked);
    }

    [Fact]
    public async Task A_receipt_dated_before_the_document_existed_is_an_earlier_documents_and_is_not_taken()
    {
        // SAP reissued numbers after its September 2026 update: the device can hold an older memo under this number,
        // possibly another customer's.
        await AddRow(CreditNote, 880021, "Failed", minutesAgo: 30);
        _device.Holds(ReceiptType.CreditNote, 880021, receiptGlobalNo: 4021, signedAt: _start.UtcDateTime.AddDays(-60));
        // Signed a few hours "before" its first row only by the device's clock: still its own.
        await AddRow(CreditNote, 880022, "Failed", minutesAgo: 30);
        _device.Holds(ReceiptType.CreditNote, 880022, receiptGlobalNo: 9122, signedAt: _start.UtcDateTime.AddHours(-3));

        var result = await Pass();

        Assert.Equal(1, result.OlderReceipt);
        Assert.Equal(1, result.PickedUp);
        Assert.Single(await Rows(CreditNote, 880021));
        Assert.Equal(9122, (await Latest(CreditNote, 880022)).ReceiptGlobalNo);
    }

    [Fact]
    public async Task Not_there_yet_writes_nothing_and_the_document_is_asked_again_after_its_back_off()
    {
        await AddRow(Invoice, 790021, "Failed", minutesAgo: 30);

        var first = await Pass();

        Assert.Equal(1, first.NotYet);
        // No row: the latest is still the background attempt's failure, which is what the retry pass reads.
        Assert.Single(await Rows(Invoice, 790021));
        Assert.Equal([790021], (await RetryCandidates()).Select(candidate => candidate.DocNum));

        // Inside the 10-minute wait after one miss: not asked.
        _device.Asked.Clear();
        await Pass(minutesFromNow: 9);
        Assert.Empty(_device.Asked);

        // After it, and the device now holds the receipt.
        _device.Holds(ReceiptType.FiscalInvoice, 790021, receiptGlobalNo: 6221);
        var later = await Pass(minutesFromNow: 11);
        Assert.Equal(1, later.PickedUp);
        Assert.Equal(6221, (await Latest(Invoice, 790021)).ReceiptGlobalNo);
    }

    [Fact]
    public async Task A_fresh_failure_waits_before_its_first_look_up()
    {
        await AddRow(Invoice, 790031, "Failed", minutesAgo: 2);
        await AddRow(CreditNote, 880031, "Failed", minutesAgo: 2);

        Assert.Equal(0, (await Pass()).Total);
        Assert.Empty(_device.Asked);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 60)]
    [InlineData(10, 60)]
    public void The_wait_doubles_with_each_miss_up_to_an_hour(int misses, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), new FiscalReceiptPickupSettings().LookupDelay(misses));

    [Fact]
    public async Task When_the_device_cannot_be_asked_nothing_is_recorded_and_the_pass_stops()
    {
        await AddRow(Invoice, 790041, "Failed", minutesAgo: 40);
        await AddRow(Invoice, 790042, "Failed", minutesAgo: 30);
        await AddRow(CreditNote, 880041, "Failed", minutesAgo: 30);
        _device.Unreachable = true;

        var result = await Pass();

        Assert.NotNull(result.StoppedBecause);
        // One request, and the credit notes are not tried after it.
        Assert.Equal([(ReceiptType.FiscalInvoice, 790041)], _device.Asked);
        Assert.Single(await Rows(Invoice, 790041));
        Assert.Single(await Rows(Invoice, 790042));
        Assert.Single(await Rows(CreditNote, 880041));
    }

    [Fact]
    public async Task Documents_that_hold_a_receipt_or_were_filed_under_another_number_are_never_asked_about()
    {
        // A receipt already recorded.
        await AddRow(Invoice, 790051, "Failed", minutesAgo: 90);
        await AddRow(Invoice, 790051, "Success", minutesAgo: 60, receiptGlobalNo: 6251);
        // Reposted after the SAP update: fiscalised under its old number.
        await AddRow(Invoice, 790052, "Failed", minutesAgo: 30, reposted: true);
        // Posted before its till sale's receipt: signed under the sale's reference.
        await AddRow(Invoice, 790053, "Failed", minutesAgo: 30);
        await AddReceiptPendingSale(790053);
        // Latest row says neither "Failed" nor "Not Fiscalised".
        await AddRow(Invoice, 790054, "Failed", minutesAgo: 90);
        await AddRow(Invoice, 790054, "Pending", minutesAgo: 60);
        // Outside the look-back window.
        await AddRow(Invoice, 790055, "Failed", minutesAgo: 15 * 24 * 60);
        // A credit note already fiscalised.
        await AddRow(CreditNote, 880051, "Not Fiscalised", minutesAgo: 90);
        await AddRow(CreditNote, 880051, "Fiscalised", minutesAgo: 60, receiptGlobalNo: 9151);
        // A credit note a till filed before SAP, under the credit's own number.
        await AddRow(CreditNote, 880052, "Failed", minutesAgo: 30);
        await AddTillCreditOf(880052);

        Assert.Equal(0, (await Pass()).Total);
        Assert.Empty(_device.Asked);
    }

    [Fact]
    public async Task Nothing_is_asked_while_the_pass_or_the_provider_is_switched_off()
    {
        await AddRow(Invoice, 790061, "Failed", minutesAgo: 30);
        _device.Holds(ReceiptType.FiscalInvoice, 790061, receiptGlobalNo: 6261);

        var off = await Handler(new FiscalReceiptPickupSettings { Enabled = false })
            .Handle(new PickUpFailedFiscalReceiptsCommand(), CancellationToken.None);
        var providerOff = await Handler(fiscalisation: new FiscalisationSettings
            {
                Provider = FiscalisationProvider.Platform,
                Enabled = false
            })
            .Handle(new PickUpFailedFiscalReceiptsCommand(), CancellationToken.None);

        Assert.Equal(0, off.Value.Total);
        Assert.Equal(0, providerOff.Value.Total);
        Assert.Empty(_device.Asked);
    }

    /// <summary>One pass, as if run <paramref name="minutesFromNow"/> minutes after the test began.</summary>
    private async Task<PickUpFailedFiscalReceiptsResult> Pass(int minutesFromNow = 0)
    {
        _clock.Now = _start.AddMinutes(minutesFromNow);
        var result = await Handler().Handle(new PickUpFailedFiscalReceiptsCommand(), CancellationToken.None);
        Assert.False(result.IsError);
        return result.Value;
    }

    private PickUpFailedFiscalReceiptsHandler Handler(
        FiscalReceiptPickupSettings? settings = null,
        FiscalisationSettings? fiscalisation = null)
        => new(
            _context,
            Sender(),
            _device,
            _clock,
            _schedule,
            Options.Create(settings ?? new FiscalReceiptPickupSettings()),
            Options.Create(fiscalisation ?? new FiscalisationSettings { Provider = FiscalisationProvider.Platform }),
            Options.Create(new RevmaxSettings()),
            NullLogger<PickUpFailedFiscalReceiptsHandler>.Instance);

    private Task<List<InvoiceFiscalisationRetryCandidate>> RetryCandidates()
        => new RetryFailedInvoiceFiscalisationsHandler(
                _context,
                Sender(),
                new InvoiceFiscalisationRetryHolds(),
                Options.Create(new InvoiceFiscalisationRetrySettings()),
                Options.Create(new FiscalisationSettings { Provider = FiscalisationProvider.Platform }),
                Options.Create(new RevmaxSettings()),
                NullLogger<RetryFailedInvoiceFiscalisationsHandler>.Instance)
            .FindCandidatesAsync(DateTime.UtcNow.AddHours(2), CancellationToken.None);

    /// <summary>The transaction log real, in a context of its own, as the real sender's scope would give it.</summary>
    private ISender Sender()
        => StubProxy.For<ISender>((method, args) => args?[0] switch
        {
            SyncFiscalTransactionCommand command => Record(command),
            var request => throw new InvalidOperationException(
                $"ISender.{method.Name} was called with {request?.GetType().Name ?? "null"}; the pick-up reads nothing from SAP.")
        });

    private async Task<ErrorOr<FiscalTransactionLogItemDto>> Record(SyncFiscalTransactionCommand command)
    {
        await using var context = new ApplicationDbContext(_options);
        return await new SyncFiscalTransactionHandler(context, NullLogger<SyncFiscalTransactionHandler>.Instance)
            .Handle(command, CancellationToken.None);
    }

    private async Task AddRow(
        string documentType,
        int docNum,
        string status,
        int minutesAgo,
        string source = Background,
        string? message = null,
        int? receiptGlobalNo = null,
        bool reposted = false)
    {
        var at = _start.UtcDateTime.AddMinutes(-minutesAgo);
        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = Guid.NewGuid().ToString("N"),
            TimestampUtc = at,
            CreatedAtUtc = at,
            LastSyncedAtUtc = at,
            DocumentType = documentType,
            DocNum = docNum,
            Status = status,
            Message = message,
            ReceiptGlobalNo = receiptGlobalNo,
            CardCode = "C001",
            CardName = "Customer",
            DocTotal = 10m,
            VatSum = 1.3m,
            Currency = "USD",
            SourceSystem = source,
            RepostedAfterSapUpdate = reposted
        });
        await _context.SaveChangesAsync();
    }

    private async Task AddReceiptPendingSale(int sapDocNum)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"KEF-FAC-{sapDocNum}",
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = "CIS006",
            DocDate = DateTime.UtcNow.Date,
            CreatedAt = DateTime.UtcNow,
            TotalAmount = 10.03m,
            VatAmount = 1.35m,
            Currency = "USD",
            WarehouseCode = "KEFSHOP",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Failed,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            PaymentMethod = "Cash",
            AmountPaid = 0m,
            SapDocNum = sapDocNum,
            PostedBeforeReceiptAt = DateTime.UtcNow.AddHours(-1)
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>A counter credit fiscalised under its own DCN- number, whose SAP credit memo is <paramref name="sapDocNum"/>.</summary>
    private async Task AddTillCreditOf(int sapDocNum)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = $"KEF-FAC-C{sapDocNum}",
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = "CIS006",
            DocDate = DateTime.UtcNow.Date,
            CreatedAt = DateTime.UtcNow,
            TotalAmount = 10m,
            VatAmount = 1.3m,
            Currency = "USD",
            WarehouseCode = "KEFSHOP",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            PaymentMethod = "Cash",
            AmountPaid = 10m
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
            Reason = "Returned",
            Currency = "USD",
            Amount = 10m,
            Status = DesktopCreditStatuses.Fiscalised,
            SapDocNum = sapDocNum,
            CreatedAtUtc = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
    }

    private async Task<List<DesktopFiscalTransactionEntity>> Rows(string documentType, int docNum)
    {
        await using var context = new ApplicationDbContext(_options);
        return await context.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(row => row.DocumentType == documentType && row.DocNum == docNum)
            .ToListAsync();
    }

    private async Task<DesktopFiscalTransactionEntity> Latest(string documentType, int docNum)
        => (await Rows(documentType, docNum))
            .OrderByDescending(row => row.LastSyncedAtUtc)
            .ThenByDescending(row => row.Id)
            .First();

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The fiscal device: answers from what it has been told it holds, by receipt type and number.</summary>
    private sealed class StubDevice : IFiscalReceiptReader
    {
        private readonly Dictionary<(ReceiptType, int), (int Receipt, DateTime SignedAt)> _receipts = [];

        public List<(ReceiptType Type, int DocNum)> Asked { get; } = [];

        public bool Unreachable { get; set; }

        public void Holds(ReceiptType type, int docNum, int receiptGlobalNo, DateTime? signedAt = null)
            => _receipts[(type, docNum)] = (receiptGlobalNo, signedAt ?? DateTime.UtcNow.AddMinutes(-20));

        public Task<FiscalReceiptSnapshot?> TryLookupAsync(
            int docNum,
            ReceiptType receiptType,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            Asked.Add((receiptType, docNum));

            if (Unreachable)
            {
                return Task.FromResult<FiscalReceiptSnapshot?>(null);
            }

            return Task.FromResult<FiscalReceiptSnapshot?>(_receipts.TryGetValue((receiptType, docNum), out var held)
                ? new FiscalReceiptSnapshot(
                    IsFiscalised: true,
                    ReceiptGlobalNo: held.Receipt,
                    QrCode: $"https://fdms.example/qr/{held.Receipt}",
                    VerificationCode: "ABCD-1234",
                    DeviceSerialNumber: "SN-1",
                    DeviceId: "28479",
                    FiscalDay: "112",
                    TimestampUtc: held.SignedAt,
                    RawResponseJson: "{}")
                : new FiscalReceiptSnapshot(false, null, null, null, null, null, null, DateTime.UtcNow, "{}"));
        }
    }
}
