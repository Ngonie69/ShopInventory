using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;
using ShopInventory.Features.Invoices.Commands.RetryFailedInvoiceFiscalisations;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocEntry;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocNum;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The scheduled pass that files again the SAP invoices whose fiscalisation failed — after 2026-10-17 and -18,
/// every invoice raised while ZIMRA was unreachable.
/// </summary>
/// <remarks>
/// It runs the real fiscalise route and the real fiscal transaction log underneath, with only SAP and the
/// device stubbed, because what the next pass takes is read back from the rows this one wrote.
/// </remarks>
public sealed class InvoiceFiscalisationRetryTests : IDisposable
{
    private const string Background = InvoiceFiscalizationBackgroundService.SourceSystem;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly InvoiceFiscalisationRetryHolds _holds = new();

    /// <summary>What the stubbed device answers, in order; the last one repeats.</summary>
    private readonly Queue<FiscalizationResult> _answers = new();
    private readonly List<int> _filed = [];
    private readonly List<FiscalizeInvoiceCommand> _routed = [];
    private Func<int, ErrorOr<InvoiceDto>> _sap = docNum => Invoice(docNum);
    private Func<FiscalizeInvoiceCommand, ErrorOr<FiscalizationResult>?> _routeOverride = _ => null;

    public InvoiceFiscalisationRetryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_failed_invoice_is_filed_again_through_the_fiscalise_route_and_recorded_as_the_retry()
    {
        await AddRow(787001, "Failed", minutesAgo: 10, message: "FDMS_UNREACHABLE_NOT_SIGNED: ZIMRA did not answer.");
        _answers.Enqueue(new FiscalizationResult { Success = true, ReceiptGlobalNo = "5120", Message = "Fiscalised." });

        var result = await Pass();

        Assert.Equal(1, result.Fiscalised);
        Assert.Null(result.StoppedBecause);
        Assert.Equal([787001], _filed);
        Assert.Equal(RetryFailedInvoiceFiscalisationsHandler.SourceSystem, Assert.Single(_routed).SourceSystem);

        var latest = await Latest(787001);
        Assert.Equal("Success", latest.Status);
        Assert.Equal(5120, latest.ReceiptGlobalNo);
        Assert.Equal(RetryFailedInvoiceFiscalisationsHandler.SourceSystem, latest.SourceSystem);

        // Settled: the next pass has nothing to do.
        Assert.Empty(await Candidates(DateTime.UtcNow.AddHours(2)));
    }

    [Fact]
    public async Task Invoices_that_hold_a_receipt_or_must_not_be_sent_are_left_alone()
    {
        await AddRow(1, "Failed", minutesAgo: 1);                                     // still waiting out 2 minutes
        await AddRow(2, "Failed", minutesAgo: 30);
        await AddRow(2, "Success", minutesAgo: 20, receiptGlobalNo: 4411);            // filed since
        await AddRow(3, "Failed", minutesAgo: 30,
            message: "The fiscal outcome is unresolved. Check the receipt on the fiscalisation console first.");
        await AddRow(4, "Failed", minutesAgo: 30, reposted: true);                    // fiscalised under its old number
        await AddRow(5, "Failed", minutesAgo: 30, source: "DesktopSync");             // not a fiscalise route's row
        await AddRow(6, "Failed", minutesAgo: 8 * 24 * 60);                           // first failed outside the window
        await AddRow(6, "Failed", minutesAgo: 90, source: RetryFailedInvoiceFiscalisationsHandler.SourceSystem);
        await AddRow(7, "Failed", minutesAgo: 60);
        await AddRow(7, "Not Fiscalised", minutesAgo: 30, source: "InvoiceStatusSync");
        await AddRow(8, "Failed", minutesAgo: 30);
        await AddReceiptPendingSale(sapDocNum: 8);                                    // to be signed as its sale
        await AddRow(9, "Failed", minutesAgo: 30);
        _holds.Hold(9, DateTime.UtcNow.AddMinutes(30));                               // refused by the route earlier
        await AddRow(10, "Failed", minutesAgo: 20);
        await AddRow(10, "Failed", minutesAgo: 12, source: RetryFailedInvoiceFiscalisationsHandler.SourceSystem);
        await AddRow(10, "Failed", minutesAgo: 5, source: RetryFailedInvoiceFiscalisationsHandler.SourceSystem);
        await AddRow(11, "Failed", minutesAgo: 25);
        await AddRow(11, "Failed", minutesAgo: 15, source: RetryFailedInvoiceFiscalisationsHandler.SourceSystem);
        await AddRow(11, "Failed", minutesAgo: 9, source: RetryFailedInvoiceFiscalisationsHandler.SourceSystem);
        await AddRow(12, "Failed", minutesAgo: 2 * 24 * 60, source: FiscalizeInvoiceHandler.SourceSystem);

        var candidates = await Candidates(DateTime.UtcNow);

        // 10 has failed three times and waits 8 minutes after the last; 11 has waited them. Earliest failure first.
        Assert.Equal([12, 11], candidates.Select(candidate => candidate.DocNum));
        Assert.Equal(3, candidates[1].Failures);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(6, 60)]
    [InlineData(40, 60)]
    public void Each_failure_doubles_the_wait_up_to_an_hour(int failures, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), new InvoiceFiscalisationRetrySettings().RetryDelay(failures));

    [Fact]
    public async Task The_pass_stops_at_the_first_failure_and_that_invoice_waits_out_its_back_off()
    {
        await AddRow(787101, "Failed", minutesAgo: 40);
        await AddRow(787102, "Failed", minutesAgo: 30);
        _answers.Enqueue(new FiscalizationResult
        {
            Success = false,
            ErrorCode = "DownstreamRequestFailed",
            Message = "FDMS_UNREACHABLE_NOT_SIGNED: ZIMRA did not answer."
        });

        var result = await Pass();

        Assert.Equal(1, result.Failed);
        Assert.Contains("787101", result.StoppedBecause);
        Assert.Equal([787101], _filed);

        // Two failures now: 787101 waits 4 minutes, and the next pass starts with 787102.
        var next = await Candidates(DateTime.UtcNow.AddMinutes(1));
        Assert.Equal([787102], next.Select(candidate => candidate.DocNum));
        Assert.Equal(
            [787101, 787102],
            (await Candidates(DateTime.UtcNow.AddMinutes(5))).Select(candidate => candidate.DocNum));
    }

    [Fact]
    public async Task An_outcome_nobody_established_is_recorded_unresolved_and_never_sent_again()
    {
        await AddRow(787201, "Failed", minutesAgo: 30);
        await AddRow(787202, "Failed", minutesAgo: 20);
        _answers.Enqueue(new FiscalizationResult
        {
            Success = false,
            RequiresReconciliation = true,
            ErrorCode = "FdmsOperationIndeterminate",
            Message = "SubmitReceipt was sent and not answered."
        });

        var result = await Pass();

        Assert.Contains("reconciling", result.StoppedBecause);
        Assert.Equal([787201], _filed);
        Assert.True(FiscalOutcomeMessages.IsUnresolved((await Latest(787201)).Message));
        Assert.Equal(
            [787202],
            (await Candidates(DateTime.UtcNow.AddDays(1))).Select(candidate => candidate.DocNum));
    }

    [Fact]
    public async Task An_invoice_the_route_refuses_is_held_and_the_pass_carries_on()
    {
        await AddRow(787301, "Failed", minutesAgo: 30);
        await AddRow(787302, "Failed", minutesAgo: 20);
        _routeOverride = command => command.DocEntry == DocEntryOf(787301)
            ? (ErrorOr<FiscalizationResult>?)Errors.Invoice.RepostedAfterSapUpdate(787301, "781999")
            : null;
        _answers.Enqueue(new FiscalizationResult { Success = true, ReceiptGlobalNo = "5121" });

        var result = await Pass();

        Assert.Equal(1, result.Refused);
        Assert.Equal(1, result.Fiscalised);
        Assert.Equal([787302], _filed);
        Assert.Empty(await Candidates(DateTime.UtcNow.AddMinutes(30)));
        Assert.Equal(
            [787301],
            (await Candidates(DateTime.UtcNow.AddMinutes(61))).Select(candidate => candidate.DocNum));
    }

    [Fact]
    public async Task When_SAP_cannot_be_read_nothing_is_filed_and_the_pass_stops()
    {
        await AddRow(787401, "Failed", minutesAgo: 30);
        await AddRow(787402, "Failed", minutesAgo: 20);
        _sap = _ => Errors.Invoice.SapTimeout;

        var result = await Pass();

        Assert.Contains("SAP", result.StoppedBecause);
        Assert.Empty(_filed);
        Assert.Equal(2, (await Candidates(DateTime.UtcNow)).Count);
    }

    [Fact]
    public async Task Nothing_is_sent_while_the_pass_or_the_provider_is_switched_off()
    {
        await AddRow(787501, "Failed", minutesAgo: 30);

        var off = await Handler(new InvoiceFiscalisationRetrySettings { Enabled = false })
            .Handle(new RetryFailedInvoiceFiscalisationsCommand(), CancellationToken.None);
        var providerOff = await Handler(fiscalisation: new FiscalisationSettings
            {
                Provider = FiscalisationProvider.Platform,
                Enabled = false
            })
            .Handle(new RetryFailedInvoiceFiscalisationsCommand(), CancellationToken.None);

        Assert.Equal(0, off.Value.Total);
        Assert.Equal(0, providerOff.Value.Total);
        Assert.Empty(_filed);
    }

    private async Task<RetryFailedInvoiceFiscalisationsResult> Pass()
    {
        var result = await Handler().Handle(new RetryFailedInvoiceFiscalisationsCommand(), CancellationToken.None);
        Assert.False(result.IsError);
        return result.Value;
    }

    private Task<List<InvoiceFiscalisationRetryCandidate>> Candidates(DateTime nowUtc)
        => Handler().FindCandidatesAsync(nowUtc, CancellationToken.None);

    private RetryFailedInvoiceFiscalisationsHandler Handler(
        InvoiceFiscalisationRetrySettings? settings = null,
        FiscalisationSettings? fiscalisation = null)
        => new(
            _context,
            Sender(),
            _holds,
            Options.Create(settings ?? new InvoiceFiscalisationRetrySettings()),
            Options.Create(fiscalisation ?? new FiscalisationSettings { Provider = FiscalisationProvider.Platform }),
            Options.Create(new RevmaxSettings()),
            NullLogger<RetryFailedInvoiceFiscalisationsHandler>.Instance);

    /// <summary>
    /// SAP and the device stubbed; the fiscalise route and the transaction log real, each in a context of its
    /// own, as the real sender's scope would give them.
    /// </summary>
    private ISender Sender()
        => StubProxy.For<ISender>((method, args) => args?[0] switch
        {
            GetInvoiceByDocNumQuery query => Task.FromResult(_sap(query.DocNum)),
            GetInvoiceByDocEntryQuery query => Task.FromResult<ErrorOr<InvoiceDto>>(Invoice(query.DocEntry - 1_000_000)),
            FiscalizeInvoiceCommand command => Route(command),
            SyncFiscalTransactionCommand command => Record(command),
            var request => throw new InvalidOperationException(
                $"ISender.{method.Name} was called with {request?.GetType().Name ?? "null"}.")
        });

    private async Task<ErrorOr<FiscalizationResult>> Route(FiscalizeInvoiceCommand command)
    {
        _routed.Add(command);

        if (_routeOverride(command) is { } overridden)
        {
            return overridden;
        }

        await using var context = new ApplicationDbContext(_options);
        return await new FiscalizeInvoiceHandler(
                context,
                Sender(),
                StubProxy.For<IFiscalizationService>((method, args) => method.Name switch
                {
                    nameof(IFiscalizationService.FiscalizeInvoiceAsync) => File((InvoiceDto)args![0]!),
                    _ => throw new InvalidOperationException($"IFiscalizationService.{method.Name} was not expected.")
                }),
                StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
                Options.Create(new FiscalisationSettings()),
                NullLogger<FiscalizeInvoiceHandler>.Instance)
            .Handle(command, CancellationToken.None);
    }

    private Task<FiscalizationResult> File(InvoiceDto invoice)
    {
        _filed.Add(invoice.DocNum);
        var answer = _answers.Count > 1 ? _answers.Dequeue() : _answers.Peek();
        return Task.FromResult(answer);
    }

    private async Task<ErrorOr<FiscalTransactionLogItemDto>> Record(SyncFiscalTransactionCommand command)
    {
        await using var context = new ApplicationDbContext(_options);
        return await new SyncFiscalTransactionHandler(context, NullLogger<SyncFiscalTransactionHandler>.Instance)
            .Handle(command, CancellationToken.None);
    }

    private static int DocEntryOf(int docNum) => docNum + 1_000_000;

    private static InvoiceDto Invoice(int docNum) => new()
    {
        DocEntry = DocEntryOf(docNum),
        DocNum = docNum,
        CardCode = "C001",
        CardName = "Customer",
        DocTotal = 10m,
        DocCurrency = "USD"
    };

    private async Task AddRow(
        int docNum,
        string status,
        int minutesAgo,
        string source = Background,
        string? message = null,
        int? receiptGlobalNo = null,
        bool reposted = false)
    {
        var at = DateTime.UtcNow.AddMinutes(-minutesAgo);
        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = Guid.NewGuid().ToString("N"),
            TimestampUtc = at,
            CreatedAtUtc = at,
            LastSyncedAtUtc = at,
            DocumentType = "Invoice",
            DocNum = docNum,
            Status = status,
            Message = message,
            ReceiptGlobalNo = receiptGlobalNo,
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

    private async Task<DesktopFiscalTransactionEntity> Latest(int docNum)
    {
        await using var context = new ApplicationDbContext(_options);
        return await context.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(row => row.DocNum == docNum)
            .OrderByDescending(row => row.LastSyncedAtUtc)
            .ThenByDescending(row => row.Id)
            .FirstAsync();
    }
}
