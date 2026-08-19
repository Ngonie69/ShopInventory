using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A transfer reaches this queue because SAP could not be reached when it was captured, so the
/// batches on it were chosen against stock nobody could see and the replay can be hours later.
/// The drain used to post that selection with no stock check of any kind, and to keep retrying
/// the rejection that came back.
/// </summary>
public sealed class InventoryTransferQueueDrainTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;

    public InventoryTransferQueueDrainTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_selection_the_warehouse_no_longer_covers_is_never_posted()
    {
        var sap = new RecordingSap();
        await GivenQueuedTransferAsync();

        await DrainAsync(sap, StockShortOf("YOG144", "YOG144/H06/26", requested: 12, available: 4));

        Assert.Equal(0, sap.TransferPosts);
        var entry = await ReloadAsync();
        Assert.Equal(InventoryTransferQueueStatus.RequiresReview, entry.Status);
        Assert.Contains("YOG144/H06/26", entry.LastError);
    }

    [Fact]
    public async Task A_rejection_a_retry_cannot_clear_goes_straight_to_review()
    {
        var sap = new RecordingSap
        {
            PostFailure = new InvalidOperationException(
                "SAP rejected transfer due to insufficient stock: 10001153 - Insufficient quantity "
                + "for item YOG144 with batch YOG144/H06/26 in warehouse")
        };
        await GivenQueuedTransferAsync();

        await DrainAsync(sap, StockAvailable());

        var entry = await ReloadAsync();
        Assert.Equal(1, entry.RetryCount);
        Assert.True(entry.RetryCount < entry.MaxRetries, "attempts were left, and they must not be spent on this");
        Assert.Equal(InventoryTransferQueueStatus.RequiresReview, entry.Status);
        Assert.Null(entry.NextRetryAt);
    }

    [Fact]
    public async Task A_failure_that_may_pass_next_time_waits_for_its_backoff()
    {
        var sap = new RecordingSap
        {
            PostFailure = new InvalidOperationException("The SAP Service Layer timed out")
        };
        await GivenQueuedTransferAsync();

        var before = DateTime.UtcNow;
        await DrainAsync(sap, StockAvailable());

        var entry = await ReloadAsync();
        Assert.Equal(InventoryTransferQueueStatus.Failed, entry.Status);
        // Nothing wrote this field before, so the backoff the poster computed was discarded and
        // the entry came round again on the next ten-second tick.
        Assert.NotNull(entry.NextRetryAt);
        Assert.True(entry.NextRetryAt > before, $"NextRetryAt {entry.NextRetryAt} is not in the future");
    }

    [Fact]
    public async Task A_transfer_the_warehouse_still_covers_is_posted()
    {
        var sap = new RecordingSap();
        await GivenQueuedTransferAsync();

        await DrainAsync(sap, StockAvailable());

        var entry = await ReloadAsync();
        Assert.Null(entry.LastError);
        Assert.Equal(1, sap.TransferPosts);
        Assert.Equal(InventoryTransferQueueStatus.Completed, entry.Status);
    }

    private async Task GivenQueuedTransferAsync()
    {
        var payload = new CreateDesktopTransferRequest
        {
            ExternalReference = "DESK-1",
            FromWarehouse = "WH-1",
            ToWarehouse = "WH-2",
            IsTransferRequest = false,
            Lines =
            [
                new()
                {
                    LineNum = 0,
                    ItemCode = "YOG144",
                    Quantity = 12,
                    BatchNumbers = [new() { BatchNumber = "YOG144/H06/26", Quantity = 12 }]
                }
            ]
        };

        _context.InventoryTransferQueue.Add(new InventoryTransferQueueEntity
        {
            ExternalReference = "DESK-1",
            FromWarehouse = "WH-1",
            ToWarehouse = "WH-2",
            TransferPayload = JsonSerializer.Serialize(payload),
            Status = InventoryTransferQueueStatus.Pending,
            SourceSystem = "DESKTOP_APP",
            // A direct transfer, which moves stock. A transfer request moves none and is posted
            // by the other branch of the drain.
            IsTransferRequest = false
        });
        await _context.SaveChangesAsync();
    }

    private async Task<InventoryTransferQueueEntity> ReloadAsync()
    {
        await using var context = new ApplicationDbContext(_options);
        return await context.InventoryTransferQueue.AsNoTracking().SingleAsync();
    }

    private async Task DrainAsync(RecordingSap sap, IStockValidationService stockValidation)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContext(_options));
        services.AddScoped<IInventoryTransferQueueService>(provider => new InventoryTransferQueueService(
            provider.GetRequiredService<ApplicationDbContext>(),
            NullLogger<InventoryTransferQueueService>.Instance));
        services.AddScoped(_ => sap.AsClient());
        services.AddScoped(_ => stockValidation);

        var job = new InventoryTransferPostingJob(
            services.BuildServiceProvider(),
            NullLogger<InventoryTransferPostingJob>.Instance);

        await job.Execute(StubProxy.For<IJobExecutionContext>((method, _) => method.Name switch
        {
            "get_CancellationToken" => CancellationToken.None,
            _ => throw new InvalidOperationException($"Unexpected Quartz call: {method.Name}")
        }));
    }

    private static IStockValidationService StockAvailable() =>
        StubProxy.For<IStockValidationService>((method, _) => method.Name switch
        {
            nameof(IStockValidationService.ValidateInventoryTransferStockAsync) =>
                Task.FromResult(new StockValidationResult()),
            _ => throw new InvalidOperationException($"Unexpected validation call: {method.Name}")
        });

    private static IStockValidationService StockShortOf(
        string itemCode,
        string batchNumber,
        decimal requested,
        decimal available) =>
        StubProxy.For<IStockValidationService>((method, _) => method.Name switch
        {
            nameof(IStockValidationService.ValidateInventoryTransferStockAsync) =>
                Task.FromResult(new StockValidationResult
                {
                    Errors =
                    [
                        new()
                        {
                            LineNumber = 1,
                            ItemCode = itemCode,
                            WarehouseCode = "WH-1",
                            BatchNumber = batchNumber,
                            RequestedQuantity = requested,
                            AvailableQuantity = available
                        }
                    ]
                }),
            _ => throw new InvalidOperationException($"Unexpected validation call: {method.Name}")
        });

    private sealed class RecordingSap
    {
        public int TransferPosts { get; private set; }
        public Exception? PostFailure { get; init; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.CreateInventoryTransferAsync) => Post(),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<InventoryTransfer> Post()
        {
            TransferPosts++;
            return PostFailure is not null
                ? Task.FromException<InventoryTransfer>(PostFailure)
                : Task.FromResult(new InventoryTransfer
                {
                    DocEntry = 9001,
                    DocNum = 9001,
                    FromWarehouse = "WH-1",
                    ToWarehouse = "WH-2"
                });
        }
    }
}
