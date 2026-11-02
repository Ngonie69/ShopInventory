using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A posted ZiG order reads back in ZiG, and one saved before that was true heals itself.
/// </summary>
/// <remarks>
/// SO-20261102-0063 for TMP191 (Pick n Pay Madokero ZiG) posted as SAP 86228 at ZiG 229,172.44,
/// rate 30. The app showed "ZiG 7,639.08": SAP's DocTotal, VatSum and LineTotal are in the
/// company's local currency (USD), and the read-back copied them under the order's ZiG label.
/// The numbers below are that document's.
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class SalesOrderForeignCurrencyTotalsTests : IDisposable
{
    private const int DocEntry = 501;
    private const int DocNum = 86228;
    private static readonly Guid Rep = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<int> _sapReadsByDocEntry = new();

    public SalesOrderForeignCurrencyTotalsTests()
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
    public void A_ZiG_order_is_read_in_ZiG()
    {
        var sap = Sap86228();

        Assert.True(SapDocumentAmounts.IsForeignCurrency(sap));
        Assert.Equal(229172.44m, SapDocumentAmounts.DocTotal(sap));
        Assert.Equal(30754.74m, SapDocumentAmounts.VatSum(sap));
        Assert.Equal(4014.00m, SapDocumentAmounts.LineTotal(sap, sap.DocumentLines![0]));
    }

    [Fact]
    public void A_USD_order_is_read_as_it_always_was()
    {
        var sap = new SAPSalesOrder
        {
            DocCurrency = "USD",
            DocRate = 1m,
            DocTotal = 115m,
            DocTotalFc = 0m,
            VatSum = 15m,
            VatSumFc = 0m,
            DocumentLines = [new SAPSalesOrderLine { LineTotal = 100m, RowTotalFC = 0m }]
        };

        Assert.False(SapDocumentAmounts.IsForeignCurrency(sap));
        Assert.Equal(115m, SapDocumentAmounts.DocTotal(sap));
        Assert.Equal(15m, SapDocumentAmounts.VatSum(sap));
        Assert.Equal(100m, SapDocumentAmounts.LineTotal(sap, sap.DocumentLines[0]));
    }

    /// <summary>A read whose <c>$select</c> left out the tax and line fields still lands in ZiG.</summary>
    [Fact]
    public void A_ZiG_order_missing_its_foreign_tax_and_lines_is_converted_at_its_own_rate()
    {
        var sap = Sap86228();
        sap.VatSumFc = null;
        sap.DocumentLines![0].RowTotalFC = null;

        Assert.Equal(30754.81m, SapDocumentAmounts.VatSum(sap));
        Assert.Equal(4014.00m, SapDocumentAmounts.LineTotal(sap, sap.DocumentLines[0]));
    }

    [Fact]
    public async Task Opening_a_posted_ZiG_order_shows_its_ZiG_totals()
    {
        var dto = await CreateService(Sap86228()).GetByIdAsync(DocEntry);

        Assert.NotNull(dto);
        Assert.Equal(229172.44m, dto!.DocTotal);
        Assert.Equal(30754.74m, dto.TaxAmount);
        Assert.Equal(198417.70m, dto.SubTotal);
        Assert.Equal(30m, dto.ExchangeRate);
        Assert.Equal(4014.00m, Assert.Single(dto.Lines).LineTotal);
    }

    [Fact]
    public async Task Posting_a_ZiG_order_keeps_its_ZiG_totals()
    {
        SeedPartner("ZiG");
        var sapDocument = new SAPSalesOrder
        {
            DocEntry = DocEntry,
            DocNum = DocNum,
            DocCurrency = "ZiG",
            DocRate = 30m,
            DocTotal = 3.33m,
            DocTotalFc = 100m,
            VatSum = 0m,
            VatSumFc = 0m
        };
        var service = CreateService(sapDocument);
        var created = await service.CreateAsync(NewMobileRequest(), Rep);
        await service.ProcessMobileOrderPostSaveAsync(created.Id);

        var order = await LoadTrackedAsync(created.Id);
        await service.PostApprovedOrderToSapCoreAsync(order, CancellationToken.None);

        var stored = await ReloadAsync(created.Id);
        Assert.Equal(DocNum, stored.SAPDocNum);
        Assert.Equal(100m, stored.DocTotal);
        Assert.Equal(30m, stored.ExchangeRate);
    }

    [Fact]
    public async Task A_ZiG_order_saved_with_its_USD_totals_heals_and_then_leaves_the_sweep()
    {
        var broken = SeedPostedOrder("SO-20261102-0063", "ZiG", DocEntry, docTotal: 7639.08m, tax: 1025.16m);
        var service = CreateService(Sap86228());

        Assert.Equal(1, await service.HealForeignCurrencySalesOrderTotalsAsync(TimeSpan.FromDays(60), 25));

        var healed = await ReloadAsync(broken);
        Assert.Equal(229172.44m, healed.DocTotal);
        Assert.Equal(30754.74m, healed.TaxAmount);
        Assert.Equal(198417.70m, healed.SubTotal);
        Assert.Equal(30m, healed.ExchangeRate);

        // Healed for good: the next sweep does not ask SAP again.
        _sapReadsByDocEntry.Clear();
        Assert.Equal(0, await service.HealForeignCurrencySalesOrderTotalsAsync(TimeSpan.FromDays(60), 25));
        Assert.Empty(_sapReadsByDocEntry);
    }

    [Fact]
    public async Task The_sweep_leaves_USD_unposted_and_old_orders_alone()
    {
        SeedPostedOrder("SO-USD", "USD", 601, docTotal: 50m, tax: 0m);
        SeedPostedOrder("SO-UNPOSTED", "ZiG", null, docTotal: 50m, tax: 0m);
        SeedPostedOrder("SO-OLD", "ZiG", 602, docTotal: 50m, tax: 0m, createdAt: DateTime.UtcNow.AddDays(-90));

        Assert.Equal(0, await CreateService(Sap86228()).HealForeignCurrencySalesOrderTotalsAsync(TimeSpan.FromDays(60), 25));
        Assert.Empty(_sapReadsByDocEntry);
    }

    /// <summary>
    /// The order lists ask SAP for the order-currency total. Only <c>DocTotalFc</c>: it is the one
    /// foreign field already proven on a live <c>$select</c>, and SAP refuses a whole query with a 400
    /// over one name it does not accept on that entity set. Tax converts at the order's own ratio.
    /// </summary>
    [Fact]
    public async Task The_order_list_asks_SAP_for_the_order_currency_totals()
    {
        var sap = new RecordingSap();
        var client = CreateClient(sap);

        var orders = await client.GetSalesOrderHeadersAsync(pageSize: 1);

        var select = Assert.Single(sap.OrderQueries);
        var fields = select.Split('&').Single(part => part.StartsWith("$select=")).Split('=', ',');
        Assert.Contains("DocTotalFc", fields);

        var order = Assert.Single(orders);
        Assert.Equal(229172.44m, SapDocumentAmounts.DocTotal(order));
        Assert.Equal(30754.81m, SapDocumentAmounts.VatSum(order));
    }

    private static SAPSalesOrder Sap86228() =>
        new()
        {
            DocEntry = DocEntry,
            DocNum = DocNum,
            CardCode = "TMP191",
            CardName = "Pick n Pay Madokero ZiG",
            DocCurrency = "ZiG",
            DocRate = 30m,
            DocTotal = 7639.08m,
            DocTotalFc = 229172.44m,
            VatSum = 1025.16m,
            VatSumFc = 30754.74m,
            DiscountPercent = 0m,
            TotalDiscount = 0m,
            TotalDiscountFC = 0m,
            DocumentStatus = "bost_Open",
            Cancelled = "tNO",
            DocumentLines =
            [
                new SAPSalesOrderLine
                {
                    LineNum = 0,
                    ItemCode = "CHE030",
                    ItemDescription = "Traditional Cheddar Vac Pac 200g",
                    Quantity = 30m,
                    Price = 133.80m,
                    UnitPrice = 133.80m,
                    LineTotal = 133.80m,
                    RowTotalFC = 4014.00m,
                    WarehouseCode = "CMFS"
                }
            ]
        };

    private int SeedPostedOrder(string orderNumber, string currency, int? docEntry, decimal docTotal, decimal tax, DateTime? createdAt = null)
    {
        var order = new SalesOrderEntity
        {
            OrderNumber = orderNumber,
            CardCode = "TMP191",
            CardName = "Pick n Pay Madokero ZiG",
            Currency = currency,
            Status = docEntry.HasValue ? SalesOrderStatus.Approved : SalesOrderStatus.Pending,
            Source = SalesOrderSource.Mobile,
            OrderDate = DateTime.UtcNow,
            CreatedByUserId = Rep,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            IsSynced = docEntry.HasValue,
            SAPDocEntry = docEntry,
            SAPDocNum = docEntry.HasValue ? DocNum : null,
            DocTotal = docTotal,
            TaxAmount = tax,
            SubTotal = docTotal - tax
        };
        _context.SalesOrders.Add(order);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        return order.Id;
    }

    private void SeedPartner(string currency)
    {
        _context.BusinessPartnerPriceProfiles.Add(new BusinessPartnerPriceProfileEntity
        {
            CardCode = "TMP191",
            CardName = "Pick n Pay Madokero ZiG",
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
        return await _context.SalesOrders.AsNoTracking().SingleAsync(order => order.Id == id);
    }

    private async Task<SalesOrderEntity> LoadTrackedAsync(int id)
    {
        _context.ChangeTracker.Clear();
        return await _context.SalesOrders
            .AsTracking()
            .Include(order => order.Lines)
            .SingleAsync(order => order.Id == id);
    }

    private static CreateSalesOrderRequest NewMobileRequest() =>
        new()
        {
            CardCode = "TMP191",
            CardName = "Pick n Pay Madokero ZiG",
            Currency = "USD",
            Source = SalesOrderSource.Mobile,
            Lines =
            {
                new CreateSalesOrderLineRequest { ItemCode = "ITEM-1", ItemDescription = "Item one", Quantity = 2, UoMCode = "EA" },
                new CreateSalesOrderLineRequest { ItemCode = "ITEM-2", ItemDescription = "Item two", Quantity = 2, UoMCode = "EA" }
            }
        };

    /// <summary>
    /// The production <see cref="SalesOrderService"/> over an in-memory database. SAP holds one
    /// document, answers reads of it by DocEntry and accepts creates as that document.
    /// </summary>
    private SalesOrderService CreateService(SAPSalesOrder sapDocument)
    {
        var sapClient = StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(ISAPServiceLayerClient.GetSalesOrderByOrderNumberAsync):
                    return Task.FromResult<SAPSalesOrder?>(null);

                case nameof(ISAPServiceLayerClient.CreateSalesOrderAsync):
                    return Task.FromResult(new SAPSalesOrder
                    {
                        DocEntry = sapDocument.DocEntry,
                        DocNum = sapDocument.DocNum,
                        DocCurrency = sapDocument.DocCurrency
                    });

                case nameof(ISAPServiceLayerClient.GetSalesOrderByDocEntryAsync):
                    var docEntry = (int)args![0]!;
                    _sapReadsByDocEntry.Add(docEntry);
                    return Task.FromResult<SAPSalesOrder?>(docEntry == sapDocument.DocEntry ? sapDocument : null);

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
                    BusinessPartner = new BusinessPartnerDto { CardCode = "TMP191" },
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

    private static SAPServiceLayerClient CreateClient(RecordingSap sap)
    {
        var httpClient = new HttpClient(sap) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };
        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings { ServiceLayerUrl = "https://sap.invalid/b1s/v1/" }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            StubProxy.Unused<ISapItemUomMappingStore>());
    }

    /// <summary>Answers the login and the order list with SAP 86228, carrying the fields the list selects.</summary>
    private sealed class RecordingSap : HttpMessageHandler
    {
        public List<string> OrderQueries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/Login", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"SessionId\":\"test-session\"}"));

            if (request.Method == HttpMethod.Get && path.EndsWith("/Orders", StringComparison.Ordinal))
            {
                OrderQueries.Add(Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?')));
                return Task.FromResult(Json(
                    "{\"value\":[{\"DocEntry\":501,\"DocNum\":86228,\"CardCode\":\"TMP191\",\"DocCurrency\":\"ZiG\"," +
                    "\"DocTotal\":7639.08,\"DocTotalFc\":229172.44,\"VatSum\":1025.16,\"DiscountPercent\":0.0,\"TotalDiscount\":0.0}]}"));
            }

            throw new InvalidOperationException($"Unexpected SAP call: {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

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
