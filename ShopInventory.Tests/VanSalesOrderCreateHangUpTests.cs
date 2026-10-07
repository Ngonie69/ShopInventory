using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.SalesOrders.Commands.CreateSalesOrder;
using ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesSalesOrder;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A van raises a sales order and waits for it. The handset gives up after 30 s, and ASP.NET then cancels
/// the request token. Past the commit point the caller going away must not stop the work, so an order
/// whose SAP item read outlives the handset is still captured, and the resend under the same van order is
/// answered with it.
/// </summary>
/// <remarks>
/// The whole chain is real — the van handler, <see cref="CreateSalesOrderHandler"/> and
/// <see cref="SalesOrderService"/> — down to the Service Layer, which is the one stub. That matters: the
/// shared handler catches every exception, a cancellation included, and answers it as a failed order, so a
/// stub higher up the chain would not show what the handset was actually sent.
/// </remarks>
public sealed class VanSalesOrderCreateHangUpTests : IDisposable
{
    private static readonly Guid VanUser = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private const string VanOrder = "VAN006-SO-20261007-AAA111";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    /// <summary>Runs as SAP is asked for the order's items, before it answers.</summary>
    private Action? _sapItemReadStarted;

    private int _sapItemReads;

    public VanSalesOrderCreateHangUpTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = VanUser,
            Username = "van006",
            Email = "van006@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = "VAN006",
            AssignedCostCentreCode = "CC006",
            // Stored as a JSON array, not a CSV — MobileAssignedCustomerScope deserializes it.
            AssignedCustomerCodes = """["SIM001"]"""
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// 2026-10-07: van requests stalled for over 30 s on SAP reads, the handsets gave up, and the server
    /// answered "The operation was canceled" to nobody. A van sales order reads SAP for every line's unit of
    /// measure before it is stored, so a slow read there cancelled the order and stored nothing, and the
    /// resend met the same read.
    /// </summary>
    [Fact]
    public async Task A_phone_that_hangs_up_while_SAP_is_read_does_not_stop_the_order()
    {
        using var hangUp = new CancellationTokenSource();
        _sapItemReadStarted = hangUp.Cancel;

        var result = await Handler().Handle(Command(), hangUp.Token);

        Assert.True(hangUp.IsCancellationRequested);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);

        var order = await _context.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleAsync();
        Assert.Equal(VanOrder, order.ClientRequestId);
        Assert.Equal(SalesOrderStatus.Pending, order.Status);
        Assert.Equal("EA", Assert.Single(order.Lines).UoMCode);
        Assert.Equal(result.Value.Id, order.Id);

        // Captured with its queue entry, so the post-save queue posts it to SAP as it would any van order.
        var queued = await _context.MobileOrderPostProcessingQueue.AsNoTracking().SingleAsync();
        Assert.Equal(order.Id, queued.SalesOrderId);
        Assert.True(queued.AutoPostToSap);

        // The handset never saw that answer, so it sends the order again under the same van order. It is
        // handed the order already captured, and nothing is captured twice.
        _sapItemReadStarted = null;
        var resent = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(resent.IsError, resent.IsError ? resent.FirstError.Description : null);
        Assert.Equal(result.Value.Id, resent.Value.Id);
        Assert.Equal(result.Value.PurchaseOrders, resent.Value.PurchaseOrders);
        Assert.Equal(1, await _context.SalesOrders.CountAsync());
        Assert.Equal(1, await _context.MobileOrderPostProcessingQueue.CountAsync());
        Assert.Equal(1, _sapItemReads);
    }

    /// <summary>
    /// The other side of the commit point: a handset that is already gone before anything was read from SAP
    /// captures nothing, so there is nothing to finish.
    /// </summary>
    [Fact]
    public async Task A_phone_that_hung_up_before_the_order_started_captures_nothing()
    {
        using var goneAlready = new CancellationTokenSource();
        goneAlready.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler().Handle(Command(), goneAlready.Token));

        Assert.Empty(await _context.SalesOrders.ToListAsync());
        Assert.Equal(0, _sapItemReads);
    }

    private static CreateVanSalesSalesOrderCommand Command() => new(
        new VanSalesOrderRequest
        {
            VanOrder = VanOrder,
            CustomerCode = "SIM001",
            Reference = "Tuck Shop",
            Type = "SO",
            Currency = "USD",
            DueDate = "2026-10-07",
            Items = [new VanSalesOrderItemRequest { Code = "CHE011", Description = "Cheese 1kg", Quantity = 2, Price = 50 }]
        },
        VanUser,
        DeviceInfo: null);

    private CreateVanSalesSalesOrderHandler Handler() =>
        new(_context, new SalesOrderMediator(CreateSalesOrderHandler()), NullLogger<CreateVanSalesSalesOrderHandler>.Instance);

    private CreateSalesOrderHandler CreateSalesOrderHandler() =>
        new(
            new SalesOrderService(
                _context,
                StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
                {
                    nameof(ISAPServiceLayerClient.GetItemsByCodesAsync) =>
                        ReadItemsAsync((CancellationToken)args![1]!),
                    _ => throw new InvalidOperationException($"ISAPServiceLayerClient.{method.Name} was not expected.")
                }),
                NullLogger<SalesOrderService>.Instance,
                new NoOpNotificationService(),
                StubProxy.Unused<IBusinessPartnerService>(),
                StubProxy.Unused<ILocalPriceCatalogService>(),
                StubProxy.Unused<IIdempotencyRequestStore>(),
                StubProxy.Unused<ICreditLimitService>(),
                Options.Create(new TaxSettings { VatRate = 0m })),
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            NullLogger<CreateSalesOrderHandler>.Instance);

    /// <summary>
    /// The Service Layer's item read. It honours the token it is handed, as the real client does; a stub
    /// that ignored it would let the hang-up test pass against the very code it was written to catch.
    /// </summary>
    private Task<Dictionary<string, Item>> ReadItemsAsync(CancellationToken cancellationToken)
    {
        _sapItemReads++;
        _sapItemReadStarted?.Invoke();

        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<Dictionary<string, Item>>(cancellationToken)
            : Task.FromResult(new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase)
            {
                ["CHE011"] = new Item { ItemCode = "CHE011", SalesUnit = "EA" }
            });
    }

    /// <summary>Hands <see cref="CreateSalesOrderCommand"/> to the real handler, with the token it was sent.</summary>
    private sealed class SalesOrderMediator(CreateSalesOrderHandler handler) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            ErrorOr<SalesOrderDto> result = await handler.Handle((CreateSalesOrderCommand)(object)request, cancellationToken);
            return (TResponse)(object)result;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => throw new NotSupportedException();
    }

    /// <summary>See MobileOrderCreditHoldTests: SQLite has no store-generated row version.</summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SalesOrderEntity>()
                .Property(order => order.RowVersion)
                .IsConcurrencyToken(false)
                .HasDefaultValue(new byte[] { 1 });
        }
    }
}
