using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pending reservations are taken off SAP's stock by every instance of the batch validator, not one.
/// </summary>
/// <remarks>
/// <para>
/// The validator is scoped, and it used to be handed its reservation provider through a setter called
/// once at startup, inside startup's own scope. That reached one instance. Every request and job
/// resolved a fresh one that had never been given a provider and netted nothing off, so a web invoice
/// or a posting job would allocate units a reservation was holding for someone else.
/// </para>
///
/// <para>
/// The first tests resolve the validator the way the application does — from a fresh scope of a
/// provider built with <see cref="StockReservationServiceCollectionExtensions.AddStockReservations"/> —
/// so they fail if the wiring ever stops reaching request scopes again. The rest pin the two places a
/// reservation must <i>not</i> count: against the sale that owns it, and after its invoice has posted.
/// </para>
/// </remarks>
public sealed class ReservedStockNettingTests : IDisposable
{
    private const string Item = "CHE011";
    private const string Warehouse = "KEFSHOP";
    private const string Batch = "B-1";
    private const string CardCode = "ABS006";
    private const decimal InSap = 10m;
    private static readonly DateTime SaleDate = new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ReservedStockNettingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(ContextOptions());
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---------------------------------------------------------------
    // The wiring
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_request_scope_nets_a_pending_reservation_off_the_batch_it_holds()
    {
        await using var services = BuildApplicationServices();

        // The startup scope: the one instance the old setter ever reached.
        using (var startup = services.CreateScope())
        {
            startup.ServiceProvider.GetRequiredService<IBatchInventoryValidationService>();
        }

        await HoldAsync(6m);

        using var request = services.CreateScope();
        var validator = request.ServiceProvider.GetRequiredService<IBatchInventoryValidationService>();

        var result = await validator.ValidateAndAllocateBatchesAsync(InvoiceFor(5m));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal(BatchValidationErrorCode.InsufficientTotalStock, error.ErrorCode);
        Assert.Equal(InSap - 6m, error.AvailableQuantity);
    }

    [Fact]
    public async Task A_request_scope_allocates_what_the_reservation_leaves()
    {
        await using var services = BuildApplicationServices();
        await HoldAsync(6m);

        using var request = services.CreateScope();
        var validator = request.ServiceProvider.GetRequiredService<IBatchInventoryValidationService>();

        var result = await validator.ValidateAndAllocateBatchesAsync(InvoiceFor(4m));

        Assert.True(result.IsValid, Describe(result));
        Assert.Equal(4m, Assert.Single(Assert.Single(result.AllocatedLines).Batches).QuantityAllocated);
    }

    /// <summary>
    /// The negative control. The same invoice against a validator with no provider — which is what
    /// every request scope had — takes units the reservation is holding.
    /// </summary>
    [Fact]
    public async Task Without_a_provider_the_reserved_units_are_allocated_anyway()
    {
        await HoldAsync(6m);

        var validator = new BatchInventoryValidationService(
            _context,
            BatchManagedSap(),
            StubProxy.Unused<IInventoryLockService>(),
            Microsoft.Extensions.Options.Options.Create(new SAPSettings()),
            NullLogger<BatchInventoryValidationService>.Instance);

        var result = await validator.ValidateAndAllocateBatchesAsync(InvoiceFor(5m));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Batch_validation_and_reservations_resolve_together_without_a_cycle()
    {
        // ValidateOnBuild walks every registration, so a provider that went back to depending on the
        // reservation service — which depends on the validator — fails here rather than in production.
        await using var services = BuildApplicationServices();
        using var scope = services.CreateScope();

        var validator = scope.ServiceProvider.GetRequiredService<IBatchInventoryValidationService>();
        var reservations = scope.ServiceProvider.GetRequiredService<IStockReservationService>();

        Assert.NotNull(validator);
        Assert.NotNull(reservations);
    }

    // ---------------------------------------------------------------
    // When a reservation must not count
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_disregarded_reservation_is_not_netted_off_until_its_scope_ends()
    {
        var reservationId = await HoldAsync(8m);
        var validator = ValidatorWithProvider();

        using (validator.DisregardReservations([reservationId]))
        {
            var own = await validator.ValidateAndAllocateBatchesAsync(InvoiceFor(8m));
            Assert.True(own.IsValid, Describe(own));
        }

        var afterwards = await validator.ValidateAndAllocateBatchesAsync(InvoiceFor(8m));
        Assert.False(afterwards.IsValid);
    }

    [Fact]
    public async Task Disregarding_one_reservation_still_counts_the_others()
    {
        var own = await HoldAsync(5m);
        await HoldAsync(3m);
        var validator = ValidatorWithProvider();

        using (validator.DisregardReservations([own]))
        {
            var result = await validator.ValidateAndAllocateBatchesAsync(InvoiceFor(8m));

            Assert.False(result.IsValid);
            Assert.Equal(InSap - 3m, Assert.Single(result.ValidationErrors).AvailableQuantity);
        }
    }

    [Fact]
    public async Task A_reservation_whose_queued_invoice_has_posted_holds_nothing()
    {
        // Consolidation marks the queue entry Completed and leaves the reservation Pending until it
        // expires. SAP has issued the units by then, so counting the reservation too takes them twice.
        var reservationId = await HoldAsync(8m);
        _context.InvoiceQueue.Add(QueueEntry(reservationId, InvoiceQueueStatus.Completed));
        await _context.SaveChangesAsync();

        var result = await ValidatorWithProvider().ValidateAndAllocateBatchesAsync(InvoiceFor(8m));

        Assert.True(result.IsValid, Describe(result));
    }

    [Fact]
    public async Task A_reservation_whose_queued_invoice_is_still_waiting_holds_its_stock()
    {
        var reservationId = await HoldAsync(8m);
        _context.InvoiceQueue.Add(QueueEntry(reservationId, InvoiceQueueStatus.Fiscalized));
        await _context.SaveChangesAsync();

        var result = await ValidatorWithProvider().ValidateAndAllocateBatchesAsync(InvoiceFor(8m));

        Assert.False(result.IsValid);
    }

    // ---------------------------------------------------------------
    // End-of-day consolidation, which posts sales that own reservations
    // ---------------------------------------------------------------

    [Fact]
    public async Task Consolidation_does_not_net_a_queued_sale_own_reservation_off_its_invoice()
    {
        // A queued sale of 8 still holds its reservation of 8. SAP shows 10. Netted off against itself
        // the sale would see 2 and the day's invoice would be refused.
        var reservationId = await HoldAsync(8m);
        var queued = QueueEntry(reservationId, InvoiceQueueStatus.Fiscalized);
        queued.Id = 77;
        _context.InvoiceQueue.Add(queued);
        await _context.SaveChangesAsync();

        CreateInvoiceRequest? posted = null;
        var sap = BatchManagedSap(onPost: request => posted = request);

        var handler = new ConsolidateDailySalesHandler(
            _context,
            sap,
            new BatchInventoryValidationService(
                _context,
                sap,
                StubProxy.Unused<IInventoryLockService>(),
                Microsoft.Extensions.Options.Options.Create(new SAPSettings()),
                NullLogger<BatchInventoryValidationService>.Instance,
                new ReservedQuantityProvider(_context)),
            QueueService([queued]),
            StubProxy.For<INotificationService>((_, _) => Task.FromResult(new NotificationDto())),
            ConsolidationDuplicatePostGuardTests.BuildHubContext(),
            ConsolidationDuplicatePostGuardTests.BuildSender(),
            new RecordingAuditService(),
            NullLogger<ConsolidateDailySalesHandler>.Instance);

        var result = await handler.Handle(new ConsolidateDailySalesCommand(SaleDate), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.Equal(1, result.Value.SuccessfulPostings);

        var line = Assert.Single(posted!.Lines!);
        var batch = Assert.Single(line.BatchNumbers!);
        Assert.Equal(Batch, batch.BatchNumber);
        Assert.Equal(8m, batch.Quantity);
    }

    // ---------------------------------------------------------------

    private DbContextOptions<ApplicationDbContext> ContextOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

    /// <summary>The application's registration, with stand-ins for what lies outside it.</summary>
    private ServiceProvider BuildApplicationServices()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.Configure<SAPSettings>(_ => { });
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton(BatchManagedSap());
        services.AddSingleton(StubProxy.Unused<IInventoryLockService>());
        services.AddSingleton(StubProxy.Unused<IStockLedger>());
        services.AddSingleton(StubProxy.Unused<IInvoiceFiscalizationQueue>());
        services.AddSingleton(StubProxy.Unused<INotificationService>());

        services.AddStockReservations();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private BatchInventoryValidationService ValidatorWithProvider() =>
        new(
            _context,
            BatchManagedSap(),
            StubProxy.Unused<IInventoryLockService>(),
            Microsoft.Extensions.Options.Options.Create(new SAPSettings()),
            NullLogger<BatchInventoryValidationService>.Instance,
            new ReservedQuantityProvider(_context));

    /// <summary>A pending, unexpired reservation holding this many units of the batch.</summary>
    private async Task<string> HoldAsync(decimal quantity)
    {
        var reservation = new StockReservationEntity
        {
            ExternalReferenceId = Guid.NewGuid().ToString("N"),
            SourceSystem = "TEST",
            CardCode = CardCode,
            Status = ReservationStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = Item,
                    WarehouseCode = Warehouse,
                    ReservedQuantity = quantity,
                    OriginalQuantity = quantity,
                    BatchAllocations =
                    [
                        new StockReservationBatchEntity
                        {
                            ItemCode = Item,
                            WarehouseCode = Warehouse,
                            BatchNumber = Batch,
                            ReservedQuantity = quantity
                        }
                    ]
                }
            ]
        };

        _context.StockReservations.Add(reservation);
        await _context.SaveChangesAsync();
        return reservation.ReservationId;
    }

    private static InvoiceQueueEntity QueueEntry(string reservationId, InvoiceQueueStatus status) => new()
    {
        ReservationId = reservationId,
        ExternalReference = "POS-" + reservationId[..8],
        SourceSystem = "KefalosPos",
        CustomerCode = CardCode,
        TotalAmount = 480.00m,
        Currency = "ZWG",
        WarehouseCode = Warehouse,
        Status = status,
        CreatedAt = SaleDate,
        FiscalReceiptNumber = "KEF-0002",
        InvoicePayload = $$"""
            {"cardCode":"{{CardCode}}","cardName":"Absolute Traders","lines":[
              {"lineNum":0,"itemCode":"{{Item}}","quantity":8,"unitPrice":60.00,"warehouseCode":"{{Warehouse}}"}]}
            """
    };

    private static CreateInvoiceRequest InvoiceFor(decimal quantity) => new()
    {
        CardCode = CardCode,
        DocCurrency = "ZWG",
        Lines =
        [
            new()
            {
                ItemCode = Item,
                Quantity = quantity,
                UnitPrice = 60m,
                WarehouseCode = Warehouse
            }
        ]
    };

    /// <summary>A batch-managed item whose warehouse holds <see cref="InSap"/> units of one batch.</summary>
    private static ISAPServiceLayerClient BatchManagedSap(Action<CreateInvoiceRequest>? onPost = null) =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetItemByCodeAsync) => Task.FromResult<Item?>(new Item
            {
                ItemCode = Item,
                ManageBatchNumbers = "tYES",
                ManageSerialNumbers = "tNO"
            }),
            nameof(ISAPServiceLayerClient.GetBatchNumbersForItemInWarehouseAsync) =>
                Task.FromResult(new List<BatchNumber>
                {
                    new()
                    {
                        ItemCode = Item,
                        BatchNum = Batch,
                        Quantity = InSap,
                        ExpiryDate = "2026-12-01",
                        AdmissionDate = "2026-01-01"
                    }
                }),
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) => Task.FromResult<Invoice?>(null),
            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) when onPost is not null => Post(args, onPost),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

    private static Task<Invoice> Post(object?[]? args, Action<CreateInvoiceRequest> onPost)
    {
        onPost((CreateInvoiceRequest)args![0]!);
        return Task.FromResult(new Invoice { DocEntry = 74555, DocNum = 5140, CardCode = CardCode });
    }

    private static IInvoiceQueueService QueueService(List<InvoiceQueueEntity> entries) =>
        StubProxy.For<IInvoiceQueueService>((method, _) => method.Name switch
        {
            nameof(IInvoiceQueueService.GetFiscalizedInvoicesAsync) => Task.FromResult(entries),
            nameof(IInvoiceQueueService.MarkAsConsolidatedAsync) => Task.CompletedTask,
            _ => throw new InvalidOperationException($"IInvoiceQueueService.{method.Name} was not expected.")
        });

    private static string Describe(BatchAllocationResult result) =>
        string.Join("; ", result.ValidationErrors.Select(error => error.Message));
}

/// <summary>What a stubbed validator hands back from <see cref="IBatchInventoryValidationService.DisregardReservations"/>.</summary>
internal sealed class NoReservationsDisregarded : IDisposable
{
    public void Dispose()
    {
    }
}
