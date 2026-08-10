using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins where a mobile order meets the credit limit: at posting, not at capture.
/// </summary>
/// <remarks>
/// A mobile order's lines arrive unpriced and are valued server-side afterwards, so a credit check
/// at capture can only ever weigh the customer's standing balance — it cannot see the order it is
/// refusing. Refusing there cost the business the order outright: the API answered the phone with a
/// 400, the app filed it as a failed draft, and the order never reached anyone on the web who could
/// act on it. These tests exist so that behaviour cannot come back by way of "the earlier the
/// check, the better". Capture is not a control point; posting is.
/// </remarks>
public sealed class MobileOrderCreditHoldTests : IDisposable
{
    private const string OverLimitMessage =
        "This order would take Frugiparus Enterprises (FRU003) over its credit limit. " +
        "Credit limit USD 30,000.00, current balance USD 65,331.06, this order USD 0.00 — USD 35,331.06 over. " +
        "Take a payment against the account or reduce the order before submitting it again.";

    private static readonly Guid Rep = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<decimal> _creditChecks = new();

    public MobileOrderCreditHoldTests()
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
            Username = "rep",
            PasswordHash = "not-a-real-hash",
            Role = ShopInventory.Models.ApplicationRoles.Merchandiser
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_mobile_order_for_an_over_limit_customer_is_captured_rather_than_refused()
    {
        var service = CreateService(isWithinLimit: false);

        var order = await service.CreateAsync(NewRequest(SalesOrderSource.Mobile), Rep);

        Assert.Equal(SalesOrderStatus.Pending, order.Status);
        Assert.False(order.IsSynced);
        Assert.Equal(2, order.Lines.Count);

        // Nothing was asked of the credit service at capture: an unpriced order has nothing to
        // measure, so the question belongs to the post-time gate that runs on the real DocTotal.
        Assert.Empty(_creditChecks);

        Assert.Single(await _context.SalesOrders.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The exemption is for mobile alone. A web order carries its prices from the form, so the
    /// capture-time check can weigh the order itself and the rep is still holding it when refused.
    /// </summary>
    [Fact]
    public async Task A_web_order_for_an_over_limit_customer_is_still_refused_at_capture()
    {
        var service = CreateService(isWithinLimit: false);

        var exception = await Assert.ThrowsAsync<CreditLimitExceededException>(
            () => service.CreateAsync(NewRequest(SalesOrderSource.Web, unitPrice: 25m), Rep));

        Assert.Equal(OverLimitMessage, exception.Message);
        Assert.Equal(new[] { 100m }, _creditChecks);
        Assert.Empty(await _context.SalesOrders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Pricing_a_captured_mobile_order_notes_that_it_is_held_on_credit()
    {
        var service = CreateService(isWithinLimit: false);
        var order = await service.CreateAsync(NewRequest(SalesOrderSource.Mobile), Rep);

        await service.ProcessMobileOrderPostSaveAsync(order.Id);

        var stored = await ReloadAsync(order.Id);
        Assert.Equal(100m, stored.DocTotal);
        Assert.StartsWith("Held on credit —", stored.SyncError);
        Assert.Contains("cannot be posted to SAP", stored.SyncError);
        Assert.Contains("over its credit limit", stored.SyncError);

        // Measured against the order's real value, which is the whole reason the check moved here.
        Assert.Equal(new[] { 100m }, _creditChecks);

        // A held order is still a captured order — post-processing completes and the queue clears.
        var queueEntry = await _context.MobileOrderPostProcessingQueue
            .AsNoTracking()
            .SingleAsync(entry => entry.SalesOrderId == order.Id);
        Assert.Equal(MobileOrderPostProcessingQueueStatus.Completed, queueEntry.Status);
        Assert.Null(queueEntry.LastError);
    }

    [Fact]
    public async Task Pricing_a_mobile_order_within_the_limit_leaves_no_note()
    {
        var service = CreateService(isWithinLimit: true);
        var order = await service.CreateAsync(NewRequest(SalesOrderSource.Mobile), Rep);

        await service.ProcessMobileOrderPostSaveAsync(order.Id);

        var stored = await ReloadAsync(order.Id);
        Assert.Equal(100m, stored.DocTotal);
        Assert.Null(stored.SyncError);
    }

    /// <summary>
    /// The note is advisory. A credit profile SAP will not answer for must not fail the capture, or
    /// an outage would strand every mobile order in the retry queue.
    /// </summary>
    [Fact]
    public async Task A_credit_lookup_failure_leaves_the_order_captured_and_unannotated()
    {
        var service = CreateService(isWithinLimit: false, creditCheckThrows: true);
        var order = await service.CreateAsync(NewRequest(SalesOrderSource.Mobile), Rep);

        await service.ProcessMobileOrderPostSaveAsync(order.Id);

        var stored = await ReloadAsync(order.Id);
        Assert.Equal(100m, stored.DocTotal);
        Assert.Null(stored.SyncError);

        var queueEntry = await _context.MobileOrderPostProcessingQueue
            .AsNoTracking()
            .SingleAsync(entry => entry.SalesOrderId == order.Id);
        Assert.Equal(MobileOrderPostProcessingQueueStatus.Completed, queueEntry.Status);
    }

    private async Task<SalesOrderEntity> ReloadAsync(int id)
    {
        _context.ChangeTracker.Clear();
        return await _context.SalesOrders
            .AsNoTracking()
            .Include(order => order.Lines)
            .SingleAsync(order => order.Id == id);
    }

    /// <summary>
    /// A mobile order as the app sends one: quantities and units, no prices.
    /// </summary>
    private static CreateSalesOrderRequest NewRequest(SalesOrderSource source, decimal unitPrice = 0m) =>
        new()
        {
            CardCode = "FRU003",
            CardName = "Frugiparus Enterprises",
            Currency = "usd",
            Source = source,
            Lines =
            {
                new CreateSalesOrderLineRequest
                {
                    ItemCode = "ITEM-1",
                    ItemDescription = "Item one",
                    Quantity = 2,
                    UnitPrice = unitPrice,
                    UoMCode = "EA"
                },
                new CreateSalesOrderLineRequest
                {
                    ItemCode = "ITEM-2",
                    ItemDescription = "Item two",
                    Quantity = 2,
                    UnitPrice = unitPrice,
                    UoMCode = "EA"
                }
            }
        };

    /// <summary>
    /// The production <see cref="SalesOrderService"/> over an in-memory database. Only the credit
    /// profile and the price catalog are stubbed — the routing between capture, pricing and the
    /// credit note is the thing under test, so it has to be the real one.
    /// </summary>
    private SalesOrderService CreateService(bool isWithinLimit, bool creditCheckThrows = false)
    {
        var creditLimitService = StubProxy.For<ICreditLimitService>((method, args) =>
        {
            if (method.Name != nameof(ICreditLimitService.CheckSalesOrderAsync))
                throw new InvalidOperationException($"Unexpected call to {method.Name}");

            _creditChecks.Add((decimal)args![1]!);

            if (creditCheckThrows)
                throw new InvalidOperationException("SAP is unreachable.");

            return Task.FromResult(isWithinLimit
                ? CreditLimitCheckResult.Allowed()
                : new CreditLimitCheckResult
                {
                    IsWithinLimit = false,
                    CreditAccountCardCode = "FRU003",
                    CreditLimit = 30_000m,
                    Exposure = 65_331.06m,
                    Message = OverLimitMessage
                });
        });

        var priceCatalog = StubProxy.For<ILocalPriceCatalogService>((method, _) =>
            method.Name == nameof(ILocalPriceCatalogService.GetBusinessPartnerPricingAsync)
                ? Task.FromResult<LocalBusinessPartnerPricingResult?>(new LocalBusinessPartnerPricingResult
                {
                    BusinessPartner = new BusinessPartnerDto { CardCode = "FRU003" },
                    Prices = new ItemPricesByListResponseDto
                    {
                        Prices = new List<ItemPriceByListDto>
                        {
                            new() { ItemCode = "ITEM-1", Price = 30m },
                            new() { ItemCode = "ITEM-2", Price = 20m }
                        }
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

    /// <summary>
    /// <see cref="SalesOrderEntity.RowVersion"/> is <c>[Timestamp]</c>, which Npgsql maps to the
    /// store-generated <c>xmin</c> system column. SQLite has no equivalent, so EF leaves the column
    /// out of the INSERT and the NOT NULL constraint fails. The orders here are built by
    /// <see cref="SalesOrderService.CreateAsync"/> rather than by the fixture, so there is nowhere
    /// to supply one by hand — a store default fills it instead.
    /// </summary>
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
