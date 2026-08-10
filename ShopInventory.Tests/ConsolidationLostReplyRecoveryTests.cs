using System.Reflection;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;
using ShopInventory.Features.Notifications;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the consolidated invoice that reaches SAP without its reply reaching us.
/// </summary>
/// <remarks>
/// SaleConsolidations.SapDocNum is what tells every later reader that a consolidated invoice's
/// constituent sales are already with FDMS. Posting to SAP and only then writing that marker left a
/// window — a timeout, a disconnect, a cancelled token — where the invoice existed and the marker
/// did not. An invoice in that state reports "Unknown", is queued for fiscal status backfill, and
/// shows the Fiscalise button; fiscalising it submits the same sales to FDMS a second time under a
/// second invoice number, which the platform's (TIN, ReceiptType, InvoiceNo) key cannot see and
/// which cannot be reversed.
///
/// NumAtCard and U_Van_saleorder both carry CONSOL-{yyyyMMdd}-{cardCode}, so the invoice can be
/// found again without depending on anything the post returned. These tests are about that
/// recovery, and about the guarantee being structural rather than a narrow window: what asks SAP
/// the question cannot itself be cancelled by the caller who caused the problem.
/// </remarks>
public sealed class ConsolidationLostReplyRecoveryTests : IDisposable
{
    private const string CardCode = "ABS006";
    private static readonly DateTime SaleDate = new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The invoice SAP holds once the post has committed.</summary>
    private static readonly Invoice PostedInvoice = new()
    {
        DocEntry = 74120,
        DocNum = 5031,
        CardCode = CardCode,
        U_Van_saleorder = "CONSOL-20260810-ABS006"
    };

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    /// <summary>Cancelled from inside the SAP post, standing in for the client that hung up.</summary>
    private readonly CancellationTokenSource _caller = new();

    private bool _invoiceFound;
    private string? _lookupKey;
    private CancellationToken _lookupToken = new CancellationTokenSource().Token;
    private (List<int> Ids, string DocEntry, int DocNum)? _queueMarking;

    /// <summary>Set when SAP holds the invoice; cleared when the post genuinely never landed.</summary>
    private Invoice? _sapHolds = PostedInvoice;

    /// <summary>Set to make the recovery lookup itself fail.</summary>
    private Exception? _lookupFailure;

    /// <summary>Set to lose the caller while the group is still being prepared, before any post.</summary>
    private bool _cancelDuringPreparation;

    /// <summary>What the lost post reports, so a test can make it overflow the LastError column.</summary>
    private string _postFailureMessage = "The invoice was posted to SAP but the reply never arrived.";

    /// <summary>Queue-sourced sales folded into the same consolidated invoice.</summary>
    private readonly List<InvoiceQueueEntity> _queueEntries = [];

    public ConsolidationLostReplyRecoveryTests()
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
        _caller.Dispose();
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The behaviour the whole change exists for. The post committed; only the reply was lost.
    /// </summary>
    [Fact]
    public async Task A_lost_reply_still_records_the_invoice_against_the_consolidation()
    {
        await GivenAPendingSaleAsync();

        await Consolidate();

        var consolidation = await _context.SaleConsolidations.SingleAsync();
        Assert.Equal(PostedInvoice.DocEntry, consolidation.SapDocEntry);
        Assert.Equal(PostedInvoice.DocNum, consolidation.SapDocNum);
        Assert.NotNull(consolidation.PostedAt);

        // The receipts the constituents went out under are recorded too, so the marker can explain
        // itself to whoever reconciles the SAP invoice back to its parts.
        Assert.Contains("KEF-0001", consolidation.ConstituentFiscalReceipts!);
    }

    /// <summary>
    /// The marker is only worth writing if the guard can see it. This is the end of the chain the
    /// bug broke: an unmarked invoice reads "Unknown", and "Unknown" is what the backfill queue and
    /// the Fiscalise button gate on.
    /// </summary>
    [Fact]
    public async Task A_recovered_invoice_is_never_offered_for_fiscalisation()
    {
        await GivenAPendingSaleAsync();

        await Consolidate();

        var page = new List<InvoiceDto>
        {
            new() { DocNum = PostedInvoice.DocNum, DocEntry = PostedInvoice.DocEntry },
            new() { DocNum = 5099, DocEntry = 74999 }
        };

        await FiscalDocumentStatusProjector.EnrichInvoicesAsync(_context, page, CancellationToken.None);

        Assert.Equal("Fiscalised", page[0].FiscalizationStatus);
        Assert.True(page[0].IsFiscalized);

        // The ordinary invoice beside it still reads Unknown, so the guard is the recovered marker
        // and not something blanketing the page.
        Assert.Equal("Unknown", page[1].FiscalizationStatus);

        var queue = new InvoiceFiscalStatusBackfillQueue();
        Assert.Equal(1, InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill(page, queue));
        Assert.Equal(5099, (await queue.DequeueAsync(CancellationToken.None)).DocNum);
    }

    /// <summary>
    /// The guarantee has to be structural. The commonest way to lose the reply is the caller hanging
    /// up, and a lookup on the caller's token would be cancelled before it could ask the one question
    /// that decides whether a real SAP invoice goes unmarked.
    /// </summary>
    [Fact]
    public async Task The_recovery_lookup_cannot_be_cancelled_by_the_caller()
    {
        await GivenAPendingSaleAsync();

        await Consolidate();

        Assert.True(_invoiceFound);
        Assert.False(
            _lookupToken.IsCancellationRequested,
            "The recovery lookup ran on a token the disconnected caller had already cancelled.");
        Assert.False(
            _lookupToken.CanBeCanceled,
            "The recovery lookup was passed a cancellable token; the caller must not get a vote here.");
    }

    /// <summary>
    /// The key is deterministic precisely so the invoice can be found without the post's reply.
    /// </summary>
    [Fact]
    public async Task The_recovery_searches_the_consolidation_key_the_post_was_sent_under()
    {
        await GivenAPendingSaleAsync();

        await Consolidate();

        Assert.Equal($"CONSOL-20260810-{CardCode}", _lookupKey);
    }

    /// <summary>
    /// Sales are only released once their invoice is accounted for, whichever way that happened —
    /// otherwise the next run would consolidate them again into a second SAP invoice.
    /// </summary>
    [Fact]
    public async Task A_recovered_invoice_still_closes_out_its_sales_and_queue_entries()
    {
        await GivenAPendingSaleAsync();
        GivenAFiscalisedQueueEntry();

        await Consolidate();

        var consolidation = await _context.SaleConsolidations.SingleAsync();
        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, sale.ConsolidationStatus);
        Assert.Equal(consolidation.Id, sale.ConsolidationId);

        // The queue entry is marked against the recovered document, not against nothing.
        Assert.NotNull(_queueMarking);
        Assert.Equal(PostedInvoice.DocEntry.ToString(), _queueMarking!.Value.DocEntry);
        Assert.Equal(PostedInvoice.DocNum, _queueMarking.Value.DocNum);
    }

    /// <summary>
    /// Reported as PartiallyCompleted, not Posted: the invoice is real and marked, but this run never
    /// saw a reply, so the incoming payment did not run and is left for an operator. The status is
    /// deliberately not a problem for the guard, which reads the marker without filtering on it.
    /// </summary>
    [Fact]
    public async Task A_recovered_consolidation_says_the_payment_is_outstanding()
    {
        await GivenAPendingSaleAsync();

        var result = await Consolidate();

        var consolidation = await _context.SaleConsolidations.SingleAsync();
        Assert.Equal(ConsolidationStatus.PartiallyCompleted, consolidation.Status);
        Assert.Contains("payment", consolidation.LastError, StringComparison.OrdinalIgnoreCase);

        // It counts as a success for the run: the invoice exists and is accounted for.
        Assert.Equal(1, result.SuccessfulPostings);
        Assert.Equal(0, result.FailedPostings);
        Assert.Equal(PostedInvoice.DocNum, result.Groups.Single().SapDocNum);

        Assert.NotNull(
            await ConsolidatedInvoiceRegistry.FindByDocNumAsync(
                _context, PostedInvoice.DocNum, CancellationToken.None));
    }

    /// <summary>
    /// The explanation shares its SaveChanges with the marker, and LastError is capped at 2000
    /// characters. A SAP message long enough to overflow that column must not be what stops the
    /// marker being written — which would land straight back on the bug this change closes.
    /// </summary>
    [Fact]
    public async Task An_overlong_failure_message_does_not_cost_the_marker()
    {
        await GivenAPendingSaleAsync();
        _postFailureMessage = new string('E', 4000);

        await Consolidate();

        var consolidation = await _context.SaleConsolidations.SingleAsync();
        Assert.Equal(PostedInvoice.DocNum, consolidation.SapDocNum);
        Assert.True(
            consolidation.LastError!.Length <= 2000,
            $"LastError was {consolidation.LastError.Length} characters; the column holds 2000.");
    }

    /// <summary>
    /// The lookup is the arbiter, so a post that genuinely never landed must still fail. Recovering
    /// an invoice that does not exist would release the sales and leave the day unconsolidated.
    /// </summary>
    [Fact]
    public async Task A_post_that_never_reached_SAP_is_still_a_failure()
    {
        await GivenAPendingSaleAsync();
        _sapHolds = null;

        var result = await Consolidate();

        var consolidation = await _context.SaleConsolidations.SingleAsync();
        Assert.Equal(ConsolidationStatus.Failed, consolidation.Status);
        Assert.Null(consolidation.SapDocNum);
        Assert.Null(consolidation.SapDocEntry);

        Assert.Equal(0, result.SuccessfulPostings);
        Assert.Equal(1, result.FailedPostings);

        // The sales stay pending, so the next run can post them properly.
        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleConsolidationStatus.Pending, sale.ConsolidationStatus);
    }

    /// <summary>
    /// A lookup that cannot answer leaves the group exactly where it would have been without any of
    /// this, and must not bury the original error under its own.
    /// </summary>
    [Fact]
    public async Task A_lookup_that_fails_leaves_the_group_failed_and_keeps_the_original_error()
    {
        await GivenAPendingSaleAsync();
        _lookupFailure = new HttpRequestException("Service Layer unreachable");

        var result = await Consolidate();

        var consolidation = await _context.SaleConsolidations.SingleAsync();
        Assert.Equal(ConsolidationStatus.Failed, consolidation.Status);
        Assert.Null(consolidation.SapDocNum);

        Assert.Equal(1, result.FailedPostings);
        Assert.Contains("reply never arrived", result.Groups.Single().Error);
        Assert.DoesNotContain("Service Layer unreachable", result.Groups.Single().Error);
    }

    /// <summary>
    /// Cancelled while still preparing, before the post was issued: nothing reached SAP, so there is
    /// nothing to look for and no reason to spend a round-trip asking.
    /// </summary>
    [Fact]
    public async Task A_caller_who_leaves_before_the_post_is_not_recovered()
    {
        await GivenAPendingSaleAsync();
        _cancelDuringPreparation = true;

        var result = await Consolidate();

        Assert.Null(_lookupKey);
        Assert.Equal(1, result.FailedPostings);
        Assert.Equal(ConsolidationStatus.Failed, (await _context.SaleConsolidations.SingleAsync()).Status);

        // The failure was still recorded, even though the token that would have written it was gone.
        Assert.NotNull((await _context.SaleConsolidations.SingleAsync()).LastError);
    }

    private async Task<ConsolidateDailySalesResult> Consolidate()
    {
        var result = await CreateHandler().Handle(
            new ConsolidateDailySalesCommand(SaleDate), _caller.Token);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        return result.Value;
    }

    private async Task GivenAPendingSaleAsync()
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = "TILL-9001",
            SourceSystem = "KefalosDesktop",
            CardCode = CardCode,
            CardName = "Absolute Traders",
            DocDate = SaleDate,
            TotalAmount = 240.00m,
            VatAmount = 31.30m,
            AmountPaid = 240.00m,
            PaymentMethod = "Cash",
            Currency = "ZWG",
            WarehouseCode = "KEFSHOP",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            FiscalReceiptNumber = "KEF-0001",
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "NRI049",
                    Quantity = 4m,
                    UnitPrice = 60.00m,
                    LineTotal = 240.00m,
                    WarehouseCode = "KEFSHOP"
                }
            ]
        });

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// A POS sale that was fiscalised through the queue rather than the desktop path. It is folded
    /// into the same consolidated invoice, and its queue entry has to be closed out with it.
    /// </summary>
    private void GivenAFiscalisedQueueEntry() =>
        _queueEntries.Add(new InvoiceQueueEntity
        {
            Id = 77,
            ExternalReference = "POS-3312",
            SourceSystem = "KefalosPos",
            CustomerCode = CardCode,
            TotalAmount = 90.00m,
            Currency = "ZWG",
            WarehouseCode = "KEFSHOP",
            CreatedAt = SaleDate,
            FiscalReceiptNumber = "KEF-0002",
            InvoicePayload = """
                {"cardCode":"ABS006","cardName":"Absolute Traders","lines":[
                  {"lineNum":0,"itemCode":"NRI049","quantity":1.5,"unitPrice":60.00,"warehouseCode":"KEFSHOP"}]}
                """
        });

    private ConsolidateDailySalesHandler CreateHandler() =>
        new(
            _context,
            BuildSapClient(),
            BuildBatchValidation(),
            BuildQueueService(),
            StubProxy.For<INotificationService>((_, _) => Task.FromResult(new NotificationDto())),
            BuildHubContext(),
            BuildSender(),
            NullLogger<ConsolidateDailySalesHandler>.Instance);

    private ISAPServiceLayerClient BuildSapClient() =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.ValidateStockAvailabilityAsync) =>
                Task.FromResult(new List<StockValidationError>()),
            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => PostAndLoseTheReply(),
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) => RecoveryLookup(args),
            _ => throw new InvalidOperationException(
                $"ISAPServiceLayerClient.{method.Name} was not expected on this path.")
        });

    /// <summary>
    /// The document commits in SAP and the reply is lost on the way back. This is the moment the
    /// marker used to go missing.
    /// </summary>
    private Task<Invoice> PostAndLoseTheReply()
    {
        _caller.Cancel();

        throw new TaskCanceledException(_postFailureMessage);
    }

    private Task<Invoice?> RecoveryLookup(object?[]? args)
    {
        _lookupKey = (string)args![0]!;
        _lookupToken = (CancellationToken)args[1]!;

        if (_lookupFailure is not null)
        {
            throw _lookupFailure;
        }

        _invoiceFound = _sapHolds is not null;
        return Task.FromResult(_sapHolds);
    }

    private IBatchInventoryValidationService BuildBatchValidation() =>
        StubProxy.For<IBatchInventoryValidationService>((method, _) =>
        {
            if (method.Name != nameof(IBatchInventoryValidationService.ValidateAndAllocateBatchesAsync))
            {
                throw new InvalidOperationException(
                    $"IBatchInventoryValidationService.{method.Name} was not expected on this path.");
            }

            // Preparation is where the caller is still allowed to walk away.
            if (_cancelDuringPreparation)
            {
                _caller.Cancel();
            }

            return Task.FromResult(new BatchAllocationResult());
        });

    private IInvoiceQueueService BuildQueueService() =>
        StubProxy.For<IInvoiceQueueService>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(IInvoiceQueueService.GetFiscalizedInvoicesAsync):
                    return Task.FromResult(_queueEntries);

                case nameof(IInvoiceQueueService.MarkAsConsolidatedAsync):
                    _queueMarking = ((List<int>)args![0]!, (string)args[1]!, (int)args[2]!);
                    return Task.CompletedTask;

                default:
                    throw new InvalidOperationException(
                        $"IInvoiceQueueService.{method.Name} was not expected on this path.");
            }
        });

    /// <summary>
    /// The consolidation broadcast. Nothing here asserts on it; it just has to not throw.
    /// </summary>
    private static IHubContext<NotificationHub> BuildHubContext()
    {
        var clientProxy = StubProxy.For<IClientProxy>((method, _) =>
            method.Name == nameof(IClientProxy.SendCoreAsync)
                ? Task.CompletedTask
                : throw new InvalidOperationException($"IClientProxy.{method.Name} was not expected."));

        var clients = StubProxy.For<IHubClients>((method, _) =>
            method.Name == nameof(IHubClients.Group)
                ? clientProxy
                : throw new InvalidOperationException($"IHubClients.{method.Name} was not expected."));

        return StubProxy.For<IHubContext<NotificationHub>>((method, _) =>
            method.Name == $"get_{nameof(IHubContext<NotificationHub>.Clients)}"
                ? clients
                : throw new InvalidOperationException($"IHubContext.{method.Name} was not expected."));
    }

    /// <summary>
    /// Takes the fiscal transaction row the recovery writes. It is best-effort in production — the
    /// marker is what the guards read — so this only has to answer.
    /// </summary>
    private static ISender BuildSender() =>
        StubProxy.For<ISender>((method, args) =>
        {
            if (method.Name != nameof(ISender.Send))
            {
                throw new InvalidOperationException($"ISender.{method.Name} was not expected.");
            }

            var responseType = method.ReturnType.GetGenericArguments()[0];
            return typeof(ConsolidationLostReplyRecoveryTests)
                .GetMethod(nameof(CompletedSend), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(responseType)
                .Invoke(null, null);
        });

    private static Task<TResponse> CompletedSend<TResponse>() =>
        Task.FromResult(default(TResponse)!);
}
