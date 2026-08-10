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
/// Covers the run that finds a consolidated invoice already in SAP, and does not post a second one.
/// </summary>
/// <remarks>
/// <see cref="ConsolidationLostReplyRecoveryTests"/> covers recovering an invoice whose reply was
/// lost, but that recovery only runs on the failure path of the run that posted — and the lookup it
/// depends on can fail too. An unreachable Service Layer answers neither the post nor the lookup,
/// and the group is left Failed with its sales still Pending while the invoice sits in SAP. Nothing
/// downstream notices: the next run rebuilds the same group and posts a second SAP invoice for the
/// same customer and date, against sales already lodged with FDMS once.
///
/// So the deterministic key is asked before the post as well as after a failure. These tests are
/// about that guard: that it fires ahead of the stock work, that adopting says plainly the payment
/// is nobody's yet, that it refuses to post when it cannot get an answer, and that an operator who
/// cancels the invoice in SAP still gets a fresh one.
/// </remarks>
public sealed class ConsolidationDuplicatePostGuardTests : IDisposable
{
    private const string CardCode = "ABS006";
    private static readonly DateTime SaleDate = new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The invoice an earlier run posted and never managed to record.</summary>
    private static readonly Invoice ExistingInvoice = new()
    {
        DocEntry = 74120,
        DocNum = 5031,
        CardCode = CardCode,
        U_Van_saleorder = "CONSOL-20260810-ABS006"
    };

    /// <summary>What SAP would return if this run were allowed to post.</summary>
    private static readonly Invoice FreshInvoice = new()
    {
        DocEntry = 74555,
        DocNum = 5140,
        CardCode = CardCode,
        U_Van_saleorder = "CONSOL-20260810-ABS006"
    };

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    /// <summary>
    /// What the key lookup finds. Defaults to the earlier run's invoice; cleared for the tests where
    /// the day is genuinely unposted — which is also what SAP returns once the invoice is cancelled,
    /// since the lookup filters on Cancelled eq 'tNO'.
    /// </summary>
    private Invoice? _sapHolds = ExistingInvoice;

    /// <summary>Set to make the guard's own lookup fail, as an unreachable Service Layer would.</summary>
    private Exception? _lookupFailure;

    /// <summary>The earlier invoice has already taken this stock, so validating it now fails.</summary>
    private bool _stockIsGone;

    private string? _lookupKey;
    private int _lookupCount;
    private bool _batchValidationRan;
    private CreateInvoiceRequest? _posted;
    private bool _paymentPosted;
    private (List<int> Ids, string DocEntry, int DocNum)? _queueMarking;

    /// <summary>Queue-sourced sales folded into the same consolidated invoice.</summary>
    private readonly List<InvoiceQueueEntity> _queueEntries = [];

    public ConsolidationDuplicatePostGuardTests()
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
    /// The behaviour the whole change exists for. Without the guard this run posts a second SAP
    /// invoice for a customer and date that already has one.
    /// </summary>
    [Fact]
    public async Task An_invoice_an_earlier_run_left_in_SAP_is_adopted_instead_of_posted()
    {
        await GivenAPendingSaleAsync();

        await Consolidate();

        Assert.Null(_posted);

        var consolidation = await _context.SaleConsolidations.SingleAsync();
        Assert.Equal(ExistingInvoice.DocEntry, consolidation.SapDocEntry);
        Assert.Equal(ExistingInvoice.DocNum, consolidation.SapDocNum);
        Assert.NotNull(consolidation.PostedAt);

        // Marked exactly as a posted one is, so the receipts its constituents went out under can be
        // reconciled back to the SAP invoice.
        Assert.Contains("KEF-0001", consolidation.ConstituentFiscalReceipts!);
    }

    /// <summary>
    /// The key is deterministic — one consolidated invoice per customer per day — which is the only
    /// reason a later run can recognise an earlier run's document at all.
    /// </summary>
    [Fact]
    public async Task The_guard_searches_the_consolidation_key_the_post_would_be_sent_under()
    {
        await GivenAPendingSaleAsync();

        await Consolidate();

        Assert.Equal($"CONSOL-20260810-{CardCode}", _lookupKey);
        Assert.Equal(1, _lookupCount);
    }

    /// <summary>
    /// Ordering, and not incidentally. The earlier run's invoice has already consumed this stock, so
    /// validating it now fails. A guard placed after the stock work would never be reached: the group
    /// would fail with no post issued, so the post-failure recovery would not run either, and the
    /// sales would stay Pending with the duplicate still waiting to happen on the next run.
    /// </summary>
    [Fact]
    public async Task The_guard_runs_before_the_stock_the_earlier_invoice_already_consumed()
    {
        await GivenAPendingSaleAsync();
        _stockIsGone = true;

        var result = await Consolidate();

        Assert.False(_batchValidationRan);
        Assert.Equal(1, result.SuccessfulPostings);
        Assert.Equal(ExistingInvoice.DocNum, (await _context.SaleConsolidations.SingleAsync()).SapDocNum);
    }

    /// <summary>
    /// Reported as PartiallyCompleted rather than Posted, matching what the post-failure recovery
    /// reports on the same evidence. The sales are only still Pending because the earlier run never
    /// reached the SaveChanges that consolidates them — and the payment is only ever attempted after
    /// that point, so no run has posted one. Saying Posted would assert a settlement nobody made.
    /// </summary>
    [Fact]
    public async Task An_adopted_invoice_says_the_payment_is_still_outstanding()
    {
        await GivenAPendingSaleAsync();

        var result = await Consolidate();

        var consolidation = await _context.SaleConsolidations.SingleAsync();
        Assert.Equal(ConsolidationStatus.PartiallyCompleted, consolidation.Status);
        Assert.Contains("payment", consolidation.LastError, StringComparison.OrdinalIgnoreCase);

        // Not attempted here: settling a document this run cannot vouch for is a second correction
        // rather than a recovery.
        Assert.False(_paymentPosted);
        Assert.Null(result.Groups.Single().PaymentSapDocNum);

        // It still counts as a success for the run: the invoice exists and is now accounted for.
        Assert.Equal(1, result.SuccessfulPostings);
        Assert.Equal(0, result.FailedPostings);
        Assert.Equal(ExistingInvoice.DocNum, result.Groups.Single().SapDocNum);
    }

    /// <summary>
    /// The marker is only worth writing if the guard downstream can see it. An unmarked consolidated
    /// invoice reads "Unknown", and "Unknown" is what the backfill queue and the Fiscalise button
    /// gate on — fiscalising it would submit sales already lodged with FDMS a second time.
    /// PartiallyCompleted must not get in the way of that, and does not: the registry looks up the
    /// marker without filtering on ConsolidationStatus.
    /// </summary>
    [Fact]
    public async Task An_adopted_invoice_is_never_offered_for_fiscalisation()
    {
        await GivenAPendingSaleAsync();

        await Consolidate();

        Assert.NotNull(
            await ConsolidatedInvoiceRegistry.FindByDocNumAsync(
                _context, ExistingInvoice.DocNum, CancellationToken.None));

        var page = new List<InvoiceDto>
        {
            new() { DocNum = ExistingInvoice.DocNum, DocEntry = ExistingInvoice.DocEntry },
            new() { DocNum = 5099, DocEntry = 74999 }
        };

        await FiscalDocumentStatusProjector.EnrichInvoicesAsync(_context, page, CancellationToken.None);

        Assert.Equal("Fiscalised", page[0].FiscalizationStatus);
        Assert.True(page[0].IsFiscalized);

        // The ordinary invoice beside it still reads Unknown, so the guard is the adopted marker and
        // not something blanketing the page.
        Assert.Equal("Unknown", page[1].FiscalizationStatus);
    }

    /// <summary>
    /// Sales and queue entries are released against the adopted document, not against nothing —
    /// otherwise the run after this one would rebuild the group and be back where it started.
    /// </summary>
    [Fact]
    public async Task An_adopted_invoice_closes_out_its_sales_and_queue_entries()
    {
        await GivenAPendingSaleAsync();
        GivenAFiscalisedQueueEntry();

        await Consolidate();

        var consolidation = await _context.SaleConsolidations.SingleAsync();
        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, sale.ConsolidationStatus);
        Assert.Equal(consolidation.Id, sale.ConsolidationId);

        Assert.NotNull(_queueMarking);
        Assert.Equal(ExistingInvoice.DocEntry.ToString(), _queueMarking!.Value.DocEntry);
        Assert.Equal(ExistingInvoice.DocNum, _queueMarking.Value.DocNum);
    }

    /// <summary>
    /// A guard that cannot get an answer must not wave the post through. This is the exact state the
    /// residual gap lived in — the Service Layer unreachable for both the post and the lookup — and
    /// posting on that uncertainty is the duplicate the guard exists to prevent. Failing the group
    /// costs a run and nothing else: the sales stay Pending and the next run asks again.
    /// </summary>
    [Fact]
    public async Task A_guard_lookup_that_cannot_answer_does_not_post()
    {
        await GivenAPendingSaleAsync();
        _lookupFailure = new HttpRequestException("Service Layer unreachable");

        var result = await Consolidate();

        Assert.Null(_posted);
        Assert.False(_batchValidationRan);

        var consolidation = await _context.SaleConsolidations.SingleAsync();
        Assert.Equal(ConsolidationStatus.Failed, consolidation.Status);
        Assert.Null(consolidation.SapDocNum);

        Assert.Equal(0, result.SuccessfulPostings);
        Assert.Equal(1, result.FailedPostings);

        // The failure says why nothing was posted, and quotes what SAP said, so an operator is not
        // left guessing whether the day is unposted or already done.
        Assert.Contains("Not posting", result.Groups.Single().Error);
        Assert.Contains("Service Layer unreachable", result.Groups.Single().Error);

        // Still Pending, so the next run consolidates them properly.
        Assert.Equal(
            DesktopSaleConsolidationStatus.Pending,
            (await _context.DesktopSales.SingleAsync()).ConsolidationStatus);
    }

    /// <summary>
    /// An operator who cancels the consolidated invoice in SAP and re-runs wants a fresh one, and
    /// gets it: the lookup filters on Cancelled eq 'tNO', so a cancelled document reads as absent and
    /// the guard does not fire. Nothing in the handler has to know about cancellation for that.
    /// </summary>
    [Fact]
    public async Task A_cancelled_invoice_reads_as_absent_and_the_rerun_posts_afresh()
    {
        await GivenAPendingSaleAsync();
        _sapHolds = null;

        var result = await Consolidate();

        Assert.NotNull(_posted);
        Assert.Equal($"CONSOL-20260810-{CardCode}", _posted!.U_Van_saleorder);

        var consolidation = await _context.SaleConsolidations.SingleAsync();
        Assert.Equal(ConsolidationStatus.Posted, consolidation.Status);
        Assert.Equal(FreshInvoice.DocNum, consolidation.SapDocNum);

        // A fresh post is a whole post: the payment runs, which is what separates it from an adoption.
        Assert.True(_paymentPosted);
        Assert.Equal(1, result.SuccessfulPostings);
    }

    /// <summary>
    /// The guard is per group, so one customer's adopted invoice does not decide anything for another
    /// customer's — each is asked about under its own key.
    /// </summary>
    [Fact]
    public async Task Each_customer_is_asked_about_under_its_own_key()
    {
        await GivenAPendingSaleAsync();
        await GivenAPendingSaleAsync("NRI002", "TILL-9002");

        var result = await Consolidate();

        Assert.Equal(2, _lookupCount);
        Assert.Equal(2, result.Groups.Count);
    }

    private async Task<ConsolidateDailySalesResult> Consolidate()
    {
        var result = await CreateHandler().Handle(
            new ConsolidateDailySalesCommand(SaleDate), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        return result.Value;
    }

    private async Task GivenAPendingSaleAsync(
        string cardCode = CardCode,
        string externalReference = "TILL-9001")
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = externalReference,
            SourceSystem = "KefalosDesktop",
            CardCode = cardCode,
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
    /// A POS sale fiscalised through the queue rather than the desktop path. It is folded into the
    /// same consolidated invoice, and its queue entry has to be closed out with it.
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
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) => KeyLookup(args),
            nameof(ISAPServiceLayerClient.ValidateStockAvailabilityAsync) =>
                Task.FromResult(new List<StockValidationError>()),
            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => Post(args),
            nameof(ISAPServiceLayerClient.CreateIncomingPaymentAsync) => PostPayment(),
            _ => throw new InvalidOperationException(
                $"ISAPServiceLayerClient.{method.Name} was not expected on this path.")
        });

    private Task<Invoice?> KeyLookup(object?[]? args)
    {
        _lookupKey = (string)args![0]!;
        _lookupCount++;

        if (_lookupFailure is not null)
        {
            throw _lookupFailure;
        }

        return Task.FromResult(_sapHolds);
    }

    private Task<Invoice> Post(object?[]? args)
    {
        _posted = (CreateInvoiceRequest)args![0]!;
        return Task.FromResult(FreshInvoice);
    }

    private Task<IncomingPayment> PostPayment()
    {
        _paymentPosted = true;
        return Task.FromResult(new IncomingPayment { DocEntry = 900, DocNum = 4411 });
    }

    private IBatchInventoryValidationService BuildBatchValidation() =>
        StubProxy.For<IBatchInventoryValidationService>((method, _) =>
        {
            if (method.Name != nameof(IBatchInventoryValidationService.ValidateAndAllocateBatchesAsync))
            {
                throw new InvalidOperationException(
                    $"IBatchInventoryValidationService.{method.Name} was not expected on this path.");
            }

            _batchValidationRan = true;

            var result = new BatchAllocationResult();
            if (_stockIsGone)
            {
                result.ValidationErrors.Add(new BatchValidationErrorDto
                {
                    LineNumber = 1,
                    ItemCode = "NRI049",
                    Message = "Insufficient stock: the consolidated invoice already took it."
                });
            }

            return Task.FromResult(result);
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
    /// Takes the fiscal transaction row the adoption writes. It is best-effort in production — the
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
            return typeof(ConsolidationDuplicatePostGuardTests)
                .GetMethod(nameof(CompletedSend), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(responseType)
                .Invoke(null, null);
        });

    private static Task<TResponse> CompletedSend<TResponse>() =>
        Task.FromResult(default(TResponse)!);
}
