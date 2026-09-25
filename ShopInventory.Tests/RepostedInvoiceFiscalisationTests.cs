using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Features.FiscalisationConfiguration.Commands.FlagRepostedFiscalTransactions;
using ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocEntry;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the guard against fiscalising an invoice reposted after the SAP Business One update.
/// </summary>
/// <remarks>
/// Those invoices were fiscalised under their old numbers before the update and reposted under new
/// ones by the back-posting tool, which starts their remarks with a fixed marker. Nothing else under
/// the new DocNum says they were fiscalised, so the fiscalise route must read the marker itself.
/// </remarks>
public sealed class RepostedInvoiceFiscalisationTests : IDisposable
{
    // What the back-posting tool writes: its RemarksTemplate, then the invoice's original remarks.
    private const string RepostedComments = "Invoice posted from SAP update. Old invoice 777418. Shop till | Ref MCH-1";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<int> _submitted = [];
    private readonly List<SyncFiscalTransactionCommand> _synced = [];

    public RepostedInvoiceFiscalisationTests()
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

    [Fact]
    public async Task A_reposted_invoice_is_refused_before_anything_reaches_the_fiscal_device()
    {
        var result = await Fiscalise(new InvoiceDto { DocEntry = 901, DocNum = 812001, Comments = RepostedComments });

        Assert.True(result.IsError);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Invoice.RepostedAfterSapUpdate", error.Code);
        // A conflict is a 409, which the web reads as "refused, nothing was submitted".
        Assert.Equal(ErrorType.Conflict, error.Type);
        Assert.Contains("812001", error.Description);
        Assert.Contains("(777418)", error.Description);
        Assert.Contains("not fiscalised again", error.Description);

        Assert.Empty(_submitted);
        Assert.Empty(_synced);
    }

    /// <summary>
    /// The refusal wins over the already-fiscalised shortcut too, so the operator is told why rather
    /// than shown a bare skip.
    /// </summary>
    [Fact]
    public async Task A_reposted_invoice_is_refused_even_when_its_new_number_reads_as_fiscalised()
    {
        var result = await Fiscalise(new InvoiceDto
        {
            DocEntry = 902,
            DocNum = 812002,
            Comments = RepostedComments,
            FiscalizationStatus = "Fiscalised"
        });

        Assert.Equal("Invoice.RepostedAfterSapUpdate", Assert.Single(result.Errors).Code);
        Assert.Empty(_submitted);
    }

    [Fact]
    public async Task An_ordinary_invoice_still_reaches_the_fiscal_device()
    {
        var result = await Fiscalise(new InvoiceDto
        {
            DocEntry = 903,
            DocNum = 812003,
            // Mentioning the marker is not starting with it.
            Comments = "Customer asked about the invoice posted from SAP update. Old invoice 777418."
        });

        Assert.False(result.IsError);
        Assert.Equal([812003], _submitted);
    }

    [Fact]
    public async Task The_marker_is_read_from_configuration()
    {
        var settings = new FiscalisationSettings { RepostedInvoiceCommentsPrefix = "Reposted:" };

        var refused = await Fiscalise(
            new InvoiceDto { DocEntry = 904, DocNum = 812004, Comments = "Reposted: old invoice 1." },
            settings);
        var allowed = await Fiscalise(
            new InvoiceDto { DocEntry = 905, DocNum = 812005, Comments = RepostedComments },
            settings);

        Assert.Equal("Invoice.RepostedAfterSapUpdate", Assert.Single(refused.Errors).Code);
        Assert.False(allowed.IsError);
        Assert.Equal([812005], _submitted);
    }

    [Theory]
    [InlineData(RepostedComments, true)]
    [InlineData("  invoice POSTED from sap update. Old invoice 1.", true)]
    [InlineData("Invoice posted from SAP update.", true)]
    [InlineData("Invoice posted from SAP", false)]
    [InlineData("Shop till | Ref MCH-1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_marker_matches_only_the_start_of_the_remarks(string? comments, bool expected)
        => Assert.Equal(expected, RepostedInvoiceMarker.IsReposted(new FiscalisationSettings(), comments));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_marker_switches_the_guard_off_rather_than_matching_everything(string prefix)
        => Assert.False(RepostedInvoiceMarker.IsReposted(
            new FiscalisationSettings { RepostedInvoiceCommentsPrefix = prefix },
            RepostedComments));

    [Theory]
    [InlineData(RepostedComments, "777418")]
    [InlineData("Invoice posted from SAP update. Old invoice #5.", "5")]
    [InlineData("Invoice posted from SAP update.", null)]
    [InlineData(null, null)]
    public void The_old_number_is_read_from_the_remarks(string? comments, string? expected)
        => Assert.Equal(expected, RepostedInvoiceMarker.OldInvoiceNumber(comments));

    // ── The fiscal transaction log's RepostedAfterSapUpdate ─────────────────

    [Fact]
    public async Task The_read_back_records_a_repost_on_the_row_it_writes()
    {
        var reader = StubProxy.For<IFiscalReceiptReader>((_, _) => Task.FromResult<FiscalReceiptSnapshot?>(
            new FiscalReceiptSnapshot(false, null, null, null, null, null, null, DateTime.UtcNow, null)));
        var sender = StubProxy.For<ISender>((_, args) =>
            new SyncFiscalTransactionHandler(_context, NullLogger<SyncFiscalTransactionHandler>.Instance)
                .Handle((SyncFiscalTransactionCommand)args![0]!, CancellationToken.None));

        foreach (var invoice in new[]
                 {
                     new InvoiceDto { DocEntry = 1, DocNum = 812001, Comments = RepostedComments },
                     new InvoiceDto { DocEntry = 2, DocNum = 812002, Comments = "Shop till | Ref MCH-2" }
                 })
        {
            Assert.True(await InvoiceFiscalTransactionSync.SyncAsync(
                invoice, reader, sender, new FiscalisationSettings(), NullLogger.Instance, CancellationToken.None));
        }

        var rows = await _context.DesktopFiscalTransactions.AsNoTracking()
            .OrderBy(row => row.DocNum)
            .Select(row => new { row.DocNum, row.Status, row.RepostedAfterSapUpdate })
            .ToListAsync();

        Assert.Equal(
            new[]
            {
                new { DocNum = 812001, Status = "Not Fiscalised", RepostedAfterSapUpdate = true },
                new { DocNum = 812002, Status = "Not Fiscalised", RepostedAfterSapUpdate = false }
            },
            rows);
    }

    [Fact]
    public async Task The_one_off_pass_flags_only_reposts_read_back_as_not_fiscalised_since_the_update()
    {
        var inWindow = new DateTime(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc);
        await SeedLogRowAsync(812001, "Not Fiscalised", inWindow);              // a repost: flagged
        await SeedLogRowAsync(812002, "Not Fiscalised", inWindow);              // ordinary: left
        await SeedLogRowAsync(812003, "Failed", inWindow);                      // a repost someone sent: left
        await SeedLogRowAsync(700001, "Not Fiscalised", new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc));
        await SeedLogRowAsync(812004, "Not Fiscalised", inWindow, reposted: true);

        var asked = new List<int>();
        var sapClient = SapClient(docNums =>
        {
            asked.AddRange(docNums);
            return docNums.Select(docNum => SapInvoice(docNum, docNum == 812002 ? "Shop till | Ref MCH-2" : RepostedComments)).ToList();
        });

        var first = await Sweep(sapClient);

        Assert.Equal(new FlagRepostedFiscalTransactionsResult(Ran: true, InvoicesChecked: 2, RowsFlagged: 1), first.Value);
        Assert.Equal([812001, 812002], asked);
        Assert.Equal([812001, 812004], await FlaggedDocNumsAsync());

        // Idempotent: a flagged row is not read again, so a second node start re-reads only the ordinary one.
        asked.Clear();
        var second = await Sweep(sapClient);

        Assert.Equal(new FlagRepostedFiscalTransactionsResult(Ran: true, InvoicesChecked: 1, RowsFlagged: 0), second.Value);
        Assert.Equal([812002], asked);
    }

    [Fact]
    public async Task A_SAP_failure_keeps_what_the_pass_flagged_before_it()
    {
        var inWindow = new DateTime(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc);
        for (var docNum = 812001; docNum <= 812051; docNum++)
        {
            await SeedLogRowAsync(docNum, "Not Fiscalised", inWindow);
        }

        var calls = 0;
        var result = await Sweep(SapClient(docNums => ++calls == 1
            ? docNums.Select(docNum => SapInvoice(docNum, RepostedComments)).ToList()
            : throw new HttpRequestException("Service Layer unreachable")));

        Assert.True(result.IsError);
        Assert.Equal(50, (await FlaggedDocNumsAsync()).Count);
    }

    [Theory]
    [InlineData("no start date")]
    [InlineData("no marker")]
    public async Task The_one_off_pass_can_be_switched_off(string how)
    {
        await SeedLogRowAsync(812001, "Not Fiscalised", new DateTime(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc));

        var settings = how == "no start date"
            ? new FiscalisationSettings { RepostedInvoiceSweepSinceUtc = null }
            : new FiscalisationSettings { RepostedInvoiceCommentsPrefix = "" };

        var calls = 0;
        var result = await Sweep(
            SapClient(docNums =>
            {
                calls++;
                return docNums.Select(docNum => SapInvoice(docNum, RepostedComments)).ToList();
            }),
            settings);

        Assert.False(result.Value.Ran);
        Assert.Equal(0, calls);
        Assert.Empty(await FlaggedDocNumsAsync());
    }

    private Task<ErrorOr<FlagRepostedFiscalTransactionsResult>> Sweep(
        ISAPServiceLayerClient sapClient,
        FiscalisationSettings? settings = null)
        => new FlagRepostedFiscalTransactionsHandler(
                _context,
                sapClient,
                Options.Create(new SAPSettings { Enabled = true }),
                Options.Create(settings ?? new FiscalisationSettings()),
                NullLogger<FlagRepostedFiscalTransactionsHandler>.Instance)
            .Handle(new FlagRepostedFiscalTransactionsCommand(), CancellationToken.None);

    private static ISAPServiceLayerClient SapClient(Func<IReadOnlyList<int>, List<Models.Invoice>> invoices)
        => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoicesByDocNumsAsync) =>
                Task.FromResult(invoices(((IEnumerable<int>)args![0]!).ToList())),
            _ => throw new InvalidOperationException($"ISAPServiceLayerClient.{method.Name} was not expected.")
        });

    private static Models.Invoice SapInvoice(int docNum, string comments) =>
        new() { DocEntry = docNum - 800000, DocNum = docNum, Comments = comments };

    private async Task SeedLogRowAsync(int docNum, string status, DateTime syncedAt, bool reposted = false)
    {
        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = $"invoice-status-backfill-{docNum}-{status}",
            DocumentType = "Invoice",
            DocNum = docNum,
            Status = status,
            TimestampUtc = syncedAt,
            LastSyncedAtUtc = syncedAt,
            RepostedAfterSapUpdate = reposted
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Task<List<int>> FlaggedDocNumsAsync()
        => _context.DesktopFiscalTransactions.AsNoTracking()
            .Where(row => row.RepostedAfterSapUpdate)
            .Select(row => row.DocNum)
            .OrderBy(docNum => docNum)
            .ToListAsync();

    private Task<ErrorOr<FiscalizationResult>> Fiscalise(InvoiceDto invoice, FiscalisationSettings? settings = null)
        => new FiscalizeInvoiceHandler(
                _context,
                SenderFor(invoice),
                StubProxy.For<IFiscalizationService>((method, args) => method.Name switch
                {
                    nameof(IFiscalizationService.FiscalizeInvoiceAsync) => Submit((InvoiceDto)args![0]!),
                    _ => throw new InvalidOperationException($"IFiscalizationService.{method.Name} was not expected.")
                }),
                StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
                Options.Create(settings ?? new FiscalisationSettings()),
                NullLogger<FiscalizeInvoiceHandler>.Instance)
            .Handle(new FiscalizeInvoiceCommand(invoice.DocEntry, null, "operator"), CancellationToken.None);

    private Task<FiscalizationResult> Submit(InvoiceDto invoice)
    {
        _submitted.Add(invoice.DocNum);
        return Task.FromResult(new FiscalizationResult { Success = true, InvoiceNumber = invoice.DocNum.ToString() });
    }

    /// <summary>
    /// Answers the invoice lookup, which would otherwise go to SAP, and records the fiscal transaction
    /// sync that follows a submission.
    /// </summary>
    private ISender SenderFor(InvoiceDto invoice)
        => StubProxy.For<ISender>((method, args) => args?[0] switch
        {
            GetInvoiceByDocEntryQuery => Task.FromResult<ErrorOr<InvoiceDto>>(invoice),
            SyncFiscalTransactionCommand command => Record(command),
            var request => throw new InvalidOperationException(
                $"ISender.{method.Name} was called with an unexpected request: {request?.GetType().Name ?? "null"}.")
        });

    private Task<ErrorOr<FiscalTransactionLogItemDto>> Record(SyncFiscalTransactionCommand command)
    {
        _synced.Add(command);
        return Task.FromResult<ErrorOr<FiscalTransactionLogItemDto>>(new FiscalTransactionLogItemDto());
    }
}
