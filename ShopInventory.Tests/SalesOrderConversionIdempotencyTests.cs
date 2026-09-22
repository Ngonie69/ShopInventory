using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.ConvertSalesOrderToInvoice;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Converting a sales order to an invoice from a handset whose reply can be lost.
/// </summary>
/// <remarks>
/// The handset mints <c>van_order</c> once per conversion and sends it on every retry. Before this,
/// the order's own status was the only guard: the first call marked it Fulfilled, so a retry after a
/// lost 202 was told "only approved orders can be converted" — and two calls that raced found the
/// same pending reservation, were refused by the queue as ALREADY_QUEUED, and cancelled the
/// reservation out from under the invoice that was about to post.
/// </remarks>
public sealed class SalesOrderConversionIdempotencyTests : IDisposable
{
    private const string Rep = "rep-1";
    private const string VanOrder = "VO-20260922-0007";
    private const int OrderId = 7004;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly FakeSalesOrders _orders = new();
    private readonly FakeReservations _reservations = new();

    public SalesOrderConversionIdempotencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();

        _orders.Add(OrderId, "SO-7004");
        _orders.Add(7005, "SO-7005");
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_retry_after_a_lost_reply_replays_the_first_answer()
    {
        var handler = Handler();

        var first = await handler.Handle(Command(), CancellationToken.None);
        var retry = await handler.Handle(Command(), CancellationToken.None);

        Assert.False(first.IsError, Describe(first));
        Assert.False(retry.IsError, Describe(retry));
        Assert.Equal(first.Value.QueueId, retry.Value.QueueId);
        Assert.Equal(first.Value.ReservationId, retry.Value.ReservationId);
        Assert.Equal(VanOrder, retry.Value.ExternalReference);
        Assert.Equal("Pending", retry.Value.Status);
        Assert.Equal(1, await _context.InvoiceQueue.CountAsync());
        Assert.Equal(1, _reservations.Created);
        Assert.Empty(_reservations.Cancelled);
        Assert.Equal(1, _orders.FulfilledCount(OrderId));
    }

    [Fact]
    public async Task A_retry_after_the_claim_has_expired_is_answered_from_the_queue_entry()
    {
        // The claim lives an hour; a handset that was offline longer comes back to an order that is
        // already Fulfilled. The queue entry is permanent, so it answers instead of the status check.
        var handler = Handler();
        var first = await handler.Handle(Command(), CancellationToken.None);
        await _context.IdempotencyRequests.ExecuteDeleteAsync();

        var retry = await handler.Handle(Command(), CancellationToken.None);

        Assert.False(retry.IsError, Describe(retry));
        Assert.Equal(first.Value.QueueId, retry.Value.QueueId);
        Assert.Equal(first.Value.ReservationId, retry.Value.ReservationId);
        Assert.Equal(1, await _context.InvoiceQueue.CountAsync());
        Assert.Equal(1, _reservations.Created);
        Assert.Empty(_reservations.Cancelled);
    }

    [Fact]
    public async Task A_second_call_while_the_first_is_running_is_refused_and_touches_nothing()
    {
        var gate = new TaskCompletionSource();
        _reservations.Gate = gate.Task;
        var handler = Handler();

        var first = handler.Handle(Command(), CancellationToken.None);
        await _reservations.Entered.Task;

        // Bounded: a second call the claim failed to stop would block on the same gate forever.
        var second = await handler.Handle(Command(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(second.IsError);
        Assert.Equal(ErrorType.Conflict, second.FirstError.Type);
        Assert.Equal("Idempotency.RequestInProgress", second.FirstError.Code);
        Assert.Equal(1, _reservations.Calls);

        gate.SetResult();
        var firstResult = await first;
        Assert.False(firstResult.IsError, Describe(firstResult));

        // Once the first lands, the same retry is answered with its result.
        var third = await handler.Handle(Command(), CancellationToken.None);
        Assert.False(third.IsError, Describe(third));
        Assert.Equal(firstResult.Value.QueueId, third.Value.QueueId);
        Assert.Empty(_reservations.Cancelled);
        Assert.Equal(1, await _context.InvoiceQueue.CountAsync());
    }

    [Fact]
    public async Task Already_queued_under_the_same_reservation_is_success_not_a_reason_to_cancel()
    {
        // Two callers that do not share a claim — here, two users with the same reference — can both
        // get past the queue lookup before either enqueues. The loser is handed the same pending
        // reservation and then ALREADY_QUEUED; that is its own conversion, already queued.
        var first = await Handler().Handle(Command(), CancellationToken.None);
        Assert.False(first.IsError, Describe(first));

        _orders.ReadBeforeFulfilment(OrderId);
        var racing = Handler(queueLookupMisses: true);
        var second = await racing.Handle(Command(createdBy: "rep-2"), CancellationToken.None);

        Assert.False(second.IsError, Describe(second));
        Assert.Equal(first.Value.QueueId, second.Value.QueueId);
        Assert.Equal(first.Value.ReservationId, second.Value.ReservationId);
        Assert.Empty(_reservations.Cancelled);
        Assert.Equal(1, await _context.InvoiceQueue.CountAsync());
    }

    [Fact]
    public async Task Already_queued_under_a_different_reservation_still_cancels_the_new_one()
    {
        _reservations.HandOutFreshIds = true;
        var first = await Handler().Handle(Command(), CancellationToken.None);
        Assert.False(first.IsError, Describe(first));

        _orders.ReadBeforeFulfilment(OrderId);
        var second = await Handler(queueLookupMisses: true)
            .Handle(Command(createdBy: "rep-2"), CancellationToken.None);

        Assert.True(second.IsError);
        var cancelled = Assert.Single(_reservations.Cancelled);
        Assert.NotEqual(first.Value.ReservationId, cancelled);
    }

    [Fact]
    public async Task The_same_reference_with_different_lines_is_refused()
    {
        var handler = Handler();
        await handler.Handle(Command(quantity: 5m), CancellationToken.None);

        var edited = await handler.Handle(Command(quantity: 50m), CancellationToken.None);

        Assert.True(edited.IsError);
        Assert.Equal("Idempotency.RequestMismatch", edited.FirstError.Code);
        Assert.Equal(1, _reservations.Created);
        Assert.Equal(1, await _context.InvoiceQueue.CountAsync());
    }

    [Fact]
    public async Task A_reference_already_used_for_another_order_is_refused()
    {
        var handler = Handler();
        await handler.Handle(Command(), CancellationToken.None);
        await _context.IdempotencyRequests.ExecuteDeleteAsync();

        var other = await handler.Handle(Command(salesOrderId: 7005), CancellationToken.None);

        Assert.True(other.IsError);
        Assert.Equal("DesktopIntegration.ValidationFailed", other.FirstError.Code);
        Assert.Contains("different invoice", other.FirstError.Description);
        Assert.Equal(1, _reservations.Created);
        Assert.Empty(_reservations.Cancelled);
    }

    [Fact]
    public async Task A_conversion_that_fails_gives_its_claim_back_so_the_retry_can_succeed()
    {
        _reservations.FailNext = true;
        var handler = Handler();

        var failed = await handler.Handle(Command(), CancellationToken.None);
        var retry = await handler.Handle(Command(), CancellationToken.None);

        Assert.True(failed.IsError);
        Assert.False(retry.IsError, Describe(retry));
        Assert.Equal(1, await _context.InvoiceQueue.CountAsync());
    }

    [Fact]
    public async Task Without_a_reference_the_claim_is_not_taken()
    {
        var result = await Handler().Handle(Command(externalReferenceId: null), CancellationToken.None);

        Assert.False(result.IsError, Describe(result));
        Assert.StartsWith("SO-CONV-SO-7004-", result.Value.ExternalReference);
        Assert.Equal(0, await _context.IdempotencyRequests.CountAsync());
    }

    private ConvertSalesOrderToInvoiceHandler Handler(bool queueLookupMisses = false)
    {
        IInvoiceQueueService queue = new InvoiceQueueService(
            new ApplicationDbContext(_options),
            StubProxy.Unused<IStockLedger>(),
            NullLogger<InvoiceQueueService>.Instance);

        if (queueLookupMisses)
        {
            var real = queue;
            queue = StubProxy.For<IInvoiceQueueService>((method, args) =>
                method.Name == nameof(IInvoiceQueueService.GetQueueStatusAsync)
                    ? Task.FromResult<InvoiceQueueStatusDto?>(null)
                    : method.Invoke(real, args));
        }

        return new ConvertSalesOrderToInvoiceHandler(
            _orders.AsService(),
            _reservations.AsService(),
            queue,
            new IdempotencyRequestStore(
                new SingleDbContextScopeFactory(_options),
                Options.Create(new SecuritySettings())),
            NullLogger<ConvertSalesOrderToInvoiceHandler>.Instance);
    }

    private static ConvertSalesOrderToInvoiceCommand Command(
        string? externalReferenceId = VanOrder,
        int salesOrderId = OrderId,
        decimal quantity = 5m,
        string createdBy = Rep) =>
        new(new ConvertSalesOrderToInvoiceRequest
        {
            SalesOrderId = salesOrderId,
            ExternalReferenceId = externalReferenceId,
            SourceSystem = "KefalosVanSales",
            DocCurrency = "USD",
            Lines =
            [
                new CreateDesktopInvoiceLineRequest
                {
                    LineNum = 0,
                    ItemCode = "FRM001",
                    Quantity = quantity,
                    UnitPrice = 2.5m,
                    WarehouseCode = "VAN01",
                    CostCentreCode = "CC01",
                    AutoAllocateBatches = true
                }
            ]
        }, createdBy);

    private static string Describe<T>(ErrorOr<T> result) =>
        result.IsError ? string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}")) : "ok";

    private sealed class FakeSalesOrders
    {
        private readonly Dictionary<int, SalesOrderDto> _orders = [];
        private readonly Dictionary<int, int> _fulfilled = [];

        public void Add(int id, string number) => _orders[id] = new SalesOrderDto
        {
            Id = id,
            OrderNumber = number,
            CardCode = "C001",
            Status = SalesOrderStatus.Approved,
            Currency = "USD",
            WarehouseCode = "VAN01",
            Lines = [new SalesOrderLineDto { ItemCode = "FRM001", Quantity = 5m, UnitPrice = 2.5m }]
        };

        /// <summary>The racing caller read the order before the winner marked it Fulfilled.</summary>
        public void ReadBeforeFulfilment(int id) => _orders[id].Status = SalesOrderStatus.Approved;

        public int FulfilledCount(int id) => _fulfilled.GetValueOrDefault(id);

        public Task<SalesOrderDto?> GetByIdFromLocalAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_orders.GetValueOrDefault(id));

        public Task<SalesOrderDto> MarkAsFulfilledAsync(int id, int? invoiceId, CancellationToken cancellationToken = default)
        {
            _fulfilled[id] = FulfilledCount(id) + 1;
            _orders[id].Status = SalesOrderStatus.Fulfilled;
            return Task.FromResult(_orders[id]);
        }

        public ISalesOrderService AsService() => StubProxy.For<ISalesOrderService>((method, args) => method.Name switch
        {
            nameof(ISalesOrderService.GetByIdFromLocalAsync) => GetByIdFromLocalAsync((int)args![0]!),
            nameof(ISalesOrderService.MarkAsFulfilledAsync) => MarkAsFulfilledAsync((int)args![0]!, (int?)args[1]),
            _ => throw new InvalidOperationException($"ISalesOrderService.{method.Name} is not used by the conversion.")
        });
    }

    /// <summary>
    /// Behaves as the real reservation service does for a reference it has seen: a pending
    /// reservation under that reference is handed back rather than a second one created.
    /// </summary>
    private sealed class FakeReservations
    {
        private readonly Dictionary<string, string> _byReference = [];

        public int Calls;
        public int Created;
        public bool FailNext;
        public bool HandOutFreshIds;
        public Task? Gate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Cancelled { get; } = [];

        public async Task<StockReservationResponseDto> CreateReservationAsync(
            CreateStockReservationRequest request,
            string? createdBy = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            if (Gate is not null)
                await Gate;

            if (FailNext)
            {
                FailNext = false;
                return new StockReservationResponseDto { Success = false, Errors = [] };
            }

            if (HandOutFreshIds || !_byReference.TryGetValue(request.ExternalReferenceId, out var id))
            {
                id = $"RES-{++Created}";
                _byReference[request.ExternalReferenceId] = id;
            }

            return new StockReservationResponseDto
            {
                Success = true,
                Reservation = new StockReservationDto { ReservationId = id, ExternalReferenceId = request.ExternalReferenceId }
            };
        }

        public Task<StockReservationResponseDto> CancelReservationAsync(
            CancelReservationRequest request,
            CancellationToken cancellationToken = default)
        {
            Cancelled.Add(request.ReservationId);
            return Task.FromResult(new StockReservationResponseDto { Success = true });
        }

        public IStockReservationService AsService() => StubProxy.For<IStockReservationService>((method, args) => method.Name switch
        {
            nameof(IStockReservationService.CreateReservationAsync) =>
                CreateReservationAsync((CreateStockReservationRequest)args![0]!, (string?)args[1]),
            nameof(IStockReservationService.CancelReservationAsync) =>
                CancelReservationAsync((CancelReservationRequest)args![0]!),
            _ => throw new InvalidOperationException($"IStockReservationService.{method.Name} is not used by the conversion.")
        });
    }
}
