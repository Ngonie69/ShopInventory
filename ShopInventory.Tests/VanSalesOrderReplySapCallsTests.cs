using System.Net;
using System.Text;
using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
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
/// A rep raises a sales order standing at a customer and waits for the answer. The order is captured in
/// Postgres and everything SAP-facing has a durable queue, so the answer must not wait on the Service
/// Layer for anything.
/// </summary>
/// <remarks>
/// <para>The chain is real from the van handler down to <see cref="SAPServiceLayerClient"/>; the Service
/// Layer itself is the one stub, and it records every request it is sent. A stub higher up would count
/// calls to an interface, not round trips, and a login is a round trip nobody calls by name.</para>
///
/// <para>2026-10-13: a one-line order took 7.1 s as the handset saw it. Run against the code as it was
/// that day, the first test here recorded <c>GET Items</c> (after a <c>POST Login</c> when the session
/// was cold) and the second a <c>GET BusinessPartners('VAN006')</c> on top — each one a wait on SAP,
/// behind the six shared concurrency slots, paid by whoever was standing at the customer.</para>
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class VanSalesOrderReplySapCallsTests : IDisposable
{
    private static readonly Guid VanUser = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private const string VanOrder = "VAN006-SO-20261013-BBB222";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingServiceLayer _sap = new();
    private readonly SAPServiceLayerClient _sapClient;

    public VanSalesOrderReplySapCallsTests()
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
            AssignedCustomerCodes = """["VAN006"]"""
        });
        _context.SaveChanges();

        _sapClient = CreateSapClient(_sap);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The ordinary van order: the handset names the shop and sends no unit for its lines, and the API's
    /// own item table cannot answer one (nothing fills it).
    /// </summary>
    [Fact]
    public async Task A_van_order_is_stored_and_answered_without_a_Service_Layer_request()
    {
        var result = await Handler().Handle(Command(reference: "Tuck Shop"), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Empty(_sap.Requests);

        var order = await _context.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleAsync();
        Assert.Equal(VanOrder, order.ClientRequestId);
        Assert.Equal(SalesOrderStatus.Pending, order.Status);
        Assert.Equal("BUT001", Assert.Single(order.Lines).ItemCode);

        var queued = await _context.MobileOrderPostProcessingQueue.AsNoTracking().SingleAsync();
        Assert.Equal(order.Id, queued.SalesOrderId);
        Assert.True(queued.AutoPostToSap);
    }

    /// <summary>
    /// With no shop name the order is stored under the van's own card code, and the account's name used to
    /// be read from SAP before the order was stored.
    /// </summary>
    [Fact]
    public async Task A_van_order_with_no_shop_name_is_answered_without_a_Service_Layer_request()
    {
        var result = await Handler().Handle(Command(reference: null), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Empty(_sap.Requests);
        Assert.Equal(1, await _context.SalesOrders.CountAsync());
    }

    /// <summary>
    /// The resend of an order the handset never heard back about is answered from the stored order, as it
    /// always was, and that answer does not ask SAP either.
    /// </summary>
    [Fact]
    public async Task A_resend_under_the_same_van_order_is_answered_with_the_stored_order()
    {
        var first = await Handler().Handle(Command(reference: "Tuck Shop"), CancellationToken.None);
        var resent = await Handler().Handle(Command(reference: "Tuck Shop"), CancellationToken.None);

        Assert.False(resent.IsError, resent.IsError ? resent.FirstError.Description : null);
        Assert.Equal(first.Value.Id, resent.Value.Id);
        Assert.Equal(first.Value.PurchaseOrders, resent.Value.PurchaseOrders);
        Assert.Equal(1, await _context.SalesOrders.CountAsync());
        Assert.Equal(1, await _context.MobileOrderPostProcessingQueue.CountAsync());
        Assert.Empty(_sap.Requests);
    }

    /// <summary>
    /// What the reply did not wait for is not lost: the post-save queue reads it, on nobody's time, before
    /// it prices the order and tells the office about it.
    /// </summary>
    [Fact]
    public async Task The_queue_fills_in_the_unit_and_the_account_name_the_reply_did_not_wait_for()
    {
        var result = await Handler().Handle(Command(reference: null), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Empty(_sap.Requests);

        var captured = await _context.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleAsync();
        Assert.Null(Assert.Single(captured.Lines).UoMCode);
        Assert.Equal("VAN006", captured.CardName);

        // The post that follows needs a Postgres advisory lock SQLite cannot give, so the entry ends
        // Failed here as it does in VanSalesOrderAutoPostTests. The fill is saved before that.
        await Service(NoPrices()).ProcessMobileOrderPostSaveAsync(captured.Id);

        _context.ChangeTracker.Clear();
        var completed = await _context.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleAsync();
        Assert.Equal("EA", Assert.Single(completed.Lines).UoMCode);
        Assert.Equal("Van 6 Cash Sales", completed.CardName);
        Assert.Equal(
            ["GET Items", "GET BusinessPartners('VAN006')"],
            _sap.Requests.Where(request => request != "POST Login").Take(2));
    }

    /// <summary>
    /// The order cart sends no unit today. A handset that does is taken at its word, and then there is
    /// nothing for the queue to read either.
    /// </summary>
    [Fact]
    public async Task The_unit_a_handset_names_is_stored_with_the_line()
    {
        var command = Command(reference: "Tuck Shop");
        command.Request.Items[0].UoMCode = " KG ";

        var result = await Handler().Handle(command, CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var order = await _context.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleAsync();
        Assert.Equal("KG", Assert.Single(order.Lines).UoMCode);
        Assert.Empty(_sap.Requests);
    }

    /// <summary>
    /// The exception to capturing first. Whether 2.5 is a quantity anyone can order depends on the unit,
    /// and refusing it is the capture's job, so that one read still happens before anything is stored.
    /// A van handset sends whole numbers; this is a merchandiser's order.
    /// </summary>
    [Fact]
    public async Task A_fractional_quantity_with_no_known_unit_is_still_judged_against_SAP_before_capture()
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service().CreateAsync(Request(SalesOrderSource.Mobile, "BUT001", quantity: 2.5m), VanUser));

        Assert.Contains("Fractional quantities are only allowed for KG items", refused.Message);
        Assert.Contains("GET Items", _sap.Requests);
        Assert.Empty(await _context.SalesOrders.ToListAsync());
    }

    /// <summary>
    /// Nothing changes for an order raised on the web, which is posted to SAP in the same call and has to
    /// know its units to be posted at all.
    /// </summary>
    [Fact]
    public async Task A_web_order_still_reads_its_units_from_SAP_before_it_is_stored()
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service().CreateAsync(Request(SalesOrderSource.Web, "ZZZ999", quantity: 1m), VanUser));

        Assert.Contains("has no sales unit configured locally or in the SAP item master", refused.Message);
        Assert.Contains("GET Items", _sap.Requests);
        Assert.Empty(await _context.SalesOrders.ToListAsync());
    }

    private static CreateVanSalesSalesOrderCommand Command(string? reference) => new(
        new VanSalesOrderRequest
        {
            VanOrder = VanOrder,
            CustomerCode = "VAN006",
            Reference = reference ?? string.Empty,
            Type = "SO",
            Currency = "USD",
            DueDate = "2026-10-13",
            Items = [new VanSalesOrderItemRequest { Code = "BUT001", Quantity = 1, Price = 5 }]
        },
        VanUser,
        DeviceInfo: "SM-G998N");

    private static CreateSalesOrderRequest Request(SalesOrderSource source, string itemCode, decimal quantity) => new()
    {
        CardCode = "SIM001",
        CardName = "Simbisa",
        Currency = "USD",
        Source = source,
        Lines = { new CreateSalesOrderLineRequest { ItemCode = itemCode, Quantity = quantity, UnitPrice = 5m } }
    };

    private CreateVanSalesSalesOrderHandler Handler() =>
        new(
            _context,
            new SalesOrderMediator(new CreateSalesOrderHandler(
                Service(),
                StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
                NullLogger<CreateSalesOrderHandler>.Instance)),
            NullLogger<CreateVanSalesSalesOrderHandler>.Instance);

    /// <summary>
    /// The real service over the real SAP client. Capturing an order touches neither prices nor credit,
    /// so by default those throw if anything reaches for them.
    /// </summary>
    private SalesOrderService Service(ILocalPriceCatalogService? priceCatalog = null) =>
        new(
            _context,
            _sapClient,
            NullLogger<SalesOrderService>.Instance,
            new NoOpNotificationService(),
            new BusinessPartnerService(_sapClient, NullLogger<BusinessPartnerService>.Instance),
            priceCatalog ?? StubProxy.Unused<ILocalPriceCatalogService>(),
            StubProxy.Unused<IIdempotencyRequestStore>(),
            StubProxy.Unused<ICreditLimitService>(),
            Options.Create(new TaxSettings { VatRate = 0m }));

    private static ILocalPriceCatalogService NoPrices() =>
        StubProxy.For<ILocalPriceCatalogService>((method, _) =>
            method.Name == nameof(ILocalPriceCatalogService.GetBusinessPartnerPricingAsync)
                ? Task.FromResult<LocalBusinessPartnerPricingResult?>(null)
                : throw new InvalidOperationException($"Unexpected call to {method.Name}"));

    private static SAPServiceLayerClient CreateSapClient(HttpMessageHandler serviceLayer)
    {
        var httpClient = new HttpClient(serviceLayer)
        {
            BaseAddress = new Uri("https://sap.invalid/b1s/v1/")
        };
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

    /// <summary>A Service Layer that answers what an order asks of it and keeps a list of what it was asked.</summary>
    private sealed class RecordingServiceLayer : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            var resource = target[(target.LastIndexOf("/b1s/v1/", StringComparison.Ordinal) + "/b1s/v1/".Length)..];
            var query = resource.IndexOf('?');
            Requests.Add($"{request.Method} {(query < 0 ? resource : resource[..query])}");

            if (resource.StartsWith("Login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"SessionId":"test-session"}"""));
            }

            // SAP knows one item. Anything else comes back as an empty page, as it does for a code that
            // is not in the item master.
            if (resource.StartsWith("Items?", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(resource.Contains("'BUT001'", StringComparison.Ordinal)
                    ? """{"value":[{"ItemCode":"BUT001","ItemName":"Butter 500g","SalesUnit":"EA","InventoryUOM":"EA"}]}"""
                    : """{"value":[]}"""));
            }

            if (resource.StartsWith("BusinessPartners(", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"CardCode":"VAN006","CardName":"Van 6 Cash Sales","CardType":"cCustomer"}"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":{"message":{"value":"not stubbed"}}}""", Encoding.UTF8, "application/json")
            });
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
