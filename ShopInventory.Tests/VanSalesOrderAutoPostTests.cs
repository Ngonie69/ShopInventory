using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Mobile;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A van sales order is posted to SAP by the post-save queue instead of waiting for approval on the web,
/// so the invoice the van raises against it can be based on it. Merchandiser orders are unchanged.
/// </summary>
/// <remarks>
/// The post itself takes a Postgres advisory lock, which SQLite cannot give, so no test here reaches
/// SAP. What they pin is the routing around it: which orders the stage runs for, that a failed post is
/// retried rather than written off, and that nothing outside the van handler can ask for it.
/// </remarks>
public sealed class VanSalesOrderAutoPostTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesOrderAutoPostTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new ShopInventory.Models.User
        {
            Id = Rep,
            Username = "van14",
            PasswordHash = "not-a-real-hash",
            Role = "Sales"
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_van_sales_order_is_queued_for_auto_post()
    {
        var order = await CreateService().CreateAsync(NewRequest(autoPost: true), Rep);

        var entry = await QueueEntryAsync(order.Id);
        Assert.True(entry.AutoPostToSap);
        Assert.Null(entry.AutoPostedAt);
    }

    /// <summary>
    /// Pricing and the notification are stamped and not repeated, but the entry is not closed while the
    /// post is still owed: it goes back on the queue with backoff, and a persistent failure reaches the
    /// exception centre. Closing it would strand the order Pending with no invoice able to link to it.
    /// </summary>
    [Fact]
    public async Task A_van_order_whose_post_fails_is_retried_rather_than_completed()
    {
        var service = CreateService();
        var order = await service.CreateAsync(NewRequest(autoPost: true), Rep);

        await service.ProcessMobileOrderPostSaveAsync(order.Id);

        var entry = await QueueEntryAsync(order.Id);
        Assert.Equal(MobileOrderPostProcessingQueueStatus.Failed, entry.Status);
        Assert.Equal(1, entry.RetryCount);
        Assert.NotNull(entry.NextRetryAt);
        Assert.NotNull(entry.LastError);
        Assert.NotNull(entry.PricesResolvedAt);
        Assert.NotNull(entry.NotificationSentAt);
        Assert.Null(entry.AutoPostedAt);

        var stored = await _context.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(SalesOrderStatus.Pending, stored.Status);
        Assert.Null(stored.SAPDocEntry);
    }

    [Fact]
    public async Task A_merchandiser_order_still_waits_for_approval_on_the_web()
    {
        var service = CreateService();
        var order = await service.CreateAsync(NewRequest(autoPost: false), Rep);

        await service.ProcessMobileOrderPostSaveAsync(order.Id);

        var entry = await QueueEntryAsync(order.Id);
        Assert.False(entry.AutoPostToSap);
        Assert.Equal(MobileOrderPostProcessingQueueStatus.Completed, entry.Status);
        Assert.Null(entry.AutoPostedAt);

        var stored = await _context.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(SalesOrderStatus.Pending, stored.Status);
    }

    /// <summary>An order someone cancelled before the queue reached it is not resurrected into SAP.</summary>
    [Fact]
    public async Task A_van_order_cancelled_before_the_post_is_left_alone()
    {
        var service = CreateService();
        var order = await service.CreateAsync(NewRequest(autoPost: true), Rep);

        await _context.SalesOrders
            .Where(o => o.Id == order.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.Status, SalesOrderStatus.Cancelled));
        _context.ChangeTracker.Clear();

        await service.ProcessMobileOrderPostSaveAsync(order.Id);

        var entry = await QueueEntryAsync(order.Id);
        Assert.Equal(MobileOrderPostProcessingQueueStatus.Completed, entry.Status);
        Assert.NotNull(entry.AutoPostedAt);

        var stored = await _context.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(SalesOrderStatus.Cancelled, stored.Status);
    }

    /// <summary>Bound from a request body, the flag would let any caller skip approval.</summary>
    [Fact]
    public void A_request_body_cannot_ask_for_auto_post()
    {
        var request = JsonSerializer.Deserialize<CreateSalesOrderRequest>(
            """{"cardCode":"VAN014","currency":"USD","autoPostToSap":true,"AutoPostToSap":true,"lines":[]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.False(request.AutoPostToSap);
    }

    [Fact]
    public void The_van_sales_sales_order_endpoint_asks_for_auto_post()
    {
        var request = VanSalesCompatibilityMapper.MapSalesOrderRequest(
            new VanSalesOrderRequest
            {
                CustomerCode = "VAN014",
                Type = "SO",
                VanOrder = "VO-20260916-0007",
                Items = [new VanSalesOrderItemRequest { Code = "NRI049", Quantity = 4, Price = 60 }]
            },
            new VanSalesCustomerResolution("VAN014", null),
            warehouseCode: "VAN14",
            costCentreCode: "CC14",
            deviceInfo: null);

        Assert.True(request.AutoPostToSap);
        Assert.Equal(SalesOrderSource.Mobile, request.Source);
    }

    private async Task<MobileOrderPostProcessingQueueEntity> QueueEntryAsync(int salesOrderId)
    {
        _context.ChangeTracker.Clear();
        return await _context.MobileOrderPostProcessingQueue
            .AsNoTracking()
            .SingleAsync(entry => entry.SalesOrderId == salesOrderId);
    }

    private static CreateSalesOrderRequest NewRequest(bool autoPost) =>
        new()
        {
            CardCode = "VAN014",
            CardName = "Van 14",
            Currency = "USD",
            Source = SalesOrderSource.Mobile,
            AutoPostToSap = autoPost,
            Lines =
            {
                new CreateSalesOrderLineRequest
                {
                    ItemCode = "NRI049",
                    Quantity = 4,
                    UnitPrice = 0m,
                    UoMCode = "EA"
                }
            }
        };

    private SalesOrderService CreateService()
    {
        var creditLimitService = StubProxy.For<ICreditLimitService>((method, _) =>
            method.Name == nameof(ICreditLimitService.CheckSalesOrderAsync)
                ? Task.FromResult(CreditLimitCheckResult.Allowed())
                : throw new InvalidOperationException($"Unexpected call to {method.Name}"));

        var priceCatalog = StubProxy.For<ILocalPriceCatalogService>((method, _) =>
            method.Name == nameof(ILocalPriceCatalogService.GetBusinessPartnerPricingAsync)
                ? Task.FromResult<LocalBusinessPartnerPricingResult?>(new LocalBusinessPartnerPricingResult
                {
                    BusinessPartner = new BusinessPartnerDto { CardCode = "VAN014" },
                    Prices = new ItemPricesByListResponseDto
                    {
                        Prices = [new() { ItemCode = "NRI049", Price = 60m }]
                    }
                })
                : throw new InvalidOperationException($"Unexpected call to {method.Name}"));

        return new SalesOrderService(
            _context,
            StubProxy.Unused<ISAPServiceLayerClient>(),
            NullLogger<SalesOrderService>.Instance,
            new NoOpNotificationService(),
            StubProxy.Unused<IBusinessPartnerService>(),
            priceCatalog,
            StubProxy.Unused<IIdempotencyRequestStore>(),
            creditLimitService,
            Options.Create(new TaxSettings { VatRate = 0m }));
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
