using System.Net;
using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.PostQueuedVanInvoices;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A van sale that reaches the invoice queue — a converted sales order, or an online sale made while SAP
/// was down — is posted to SAP one invoice per sale once it is fiscalised.
/// </summary>
/// <remarks>
/// Until this existed nothing did it. <c>InvoicePostingJob</c> fiscalised the entry and left it
/// Fiscalized, and the end-of-day consolidation, which posts every other Fiscalized entry, refuses van
/// sales. The tests run the real reservation service against a stubbed Service Layer, because every
/// guard that makes the post safe to repeat lives there.
/// </remarks>
public sealed class QueuedVanInvoicePostingTests : IDisposable
{
    private const string VanOrder = "VAN-7-20260901-0042";
    private const string CardCode = "VAN007";
    private const int DocEntry = 88120;
    private const int DocNum = 772450;

    // 22:30 UTC is 00:30 the next day in Harare, which is the day the rep made the sale.
    private static readonly DateTime QueuedAtUtc = new(2026, 9, 1, 22, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private const int OrderDocEntry = 5310;

    private readonly List<CreateInvoiceRequest> _posted = [];

    // The lines of each post as they stood when it was sent. The request itself is mutated by a repost.
    private readonly List<List<string>> _postedLines = [];

    private Func<CreateInvoiceRequest, Task<Invoice>> _post;
    private Invoice? _alreadyInSap;
    private SAPSalesOrder? _sapOrder;

    public QueuedVanInvoicePostingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _post = _ => Task.FromResult(new Invoice { DocEntry = DocEntry, DocNum = DocNum, CardCode = CardCode });
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---------------------------------------------------------------
    // The stuck sale
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_fiscalised_converted_order_is_posted_to_SAP_as_its_own_invoice()
    {
        var reservationId = await SeedQueuedSaleAsync();

        var result = await RunAsync();

        Assert.Equal(new PostQueuedVanInvoicesResult(1, 0, 0), result);

        var invoice = Assert.Single(_posted);
        Assert.Equal(VanOrder, invoice.U_Van_saleorder);
        Assert.Equal("2026-09-02", invoice.DocDate);
        Assert.Equal("Van sales conversion from SO-1182", invoice.Comments);

        var entry = await QueueEntryAsync();
        Assert.Equal(InvoiceQueueStatus.Completed, entry.Status);
        Assert.Equal(DocNum, entry.SapDocNum);
        Assert.Equal(DocEntry.ToString(), entry.SapDocEntry);

        var reservation = await ReservationAsync(reservationId);
        Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
        Assert.Equal(DocNum, reservation.SAPDocNum);
    }

    [Fact]
    public async Task A_second_run_does_not_post_the_sale_again()
    {
        await SeedQueuedSaleAsync();

        await RunAsync();
        var second = await RunAsync();

        Assert.Equal(new PostQueuedVanInvoicesResult(0, 0, 0), second);
        Assert.Single(_posted);
    }

    /// <summary>
    /// The backlog. Before the queue outranked the clock, the cleanup job expired these reservations an
    /// hour after the sale, so the sales stuck since then are held by reservations marked Expired.
    /// </summary>
    [Fact]
    public async Task A_sale_whose_reservation_the_cleanup_job_expired_is_still_posted()
    {
        var reservationId = await SeedQueuedSaleAsync(reservationStatus: ReservationStatus.Expired);

        var result = await RunAsync();

        Assert.Equal(1, result.Posted);
        Assert.Single(_posted);
        Assert.Equal(ReservationStatus.Confirmed, (await ReservationAsync(reservationId)).Status);
    }

    [Fact]
    public async Task An_invoice_already_in_SAP_is_adopted_rather_than_posted_twice()
    {
        await SeedQueuedSaleAsync();
        _alreadyInSap = new Invoice { DocEntry = DocEntry, DocNum = DocNum, CardCode = CardCode, U_Van_saleorder = VanOrder };

        var result = await RunAsync();

        Assert.Equal(1, result.Posted);
        Assert.Empty(_posted);
        Assert.Equal(DocNum, (await QueueEntryAsync()).SapDocNum);
    }

    // ---------------------------------------------------------------
    // Based on the sales order it was converted from
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_converted_sale_is_invoiced_against_its_sales_order()
    {
        var orderId = await SeedSalesOrderAsync(OrderDocEntry);
        await SeedQueuedSaleAsync(salesOrderId: orderId);
        _sapOrder = OpenOrder(openQuantity: 5m);

        Assert.Equal(1, (await RunAsync()).Posted);

        Assert.Equal(["CHE011 x2 on 17/5310/0 [B-1:2]"], Assert.Single(_postedLines));
    }

    /// <summary>
    /// SAP refuses a whole invoice whose line asks for more than the order has open, so only the open part
    /// is linked — and the batches reserved for the sale are split with the line.
    /// </summary>
    [Fact]
    public async Task Only_what_the_order_still_has_open_is_linked()
    {
        var orderId = await SeedSalesOrderAsync(OrderDocEntry);
        await SeedQueuedSaleAsync(salesOrderId: orderId);
        _sapOrder = OpenOrder(openQuantity: 1.5m);

        await RunAsync();

        Assert.Equal(
            ["CHE011 x1.5 on 17/5310/0 [B-1:1.5]", "CHE011 x0.5 [B-1:0.5]"],
            Assert.Single(_postedLines));
    }

    [Fact]
    public async Task A_fresh_sale_waits_for_its_order_to_reach_SAP()
    {
        var orderId = await SeedSalesOrderAsync(sapDocEntry: null);
        await SeedQueuedSaleAsync(salesOrderId: orderId, queuedAt: DateTime.UtcNow.AddMinutes(-5));

        var result = await RunAsync();

        Assert.Equal(new PostQueuedVanInvoicesResult(0, 1, 0), result);
        Assert.Empty(_postedLines);
        Assert.Contains("SO-1182", (await QueueEntryAsync()).LastError);
    }

    [Fact]
    public async Task A_sale_whose_order_never_reached_SAP_is_invoiced_without_the_link_in_the_end()
    {
        var orderId = await SeedSalesOrderAsync(sapDocEntry: null);
        await SeedQueuedSaleAsync(salesOrderId: orderId, queuedAt: DateTime.UtcNow.Add(-PostQueuedVanInvoicesHandler.OrderPostWait).AddMinutes(-1));

        Assert.Equal(1, (await RunAsync()).Posted);
        Assert.Equal(["CHE011 x2 [B-1:2]"], Assert.Single(_postedLines));
    }

    [Fact]
    public async Task A_closed_order_is_not_linked()
    {
        var orderId = await SeedSalesOrderAsync(OrderDocEntry);
        await SeedQueuedSaleAsync(salesOrderId: orderId);
        _sapOrder = OpenOrder(openQuantity: 5m);
        _sapOrder.DocumentStatus = "bost_Close";

        await RunAsync();

        Assert.Equal(["CHE011 x2 [B-1:2]"], Assert.Single(_postedLines));
    }

    [Fact]
    public async Task A_linked_invoice_SAP_refuses_is_posted_once_more_without_the_link()
    {
        var orderId = await SeedSalesOrderAsync(OrderDocEntry);
        await SeedQueuedSaleAsync(salesOrderId: orderId);
        _sapOrder = OpenOrder(openQuantity: 5m);

        var attempts = 0;
        _post = _ => ++attempts == 1
            ? throw new SapRequestRejectedException("CreateInvoice", HttpStatusCode.BadRequest, "Base document line is closed")
            : Task.FromResult(new Invoice { DocEntry = DocEntry, DocNum = DocNum, CardCode = CardCode });

        Assert.Equal(1, (await RunAsync()).Posted);

        Assert.Equal(
            [["CHE011 x2 on 17/5310/0 [B-1:2]"], ["CHE011 x2 [B-1:2]"]],
            _postedLines);
    }

    // ---------------------------------------------------------------
    // Failures
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_SAP_that_cannot_be_reached_defers_the_sale_without_spending_it()
    {
        var reservationId = await SeedQueuedSaleAsync();
        _post = _ => throw new HttpRequestException("No connection could be made");

        var result = await RunAsync();

        Assert.Equal(new PostQueuedVanInvoicesResult(0, 1, 0), result);

        var entry = await QueueEntryAsync();
        Assert.Equal(InvoiceQueueStatus.Fiscalized, entry.Status);
        Assert.Equal(0, entry.RetryCount);
        Assert.NotNull(entry.NextRetryAt);
        Assert.Contains("No connection", entry.LastError);
        Assert.Equal(ReservationStatus.Pending, (await ReservationAsync(reservationId)).Status);

        // Deferred means later, not on the next tick of a job that runs every few seconds.
        Assert.Equal(new PostQueuedVanInvoicesResult(0, 0, 0), await RunAsync());
    }

    [Fact]
    public async Task A_sale_SAP_refuses_goes_to_review()
    {
        var reservationId = await SeedQueuedSaleAsync();
        _post = _ => throw new SapRequestRejectedException(
            "CreateInvoice", HttpStatusCode.BadRequest, "Quantity falls into negative inventory");

        var result = await RunAsync();

        Assert.Equal(new PostQueuedVanInvoicesResult(0, 0, 1), result);

        var entry = await QueueEntryAsync();
        Assert.Equal(InvoiceQueueStatus.RequiresReview, entry.Status);
        Assert.Contains("negative inventory", entry.LastError);
        Assert.Equal(ReservationStatus.Failed, (await ReservationAsync(reservationId)).Status);
    }

    /// <summary>
    /// What Retry is for. The refusal left the reservation Failed; once whatever SAP objected to is put
    /// right, the sale is still owed its invoice, and a Failed reservation must not read as "already being
    /// posted" and send it straight back to review.
    /// </summary>
    [Fact]
    public async Task A_refused_sale_put_back_after_the_cause_is_fixed_is_posted()
    {
        var reservationId = await SeedQueuedSaleAsync();
        _post = _ => throw new SapRequestRejectedException(
            "CreateInvoice", HttpStatusCode.BadRequest, "Account currency 805650 not the same as document currency");
        await RunAsync();
        Assert.Equal(ReservationStatus.Failed, (await ReservationAsync(reservationId)).Status);

        // Retry resets the entry, and InvoicePostingJob adopts the existing receipt back to Fiscalized.
        _post = _ => Task.FromResult(new Invoice { DocEntry = DocEntry, DocNum = DocNum, CardCode = CardCode });
        var entry = await _context.InvoiceQueue.AsTracking().SingleAsync();
        entry.Status = InvoiceQueueStatus.Fiscalized;
        entry.NextRetryAt = null;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await RunAsync();

        Assert.Equal(1, result.Posted);
        Assert.Single(_posted);
        Assert.Equal(ReservationStatus.Confirmed, (await ReservationAsync(reservationId)).Status);
        Assert.Equal(InvoiceQueueStatus.Completed, (await QueueEntryAsync()).Status);
    }

    [Fact]
    public async Task A_failed_reservation_is_not_claimable_without_a_fiscalised_entry_behind_it()
    {
        var reservationId = await SeedQueuedSaleAsync(
            queueStatus: InvoiceQueueStatus.RequiresReview,
            reservationStatus: ReservationStatus.Failed);

        var confirmed = await ReservationService().ConfirmQueuedReservationAsync(
            new ConfirmReservationRequest { ReservationId = reservationId });

        Assert.False(confirmed.Success);
        Assert.Empty(_posted);
    }

    [Fact]
    public async Task Nothing_is_attempted_while_the_SAP_circuit_is_open()
    {
        await SeedQueuedSaleAsync();
        var breaker = new SapCircuitBreakerState(Options.Create(new SAPSettings()));
        for (var i = 0; i < 20; i++)
        {
            breaker.RecordFailure("Service Layer unreachable");
        }

        var result = await RunAsync(breaker);

        Assert.Equal(new PostQueuedVanInvoicesResult(0, 0, 0), result);
        Assert.Empty(_posted);
    }

    // ---------------------------------------------------------------
    // Scope
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_desktop_entry_is_left_for_the_end_of_day_consolidation()
    {
        await SeedQueuedSaleAsync(sourceSystem: "DESKTOP_APP");

        var result = await RunAsync();

        Assert.Equal(new PostQueuedVanInvoicesResult(0, 0, 0), result);
        Assert.Equal(InvoiceQueueStatus.Fiscalized, (await QueueEntryAsync()).Status);
    }

    [Fact]
    public async Task A_van_sale_still_waiting_to_be_fiscalised_is_not_posted()
    {
        await SeedQueuedSaleAsync(queueStatus: InvoiceQueueStatus.Pending);

        Assert.Equal(new PostQueuedVanInvoicesResult(0, 0, 0), await RunAsync());
        Assert.Empty(_posted);
    }

    /// <summary>
    /// The override is the queue's, not the endpoint's. The confirm route a desktop client calls must go
    /// on refusing an expired reservation exactly as before.
    /// </summary>
    [Fact]
    public async Task An_ordinary_confirm_still_refuses_an_expired_reservation()
    {
        var reservationId = await SeedQueuedSaleAsync();

        var confirmed = await ReservationService().ConfirmReservationAsync(
            new ConfirmReservationRequest { ReservationId = reservationId });

        Assert.False(confirmed.Success);
        Assert.Equal("Reservation has expired", confirmed.Message);
        Assert.Empty(_posted);
    }

    [Fact]
    public async Task The_queue_override_needs_a_fiscalised_entry_behind_it()
    {
        var reservationId = await SeedQueuedSaleAsync(queueStatus: InvoiceQueueStatus.Pending);

        var confirmed = await ReservationService().ConfirmQueuedReservationAsync(
            new ConfirmReservationRequest { ReservationId = reservationId });

        Assert.False(confirmed.Success);
        Assert.Equal("Reservation has expired", confirmed.Message);
    }

    // ---------------------------------------------------------------
    // Never fiscalised a second time
    // ---------------------------------------------------------------

    [Fact]
    public async Task The_posted_invoice_is_recognised_as_already_fiscalised()
    {
        await SeedQueuedSaleAsync();
        await RunAsync();
        _context.ChangeTracker.Clear();

        var sale = await PerSaleInvoiceRegistry.FindByDocNumAsync(_context, DocNum, CancellationToken.None);

        Assert.NotNull(sale);
        Assert.Equal(VanOrder, sale.ExternalReferenceId);
        Assert.Equal("R-9001", sale.FiscalReceiptNumber);
        Assert.Contains(
            DocNum,
            await PerSaleInvoiceRegistry.FindPerSaleDocNumsAsync(_context, [DocNum, DocNum + 1], CancellationToken.None));
    }

    [Fact]
    public async Task A_queued_invoice_that_was_never_fiscalised_is_not_claimed_as_fiscalised()
    {
        await SeedQueuedSaleAsync(requiresFiscalization: false);
        await RunAsync();
        _context.ChangeTracker.Clear();

        Assert.Null(await PerSaleInvoiceRegistry.FindByDocNumAsync(_context, DocNum, CancellationToken.None));
        Assert.Empty(await PerSaleInvoiceRegistry.FindPerSaleDocNumsAsync(_context, [DocNum], CancellationToken.None));
    }

    /// <summary>
    /// An online van sale queued while SAP was down stored its handset's signed receipt with no document
    /// on it. Once the queue posts the sale, that row has to name the invoice or the invoice reads as
    /// unfiscalised.
    /// </summary>
    [Fact]
    public async Task An_online_sale_receipt_row_is_given_the_invoice_the_queue_posted()
    {
        await SeedQueuedSaleAsync(requiresFiscalization: false);
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = VanOrder,
            SourceSystem = SaleSourceSystems.VanSalesOnline,
            CardCode = CardCode,
            DocDate = QueuedAtUtc.Date,
            WarehouseCode = "VAN07",
            Currency = "USD",
            TotalAmount = 60m,
            AmountPaid = 60m,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            FiscalReceiptNumber = "H-311",
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            CreatedAt = QueuedAtUtc
        });
        await _context.SaveChangesAsync();

        await RunAsync();
        _context.ChangeTracker.Clear();

        var row = await _context.DesktopSales.SingleAsync(sale => sale.ExternalReferenceId == VanOrder);
        Assert.Equal(DocNum, row.SapDocNum);
        Assert.Equal(DocEntry, row.SapDocEntry);
        Assert.Equal("H-311", (await PerSaleInvoiceRegistry.FindByDocNumAsync(_context, DocNum, CancellationToken.None))?.FiscalReceiptNumber);
    }

    /// <summary>
    /// A fiscalised sale SAP refused goes to review, and Retry on the review list puts it back to Pending.
    /// The job then fiscalises it again — so it has to ask the device first, or the sale is signed twice.
    /// </summary>
    [Fact]
    public async Task The_queue_job_adopts_an_existing_receipt_instead_of_signing_a_retried_sale_again()
    {
        await SeedQueuedSaleAsync(queueStatus: InvoiceQueueStatus.Pending);
        var entry = await QueueEntryAsync();
        entry.ProcessingStartedAt = QueuedAtUtc.AddMinutes(1);
        entry.FiscalReceiptNumber = null;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var signed = 0;
        var fiscalisation = StubProxy.For<IFiscalizationService>((method, _) => method.Name switch
        {
            nameof(IFiscalizationService.FindPreSapReceiptAsync) => Task.FromResult<FiscalizationResult?>(
                new FiscalizationResult { Success = true, ReceiptGlobalNo = "R-9001", DeviceSerial = "REVMAX-1" }),
            nameof(IFiscalizationService.FiscalizePreSapInvoiceAsync) => Signed(ref signed),
            _ => throw new InvalidOperationException($"Unexpected fiscal call: {method.Name}")
        });

        await RunJobAsync(fiscalisation);
        _context.ChangeTracker.Clear();

        Assert.Equal(0, signed);
        var after = await QueueEntryAsync();
        Assert.Equal(InvoiceQueueStatus.Fiscalized, after.Status);
        Assert.Equal("R-9001", after.FiscalReceiptNumber);
    }

    [Fact]
    public async Task The_queue_job_signs_a_sale_it_has_never_started()
    {
        await SeedQueuedSaleAsync(queueStatus: InvoiceQueueStatus.Pending);
        var entry = await QueueEntryAsync();
        entry.FiscalReceiptNumber = null;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var signed = 0;
        var fiscalisation = StubProxy.For<IFiscalizationService>((method, _) => method.Name switch
        {
            nameof(IFiscalizationService.FiscalizePreSapInvoiceAsync) => Signed(ref signed),
            _ => throw new InvalidOperationException($"Unexpected fiscal call: {method.Name}")
        });

        await RunJobAsync(fiscalisation);

        Assert.Equal(1, signed);
    }

    // ---------------------------------------------------------------

    private static Task<FiscalizationResult> Signed(ref int count)
    {
        count++;
        return Task.FromResult(new FiscalizationResult { Success = true, ReceiptGlobalNo = "R-NEW" });
    }

    private async Task<string> SeedQueuedSaleAsync(
        string sourceSystem = SaleSourceSystems.VanSales,
        string reservationStatus = ReservationStatus.Pending,
        InvoiceQueueStatus queueStatus = InvoiceQueueStatus.Fiscalized,
        bool requiresFiscalization = true,
        DateTime? queuedAt = null,
        int? salesOrderId = null)
    {
        var createdAt = queuedAt ?? QueuedAtUtc;

        var reservation = new StockReservationEntity
        {
            ExternalReferenceId = VanOrder,
            SourceSystem = sourceSystem,
            CardCode = CardCode,
            Currency = "USD",
            Status = reservationStatus,
            // Long past its hour: the sale was queued a fortnight ago and has sat Fiscalized since.
            ExpiresAt = createdAt.AddHours(1),
            CreatedAt = createdAt,
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    WarehouseCode = "VAN07",
                    ReservedQuantity = 2m,
                    OriginalQuantity = 2m,
                    UnitPrice = 30m,
                    BatchAllocations =
                    [
                        new StockReservationBatchEntity
                        {
                            ItemCode = "CHE011",
                            WarehouseCode = "VAN07",
                            BatchNumber = "B-1",
                            ReservedQuantity = 2m
                        }
                    ]
                }
            ]
        };

        _context.StockReservations.Add(reservation);
        await _context.SaveChangesAsync();

        _context.InvoiceQueue.Add(new InvoiceQueueEntity
        {
            ReservationId = reservation.ReservationId,
            ExternalReference = VanOrder,
            SourceSystem = sourceSystem,
            CustomerCode = CardCode,
            TotalAmount = 60m,
            Currency = "USD",
            WarehouseCode = "VAN07",
            Status = queueStatus,
            RequiresFiscalization = requiresFiscalization,
            FiscalReceiptNumber = requiresFiscalization ? "R-9001" : null,
            Notes = "Van sales conversion from SO-1182",
            CreatedAt = createdAt,
            SalesOrderId = salesOrderId,
            InvoicePayload = $$"""
                {"cardCode":"{{CardCode}}","cardName":"Route 7","lines":[
                  {"lineNum":0,"itemCode":"CHE011","quantity":2,"unitPrice":30.00,"warehouseCode":"VAN07"}]}
                """
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return reservation.ReservationId;
    }

    private async Task<int> SeedSalesOrderAsync(int? sapDocEntry)
    {
        var order = new SalesOrderEntity
        {
            OrderNumber = "SO-1182",
            CardCode = CardCode,
            Status = SalesOrderStatus.Fulfilled,
            SAPDocEntry = sapDocEntry,
            SAPDocNum = sapDocEntry + 100000,
            Source = SalesOrderSource.Mobile,
            RowVersion = BitConverter.GetBytes(1L)
        };

        _context.SalesOrders.Add(order);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return order.Id;
    }

    private static SAPSalesOrder OpenOrder(decimal openQuantity) => new()
    {
        DocEntry = OrderDocEntry,
        CardCode = CardCode,
        DocumentStatus = "bost_Open",
        Cancelled = "tNO",
        DocumentLines =
        [
            new SAPSalesOrderLine
            {
                LineNum = 0,
                ItemCode = "CHE011",
                LineStatus = "bost_Open",
                RemainingOpenQuantity = openQuantity
            }
        ]
    };

    private async Task<PostQueuedVanInvoicesResult> RunAsync(SapCircuitBreakerState? breaker = null)
    {
        var handler = new PostQueuedVanInvoicesHandler(
            _context,
            ReservationService(),
            Sap(),
            breaker ?? new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            NullLogger<PostQueuedVanInvoicesHandler>.Instance);

        var result = await handler.Handle(new PostQueuedVanInvoicesCommand(5), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        _context.ChangeTracker.Clear();
        return result.Value;
    }

    private async Task RunJobAsync(IFiscalizationService fiscalisation)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton(StubProxy.Unused<IStockLedger>());
        services.AddScoped<IInvoiceQueueService, InvoiceQueueService>();
        services.AddSingleton(fiscalisation);
        services.AddSingleton(Options.Create(new TaxSettings()));
        services.AddSingleton(StubProxy.For<ISender>((method, _) => method.Name == nameof(ISender.Send)
            ? Task.FromResult<ErrorOr<PostQueuedVanInvoicesResult>>(new PostQueuedVanInvoicesResult(0, 0, 0))
            : throw new InvalidOperationException($"Unexpected sender call: {method.Name}")));

        await using var provider = services.BuildServiceProvider();

        var job = new InvoicePostingJob(provider, NullLogger<InvoicePostingJob>.Instance);
        await job.Execute(StubProxy.For<IJobExecutionContext>((method, _) =>
            method.Name == $"get_{nameof(IJobExecutionContext.CancellationToken)}"
                ? CancellationToken.None
                : throw new InvalidOperationException($"Unexpected job context call: {method.Name}")));
    }

    private StockReservationService ReservationService() => new(
        _context,
        Sap(),
        StubProxy.Unused<IBatchInventoryValidationService>(),
        StubProxy.Unused<IInventoryLockService>(),
        StubProxy.For<IStockLedger>((method, _) => method.Name == nameof(IStockLedger.TakeSettledAsync)
            ? Task.FromResult<IReadOnlyList<StockLedgerShortfall>>([])
            : throw new InvalidOperationException($"Unexpected ledger call: {method.Name}")),
        StubProxy.Unused<IInvoiceFiscalizationQueue>(),
        StubProxy.Unused<INotificationService>(),
        NullLogger<StockReservationService>.Instance);

    private ISAPServiceLayerClient Sap() =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) => Task.FromResult(_alreadyInSap),
            nameof(ISAPServiceLayerClient.GetSalesOrderByDocEntryAsync) => Task.FromResult(_sapOrder),
            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => Post((CreateInvoiceRequest)args![0]!),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

    private Task<Invoice> Post(CreateInvoiceRequest request)
    {
        _postedLines.Add(request.Lines!
            .Select(line =>
                $"{line.ItemCode} x{line.Quantity:0.##}"
                + (line.BaseEntry.HasValue ? $" on {line.BaseType}/{line.BaseEntry}/{line.BaseLine}" : string.Empty)
                + $" [{string.Join(",", (line.BatchNumbers ?? []).Select(b => $"{b.BatchNumber}:{b.Quantity:0.##}"))}]")
            .ToList());

        var outcome = _post(request);
        _posted.Add(request);
        return outcome;
    }

    /// <summary>
    /// <see cref="SalesOrderEntity.RowVersion"/> is store-generated on PostgreSQL and has no SQLite
    /// equivalent, so it is made an ordinary property the fixture supplies — as in
    /// <c>MobileSalesOrderListTests</c>.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SalesOrderEntity>()
                .Property(order => order.RowVersion)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }

    private Task<InvoiceQueueEntity> QueueEntryAsync() =>
        _context.InvoiceQueue.AsTracking().SingleAsync();

    private Task<StockReservationEntity> ReservationAsync(string reservationId) =>
        _context.StockReservations.AsNoTracking().SingleAsync(r => r.ReservationId == reservationId);
}
