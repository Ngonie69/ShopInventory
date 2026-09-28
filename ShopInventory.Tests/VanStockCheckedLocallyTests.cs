using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// While SAP is down, a van's sale is checked against what this system knows the van is carrying,
/// instead of being refused because SAP cannot answer.
/// </summary>
/// <remarks>
/// Before this, every online van sale during an outage failed at the reservation's SAP stock read, and
/// the handset has no path to sell without the server. The policy (2026-09-28): refuse a shortfall, as
/// a till does, and refuse a van that has not filed its opening count.
/// </remarks>
public sealed class VanStockCheckedLocallyTests : IDisposable
{
    private const string Van = "VAN005";
    private const string Depot = "KEFDEP";
    private const string Item = "YOG144";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly SapAvailability _availability;
    private int _sapStockReads;

    public VanStockCheckedLocallyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new SnapshotSqliteContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "van5",
            PasswordHash = "x",
            Role = "VanSales",
            IsActive = true,
            AssignedWarehouseCodes = JsonSerializer.Serialize(new[] { Van }),
            SupplyingWarehouseCode = Depot
        });
        _context.SaveChanges();

        _availability = new SapAvailability(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SapAvailability>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task During_an_outage_a_counted_van_sells_what_it_carries_without_asking_sap()
    {
        SapIsDown();
        await GivenOpeningCountAsync(10m);

        var (valid, errors) = await Service().ValidateStockAvailabilityAsync([Line(Van, 4m)]);

        Assert.True(valid, string.Join("; ", errors.Select(e => e.Message)));
        Assert.Equal(0, _sapStockReads);
    }

    [Fact]
    public async Task During_an_outage_a_sale_the_van_cannot_cover_is_refused()
    {
        SapIsDown();
        await GivenOpeningCountAsync(10m);
        await GivenSaleTodayAsync("VAN5-OFFLINE-1", SaleSourceSystems.VanSales, 7m);

        var (valid, errors) = await Service().ValidateStockAvailabilityAsync([Line(Van, 4m)]);

        Assert.False(valid);
        var error = Assert.Single(errors);
        Assert.Equal(ReservationErrorCode.InsufficientStock, error.ErrorCode);
        Assert.Equal(3m, error.AvailableQuantity);
    }

    /// <summary>
    /// An online sale made during the outage is both a sale row and a live hold until SAP takes it.
    /// Counting it as both would take its units off twice and refuse a sale the van can make.
    /// </summary>
    [Fact]
    public async Task An_online_sale_still_holding_its_stock_is_counted_once()
    {
        SapIsDown();
        await GivenOpeningCountAsync(10m);
        await GivenSaleTodayAsync("VAN5-ONLINE-1", SaleSourceSystems.VanSalesOnline, 5m);
        await GivenPendingReservationAsync("VAN5-ONLINE-1", 5m);

        var (valid, errors) = await Service().ValidateStockAvailabilityAsync([Line(Van, 5m)]);

        Assert.True(valid, string.Join("; ", errors.Select(e => e.Message)));
    }

    [Fact]
    public async Task During_an_outage_a_van_that_has_not_counted_is_refused_with_a_reason_it_can_act_on()
    {
        SapIsDown();

        var (valid, errors) = await Service().ValidateStockAvailabilityAsync([Line(Van, 1m)]);

        Assert.False(valid);
        Assert.Equal(ReservationErrorCode.StockNotCounted, Assert.Single(errors).ErrorCode);
        Assert.Equal(0, _sapStockReads);
    }

    [Fact]
    public async Task An_item_the_van_does_not_carry_reads_as_none_left_rather_than_unknown()
    {
        SapIsDown();
        await GivenOpeningCountAsync(10m);

        var (valid, errors) = await Service().ValidateStockAvailabilityAsync([Line(Van, 1m, item: "CHE011")]);

        Assert.False(valid);
        Assert.Equal(ReservationErrorCode.InsufficientStock, Assert.Single(errors).ErrorCode);
    }

    [Fact]
    public async Task With_sap_up_the_van_is_checked_against_sap_as_before()
    {
        await GivenOpeningCountAsync(10m);

        var (valid, _) = await Service(sapInStock: 2m).ValidateStockAvailabilityAsync([Line(Van, 4m)]);

        Assert.False(valid);
        Assert.Equal(1, _sapStockReads);
    }

    [Fact]
    public async Task During_an_outage_a_warehouse_that_is_not_a_van_still_asks_sap()
    {
        SapIsDown();

        await Service(sapInStock: 50m).ValidateStockAvailabilityAsync([Line("KEFGRS", 4m)]);

        Assert.Equal(1, _sapStockReads);
    }

    // ── Making the reservation, and posting it later ─────

    /// <summary>
    /// The stock check passing was not enough: creating the reservation asked SAP whether each item is
    /// batch-managed, and that read throws while SAP is down.
    /// </summary>
    [Fact]
    public async Task During_an_outage_a_van_reservation_is_made_without_asking_sap_anything()
    {
        SapIsDown();
        await GivenOpeningCountAsync(10m);

        var created = await Service(batchValidation: StubProxy.Unused<IBatchInventoryValidationService>())
            .CreateReservationAsync(new CreateStockReservationRequest
            {
                ExternalReferenceId = "VAN5-ONLINE-9",
                SourceSystem = SaleSourceSystems.VanSales,
                CardCode = "VAN010",
                Currency = "USD",
                ReservationDurationMinutes = 60,
                Lines = [Line(Van, 4m, uom: "CS")]
            }, "van5");

        Assert.True(created.Success, created.Message + " " + string.Join("; ", created.Errors?.Select(e => e.Message) ?? []));
        var line = Assert.Single(Assert.Single(await _context.StockReservations.Include(r => r.Lines).ThenInclude(l => l.BatchAllocations).ToListAsync()).Lines);
        Assert.Equal(4m, line.ReservedQuantity);
        Assert.Empty(line.BatchAllocations);
        Assert.Equal(0, _sapStockReads);
    }

    [Fact]
    public async Task At_posting_a_batch_managed_line_without_batches_gets_them_and_the_others_are_left_alone()
    {
        var allocation = new RecordingAllocation();
        var request = new CreateInvoiceRequest
        {
            CardCode = "VAN010",
            Lines =
            [
                new CreateInvoiceLineRequest { ItemCode = "CHE011", Quantity = 2, WarehouseCode = Van,
                    BatchNumbers = [new BatchNumberRequest { BatchNumber = "KEEP-ME", Quantity = 2 }] },
                new CreateInvoiceLineRequest { ItemCode = Item, Quantity = 4, WarehouseCode = Van },
                new CreateInvoiceLineRequest { ItemCode = "CON020", Quantity = 1, WarehouseCode = Van }
            ]
        };

        await Service(batchValidation: allocation.Stub).AllocateMissingBatchesAsync(
            new StockReservationEntity { ReservationId = "R-1" }, request, default);

        Assert.Equal("KEEP-ME", Assert.Single(request.Lines[0].BatchNumbers!).BatchNumber);
        Assert.Equal("FEFO-1", Assert.Single(request.Lines[1].BatchNumbers!).BatchNumber);
        Assert.True(request.Lines[2].BatchNumbers is not { Count: > 0 });

        // Only the line that needed it went to the allocator, with this reservation's own hold set aside.
        Assert.Equal([Item], allocation.AllocatedItems);
        Assert.Equal(["R-1"], allocation.Disregarded);
    }

    [Fact]
    public async Task At_posting_an_allocation_that_fails_stops_the_post_with_the_reason()
    {
        var allocation = new RecordingAllocation { Fails = true };
        var request = new CreateInvoiceRequest
        {
            CardCode = "VAN010",
            Lines = [new CreateInvoiceLineRequest { ItemCode = Item, Quantity = 4, WarehouseCode = Van }]
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(batchValidation: allocation.Stub).AllocateMissingBatchesAsync(
                new StockReservationEntity { ReservationId = "R-2" }, request, default));

        Assert.Contains("reservation R-2", failure.Message);
        Assert.Contains("only 1 left", failure.Message);
    }

    [Fact]
    public async Task At_posting_a_reservation_with_all_its_batches_is_not_touched()
    {
        var allocation = new RecordingAllocation();
        var request = new CreateInvoiceRequest
        {
            CardCode = "VAN010",
            Lines = [new CreateInvoiceLineRequest { ItemCode = "CON020", Quantity = 1, WarehouseCode = Van }]
        };

        await Service(batchValidation: allocation.Stub).AllocateMissingBatchesAsync(
            new StockReservationEntity { ReservationId = "R-3" }, request, default);

        Assert.Empty(allocation.AllocatedItems);
        Assert.Empty(allocation.Disregarded);
    }

    private sealed class RecordingAllocation
    {
        public bool Fails { get; init; }
        public List<string> AllocatedItems { get; } = [];
        public List<string> Disregarded { get; } = [];

        public IBatchInventoryValidationService Stub => StubProxy.For<IBatchInventoryValidationService>((method, args) => method.Name switch
        {
            nameof(IBatchInventoryValidationService.IsBatchManagedItemAsync) =>
                Task.FromResult((string)args![0]! is Item or "CHE011"),
            nameof(IBatchInventoryValidationService.DisregardReservations) => Disregard((IEnumerable<string>)args![0]!),
            nameof(IBatchInventoryValidationService.ValidateAndAllocateBatchesAsync) => Allocate((CreateInvoiceRequest)args![0]!),
            _ => throw new InvalidOperationException($"Unexpected batch validation call: {method.Name}")
        });

        private IDisposable Disregard(IEnumerable<string> ids)
        {
            Disregarded.AddRange(ids);
            return new Released();
        }

        private Task<BatchAllocationResult> Allocate(CreateInvoiceRequest request)
        {
            AllocatedItems.AddRange(request.Lines!.Select(line => line.ItemCode!));

            if (Fails)
            {
                return Task.FromResult(new BatchAllocationResult
                {
                    ValidationErrors = [new BatchValidationErrorDto { Message = "YOG144: only 1 left in VAN005" }]
                });
            }

            return Task.FromResult(new BatchAllocationResult
            {
                AllocatedLines = request.Lines!.Select((line, index) => new AllocatedBatchLine
                {
                    LineNumber = index + 1,
                    ItemCode = line.ItemCode!,
                    IsBatchManaged = true,
                    Batches = [new AllocatedBatch { BatchNumber = "FEFO-1", QuantityAllocated = line.Quantity }]
                }).ToList()
            });
        }

        private sealed class Released : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private void SapIsDown() =>
        _availability.Apply(new SapAvailabilityState(1, DateTime.UtcNow.AddHours(-1), SapOutageCauses.Unreachable));

    private StockReservationService Service(
        decimal sapInStock = 0m,
        IBatchInventoryValidationService? batchValidation = null) => new(
        _context,
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync) => ReadSap(
                (string)args![0]!, (IEnumerable<string>)args[1]!, sapInStock),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        }),
        batchValidation ?? StubProxy.For<IBatchInventoryValidationService>((method, _) => method.Name switch
        {
            nameof(IBatchInventoryValidationService.IsBatchManagedItemAsync) => Task.FromResult(false),
            _ => throw new InvalidOperationException($"Unexpected batch validation call: {method.Name}")
        }),
        StubProxy.Unused<IInventoryLockService>(),
        StubProxy.Unused<IStockLedger>(),
        StubProxy.Unused<IInvoiceFiscalizationQueue>(),
        StubProxy.Unused<INotificationService>(),
        NullLogger<StockReservationService>.Instance,
        new SapCircuitBreakerState(Options.Create(new SAPSettings()), null, _availability));

    private Task<List<StockQuantityDto>> ReadSap(string warehouse, IEnumerable<string> items, decimal inStock)
    {
        _sapStockReads++;
        return Task.FromResult(items
            .Select(item => new StockQuantityDto { ItemCode = item, WarehouseCode = warehouse, InStock = inStock })
            .ToList());
    }

    private static CreateStockReservationLineRequest Line(
        string warehouse, decimal quantity, string item = Item, string? uom = null) => new()
    {
        LineNum = 1,
        ItemCode = item,
        WarehouseCode = warehouse,
        Quantity = quantity,
        UoMCode = uom
    };

    private async Task GivenOpeningCountAsync(decimal quantity)
    {
        var snapshot = new DailyStockSnapshotEntity
        {
            SnapshotDate = AuditService.ToCAT(DateTime.UtcNow).Date,
            WarehouseCode = Van,
            Status = StockSnapshotStatus.Complete,
            CreatedAt = DateTime.UtcNow
        };
        _context.DailyStockSnapshots.Add(snapshot);
        await _context.SaveChangesAsync();

        _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = Item,
            ItemDescription = "Yoghurt",
            WarehouseCode = Van,
            BatchNumber = "B-1",
            Version = 1
        }.Opened(quantity));
        await _context.SaveChangesAsync();
    }

    private async Task GivenSaleTodayAsync(string reference, string source, decimal quantity)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = source,
            CardCode = "VAN010",
            WarehouseCode = Van,
            Currency = "USD",
            DocDate = AuditService.ToCAT(DateTime.UtcNow).Date,
            CreatedAt = DateTime.UtcNow,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 1, ItemCode = Item, Quantity = quantity, UnitPrice = 1m, LineTotal = quantity, WarehouseCode = Van
                }
            ]
        });
        await _context.SaveChangesAsync();
    }

    private async Task GivenPendingReservationAsync(string reference, decimal quantity)
    {
        _context.StockReservations.Add(new StockReservationEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "VAN010",
            Currency = "USD",
            Status = ReservationStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 1, ItemCode = Item, WarehouseCode = Van, OriginalQuantity = quantity, ReservedQuantity = quantity
                }
            ]
        });
        await _context.SaveChangesAsync();
    }
}
