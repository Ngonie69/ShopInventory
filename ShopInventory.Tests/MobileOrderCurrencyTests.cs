using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that a merchandiser's mobile order carries its account's currency, whatever the phone said.
/// </summary>
/// <remarks>
/// The merchandiser app never sets a currency, so every order it sends says USD. On 2026-11-01
/// SO-20261101-0008 for TMP158 (Pick n Pay Arundel ZiG) showed ZiG prices labelled USD, and SAP
/// refused its approval with "enter valid currency code [ORDR.DocCur], 'USD'".
/// </remarks>
public sealed class MobileOrderCurrencyTests : IDisposable
{
    private const string ZigAccount = "TMP158";
    private static readonly Guid Rep = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<string?> _currenciesSentToSap = new();

    public MobileOrderCurrencyTests()
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
            Id = Rep,
            Username = "merch",
            PasswordHash = "not-a-real-hash",
            Role = ApplicationRoles.Merchandiser
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_mobile_order_for_a_ZiG_account_is_stored_in_ZiG_once_captured()
    {
        SeedPartner(ZigAccount, "ZiG");
        var service = CreateService();
        var order = await service.CreateAsync(NewMobileRequest(), Rep);

        await service.ProcessMobileOrderPostSaveAsync(order.Id);

        var stored = await ReloadAsync(order.Id);
        Assert.Equal("ZIG", stored.Currency);
        // Only the label changes: the lines were always priced from the account's own price list.
        Assert.Equal(100m, stored.DocTotal);
    }

    [Fact]
    public async Task An_all_currencies_account_keeps_the_currency_the_order_was_sent_in()
    {
        SeedPartner(ZigAccount, "##");
        var service = CreateService();
        var order = await service.CreateAsync(NewMobileRequest(), Rep);

        await service.ProcessMobileOrderPostSaveAsync(order.Id);

        Assert.Equal("USD", (await ReloadAsync(order.Id)).Currency);
    }

    [Fact]
    public async Task An_account_with_no_synced_record_keeps_the_currency_the_order_was_sent_in()
    {
        var service = CreateService();
        var order = await service.CreateAsync(NewMobileRequest(), Rep);

        await service.ProcessMobileOrderPostSaveAsync(order.Id);

        Assert.Equal("USD", (await ReloadAsync(order.Id)).Currency);
    }

    /// <summary>
    /// A van order's account is the van's own, and the handset picks the currency on purpose.
    /// </summary>
    [Fact]
    public async Task A_van_order_keeps_the_currency_its_handset_chose()
    {
        SeedPartner(ZigAccount, "ZiG");
        var service = CreateService();
        var created = await service.CreateAsync(NewMobileRequest(), Rep);

        var queueEntry = await _context.MobileOrderPostProcessingQueue
            .AsTracking()
            .SingleAsync(entry => entry.SalesOrderId == created.Id);
        queueEntry.AutoPostToSap = true;
        await _context.SaveChangesAsync();

        var order = await LoadTrackedAsync(created.Id);
        Assert.False(await service.AlignMobileOrderCurrencyWithPartnerAsync(order, CancellationToken.None));
        Assert.Equal("USD", order.Currency);
    }

    [Fact]
    public async Task A_web_order_keeps_the_currency_its_operator_chose()
    {
        SeedPartner(ZigAccount, "ZiG");
        var order = new SalesOrderEntity
        {
            OrderNumber = "SO-WEB",
            CardCode = ZigAccount,
            Currency = "USD",
            Source = SalesOrderSource.Web
        };

        Assert.False(await CreateService().AlignMobileOrderCurrencyWithPartnerAsync(order, CancellationToken.None));
        Assert.Equal("USD", order.Currency);
    }

    /// <summary>
    /// Orders captured before the fix are already priced and still say USD. Approving one must send
    /// SAP the account's currency, or it stays stuck.
    /// </summary>
    [Fact]
    public async Task An_order_captured_in_USD_is_posted_to_SAP_in_its_account_currency()
    {
        var service = CreateService();
        var created = await service.CreateAsync(NewMobileRequest(), Rep);
        await service.ProcessMobileOrderPostSaveAsync(created.Id);
        Assert.Equal("USD", (await ReloadAsync(created.Id)).Currency);

        // The account's record arrives after capture, as it would have for an order stuck before
        // this fix.
        SeedPartner(ZigAccount, "ZiG");

        var order = await LoadTrackedAsync(created.Id);
        await service.PostApprovedOrderToSapCoreAsync(order, CancellationToken.None);

        Assert.Equal(new[] { "ZIG" }, _currenciesSentToSap);
        Assert.Equal("ZIG", (await ReloadAsync(created.Id)).Currency);
    }

    private void SeedPartner(string cardCode, string currency)
    {
        _context.BusinessPartnerPriceProfiles.Add(new BusinessPartnerPriceProfileEntity
        {
            CardCode = cardCode,
            CardName = "Pick n Pay Arundel ZiG",
            Currency = currency,
            PriceListNum = 1,
            SyncedFromSAP = true
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private async Task<SalesOrderEntity> ReloadAsync(int id)
    {
        _context.ChangeTracker.Clear();
        return await _context.SalesOrders
            .AsNoTracking()
            .Include(order => order.Lines)
            .SingleAsync(order => order.Id == id);
    }

    private async Task<SalesOrderEntity> LoadTrackedAsync(int id)
    {
        _context.ChangeTracker.Clear();
        return await _context.SalesOrders
            .AsTracking()
            .Include(order => order.Lines)
            .SingleAsync(order => order.Id == id);
    }

    /// <summary>A mobile order as the merchandiser app sends one: unpriced, and always USD.</summary>
    private static CreateSalesOrderRequest NewMobileRequest() =>
        new()
        {
            CardCode = ZigAccount,
            CardName = "Pick n Pay Arundel ZiG",
            Currency = "USD",
            Source = SalesOrderSource.Mobile,
            Lines =
            {
                new CreateSalesOrderLineRequest
                {
                    ItemCode = "ITEM-1",
                    ItemDescription = "Item one",
                    Quantity = 2,
                    UnitPrice = 0m,
                    UoMCode = "EA"
                },
                new CreateSalesOrderLineRequest
                {
                    ItemCode = "ITEM-2",
                    ItemDescription = "Item two",
                    Quantity = 2,
                    UnitPrice = 0m,
                    UoMCode = "EA"
                }
            }
        };

    /// <summary>
    /// The production <see cref="SalesOrderService"/> over an in-memory database. SAP answers only
    /// the duplicate probe and the create, which records the currency it was sent.
    /// </summary>
    private SalesOrderService CreateService()
    {
        var sapClient = StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(ISAPServiceLayerClient.GetSalesOrderByOrderNumberAsync):
                    return Task.FromResult<SAPSalesOrder?>(null);

                case nameof(ISAPServiceLayerClient.CreateSalesOrderAsync):
                    var order = (SalesOrderEntity)args![0]!;
                    _currenciesSentToSap.Add(order.Currency);
                    return Task.FromResult(new SAPSalesOrder
                    {
                        DocEntry = 501,
                        DocNum = 9001,
                        DocCurrency = order.Currency
                    });

                default:
                    throw new InvalidOperationException($"Unexpected call to {method.Name}");
            }
        });

        var idempotencyStore = StubProxy.For<IIdempotencyRequestStore>((method, _) => method.Name switch
        {
            nameof(IIdempotencyRequestStore.TryAcquireAsync) =>
                Task.FromResult(new IdempotencyAcquireResult<SAPSalesOrder>(IdempotencyAcquireOutcome.Acquired, RequestId: 1)),
            nameof(IIdempotencyRequestStore.CompleteAsync) => Task.CompletedTask,
            nameof(IIdempotencyRequestStore.ReleaseAsync) => Task.CompletedTask,
            _ => throw new InvalidOperationException($"Unexpected call to {method.Name}")
        });

        var creditLimitService = StubProxy.For<ICreditLimitService>((method, _) =>
            method.Name == nameof(ICreditLimitService.CheckSalesOrderAsync)
                ? Task.FromResult(CreditLimitCheckResult.Allowed())
                : throw new InvalidOperationException($"Unexpected call to {method.Name}"));

        var priceCatalog = StubProxy.For<ILocalPriceCatalogService>((method, _) =>
            method.Name == nameof(ILocalPriceCatalogService.GetBusinessPartnerPricingAsync)
                ? Task.FromResult<LocalBusinessPartnerPricingResult?>(new LocalBusinessPartnerPricingResult
                {
                    BusinessPartner = new BusinessPartnerDto { CardCode = ZigAccount },
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
            sapClient,
            NullLogger<SalesOrderService>.Instance,
            new NoOpNotificationService(),
            StubProxy.Unused<IBusinessPartnerService>(),
            priceCatalog,
            idempotencyStore,
            creditLimitService,
            Options.Create(new TaxSettings { VatRate = 0m }));
    }

    /// <summary>
    /// SQLite has no <c>xmin</c>, so the row version needs a store default (see
    /// <see cref="MobileOrderCreditHoldTests"/>).
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
